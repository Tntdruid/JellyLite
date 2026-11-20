# JellyLite

En lille medieserver, der taler Jellyfins API, så TvFin på Samsung-TV'et kan bruge den. Den omkoder aldrig: filerne sendes direkte til TV'et.

## Opsætning

1. Læg dine medier under `/media` eller `/share`. Netværksdrev (NAS) tilføjes under *Indstillinger → System → Lagring* og dukker op under `/media`.
2. Ret **libraries** i fanen *Konfiguration*:
   - `movies`: film, én fil pr. film (gerne i egen mappe med `.nfo`, `poster.jpg`, `fanart.jpg`).
   - `tvshows`: `Serie/Season 01/Serie - S01E01 - Titel.mkv`.
   - `youtube`: én mappe pr. kanal; yt-dlp's `.info.json` og thumbnails bruges, hvis de findes.
3. Tilføj en bruger pr. person under **users**.
4. Start add-on'et og åbn **JellyLite** i sidepanelet.

## Log ind fra TV'et

I TvFin skriver du Home Assistants IP-adresse (port 8096) og vælger Quick Connect. Koden dukker op på JellyLite-siden i Home Assistant. Vælg bruger og tryk *Godkend*.

## NAS der slukker

Er NAS'en slukket (eller netværkslageret ikke monteret), beholder JellyLite det sidst kendte indhold, så bibliotekerne ikke bliver tomme. Afspilning virker først igen, når NAS'en er tilbage. Genstartes add-on'et, mens NAS'en er slukket, er bibliotekerne tomme, indtil den er tilbage.

## admin_password

Admin-siden virker altid via Home Assistant-sidepanelet. Sæt `admin_password`, hvis du også vil kunne åbne `http://<ha-ip>:8096/` direkte fra en anden computer.
