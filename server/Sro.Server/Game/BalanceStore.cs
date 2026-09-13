using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// Параметры корпусов из shared/hulls.json с горячей перезагрузкой: файл правят во время плейтеста,
/// сервер подхватывает его без перезапуска и рассылает клиентам. Невалидный файл отклоняется целиком.
/// </summary>
public sealed class BalanceStore : IDisposable
{
    private const string FileName = "hulls.json";
    private const int DebounceMs = 200;
    private const int ReadAttempts = 5;

    private readonly ILogger<BalanceStore> _log;
    private readonly string _path;
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounce;
    private string _lastJson;

    public BalanceStore(IConfiguration configuration, IHostEnvironment environment, ILogger<BalanceStore> log)
    {
        _log = log;
        var dir = Path.GetFullPath(Path.Combine(environment.ContentRootPath, configuration["SharedDir"]!));
        _path = Path.Combine(dir, FileName);

        _lastJson = File.ReadAllText(_path);
        if (!HullCatalog.TryParse(_lastJson, out var hulls, out var error))
            throw new InvalidOperationException($"{_path}: {error}");
        Hulls = hulls;
        log.LogInformation("Hulls loaded from {Path}: {Ids}", _path, string.Join(", ", hulls.Keys));

        // Редакторы сохраняют файл в несколько приёмов — реагируем на последнее событие.
        _debounce = new Timer(_ => Reload());
        _watcher = new FileSystemWatcher(dir, FileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
        _watcher.EnableRaisingEvents = true;
    }

    public IReadOnlyDictionary<string, HullParams> Hulls { get; private set; }

    /// <summary>Вызывается из потока таймера; подписчик сам переносит изменение в свой поток.</summary>
    public event Action<IReadOnlyDictionary<string, HullParams>>? Changed;

    private void OnFileEvent(object sender, FileSystemEventArgs e) => _debounce.Change(DebounceMs, Timeout.Infinite);

    private void Reload()
    {
        var json = TryRead();
        if (json is null || json == _lastJson) return;

        if (!HullCatalog.TryParse(json, out var hulls, out var error))
        {
            _log.LogWarning("{File} rejected, keeping previous values: {Error}", FileName, error);
            return;
        }
        _lastJson = json;
        Hulls = hulls;
        _log.LogInformation("{File} reloaded", FileName);
        Changed?.Invoke(hulls);
    }

    private string? TryRead()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException e) when (attempt < ReadAttempts)
            {
                _log.LogDebug(e, "{File} is busy, retrying", FileName);
                Thread.Sleep(100);
            }
            catch (IOException e)
            {
                _log.LogWarning(e, "Cannot read {File}", FileName);
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
