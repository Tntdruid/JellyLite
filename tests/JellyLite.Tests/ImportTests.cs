using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using JellyLite.Import;
using JellyLite.Library;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace JellyLite.Tests;

/// <summary>Imports watch state from a (fake) Jellyfin server.</summary>
public sealed class ImportTests(ServerFixture fx) : IClassFixture<ServerFixture>
{
    /// <summary>Answers like Jellyfin: paths as Jellyfin sees the NAS (/mnt/video/...), not like JellyLite does.</summary>
    private sealed class FakeJellyfin : HttpMessageHandler
    {
        public const string Key = "jf-secret";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            if (req.Headers.Authorization?.ToString().Contains($"Token=\"{Key}\"") != true)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            var path = req.RequestUri!.AbsolutePath;
            object body = path switch
            {
                "/Users" => new[] { new { Id = "jfcarol", Name = "carol" }, new { Id = "jfx", Name = "Someone Else" } },
                "/Users/jfcarol/Items" => new
                {
                    TotalRecordCount = 5,
                    Items = new object[]
                    {
                        new { Name = "Inception", Path = "/mnt/video/Film/Inception (2010)/Inception (2010).mkv",
                              UserData = new { Played = true, PlayCount = 2, PlaybackPositionTicks = 0L, IsFavorite = false, LastPlayedDate = "2025-01-01T20:00:00Z" } },
                        new { Name = "Second", Path = "/mnt/video/Serier/Test Show (2020)/Season 01/Test Show - S01E02 - Second.mkv",
                              UserData = new { Played = false, PlayCount = 0, PlaybackPositionTicks = 1234L, IsFavorite = false, LastPlayedDate = "2025-02-01T20:00:00Z" } },
                        new { Name = "Test Show", Path = "/mnt/video/Serier/Test Show (2020)",
                              UserData = new { Played = false, PlayCount = 0, PlaybackPositionTicks = 0L, IsFavorite = true } },
                        new { Name = "Gone", Path = "/mnt/video/Film/Gone (1999)/Gone.mkv",
                              UserData = new { Played = true, PlayCount = 1, PlaybackPositionTicks = 0L, IsFavorite = false } },
                        new { Name = "Untouched", Path = "/mnt/video/Film/Other.Movie.2015.1080p.BluRay.x264.mkv",
                              UserData = new { Played = false, PlayCount = 0, PlaybackPositionTicks = 0L, IsFavorite = false } },
                    },
                },
                _ => new { },
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            });
        }
    }

    private WebApplicationFactory<Program> Factory => fx.Factory.WithWebHostBuilder(b =>
        b.ConfigureServices(s => s.AddHttpClient(JellyfinImport.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeJellyfin())));

    [Fact]
    public async Task Imports_watch_state_matched_on_path_ending_and_suggests_users_by_name()
    {
        var factory = Factory;
        var admin = factory.CreateClient();
        var req = new { url = "http://jellyfin.test:8096", apiKey = FakeJellyfin.Key };

        var jfUsers = await (await admin.PostAsJsonAsync("/admin/api/jellyfin/users", req)).Content.ReadFromJsonAsync<JsonElement>();
        var state = await admin.GetFromJsonAsync<JsonElement>("/admin/api/state");
        var carolId = state.GetProperty("users").EnumerateArray().First(u => u.GetProperty("name").GetString() == "Carol").GetProperty("id").GetString();
        var carol = jfUsers.EnumerateArray().First(u => u.GetProperty("Id").GetString() == "jfcarol");
        Assert.Equal(carolId, carol.GetProperty("SuggestedUserId").GetString());   // "carol" -> "Carol"
        var other = jfUsers.EnumerateArray().First(u => u.GetProperty("Id").GetString() == "jfx");
        Assert.False(other.TryGetProperty("SuggestedUserId", out var s) && s.ValueKind == JsonValueKind.String);   // no JellyLite user of that name

        var import = new { req.url, req.apiKey, map = new Dictionary<string, string> { ["jfcarol"] = carolId! } };
        var result = (await (await admin.PostAsJsonAsync("/admin/api/jellyfin/import", import)).Content.ReadFromJsonAsync<JsonElement>())[0];
        Assert.Equal(4, result.GetProperty("WithData").GetInt32());
        Assert.Equal(3, result.GetProperty("Matched").GetInt32());
        Assert.Equal(["/mnt/video/Film/Gone (1999)/Gone.mkv"], result.GetProperty("UnmatchedSamples").EnumerateArray().Select(x => x.GetString()));

        // Seen from the TV as Carol
        var (c, _, _) = await LoginAsync(factory, "Carol");
        var inception = await FindAsync(c, "Inception", "Movie");
        Assert.True(inception.GetProperty("UserData").GetProperty("Played").GetBoolean());
        Assert.Equal(2, inception.GetProperty("UserData").GetProperty("PlayCount").GetInt32());
        var resume = (await c.GetFromJsonAsync<JsonElement>("/UserItems/Resume")).GetProperty("Items").EnumerateArray().ToList();
        Assert.Equal(1234, Assert.Single(resume).GetProperty("UserData").GetProperty("PlaybackPositionTicks").GetInt64());
        var show = await FindAsync(c, "Test Show", "Series");
        Assert.True(show.GetProperty("UserData").GetProperty("IsFavorite").GetBoolean());

        // Watched further in JellyLite since; a second import must not undo that or count twice
        var second = resume[0].GetProperty("Id").GetString();
        (await c.PostAsJsonAsync("/Sessions/Playing/Progress", new { ItemId = second, PositionTicks = 5000L })).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync("/admin/api/jellyfin/import", import)).EnsureSuccessStatusCode();
        resume = (await c.GetFromJsonAsync<JsonElement>("/UserItems/Resume")).GetProperty("Items").EnumerateArray().ToList();
        Assert.Equal(5000, Assert.Single(resume).GetProperty("UserData").GetProperty("PlaybackPositionTicks").GetInt64());
        Assert.Equal(2, (await FindAsync(c, "Inception", "Movie")).GetProperty("UserData").GetProperty("PlayCount").GetInt32());
    }

    [Fact]
    public async Task Wrong_jellyfin_key_gives_a_readable_error()
    {
        var r = await Factory.CreateClient().PostAsJsonAsync("/admin/api/jellyfin/users", new { url = "http://jellyfin.test:8096", apiKey = "wrong" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("Jellyfin", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public void Path_ending_must_be_unique()
    {
        var snap = new LibrarySnapshot();
        foreach (var show in new[] { "Show A", "Show B" })
        {
            var it = new Item { Id = Guid.NewGuid(), Type = ItemTypes.Episode, Name = show, Path = $"/media/Nas/TV/{show}/Season 01/S01E01.mkv" };
            snap.Items[it.Id] = it;
        }
        var index = JellyfinImport.BuildIndex(snap);
        Assert.Equal("Show B", JellyfinImport.Find(index, @"\\nas\video\TV\Show B\Season 01\S01E01.mkv")?.Name);
        Assert.Null(JellyfinImport.Find(index, "/elsewhere/S01E01.mkv"));   // same file name in both shows: no guess
    }

    private static async Task<JsonElement> FindAsync(HttpClient c, string name, string type)
    {
        var r = await c.GetFromJsonAsync<JsonElement>($"/Items?searchTerm={Uri.EscapeDataString(name)}&Recursive=true&IncludeItemTypes={type}");
        return r.GetProperty("Items").EnumerateArray().First(i => i.GetProperty("Name").GetString() == name);
    }

    private static async Task<(HttpClient, string, string)> LoginAsync(WebApplicationFactory<Program> factory, string userName)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "MediaBrowser Client=\"TvFin\", Device=\"TV\", DeviceId=\"imp\", Version=\"1\"");
        var init = await (await c.PostAsync("/QuickConnect/Initiate", null)).Content.ReadFromJsonAsync<JsonElement>();
        var admin = factory.CreateClient();
        var state = await admin.GetFromJsonAsync<JsonElement>("/admin/api/state");
        var userId = state.GetProperty("users").EnumerateArray().First(u => u.GetProperty("name").GetString() == userName).GetProperty("id").GetString()!;
        (await admin.PostAsJsonAsync("/admin/api/quickconnect", new { code = init.GetProperty("Code").GetString(), userId })).EnsureSuccessStatusCode();
        var auth = await (await c.PostAsJsonAsync("/Users/AuthenticateWithQuickConnect", new { Secret = init.GetProperty("Secret").GetString() })).Content.ReadFromJsonAsync<JsonElement>();
        var token = auth.GetProperty("AccessToken").GetString()!;
        c.DefaultRequestHeaders.Remove("Authorization");
        c.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"MediaBrowser Client=\"TvFin\", Device=\"TV\", DeviceId=\"imp\", Version=\"1\", Token=\"{token}\"");
        return (c, userId, token);
    }
}
