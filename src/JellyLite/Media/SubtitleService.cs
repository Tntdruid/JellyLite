using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using JellyLite.Library;

namespace JellyLite.Media;

/// <summary>Delivers subtitles as WebVTT. SRT/VTT files are converted in code; ASS and embedded text
/// subtitles need ffmpeg and are cached in {data}/cache/subs.</summary>
public sealed partial class SubtitleService(AppConfig cfg, ILogger<SubtitleService> log)
{
    private bool? _ffmpeg;
    private bool FfmpegAvailable => _ffmpeg ??= Prober.CanRun(cfg.Ffmpeg);

    public static readonly HashSet<string> TextCodecs = new(StringComparer.OrdinalIgnoreCase)
        { "subrip", "srt", "ass", "ssa", "webvtt", "vtt", "mov_text", "text", "ttml" };

    [GeneratedRegex(@"(\d{1,2}:\d{2}:\d{2}),(\d{3})")]
    private static partial Regex SrtTime();

    /// <summary>Index of the first external subtitle (after all embedded streams).</summary>
    public static int ExternalBase(Item it) => (it.Media?.Streams.Count > 0 ? it.Media.Streams.Max(s => s.Index) : -1) + 1;

    public async Task<string?> GetVttAsync(Item it, int index, CancellationToken ct)
    {
        var extBase = ExternalBase(it);
        if (index >= extBase && index - extBase < it.ExternalSubs.Count)
        {
            var sub = it.ExternalSubs[index - extBase];
            if (sub.Format is "srt") return SrtToVtt(ReadText(sub.Path));
            if (sub.Format is "vtt") return ReadText(sub.Path);
            return await ConvertAsync(sub.Path, null, ct);
        }
        var stream = it.Media?.Streams.FirstOrDefault(s => s.Index == index && s.Type == "Subtitle");
        if (stream == null || it.Path == null || !TextCodecs.Contains(stream.Codec ?? "")) return null;
        return await ConvertAsync(it.Path, index, ct);
    }

    private async Task<string?> ConvertAsync(string path, int? streamIndex, CancellationToken ct)
    {
        if (!FfmpegAvailable) return null;
        var fi = new FileInfo(path);
        var dir = Path.Combine(cfg.DataDir, "cache", "subs");
        Directory.CreateDirectory(dir);
        var cache = Path.Combine(dir, Ids.Hash($"{path}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{streamIndex}") + ".vtt");
        if (File.Exists(cache)) return await File.ReadAllTextAsync(cache, ct);

        var psi = new ProcessStartInfo(cfg.Ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        var args = new List<string> { "-v", "error", "-y", "-i", path };
        if (streamIndex is int i) args.AddRange(["-map", "0:" + i]);
        args.AddRange(["-f", "webvtt", cache + ".tmp"]);
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            var err = p.StandardError.ReadToEndAsync(timeout.Token);
            await p.WaitForExitAsync(timeout.Token);
            if (p.ExitCode != 0)
            {
                log.LogWarning("ffmpeg subtitle extraction failed for {Path}: {Error}", path, await err);
                return null;
            }
            File.Move(cache + ".tmp", cache, true);
            return await File.ReadAllTextAsync(cache, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogWarning(e, "ffmpeg subtitle extraction error for {Path}", path);
            return null;
        }
    }

    /// <summary>Reads UTF-8, falling back to Latin-1 (old Danish .srt files are often Windows-1252).</summary>
    internal static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    internal static string SrtToVtt(string srt)
    {
        var body = SrtTime().Replace(srt.Replace("\r\n", "\n").Replace('\r', '\n').Trim(), "$1.$2");
        return "WEBVTT\n\n" + body + "\n";
    }
}
