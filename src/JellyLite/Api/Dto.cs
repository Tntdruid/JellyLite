using JellyLite.Auth;
using JellyLite.Storage;
using JellyLite.Library;
using JellyLite.Media;

namespace JellyLite.Api;

/// <summary>Everything needed to turn items into Jellyfin BaseItemDto JSON for one request.</summary>
public sealed class DtoContext(LibrarySnapshot lib, Dictionary<Guid, UserData> userData, string serverId, string? token)
{
    public LibrarySnapshot Lib { get; } = lib;
    public Dictionary<Guid, UserData> UserData { get; } = userData;
    public string ServerId { get; } = serverId;
    public string? Token { get; } = token;

    public UserData Ud(Guid id) => UserData.TryGetValue(id, out var d) ? d : new UserData { ItemId = id };
}

public static class Dto
{
    private static string N(Guid g) => Ids.N(g);

    public static Dictionary<string, object?> Wrap(IEnumerable<object> items, int? total = null, int start = 0)
    {
        var list = items.ToList();
        return new() { ["Items"] = list, ["TotalRecordCount"] = total ?? list.Count, ["StartIndex"] = start };
    }

    public static Dictionary<string, object?> Item(Item it, DtoContext c, bool full = false)
    {
        var d = new Dictionary<string, object?>
        {
            ["Name"] = it.Name,
            ["ServerId"] = c.ServerId,
            ["Id"] = N(it.Id),
            ["Type"] = it.Type,
            ["IsFolder"] = it.IsFolder,
            ["SortName"] = it.SortName,
            ["DateCreated"] = it.DateCreated,
            ["LocationType"] = "FileSystem",
        };
        void Add(string k, object? v) { if (v != null) d[k] = v; }

        Add("CollectionType", it.CollectionType);
        Add("OriginalTitle", it.OriginalTitle);
        Add("Overview", it.Overview);
        Add("Taglines", it.Tagline != null ? new[] { it.Tagline } : null);
        Add("OfficialRating", it.OfficialRating);
        Add("CommunityRating", it.CommunityRating);
        Add("ProductionYear", it.ProductionYear);
        Add("PremiereDate", it.PremiereDate);
        Add("DateLastContentAdded", it.DateLastContentAdded);
        Add("IndexNumber", it.IndexNumber);
        Add("IndexNumberEnd", it.IndexNumberEnd);
        Add("ParentIndexNumber", it.ParentIndexNumber);
        if (it.ParentId is Guid pid) d["ParentId"] = N(pid);
        if (it.SeriesId is Guid sid) { d["SeriesId"] = N(sid); d["SeriesName"] = it.SeriesName; }
        if (it.SeasonId is Guid seid) { d["SeasonId"] = N(seid); d["SeasonName"] = it.SeasonName; }
        d["Genres"] = it.Genres;
        d["Studios"] = it.Studios.Select(s => new { Name = s, Id = N(Ids.From("studio:" + s.ToLowerInvariant())) }).ToList();
        d["ProviderIds"] = it.ProviderIds;

        if (it.IsPlayable)
        {
            d["MediaType"] = "Video";
            d["VideoType"] = "VideoFile";
            Add("RunTimeTicks", it.RunTimeTicks);
            d["Container"] = Container(it);
            d["HasSubtitles"] = it.ExternalSubs.Count > 0 || (it.Media?.Streams.Any(s => s.Type == "Subtitle") ?? false);
        }
        if (it.IsFolder) d["ChildCount"] = it.Children.Count;

        Images(it, c, d);
        d["UserData"] = UserDataDto(it, c);

        if (full)
        {
            d["People"] = it.People.Select(p => PersonRefDto(p, c)).ToList();
            if (it.IsPlayable)
            {
                var src = MediaSource(it, c.Token);
                d["MediaSources"] = new[] { src };
                d["MediaStreams"] = src["MediaStreams"];
                d["Chapters"] = (it.Media?.Chapters ?? []).Select((ch, i) => new Dictionary<string, object?>
                {
                    ["StartPositionTicks"] = ch.StartTicks,
                    ["Name"] = ch.Name,
                    ["ImageTag"] = ChapterImages.Tag(it, i),
                }).ToList();
            }
        }
        return d;
    }

