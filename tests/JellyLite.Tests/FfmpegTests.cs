using System.Diagnostics;
using JellyLite.Storage;
using JellyLite.Library;
using JellyLite.Media;
using Microsoft.Extensions.Logging.Abstractions;

namespace JellyLite.Tests;

/// <summary>Runs against a real file made with ffmpeg. Does nothing when ffmpeg/ffprobe aren't installed.</summary>
public sealed class FfmpegTests
{
    [Fact]
    public async Task Probe_real_file_and_extract_embedded_subtitles()
    {
        if (!Prober.CanRun("ffmpeg") || !Prober.CanRun("ffprobe")) return;

        var dir = Path.Combine(Path.GetTempPath(), "jellylite-ff-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var srt = Path.Combine(dir, "in.srt");
            File.WriteAllText(srt, "1\n00:00:00,500 --> 00:00:01,500\nIndlejret tekst\n");
            var meta = Path.Combine(dir, "chapters.txt");
            File.WriteAllText(meta, ";FFMETADATA1\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=1000\ntitle=Intro\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=1000\nEND=2000\ntitle=Handling\n");
            var mkv = Path.Combine(dir, "test.mkv");
            Run("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "testsrc=duration=2:size=320x240:rate=25", "-f", "lavfi", "-i", "sine=duration=2",
                "-i", srt, "-i", meta, "-map", "0", "-map", "1", "-map", "2", "-map_metadata", "3", "-map_chapters", "3",
                "-c:v", "mpeg4", "-c:a", "aac", "-c:s", "srt", "-metadata:s:a:0", "language=dan", "-metadata:s:s:0", "language=dan", mkv);

            var cfg = new AppConfig { DataDir = Path.Combine(dir, "data") };
            var prober = new Prober(cfg, new Db(cfg), NullLogger<Prober>.Instance);
            var fi = new FileInfo(mkv);
            var info = await prober.ProbeAsync(mkv, fi.Length, fi.LastWriteTimeUtc);

            Assert.NotNull(info);
            Assert.InRange(info.RunTimeTicks!.Value, TimeSpan.FromSeconds(1.9).Ticks, TimeSpan.FromSeconds(2.2).Ticks);
            Assert.Equal(["Video", "Audio", "Subtitle"], info.Streams.Select(s => s.Type));
            Assert.Equal(240, info.Streams[0].Height);
            Assert.Equal("dan", info.Streams[1].Language);
            Assert.Equal(["Intro", "Handling"], info.Chapters.Select(c => c.Name));

            var item = new Item { Id = Guid.NewGuid(), Type = ItemTypes.Movie, Name = "t", Path = mkv, Media = info };
            var vtt = await new SubtitleService(cfg, NullLogger<SubtitleService>.Instance).GetVttAsync(item, info.Streams[2].Index, default);
            Assert.NotNull(vtt);
            Assert.StartsWith("WEBVTT", vtt);
            Assert.Contains("Indlejret tekst", vtt);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { /* sqlite */ }
        }
    }

    private static void Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, err);
    }
}
