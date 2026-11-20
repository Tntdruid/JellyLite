using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace JellyLite.Tests;

/// <summary>API keys for other programs, e.g. AutoYTDownload asking for a scan after new downloads.</summary>
public sealed class ApiKeyTests(ServerFixture fx) : IClassFixture<ServerFixture>
{
    private async Task<string> NewKeyAsync()
    {
        var r = await fx.Factory.CreateClient().PostAsJsonAsync("/admin/api/apikeys", new { name = "AutoYTDownload" });
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!;
    }

    private HttpClient WithKey(string key, bool bearer = false)
    {
        var c = fx.Factory.CreateClient();
        if (bearer) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        else c.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{key}\"");
        return c;
    }

    [Fact]
    public async Task Scan_request_needs_an_api_key_and_picks_up_new_videos()
    {
        var key = await NewKeyAsync();
        var (user, _, _) = await fx.LoginAsync("Bob");
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Anonymous().PostAsync("/Library/Refresh", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsync("/Library/Refresh", null)).StatusCode);

        // A downloader drops a new video and asks for a scan, the way AutoYTDownload does it
        var file = Path.Combine(fx.Root, "media", "YouTube", "Kanal B", "Nyt klip [ccccccccccc].mp4");
        await File.WriteAllBytesAsync(file, new byte[1024]);
        var body = new { Updates = new[] { new { Path = "/mnt/Youtube/Kanal B", UpdateType = "Created" } } };
        Assert.Equal(HttpStatusCode.NoContent, (await WithKey(key).PostAsJsonAsync("/Library/Media/Updated", body)).StatusCode);

        var found = false;
        for (var i = 0; i < 50 && !found; i++)
        {
            var r = await user.GetFromJsonAsync<JsonElement>("/Items?searchTerm=Nyt%20klip&Recursive=true&IncludeItemTypes=Video");
            found = r.GetProperty("Items").GetArrayLength() == 1;
            if (!found) await Task.Delay(100);
        }
        Assert.True(found, "the new video did not show up after the scan request");
    }

    [Fact]
    public async Task Api_key_works_as_bearer_and_acts_for_the_user_in_userId()
    {
        var key = await NewKeyAsync();
        var c = WithKey(key, bearer: true);
        (await c.GetAsync("/System/Info")).EnsureSuccessStatusCode();

        // Reading works without a user (no watch state); changing a user's data needs to say which user
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/UserViews")).StatusCode);
        var anyMovie = (await c.GetFromJsonAsync<JsonElement>("/Items?Recursive=true&IncludeItemTypes=Movie")).GetProperty("Items")[0].GetProperty("Id").GetString();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"/UserPlayedItems/{anyMovie}", null)).StatusCode);
        var (_, userId, _) = await fx.LoginAsync("Alice");
        var views = await c.GetFromJsonAsync<JsonElement>($"/UserViews?userId={userId}");
        Assert.Equal(3, views.GetProperty("Items").GetArrayLength());
        var items = await c.GetFromJsonAsync<JsonElement>($"/Users/{userId}/Items?Recursive=true&IncludeItemTypes=Movie");
        Assert.Equal(2, items.GetProperty("Items").GetArrayLength());
    }

    [Fact]
    public async Task Deleted_key_stops_working()
    {
        var key = await NewKeyAsync();
        var admin = fx.Factory.CreateClient();
        var state = await admin.GetFromJsonAsync<JsonElement>("/admin/api/state");
        var id = state.GetProperty("apiKeys").EnumerateArray().First(k => k.GetProperty("key").GetString() == key).GetProperty("id").GetInt64();
        (await admin.DeleteAsync($"/admin/api/apikeys/{id}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await WithKey(key).PostAsync("/Library/Refresh", null)).StatusCode);
    }
}
