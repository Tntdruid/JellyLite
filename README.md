# JellyLite

En lille medieserver, der taler den del af Jellyfins API, som [TvFin](https://github.com/Tntdruid/TvFin) bruger. Den omkoder aldrig: filerne sendes direkte til TV'et (direct play), så den kan køre på en Raspberry Pi eller i Home Assistant.

## Hvad den kan

- **Film, serier og YouTube** fra lokale mapper. Metadata hentes fra `.nfo`, yt-dlp's `.info.json`, billeder og undertekster ved siden af filerne.
- **Skuespillere matches på TMDB/IMDb-id**, så to forskellige personer med samme navn ikke bliver lagt sammen. Gæsteskuespillere i et afsnit vises på deres personside sammen med serien.
- **YouTube-kanaler** vises som kanaler i TvFin (*Gruppér: Kanal*).
- **Undertekster**: `.srt`/`.vtt` ved siden af filen (`Film.da.srt`, `Film.en.forced.srt`) og indlejrede tekst-undertekster (kræver ffmpeg). Billed-undertekster (PGS) understøttes ikke, fordi de kræver omkodning.
- **Fortsæt, næste afsnit, set og favoritter** pr. bruger. "Fortsæt" gemmes løbende, så det også virker, når TV'et bare slukkes.
- **Spring intro over** fra kapitler, der hedder *Intro*, *Recap*, *Credits* eller *Preview*.
- **Quick Connect-login**: TV'et viser en kode, som du godkender på admin-siden.
- **Titelretning**: afsnit med en pladsholdertitel som "Afsnit 11" får originaltitlen fra `.nfo`-filen (samme regel som plugin'et Episode Title Fixer).
- **Import fra Jellyfin**: set, fortsæt-punkter, favoritter og antal visninger hentes pr. bruger fra en Jellyfin-server (admin-siden → *Importér fra Jellyfin*). Brugere foreslås ud fra navnet, og filer matches på slutningen af stien, så det virker, selvom Jellyfin ser NAS'en under en anden mappe. Importen kan gentages uden at overskrive det, der er set i JellyLite siden.
- **API-nøgler** til andre programmer, oprettet på admin-siden. Med en nøgle kan et program bede om en scanning (`POST /Library/Refresh` eller `/Library/Media/Updated`, ligesom i Jellyfin) og læse data for en bruger med `?userId=`.

## Kom i gang (Windows/Linux)

Kræver [.NET 10](https://dotnet.microsoft.com/download) og gerne ffmpeg/ffprobe i PATH.

```bash
dotnet run --project src/JellyLite
```

Første start skriver `data/config.json`. Ret den og genstart:

```json
{
  "server_name": "JellyLite",
  "port": 8096,
  "libraries": [
    { "name": "Film", "path": "D:/Film", "type": "movies" },
    { "name": "Serier", "path": "D:/Serier", "type": "tvshows" },
    { "name": "YouTube", "path": "D:/YouTube", "type": "youtube" }
  ],
  "users": [ { "name": "Stue" }, { "name": "Børneværelset" } ],
  "admin_password": "skift-mig"
}
```

Åbn `http://localhost:8096/` for admin-siden. Peg TvFin på serverens IP og vælg Quick Connect.

| Indstilling | Standard | |
|---|---|---|
| `scan_interval_minutes` | 15 | Hvor tit mapperne scannes (eller tryk *Scan nu*) |
| `ffprobe` / `ffmpeg` | `ffprobe` / `ffmpeg` | Stier; tom = slået fra |
| `admin_password` | – | Uden kode kan admin-siden kun åbnes fra serveren selv (og via Home Assistant) |
| `season_label` | `Sæson` | "Sæson 1" |
| `fix_placeholder_titles` | `true` | "Afsnit 11" → originaltitel fra `.nfo` |
| `placeholder_title_pattern` | se kode | Regex for hvad der tæller som pladsholdertitel |

### Ny video med det samme (AutoYTDownload)

Opret en API-nøgle på admin-siden, og sæt den i `jellyfin.conf`:

```bash
JELLYFIN_URL=http://<jellylite-ip>:8096
JELLYFIN_API_KEY=<nøglen>
```

`JELLYFIN_PATH` kan blive stående eller slettes: JellyLite scanner hele biblioteket ved hver forespørgsel.

### Statistik (Jellyfin Video Stats)

[Jellyfin Video Stats](https://github.com/Tntdruid/jellyfin-video-stats) virker med JellyLite: sæt dens Jellyfin-adresse til JellyLite og brug en API-nøgle fra JellyLites admin-side. JellyLite leverer de kald, den bruger: aktive afspilninger (`/Sessions`, ud fra klienternes afspilningsrapporter; en afspilning uden rapport i et minut regnes som stoppet), brugere (`/Users`), titler med `Path`, afsnit med set-status og billeder. Playback Reporting-importen findes ikke i JellyLite og springes over.

Datamappen er `./data`, eller `$JELLYLITE_DATA`. Konfigurationen kan også peges ud med `$JELLYLITE_CONFIG`.

## Mappestruktur

```
Film/Inception (2010)/Inception (2010).mkv  (+ .nfo, poster.jpg, fanart.jpg, .da.srt)
Serier/Serienavn/Season 01/Serienavn - S01E01 - Titel.mkv  (+ tvshow.nfo, poster.jpg)
YouTube/Kanalnavn/20240101 - Titel [videoid].mp4  (+ .info.json, .jpg; poster.jpg = kanalbillede)
```

Mapper med en `.ignore`-fil springes over, ligesom `extras`, `trailers` og lignende.

## Docker

```bash
docker compose up -d
```

Se [docker-compose.yml](docker-compose.yml). Imaget bygges til `amd64` og `arm64` af GitHub Actions, når der pushes et `v*`-tag.

## Home Assistant

Tilføj dette repo under *Indstillinger → Add-ons → Add-on-butik → ⋮ → Repositories*, og installér **JellyLite**. Se [ha-addon/DOCS.md](ha-addon/DOCS.md).

## Udvikling

```bash
dotnet test
```
