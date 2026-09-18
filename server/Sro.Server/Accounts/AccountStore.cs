using System.Text.Encodings.Web;
using System.Text.Json;
using Sro.Server.Game;

namespace Sro.Server.Accounts;

public enum LoginError
{
    None,
    /// <summary>Ник пустой или короче <see cref="AccountStore.MinNameLength"/>.</summary>
    BadName,
    /// <summary>Пароль короче <see cref="AccountStore.MinPasswordLength"/> или длиннее <see cref="AccountStore.MaxPasswordLength"/>.</summary>
    BadPassword,
    /// <summary>Ник занят, пароль к нему не подошёл.</summary>
    WrongPassword,
    /// <summary>Ключ устройства не найден: его вытеснили новые или файл аккаунта пропал.</summary>
    BadKey,
}

/// <param name="Id">Аккаунт; пусто при ошибке.</param>
/// <param name="Name">Ник так, как он записан в аккаунте.</param>
/// <param name="Key">Новый ключ устройства — только при входе по паролю.</param>
/// <param name="Created">Аккаунт только что заведён.</param>
public sealed record LoginResult(LoginError Error, string Id = "", string Name = "", string? Key = null, bool Created = false)
{
    public bool Ok => Error == LoginError.None;
}

/// <summary>Файл аккаунта целиком. Меняется только заменой записи под замком.</summary>
/// <param name="Password">Хеш пароля, см. <see cref="Passwords.Hash"/>.</param>
/// <param name="Keys">Хеши ключей устройств, старые впереди.</param>
/// <param name="Profile">Игровое состояние; null — пилот ещё ни разу не заходил в систему.</param>
public sealed record AccountData(
    string Id,
    string Name,
    string Password,
    IReadOnlyList<string> Keys,
    DateTimeOffset Created,
    AccountProfile? Profile = null);

/// <summary>
/// Аккаунты пилотов: по JSON-файлу на аккаунт в папке данных (GDD §61–62). Всё держится в памяти, на диск
/// уходят только изменённые аккаунты — раз в <see cref="FlushInterval"/> и при остановке сервера.
/// Потокобезопасно: вход идёт из сетевых потоков (хеш пароля — десятки миллисекунд, тику их ждать нельзя),
/// сохранение — из потока тика; файлы пишет таймер.
/// </summary>
public sealed class AccountStore : IDisposable
{
    public const int MinNameLength = 3;
    public const int MinPasswordLength = 4;
    public const int MaxPasswordLength = 64;

    /// <summary>Столько устройств помнят вход; вход с ещё одного забывает самое старое.</summary>
    public const int MaxKeys = 5;

