using JellyLite.Auth;
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
            };
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
