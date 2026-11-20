using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace JellyLite.Library;

/// <summary>Data from a Kodi/Jellyfin .nfo file.</summary>
public sealed class NfoData
{
    public string? Title, OriginalTitle, SortTitle, ShowTitle, Plot, Tagline, Mpaa;
    public int? Year, Season, Episode, EpisodeEnd, RuntimeMinutes;
    public DateTime? Premiered;
    public float? Rating;
    public List<string> Genres = [], Studios = [];
    public List<RawPerson> People = [];
    public Dictionary<string, string> Ids = new(StringComparer.OrdinalIgnoreCase);
}

public static partial class Nfo
{
    [GeneratedRegex(@"themoviedb\.org/person/(\d+)")]
    private static partial Regex TmdbProfile();

    public static NfoData? Load(string? path)
    {
        if (path == null) return null;
        try
        {
            var text = File.ReadAllText(path);
            // Some .nfo files have a URL line before/after the XML: keep only the XML part.
            int start = text.IndexOf('<'), end = text.LastIndexOf('>');
            if (start < 0 || end <= start) return null;
            var root = XDocument.Parse(text[start..(end + 1)]).Root;
            return root == null ? null : Parse(root);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static NfoData Parse(XElement root)
    {
        var d = new NfoData
        {
            Title = Str(root, "title"),
            OriginalTitle = Str(root, "originaltitle"),
            SortTitle = Str(root, "sorttitle"),
            ShowTitle = Str(root, "showtitle"),
            Plot = Str(root, "plot") ?? Str(root, "outline"),
            Tagline = Str(root, "tagline"),
            Mpaa = Str(root, "mpaa"),
            Year = Int(Str(root, "year")),
            Season = Int(Str(root, "season")),
            Episode = Int(Str(root, "episode")),
            EpisodeEnd = Int(Str(root, "episodenumberend")),
            RuntimeMinutes = Int(Str(root, "runtime")),
            Premiered = Date(Str(root, "premiered") ?? Str(root, "aired") ?? Str(root, "releasedate")),
            Rating = Float(Str(root, "rating")),
        };
        if (d.Rating == null)
        {
            var ratings = root.Element("ratings")?.Elements("rating").ToList() ?? [];
            var r = ratings.FirstOrDefault(x => (string?)x.Attribute("default") == "true") ?? ratings.FirstOrDefault();
            d.Rating = Float(r?.Element("value")?.Value);
        }
        d.Year ??= d.Premiered?.Year;
        d.Genres = Many(root, "genre");
        d.Studios = Many(root, "studio");

        foreach (var u in root.Elements("uniqueid"))
        {
            var type = (string?)u.Attribute("type");
            if (!string.IsNullOrWhiteSpace(type) && !string.IsNullOrWhiteSpace(u.Value)) d.Ids[type.Trim()] = u.Value.Trim();
        }
        foreach (var (tag, key) in new[] { ("tmdbid", "Tmdb"), ("imdbid", "Imdb"), ("imdb_id", "Imdb"), ("tvdbid", "Tvdb") })
            if (Str(root, tag) is { } v) d.Ids.TryAdd(key, v);
        NormalizeIds(d.Ids);

        foreach (var a in root.Elements("actor"))
        {
            var name = Str(a, "name");
            if (name == null) continue;
            var type = Str(a, "type") ?? "Actor";
            var tmdb = Str(a, "tmdbid");
            if (tmdb == null && Str(a, "profile") is { } profile && TmdbProfile().Match(profile) is { Success: true } pm) tmdb = pm.Groups[1].Value;
            d.People.Add(new RawPerson(name, Str(a, "role"), type, tmdb, Str(a, "imdbid"), Str(a, "thumb")));
        }
        foreach (var dir in root.Elements("director"))
            if (!string.IsNullOrWhiteSpace(dir.Value))
                d.People.Add(new RawPerson(dir.Value.Trim(), null, "Director", (string?)dir.Attribute("tmdbid"), null, null));
        foreach (var w in root.Elements("credits"))
            if (!string.IsNullOrWhiteSpace(w.Value))
                d.People.Add(new RawPerson(w.Value.Trim(), null, "Writer", (string?)w.Attribute("tmdbid"), null, null));
        return d;
    }

    /// <summary>"tmdb"/"TMDB"/"themoviedb" -> "Tmdb" (Jellyfin's ProviderIds keys).</summary>
    private static void NormalizeIds(Dictionary<string, string> ids)
    {
        foreach (var (k, v) in ids.ToList())
        {
            var key = k.ToLowerInvariant() switch
            {
                "tmdb" or "themoviedb" => "Tmdb",
                "imdb" => "Imdb",
                "tvdb" => "Tvdb",
                "youtube" => "YouTube",
                _ => null,
            };
            if (key == null || key == k) continue;
            ids.Remove(k);
            ids.TryAdd(key, v);
        }
    }

    private static string? Str(XElement e, string name)
    {
        var v = e.Element(name)?.Value?.Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }

    private static List<string> Many(XElement e, string name) =>
        e.Elements(name).SelectMany(x => x.Value.Split(" / ")).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();

    internal static int? Int(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    internal static float? Float(string? s) =>
        float.TryParse(s?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;

    internal static DateTime? Date(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        string[] formats = ["yyyy-MM-dd", "yyyyMMdd", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy"];
        return DateTime.TryParseExact(s.Trim(), formats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
    }
}

/// <summary>Data from a yt-dlp .info.json file.</summary>
public sealed class VideoInfo
{
    public string? Id, Title, Description, Channel, ChannelId;
    public DateTime? UploadDate;
    public double? Duration;
    public List<string> Categories = [];

    public static VideoInfo? Load(string? path)
    {
        if (path == null) return null;
        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            var r = doc.RootElement;
            return new VideoInfo
            {
                Id = S(r, "id"),
                Title = S(r, "title") ?? S(r, "fulltitle"),
                Description = S(r, "description"),
                Channel = S(r, "channel") ?? S(r, "uploader"),
                ChannelId = S(r, "channel_id") ?? S(r, "uploader_id"),
                UploadDate = Nfo.Date(S(r, "upload_date")),
                Duration = r.TryGetProperty("duration", out var du) && du.ValueKind == JsonValueKind.Number ? du.GetDouble() : null,
                Categories = r.TryGetProperty("categories", out var c) && c.ValueKind == JsonValueKind.Array
                    ? c.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                    : [],
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? S(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;
}
