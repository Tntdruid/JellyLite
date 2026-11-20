using System.Collections.Concurrent;
using JellyLite.Auth;

namespace JellyLite.Api;

public sealed class PlaySession
{
    public required string Id { get; init; }
    public required UserInfo User { get; init; }
    public required ClientInfo Client { get; init; }
    public Guid ItemId { get; set; }
    public long PositionTicks { get; set; }
    public bool IsPaused { get; set; }
    public string PlayMethod { get; set; } = "DirectPlay";
    public int? AudioStreamIndex { get; set; }
    public int? SubtitleStreamIndex { get; set; }
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
}

/// <summary>Who is watching what right now, built from the clients' playback reports (start/progress/stopped).
/// A session without a report for a minute is dropped, since a TV switched off never says "stopped".</summary>
public sealed class SessionTracker
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<string, PlaySession> _sessions = new();

    /// <summary>One session per signed-in device (like Jellyfin), so the id is stable between items.</summary>
    public static string SessionId(UserInfo user, ClientInfo client) => Ids.N(Ids.From($"session:{user.Id}:{client.DeviceId}"));

    public void Report(UserInfo user, ClientInfo client, Guid itemId, long? position, bool? paused, string? method, int? audio, int? sub, DateTime now)
    {
        var id = SessionId(user, client);
        var s = _sessions.AddOrUpdate(id,
            _ => new PlaySession { Id = id, User = user, Client = client, ItemId = itemId },
            (_, old) => old.ItemId == itemId ? old : new PlaySession { Id = id, User = user, Client = client, ItemId = itemId });
        if (position is long p) s.PositionTicks = p;
        if (paused is bool b) s.IsPaused = b;
        if (!string.IsNullOrEmpty(method)) s.PlayMethod = method;
        if (audio != null) s.AudioStreamIndex = audio;
        if (sub != null) s.SubtitleStreamIndex = sub;
        s.LastActivity = now;
    }

    public void Stop(UserInfo user, ClientInfo client) => _sessions.TryRemove(SessionId(user, client), out _);

    public List<PlaySession> Active(DateTime now)
    {
        foreach (var (k, s) in _sessions)
            if (now - s.LastActivity > Timeout) _sessions.TryRemove(k, out _);
        return _sessions.Values.OrderBy(s => s.User.Name).ToList();
    }
}
