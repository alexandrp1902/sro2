using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// Баланс из shared/ (hulls.json, weapons.json, combat.json) с горячей перезагрузкой: файлы правят во время плейтеста,
/// сервер подхватывает их без перезапуска и рассылает клиентам. Файлы разбираются вместе (правила ссылаются
/// на корпуса); если хоть один невалиден, остаётся прежний баланс целиком.
/// </summary>
public sealed class BalanceStore : IDisposable
{
    private static readonly string[] Files =
        [Balance.HullsFile, Balance.WeaponsFile, Balance.RulesFile, Balance.NpcsFile, Balance.LootFile];
    private const int DebounceMs = 200;
    private const int ReadAttempts = 5;

    private readonly ILogger<BalanceStore> _log;
    private readonly string _dir;
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounce;
    private string[] _lastTexts;

    public BalanceStore(IConfiguration configuration, IHostEnvironment environment, ILogger<BalanceStore> log)
    {
        _log = log;
        _dir = Path.GetFullPath(Path.Combine(environment.ContentRootPath, configuration["SharedDir"]!));

        _lastTexts = Files.Select(file => File.ReadAllText(Path.Combine(_dir, file))).ToArray();
        if (!Parse(_lastTexts, out var balance, out var error))
            throw new InvalidOperationException($"{_dir}: {error}");
        Balance = balance!;
        log.LogInformation(
            "Balance loaded from {Dir}: hulls {Hulls}; weapons {Weapons}; drones {Drones}; pirates {Pirates}; loot {Items} items, {Tables} tables",
            _dir,
            string.Join(", ", balance!.Hulls.Keys),
            string.Join(", ", balance.Weapons.Keys),
            balance.Rules.DroneList.Count,
            balance.Npc.Count,
            balance.Loot.ItemMap.Count,
            balance.Loot.TableMap.Count);

        // Редакторы сохраняют файл в несколько приёмов — реагируем на последнее событие.
        _debounce = new Timer(_ => Reload());
        _watcher = new FileSystemWatcher(_dir)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        _watcher.Filters.Clear();
        foreach (var file in Files) _watcher.Filters.Add(file);
        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
        _watcher.EnableRaisingEvents = true;
    }

    public Balance Balance { get; private set; }

    /// <summary>Вызывается из потока таймера; подписчик сам переносит изменение в свой поток.</summary>
    public event Action<Balance>? Changed;

    private void OnFileEvent(object sender, FileSystemEventArgs e) => _debounce.Change(DebounceMs, Timeout.Infinite);

    private void Reload()
    {
        var texts = new string[Files.Length];
        for (var i = 0; i < Files.Length; i++)
        {
            var text = TryRead(Files[i]);
            if (text is null) return;
            texts[i] = text;
        }
        if (texts.SequenceEqual(_lastTexts)) return;

        if (!Parse(texts, out var balance, out var error))
        {
            _log.LogWarning("Balance rejected, keeping previous values: {Error}", error);
            return;
        }
        _lastTexts = texts;
        Balance = balance!;
        _log.LogInformation("Balance reloaded");
        Changed?.Invoke(balance!);
    }

    private static bool Parse(string[] texts, out Balance? balance, out string? error) =>
        Balance.TryParse(texts[0], texts[1], texts[2], texts[3], texts[4], out balance, out error);

    private string? TryRead(string file)
    {
        var path = Path.Combine(_dir, file);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException e) when (attempt < ReadAttempts)
            {
                _log.LogDebug(e, "{File} is busy, retrying", file);
                Thread.Sleep(100);
            }
            catch (IOException e)
            {
                _log.LogWarning(e, "Cannot read {File}", file);
                return null;
            }
        }
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _debounce.Dispose();
    }
}
