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
    /// <summary>Аккаунта нет: файл удалили, пока пилот играл, — или вход искал старый, а такого ника нет.</summary>
    NoAccount,
    /// <summary>Ник занят, а вход просил завести новый аккаунт: это регистрация под чужим ником.</summary>
    NameTaken,
}

/// <summary>
/// Чего ждёт вход — вкладка, с которой его начали (M15.8). <see cref="Existing"/> — только старый аккаунт,
/// <see cref="New"/> — только новый. <see cref="Any"/> — любой исход: свободный ник заводит аккаунт, занятый
/// пускает в него; так входят тесты и фикстуры, а не экран входа.
/// </summary>
public enum LoginMode { Any, Existing, New }

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
    /// Этим ником заведётся новый аккаунт (M15.5). По нему экран входа решает, показывать ли карточки пути:
    /// вошедшему в старый аккаунт выбирать нечего. Пароль не спрашиваем и хеш не считаем — это просто
    /// заглядывание в словарь. Ник могут занять между проверкой и входом: тогда вход станет обычным,
    /// и <see cref="Login"/> разберётся сам.
    /// </summary>
    public bool Free(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var clean = Room.SanitizeName(name);
        if (clean.Length < MinNameLength) return false;
        lock (_lock) return !_idByName.ContainsKey(clean);
    }

    /// <summary>
    /// Вход по нику и паролю. Успешный вход выдаёт устройству ключ: с ним переподключение обходится без пароля.
    /// </summary>
    /// <param name="mode">
    /// Что обещала игроку вкладка на экране входа (M15.8). Без этого опечатка в логине заводила бы пустой
    /// аккаунт вместо отказа, и игрок решил бы, что потерял корабль.
    /// </param>
    public LoginResult Login(string? name, string? password, LoginMode mode = LoginMode.Any)
    {
        if (string.IsNullOrWhiteSpace(name)) return new(LoginError.BadName);
        var clean = Room.SanitizeName(name);
        if (clean.Length < MinNameLength) return new(LoginError.BadName);
        if (password is null || password.Length < MinPasswordLength || password.Length > MaxPasswordLength)
            return new(LoginError.BadPassword);

        AccountData? existing;
        lock (_lock) existing = _idByName.TryGetValue(clean, out var id) ? _byId[id] : null;

        // Отказы вкладок — до счёта хеша: он стоит десятки миллисекунд, а сравнивать здесь нечего.
        if (existing is not null && mode == LoginMode.New) return new(LoginError.NameTaken);
        if (existing is null && mode == LoginMode.Existing) return new(LoginError.NoAccount);

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
        if (created is null) return Login(name, password, mode);
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

    /// <summary>
    /// Сменить пароль (M15.7). Все прежние ключи устройств при этом сбрасываются: пароль меняют в том
    /// числе потому, что он утёк, и оставить чужому устройству вход значило бы ничего не менять.
    /// Взамен выдаётся один новый ключ — для того устройства, с которого пароль меняли.
    /// </summary>
    /// <param name="id">Аккаунт, который сейчас в игре на этом соединении.</param>
    /// <param name="old">Прежний пароль: без него сменить нельзя даже со своего устройства.</param>
    /// <param name="fresh">Новый пароль.</param>
    /// <returns>
    /// <see cref="LoginError.None"/> и новый ключ устройства, которым дальше входить, — или причина отказа.
    /// </returns>
    public LoginResult ChangePassword(string? id, string? old, string? fresh)
    {
        if (string.IsNullOrEmpty(id)) return new(LoginError.NoAccount);
        if (fresh is null || fresh.Length < MinPasswordLength || fresh.Length > MaxPasswordLength)
            return new(LoginError.BadPassword);

        AccountData? account;
        lock (_lock) account = _byId.GetValueOrDefault(id);
        if (account is null) return new(LoginError.NoAccount);
        // Хеши считаются вне замка: каждый — десятки миллисекунд, тику и другим входам их ждать нельзя.
        if (old is null || !Passwords.Verify(old, account.Password)) return new(LoginError.WrongPassword);

        var hash = Passwords.Hash(fresh, _iterations);
        var key = Passwords.NewKey();
        lock (_lock)
        {
            if (!_byId.TryGetValue(id, out var current)) return new(LoginError.NoAccount);
            Replace(current with { Password = hash, Keys = [Passwords.KeyHash(key)] });
            account = current;
        }
        _log.LogInformation("Account '{Name}' changed its password", account.Name);
        return new(LoginError.None, account.Id, account.Name, key);
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
