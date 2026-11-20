using JellyLite.Api;
using JellyLite.Storage;
using JellyLite.Library;
using JellyLite.Media;

namespace JellyLite.Tests;

public sealed class FilenameTests
{
    [Theory]
    [InlineData("Show - S01E02 - Title", 1, 2, null, "Title")]
    [InlineData("show.s03e10.720p", 3, 10, null, "720p")]
    [InlineData("Show S01E01-E02", 1, 1, 2, null)]
    [InlineData("Show 2x05 Something", 2, 5, null, "Something")]
    [InlineData("Afsnit 7", null, 7, null, null)]
    [InlineData("s2024.e0101 - Video title", 2024, 101, null, "Video title")]
    public void Episodes(string name, int? season, int? ep, int? epEnd, string? title)
    {
        var r = FilenameParser.ParseEpisode(name);
        Assert.Equal(season, r.Season);
        Assert.Equal(ep, r.Episode);
        Assert.Equal(epEnd, r.EpisodeEnd);
        Assert.Equal(title, r.Title);
    }

    [Theory]
    [InlineData("Inception (2010)", "Inception", 2010)]
    [InlineData("Movie.Name.2015.1080p.BluRay.x264", "Movie Name", 2015)]
    [InlineData("2001 A Space Odyssey (1968)", "2001 A Space Odyssey", 1968)]
    [InlineData("Blade Runner 2049 (2017) [1080p]", "Blade Runner 2049", 2017)]
    [InlineData("Just A Title", "Just A Title", null)]
    public void Movies(string name, string title, int? year)
    {
        var (t, y) = FilenameParser.ParseMovie(name);
        Assert.Equal(title, t);
        Assert.Equal(year, y);
    }

    [Theory]
    [InlineData("Season 1", 1)]
    [InlineData("Sæson 02", 2)]
    [InlineData("S03", 3)]
    [InlineData("Specials", 0)]
    [InlineData("Random", null)]
    public void SeasonFolders(string name, int? expected) => Assert.Equal(expected, FilenameParser.ParseSeasonFolder(name));

    [Fact]
    public void YouTube_titles_lose_date_and_id()
    {
        Assert.Equal("My Video", FilenameParser.CleanVideoTitle("20240101 - My Video [abcdefghijk]"));
        Assert.Equal("abcdefghijk", FilenameParser.YoutubeId("x [abcdefghijk]"));
        Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), FilenameParser.LeadingDateOf("20240101 - x"));
    }

    [Fact]
    public void Natural_sort()
    {
        var list = new List<string> { "episode 10", "episode 2", "Episode 1" };
        list.Sort(NaturalComparer.Instance);
        Assert.Equal(["Episode 1", "episode 2", "episode 10"], list);
    }

    [Fact]
    public void Subtitle_files_get_language_and_flags()
    {
        var subs = Scanner.FindSubtitlesIn(["Movie.mkv", "Movie.srt", "Movie.da.srt", "Movie.en.forced.srt", "Movie 2.srt", "Movie.nfo"], "/x", "Movie").ToList();
        Assert.Equal(3, subs.Count);
        Assert.Contains(subs, s => s.Language == null && s.Path.EndsWith("Movie.srt"));
        Assert.Contains(subs, s => s.Language == "dan" && !s.IsForced);
        Assert.Contains(subs, s => s.Language == "eng" && s.IsForced);
    }
}