    private static void Images(Item it, DtoContext c, Dictionary<string, object?> d)
    {
        d["ImageTags"] = it.Images.ToDictionary(kv => kv.Key, kv => kv.Value.Tag);
        d["BackdropImageTags"] = it.Backdrops.Select(b => b.Tag).ToList();
        d["PrimaryImageAspectRatio"] = it.Type is ItemTypes.Episode or ItemTypes.Video or ItemTypes.Library ? 16.0 / 9 : 2.0 / 3;

        // Episodes/seasons borrow images from their series, like Jellyfin
        var series = it.SeriesId is Guid sid ? c.Lib.Get(sid) : null;
        if (series == null) return;
        if (series.Images.TryGetValue("Primary", out var sp)) d["SeriesPrimaryImageTag"] = sp.Tag;
        if (it.Backdrops.Count == 0 && series.Backdrops.Count > 0)
        {
            d["ParentBackdropItemId"] = N(series.Id);
            d["ParentBackdropImageTags"] = series.Backdrops.Select(b => b.Tag).ToList();
        }
        if (!it.Images.ContainsKey("Thumb") && series.Images.TryGetValue("Thumb", out var st))
        {
            d["ParentThumbItemId"] = N(series.Id);
            d["ParentThumbImageTag"] = st.Tag;
        }
        if (!it.Images.ContainsKey("Banner") && series.Images.TryGetValue("Banner", out var sb))
        {
            d["ParentBannerItemId"] = N(series.Id);
            d["ParentBannerImageTag"] = sb.Tag;
        }
        if (series.Images.TryGetValue("Logo", out var sl))
        {
            d["ParentLogoItemId"] = N(series.Id);
            d["ParentLogoImageTag"] = sl.Tag;
        }
    }

    public static Dictionary<string, object?> UserDataDto(Item it, DtoContext c)
    {
        var ud = c.Ud(it.Id);
        var d = new Dictionary<string, object?>
        {
            ["PlaybackPositionTicks"] = ud.Position,
            ["PlayCount"] = ud.PlayCount,
            ["IsFavorite"] = ud.Favorite,
            ["Played"] = ud.Played,
            ["Key"] = N(it.Id),
            ["ItemId"] = N(it.Id),
        };
        if (ud.LastPlayed is { } lp) d["LastPlayedDate"] = lp;
        if (it.IsPlayable && ud.Position > 0 && it.RunTimeTicks is long rt && rt > 0)
            d["PlayedPercentage"] = Math.Min(100.0, ud.Position * 100.0 / rt);
        if (it.Type is ItemTypes.Series or ItemTypes.Season)
        {
            var eps = c.Lib.Descendants(it).Where(e => e.IsPlayable).ToList();
            var played = eps.Count(e => c.Ud(e.Id).Played);
            d["UnplayedItemCount"] = eps.Count - played;
            d["Played"] = eps.Count > 0 && played == eps.Count;
            if (eps.Count > 0 && played > 0) d["PlayedPercentage"] = played * 100.0 / eps.Count;
        }
        return d;
    }

    private static Dictionary<string, object?> PersonRefDto(PersonRef p, DtoContext c)
    {
        var d = new Dictionary<string, object?> { ["Name"] = p.Name, ["Id"] = N(p.Id), ["Role"] = p.Role ?? "", ["Type"] = p.Type };
        if (c.Lib.People.TryGetValue(p.Id, out var person) && person.Image != null) d["PrimaryImageTag"] = person.Image.Tag;
        return d;
    }

