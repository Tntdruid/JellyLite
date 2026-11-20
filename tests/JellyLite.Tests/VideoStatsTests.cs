using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JellyLite.Api;
using JellyLite.Auth;

namespace JellyLite.Tests;

/// <summary>The calls Jellyfin Video Stats makes (app/jellyfin.py), with its own headers and parameters.</summary>
public sealed class VideoStatsTests(ServerFixture fx) : IClassFixture<ServerFixture>
{
    private const string Fields = "Genres,Studios,People,ProductionYear,OfficialRating,CommunityRating,ParentId,Path";

    private async Task<HttpClient> VideoStatsClientAsync()
    {
        var r = await fx.Factory.CreateClient().PostAsJsonAsync("/admin/api/apikeys", new { name = "Video Stats" });
        var key = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!;
        var c = fx.Factory.CreateClient();
        c.DefaultRequestHeaders.TryAddWithoutValidation("Authorization",
            $"MediaBrowser Client=\"Jellyfin Video Stats\", Device=\"Home Assistant\", DeviceId=\"jellyfin-video-stats\", Version=\"0.1\", Token=\"{key}\"");
        c.DefaultRequestHeaders.TryAddWithoutValidation("X-Emby-Token", key);
        return c;
    }

    [Fact]
    public async Task Sees_what_is_playing_and_reads_items_users_and_episodes()
    {
        var stats = await VideoStatsClientAsync();
        var (tv, aliceId, _) = await fx.LoginAsync("Alice");
        var yt = (await tv.GetFromJsonAsync<JsonElement>("/UserViews")).GetProperty("Items").EnumerateArray().First(v => v.GetProperty("Name").GetString() == "YouTube");
        var video = (await tv.GetFromJsonAsync<JsonElement>($"/Items?ParentId={yt.GetProperty("Id").GetString()}&SortBy=SortName"))
            .GetProperty("Items").EnumerateArray().First(v => v.GetProperty("Name").GetString() == "Video One");
        var videoId = video.GetProperty("Id").GetString()!;

        // TvFin starts playing and reports progress (paused, Danish subtitles chosen)
        (await tv.PostAsJsonAsync("/Sessions/Playing", new { ItemId = videoId, PositionTicks = 0L, IsPaused = false, PlayMethod = "DirectPlay", AudioStreamIndex = 1, SubtitleStreamIndex = -1 })).EnsureSuccessStatusCode();
        (await tv.PostAsJsonAsync("/Sessions/Playing/Progress", new { ItemId = videoId, PositionTicks = 300_000_000L, IsPaused = true, PlayMethod = "DirectPlay", AudioStreamIndex = 1, SubtitleStreamIndex = 3 })).EnsureSuccessStatusCode();

        // sessions()
        var sessions = (await stats.GetFromJsonAsync<JsonElement>("/Sessions")).EnumerateArray().ToList();
        var s = Assert.Single(sessions);
        Assert.Equal("Alice", s.GetProperty("UserName").GetString());
        Assert.Equal(aliceId, s.GetProperty("UserId").GetString());
        Assert.Equal("TvFin", s.GetProperty("Client").GetString());
        Assert.Equal(videoId, s.GetProperty("NowPlayingItem").GetProperty("Id").GetString());
        Assert.Equal("Video", s.GetProperty("NowPlayingItem").GetProperty("Type").GetString());
        var ps = s.GetProperty("PlayState");
        Assert.Equal(300_000_000L, ps.GetProperty("PositionTicks").GetInt64());
        Assert.True(ps.GetProperty("IsPaused").GetBoolean());
        Assert.Equal("DirectPlay", ps.GetProperty("PlayMethod").GetString());
        Assert.Equal(3, ps.GetProperty("SubtitleStreamIndex").GetInt32());

        // item(id, user) and items(ids): Path, Studios (channel) and parent
        var item = (await stats.GetFromJsonAsync<JsonElement>($"/Items?ids={videoId}&fields={Fields}&enableImages=false&userId={aliceId}"))
            .GetProperty("Items")[0];
        Assert.EndsWith("Video One [abcdefghijk].mp4", item.GetProperty("Path").GetString());
        var studio = item.GetProperty("Studios")[0];
        Assert.Equal("Kanal A", studio.GetProperty("Name").GetString());
        Assert.Matches("^[0-9a-f]{32}$", studio.GetProperty("Id").GetString());
        var noUser = (await stats.GetFromJsonAsync<JsonElement>($"/Items?ids={videoId}&fields={Fields}&enableImages=false&recursive=true")).GetProperty("Items");
        Assert.Equal(1, noUser.GetArrayLength());
        var parent = (await stats.GetFromJsonAsync<JsonElement>($"/Items?ids={item.GetProperty("ParentId").GetString()}&fields={Fields}")).GetProperty("Items")[0];
        Assert.Equal("CollectionFolder", parent.GetProperty("Type").GetString());

        // users()
        var users = (await stats.GetFromJsonAsync<JsonElement>("/Users")).EnumerateArray().Select(u => u.GetProperty("Name").GetString()).ToList();
        Assert.Equal(["Alice", "Bob", "Carol"], users);

        // recent_episodes() and episodes(series, user)
        var recent = (await stats.GetFromJsonAsync<JsonElement>("/Items?IncludeItemTypes=Episode&Recursive=true&SortBy=DateCreated&SortOrder=Descending&Limit=100&Fields=DateCreated&enableImages=false"))
            .GetProperty("Items").EnumerateArray().ToList();
        Assert.Equal(5, recent.Count);
        var seriesId = recent[0].GetProperty("SeriesId").GetString();
        (await tv.PostAsync($"/UserPlayedItems/{recent.First(e => e.GetProperty("Name").GetString() == "Piloten").GetProperty("Id").GetString()}", null)).EnsureSuccessStatusCode();
        var eps = (await stats.GetFromJsonAsync<JsonElement>($"/Shows/{seriesId}/Episodes?fields=UserData&enableImages=false&userId={aliceId}"))
            .GetProperty("Items").EnumerateArray().ToList();
        Assert.All(eps, e => Assert.Equal("FileSystem", e.GetProperty("LocationType").GetString()));
        Assert.True(eps.First(e => e.GetProperty("Name").GetString() == "Piloten").GetProperty("UserData").GetProperty("Played").GetBoolean());
        Assert.Equal(5, (await stats.GetFromJsonAsync<JsonElement>($"/Shows/{seriesId}/Episodes?fields=UserData&enableImages=false")).GetProperty("Items").GetArrayLength());

        // playback_reporting(): no such plugin -> 404, which Video Stats treats as "not installed"
        var pr = await stats.PostAsJsonAsync("/user_usage_stats/submit_custom_query", new { CustomQueryString = "SELECT 1", ReplaceUserId = false });
        Assert.Equal(HttpStatusCode.NotFound, pr.StatusCode);

        // image()
        (await stats.GetAsync($"/Items/{videoId}/Images/Primary?maxWidth=300&quality=85")).EnsureSuccessStatusCode();

        // Stopped: the session is gone
        (await tv.PostAsJsonAsync("/Sessions/Playing/Stopped", new { ItemId = videoId, PositionTicks = 300_000_000L })).EnsureSuccessStatusCode();
        Assert.Equal(0, (await stats.GetFromJsonAsync<JsonElement>("/Sessions")).GetArrayLength());
    }

    [Fact]
    public async Task A_user_only_sees_their_own_session_and_user()
    {
        var (bob, _, _) = await fx.LoginAsync("Bob");
        var users = (await bob.GetFromJsonAsync<JsonElement>("/Users")).EnumerateArray().Select(u => u.GetProperty("Name").GetString());
        Assert.Equal(["Bob"], users);
        Assert.Equal(0, (await bob.GetFromJsonAsync<JsonElement>("/Sessions")).GetArrayLength());
    }

    [Fact]
    public void A_tv_switched_off_without_stop_drops_out_after_a_minute()
    {
        var tracker = new SessionTracker();
        var user = new UserInfo(Guid.NewGuid(), "Stue");
        var client = new ClientInfo("TvFin", "Samsung TV", "tv1", "1.10.0", "t");
        var t0 = new DateTime(2026, 1, 1, 20, 0, 0, DateTimeKind.Utc);
        tracker.Report(user, client, Guid.NewGuid(), 0, false, "DirectPlay", 1, -1, t0);
        Assert.Single(tracker.Active(t0.AddSeconds(50)));
        Assert.Empty(tracker.Active(t0.AddSeconds(61)));
    }
}
