using System.Text.Json;
using JellyLite.Auth;
using JellyLite.Storage;
using JellyLite.Library;
using JellyLite.Media;

namespace JellyLite.Api;

/// <summary>The Jellyfin API subset TvFin uses. Old and new (10.9+) paths are both mapped.</summary>
public static class Endpoints
{
    public const string ServerVersion = "10.10.7";

    private static async Task<DtoContext> Ctx(HttpContext h)
    {
        var lib = await h.RequestServices.GetRequiredService<LibraryService>().GetAsync();
        var db = h.RequestServices.GetRequiredService<Db>();
        var cfg = h.RequestServices.GetRequiredService<AppConfig>();
        return new DtoContext(lib, db.UserDataFor(h.User().Id), cfg.ServerId, h.Client().Token);
    }

    private static void Get(WebApplication app, Delegate handler, params string[] patterns)
    {
        foreach (var p in patterns) app.MapMethods(p, ["GET", "HEAD"], handler);
    }

    private static void Post(WebApplication app, Delegate handler, params string[] patterns)
    {
        foreach (var p in patterns) app.MapPost(p, handler);
    }

    private static async Task<JsonElement?> Body(HttpRequest req)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Prop(JsonElement? e, string name)
    {
        if (e is not { ValueKind: JsonValueKind.Object } o) return null;
        foreach (var p in o.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return p.Value.ValueKind switch { JsonValueKind.String => p.Value.GetString(), JsonValueKind.Number => p.Value.GetRawText(), _ => null };
        return null;
    }

    public static void Map(WebApplication app)
    {
        MapSystem(app);
        MapAuth(app);
        MapBrowse(app);
        MapImages(app);
        MapPlayback(app);
    }

    // ---------- System ----------

    private static void MapSystem(WebApplication app)
    {
        object Info(HttpContext h)
        {
            var cfg = h.RequestServices.GetRequiredService<AppConfig>();
            return new Dictionary<string, object?>
            {
                ["LocalAddress"] = $"{h.Request.Scheme}://{h.Request.Host}",
                ["ServerName"] = cfg.ServerName,
                ["Version"] = ServerVersion,
                ["ProductName"] = "JellyLite",
                ["OperatingSystem"] = Environment.OSVersion.Platform.ToString(),
                ["Id"] = cfg.ServerId,
                ["StartupWizardCompleted"] = true,
            };
        }
        Get(app, Info, "/System/Info/Public", "/System/Info");
        app.MapMethods("/System/Ping", ["GET", "POST"], () => "JellyLite");
        Get(app, () => new { LoginDisclaimer = "", CustomCss = "", SplashscreenEnabled = false }, "/Branding/Configuration");
    }

    // ---------- Sign-in (Quick Connect only) ----------

    private static object QcDto(QuickConnectRequest r) => new Dictionary<string, object?>
    {
        ["Authenticated"] = r.Authenticated,
        ["Secret"] = r.Secret,
        ["Code"] = r.Code,
        ["DeviceId"] = r.Client.DeviceId,
        ["DeviceName"] = r.Client.Device,
        ["AppName"] = r.Client.Client,
        ["AppVersion"] = r.Client.Version,
        ["DateAdded"] = r.Created,
    };

    private static string? UserImageTag(Users users, UserInfo u)
    {
        var p = users.ImagePath(u);
        return p == null ? null : Ids.Hash(p + File.GetLastWriteTimeUtc(p).Ticks);
    }

    private static void MapAuth(WebApplication app)
    {
        Get(app, () => true, "/QuickConnect/Enabled");
        app.MapMethods("/QuickConnect/Initiate", ["GET", "POST"], (HttpContext h, QuickConnectStore qc) => Results.Json(QcDto(qc.Initiate(h.Client()))));
        Get(app, (string secret, QuickConnectStore qc) => qc.Get(secret) is { } r ? Results.Json(QcDto(r)) : Results.NotFound(), "/QuickConnect/Connect");

        app.MapPost("/Users/AuthenticateWithQuickConnect", async (HttpContext h, QuickConnectStore qc, Users users, Db db, AppConfig cfg) =>
        {
            var secret = Prop(await Body(h.Request), "Secret");
            var req = secret == null ? null : qc.Consume(secret);
            if (req?.UserId is not Guid uid || users.Get(uid) is not { } user) return Results.Unauthorized();
            var client = h.Client();
            var token = AuthMiddleware.NewToken();
            db.AddToken(token, user.Id, client.DeviceId, client.Device, client.Client);
            return Results.Json(new Dictionary<string, object?>
            {
                ["User"] = Dto.User(user, cfg.ServerId, UserImageTag(users, user)),
                ["SessionInfo"] = new Dictionary<string, object?>
                {
                    ["Id"] = Ids.N(Ids.From("session:" + token)),
                    ["UserId"] = Ids.N(user.Id),
                    ["UserName"] = user.Name,
                    ["Client"] = client.Client,
                    ["DeviceName"] = client.Device,
                    ["DeviceId"] = client.DeviceId,
                    ["ServerId"] = cfg.ServerId,
                },
                ["AccessToken"] = token,
                ["ServerId"] = cfg.ServerId,
            });
        });
        app.MapPost("/Users/AuthenticateByName", () => Results.Json(new { Message = "Brug Quick Connect" }, statusCode: 401));
        Get(app, () => Array.Empty<object>(), "/Users/Public");

        Get(app, (HttpContext h, Users users, AppConfig cfg) => Dto.User(h.User(), cfg.ServerId, UserImageTag(users, h.User())), "/Users/Me");
        Get(app, (Guid id, HttpContext h, Users users, AppConfig cfg) =>
            users.Get(id) is { } u ? Results.Json(Dto.User(u, cfg.ServerId, UserImageTag(users, u))) : Results.NotFound(), "/Users/{id:guid}");

        app.MapPost("/Sessions/Logout", (HttpContext h, Db db) =>
        {
            if (h.Client().Token is { } t) db.RemoveToken(t);
            return Results.NoContent();
        });
        foreach (var p in new[] { "/Sessions/Capabilities", "/Sessions/Capabilities/Full", "/Sessions/Playing/Ping" })
            app.MapPost(p, () => Results.NoContent());
    }

    // ---------- Browsing ----------

    private static void MapBrowse(WebApplication app)
    {
        Get(app, async (HttpContext h) =>
        {
            var c = await Ctx(h);
            return Dto.Wrap(c.Lib.Libraries.Select(l => (object)Dto.Item(l, c)));
        }, "/UserViews", "/Users/{userId}/Views");

        Get(app, async (HttpContext h) =>
        {
            var c = await Ctx(h);
            var (items, total, start) = ItemQuery.Run(h.Request.Query, c);
            return Dto.Wrap(items.Select(i => (object)Dto.Item(i, c)), total, start);
        }, "/Items", "/Users/{userId}/Items");

        Get(app, async (Guid id, HttpContext h) =>
        {
            var c = await Ctx(h);
            if (c.Lib.Get(id) is { } it)
            {
                if (it.IsPlayable) await h.RequestServices.GetRequiredService<LibraryService>().EnsureProbedAsync(it, h.RequestAborted);
                return Results.Json(Dto.Item(it, c, full: true));
            }
            if (c.Lib.People.TryGetValue(id, out var person)) return Results.Json(Dto.Person(person, c));
            if (c.Lib.Studios.TryGetValue(id, out var studio)) return Results.Json(Dto.Studio(studio, c));
            return Results.NotFound();
        }, "/Items/{id:guid}", "/Users/{userId}/Items/{id:guid}");

        // Newest in a library. Episodes are grouped into their series.
        Get(app, async (HttpContext h) =>
        {
            var c = await Ctx(h);
            var q = h.Request.Query;
            var limit = int.TryParse(q["Limit"], out var l) && l > 0 ? l : 20;
            var parent = Ids.Parse(q["ParentId"]) is Guid pid ? c.Lib.Get(pid) : null;
            var src = parent != null ? c.Lib.Descendants(parent) : c.Lib.Items.Values;
            var result = new List<Item>();
            var seen = new HashSet<Guid>();
            foreach (var it in src.Where(i => i.IsPlayable).OrderByDescending(i => i.DateCreated))
            {
                var shown = it.Type == ItemTypes.Episode && it.SeriesId is Guid sid && c.Lib.Get(sid) is { } series ? series : it;
                if (seen.Add(shown.Id)) result.Add(shown);
                if (result.Count >= limit) break;
            }
            return result.Select(i => Dto.Item(i, c)).ToList();
        }, "/Items/Latest", "/Users/{userId}/Items/Latest");

        Get(app, async (HttpContext h) =>
        {
            var c = await Ctx(h);
            var q = h.Request.Query;
            var limit = int.TryParse(q["Limit"], out var l) && l > 0 ? l : 24;
            var parent = Ids.Parse(q["ParentId"]) is Guid pid ? c.Lib.Get(pid) : null;
            var src = parent != null ? c.Lib.Descendants(parent) : c.Lib.Items.Values;
            var items = src.Where(i => i.IsPlayable && c.Ud(i.Id) is { Position: > 0, Played: false })
                .OrderByDescending(i => c.Ud(i.Id).LastPlayed).Take(limit).ToList();
            return Dto.Wrap(items.Select(i => (object)Dto.Item(i, c)));
        }, "/UserItems/Resume", "/Users/{userId}/Items/Resume");

        Get(app, async (HttpContext h) =>
        {
            var c = await Ctx(h);
            var q = h.Request.Query;
            var limit = int.TryParse(q["Limit"], out var l) && l > 0 ? l : 24;
            var onlySeries = Ids.Parse(q["SeriesId"]);
            var next = new List<(Item Episode, DateTime Activity)>();
            foreach (var series in c.Lib.Items.Values.Where(i => i.Type == ItemTypes.Series && (onlySeries == null || i.Id == onlySeries)))
            {
                var eps = c.Lib.EpisodesOf(series).Where(e => e.ParentIndexNumber != 0).ToList();
                var last = eps.FindLastIndex(e => c.Ud(e.Id).Played);
                if (last < 0) continue;
                var ep = eps.Skip(last + 1).FirstOrDefault(e => !c.Ud(e.Id).Played);
                if (ep == null) continue;
                next.Add((ep, eps.Max(e => c.Ud(e.Id).LastPlayed) ?? DateTime.MinValue));
            }
            var list = next.OrderByDescending(n => n.Activity).Take(limit).Select(n => (object)Dto.Item(n.Episode, c));
            return Dto.Wrap(list);
        }, "/Shows/NextUp");

        Get(app, async (Guid id, HttpContext h) =>
        {
            var c = await Ctx(h);
            if (c.Lib.Get(id) is not { Type: ItemTypes.Series } series) return Results.NotFound();
            var q = h.Request.Query;
            IEnumerable<Item> eps = c.Lib.EpisodesOf(series);
            if (Ids.Parse(q["SeasonId"]) is Guid seasonId) eps = eps.Where(e => e.SeasonId == seasonId);
            if (int.TryParse(q["Season"], out var sn)) eps = eps.Where(e => e.ParentIndexNumber == sn);
            if (Ids.Parse(q["StartItemId"]) is Guid startId) eps = eps.SkipWhile(e => e.Id != startId);
            var list = eps.ToList();
            var start = int.TryParse(q["StartIndex"], out var si) && si > 0 ? si : 0;
            var limit = int.TryParse(q["Limit"], out var l) && l > 0 ? l : int.MaxValue;
            return Results.Json(Dto.Wrap(list.Skip(start).Take(limit).Select(e => (object)Dto.Item(e, c)), list.Count, start));
        }, "/Shows/{id:guid}/Episodes");

        Get(app, async (Guid id, HttpContext h) =>
        {
            var c = await Ctx(h);
            if (c.Lib.Get(id) is not { Type: ItemTypes.Series } series) return Results.NotFound();
            return Results.Json(Dto.Wrap(c.Lib.ChildrenOf(series).Select(s => (object)Dto.Item(s, c))));
        }, "/Shows/{id:guid}/Seasons");

        // "More like this": shared genres, studios (YouTube channel) and people
        Get(app, async (Guid id, HttpContext h) =>
        {
            var c = await Ctx(h);
            if (c.Lib.Get(id) is not { } target) return Results.NotFound();
            var limit = int.TryParse(h.Request.Query["Limit"], out var l) && l > 0 ? l : 12;
            var genres = target.Genres.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var studios = target.Studios.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var people = target.People.Take(10).Select(p => p.Id).ToHashSet();
            var list = c.Lib.Items.Values
                .Where(i => i.Type == target.Type && i.Id != target.Id)
                .Select(i => (Item: i, Score: 3 * i.Genres.Count(genres.Contains) + 2 * i.Studios.Count(studios.Contains) + i.People.Count(p => people.Contains(p.Id))))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score).ThenByDescending(x => x.Item.CommunityRating ?? 0)
                .Take(limit).Select(x => (object)Dto.Item(x.Item, c));
            return Results.Json(Dto.Wrap(list));
        }, "/Items/{id:guid}/Similar", "/Movies/{id:guid}/Similar", "/Shows/{id:guid}/Similar");

        // Studios = YouTube channels
        Get(app, async (HttpContext h) =>
        {
            var c = await Ctx(h);
            var parent = Ids.Parse(h.Request.Query["ParentId"]) is Guid pid ? c.Lib.Get(pid) : null;
            var src = parent != null ? c.Lib.Descendants(parent) : c.Lib.Items.Values;
            var names = src.SelectMany(i => i.Studios).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var studios = c.Lib.Studios.Values.Where(s => names.Contains(s.Name)).OrderBy(s => s.Name, NaturalComparer.Instance);
            return Dto.Wrap(studios.Select(s => (object)Dto.Studio(s, c)));
        }, "/Studios");
    }

