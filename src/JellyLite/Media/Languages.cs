namespace JellyLite.Media;

/// <summary>Language codes: "da"/"danish" -> "dan", and Danish display names for track titles.</summary>
public static class Languages
{
    private static readonly Dictionary<string, (string Code, string Name)> Map = Build();

    private static Dictionary<string, (string, string)> Build()
    {
        var list = new (string Iso3, string[] Aliases, string Name)[]
        {
            ("dan", ["da", "danish", "dansk"], "Dansk"),
            ("eng", ["en", "english", "engelsk"], "Engelsk"),
            ("swe", ["sv", "swedish", "svensk"], "Svensk"),
            ("nor", ["no", "nb", "nn", "nob", "nno", "norwegian", "norsk"], "Norsk"),
            ("fin", ["fi", "finnish", "finsk"], "Finsk"),
            ("isl", ["is", "ice", "icelandic"], "Islandsk"),
            ("ger", ["de", "deu", "german", "tysk"], "Tysk"),
            ("fre", ["fr", "fra", "french", "fransk"], "Fransk"),
            ("spa", ["es", "spanish", "spansk"], "Spansk"),
            ("ita", ["it", "italian", "italiensk"], "Italiensk"),
            ("dut", ["nl", "nld", "dutch", "hollandsk"], "Hollandsk"),
            ("por", ["pt", "portuguese", "portugisisk"], "Portugisisk"),
            ("pol", ["pl", "polish", "polsk"], "Polsk"),
            ("rus", ["ru", "russian", "russisk"], "Russisk"),
            ("jpn", ["ja", "japanese", "japansk"], "Japansk"),
            ("kor", ["ko", "korean", "koreansk"], "Koreansk"),
            ("chi", ["zh", "zho", "chinese", "kinesisk"], "Kinesisk"),
            ("ara", ["ar", "arabic"], "Arabisk"),
            ("tur", ["tr", "turkish"], "Tyrkisk"),
            ("hin", ["hi", "hindi"], "Hindi"),
        };
        var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (iso3, aliases, name) in list)
        {
            map[iso3] = (iso3, name);
            foreach (var a in aliases) map[a] = (iso3, name);
        }
        return map;
    }

    /// <summary>Returns the ISO 639-2 code, or null if the token isn't a known language.</summary>
    public static string? Normalize(string? token) =>
        token != null && Map.TryGetValue(token.Trim(), out var v) ? v.Code : null;

    public static string? DisplayName(string? code) =>
        code == null ? null : Map.TryGetValue(code, out var v) ? v.Name : code.ToUpperInvariant();
}