public sealed class MetadataTests
{
    [Fact]
    public void Nfo_with_url_after_xml_and_ratings_block()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            <movie><title>X</title><ratings><rating name="imdb"><value>6,5</value></rating><rating name="tmdb" default="true"><value>7.1</value></rating></ratings>
            <actor><name>A</name><profile>https://www.themoviedb.org/person/42</profile></actor><premiered>2001-02-03</premiered></movie>
            https://www.imdb.com/title/tt123/
            """);
        var n = Nfo.Load(path)!;
        File.Delete(path);
        Assert.Equal("X", n.Title);
        Assert.Equal(7.1f, n.Rating);
        Assert.Equal(2001, n.Year);
        Assert.Equal("42", n.People[0].TmdbId);
    }

    [Fact]
    public void Person_without_id_joins_the_only_person_with_that_name()
    {
        var snap = new LibrarySnapshot();
        Item Movie(string key, params RawPerson[] people)
        {
            var it = new Item { Id = Ids.From(key), Type = ItemTypes.Movie, Name = key, RawPeople = people.ToList() };
            snap.Items[it.Id] = it;
            return it;
        }
        Movie("a", new RawPerson("Mads Mikkelsen", null, "Actor", "1019", null, null));
        Movie("b", new RawPerson("Mads Mikkelsen", null, "Actor", null, null, null));
        Movie("c", new RawPerson("John Smith", null, "Actor", "1", null, null));
        Movie("d", new RawPerson("John Smith", null, "Actor", "2", null, null));
        Movie("e", new RawPerson("John Smith", null, "Actor", null, null, null));
        Scanner.ResolvePeople(snap);

        Assert.Single(snap.People.Values, p => p.Name == "Mads Mikkelsen");
        // Ambiguous name without id: kept apart from both
        Assert.Equal(3, snap.People.Values.Count(p => p.Name == "John Smith"));
    }

    [Fact]
    public void Ffprobe_output_is_parsed_and_cover_art_skipped()
    {
        var info = Prober.Parse("""
            {"streams":[
              {"index":0,"codec_type":"video","codec_name":"hevc","width":1920,"height":1080,"avg_frame_rate":"24000/1001","disposition":{"default":1}},
              {"index":1,"codec_type":"audio","codec_name":"eac3","channels":6,"channel_layout":"5.1","tags":{"language":"dan"},"disposition":{"default":1}},
              {"index":2,"codec_type":"subtitle","codec_name":"subrip","tags":{"language":"eng"},"disposition":{"forced":1}},
              {"index":3,"codec_type":"video","codec_name":"mjpeg","disposition":{"attached_pic":1}}],
             "format":{"format_name":"matroska,webm","duration":"2700.5","bit_rate":"8000000"},
             "chapters":[{"start_time":"0.000","tags":{"title":"Intro"}},{"start_time":"90.0","tags":{"title":"Chapter 2"}}]}
            """);
        Assert.Equal(3, info.Streams.Count);
        Assert.Equal(27005000000, info.RunTimeTicks);
        Assert.Equal(23.976f, info.Streams[0].FrameRate);
        Assert.True(info.Streams[2].IsForced);

        var it = new Item { Id = Guid.NewGuid(), Type = ItemTypes.Episode, Name = "x", Media = info };
        var seg = Assert.Single(Playstate.Segments(it));
        Assert.Equal("Intro", seg["Type"]);
        Assert.Equal(900000000L, seg["EndTicks"]);
    }
}

public sealed class SubtitleTests
{
    [Fact]
    public void Srt_becomes_vtt()
    {
        var vtt = SubtitleService.SrtToVtt("1\r\n00:00:01,000 --> 00:00:02,500\r\nHej\r\n\r\n2\r\n01:02:03,004 --> 01:02:05,000\r\nIgen");
        Assert.StartsWith("WEBVTT\n\n", vtt);
        Assert.Contains("00:00:01.000 --> 00:00:02.500", vtt);
        Assert.Contains("01:02:03.004 --> 01:02:05.000", vtt);
        Assert.DoesNotContain("\r", vtt);
    }

    [Fact]
    public void Latin1_srt_keeps_danish_letters()
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, System.Text.Encoding.Latin1.GetBytes("Blåbærgrød"));
        Assert.Equal("Blåbærgrød", SubtitleService.ReadText(path));
        File.Delete(path);
    }
}

public sealed class PlaystateTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Thresholds()
    {
        var ud = new UserData();
        Playstate.Report(ud, 30, 1000, stopped: true, Now);      // 3%: not started
        Assert.Equal(0, ud.Position);
        Playstate.Report(ud, 500, 1000, stopped: true, Now);     // 50%: resume
        Assert.Equal(500, ud.Position);
        Assert.False(ud.Played);
        Playstate.Report(ud, 950, 1000, stopped: false, Now);    // 95% during progress: watched
        Assert.True(ud.Played);
        Playstate.Report(ud, 990, 1000, stopped: true, Now);     // the stop right after doesn't count twice
        Assert.Equal(1, ud.PlayCount);
        Assert.Equal(0, ud.Position);
    }
}
