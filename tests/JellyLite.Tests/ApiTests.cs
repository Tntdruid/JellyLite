using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace JellyLite.Tests;

public sealed class ApiTests(ServerFixture fx) : IClassFixture<ServerFixture>
{
    private static List<JsonElement> Items(JsonElement r) => r.GetProperty("Items").EnumerateArray().ToList();
    private static string Name(JsonElement e) => e.GetProperty("Name").GetString()!;
    private static string Id(JsonElement e) => e.GetProperty("Id").GetString()!;

    private static async Task<JsonElement> Json(HttpClient c, string url)
    {
        var r = await c.GetAsync(url);
        Assert.True(r.IsSuccessStatusCode, $"{url} -> {(int)r.StatusCode}");
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> Library(HttpClient c, string name) =>
        Items(await Json(c, "/UserViews")).First(v => Name(v) == name);

    [Fact]
    public async Task Public_info_is_open_and_the_rest_needs_a_token()
    {
        var c = fx.Anonymous();
        var info = await Json(c, "/System/Info/Public");
        Assert.Equal("Test", info.GetProperty("ServerName").GetString());
        Assert.Equal(32, info.GetProperty("Id").GetString()!.Length);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/UserViews")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/Users/Me")).StatusCode);
    }

    [Fact]
    public async Task Quick_connect_signs_in_and_logout_revokes_the_token()
    {
        var (c, userId, _) = await fx.LoginAsync("Carol");
        var me = await Json(c, "/Users/Me");
        Assert.Equal("Carol", Name(me));
        Assert.Equal(userId, Id(me));

        (await c.PostAsync("/Sessions/Logout", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/Users/Me")).StatusCode);
    }

    [Fact]
    public async Task Quick_connect_secret_is_useless_before_approval()
    {
        var c = fx.Anonymous();
        var init = await (await c.PostAsync("/QuickConnect/Initiate", null)).Content.ReadFromJsonAsync<JsonElement>();
        var secret = init.GetProperty("Secret").GetString();
        var check = await Json(c, "/QuickConnect/Connect?secret=" + secret);
        Assert.False(check.GetProperty("Authenticated").GetBoolean());
        var r = await c.PostAsJsonAsync("/Users/AuthenticateWithQuickConnect", new { Secret = secret });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Views_have_the_collection_types_TvFin_shows()
    {
        var (c, _, _) = await fx.LoginAsync("Bob");
        var views = Items(await Json(c, "/UserViews"));
        Assert.Equal(["Film", "Serier", "YouTube"], views.Select(Name));
        Assert.Equal(["movies", "tvshows", "homevideos"], views.Select(v => v.GetProperty("CollectionType").GetString()));
        Assert.All(views, v => Assert.Equal("CollectionFolder", v.GetProperty("Type").GetString()));
    }

    [Fact]
    public async Task Libraries_get_a_wide_image_from_their_newest_content()
    {
        var (c, _, _) = await fx.LoginAsync("Bob");
        foreach (var lib in Items(await Json(c, "/UserViews")))
        {
            var tag = lib.GetProperty("ImageTags").GetProperty("Primary").GetString();
            var r = await fx.Anonymous().GetAsync($"/Items/{Id(lib)}/Images/Primary?fillHeight=360&tag={tag}");
            Assert.True(r.IsSuccessStatusCode, Name(lib));
        }
        // Film: Inception's fanart (not its tall poster); YouTube: the video thumbnail
        var film = await Library(c, "Film");
        var inception = Items(await Json(c, $"/Items?ParentId={Id(film)}&Recursive=true&IncludeItemTypes=Movie&SortBy=SortName"))[0];
        Assert.Equal(inception.GetProperty("BackdropImageTags")[0].GetString(), film.GetProperty("ImageTags").GetProperty("Primary").GetString());
        var yt = await Library(c, "YouTube");
        var video = Items(await Json(c, $"/Items?ParentId={Id(yt)}&SortBy=SortName")).First(v => Name(v) == "Video One");
        Assert.Equal(video.GetProperty("ImageTags").GetProperty("Primary").GetString(), yt.GetProperty("ImageTags").GetProperty("Primary").GetString());
    }

    [Fact]
    public async Task Movies_get_metadata_from_nfo_and_file_names()
    {
        var (c, _, _) = await fx.LoginAsync("Bob");
        var lib = await Library(c, "Film");
        var movies = Items(await Json(c, $"/Items?ParentId={Id(lib)}&Recursive=true&IncludeItemTypes=Movie&SortBy=SortName"));
        Assert.Equal(["Inception", "Other Movie"], movies.Select(Name));   // the trailer is not a movie
        Assert.Equal(2015, movies[1].GetProperty("ProductionYear").GetInt32());

        var full = await Json(c, "/Items/" + Id(movies[0]));
        Assert.Equal(2010, full.GetProperty("ProductionYear").GetInt32());
        Assert.Equal("Drømme i drømme.", full.GetProperty("Overview").GetString());
        Assert.Equal("27205", full.GetProperty("ProviderIds").GetProperty("Tmdb").GetString());
        Assert.True(full.GetProperty("ImageTags").TryGetProperty("Primary", out _));
        Assert.Single(full.GetProperty("BackdropImageTags").EnumerateArray());
        var people = full.GetProperty("People").EnumerateArray().ToList();
        Assert.Contains(people, p => Name(p) == "Leonardo DiCaprio" && p.GetProperty("Type").GetString() == "Actor" && p.GetProperty("Role").GetString() == "Cobb");
        Assert.Contains(people, p => Name(p) == "Christopher Nolan" && p.GetProperty("Type").GetString() == "Director");

        var sub = full.GetProperty("MediaSources")[0].GetProperty("MediaStreams").EnumerateArray().Single(s => s.GetProperty("Type").GetString() == "Subtitle");
        Assert.Equal("dan", sub.GetProperty("Language").GetString());
        Assert.True(sub.GetProperty("IsExternal").GetBoolean());
    }

    [Fact]
    public async Task Series_seasons_and_episodes_are_built_from_folders()
    {
        var (c, _, _) = await fx.LoginAsync("Bob");
        var lib = await Library(c, "Serier");
        var series = Items(await Json(c, $"/Items?ParentId={Id(lib)}&Recursive=true&IncludeItemTypes=Series"));
        var show = Assert.Single(series);
        Assert.Equal("Test Show", Name(show));
        Assert.Equal(2020, show.GetProperty("ProductionYear").GetInt32());

        var seasons = Items(await Json(c, $"/Items?ParentId={Id(show)}"));
        Assert.Equal(["Sæson 1", "Sæson 2", "Specials"], seasons.Select(Name).Order(StringComparer.Ordinal).OrderBy(n => n == "Specials"));

        var eps = Items(await Json(c, $"/Shows/{Id(show)}/Episodes"));
        Assert.Equal(["Piloten", "Second", "Tenth", "Episode 1", "Bonus"], eps.Select(Name));
        Assert.Equal("Test Show", eps[0].GetProperty("SeriesName").GetString());
        Assert.Equal(10, eps[2].GetProperty("IndexNumber").GetInt32());
        Assert.Equal(2, eps[3].GetProperty("ParentIndexNumber").GetInt32());
        // Episode borrows the series backdrop
        Assert.Equal(Id(show), eps[0].GetProperty("ParentBackdropItemId").GetString());

        // Next episode as the player asks for it
        var next = Items(await Json(c, $"/Shows/{Id(show)}/Episodes?StartItemId={Id(eps[2])}&Limit=2"));
        Assert.Equal("Episode 1", Name(next[1]));
    }

    [Fact]
    public async Task Same_name_actors_with_different_tmdb_ids_stay_separate()
    {
        var (c, _, _) = await fx.LoginAsync("Bob");
        var lib = await Library(c, "Serier");
        var show = Items(await Json(c, $"/Items?ParentId={Id(lib)}&Recursive=true&IncludeItemTypes=Series"))[0];
        var pilot = Items(await Json(c, $"/Shows/{Id(show)}/Episodes"))[0];
        var people = (await Json(c, "/Items/" + Id(pilot))).GetProperty("People").EnumerateArray().ToList();

        var johns = people.Where(p => Name(p) == "John Smith").ToList();
        Assert.Equal(2, johns.Select(Id).Distinct().Count());
        Assert.Contains(johns, p => p.GetProperty("Type").GetString() == "Actor");      // series regular
        Assert.Contains(people, p => Name(p) == "Jane Doe" && p.GetProperty("Type").GetString() == "GuestStar");

        // A guest star's page lists the series, even though they're only in one episode
        var jane = people.First(p => Name(p) == "Jane Doe");
        var credits = Items(await Json(c, $"/Items?PersonIds={Id(jane)}&Recursive=true&IncludeItemTypes=Movie,Series,Video,MusicVideo"));
        Assert.Equal(["Test Show"], credits.Select(Name));
    }

    [Fact]
    public async Task YouTube_videos_are_grouped_by_channel()
    {
        var (c, _, _) = await fx.LoginAsync("Bob");
        var lib = await Library(c, "YouTube");
        var studios = Items(await Json(c, $"/Studios?ParentId={Id(lib)}&Recursive=true"));
        Assert.Equal(["Kanal A", "Kanal B"], studios.Select(Name));
        Assert.True(studios[0].GetProperty("ImageTags").TryGetProperty("Primary", out _));

        var videos = Items(await Json(c, $"/Items?ParentId={Id(lib)}&StudioIds={Id(studios[0])}&Recursive=true&IncludeItemTypes=Movie,Video,Episode,MusicVideo"));
        var v = Assert.Single(videos);
        Assert.Equal("Video One", Name(v));
        Assert.Equal("Video", v.GetProperty("Type").GetString());
        Assert.Equal(2024, v.GetProperty("ProductionYear").GetInt32());
        Assert.Equal(1255000000, v.GetProperty("RunTimeTicks").GetInt64());

        // Without metadata: title from the file name, channel from the folder
        var all = Items(await Json(c, $"/Items?ParentId={Id(lib)}&SortBy=SortName"));
        Assert.Contains(all, x => Name(x) == "Something Else" && x.GetProperty("Studios")[0].GetProperty("Name").GetString() == "Kanal B");
    }

    [Fact]
    public async Task Images_are_served_without_token()
    {
        var (c, _, _) = await fx.LoginAsync("Bob");
        var lib = await Library(c, "Film");
        var movie = Items(await Json(c, $"/Items?ParentId={Id(lib)}&Recursive=true&IncludeItemTypes=Movie&SortBy=SortName"))[0];
        var tag = movie.GetProperty("ImageTags").GetProperty("Primary").GetString();
        var r = await fx.Anonymous().GetAsync($"/Items/{Id(movie)}/Images/Primary?fillHeight=360&tag={tag}");
        r.EnsureSuccessStatusCode();
        Assert.Equal("image/jpeg", r.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await fx.Anonymous().GetAsync($"/Items/{Id(movie)}/Images/Logo")).StatusCode);
    }

    [Fact]
    public async Task Playback_is_direct_play_with_range_requests_and_vtt_subtitles()
    {
        var (c, _, token) = await fx.LoginAsync("Alice");
        var lib = await Library(c, "Film");
        var movie = Items(await Json(c, $"/Items?ParentId={Id(lib)}&Recursive=true&IncludeItemTypes=Movie&SortBy=SortName"))[0];

        var info = await (await c.PostAsJsonAsync($"/Items/{Id(movie)}/PlaybackInfo?userId=x&IsPlayback=true", new { DeviceProfile = new { } })).Content.ReadFromJsonAsync<JsonElement>();
        var src = info.GetProperty("MediaSources")[0];
        Assert.True(src.GetProperty("SupportsDirectPlay").GetBoolean());
        Assert.Equal("mkv", src.GetProperty("Container").GetString());

        // Same URL TvFin builds: only api_key, no Authorization header
        var plain = fx.Factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, $"/Videos/{Id(movie)}/stream?static=true&mediaSourceId={src.GetProperty("Id").GetString()}&api_key={token}");
        req.Headers.Range = new RangeHeaderValue(100, 199);
        var r = await plain.SendAsync(req);
        Assert.Equal(HttpStatusCode.PartialContent, r.StatusCode);
        Assert.Equal(100, (await r.Content.ReadAsByteArrayAsync()).Length);
        Assert.Equal(HttpStatusCode.Unauthorized, (await plain.GetAsync($"/Videos/{Id(movie)}/stream?static=true")).StatusCode);

        var sub = src.GetProperty("MediaStreams").EnumerateArray().Single(s => s.GetProperty("Type").GetString() == "Subtitle");
        var vtt = await plain.GetStringAsync(sub.GetProperty("DeliveryUrl").GetString());
        Assert.StartsWith("WEBVTT", vtt);
        Assert.Contains("00:00:01.000 --> 00:00:02.500", vtt);
        Assert.Contains("Hej verden", vtt);
    }

    [Fact]
    public async Task Progress_gives_resume_and_next_up_follows_watched_episodes()
    {
        var (c, _, _) = await fx.LoginAsync("Alice");
        var yt = await Library(c, "YouTube");
        var video = Items(await Json(c, $"/Items?ParentId={Id(yt)}&SortBy=SortName")).First(v => Name(v) == "Video One");

        // Half-way: a resume point. Progress alone is enough (TV switched off without "stopped").
        (await c.PostAsJsonAsync("/Sessions/Playing", new { ItemId = Id(video), PositionTicks = 0 })).EnsureSuccessStatusCode();
        (await c.PostAsJsonAsync("/Sessions/Playing/Progress", new { ItemId = Id(video), PositionTicks = 600_000_000L })).EnsureSuccessStatusCode();
        var resume = Items(await Json(c, "/UserItems/Resume?userId=x&MediaTypes=Video&Limit=24"));
        var r = Assert.Single(resume);
        Assert.Equal(600_000_000L, r.GetProperty("UserData").GetProperty("PlaybackPositionTicks").GetInt64());

        // Watched to the end: played, no longer resumable
        (await c.PostAsJsonAsync("/Sessions/Playing/Stopped", new { ItemId = Id(video), PositionTicks = 1_250_000_000L })).EnsureSuccessStatusCode();
        Assert.Empty(Items(await Json(c, "/UserItems/Resume")));
        var after = await Json(c, "/Items/" + Id(video));
        Assert.True(after.GetProperty("UserData").GetProperty("Played").GetBoolean());
        Assert.Equal(1, after.GetProperty("UserData").GetProperty("PlayCount").GetInt32());

        // Mark the pilot watched -> next up is episode 2
        var tv = await Library(c, "Serier");
        var show = Items(await Json(c, $"/Items?ParentId={Id(tv)}&Recursive=true&IncludeItemTypes=Series"))[0];
        var eps = Items(await Json(c, $"/Shows/{Id(show)}/Episodes"));
        (await c.PostAsync($"/UserPlayedItems/{Id(eps[0])}?userId=x", null)).EnsureSuccessStatusCode();
        var nextUp = Items(await Json(c, "/Shows/NextUp?userId=x&Limit=24"));
        Assert.Equal(["Second"], nextUp.Select(Name));

        // Another user has their own state
        var (bob, _, _) = await fx.LoginAsync("Bob");
        Assert.Empty(Items(await Json(bob, "/Shows/NextUp")));
    }

    [Fact]
    public async Task Favorites_sorting_and_search()
    {
        var (c, _, _) = await fx.LoginAsync("Alice");
        var film = await Library(c, "Film");
        var movies = Items(await Json(c, $"/Items?ParentId={Id(film)}&Recursive=true&IncludeItemTypes=Movie&SortBy=SortName&SortOrder=Descending"));
        Assert.Equal(["Other Movie", "Inception"], movies.Select(Name));

        (await c.PostAsync($"/UserFavoriteItems/{Id(movies[1])}", null)).EnsureSuccessStatusCode();
        var favs = Items(await Json(c, "/Items?Filters=IsFavorite&Recursive=true&IncludeItemTypes=Movie,Series,Episode,Video,MusicVideo&SortBy=SortName&Limit=40"));
        Assert.Equal(["Inception"], favs.Select(Name));

        var found = Items(await Json(c, "/Items?searchTerm=PILOT&Recursive=true&IncludeItemTypes=Movie,Series,Episode,Video,MusicVideo"));
        Assert.Equal(["Piloten"], found.Select(Name));   // the name from the NFO, case-insensitive
        found = Items(await Json(c, "/Items?searchTerm=Drømme&Recursive=true&IncludeItemTypes=Movie"));
        Assert.Empty(found);   // search is on names, not descriptions
    }

    [Fact]
    public async Task Latest_groups_episodes_into_their_series()
    {
        var (c, _, _) = await fx.LoginAsync("Bob");
        var tv = await Library(c, "Serier");
        var latest = (await Json(c, $"/Items/Latest?ParentId={Id(tv)}&Limit=16")).EnumerateArray().ToList();
        var only = Assert.Single(latest);
        Assert.Equal("Series", only.GetProperty("Type").GetString());
    }

    [Fact]
    public async Task Admin_page_is_served()
    {
        var html = await fx.Factory.CreateClient().GetStringAsync("/");
        Assert.Contains("Quick Connect", html);
    }
}
