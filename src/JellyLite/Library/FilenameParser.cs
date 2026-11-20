using System.Globalization;
using System.Text.RegularExpressions;

namespace JellyLite.Library;

public sealed record EpisodeName(int? Season, int? Episode, int? EpisodeEnd, string? Title);

public static partial class FilenameParser
{
    [GeneratedRegex(@"(?<![a-z0-9])s(?<s>\d{1,4})[ ._-]*e(?<e>\d{1,4})(?:[ ._-]*-?[ ._-]*e(?<e2>\d{1,4}))?", RegexOptions.IgnoreCase)]
    private static partial Regex SxE();

    [GeneratedRegex(@"(?<![0-9a-z])(?<s>\d{1,2})x(?<e>\d{1,3})(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex XFormat();

    [GeneratedRegex(@"(?:^|[ ._-])(?:ep|episode|afsnit|e)[ ._-]?(?<e>\d{1,4})(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeOnly();

    [GeneratedRegex(@"^(?:season|s[æa]son|series|serie|staffel|saison|temporada|s)[ ._-]*(?<n>\d{1,4})$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFolder();

    [GeneratedRegex(@"(?<=[ ._\(\[-])(?<open>[\(\[])?(?<year>(?:19|20)\d{2})(?<close>[\)\]])?(?=$|[ ._\)\]\[\(-])")]
    private static partial Regex YearToken();

    [GeneratedRegex(@"\[(?<id>[A-Za-z0-9_-]{11})\]")]
    private static partial Regex YoutubeIdRx();

    [GeneratedRegex(@"\[[^\]]*\]|\{[^}]*\}")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"^(?<d>(?:19|20)\d{2}-?[01]\d-?[0-3]\d)(?:[ ._-]+|$)")]
    private static partial Regex LeadingDate();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^(?:the|a|an)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex Article();

    public static EpisodeName ParseEpisode(string name)
    {
        var m = SxE().Match(name);
        if (m.Success)
            return new(Int(m.Groups["s"].Value), Int(m.Groups["e"].Value),
                m.Groups["e2"].Success ? Int(m.Groups["e2"].Value) : null, TitleAfter(name, m));
        m = XFormat().Match(name);
        if (m.Success) return new(Int(m.Groups["s"].Value), Int(m.Groups["e"].Value), null, TitleAfter(name, m));
        m = EpisodeOnly().Match(name);
        if (m.Success) return new(null, Int(m.Groups["e"].Value), null, TitleAfter(name, m));
        return new(null, null, null, null);
    }

    private static string? TitleAfter(string name, Match m)
    {
        var t = CleanTitle(name[(m.Index + m.Length)..]);
        return t.Length > 0 ? t : null;
    }

    /// <summary>"Season 01", "Sæson 2", "S03", "Specials" -> season number.</summary>
    public static int? ParseSeasonFolder(string folderName)
    {
        if (folderName.Equals("specials", StringComparison.OrdinalIgnoreCase) || folderName.Equals("extras", StringComparison.OrdinalIgnoreCase))
            return 0;
        var m = SeasonFolder().Match(folderName.Trim());
        return m.Success ? Int(m.Groups["n"].Value) : null;
    }

    /// <summary>"Inception (2010)", "Movie.Name.2015.1080p.BluRay" -> title + year.</summary>
    public static (string Title, int? Year) ParseMovie(string name)
    {
        var s = Normalize(name);
        // The year is the last one in brackets, else the last bare one ("Blade Runner 2049 (2017)", "Movie.2015.1080p")
        var years = YearToken().Matches(s).Where(m => m.Index > 0).ToList();
        var m = years.LastOrDefault(x => x.Groups["open"].Success && x.Groups["close"].Success) ?? years.LastOrDefault();
        if (m != null)
        {
            var title = CleanTitle(s[..m.Index]);
            if (title.Length > 0) return (title, Int(m.Groups["year"].Value));
        }
        return (CleanTitle(s), null);
    }

    public static string? YoutubeId(string name)
    {
        var m = YoutubeIdRx().Match(name);
        return m.Success ? m.Groups["id"].Value : null;
    }

    public static DateTime? LeadingDateOf(string name)
    {
        var m = LeadingDate().Match(name);
        if (!m.Success) return null;
        var d = m.Groups["d"].Value.Replace("-", "");
        return DateTime.TryParseExact(d, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var r) ? r : null;
    }

    /// <summary>A YouTube file name without id, date prefix or ytdl-sub episode prefix.</summary>
    public static string CleanVideoTitle(string name)
    {
        var s = YoutubeIdRx().Replace(name, "");
        s = LeadingDate().Replace(s, "");
        var ep = SxE().Match(s);
        if (ep.Success && ep.Index == 0) s = s[ep.Length..];
        var t = CleanTitle(s);
        return t.Length > 0 ? t : name;
    }

    public static string CleanTitle(string s)
    {
        s = Brackets().Replace(Normalize(s), " ");
        s = Spaces().Replace(s, " ");
        return s.Trim(' ', '-', '.', '_', '(', ')');
    }

    /// <summary>Dots and underscores become spaces in release-style names ("Movie.Name.2015").</summary>
    private static string Normalize(string s)
    {
        s = s.Replace('_', ' ');
        if (!s.Contains(' ')) s = s.Replace('.', ' ');
        return s;
    }

    public static string SortName(string name) => Article().Replace(name.Trim(), "").ToLowerInvariant();

    private static int? Int(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
}
