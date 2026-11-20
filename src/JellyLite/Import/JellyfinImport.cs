using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using JellyLite.Auth;
using JellyLite.Library;
using JellyLite.Storage;

namespace JellyLite.Import;

public sealed record JellyfinUser(string Id, string Name, string? SuggestedUserId);

public sealed record ImportUserResult(string JellyfinUser, string JellyLiteUser, int WithData, int Matched, int Unmatched, List<string> UnmatchedSamples);

/// <summary>One-time (repeatable) import of watch state from a Jellyfin server: played, play count,
/// resume position, favorites and last played, per user. Files are matched on the end of their path,
/// since Jellyfin and JellyLite usually see the NAS under different mount points.</summary>
public sealed class JellyfinImport(IHttpClientFactory http, LibraryService library, Db db, Users users, ILogger<JellyfinImport> log)
{
    public const string HttpClientName = "jellyfin";
    private const int MaxSuffix = 4;

    private HttpClient Client(string url, string apiKey)
    {
        var c = http.CreateClient(HttpClientName);
        c.BaseAddress = new Uri(url.TrimEnd('/') + "/");
        c.Timeout = TimeSpan.FromSeconds(60);
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("MediaBrowser",
            $"Client=\"JellyLite\", Device=\"JellyLite\", DeviceId=\"jellylite-import\", Version=\"1\", Token=\"{apiKey}\"");
        return c;
    }

    /// <summary>Jellyfin's users, each with the JellyLite user of the same name (if any) as suggestion.</summary>
    public async Task<List<JellyfinUser>> UsersAsync(string url, string apiKey, CancellationToken ct)
    {
        using var c = Client(url, apiKey);
        using var doc = JsonDocument.Parse(await c.GetStringAsync("Users", ct));
        return doc.RootElement.EnumerateArray().Select(u =>
        {
            var name = Str(u, "Name") ?? "?";
            var match = users.All.FirstOrDefault(x => x.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
            return new JellyfinUser(Str(u, "Id") ?? "", name, match != null ? Ids.N(match.Id) : null);
        }).Where(u => u.Id.Length > 0).ToList();
    }

    /// <summary>Imports for each mapped user (Jellyfin user id -> JellyLite user id).</summary>
    public async Task<List<ImportUserResult>> ImportAsync(string url, string apiKey, Dictionary<string, string> map, CancellationToken ct)
    {
        var snap = await library.GetAsync();
        var index = BuildIndex(snap);
        var jfUsers = (await UsersAsync(url, apiKey, ct)).ToDictionary(u => u.Id);
        using var c = Client(url, apiKey);
        var results = new List<ImportUserResult>();

        foreach (var (jfId, jlId) in map)
        {
            if (!jfUsers.TryGetValue(jfId, out var jfUser) || Ids.Parse(jlId) is not Guid uid || users.Get(uid) is not { } jlUser) continue;
            var existing = db.UserDataFor(uid);
            var changed = new List<UserData>();
            int withData = 0, matched = 0;
            var unmatched = new List<string>();

            await foreach (var item in ItemsAsync(c, jfId, ct))
            {
                if (!item.TryGetProperty("UserData", out var ud) || !HasData(ud)) continue;
                withData++;
                var path = Str(item, "Path");
                var target = path == null ? null : Find(index, path);
                if (target == null)
                {
                    unmatched.Add(path ?? Str(item, "Name") ?? "?");
                    continue;
                }
                matched++;
                var mine = existing.TryGetValue(target.Id, out var e) ? e : new UserData { ItemId = target.Id };
                Merge(mine, ud);
                existing[target.Id] = mine;
                changed.Add(mine);
            }
            db.Save(uid, changed);
            log.LogInformation("Jellyfin import {From} -> {To}: {Matched} of {Total} matched", jfUser.Name, jlUser.Name, matched, withData);
            results.Add(new ImportUserResult(jfUser.Name, jlUser.Name, withData, matched, unmatched.Count, unmatched.Take(20).ToList()));
        }
        return results;
    }

    private static async IAsyncEnumerable<JsonElement> ItemsAsync(HttpClient c, string jfUserId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        const int page = 500;
        for (var start = 0; ; start += page)
        {
            var url = $"Users/{jfUserId}/Items?Recursive=true&IncludeItemTypes=Movie,Episode,Video,MusicVideo,Series" +
                      $"&Fields=Path&EnableUserData=true&StartIndex={start}&Limit={page}";
            using var doc = JsonDocument.Parse(await c.GetStringAsync(url, ct));
            var items = doc.RootElement.GetProperty("Items").EnumerateArray().Select(i => i.Clone()).ToList();
            foreach (var i in items) yield return i;
            var total = doc.RootElement.TryGetProperty("TotalRecordCount", out var t) ? t.GetInt32() : 0;
            if (items.Count < page || start + page >= total) yield break;
        }
    }

    private static bool HasData(JsonElement ud) =>
        Bool(ud, "Played") || Bool(ud, "IsFavorite") || Long(ud, "PlaybackPositionTicks") > 0 || Long(ud, "PlayCount") > 0;

    /// <summary>Merges so a repeated import never undoes what was watched in JellyLite meanwhile:
    /// played/favorite stay on, play count is the highest, the position comes from whichever was played last.</summary>
    internal static void Merge(UserData mine, JsonElement theirs)
    {
        var theirLast = DateTime.TryParse(Str(theirs, "LastPlayedDate"), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var lp) ? lp : (DateTime?)null;
        // Resume position: from whichever side was played most recently (Jellyfin if JellyLite never played it)
        if (mine.LastPlayed == null || (theirLast != null && theirLast > mine.LastPlayed))
        {
            mine.Position = Long(theirs, "PlaybackPositionTicks");
            mine.LastPlayed = theirLast ?? mine.LastPlayed;
        }
        mine.Played |= Bool(theirs, "Played");
        mine.PlayCount = (int)Math.Max(mine.PlayCount, Long(theirs, "PlayCount"));
        if (mine.Played && mine.PlayCount == 0) mine.PlayCount = 1;
        mine.Favorite |= Bool(theirs, "IsFavorite");
    }

    // ---------- Path matching ----------

    /// <summary>Path suffix (last 1..4 parts, lowercase) -> item; null when a suffix is ambiguous.</summary>
    internal static Dictionary<string, Item?> BuildIndex(LibrarySnapshot snap)
    {
        var index = new Dictionary<string, Item?>();
        foreach (var it in snap.Items.Values)
        {
            if (it.Path == null || it.Type is ItemTypes.Library or ItemTypes.Season) continue;
            var parts = Parts(it.Path);
            for (var n = 1; n <= Math.Min(MaxSuffix, parts.Length); n++)
            {
                var key = string.Join('/', parts[^n..]);
                if (!index.TryGetValue(key, out var other)) index[key] = it;
                else if (other == null || other.Id != it.Id) index[key] = null;   // two items end like this: ambiguous
            }
        }
        return index;
    }

    /// <summary>The longest path ending that points to exactly one item.</summary>
    internal static Item? Find(Dictionary<string, Item?> index, string jellyfinPath)
    {
        var parts = Parts(jellyfinPath);
        for (var n = Math.Min(MaxSuffix, parts.Length); n >= 1; n--)
            if (index.TryGetValue(string.Join('/', parts[^n..]), out var it) && it != null) return it;
        return null;
    }

    private static string[] Parts(string path) =>
        path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(p => p.ToLowerInvariant()).ToArray();

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
}
