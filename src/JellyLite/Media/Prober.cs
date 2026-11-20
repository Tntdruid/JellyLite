using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using JellyLite.Data;

namespace JellyLite.Media;

public sealed class MediaInfo
{
    public string? FormatName { get; set; }
    public long? RunTimeTicks { get; set; }
    public long? Bitrate { get; set; }
    public List<StreamInfo> Streams { get; set; } = [];
    public List<ChapterInfo> Chapters { get; set; } = [];
}

public sealed class StreamInfo
{
    public int Index { get; set; }
    /// <summary>Video | Audio | Subtitle</summary>
    public string Type { get; set; } = "";
    public string? Codec { get; set; }
    public string? Profile { get; set; }
    public string? Language { get; set; }
    public string? Title { get; set; }
    public bool IsDefault { get; set; }
    public bool IsForced { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? Channels { get; set; }
    public string? ChannelLayout { get; set; }
    public int? SampleRate { get; set; }
    public long? BitRate { get; set; }
    public float? FrameRate { get; set; }
}

public sealed class ChapterInfo
{
    public long StartTicks { get; set; }
    public string? Name { get; set; }
}

/// <summary>Reads tracks, duration and chapters with ffprobe. Results are cached in SQLite by path + size + mtime.</summary>
public sealed class Prober(AppConfig cfg, Db db, ILogger<Prober> log)
{
    private bool? _available;

    public bool Available => _available ??= CanRun(cfg.Ffprobe);

    internal static bool CanRun(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return false;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, "-version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (p == null) return false;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<MediaInfo?> ProbeAsync(string path, long size, DateTime mtime, CancellationToken ct = default)
    {
        var mt = mtime.ToUniversalTime().Ticks;
        if (db.GetProbe(path, size, mt) is { } cached)
        {
            try { return JsonSerializer.Deserialize<MediaInfo>(cached); } catch (JsonException) { /* re-probe */ }
        }
        if (!Available) return null;

        try
        {
            var psi = new ProcessStartInfo(cfg.Ffprobe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-v", "quiet", "-print_format", "json", "-show_format", "-show_streams", "-show_chapters", path })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var output = await p.StandardOutput.ReadToEndAsync(timeout.Token);
            await p.WaitForExitAsync(timeout.Token);
            if (p.ExitCode != 0) { log.LogWarning("ffprobe failed for {Path}", path); return null; }
            var info = Parse(output);
            db.PutProbe(path, size, mt, JsonSerializer.Serialize(info));
            return info;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("ffprobe timed out for {Path}", path);
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "ffprobe error for {Path}", path);
            return null;
        }
    }

    internal static MediaInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var info = new MediaInfo();
        if (root.TryGetProperty("format", out var f))
        {
            info.FormatName = Str(f, "format_name");
            if (Dbl(f, "duration") is { } dur) info.RunTimeTicks = (long)(dur * TimeSpan.TicksPerSecond);
            info.Bitrate = Lng(f, "bit_rate");
        }
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = Str(s, "codec_type") switch { "video" => "Video", "audio" => "Audio", "subtitle" => "Subtitle", _ => null };
                if (type == null) continue;
                var disp = s.TryGetProperty("disposition", out var d) ? d : default;
                // Cover art inside mkv/mp4 shows up as a video stream: skip it
                if (type == "Video" && Flag(disp, "attached_pic")) continue;
                var tags = s.TryGetProperty("tags", out var t) ? t : default;
                info.Streams.Add(new StreamInfo
                {
                    Index = s.TryGetProperty("index", out var i) ? i.GetInt32() : info.Streams.Count,
                    Type = type,
                    Codec = Str(s, "codec_name"),
                    Profile = Str(s, "profile"),
                    Language = tags.ValueKind == JsonValueKind.Object ? Str(tags, "language") is { } l && l != "und" ? l : null : null,
                    Title = tags.ValueKind == JsonValueKind.Object ? Str(tags, "title") : null,
                    IsDefault = Flag(disp, "default"),
                    IsForced = Flag(disp, "forced"),
                    Width = (int?)Lng(s, "width"),
                    Height = (int?)Lng(s, "height"),
                    Channels = (int?)Lng(s, "channels"),
                    ChannelLayout = Str(s, "channel_layout"),
                    SampleRate = (int?)Lng(s, "sample_rate"),
                    BitRate = Lng(s, "bit_rate"),
                    FrameRate = Rate(Str(s, "avg_frame_rate")),
                });
            }
        }
        if (root.TryGetProperty("chapters", out var chapters))
        {
            foreach (var c in chapters.EnumerateArray())
            {
                var tags = c.TryGetProperty("tags", out var t) ? t : default;
                info.Chapters.Add(new ChapterInfo
                {
                    StartTicks = (long)((Dbl(c, "start_time") ?? 0) * TimeSpan.TicksPerSecond),
                    Name = tags.ValueKind == JsonValueKind.Object ? Str(tags, "title") : null,
                });
            }
        }
        return info;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null
            : null;

    private static double? Dbl(JsonElement e, string name) =>
        double.TryParse(Str(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static long? Lng(JsonElement e, string name) =>
        long.TryParse(Str(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static bool Flag(JsonElement disp, string name) =>
        disp.ValueKind == JsonValueKind.Object && disp.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt32() == 1;

    private static float? Rate(string? r)
    {
        if (r == null) return null;
        var parts = r.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var a) && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var b) && b > 0)
            return (float)Math.Round(a / b, 3);
        return null;
    }
}