    public static Dictionary<string, object?> Person(Person p, DtoContext c)
    {
        var ids = new Dictionary<string, string>();
        if (p.TmdbId != null) ids["Tmdb"] = p.TmdbId;
        if (p.ImdbId != null) ids["Imdb"] = p.ImdbId;
        return new()
        {
            ["Name"] = p.Name,
            ["ServerId"] = c.ServerId,
            ["Id"] = N(p.Id),
            ["Type"] = "Person",
            ["IsFolder"] = false,
            ["ProviderIds"] = ids,
            ["ImageTags"] = p.Image != null ? new Dictionary<string, string> { ["Primary"] = p.Image.Tag } : new Dictionary<string, string>(),
            ["BackdropImageTags"] = Array.Empty<string>(),
            ["UserData"] = new Dictionary<string, object?> { ["IsFavorite"] = c.Ud(p.Id).Favorite, ["Played"] = false, ["PlaybackPositionTicks"] = 0, ["PlayCount"] = 0 },
        };
    }

    public static Dictionary<string, object?> Studio(Studio s, DtoContext c) => new()
    {
        ["Name"] = s.Name,
        ["ServerId"] = c.ServerId,
        ["Id"] = N(s.Id),
        ["Type"] = "Studio",
        ["IsFolder"] = false,
        ["ImageTags"] = s.Image != null ? new Dictionary<string, string> { ["Primary"] = s.Image.Tag } : new Dictionary<string, string>(),
        ["BackdropImageTags"] = Array.Empty<string>(),
    };

    public static Dictionary<string, object?> User(UserInfo u, string serverId, string? imageTag) => new()
    {
        ["Name"] = u.Name,
        ["ServerId"] = serverId,
        ["Id"] = N(u.Id),
        ["PrimaryImageTag"] = imageTag,
        ["HasPassword"] = false,
        ["HasConfiguredPassword"] = false,
        ["EnableAutoLogin"] = false,
        ["Configuration"] = new Dictionary<string, object?> { ["PlayDefaultAudioTrack"] = true, ["SubtitleMode"] = "Default" },
        ["Policy"] = new Dictionary<string, object?> { ["IsAdministrator"] = false, ["IsDisabled"] = false, ["EnableMediaPlayback"] = true, ["EnableAllFolders"] = true },
    };

    // ---------- Playback ----------

    public static string Container(Item it)
    {
        var ext = Path.GetExtension(it.Path ?? "").TrimStart('.').ToLowerInvariant();
        return ext switch { "m2ts" or "mts" => "ts", "mpeg" => "mpg", "" => "mkv", _ => ext };
    }

