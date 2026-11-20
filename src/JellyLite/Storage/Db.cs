using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace JellyLite.Storage;

public sealed class UserData
{
    public Guid ItemId { get; set; }
    public long Position { get; set; }
    public bool Played { get; set; }
    public int PlayCount { get; set; }
    public bool Favorite { get; set; }
    public DateTime? LastPlayed { get; set; }
}

public sealed record DeviceToken(long Id, Guid UserId, string DeviceId, string DeviceName, string Client, DateTime Created);

/// <summary>SQLite storage: sign-ins, per-user watch state, ffprobe cache and "first seen" dates.</summary>
public sealed class Db
{
    private readonly string _cs;
    private readonly ConcurrentDictionary<string, Guid?> _tokenCache = new();

    public Db(AppConfig cfg)
    {
        Directory.CreateDirectory(cfg.DataDir);
        _cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(cfg.DataDir, "jellylite.db") }.ToString();
        using var c = Open();
        Exec(c, "PRAGMA journal_mode=WAL;");
        Exec(c, """
            CREATE TABLE IF NOT EXISTS tokens(id INTEGER PRIMARY KEY, token TEXT UNIQUE NOT NULL, user_id TEXT NOT NULL,
                device_id TEXT NOT NULL, device_name TEXT NOT NULL, client TEXT NOT NULL, created TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS user_data(user_id TEXT NOT NULL, item_id TEXT NOT NULL, position INTEGER NOT NULL DEFAULT 0,
                played INTEGER NOT NULL DEFAULT 0, play_count INTEGER NOT NULL DEFAULT 0, favorite INTEGER NOT NULL DEFAULT 0,
                last_played TEXT, PRIMARY KEY(user_id, item_id));
            CREATE TABLE IF NOT EXISTS probe(path TEXT PRIMARY KEY, size INTEGER NOT NULL, mtime INTEGER NOT NULL, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS first_seen(path TEXT PRIMARY KEY, ts TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS api_keys(id INTEGER PRIMARY KEY, name TEXT NOT NULL, key TEXT UNIQUE NOT NULL, created TEXT NOT NULL);
            """);
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = Cmd(c, sql, args);
        cmd.ExecuteNonQuery();
    }

    private static SqliteCommand Cmd(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return cmd;
    }

    private static string Iso(DateTime d) => d.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
    private static DateTime ParseIso(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    // ---- Tokens ----

    /// <summary>Saves a sign-in. Older sign-ins of the same user on the same device are replaced.</summary>
    public void AddToken(string token, Guid userId, string deviceId, string deviceName, string client)
    {
        using var c = Open();
        Exec(c, "DELETE FROM tokens WHERE user_id=$u AND device_id=$d", ("$u", Ids.N(userId)), ("$d", deviceId));
        Exec(c, "INSERT INTO tokens(token,user_id,device_id,device_name,client,created) VALUES($t,$u,$d,$n,$c,$cr)",
            ("$t", token), ("$u", Ids.N(userId)), ("$d", deviceId), ("$n", deviceName), ("$c", client), ("$cr", Iso(DateTime.UtcNow)));
        _tokenCache.Clear();
    }

    public Guid? UserForToken(string token) => _tokenCache.GetOrAdd(token, t =>
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT user_id FROM tokens WHERE token=$t", ("$t", t));
        return cmd.ExecuteScalar() is string s ? Ids.Parse(s) : null;
    });

    public void RemoveToken(string token)
    {
        using var c = Open();
        Exec(c, "DELETE FROM tokens WHERE token=$t", ("$t", token));
        _tokenCache.Clear();
    }

    public void RemoveTokenById(long id)
    {
        using var c = Open();
        Exec(c, "DELETE FROM tokens WHERE id=$i", ("$i", id));
        _tokenCache.Clear();
    }

    public List<DeviceToken> Tokens()
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT id,user_id,device_id,device_name,client,created FROM tokens ORDER BY created DESC");
        using var r = cmd.ExecuteReader();
        var list = new List<DeviceToken>();
        while (r.Read())
            list.Add(new DeviceToken(r.GetInt64(0), Guid.Parse(r.GetString(1)), r.GetString(2), r.GetString(3), r.GetString(4), ParseIso(r.GetString(5))));
        return list;
    }

    // ---- API keys (for other programs, like Jellyfin's API keys) ----

    private readonly ConcurrentDictionary<string, bool> _apiKeyCache = new();

    public string AddApiKey(string name)
    {
        var key = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        using var c = Open();
        Exec(c, "INSERT INTO api_keys(name,key,created) VALUES($n,$k,$c)", ("$n", name), ("$k", key), ("$c", Iso(DateTime.UtcNow)));
        _apiKeyCache.Clear();
        return key;
    }

    public bool IsApiKey(string key) => _apiKeyCache.GetOrAdd(key, k =>
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT 1 FROM api_keys WHERE key=$k", ("$k", k));
        return cmd.ExecuteScalar() != null;
    });

    public List<(long Id, string Name, string Key, DateTime Created)> ApiKeys()
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT id,name,key,created FROM api_keys ORDER BY created");
        using var r = cmd.ExecuteReader();
        var list = new List<(long, string, string, DateTime)>();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), ParseIso(r.GetString(3))));
        return list;
    }

    public void RemoveApiKey(long id)
    {
        using var c = Open();
        Exec(c, "DELETE FROM api_keys WHERE id=$i", ("$i", id));
        _apiKeyCache.Clear();
    }

    // ---- User data ----

    public Dictionary<Guid, UserData> UserDataFor(Guid userId)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT item_id,position,played,play_count,favorite,last_played FROM user_data WHERE user_id=$u", ("$u", Ids.N(userId)));
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<Guid, UserData>();
        while (r.Read())
        {
            var d = new UserData
            {
                ItemId = Guid.Parse(r.GetString(0)),
                Position = r.GetInt64(1),
                Played = r.GetInt64(2) != 0,
                PlayCount = (int)r.GetInt64(3),
                Favorite = r.GetInt64(4) != 0,
                LastPlayed = r.IsDBNull(5) ? null : ParseIso(r.GetString(5)),
            };
            map[d.ItemId] = d;
        }
        return map;
    }

    public UserData Get(Guid userId, Guid itemId) =>
        UserDataFor(userId).TryGetValue(itemId, out var d) ? d : new UserData { ItemId = itemId };

    public void Save(Guid userId, IEnumerable<UserData> items)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var d in items)
        {
            using var cmd = Cmd(c, """
                INSERT INTO user_data(user_id,item_id,position,played,play_count,favorite,last_played) VALUES($u,$i,$p,$pl,$pc,$f,$lp)
                ON CONFLICT(user_id,item_id) DO UPDATE SET position=$p, played=$pl, play_count=$pc, favorite=$f, last_played=$lp
                """,
                ("$u", Ids.N(userId)), ("$i", Ids.N(d.ItemId)), ("$p", d.Position), ("$pl", d.Played ? 1 : 0),
                ("$pc", d.PlayCount), ("$f", d.Favorite ? 1 : 0), ("$lp", d.LastPlayed is { } lp ? Iso(lp) : null));
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // ---- ffprobe cache ----

    public string? GetProbe(string path, long size, long mtime)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT json FROM probe WHERE path=$p AND size=$s AND mtime=$m", ("$p", path), ("$s", size), ("$m", mtime));
        return cmd.ExecuteScalar() as string;
    }

    public void PutProbe(string path, long size, long mtime, string json)
    {
        using var c = Open();
        Exec(c, "INSERT OR REPLACE INTO probe(path,size,mtime,json) VALUES($p,$s,$m,$j)", ("$p", path), ("$s", size), ("$m", mtime), ("$j", json));
    }

    // ---- First seen ("date added") ----

    public Dictionary<string, DateTime> FirstSeen()
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT path, ts FROM first_seen");
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, DateTime>();
        while (r.Read()) map[r.GetString(0)] = ParseIso(r.GetString(1));
        return map;
    }

    public void AddFirstSeen(IEnumerable<KeyValuePair<string, DateTime>> entries)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var (path, ts) in entries)
        {
            using var cmd = Cmd(c, "INSERT OR IGNORE INTO first_seen(path, ts) VALUES($p,$t)", ("$p", path), ("$t", Iso(ts)));
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