    private const int MaxKeyLength = 128;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    /// <summary>С отступами и кириллицей как есть: файл аккаунта должен читаться и правиться руками.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _dir;
    private readonly ILogger _log;
    private readonly int _iterations;
    private readonly Lock _lock = new();
    private readonly Lock _writeLock = new();
    private readonly Dictionary<string, AccountData> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _idByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _idByKey = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);
    private readonly Timer? _timer;

    /// <param name="dir">Папка с файлами аккаунтов; создаётся, если её нет.</param>
    /// <param name="iterations">Итерации PBKDF2 для новых паролей; тесты ставят поменьше.</param>
    /// <param name="autoFlush">false — на диск только по <see cref="Flush"/>: так тесты проверяют, что и когда пишется.</param>
    public AccountStore(string dir, ILogger log, int iterations = Passwords.DefaultIterations, bool autoFlush = true)
    {
        _dir = dir;
        _log = log;
        _iterations = iterations;
        Directory.CreateDirectory(dir);
        Load();
        if (autoFlush) _timer = new Timer(_ => Flush(), null, FlushInterval, FlushInterval);
    }

    public int Count
    {
        get { lock (_lock) return _byId.Count; }
    }

    /// <summary>
    /// Вход по нику и паролю; свободный ник заводит новый аккаунт — отдельной регистрации нет.
    /// Успешный вход выдаёт устройству ключ: с ним переподключение обходится без пароля.
    /// </summary>
    public LoginResult Login(string? name, string? password)
    {
        if (string.IsNullOrWhiteSpace(name)) return new(LoginError.BadName);
        var clean = Room.SanitizeName(name);
        if (clean.Length < MinNameLength) return new(LoginError.BadName);
        if (password is null || password.Length < MinPasswordLength || password.Length > MaxPasswordLength)
            return new(LoginError.BadPassword);

        AccountData? existing;
        lock (_lock) existing = _idByName.TryGetValue(clean, out var id) ? _byId[id] : null;

        if (existing is not null)
        {
            // Хеш считается вне замка: иначе один вход задержал бы все остальные.
            if (!Passwords.Verify(password, existing.Password)) return new(LoginError.WrongPassword);
            var key = Passwords.NewKey();
            lock (_lock)
            {
                var current = _byId[existing.Id];
                Replace(current with { Keys = WithKey(current.Keys, Passwords.KeyHash(key)) });
            }
            return new(LoginError.None, existing.Id, existing.Name, key);
        }

        var hash = Passwords.Hash(password, _iterations);
        var newKey = Passwords.NewKey();
        AccountData? created = null;
        lock (_lock)
        {
            // Пока считался хеш, этот ник мог занять кто-то ещё — тогда это уже вход в его аккаунт.
            if (!_idByName.ContainsKey(clean))
            {
                created = new AccountData(Guid.NewGuid().ToString("N"), clean, hash, [Passwords.KeyHash(newKey)], DateTimeOffset.UtcNow);
                Replace(created);
            }
        }
        if (created is null) return Login(name, password);
        _log.LogInformation("Account '{Name}' created", clean);
        return new(LoginError.None, created.Id, created.Name, newKey, Created: true);
    }

    /// <summary>Вход по ключу устройства — после обрыва связи или перезапуска страницы.</summary>
    public LoginResult Resume(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength) return new(LoginError.BadKey);
        var hash = Passwords.KeyHash(key);
        lock (_lock)
        {
            if (!_idByKey.TryGetValue(hash, out var id)) return new(LoginError.BadKey);
            var account = _byId[id];
            return new(LoginError.None, account.Id, account.Name);
        }
    }

    /// <returns>null — аккаунта нет или пилот ещё не заходил в систему.</returns>
    public AccountProfile? Profile(string id)
    {
        lock (_lock) return _byId.TryGetValue(id, out var account) ? account.Profile : null;
    }

    /// <summary>Запомнить игровое состояние; на диск оно уйдёт при следующем сбросе.</summary>
    public void Save(string id, AccountProfile profile)
    {
        lock (_lock)
        {
            if (_byId.TryGetValue(id, out var account)) Replace(account with { Profile = profile });
        }
    }

    /// <summary>Записать изменённые аккаунты. Каждый файл пишется во временный и подменяется целиком.</summary>
    public void Flush()
    {
        lock (_writeLock)
        {
            List<AccountData> changed;
            lock (_lock)
            {
                if (_dirty.Count == 0) return;
                changed = [.. _dirty.Select(id => _byId[id])];
                _dirty.Clear();
            }
            foreach (var account in changed)
            {
                var path = PathOf(account.Id);
                var temp = path + ".tmp";
                try
                {
                    File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(account, Json));
                    File.Move(temp, path, overwrite: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    _log.LogError(e, "Account '{Name}' was not saved, will retry", account.Name);
                    lock (_lock) _dirty.Add(account.Id);
                }
            }
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        Flush();
    }

    private string PathOf(string id) => Path.Combine(_dir, id + ".json");

    /// <summary>Под замком: новая запись аккаунта, индексы и отметка «записать».</summary>
    private void Replace(AccountData account)
    {
        if (_byId.TryGetValue(account.Id, out var old))
        {
            foreach (var key in old.Keys) _idByKey.Remove(key);
        }
        _byId[account.Id] = account;
        _idByName[account.Name] = account.Id;
        foreach (var key in account.Keys) _idByKey[key] = account.Id;
        _dirty.Add(account.Id);
    }

    private static IReadOnlyList<string> WithKey(IReadOnlyList<string> keys, string key)
    {
        var list = keys.ToList();
        list.Add(key);
        if (list.Count > MaxKeys) list.RemoveRange(0, list.Count - MaxKeys);
        return list;
    }

    /// <summary>Испорченный файл пропускается и остаётся на диске как есть — его можно починить руками.</summary>
    private void Load()
    {
        foreach (var path in Directory.EnumerateFiles(_dir, "*.json"))
        {
            AccountData? account;
            try
            {
                account = JsonSerializer.Deserialize<AccountData>(File.ReadAllBytes(path), Json);
            }
            catch (Exception e) when (e is JsonException or IOException or NotSupportedException)
            {
                _log.LogError("Account file {Path} is unreadable, skipped: {Error}", path, e.Message);
                continue;
            }
            if (account is null || string.IsNullOrEmpty(account.Id) || string.IsNullOrWhiteSpace(account.Name) ||
                string.IsNullOrEmpty(account.Password) || Path.GetFileNameWithoutExtension(path) != account.Id)
            {
                _log.LogError("Account file {Path} is incomplete, skipped", path);
                continue;
            }
            if (_idByName.ContainsKey(account.Name))
            {
                _log.LogError("Account file {Path} repeats the name '{Name}', skipped", path, account.Name);
                continue;
            }
            Replace(account with { Keys = account.Keys ?? [] });
        }
        _dirty.Clear(); // только что прочитано с диска
        _log.LogInformation("Loaded {Count} accounts from {Dir}", _byId.Count, _dir);
    }
}
