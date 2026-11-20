using JellyLite.Storage;
using JellyLite.Media;

namespace JellyLite.Library;

/// <summary>Walks the library folders and builds a <see cref="LibrarySnapshot"/>.
/// Metadata comes from local files only: .nfo, yt-dlp .info.json, images and subtitles next to the video.</summary>
public sealed class Scanner(AppConfig cfg, Db db, ILogger log)
{
    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
        { ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts", ".webm", ".mpg", ".mpeg", ".flv", ".ogv", ".3gp" };
    private static readonly string[] ImageExt = [".jpg", ".jpeg", ".png", ".webp"];
    private static readonly HashSet<string> SubtitleExt = new(StringComparer.OrdinalIgnoreCase) { ".srt", ".vtt", ".ass", ".ssa" };
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "extras", "featurettes", "behind the scenes", "deleted scenes", "interviews", "scenes", "shorts", "trailers",
        "samples", "sample", "other", "@eadir", "#recycle", "$recycle.bin", "#snapshot", "lost+found", "system volume information",
    };
    private static readonly string[] ExtraSuffixes =
        ["-trailer", "-sample", "-featurette", "-behindthescenes", "-deleted", "-deletedscene", "-interview", "-scene", "-short", "-other", "-extra"];

    // Parsed .nfo / .info.json files, kept between scans and re-read only when the file changes.
    private readonly Dictionary<string, (DateTime Mtime, object? Value)> _sidecars = new();

    private sealed class Ctx(LibrarySnapshot previous, Dictionary<string, DateTime> firstSeen)
    {
        public LibrarySnapshot Previous { get; } = previous;
        public Dictionary<string, DateTime> FirstSeen { get; } = firstSeen;
        public Dictionary<string, DateTime> NewFirstSeen { get; } = [];
        public Dictionary<string, ImageRef> StudioImages { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool FreshLibrary { get; set; }
    }

    private sealed class Dir(DirectoryInfo info, Dictionary<string, FileInfo> files, List<DirectoryInfo> subDirs)
    {
        public string Path => info.FullName;
        public string Name => info.Name;
        public Dictionary<string, FileInfo> Files { get; } = files;
        public List<DirectoryInfo> SubDirs { get; } = subDirs;
        public FileInfo? Find(string fileName) => Files.GetValueOrDefault(fileName);
    }

    public LibrarySnapshot Scan(LibrarySnapshot previous)
    {
        var snap = new LibrarySnapshot();
        var ctx = new Ctx(previous, db.FirstSeen());
        foreach (var lc in cfg.Libraries)
        {
            var type = lc.Type.Trim().ToLowerInvariant();
            var lib = new Item
            {
                Id = Ids.From("library:" + lc.Name),
                Type = ItemTypes.Library,
                Name = lc.Name,
                SortName = FilenameParser.SortName(lc.Name),
                Path = lc.Path,
                CollectionType = type switch { "tvshows" or "shows" or "series" => "tvshows", "youtube" or "homevideos" or "videos" => "homevideos", _ => "movies" },
                DateCreated = DateTime.UtcNow,
            };
            lib.LibraryId = lib.Id;
            snap.Libraries.Add(lib);
            snap.Items[lib.Id] = lib;
            if (!Reachable(lc.Path, previous.Get(lib.Id)))
            {
                // NAS off or network share not mounted: keep showing what was there, instead of an empty library
                lib.Offline = true;
                var kept = KeepPrevious(snap, previous, lib);
                log.LogWarning("Library {Name}: {Path} is not reachable, keeping {Count} known items", lc.Name, lc.Path, kept);
                continue;
            }
            var root = Path.GetFullPath(lc.Path);
            ctx.FreshLibrary = !ctx.FirstSeen.Keys.Any(k => k.StartsWith(root, StringComparison.Ordinal));
            switch (lib.CollectionType)
            {
                case "tvshows": ScanShows(snap, ctx, lib, root); break;
                case "homevideos": ScanVideos(snap, ctx, lib, root); break;
                default: ScanMovies(snap, ctx, lib, root); break;
            }
            log.LogInformation("Library {Name}: {Count} items", lc.Name, snap.Descendants(lib).Count());
        }
        ResolvePeople(snap);
        KeepPreviousPeople(snap, previous);
        foreach (var it in snap.Items.Values)
        {
            foreach (var name in it.Studios)
            {
                var id = Ids.From("studio:" + name.ToLowerInvariant());
                if (!snap.Studios.ContainsKey(id))
                    snap.Studios[id] = new Studio
                    {
                        Id = id,
                        Name = name,
                        Image = ctx.StudioImages.GetValueOrDefault(name) ?? previous.Studios.GetValueOrDefault(id)?.Image,
                    };
            }
        }
        if (ctx.NewFirstSeen.Count > 0) db.AddFirstSeen(ctx.NewFirstSeen);
        return snap;
    }

