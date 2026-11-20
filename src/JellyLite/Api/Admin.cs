using System.Text.Json;
using JellyLite.Auth;
using JellyLite.Import;
using JellyLite.Storage;
using JellyLite.Library;
using JellyLite.Media;

namespace JellyLite.Api;

/// <summary>Small admin page: approve Quick Connect codes, rescan, see and remove signed-in devices.
/// Served at "/" with relative URLs, so it also works behind Home Assistant ingress.</summary>
public static class Admin
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("").AddEndpointFilter(async (ctx, next) =>
        {
            var cfg = ctx.HttpContext.RequestServices.GetRequiredService<AppConfig>();
            if (AuthMiddleware.IsAdmin(ctx.HttpContext, cfg)) return await next(ctx);
            if (!string.IsNullOrEmpty(cfg.AdminPassword))
            {
                ctx.HttpContext.Response.Headers.WWWAuthenticate = "Basic realm=\"JellyLite\"";
                return Results.Unauthorized();
            }
            return Results.Text("Admin-siden kan kun åbnes fra serveren selv. Sæt admin_password i konfigurationen for at åbne den fra netværket.", statusCode: 403);
        });

        group.MapMethods("/", ["GET", "HEAD"], () => Results.Content(Html, "text/html; charset=utf-8"));
        group.MapGet("/web", () => Results.Redirect("../"));

        group.MapGet("/admin/api/state", (AppConfig cfg, LibraryService ls, QuickConnectStore qc, Users users, Db db, Prober prober) =>
        {
            var lib = ls.Current;
            var names = users.All.ToDictionary(u => u.Id, u => u.Name);
            return new
            {
                serverName = cfg.ServerName,
                version = typeof(Admin).Assembly.GetName().Version?.ToString(3),
                configPath = cfg.LoadedFrom,
                ffprobe = prober.Available,
                scanning = ls.Scanning,
                lastScan = ls.LastScan,
                probePending = ls.ProbePending,
                libraries = lib.Libraries.Select(l => new
                {
                    name = l.Name,
                    type = l.CollectionType,
                    path = l.Path,
                    items = lib.Descendants(l).Count(i => i.IsPlayable),
                    offline = l.Offline,
                }),
                users = users.All.Select(u => new { id = Ids.N(u.Id), name = u.Name }),
                pending = qc.Pending.Select(r => new { code = r.Code, device = r.Client.Device, app = r.Client.Client, version = r.Client.Version, created = r.Created }),
                devices = db.Tokens().Select(t => new { id = t.Id, user = names.GetValueOrDefault(t.UserId, "?"), device = t.DeviceName, app = t.Client, created = t.Created }),
                apiKeys = db.ApiKeys().Select(k => new { id = k.Id, name = k.Name, key = k.Key, created = k.Created }),
            };
        });

        group.MapPost("/admin/api/apikeys", (ApiKeyRequest body, Db db) =>
            string.IsNullOrWhiteSpace(body.Name) ? Results.BadRequest("Navn mangler") : Results.Ok(new { key = db.AddApiKey(body.Name.Trim()) }));

        group.MapDelete("/admin/api/apikeys/{id:long}", (long id, Db db) =>
        {
            db.RemoveApiKey(id);
            return Results.NoContent();
        });

        // Import watch state from Jellyfin: 1) list its users with suggested matches, 2) import
        group.MapPost("/admin/api/jellyfin/users", async (JellyfinRequest body, JellyfinImport import, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Url) || string.IsNullOrWhiteSpace(body.ApiKey)) return Results.BadRequest("Adresse og API-nøgle mangler");
            try
            {
                return Results.Ok(await import.UsersAsync(body.Url.Trim(), body.ApiKey.Trim(), ct));
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or UriFormatException or JsonException)
            {
                return Results.BadRequest("Kunne ikke hente brugere fra Jellyfin: " + e.Message);
            }
        });

        group.MapPost("/admin/api/jellyfin/import", async (JellyfinRequest body, JellyfinImport import, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Url) || string.IsNullOrWhiteSpace(body.ApiKey) || body.Map is not { Count: > 0 })
                return Results.BadRequest("Vælg mindst én bruger at importere");
            try
            {
                return Results.Ok(await import.ImportAsync(body.Url.Trim(), body.ApiKey.Trim(), body.Map, ct));
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or UriFormatException or JsonException)
            {
                return Results.BadRequest("Importen fejlede: " + e.Message);
            }
        });

        group.MapPost("/admin/api/quickconnect", (AuthorizeRequest body, QuickConnectStore qc, Users users) =>
        {
            if (Ids.Parse(body.UserId) is not Guid uid || users.Get(uid) == null) return Results.BadRequest("Ukendt bruger");
            return qc.Authorize(body.Code ?? "", uid) ? Results.Ok() : Results.NotFound("Koden findes ikke (eller er udløbet)");
        });

        group.MapPost("/admin/api/scan", (LibraryService ls) =>
        {
            ls.RequestScan();
            return Results.Accepted();
        });

        group.MapDelete("/admin/api/devices/{id:long}", (long id, Db db) =>
        {
            db.RemoveTokenById(id);
            return Results.NoContent();
        });
    }

    public sealed record AuthorizeRequest(string? Code, string? UserId);
    public sealed record ApiKeyRequest(string? Name);
    /// <summary>Map: Jellyfin user id -> JellyLite user id (users left out are skipped).</summary>
    public sealed record JellyfinRequest(string? Url, string? ApiKey, Dictionary<string, string>? Map);

    private const string Html = """
<!doctype html>
<html lang="da">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>JellyLite</title>
<style>
  :root { color-scheme: light dark; --bg:#f6f7f9; --card:#fff; --text:#1d2330; --muted:#677083; --line:#e2e5ea; --accent:#00a4dc; --danger:#c4314b; }
  @media (prefers-color-scheme: dark) { :root { --bg:#101218; --card:#1a1d25; --text:#e8eaf0; --muted:#9aa3b5; --line:#2b2f3a; } }
  * { box-sizing: border-box; }
  body { margin:0; font:15px/1.5 system-ui, sans-serif; background:var(--bg); color:var(--text); }
  main { max-width:820px; margin:0 auto; padding:24px 16px 48px; }
  h1 { font-size:22px; margin:0 0 4px; } h2 { font-size:16px; margin:0 0 12px; }
  .muted { color:var(--muted); }
  section { background:var(--card); border:1px solid var(--line); border-radius:10px; padding:16px; margin-top:16px; }
  table { width:100%; border-collapse:collapse; } td, th { text-align:left; padding:6px 4px; border-top:1px solid var(--line); vertical-align:middle; }
  th { font-weight:600; color:var(--muted); font-size:13px; border-top:0; }
  .code { font:600 22px/1 ui-monospace, monospace; letter-spacing:3px; }
  button, select { font:inherit; padding:6px 12px; border-radius:6px; border:1px solid var(--line); background:var(--card); color:var(--text); cursor:pointer; }
  button.primary { background:var(--accent); border-color:var(--accent); color:#fff; }
  button.danger { color:var(--danger); }
  .row { display:flex; gap:8px; align-items:center; flex-wrap:wrap; }
  .warn { color:var(--danger); }
  .empty { color:var(--muted); padding:4px 0; }
</style>
</head>
<body>
<main>
  <h1 id="title">JellyLite</h1>
  <div class="muted" id="sub"></div>

  <section>
    <h2>Log ind fra TV (Quick Connect)</h2>
    <div id="pending" class="empty">Ingen ventende koder. Vælg "Quick Connect" i TvFin, så dukker koden op her.</div>
  </section>

  <section>
    <div class="row" style="justify-content:space-between"><h2 style="margin:0">Biblioteker</h2><button id="scan">Scan nu</button></div>
    <div id="scanstate" class="muted" style="margin:6px 0 8px"></div>
    <table><thead><tr><th>Navn</th><th>Type</th><th>Mappe</th><th>Videoer</th></tr></thead><tbody id="libs"></tbody></table>
  </section>

  <section>
    <h2>Logget ind</h2>
    <table><thead><tr><th>Bruger</th><th>Enhed</th><th>App</th><th>Siden</th><th></th></tr></thead><tbody id="devices"></tbody></table>
  </section>

  <section>
    <h2>API-nøgler</h2>
    <p class="muted" style="margin-top:0">Til andre programmer, f.eks. AutoYTDownload (scan efter nye videoer). Bruges som Jellyfins API-nøgler.</p>
    <table><thead><tr><th>Navn</th><th>Nøgle</th><th>Oprettet</th><th></th></tr></thead><tbody id="apikeys"></tbody></table>
    <div class="row" style="margin-top:10px"><input id="keyname" placeholder="Navn, f.eks. AutoYTDownload" style="font:inherit;padding:6px 10px;border-radius:6px;border:1px solid var(--line);background:var(--card);color:var(--text);flex:1;min-width:0">
      <button id="addkey" class="primary">Opret nøgle</button></div>
  </section>

  <section>
    <h2>Importér fra Jellyfin</h2>
    <p class="muted" style="margin-top:0">Henter set, fortsæt-punkter, favoritter og antal visninger. Filerne matches på slutningen af stien,
      så det virker, selvom Jellyfin ser NAS'en et andet sted. Kan køres igen: intet, I har set i JellyLite, bliver overskrevet.</p>
    <div class="row">
      <input id="jfurl" placeholder="http://10.10.1.x:8096" style="font:inherit;padding:6px 10px;border-radius:6px;border:1px solid var(--line);background:var(--card);color:var(--text);flex:2;min-width:180px">
      <input id="jfkey" placeholder="API-nøgle fra Jellyfin" style="font:inherit;padding:6px 10px;border-radius:6px;border:1px solid var(--line);background:var(--card);color:var(--text);flex:2;min-width:180px">
      <button id="jfusers">Hent brugere</button>
    </div>
    <div id="jfmap" style="margin-top:10px"></div>
    <div id="jfresult" style="margin-top:10px"></div>
  </section>
</main>
<script>
var users = [], pendingKey = null;
function esc(s) { return String(s == null ? '' : s).replace(/[&<>"]/g, function (c) { return { '&':'&amp;', '<':'&lt;', '>':'&gt;', '"':'&quot;' }[c]; }); }
function when(d) { return d ? new Date(d).toLocaleString('da-DK') : ''; }
var typeNames = { movies: 'Film', tvshows: 'Serier', homevideos: 'YouTube / video' };

async function load() {
  var r = await fetch('admin/api/state', { cache: 'no-store' });
  if (!r.ok) { document.getElementById('sub').textContent = 'Fejl: ' + r.status; return; }
  var s = await r.json();
  users = s.users;
  document.getElementById('title').textContent = s.serverName;
  document.getElementById('sub').innerHTML = 'Version ' + esc(s.version) + ' · Konfiguration: ' + esc(s.configPath || '(ingen)') +
    (s.ffprobe ? '' : ' · <span class="warn">ffprobe ikke fundet: ingen info om lyd- og undertekstspor</span>');

  var p = document.getElementById('pending');
  // Only redraw when the codes change, so a click on "Godkend" never hits a replaced button
  var key = s.pending.map(function (q) { return q.code; }).join(',') + '|' + users.length;
  if (key === pendingKey) { /* unchanged */ }
  else if (!s.pending.length) {
    pendingKey = key;
    p.className = 'empty';
    p.textContent = 'Ingen ventende koder. Vælg "Quick Connect" i TvFin, så dukker koden op her.';
  } else {
    pendingKey = key;
    p.className = '';
    p.innerHTML = '<table><tbody>' + s.pending.map(function (q) {
      return '<tr><td class="code">' + esc(q.code) + '</td><td>' + esc(q.device) + '<div class="muted">' + esc(q.app) + ' ' + esc(q.version) + '</div></td>' +
        '<td><div class="row"><select data-code="' + esc(q.code) + '">' + users.map(function (u) { return '<option value="' + u.id + '">' + esc(u.name) + '</option>'; }).join('') +
        '</select><button class="primary" data-approve="' + esc(q.code) + '">Godkend</button></div></td></tr>';
    }).join('') + '</tbody></table>';
    if (!users.length) p.innerHTML += '<p class="warn">Der er ingen brugere i konfigurationen.</p>';
  }

  document.getElementById('scanstate').textContent = s.scanning ? 'Scanner…' :
    'Sidst scannet ' + when(s.lastScan) + (s.probePending ? ' · aflæser ' + s.probePending + ' filer med ffprobe…' : '');
  document.getElementById('libs').innerHTML = s.libraries.map(function (l) {
    return '<tr><td>' + esc(l.name) + '</td><td>' + esc(typeNames[l.type] || l.type) + '</td><td>' + esc(l.path) +
      (l.offline ? ' <span class="warn">(kan ikke nås' + (l.items ? ' – viser sidst kendte indhold' : '') + ')</span>' : '') +
      '</td><td>' + l.items + '</td></tr>';
  }).join('') || '<tr><td colspan="4" class="empty">Ingen biblioteker i konfigurationen.</td></tr>';
  document.getElementById('devices').innerHTML = s.devices.map(function (d) {
    return '<tr><td>' + esc(d.user) + '</td><td>' + esc(d.device) + '</td><td>' + esc(d.app) + '</td><td>' + when(d.created) +
      '</td><td><button class="danger" data-remove="' + d.id + '">Log ud</button></td></tr>';
  }).join('') || '<tr><td colspan="5" class="empty">Ingen.</td></tr>';
  document.getElementById('apikeys').innerHTML = s.apiKeys.map(function (k) {
    return '<tr><td>' + esc(k.name) + '</td><td><code style="user-select:all;word-break:break-all">' + esc(k.key) + '</code></td><td>' + when(k.created) +
      '</td><td><button class="danger" data-delkey="' + k.id + '">Slet</button></td></tr>';
  }).join('') || '<tr><td colspan="4" class="empty">Ingen.</td></tr>';
}

document.addEventListener('click', async function (e) {
  var t = e.target;
  if (t.dataset.approve) {
    var code = t.dataset.approve;
    var sel = document.querySelector('select[data-code="' + code + '"]');
    var r = await fetch('admin/api/quickconnect', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ code: code, userId: sel.value }) });
    if (!r.ok) alert(await r.text());
    t.blur(); load();
  } else if (t.dataset.remove) {
    await fetch('admin/api/devices/' + t.dataset.remove, { method: 'DELETE' }); load();
  } else if (t.dataset.delkey) {
    if (confirm('Slet nøglen? Programmer, der bruger den, mister adgang.')) { await fetch('admin/api/apikeys/' + t.dataset.delkey, { method: 'DELETE' }); load(); }
  } else if (t.id === 'addkey') {
    var name = document.getElementById('keyname').value.trim();
    if (!name) { document.getElementById('keyname').focus(); return; }
    var r = await fetch('admin/api/apikeys', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name: name }) });
    if (!r.ok) alert(await r.text());
    document.getElementById('keyname').value = ''; load();
  } else if (t.id === 'jfusers' || t.id === 'jfimport') {
    var body = { url: document.getElementById('jfurl').value.trim(), apiKey: document.getElementById('jfkey').value.trim() };
    var res = document.getElementById('jfresult');
    if (t.id === 'jfusers') {
      res.innerHTML = '<span class="muted">Henter brugere…</span>';
      var r1 = await fetch('admin/api/jellyfin/users', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
      if (!r1.ok) { res.innerHTML = '<span class="warn">' + esc(await r1.text()) + '</span>'; return; }
      var jf = await r1.json();
      res.innerHTML = '';
      document.getElementById('jfmap').innerHTML = '<table><thead><tr><th>Jellyfin-bruger</th><th>Importér til JellyLite-bruger</th></tr></thead><tbody>' +
        jf.map(function (u) {
          return '<tr><td>' + esc(u.Name) + '</td><td><select data-jf="' + esc(u.Id) + '"><option value="">— spring over —</option>' +
            users.map(function (x) { return '<option value="' + x.id + '"' + (x.id === u.SuggestedUserId ? ' selected' : '') + '>' + esc(x.name) + '</option>'; }).join('') +
            '</select></td></tr>';
        }).join('') + '</tbody></table><div class="row" style="margin-top:10px"><button id="jfimport" class="primary">Importér</button></div>';
    } else {
      body.map = {};
      document.querySelectorAll('select[data-jf]').forEach(function (s) { if (s.value) body.map[s.dataset.jf] = s.value; });
      res.innerHTML = '<span class="muted">Importerer… det kan tage et øjeblik.</span>';
      var r2 = await fetch('admin/api/jellyfin/import', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
      if (!r2.ok) { res.innerHTML = '<span class="warn">' + esc(await r2.text()) + '</span>'; return; }
      var out = await r2.json();
      res.innerHTML = out.map(function (u) {
        return '<p><b>' + esc(u.JellyfinUser) + ' → ' + esc(u.JellyLiteUser) + ':</b> ' + u.Matched + ' af ' + u.WithData + ' matchet' +
          (u.Unmatched ? ' · <span class="warn">' + u.Unmatched + ' ikke fundet</span>' : ' ✓') + '</p>' +
          (u.UnmatchedSamples.length ? '<details><summary class="muted">Vis ikke fundne</summary><div class="muted" style="font-size:13px">' +
            u.UnmatchedSamples.map(esc).join('<br>') + (u.Unmatched > u.UnmatchedSamples.length ? '<br>…' : '') + '</div></details>' : '');
      }).join('') || '<span class="muted">Ingen brugere valgt.</span>';
    }
  } else if (t.id === 'scan') {
    await fetch('admin/api/scan', { method: 'POST' }); setTimeout(load, 500);
  }
});
load();
setInterval(load, 2000);
</script>
</body>
</html>
""";
}