    // ---------- Images ----------

    private static void MapImages(WebApplication app)
    {
        async Task<IResult> Image(Guid id, string type, int? index, HttpContext h)
        {
            var lib = await h.RequestServices.GetRequiredService<LibraryService>().GetAsync();
            ImageRef? img = null;
            if (lib.Get(id) is { IsPlayable: true } video && type.Equals("Chapter", StringComparison.OrdinalIgnoreCase))
            {
                await h.RequestServices.GetRequiredService<LibraryService>().EnsureProbedAsync(video, h.RequestAborted);
                var frame = await h.RequestServices.GetRequiredService<ChapterImages>().GetAsync(video, index ?? 0, h.RequestAborted);
                return frame == null ? Results.NotFound() : ImageFile(h, frame);
            }
            if (lib.Get(id) is { } it)
                img = type.Equals("Backdrop", StringComparison.OrdinalIgnoreCase) ? it.Backdrops.ElementAtOrDefault(index ?? 0) : it.Images.GetValueOrDefault(type);
            else if (lib.People.TryGetValue(id, out var p) && type.Equals("Primary", StringComparison.OrdinalIgnoreCase))
                img = p.Image;
            else if (lib.Studios.TryGetValue(id, out var s) && (type.Equals("Primary", StringComparison.OrdinalIgnoreCase) || type.Equals("Thumb", StringComparison.OrdinalIgnoreCase)))
                img = s.Image;
            if (img == null) return Results.NotFound();
            var path = await h.RequestServices.GetRequiredService<RemoteImages>().GetAsync(img, h.RequestAborted);
            return path == null ? Results.NotFound() : ImageFile(h, path);
        }
        Get(app, (Guid id, string type, HttpContext h) => Image(id, type, null, h), "/Items/{id:guid}/Images/{type}");
        Get(app, (Guid id, string type, int index, HttpContext h) => Image(id, type, index, h), "/Items/{id:guid}/Images/{type}/{index:int}");

        Get(app, (Guid id, Users users, HttpContext h) =>
            users.Get(id) is { } u && users.ImagePath(u) is { } p ? ImageFile(h, p) : Results.NotFound(), "/Users/{id:guid}/Images/{type}");
    }

