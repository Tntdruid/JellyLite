using JellyLite.Library;

namespace JellyLite.Api;

/// <summary>The /Items query: parent, recursion, types, filters, search, people, studios, sorting and paging.</summary>
public static class ItemQuery
{
    public static List<string> Split(string? s) =>
        string.IsNullOrWhiteSpace(s) ? [] : s.Split([',', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static bool Bool(string? s) => s != null && s.Equals("true", StringComparison.OrdinalIgnoreCase);

    public static (List<Item> Items, int Total, int Start) Run(IQueryCollection q, DtoContext c)
    {
        var lib = c.Lib;
        IEnumerable<Item> src;
        var parentId = Ids.Parse(q["ParentId"]);
        var recursive = Bool(q["Recursive"]);
        var ids = Split(q["Ids"]).Select(Ids.Parse).OfType<Guid>().ToList();
        Item? parent = parentId is Guid p ? lib.Get(p) : null;

        if (ids.Count > 0) src = ids.Select(lib.Get).OfType<Item>();
        else if (parentId != null) src = parent == null ? [] : recursive ? lib.Descendants(parent) : lib.ChildrenOf(parent);
        else src = recursive ? lib.Items.Values.Where(i => i.Type != ItemTypes.Library) : lib.Libraries;

        var include = new HashSet<string>(Split(q["IncludeItemTypes"]), StringComparer.OrdinalIgnoreCase);
        // MusicVideo/BoxSet etc. don't exist here; asking only for those gives nothing
        if (include.Count > 0) src = src.Where(i => include.Contains(i.Type));
        var exclude = new HashSet<string>(Split(q["ExcludeItemTypes"]), StringComparer.OrdinalIgnoreCase);
        if (exclude.Count > 0) src = src.Where(i => !exclude.Contains(i.Type));
        if (Split(q["MediaTypes"]).Any(m => m.Equals("Video", StringComparison.OrdinalIgnoreCase))) src = src.Where(i => i.IsPlayable);

        foreach (var f in Split(q["Filters"]))
        {
            src = f.ToLowerInvariant() switch
            {
                "isfavorite" or "isfavoriteorlikes" => src.Where(i => c.Ud(i.Id).Favorite),
                "isplayed" => src.Where(i => IsPlayed(i, c)),
                "isunplayed" => src.Where(i => !IsPlayed(i, c)),
                "isresumable" => src.Where(i => i.IsPlayable && c.Ud(i.Id).Position > 0),
                "isfolder" => src.Where(i => i.IsFolder),
                "isnotfolder" => src.Where(i => !i.IsFolder),
                _ => src,
            };
        }
        if (q["IsPlayed"].FirstOrDefault() is { } ip) src = src.Where(i => IsPlayed(i, c) == Bool(ip));
        if (q["IsFavorite"].FirstOrDefault() is { } isf) src = src.Where(i => c.Ud(i.Id).Favorite == Bool(isf));

        if (q["searchTerm"].FirstOrDefault() is { Length: > 0 } term)
        {
            var t = Fold(term);
            src = src.Where(i => Fold(i.Name).Contains(t) || (i.OriginalTitle != null && Fold(i.OriginalTitle).Contains(t)));
        }

        var personIds = Split(q["PersonIds"]).Select(Ids.Parse).OfType<Guid>().ToList();
        if (personIds.Count > 0)
        {
            var items = personIds.Select(id => lib.People.GetValueOrDefault(id)).OfType<Person>().SelectMany(pp => pp.ItemIds).ToHashSet();
            src = src.Where(i => items.Contains(i.Id));
        }

        var studioIds = Split(q["StudioIds"]).Select(Ids.Parse).OfType<Guid>().ToList();
        if (studioIds.Count > 0)
        {
            var names = studioIds.Select(id => lib.Studios.GetValueOrDefault(id)?.Name).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            src = src.Where(i => i.Studios.Any(names.Contains));
        }

        var genres = Split(q["Genres"]);
        if (genres.Count > 0) src = src.Where(i => i.Genres.Any(g => genres.Contains(g, StringComparer.OrdinalIgnoreCase)));

        var years = Split(q["Years"]).Select(y => int.TryParse(y, out var v) ? v : (int?)null).OfType<int>().ToHashSet();
        if (years.Count > 0) src = src.Where(i => i.ProductionYear is int y && years.Contains(y));

        var list = Sort(src.ToList(), q, parent, c, keepOrder: ids.Count > 0);
        var total = list.Count;
        var start = int.TryParse(q["StartIndex"], out var si) && si > 0 ? si : 0;
        var limit = int.TryParse(q["Limit"], out var li) && li > 0 ? li : int.MaxValue;
        return (list.Skip(start).Take(limit).ToList(), total, start);
    }

    public static bool IsPlayed(Item i, DtoContext c)
    {
        if (i.IsPlayable) return c.Ud(i.Id).Played;
        var eps = c.Lib.Descendants(i).Where(e => e.IsPlayable).ToList();
        return eps.Count > 0 && eps.All(e => c.Ud(e.Id).Played);
    }

    /// <summary>Lowercase without accents, so "pokemon" finds "Pokémon".</summary>
    public static string Fold(string s)
    {
        var norm = s.Normalize(System.Text.NormalizationForm.FormD);
        var chars = norm.Where(ch => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark);
        return new string(chars.ToArray()).ToLowerInvariant();
    }

    public static List<Item> Sort(List<Item> items, IQueryCollection q, Item? parent, DtoContext c, bool keepOrder = false)
    {
        var keys = Split(q["SortBy"]);
        var orders = Split(q["SortOrder"]);
        if (keys.Count == 0)
        {
            if (keepOrder) return items;
            keys = parent?.Type is ItemTypes.Series or ItemTypes.Season ? ["ParentIndexNumber", "IndexNumber", "SortName"] : ["SortName"];
        }
        var random = keys.Any(k => k.Equals("Random", StringComparison.OrdinalIgnoreCase))
            ? items.ToDictionary(i => i.Id, _ => Random.Shared.Next()) : null;

        var comparisons = new List<Comparison<Item>>();
        for (var k = 0; k < keys.Count; k++)
        {
            var desc = (orders.Count > k ? orders[k] : orders.LastOrDefault())?.StartsWith("Desc", StringComparison.OrdinalIgnoreCase) == true;
            Comparison<Item>? cmp = keys[k].ToLowerInvariant() switch
            {
                "sortname" or "name" => (a, b) => NaturalComparer.Instance.Compare(a.SortName, b.SortName),
                "datecreated" => (a, b) => Latest(a).CompareTo(Latest(b)),
                "premieredate" => (a, b) => Nullable.Compare(Premiere(a), Premiere(b)),
                "productionyear" => (a, b) => Nullable.Compare(a.ProductionYear, b.ProductionYear),
                "communityrating" => (a, b) => Nullable.Compare(a.CommunityRating, b.CommunityRating),
                "dateplayed" or "datelastplayed" => (a, b) => Nullable.Compare(LastPlayed(a, c), LastPlayed(b, c)),
                "random" => (a, b) => random![a.Id].CompareTo(random[b.Id]),
                "parentindexnumber" => (a, b) => (a.ParentIndexNumber ?? 0).CompareTo(b.ParentIndexNumber ?? 0),
                "indexnumber" => (a, b) => (a.IndexNumber ?? int.MaxValue).CompareTo(b.IndexNumber ?? int.MaxValue),
                "runtime" => (a, b) => Nullable.Compare(a.RunTimeTicks, b.RunTimeTicks),
                "playcount" => (a, b) => c.Ud(a.Id).PlayCount.CompareTo(c.Ud(b.Id).PlayCount),
                _ => null,
            };
            if (cmp == null) continue;
            comparisons.Add(desc ? (a, b) => cmp(b, a) : cmp);
        }
        comparisons.Add((a, b) => NaturalComparer.Instance.Compare(a.SortName, b.SortName));
        return items.OrderBy(i => i, Comparer<Item>.Create((a, b) =>
        {
            foreach (var cmp in comparisons)
            {
                var r = cmp(a, b);
                if (r != 0) return r;
            }
            return 0;
        })).ToList();
    }

    /// <summary>Series sort by their newest episode, so "date added" shows series with new episodes first.</summary>
    private static DateTime Latest(Item i) => i.DateLastContentAdded ?? i.DateCreated;

    private static DateTime? Premiere(Item i) => i.PremiereDate ?? (i.ProductionYear is int y ? new DateTime(y, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null);

    private static DateTime? LastPlayed(Item i, DtoContext c)
    {
        if (i.IsPlayable) return c.Ud(i.Id).LastPlayed;
        return c.Lib.Descendants(i).Where(e => e.IsPlayable).Select(e => c.Ud(e.Id).LastPlayed).Max();
    }
}
