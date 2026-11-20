# Changelog

## 0.1.5

- Virker med Jellyfin Video Stats:
  - `GET /Sessions` viser aktive afspilninger (bruger, titel, position, pause, afspilningsmetode, lyd- og undertekstspor) ud fra klienternes afspilningsrapporter. En afspilning uden rapport i et minut regnes som stoppet.
  - `GET /Users` viser alle brugere for en API-nøgle (et TV-login ser kun sig selv).
  - En API-nøgle uden `userId` kan læse biblioteket (uden set-status). Ændringer af en brugers data kræver stadig en bruger.
  - `Path` medsendes, når der spørges efter det med `Fields=Path`, og seriestatus (Continuing/Ended) læses fra `tvshow.nfo`.

## 0.1.4

- Import fra Jellyfin: set, fortsæt-punkter, favoritter og antal visninger pr. bruger (admin-siden → *Importér fra Jellyfin*). Brugere foreslås ud fra navnet; filer matches på slutningen af stien. Kan gentages uden at overskrive det, der er set i JellyLite siden.
- API-nøgler til andre programmer, oprettet på admin-siden. Virker som Jellyfins nøgler (`MediaBrowser Token=…` eller `Bearer …`), og med `?userId=` på vegne af en bruger.
- `POST /Library/Refresh` og `/Library/Media/Updated` starter en scanning, så f.eks. AutoYTDownload kan få nye videoer vist med det samme.
- Indbygget titelretning: "Afsnit 11", "Episode 11" osv. erstattes af originaltitlen fra `.nfo` (som plugin'et Episode Title Fixer). Kan slås fra med `fix_placeholder_titles`.
- Scanning venter ikke længere på, at ffprobe er færdig med at aflæse filer.
- `server_id` skrives ikke længere ind i `config.json`.

## 0.1.3

- Bibliotekerne har nu et billede i stedet for en grå boks: et billede i selve biblioteksmappen (`folder.jpg`, `poster.jpg`, `backdrop.jpg` …), ellers baggrunden eller thumbnailen fra det nyeste indhold.
- Scener (kapitler) har nu billeder. ffmpeg tager ét billede pr. kapitel første gang og gemmer det i `{data}/cache/chapters`.

## 0.1.2

- Et bibliotek, hvis mappe ikke kan nås (f.eks. en NAS, der er slukket om natten), beholder nu det sidst kendte indhold i stedet for at blive tomt. Det gælder også et tomt mountpoint, når biblioteket før havde indhold. Admin-siden viser "kan ikke nås – viser sidst kendte indhold". Når mappen er tilbage, scannes den som normalt, og set-status og fortsæt-punkter er uændrede.

## 0.1.1

- Rettet: databasekoden manglede i 0.1.0 på GitHub (en `.gitignore`-regel for datamappen ramte også kildemappen `Data` på Windows), så Docker-imaget kunne ikke bygges. Mappen hedder nu `Storage`, så den heller ikke kolliderer med datamappen, når serveren startes fra projektmappen.
- Docker-workflowet bygger nu også arm64-imaget (QEMU).
- Nyt CI-workflow, der bygger og tester fra en ren checkout ved hvert push.

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