    private static IResult ImageFile(HttpContext h, string path)
    {
        // The app always sends the tag, so the URL changes when the image changes: cache hard
        h.Response.Headers.CacheControl = h.Request.Query.ContainsKey("tag") ? "public, max-age=31536000, immutable" : "public, max-age=3600";
        var type = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "image/jpeg",
        };
        return TypedResults.PhysicalFile(path, type);
    }

    // ---------- Playback ----------

    private static string VideoMime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".mkv" => "video/x-matroska",
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        ".ts" or ".m2ts" => "video/mp2t",
        ".avi" => "video/x-msvideo",
        ".wmv" => "video/x-ms-wmv",
        ".mpg" or ".mpeg" => "video/mpeg",
        ".flv" => "video/x-flv",
        ".ogv" => "video/ogg",
        _ => "application/octet-stream",
    };

    private static void MapPlayback(WebApplication app)
    {
        app.MapMethods("/Items/{id:guid}/PlaybackInfo", ["GET", "POST"], async (Guid id, HttpContext h, LibraryService ls) =>
        {
            var lib = await ls.GetAsync();
            if (lib.Get(id) is not { IsPlayable: true } it) return Results.NotFound();
            await ls.EnsureProbedAsync(it, h.RequestAborted);
            return Results.Json(new Dictionary<string, object?>
            {
                ["MediaSources"] = new[] { Dto.MediaSource(it, h.Client().Token) },
                ["PlaySessionId"] = Ids.N(Guid.NewGuid()),
            });
        });

        // Direct play with HTTP range requests (seeking)
        Get(app, async (Guid id, LibraryService ls) =>
        {
            var lib = await ls.GetAsync();
            if (lib.Get(id) is not { IsPlayable: true, Path: { } path } || !File.Exists(path)) return Results.NotFound();
            return TypedResults.PhysicalFile(path, VideoMime(path), enableRangeProcessing: true);
        }, "/Videos/{id:guid}/stream", "/Videos/{id:guid}/stream.{ext}");

        Get(app, async (Guid id, int index, HttpContext h, LibraryService ls, SubtitleService subs) =>
        {
            var lib = await ls.GetAsync();
            if (lib.Get(id) is not { IsPlayable: true } it) return Results.NotFound();
            await ls.EnsureProbedAsync(it, h.RequestAborted);
            var vtt = await subs.GetVttAsync(it, index, h.RequestAborted);
            return vtt == null ? Results.NotFound() : Results.Text(vtt, "text/vtt; charset=utf-8");
        }, "/Videos/{id:guid}/{msid}/Subtitles/{index:int}/{start:long}/Stream.{fmt}", "/Videos/{id:guid}/{msid}/Subtitles/{index:int}/Stream.{fmt}");

        foreach (var (path, kind) in new[] { ("/Sessions/Playing", "start"), ("/Sessions/Playing/Progress", "progress"), ("/Sessions/Playing/Stopped", "stop") })
        {
            app.MapPost(path, async (HttpContext h, LibraryService ls, Db db) =>
            {
                var body = await Body(h.Request);
                var lib = await ls.GetAsync();
                if (Ids.Parse(Prop(body, "ItemId")) is not Guid id || lib.Get(id) is not { IsPlayable: true } it) return Results.NoContent();
                var ud = db.Get(h.User().Id, id);
                long? pos = long.TryParse(Prop(body, "PositionTicks"), out var p) ? p : null;
                if (kind == "start") ud.LastPlayed = DateTime.UtcNow;
                else Playstate.Report(ud, pos, it.RunTimeTicks, kind == "stop", DateTime.UtcNow);
                db.Save(h.User().Id, [ud]);
                return Results.NoContent();
            });
        }

        async Task<IResult> SetPlayed(Guid id, bool played, HttpContext h)
        {
            var c = await Ctx(h);
            if (c.Lib.Get(id) is not { } it) return Results.NotFound();
            var targets = it.IsPlayable ? [it] : c.Lib.Descendants(it).Where(i => i.IsPlayable).ToList();
            var now = DateTime.UtcNow;
            var changed = targets.Select(t =>
            {
                var ud = c.Ud(t.Id);
                if (played) Playstate.MarkPlayed(ud, now, count: false); else Playstate.MarkUnplayed(ud);
                c.UserData[t.Id] = ud;
                return ud;
            }).ToList();
            h.RequestServices.GetRequiredService<Db>().Save(h.User().Id, changed);
            return Results.Json(Dto.UserDataDto(it, c));
        }
        foreach (var p in new[] { "/UserPlayedItems/{id:guid}", "/Users/{userId}/PlayedItems/{id:guid}" })
        {
            app.MapPost(p, (Guid id, HttpContext h) => SetPlayed(id, true, h));
            app.MapDelete(p, (Guid id, HttpContext h) => SetPlayed(id, false, h));
        }

        async Task<IResult> SetFavorite(Guid id, bool fav, HttpContext h)
        {
            var c = await Ctx(h);
            var ud = c.Ud(id);
            ud.Favorite = fav;
            c.UserData[id] = ud;
            h.RequestServices.GetRequiredService<Db>().Save(h.User().Id, [ud]);
            return c.Lib.Get(id) is { } it ? Results.Json(Dto.UserDataDto(it, c)) : Results.Json(new { IsFavorite = fav, ItemId = Ids.N(id) });
        }
        foreach (var p in new[] { "/UserFavoriteItems/{id:guid}", "/Users/{userId}/FavoriteItems/{id:guid}" })
        {
            app.MapPost(p, (Guid id, HttpContext h) => SetFavorite(id, true, h));
            app.MapDelete(p, (Guid id, HttpContext h) => SetFavorite(id, false, h));
        }

        Get(app, async (Guid id, HttpContext h, LibraryService ls) =>
        {
            var lib = await ls.GetAsync();
            if (lib.Get(id) is not { IsPlayable: true } it) return Results.Json(Dto.Wrap([]));
            await ls.EnsureProbedAsync(it, h.RequestAborted);
            return Results.Json(Dto.Wrap(Playstate.Segments(it)));
        }, "/MediaSegments/{id:guid}");

        // Intro Skipper plugin endpoints (TvFin's fallback when there are no media segments): not a plugin host
        Get(app, () => Results.NotFound(), "/Episode/{id}/IntroSkipperSegments", "/Episode/{id}/IntroTimestamps/v1");

        // Nothing is ever transcoded
        app.MapDelete("/Videos/ActiveEncodings", () => Results.NoContent());
    }
}
