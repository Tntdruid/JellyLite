using System.Collections.Concurrent;
using JellyLite.Library;

namespace JellyLite.Media;

/// <summary>Downloads remote images (actor thumbs from .nfo files) once and serves them from {data}/cache/remote.</summary>
public sealed class RemoteImages(AppConfig cfg, IHttpClientFactory http, ILogger<RemoteImages> log)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<string, DateTime> _failed = new();

    public async Task<string?> GetAsync(ImageRef img, CancellationToken ct)
    {
        if (!img.IsRemote) return File.Exists(img.Path) ? img.Path : null;
        var dir = Path.Combine(cfg.DataDir, "cache", "remote");
        var ext = Path.GetExtension(new Uri(img.Path).AbsolutePath);
        if (ext.Length is 0 or > 5) ext = ".jpg";
        var file = Path.Combine(dir, Ids.Hash(img.Path) + ext);
        if (File.Exists(file)) return file;
        if (_failed.TryGetValue(img.Path, out var at) && DateTime.UtcNow - at < TimeSpan.FromHours(1)) return null;

        var gate = _locks.GetOrAdd(file, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (File.Exists(file)) return file;
            Directory.CreateDirectory(dir);
            using var client = http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            var bytes = await client.GetByteArrayAsync(img.Path, ct);
            await File.WriteAllBytesAsync(file + ".tmp", bytes, ct);
            File.Move(file + ".tmp", file, true);
            return file;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            log.LogDebug("Could not download {Url}: {Error}", img.Path, e.Message);
            _failed[img.Path] = DateTime.UtcNow;
            return null;
        }
        finally
        {
            gate.Release();
        }
    }
}
