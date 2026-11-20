using System.Text.RegularExpressions;
using JellyLite.Data;
using JellyLite.Library;

namespace JellyLite.Api;

/// <summary>Watch-state rules (same thresholds as Jellyfin: under 5% = not started, from 90% = watched).</summary>
public static partial class Playstate
{
    public const double MinResume = 0.05, MaxResume = 0.90;

    /// <summary>Progress and stop reports. Progress also saves, so turning the TV off still leaves a resume point.</summary>
    public static void Report(UserData ud, long? position, long? runtime, bool stopped, DateTime now)
    {
        ud.LastPlayed = now;
        if (position is not long p) return;
        if (runtime is long rt && rt > 0)
        {
            var pct = (double)p / rt;
            if (pct >= MaxResume)
            {
                // Already counted by an earlier progress report in this playback
                if (!(ud.Played && ud.Position == 0)) MarkPlayed(ud, now, count: true);
                return;
            }
            if (pct < MinResume)
            {
                if (stopped) ud.Position = 0;
                return;
            }
        }
        ud.Position = p;
    }

    public static void MarkPlayed(UserData ud, DateTime now, bool count)
    {
        if (count || !ud.Played) ud.PlayCount++;
        ud.Played = true;
        ud.Position = 0;
        ud.LastPlayed = now;
    }

    public static void MarkUnplayed(UserData ud)
    {
        ud.Played = false;
        ud.Position = 0;
    }

    [GeneratedRegex(@"\b(intro|opening|op|title sequence)\b", RegexOptions.IgnoreCase)]
    private static partial Regex IntroRx();
    [GeneratedRegex(@"\b(recap|previously|tidligere)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RecapRx();
    [GeneratedRegex(@"\b(credits|ending|outro|ed|end credits|rulletekster)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OutroRx();
    [GeneratedRegex(@"\b(preview|next time|næste gang)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PreviewRx();

    /// <summary>Skip markers from chapter names ("Intro", "Recap", "Credits", "Preview").</summary>
    public static List<Dictionary<string, object?>> Segments(Item it)
    {
        var chapters = it.Media?.Chapters ?? [];
        var list = new List<Dictionary<string, object?>>();
        for (var i = 0; i < chapters.Count; i++)
        {
            var name = chapters[i].Name ?? "";
            var type = IntroRx().IsMatch(name) ? "Intro" : RecapRx().IsMatch(name) ? "Recap" : OutroRx().IsMatch(name) ? "Outro" : PreviewRx().IsMatch(name) ? "Preview" : null;
            if (type == null) continue;
            var end = i + 1 < chapters.Count ? chapters[i + 1].StartTicks : it.RunTimeTicks ?? chapters[i].StartTicks;
            if (end <= chapters[i].StartTicks) continue;
            list.Add(new()
            {
                ["Id"] = Ids.N(Ids.From($"segment:{it.Id}:{i}")),
                ["ItemId"] = Ids.N(it.Id),
                ["Type"] = type,
                ["StartTicks"] = chapters[i].StartTicks,
                ["EndTicks"] = end,
            });
        }
        return list;
    }
}