    /// <summary>A library folder counts as unreachable when it's missing or can't be listed, or when it's
    /// suddenly empty while the last scan found items (an unmounted share often leaves an empty mount point).</summary>
    internal static bool Reachable(string path, Item? previousLibrary)
    {
        try
        {
            if (!Directory.Exists(path)) return false;
            var empty = !Directory.EnumerateFileSystemEntries(path).Any();
            return !(empty && previousLibrary is { Children.Count: > 0 });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Copies a library's items from the previous scan. Returns how many were kept.</summary>
    private static int KeepPrevious(LibrarySnapshot snap, LibrarySnapshot previous, Item lib)
    {
        if (previous.Get(lib.Id) is not { } old) return 0;
        lib.Children.AddRange(old.Children);
        var count = 0;
        foreach (var it in previous.Descendants(old))
        {
            it.KeptFromPreviousScan = true;
            snap.Items[it.Id] = it;
            count++;
        }
        return count;
    }

    /// <summary>People for kept items (their metadata files couldn't be read this time).</summary>
    private static void KeepPreviousPeople(LibrarySnapshot snap, LibrarySnapshot previous)
    {
        foreach (var it in snap.Items.Values.Where(i => i.KeptFromPreviousScan))
        {
            foreach (var p in it.People)
            {
                if (!snap.People.TryGetValue(p.Id, out var person))
                {
                    var old = previous.People.GetValueOrDefault(p.Id);
                    snap.People[p.Id] = person = new Person
                    {
                        Id = p.Id,
                        Name = p.Name,
                        TmdbId = old?.TmdbId,
                        ImdbId = old?.ImdbId,
                        Image = old?.Image,
                    };
                }
                person.ItemIds.Add(it.Id);
                if (it.SeriesId is Guid sid) person.ItemIds.Add(sid);
            }
        }
    }

    // ---------- Movies ----------

    private void ScanMovies(LibrarySnapshot snap, Ctx ctx, Item lib, string root)
    {
        foreach (var dir in Walk(root))
        {
            var videos = Videos(dir);
            // A folder with one movie: folder-level poster.jpg/movie.nfo belong to it
            var single = videos.Count == 1 && !SamePath(dir.Path, root);
            foreach (var f in videos)
            {
                var baseName = Path.GetFileNameWithoutExtension(f.Name);
                var nfo = LoadNfo(dir.Find(baseName + ".nfo") ?? (single ? dir.Find("movie.nfo") : null));
                var fromFile = FilenameParser.ParseMovie(baseName);
                var fromFolder = single ? FilenameParser.ParseMovie(dir.Name) : fromFile;
                var (title, year) = fromFile.Year == null && fromFolder.Year != null ? fromFolder : fromFile;

                var it = NewVideo(ItemTypes.Movie, lib, root, f, dir, ctx);
                it.Name = nfo?.Title ?? title;
                ApplyNfo(it, nfo);
                it.ProductionYear ??= year;
                SetImage(it, "Primary", Side(dir, baseName, "poster", "cover") ?? (single ? Named(dir, "poster", "folder", "cover", "movie") : null) ?? Same(dir, baseName));
                AddBackdrop(it, Side(dir, baseName, "fanart", "backdrop") ?? (single ? Named(dir, "fanart", "backdrop", "background") : null));
                SetImage(it, "Thumb", Side(dir, baseName, "landscape", "thumb") ?? (single ? Named(dir, "landscape", "thumb") : null));
                SetImage(it, "Banner", Side(dir, baseName, "banner") ?? (single ? Named(dir, "banner") : null));
                SetImage(it, "Logo", Side(dir, baseName, "clearlogo", "logo") ?? (single ? Named(dir, "clearlogo", "logo") : null));
                ActorThumbs(it, dir);
                Finish(it, nfo);
                Add(snap, lib, it);
            }
        }
    }

    // ---------- TV shows ----------

    private void ScanShows(LibrarySnapshot snap, Ctx ctx, Item lib, string root)
    {
        foreach (var sub in ListDir(root)?.SubDirs ?? [])
        {
            if (Skip(sub)) continue;
            var sdir = ListDir(sub.FullName);
            if (sdir == null) continue;
            var nfo = LoadNfo(sdir.Find("tvshow.nfo"));
            var (title, year) = FilenameParser.ParseMovie(sdir.Name);
            var series = new Item
            {
                Id = Ids.From("item:" + lib.Name + "/" + Rel(root, sdir.Path)),
                Type = ItemTypes.Series,
                Name = nfo?.Title ?? title,
                Path = sdir.Path,
                LibraryId = lib.Id,
                ParentId = lib.Id,
            };
            ApplyNfo(series, nfo);
            series.ProductionYear ??= year;
            SetImage(series, "Primary", Named(sdir, "poster", "folder", "cover", "show"));
            AddBackdrop(series, Named(sdir, "fanart", "backdrop", "background", "art"));
            SetImage(series, "Banner", Named(sdir, "banner"));
            SetImage(series, "Thumb", Named(sdir, "landscape", "thumb"));
            SetImage(series, "Logo", Named(sdir, "clearlogo", "logo"));
            ActorThumbs(series, sdir);
            Finish(series, nfo);

            var regulars = new HashSet<string>(series.RawPeople.Where(p => p.Type == "Actor").Select(p => p.Name.Trim()), StringComparer.OrdinalIgnoreCase);
            var seasons = new Dictionary<int, Item>();
            var seasonDirs = new Dictionary<int, Dir>();

            foreach (var dir in Walk(sdir.Path))
            {
                int? folderSeason = SamePath(dir.Path, sdir.Path) ? null : FilenameParser.ParseSeasonFolder(dir.Name);
                if (folderSeason is int fs) seasonDirs.TryAdd(fs, dir);
                foreach (var f in Videos(dir))
                {
                    var baseName = Path.GetFileNameWithoutExtension(f.Name);
                    var parsed = FilenameParser.ParseEpisode(baseName);
                    var enfo = LoadNfo(dir.Find(baseName + ".nfo"));
                    var seasonNo = enfo?.Season ?? parsed.Season ?? folderSeason ?? 1;

                    var ep = NewVideo(ItemTypes.Episode, lib, root, f, dir, ctx);
                    ep.IndexNumber = enfo?.Episode ?? parsed.Episode;
                    ep.IndexNumberEnd = enfo?.EpisodeEnd ?? parsed.EpisodeEnd;
                    ep.ParentIndexNumber = seasonNo;
                    ep.Name = enfo?.Title ?? parsed.Title ?? (ep.IndexNumber is int n ? $"Episode {n}" : baseName);
                    ApplyNfo(ep, enfo);
                    // Actors in an episode who aren't series regulars are guest stars
                    ep.RawPeople = ep.RawPeople
                        .Select(p => p.Type == "Actor" && regulars.Count > 0 && !regulars.Contains(p.Name.Trim()) ? p with { Type = "GuestStar" } : p)
                        .ToList();
                    SetImage(ep, "Primary", Side(dir, baseName, "thumb") ?? Same(dir, baseName));
                    Finish(ep, enfo);

                    if (!seasons.TryGetValue(seasonNo, out var season))
                    {
                        season = new Item
                        {
                            Id = Ids.From("item:" + lib.Name + "/" + Rel(root, sdir.Path) + "#season" + seasonNo),
                            Type = ItemTypes.Season,
                            Name = seasonNo == 0 ? cfg.SpecialsLabel : $"{cfg.SeasonLabel} {seasonNo}",
                            IndexNumber = seasonNo,
                            LibraryId = lib.Id,
                            ParentId = series.Id,
                            SeriesId = series.Id,
                            SeriesName = series.Name,
                            DateCreated = ep.DateCreated,
                        };
                        seasons[seasonNo] = season;
                    }
                    ep.ParentId = season.Id;
                    ep.SeasonId = season.Id;
                    ep.SeasonName = season.Name;
                    ep.SeriesId = series.Id;
                    ep.SeriesName = series.Name;
                    season.Children.Add(ep.Id);
                    snap.Items[ep.Id] = ep;
                }
            }
            if (seasons.Count == 0) continue;

            foreach (var (no, season) in seasons.OrderBy(s => s.Key == 0 ? int.MaxValue : s.Key))
            {
                var eps = season.Children.Select(id => snap.Items[id]).ToList();
                season.DateCreated = eps.Min(e => e.DateCreated);
                season.DateLastContentAdded = eps.Max(e => e.DateCreated);
                season.PremiereDate = eps.Min(e => e.PremiereDate);
                season.ProductionYear = season.PremiereDate?.Year;
                var ordered = eps.OrderBy(e => e.IndexNumber ?? int.MaxValue).ThenBy(e => e.SortName, NaturalComparer.Instance).Select(e => e.Id).ToList();
                season.Children.Clear();
                season.Children.AddRange(ordered);

                var key = no == 0 ? "season-specials" : $"season{no:00}";
                if (seasonDirs.TryGetValue(no, out var sd))
                {
                    var snfo = LoadNfo(sd.Find("season.nfo"));
                    if (snfo?.Title != null) season.Name = snfo.Title;
                    season.Overview = snfo?.Plot;
                    SetImage(season, "Primary", Named(sd, "poster", "folder", "cover"));
                    AddBackdrop(season, Named(sd, "fanart", "backdrop"));
                    SetImage(season, "Banner", Named(sd, "banner"));
                    SetImage(season, "Thumb", Named(sd, "landscape", "thumb"));
                }
                SetImage(season, "Primary", Named(sdir, key + "-poster", $"season{no}-poster"));
                SetImage(season, "Banner", Named(sdir, key + "-banner"));
                SetImage(season, "Thumb", Named(sdir, key + "-landscape", key + "-thumb"));
                season.SortName = no.ToString("D4");
                foreach (var e in eps) e.SeasonName = season.Name;
                series.Children.Add(season.Id);
                snap.Items[season.Id] = season;
            }
            var all = seasons.Values.SelectMany(s => s.Children).Select(id => snap.Items[id]).ToList();
            series.DateCreated = all.Min(e => e.DateCreated);
            series.DateLastContentAdded = all.Max(e => e.DateCreated);
            series.PremiereDate ??= all.Min(e => e.PremiereDate);
            series.ProductionYear ??= series.PremiereDate?.Year;
            Add(snap, lib, series);
        }
    }

    // ---------- YouTube / videos ----------

    private void ScanVideos(LibrarySnapshot snap, Ctx ctx, Item lib, string root)
    {
        var rootDir = ListDir(root);
        if (rootDir == null) return;
        // Videos directly in the library folder
        ScanVideoFolder(snap, ctx, lib, root, rootDir, lib.Name, null, recursive: false);
        // Each top-level folder is a channel
        foreach (var sub in rootDir.SubDirs)
        {
            if (Skip(sub)) continue;
            var cdir = ListDir(sub.FullName);
            if (cdir == null) continue;
            var nfo = LoadNfo(cdir.Find("tvshow.nfo"));
            var image = Named(cdir, "poster", "folder", "cover", "avatar", "channel");
            ScanVideoFolder(snap, ctx, lib, root, cdir, nfo?.Title ?? cdir.Name, image, recursive: true);
        }
    }

    private void ScanVideoFolder(LibrarySnapshot snap, Ctx ctx, Item lib, string root, Dir start, string defaultChannel, ImageRef? channelImage, bool recursive)
    {
        foreach (var dir in recursive ? Walk(start.Path) : [start])
        {
            foreach (var f in Videos(dir))
            {
                var baseName = Path.GetFileNameWithoutExtension(f.Name);
                var info = LoadInfo(dir.Find(baseName + ".info.json"));
                var nfo = LoadNfo(dir.Find(baseName + ".nfo"));
                var v = NewVideo(ItemTypes.Video, lib, root, f, dir, ctx);
                v.Name = info?.Title ?? nfo?.Title ?? FilenameParser.CleanVideoTitle(baseName);
                ApplyNfo(v, nfo);
                v.Overview ??= info?.Description;
                v.PremiereDate = info?.UploadDate ?? nfo?.Premiered ?? FilenameParser.LeadingDateOf(baseName);
                v.ProductionYear = v.PremiereDate?.Year ?? v.ProductionYear;
                if (info?.Duration is double d) v.MetadataRunTimeTicks ??= (long)(d * TimeSpan.TicksPerSecond);
                var channel = info?.Channel ?? nfo?.ShowTitle ?? nfo?.Studios.FirstOrDefault() ?? defaultChannel;
                v.Studios = [channel];
                if (v.Genres.Count == 0 && info != null) v.Genres = info.Categories;
                if ((info?.Id ?? FilenameParser.YoutubeId(baseName)) is { } ytId) v.ProviderIds["YouTube"] = ytId;
                SetImage(v, "Primary", Side(dir, baseName, "thumb", "poster") ?? Same(dir, baseName));
                if (channelImage != null) ctx.StudioImages.TryAdd(channel, channelImage);
                Finish(v, nfo);
                Add(snap, lib, v);
            }
        }
    }

    // ---------- People ----------

    /// <summary>Matches people across the library by TMDB/IMDb id, so two actors with the same name stay apart.
    /// A person without an id is matched by name only when exactly one person with that name has an id.</summary>
    internal static void ResolvePeople(LibrarySnapshot snap)
    {
        static string Norm(string n) => n.Trim().ToLowerInvariant();
        var raws = snap.Items.Values.SelectMany(i => i.RawPeople).ToList();
        var imdbToTmdb = new Dictionary<string, string>();
        foreach (var p in raws)
            if (p.TmdbId != null && p.ImdbId != null) imdbToTmdb.TryAdd(p.ImdbId, p.TmdbId);

        string? Strong(RawPerson p) =>
            p.TmdbId != null ? "tmdb:" + p.TmdbId :
            p.ImdbId != null ? imdbToTmdb.TryGetValue(p.ImdbId, out var t) ? "tmdb:" + t : "imdb:" + p.ImdbId : null;

        var strongByName = new Dictionary<string, HashSet<string>>();
        foreach (var p in raws)
            if (Strong(p) is { } s)
            {
                if (!strongByName.TryGetValue(Norm(p.Name), out var set)) strongByName[Norm(p.Name)] = set = [];
                set.Add(s);
            }

        foreach (var item in snap.Items.Values)
        {
            foreach (var p in item.RawPeople)
            {
                var key = Strong(p) ?? (strongByName.TryGetValue(Norm(p.Name), out var set) && set.Count == 1 ? set.First() : "name:" + Norm(p.Name));
                var id = Ids.From("person:" + key);
                if (!snap.People.TryGetValue(id, out var person))
                    snap.People[id] = person = new Person { Id = id, Name = p.Name.Trim() };
                if (key.StartsWith("tmdb:")) person.TmdbId ??= key[5..];
                person.ImdbId ??= p.ImdbId;
                if (person.Image == null && !string.IsNullOrWhiteSpace(p.Thumb)) person.Image = new ImageRef(p.Thumb, Ids.Hash(p.Thumb));
                person.ItemIds.Add(item.Id);
                if (item.SeriesId is Guid sid) person.ItemIds.Add(sid);
                if (!item.People.Any(x => x.Id == id && x.Type == p.Type)) item.People.Add(new PersonRef(id, person.Name, p.Role, p.Type));
            }
            item.RawPeople = [];
        }
    }

    /// <summary>Kodi-style .actors/Firstname_Lastname.jpg next to the movie/series.</summary>
    private static void ActorThumbs(Item it, Dir dir)
    {
        var actorsDir = dir.SubDirs.FirstOrDefault(d => d.Name.Equals(".actors", StringComparison.OrdinalIgnoreCase));
        if (actorsDir == null || it.RawPeople.Count == 0) return;
        var files = ListDir(actorsDir.FullName);
        if (files == null) return;
        it.RawPeople = it.RawPeople.Select(p =>
        {
            if (!string.IsNullOrWhiteSpace(p.Thumb)) return p;
            var img = Named(files, p.Name.Trim().Replace(' ', '_'));
            return img != null ? p with { Thumb = img.Path } : p;
        }).ToList();
    }

    // ---------- Helpers ----------

    private Item NewVideo(string type, Item lib, string root, FileInfo f, Dir dir, Ctx ctx)
    {
        var id = Ids.From("item:" + lib.Name + "/" + Rel(root, f.FullName));
        var it = new Item
        {
            Id = id,
            Type = type,
            Name = Path.GetFileNameWithoutExtension(f.Name),
            Path = f.FullName,
            LibraryId = lib.Id,
            Size = f.Length,
            Mtime = f.LastWriteTimeUtc,
            DateCreated = DateFor(f, ctx),
        };
        if (ctx.Previous.Items.TryGetValue(id, out var old) && old.Size == it.Size && old.Mtime == it.Mtime) it.Media = old.Media;
        it.ExternalSubs.AddRange(FindSubtitles(dir, Path.GetFileNameWithoutExtension(f.Name)));
        return it;
    }

    /// <summary>"Date added" = when the server first saw the file. For a library scanned the first time, the file date is used.</summary>
    private static DateTime DateFor(FileInfo f, Ctx ctx)
    {
        if (ctx.FirstSeen.TryGetValue(f.FullName, out var d)) return d;
        if (ctx.NewFirstSeen.TryGetValue(f.FullName, out d)) return d;
        d = ctx.FreshLibrary ? Min(f.CreationTimeUtc, f.LastWriteTimeUtc) : DateTime.UtcNow;
        ctx.NewFirstSeen[f.FullName] = d;
        return d;
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    /// <summary>Movie.srt, Movie.da.srt, Movie.en.forced.srt ...</summary>
    internal static IEnumerable<ExternalSub> FindSubtitlesIn(IEnumerable<string> fileNames, string dirPath, string baseName)
    {
        foreach (var name in fileNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var ext = Path.GetExtension(name);
            if (!SubtitleExt.Contains(ext) || !name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase)) continue;
            var middle = name[baseName.Length..^ext.Length];
            if (middle.Length > 0 && middle[0] != '.') continue;
            string? lang = null;
            bool forced = false, isDefault = false;
            foreach (var token in middle.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                var t = token.ToLowerInvariant();
                if (t is "forced" or "foreign") forced = true;
                else if (t == "default") isDefault = true;
                else lang ??= Languages.Normalize(t);
            }
            yield return new ExternalSub(Path.Combine(dirPath, name), lang, forced, isDefault, ext.TrimStart('.').ToLowerInvariant());
        }
    }

    private static IEnumerable<ExternalSub> FindSubtitles(Dir dir, string baseName) => FindSubtitlesIn(dir.Files.Keys, dir.Path, baseName);

    private static void ApplyNfo(Item it, NfoData? n)
    {
        if (n == null) return;
        it.OriginalTitle = n.OriginalTitle;
        it.Overview = n.Plot;
        it.Tagline = n.Tagline;
        it.OfficialRating = n.Mpaa;
        it.CommunityRating = n.Rating;
        it.ProductionYear = n.Year;
        it.PremiereDate = n.Premiered;
        it.Genres = n.Genres;
        it.Studios = n.Studios;
        it.RawPeople = n.People;
        if (n.RuntimeMinutes is int m && m > 0) it.MetadataRunTimeTicks = TimeSpan.FromMinutes(m).Ticks;
        foreach (var (k, v) in n.Ids) it.ProviderIds[k] = v;
    }

    private static void Finish(Item it, NfoData? n) => it.SortName = FilenameParser.SortName(n?.SortTitle ?? it.Name);

    private static void Add(LibrarySnapshot snap, Item lib, Item it)
    {
        it.ParentId ??= lib.Id;
        lib.Children.Add(it.Id);
        snap.Items[it.Id] = it;
    }

    private static void SetImage(Item it, string type, ImageRef? img)
    {
        if (img != null && !it.Images.ContainsKey(type)) it.Images[type] = img;
    }

    private static void AddBackdrop(Item it, ImageRef? img)
    {
        if (img != null) it.Backdrops.Add(img);
    }

    private static ImageRef Ref(FileInfo f) => new(f.FullName, Ids.Hash(f.FullName + "|" + f.LastWriteTimeUtc.Ticks + "|" + f.Length));

    /// <summary>{base}-{suffix}.jpg</summary>
    private static ImageRef? Side(Dir d, string baseName, params string[] suffixes)
    {
        foreach (var s in suffixes)
            foreach (var ext in ImageExt)
                if (d.Find(baseName + "-" + s + ext) is { } f) return Ref(f);
        return null;
    }

    /// <summary>{name}.jpg</summary>
    private static ImageRef? Named(Dir d, params string[] names)
    {
        foreach (var n in names)
            foreach (var ext in ImageExt)
                if (d.Find(n + ext) is { } f) return Ref(f);
        return null;
    }

    private static ImageRef? Same(Dir d, string baseName) => Named(d, baseName);

    private NfoData? LoadNfo(FileInfo? f) => Sidecar(f, Nfo.Load);
    private VideoInfo? LoadInfo(FileInfo? f) => Sidecar(f, VideoInfo.Load);

    private T? Sidecar<T>(FileInfo? f, Func<string, T?> load) where T : class
    {
        if (f == null) return null;
        lock (_sidecars)
        {
            if (_sidecars.TryGetValue(f.FullName, out var c) && c.Mtime == f.LastWriteTimeUtc) return c.Value as T;
        }
        var v = load(f.FullName);
        lock (_sidecars) _sidecars[f.FullName] = (f.LastWriteTimeUtc, v);
        return v;
    }

    private static List<FileInfo> Videos(Dir dir) => dir.Files.Values
        .Where(f => VideoExt.Contains(f.Extension) && !IsExtra(Path.GetFileNameWithoutExtension(f.Name)) && !f.Name.StartsWith('.'))
        .OrderBy(f => f.Name, NaturalComparer.Instance)
        .ToList();

    private static bool IsExtra(string baseName) =>
        baseName.Equals("sample", StringComparison.OrdinalIgnoreCase) ||
        ExtraSuffixes.Any(s => baseName.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    private static bool Skip(DirectoryInfo d) => d.Name.StartsWith('.') || SkipDirs.Contains(d.Name);

    private IEnumerable<Dir> Walk(string path)
    {
        var dir = ListDir(path);
        if (dir == null) yield break;
        if (dir.Files.ContainsKey(".ignore") || dir.Files.ContainsKey(".jellylite-ignore")) yield break;
        yield return dir;
        foreach (var sub in dir.SubDirs.OrderBy(s => s.Name, NaturalComparer.Instance))
        {
            if (Skip(sub)) continue;
            foreach (var d in Walk(sub.FullName)) yield return d;
        }
    }

    private static Dir? ListDir(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            var files = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
            var subs = new List<DirectoryInfo>();
            foreach (var e in info.EnumerateFileSystemInfos())
            {
                if (e is FileInfo f) files[f.Name] = f;
                else if (e is DirectoryInfo d) subs.Add(d);
            }
            return new Dir(info, files, subs);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Rel(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
}
