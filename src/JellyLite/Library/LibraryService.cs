using System.Collections.Concurrent;
using JellyLite.Storage;
using JellyLite.Media;

namespace JellyLite.Library;

/// <summary>Holds the current library snapshot. Scans at startup, every N minutes and on request;
/// a separate worker reads new files with ffprobe, so a long first probe never delays a rescan.</summary>
public sealed class LibraryService(AppConfig cfg, Db db, Prober prober, ILogger<LibraryService> log) : BackgroundService
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _scanWake = new(0, 1);
    private readonly SemaphoreSlim _probeWake = new(0, 1);
    private readonly Scanner _scanner = new(cfg, db, log);
    // Files ffprobe couldn't read (path|mtime): not retried every scan
    private readonly ConcurrentDictionary<string, byte> _probeFailed = new();
    private volatile LibrarySnapshot _current = new();
    private int _probePending;

    public LibrarySnapshot Current => _current;
    public bool Scanning { get; private set; }
    public DateTime? LastScan { get; private set; }
    public int ProbePending => _probePending;

    public async Task<LibrarySnapshot> GetAsync()
    {
        await _ready.Task;
        return _current;
    }

    /// <summary>Asks for a rescan soon. Several requests while one is waiting become one scan.</summary>
    public void RequestScan()
    {
        try { _scanWake.Release(); } catch (SemaphoreFullException) { /* already requested */ }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var probing = Task.Run(() => ProbeLoopAsync(ct), ct);
        while (!ct.IsCancellationRequested)
        {
            await ScanNowAsync();
            try { _probeWake.Release(); } catch (SemaphoreFullException) { /* already signalled */ }
            try
            {
                await _scanWake.WaitAsync(TimeSpan.FromMinutes(Math.Max(1, cfg.ScanIntervalMinutes)), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        try { await probing; } catch (OperationCanceledException) { /* stopping */ }
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

    private async Task ProbeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _probeWake.WaitAsync(ct);
            var todo = _current.Items.Values
                .Where(i => i.IsPlayable && i.Media == null && i.Path != null && !_probeFailed.ContainsKey(Key(i)))
                .ToList();
            _probePending = todo.Count;
            try
            {
                await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, cfg.ProbeParallelism), CancellationToken = ct }, async (item, token) =>
                {
                    item.Media ??= await prober.ProbeAsync(item.Path!, item.Size, item.Mtime, token);
                    if (item.Media == null && prober.Available) _probeFailed.TryAdd(Key(item), 0);
                    Interlocked.Decrement(ref _probePending);
                });
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Probing failed");
            }
            finally
            {
                _probePending = 0;
            }
        }
    }

    private static string Key(Item i) => i.Path + "|" + i.Mtime.Ticks;

    /// <summary>Probes right away if the background prober hasn't reached the item yet (e.g. just before playback).</summary>
    public async Task EnsureProbedAsync(Item item, CancellationToken ct)
    {
        if (item.Media == null && item.Path != null) item.Media = await prober.ProbeAsync(item.Path, item.Size, item.Mtime, ct);
    }
}
