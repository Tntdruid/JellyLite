using JellyLite.Storage;
using JellyLite.Media;

namespace JellyLite.Library;

/// <summary>Holds the current library snapshot. Scans at startup and every N minutes, then probes new files with ffprobe.</summary>
public sealed class LibraryService(AppConfig cfg, Db db, Prober prober, ILogger<LibraryService> log) : BackgroundService
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _wake = new(0);
    private readonly Scanner _scanner = new(cfg, db, log);
    private volatile LibrarySnapshot _current = new();

    public LibrarySnapshot Current => _current;
    public bool Scanning { get; private set; }
    public DateTime? LastScan { get; private set; }
    private int _probePending;
    public int ProbePending => _probePending;

    public async Task<LibrarySnapshot> GetAsync()
    {
        await _ready.Task;
        return _current;
    }

    public void RequestScan() => _wake.Release();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await ScanNowAsync();
            await ProbeAsync(ct);
            try
            {
                await _wake.WaitAsync(TimeSpan.FromMinutes(Math.Max(1, cfg.ScanIntervalMinutes)), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task ScanNowAsync()
    {
        Scanning = true;
        try
        {
            var started = DateTime.UtcNow;
            _current = await Task.Run(() => _scanner.Scan(_current));
            log.LogInformation("Scan done in {Seconds:0.0}s", (DateTime.UtcNow - started).TotalSeconds);
        }
        catch (Exception e)
        {
            log.LogError(e, "Scan failed");
        }
        finally
        {
            Scanning = false;
            LastScan = DateTime.UtcNow;
            _ready.TrySetResult();
        }
    }

    private async Task ProbeAsync(CancellationToken ct)
    {
        var todo = _current.Items.Values.Where(i => i.IsPlayable && i.Media == null && i.Path != null).ToList();
        _probePending = todo.Count;
        if (todo.Count == 0) return;
        try
        {
            await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, cfg.ProbeParallelism), CancellationToken = ct }, async (item, token) =>
            {
                item.Media ??= await prober.ProbeAsync(item.Path!, item.Size, item.Mtime, token);
                Interlocked.Decrement(ref _probePending);
            });
        }
        finally
        {
            _probePending = 0;
        }
    }

    /// <summary>Probes right away if the background prober hasn't reached the item yet (e.g. just before playback).</summary>
    public async Task EnsureProbedAsync(Item item, CancellationToken ct)
    {
        if (item.Media == null && item.Path != null) item.Media = await prober.ProbeAsync(item.Path, item.Size, item.Mtime, ct);
    }
}
