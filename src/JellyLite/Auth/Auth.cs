using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using JellyLite.Storage;

namespace JellyLite.Auth;

public sealed record UserInfo(Guid Id, string Name);

public sealed class Users(AppConfig cfg)
{
    public IReadOnlyList<UserInfo> All => cfg.Users
        .Where(u => !string.IsNullOrWhiteSpace(u.Name))
        .Select(u => new UserInfo(Ids.From("user:" + u.Name.Trim().ToLowerInvariant()), u.Name.Trim()))
        .ToList();

    public UserInfo? Get(Guid id) => All.FirstOrDefault(u => u.Id == id);

    /// <summary>Optional user picture: {data}/users/{name}.jpg|png</summary>
    public string? ImagePath(UserInfo u)
    {
        foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".webp" })
        {
            var p = Path.Combine(cfg.DataDir, "users", u.Name + ext);
            if (File.Exists(p)) return p;
        }
        return null;
    }
}

/// <summary>Client info from the MediaBrowser Authorization header (or X-Emby-* headers / api_key).</summary>
public sealed partial record ClientInfo(string Client, string Device, string DeviceId, string Version, string? Token)
{
    [GeneratedRegex(@"(\w+)=""([^""]*)""")]
    private static partial Regex Pair();

    public static ClientInfo From(HttpRequest req)
    {
        var header = req.Headers.Authorization.FirstOrDefault() ?? req.Headers["X-Emby-Authorization"].FirstOrDefault() ?? "";
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Pair().Matches(header)) values[m.Groups[1].Value] = WebUtility.UrlDecode(m.Groups[2].Value);
        var token = values.GetValueOrDefault("Token")
            ?? req.Headers["X-Emby-Token"].FirstOrDefault()
            ?? req.Headers["X-MediaBrowser-Token"].FirstOrDefault()
            ?? req.Query["api_key"].FirstOrDefault()
            ?? req.Query["ApiKey"].FirstOrDefault();
        return new ClientInfo(
            values.GetValueOrDefault("Client") ?? "Unknown",
            values.GetValueOrDefault("Device") ?? "Unknown",
            values.GetValueOrDefault("DeviceId") ?? "unknown",
            values.GetValueOrDefault("Version") ?? "",
            string.IsNullOrWhiteSpace(token) ? null : token);
    }
}

public sealed class QuickConnectRequest
{
    public required string Secret { get; init; }
    public required string Code { get; init; }
    public required ClientInfo Client { get; init; }
    public DateTime Created { get; } = DateTime.UtcNow;
    public Guid? UserId { get; set; }
    public bool Authenticated => UserId != null;
}

/// <summary>Quick Connect: the TV shows a code, someone approves it on the admin page.</summary>
public sealed class QuickConnectStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, QuickConnectRequest> _bySecret = new();

    public QuickConnectRequest Initiate(ClientInfo client)
    {
        Cleanup();
        string code;
        do code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        while (_bySecret.Values.Any(r => r.Code == code));
        var req = new QuickConnectRequest { Secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(), Code = code, Client = client };
        _bySecret[req.Secret] = req;
        return req;
    }

    public QuickConnectRequest? Get(string secret)
    {
        Cleanup();
        return _bySecret.GetValueOrDefault(secret);
    }

    public IReadOnlyList<QuickConnectRequest> Pending
    {
        get
        {
            Cleanup();
            return _bySecret.Values.Where(r => !r.Authenticated).OrderBy(r => r.Created).ToList();
        }
    }

    public bool Authorize(string code, Guid userId)
    {
        var req = Pending.FirstOrDefault(r => r.Code == code.Trim());
        if (req == null) return false;
        req.UserId = userId;
        return true;
    }

    public QuickConnectRequest? Consume(string secret) => _bySecret.TryRemove(secret, out var r) && r.Authenticated ? r : null;

    private void Cleanup()
    {
        foreach (var (k, r) in _bySecret)
            if (DateTime.UtcNow - r.Created > Lifetime) _bySecret.TryRemove(k, out _);
    }
}

/// <summary>Resolves the user from the token and rejects unauthenticated calls to non-public endpoints.</summary>
public sealed partial class AuthMiddleware(RequestDelegate next)
{
    [GeneratedRegex(@"^/(?:System/Info/Public|System/Ping|QuickConnect/|Users/AuthenticateWithQuickConnect|Users/AuthenticateByName|Users/Public|Branding/|admin(?:/|$)|favicon\.ico|web(?:/|$)|$)", RegexOptions.IgnoreCase)]
    private static partial Regex PublicPath();

    // Images are public in Jellyfin too (the app loads them in <img> tags)
    [GeneratedRegex(@"^/(?:Items|Users)/[^/]+/Images/", RegexOptions.IgnoreCase)]
    private static partial Regex ImagePath();

    public async Task Invoke(HttpContext ctx, Db db, Users users)
    {
        var client = ClientInfo.From(ctx.Request);
        ctx.Items["client"] = client;
        if (client.Token != null && db.UserForToken(client.Token) is Guid uid && users.Get(uid) is { } user)
            ctx.Items["user"] = user;

        var path = ctx.Request.Path.Value ?? "/";
        if (ctx.Items.ContainsKey("user") || HttpMethods.IsOptions(ctx.Request.Method) || PublicPath().IsMatch(path) || ImagePath().IsMatch(path))
        {
            await next(ctx);
            return;
        }
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
    }

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>Admin page: allowed from Home Assistant ingress, localhost, or with the admin password (Basic auth).</summary>
    public static bool IsAdmin(HttpContext ctx, AppConfig cfg)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip != null && ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ctx.Request.Headers.ContainsKey("X-Ingress-Path") && ip?.ToString() == "172.30.32.2") return true;
        if (!string.IsNullOrEmpty(cfg.AdminPassword))
        {
            var h = ctx.Request.Headers.Authorization.FirstOrDefault();
            if (h != null && h.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(h[6..].Trim()));
                    var pw = decoded[(decoded.IndexOf(':') + 1)..];
                    if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(pw), Encoding.UTF8.GetBytes(cfg.AdminPassword))) return true;
                }
                catch (FormatException) { /* bad header */ }
            }
        }
        return ip == null || IPAddress.IsLoopback(ip);
    }
}

public static class HttpContextExtensions
{
    public static UserInfo User(this HttpContext c) => (UserInfo)c.Items["user"]!;
    public static ClientInfo Client(this HttpContext c) => (ClientInfo)c.Items["client"]!;
}
