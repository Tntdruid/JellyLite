using JellyLite.Library;
using JellyLite.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace JellyLite.Tests;

/// <summary>A NAS that is switched off at night must not empty the library.</summary>
public sealed class OfflineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jellylite-offline-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _nas;
    private readonly Scanner _scanner;

    public OfflineTests()
    {
        _nas = Path.Combine(_root, "nas", "Serier");
        Write("Show/tvshow.nfo", "<tvshow><title>Show</title><actor><name>Jane Doe</name><tmdbid>200</tmdbid></actor></tvshow>");
        Write("Show/Season 01/Show - S01E01 - One.mkv", "x");
        Write("Show/Season 01/Show - S01E02 - Two.mkv", "x");
        var cfg = new AppConfig
        {
            DataDir = Path.Combine(_root, "data"),
            Libraries = [new() { Name = "Serier", Path = _nas, Type = "tvshows" }],
        };
        _scanner = new Scanner(cfg, new Db(cfg), NullLogger.Instance);
    }

    private void Write(string rel, string content)
    {
        var p = Path.Combine(_nas, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    private static List<string> Episodes(LibrarySnapshot s) =>
        s.Items.Values.Where(i => i.Type == ItemTypes.Episode).Select(i => i.Name).Order().ToList();

    [Fact]
    public void Missing_folder_keeps_last_known_items_and_they_come_back_unchanged()
    {
        var online = _scanner.Scan(new LibrarySnapshot());
        Assert.Equal(["One", "Two"], Episodes(online));
        var ids = online.Items.Keys.ToHashSet();

        // NAS switched off: the share's folder is gone
        Directory.Move(_nas, _nas + "-off");
        var offline = _scanner.Scan(online);
        Assert.True(offline.Libraries[0].Offline);
        Assert.Equal(["One", "Two"], Episodes(offline));
        Assert.Contains(offline.People.Values, p => p.Name == "Jane Doe" && p.ItemIds.Count > 0);

        // Still off at the next scan (previous snapshot is the kept one)
        var stillOffline = _scanner.Scan(offline);
        Assert.Equal(["One", "Two"], Episodes(stillOffline));

        // Back on: same ids (watch state is keyed by them), no longer offline
        Directory.Move(_nas + "-off", _nas);
        var back = _scanner.Scan(stillOffline);
        Assert.False(back.Libraries[0].Offline);
        Assert.Equal(ids, back.Items.Keys.ToHashSet());
        Assert.DoesNotContain(back.Items.Values, i => i.KeptFromPreviousScan);
    }

    [Fact]
    public void Empty_mount_point_counts_as_offline()
    {
        var online = _scanner.Scan(new LibrarySnapshot());
        // An unmounted share often leaves the folder itself, but empty
        foreach (var d in Directory.GetDirectories(_nas)) Directory.Delete(d, true);
        var offline = _scanner.Scan(online);
        Assert.True(offline.Libraries[0].Offline);
        Assert.Equal(["One", "Two"], Episodes(offline));
    }

    [Fact]
    public void A_deleted_file_disappears_while_the_nas_is_on()
    {
        var online = _scanner.Scan(new LibrarySnapshot());
        File.Delete(Path.Combine(_nas, "Show", "Season 01", "Show - S01E02 - Two.mkv"));
        var after = _scanner.Scan(online);
        Assert.False(after.Libraries[0].Offline);
        Assert.Equal(["One"], Episodes(after));
    }

    [Fact]
    public void Folder_missing_from_the_start_is_just_empty()
    {
        Directory.Delete(_nas, true);
        var s = _scanner.Scan(new LibrarySnapshot());
        Assert.True(s.Libraries[0].Offline);
        Assert.Empty(Episodes(s));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { /* sqlite */ }
    }
}
