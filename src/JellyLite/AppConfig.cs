using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JellyLite;

/// <summary>
/// Server configuration. Read from (first found):
/// $JELLYLITE_CONFIG, {data}/config.json, {data}/options.json (Home Assistant add-on).
/// The data folder is $JELLYLITE_DATA, or ./data.
/// </summary>
public sealed class AppConfig
{
    public string ServerName { get; set; } = "JellyLite";
    public int Port { get; set; } = 8096;
    public string DataDir { get; set; } = "";
    public List<LibraryConfig> Libraries { get; set; } = [];
    public List<UserConfig> Users { get; set; } = [];
    /// <summary>Path to ffprobe. Empty = don't probe (no audio/subtitle track info).</summary>
    public string Ffprobe { get; set; } = "ffprobe";
    /// <summary>Path to ffmpeg, only used to extract embedded text subtitles. Empty = off.</summary>
    public string Ffmpeg { get; set; } = "ffmpeg";
    public int ScanIntervalMinutes { get; set; } = 15;
    public int ProbeParallelism { get; set; } = 2;
    /// <summary>Password for the admin page. Without one it's only reachable from localhost and Home Assistant ingress.</summary>
    public string? AdminPassword { get; set; }
    /// <summary>Season name prefix, e.g. "Sæson" -> "Sæson 1".</summary>
    public string SeasonLabel { get; set; } = "Sæson";
    public string SpecialsLabel { get; set; } = "Specials";

    [JsonIgnore] public string? LoadedFrom { get; set; }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public static AppConfig Load()
    {
        var dataDir = Environment.GetEnvironmentVariable("JELLYLITE_DATA");
        if (string.IsNullOrWhiteSpace(dataDir)) dataDir = Path.Combine(Directory.GetCurrentDirectory(), "data");

        var candidates = new List<string>();
        var env = Environment.GetEnvironmentVariable("JELLYLITE_CONFIG");
        if (!string.IsNullOrWhiteSpace(env)) candidates.Add(env);
        candidates.Add(Path.Combine(dataDir, "config.json"));
        candidates.Add(Path.Combine(dataDir, "options.json"));

        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Json) ?? new AppConfig();
            cfg.LoadedFrom = path;
            if (string.IsNullOrWhiteSpace(cfg.DataDir)) cfg.DataDir = dataDir;
            return cfg;
        }
        return new AppConfig { DataDir = dataDir };
    }

    /// <summary>Writes an example config.json so a first run on Windows/Linux has something to edit.</summary>
    public void WriteExample()
    {
        Directory.CreateDirectory(DataDir);
        var path = Path.Combine(DataDir, "config.json");
        var example = new AppConfig
        {
            Libraries =
            [
                new() { Name = "Film", Path = "/media/Film", Type = "movies" },
                new() { Name = "Serier", Path = "/media/Serier", Type = "tvshows" },
                new() { Name = "YouTube", Path = "/media/YouTube", Type = "youtube" },
            ],
            Users = [new() { Name = "Stue" }],
        };
        File.WriteAllText(path, JsonSerializer.Serialize(example, Json));
        LoadedFrom = path;
    }

    public string ServerId => Ids.N(Ids.From("server:" + ServerName));
}

public sealed class LibraryConfig
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    /// <summary>movies | tvshows | youtube</summary>
    public string Type { get; set; } = "movies";
}

public sealed class UserConfig
{
    public string Name { get; set; } = "";
}

public static class Ids
{
    /// <summary>Stable id from a key (same file -> same id across rescans and restarts).</summary>
    public static Guid From(string key) => new(MD5.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>Jellyfin's id format: 32 hex chars without dashes.</summary>
    public static string N(Guid g) => g.ToString("N");

    public static Guid? Parse(string? s) => Guid.TryParse(s, out var g) ? g : null;

    public static string Hash(string s) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s)))[..16].ToLowerInvariant();
}
