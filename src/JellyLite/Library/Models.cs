using JellyLite.Media;

namespace JellyLite.Library;

public static class ItemTypes
{
    public const string Library = "CollectionFolder";
    public const string Movie = "Movie";
    public const string Series = "Series";
    public const string Season = "Season";
    public const string Episode = "Episode";
    public const string Video = "Video";
}

/// <summary>An image file (or remote URL) with a tag that changes when the file changes.</summary>
public sealed record ImageRef(string Path, string Tag)
{
    public bool IsRemote => Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}

public sealed record ExternalSub(string Path, string? Language, bool IsForced, bool IsDefault, string Format);

/// <summary>A person as written in a metadata file, before people are matched up across the library.</summary>
public sealed record RawPerson(string Name, string? Role, string Type, string? TmdbId, string? ImdbId, string? Thumb);

public sealed record PersonRef(Guid Id, string Name, string? Role, string Type);

public sealed class Person
{
    public required Guid Id { get; init; }
    public required string Name { get; set; }
    public string? TmdbId { get; set; }
    public string? ImdbId { get; set; }
    public ImageRef? Image { get; set; }
    /// <summary>Items the person appears in. A series is included when the person is only in some of its episodes.</summary>
    public HashSet<Guid> ItemIds { get; } = [];
}

public sealed class Studio
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public ImageRef? Image { get; set; }
}

public sealed class Item
{
    public required Guid Id { get; init; }
    public required string Type { get; init; }
    public required string Name { get; set; }
    public string SortName { get; set; } = "";
    public string? OriginalTitle { get; set; }
    public string? Path { get; init; }
    public Guid? ParentId { get; set; }
    public Guid LibraryId { get; set; }
    /// <summary>For libraries: movies | tvshows | homevideos (YouTube).</summary>
    public string? CollectionType { get; set; }

    public Guid? SeriesId { get; set; }
    public string? SeriesName { get; set; }
    public Guid? SeasonId { get; set; }
    public string? SeasonName { get; set; }
    public int? IndexNumber { get; set; }
    public int? IndexNumberEnd { get; set; }
    public int? ParentIndexNumber { get; set; }

    public int? ProductionYear { get; set; }
    public DateTime? PremiereDate { get; set; }
    public DateTime DateCreated { get; set; }
    public DateTime? DateLastContentAdded { get; set; }
    public string? Overview { get; set; }
    public string? Tagline { get; set; }
    public string? OfficialRating { get; set; }
    public float? CommunityRating { get; set; }
    public List<string> Genres { get; set; } = [];
    public List<string> Studios { get; set; } = [];
    public List<RawPerson> RawPeople { get; set; } = [];
    public List<PersonRef> People { get; set; } = [];
    public Dictionary<string, string> ProviderIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Primary, Thumb, Banner, Logo</summary>
    public Dictionary<string, ImageRef> Images { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ImageRef> Backdrops { get; } = [];
    public List<ExternalSub> ExternalSubs { get; } = [];
    public List<Guid> Children { get; } = [];

    public long Size { get; set; }
    public DateTime Mtime { get; set; }
    public long? MetadataRunTimeTicks { get; set; }
    /// <summary>Set by the prober (ffprobe) in the background.</summary>
    public MediaInfo? Media { get; set; }

    /// <summary>Library whose folder couldn't be reached at the last scan (e.g. NAS switched off).</summary>
    public bool Offline { get; set; }
    /// <summary>Taken over unchanged from the previous scan because its library was offline.</summary>
    public bool KeptFromPreviousScan { get; set; }

    public bool IsFolder => Type is ItemTypes.Library or ItemTypes.Series or ItemTypes.Season;
    public bool IsPlayable => Type is ItemTypes.Movie or ItemTypes.Episode or ItemTypes.Video;
    public long? RunTimeTicks => Media?.RunTimeTicks ?? MetadataRunTimeTicks;
}

/// <summary>An immutable view of all libraries. A rescan builds a new one and swaps it in.</summary>
public sealed class LibrarySnapshot
{
    public List<Item> Libraries { get; } = [];
    public Dictionary<Guid, Item> Items { get; } = [];
    public Dictionary<Guid, Person> People { get; } = [];
    public Dictionary<Guid, Studio> Studios { get; } = [];
    public DateTime ScannedAt { get; init; } = DateTime.UtcNow;

    public Item? Get(Guid id) => Items.GetValueOrDefault(id);

    public IEnumerable<Item> ChildrenOf(Item item)
    {
        foreach (var id in item.Children)
            if (Items.TryGetValue(id, out var c)) yield return c;
    }

    public IEnumerable<Item> Descendants(Item item)
    {
        foreach (var c in ChildrenOf(item))
        {
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    /// <summary>Episodes of a series in watch order (specials last).</summary>
    public List<Item> EpisodesOf(Item series) => Descendants(series)
        .Where(e => e.Type == ItemTypes.Episode)
        .OrderBy(e => e.ParentIndexNumber is 0 ? int.MaxValue : e.ParentIndexNumber ?? 1)
        .ThenBy(e => e.IndexNumber ?? int.MaxValue)
        .ThenBy(e => e.SortName, NaturalComparer.Instance)
        .ToList();
}

/// <summary>Case-insensitive compare where "Episode 2" comes before "Episode 10".</summary>
public sealed class NaturalComparer : IComparer<string?>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? a, string? b)
    {
        if (a == null || b == null) return a == null ? (b == null ? 0 : -1) : 1;
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var na = a[si..i].TrimStart('0');
                var nb = b[sj..j].TrimStart('0');
                if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                var c = string.CompareOrdinal(na, nb);
                if (c != 0) return c;
            }
            else
            {
                var c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                if (c != 0) return c;
                i++; j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}
