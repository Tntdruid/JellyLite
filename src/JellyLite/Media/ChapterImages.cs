using System.Diagnostics;
using System.Globalization;
using JellyLite.Library;

namespace JellyLite.Media;

/// <summary>Scene pictures for chapters: one frame grabbed with ffmpeg the first time it's asked for,
/// then served from {data}/cache/chapters. Grabbing a single frame is a quick seek, not transcoding.</summary>
public sealed class ChapterImages(AppConfig cfg, ILogger<ChapterImages> log)
{
    private readonly SemaphoreSlim _gate = new(2, 2);
    private bool? _ffmpeg;
    private bool FfmpegAvailable => _ffmpeg ??= Prober.CanRun(cfg.Ffmpeg);

    /// <summary>Changes when the file changes, so cached pictures are refreshed.</summary>
    public static string Tag(Item it, int index) => Ids.Hash($"{it.Path}|{it.Mtime.Ticks}|chapter{index}");

    public async Task<string?> GetAsync(Item it, int index, CancellationToken ct)
    {
        var chapters = it.Media?.Chapters ?? [];
        if (it.Path == null || index < 0 || index >= chapters.Count || !FfmpegAvailable) return null;
        var dir = Path.Combine(cfg.DataDir, "cache", "chapters");
        var file = Path.Combine(dir, Tag(it, index) + ".jpg");
        if (File.Exists(file)) return file;

        // A little after the chapter starts: the very first frame is often black
        var start = chapters[index].StartTicks;
        var end = index + 1 < chapters.Count ? chapters[index + 1].StartTicks : it.RunTimeTicks ?? start;
        var at = start + Math.Min(TimeSpan.FromSeconds(3).Ticks, Math.Max(0, end - start) / 4);
        var seconds = (at / (double)TimeSpan.TicksPerSecond).ToString("0.###", CultureInfo.InvariantCulture);

        await _gate.WaitAsync(ct);
        try
        {
            if (File.Exists(file)) return file;
            Directory.CreateDirectory(dir);
            var tmp = file + ".tmp.jpg";
            var psi = new ProcessStartInfo(cfg.Ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var a in new[] { "-v", "error", "-y", "-ss", seconds, "-i", it.Path, "-frames:v", "1", "-vf", "scale=-2:360", "-q:v", "4", tmp })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var err = p.StandardError.ReadToEndAsync(timeout.Token);
            await p.WaitForExitAsync(timeout.Token);
            if (p.ExitCode != 0 || !File.Exists(tmp))
            {
                log.LogWarning("Chapter image failed for {Path} #{Index}: {Error}", it.Path, index, await err);
                return null;
            }
            File.Move(tmp, file, true);
            return file;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Chapter image timed out for {Path} #{Index}", it.Path, index);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
