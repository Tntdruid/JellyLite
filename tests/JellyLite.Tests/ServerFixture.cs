using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JellyLite.Tests;

/// <summary>Starts the server against a small fake media library in a temp folder (no ffprobe).</summary>
public sealed class ServerFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "jellylite-tests-" + Guid.NewGuid().ToString("N")[..8]);
    public AppConfig Config { get; }
    public WebApplicationFactory<Program> Factory { get; }

    public ServerFixture()
    {
        BuildMedia();
        Config = new AppConfig
        {
            ServerName = "Test",
            DataDir = Path.Combine(Root, "data"),
            Ffprobe = "",
            Ffmpeg = "",
            Libraries =
            [
                new() { Name = "Film", Path = Path.Combine(Root, "media", "Film"), Type = "movies" },
                new() { Name = "Serier", Path = Path.Combine(Root, "media", "Serier"), Type = "tvshows" },
                new() { Name = "YouTube", Path = Path.Combine(Root, "media", "YouTube"), Type = "youtube" },
            ],
            Users = [new() { Name = "Alice" }, new() { Name = "Bob" }, new() { Name = "Carol" }],
        };
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.ConfigureServices(s =>
            {
                s.RemoveAll<AppConfig>();
                s.AddSingleton(Config);
            });
        });
    }

    public HttpClient Anonymous()
    {
        var c = Factory.CreateClient();
        c.DefaultRequestHeaders.TryAddWithoutValidation("Authorization",
            "MediaBrowser Client=\"TvFin\", Device=\"Test TV\", DeviceId=\"dev-" + Guid.NewGuid().ToString("N")[..6] + "\", Version=\"1.10.0\"");
        return c;
    }

    /// <summary>Signs in through the whole Quick Connect flow and returns a client with the token.</summary>
    public async Task<(HttpClient Client, string UserId, string Token)> LoginAsync(string userName)
    {
        var c = Anonymous();
        var init = await (await c.PostAsync("/QuickConnect/Initiate", null)).Content.ReadFromJsonAsync<JsonElement>();
        var code = init.GetProperty("Code").GetString()!;
        var secret = init.GetProperty("Secret").GetString()!;

        var admin = Factory.CreateClient();
        var state = await admin.GetFromJsonAsync<JsonElement>("/admin/api/state");
        var userId = state.GetProperty("users").EnumerateArray().First(u => u.GetProperty("name").GetString() == userName).GetProperty("id").GetString()!;
        (await admin.PostAsJsonAsync("/admin/api/quickconnect", new { code, userId })).EnsureSuccessStatusCode();

        var auth = await (await c.PostAsJsonAsync("/Users/AuthenticateWithQuickConnect", new { Secret = secret })).Content.ReadFromJsonAsync<JsonElement>();
        var token = auth.GetProperty("AccessToken").GetString()!;
        c.DefaultRequestHeaders.Remove("Authorization");
        c.DefaultRequestHeaders.TryAddWithoutValidation("Authorization",
            "MediaBrowser Client=\"TvFin\", Device=\"Test TV\", DeviceId=\"dev1\", Version=\"1.10.0\", Token=\"" + token + "\"");
        return (c, userId, token);
    }

    private void BuildMedia()
    {
        void File(string rel, string content = "")
        {
            var p = Path.Combine(Root, "media", rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            if (content.Length == 0 && Path.GetExtension(p) is ".mkv" or ".mp4" or ".webm")
                System.IO.File.WriteAllBytes(p, Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray());
            else
                System.IO.File.WriteAllText(p, content);
        }

        // Movie in its own folder with NFO, poster, fanart and Danish subtitles
        File("Film/Inception (2010)/Inception (2010).mkv");
        File("Film/Inception (2010)/Inception (2010).nfo", """
            <?xml version="1.0" encoding="utf-8" standalone="yes"?>
            <movie>
              <title>Inception</title>
              <plot>Drømme i drømme.</plot>
              <year>2010</year>
              <rating>8.8</rating>
              <genre>Sci-Fi</genre><genre>Action</genre>
              <studio>Warner Bros.</studio>
              <uniqueid type="tmdb">27205</uniqueid>
              <actor><name>Leonardo DiCaprio</name><role>Cobb</role><tmdbid>6193</tmdbid><thumb>https://example.invalid/leo.jpg</thumb></actor>
              <director>Christopher Nolan</director>
            </movie>
            https://www.themoviedb.org/movie/27205
            """);
        File("Film/Inception (2010)/poster.jpg", "jpg");
        File("Film/Inception (2010)/fanart.jpg", "jpg");
        File("Film/Inception (2010)/Inception (2010).da.srt", "1\r\n00:00:01,000 --> 00:00:02,500\r\nHej verden\r\n");
        File("Film/Inception (2010)/Inception (2010)-trailer.mkv");
        // Release-style name without metadata
        File("Film/Other.Movie.2015.1080p.BluRay.x264.mkv");

        // Series: same actor name for two different people (different TMDB ids)
        File("Serier/Test Show (2020)/tvshow.nfo", """
            <tvshow><title>Test Show</title><plot>En serie.</plot><genre>Drama</genre>
              <actor><name>John Smith</name><role>Hovedperson</role><tmdbid>100</tmdbid></actor>
            </tvshow>
            """);
        File("Serier/Test Show (2020)/poster.jpg", "jpg");
        File("Serier/Test Show (2020)/fanart.jpg", "jpg");
        File("Serier/Test Show (2020)/Season 01/Test Show - S01E01 - Pilot.mkv");
        File("Serier/Test Show (2020)/Season 01/Test Show - S01E01 - Pilot.nfo", """
            <episodedetails><title>Piloten</title><season>1</season><episode>1</episode><aired>2020-01-05</aired>
              <actor><name>John Smith</name><role>Hovedperson</role><tmdbid>100</tmdbid></actor>
              <actor><name>Jane Doe</name><role>Gæst</role><tmdbid>200</tmdbid></actor>
              <actor><name>John Smith</name><role>Postbud</role><tmdbid>999</tmdbid></actor>
            </episodedetails>
            """);
        File("Serier/Test Show (2020)/Season 01/Test Show - S01E01 - Pilot-thumb.jpg", "jpg");
        File("Serier/Test Show (2020)/Season 01/Test Show - S01E02 - Second.mkv");
        File("Serier/Test Show (2020)/Season 01/Test Show - S01E10 - Tenth.mkv");
        // Placeholder title from TMDB with the real title as original title (what Episode Title Fixer fixed)
        File("Serier/Test Show (2020)/Season 01/Test Show - S01E10 - Tenth.nfo",
            "<episodedetails><title>Afsnit 10</title><originaltitle>Tiende</originaltitle><season>1</season><episode>10</episode></episodedetails>");
        File("Serier/Test Show (2020)/Season 02/Test.Show.S02E01.mkv");
        File("Serier/Test Show (2020)/Specials/Test Show - S00E01 - Bonus.mkv");

        // YouTube: info.json + thumbnail, and one without metadata (channel = folder)
        File("YouTube/Kanal A/20240101 - Video One [abcdefghijk].mp4");
        File("YouTube/Kanal A/20240101 - Video One [abcdefghijk].info.json",
            """{"id":"abcdefghijk","title":"Video One","channel":"Kanal A","upload_date":"20240101","duration":125.5,"description":"Første video"}""");
        File("YouTube/Kanal A/20240101 - Video One [abcdefghijk].jpg", "jpg");
        File("YouTube/Kanal A/poster.jpg", "jpg");
        File("YouTube/Kanal B/Something Else [bbbbbbbbbbb].webm");
    }

    public void Dispose()
    {
        Factory.Dispose();
        try { Directory.Delete(Root, true); } catch (IOException) { /* sqlite may still hold the file */ }
    }
}
