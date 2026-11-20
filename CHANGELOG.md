# Changelog

## 0.1.0

Første version.

- Jellyfin-kompatibelt API til TvFin: Quick Connect, biblioteker, film, serier, YouTube-kanaler, søgning, favoritter, fortsæt, næste afsnit, lignende titler og personsider.
- Direct play med range requests, ingen omkodning.
- Metadata fra `.nfo`, yt-dlp `.info.json`, lokale billeder og `.actors`-mapper.
- Personer matches på TMDB/IMDb-id (ingen sammenblanding af navne).
- Undertekster som WebVTT: eksterne SRT/VTT, plus ASS og indlejrede tekst-undertekster via ffmpeg.
- Spor, varighed og kapitler med ffprobe (cachet); spring-over-markører fra kapitelnavne.
- Admin-side til Quick Connect, scanning og enheder.
- Docker-image (amd64/arm64) og Home Assistant add-on.
