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