    /// <summary>One media source per file, always direct play (this server never transcodes).</summary>
    public static Dictionary<string, object?> MediaSource(Item it, string? token)
    {
        var streams = new List<Dictionary<string, object?>>();
        var srcId = N(it.Id);
        var key = token != null ? "?api_key=" + token : "";
        foreach (var s in it.Media?.Streams ?? [])
        {
            // Image subtitles (PGS/DVD) would need burning in = transcoding: leave them out
            if (s.Type == "Subtitle" && !SubtitleService.TextCodecs.Contains(s.Codec ?? "")) continue;
            var d = new Dictionary<string, object?>
            {
                ["Index"] = s.Index,
                ["Type"] = s.Type,
                ["Codec"] = s.Codec,
                ["Profile"] = s.Profile,
                ["Language"] = s.Language,
                ["Title"] = s.Title,
                ["DisplayTitle"] = DisplayTitle(s),
                ["IsDefault"] = s.IsDefault,
                ["IsForced"] = s.IsForced,
                ["IsExternal"] = false,
                ["Width"] = s.Width,
                ["Height"] = s.Height,
                ["Channels"] = s.Channels,
                ["ChannelLayout"] = s.ChannelLayout,
                ["SampleRate"] = s.SampleRate,
                ["BitRate"] = s.BitRate,
                ["AverageFrameRate"] = s.FrameRate,
            };
            if (s.Type == "Subtitle")
            {
                d["IsTextSubtitleStream"] = true;
                d["SupportsExternalStream"] = true;
                d["DeliveryMethod"] = "External";
                d["DeliveryUrl"] = $"/Videos/{srcId}/{srcId}/Subtitles/{s.Index}/0/Stream.vtt{key}";
            }
            streams.Add(d);
        }
        var extBase = SubtitleService.ExternalBase(it);
        for (var i = 0; i < it.ExternalSubs.Count; i++)
        {
            var sub = it.ExternalSubs[i];
            var index = extBase + i;
            var lang = Languages.DisplayName(sub.Language);
            streams.Add(new Dictionary<string, object?>
            {
                ["Index"] = index,
                ["Type"] = "Subtitle",
                ["Codec"] = sub.Format == "srt" ? "subrip" : sub.Format,
                ["Language"] = sub.Language,
                ["DisplayTitle"] = string.Join(" - ", new[] { lang ?? "Ukendt", sub.IsForced ? "Tvungen" : null, "Ekstern" }.Where(x => x != null)),
                ["IsDefault"] = sub.IsDefault,
                ["IsForced"] = sub.IsForced,
                ["IsExternal"] = true,
                ["IsTextSubtitleStream"] = true,
                ["SupportsExternalStream"] = true,
                ["DeliveryMethod"] = "External",
                ["DeliveryUrl"] = $"/Videos/{srcId}/{srcId}/Subtitles/{index}/0/Stream.vtt{key}",
                ["Path"] = null,
            });
        }

        var audio = streams.Where(s => (string?)s["Type"] == "Audio").ToList();
        var defaultAudio = audio.FirstOrDefault(s => s["IsDefault"] is true) ?? audio.FirstOrDefault();
        var forcedSub = streams.FirstOrDefault(s => (string?)s["Type"] == "Subtitle" && s["IsForced"] is true);

        var src = new Dictionary<string, object?>
        {
            ["Id"] = srcId,
            ["Name"] = it.Name,
            ["Protocol"] = "File",
            ["Type"] = "Default",
            ["Container"] = Container(it),
            ["Size"] = it.Size,
            ["IsRemote"] = false,
            ["ETag"] = Ids.Hash(it.Path + "|" + it.Mtime.Ticks),
            ["RunTimeTicks"] = it.RunTimeTicks,
            ["Bitrate"] = it.Media?.Bitrate,
            ["SupportsDirectPlay"] = true,
            ["SupportsDirectStream"] = true,
            ["SupportsTranscoding"] = false,
            ["MediaStreams"] = streams,
            ["DefaultAudioStreamIndex"] = defaultAudio?["Index"],
            ["DefaultSubtitleStreamIndex"] = forcedSub?["Index"] ?? -1,
        };
        return src;
    }

    private static string DisplayTitle(StreamInfo s)
    {
        var codec = (s.Codec ?? "").ToUpperInvariant();
        switch (s.Type)
        {
            case "Video":
                return string.Join(" ", new[] { s.Height is int h ? Resolution(h, s.Width) : null, codec }.Where(x => !string.IsNullOrEmpty(x)));
            case "Audio":
                var layout = s.ChannelLayout ?? (s.Channels is int ch ? ch + " ch" : null);
                return string.Join(" - ", new[] { Languages.DisplayName(s.Language), s.Title, codec, layout, s.IsDefault ? "Standard" : null }.Where(x => !string.IsNullOrEmpty(x)));
            default:
                return string.Join(" - ", new[] { Languages.DisplayName(s.Language) ?? "Ukendt", s.Title, s.IsForced ? "Tvungen" : null, codec }.Where(x => !string.IsNullOrEmpty(x)));
        }
    }

    private static string Resolution(int height, int? width)
    {
        var w = width ?? 0;
        if (w >= 3800 || height >= 2100) return "4K";
        if (w >= 1900 || height >= 1000) return "1080p";
        if (w >= 1260 || height >= 700) return "720p";
        return height + "p";
    }
}
