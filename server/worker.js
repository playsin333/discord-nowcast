// Cover host for discord-nowcast: the program posts a cover (JPEG or PNG),
// gets a link back, and Discord loads the picture by it. Covers are kept for
// 14 days; pictures of one's own status (?kind=own) for 2 days, the program
// uploads them again while the status is on, and the uploader's IP hash is
// kept with them so that a ban can follow.
// GET / is a test page: pick any picture, it is shrunk and uploaded like a cover.
// GET /admin?key=ADMIN_KEY: the latest own pictures, to remove and ban. ADMIN_KEY
// is a Secret in the worker's Settings → Variables and Secrets.
//
// Runs as the Cloudflare Worker nowcast-covers (https://nowcast-covers.playsin333.workers.dev,
// src/Hosts.cs) on the free plan: a D1 database bound as DB (the tables are
// made on the first request) and a daily Cron Trigger for the clean-up.
// Changed by pasting this file into Edit code in the Cloudflare dashboard.
const MAX_SIZE = 256 * 1024;
const KEEP_DAYS = 14;
const OWN_KEEP_DAYS = 2;
const PER_HOUR = 120;

let ready = false;

export default {
  async fetch(request, env) {
    await setup(env);
    const url = new URL(request.url);
    if (request.method === "POST" && url.pathname === "/upload") return upload(request, env, url.origin, url.searchParams.get("kind") === "own" ? "own" : "cover");
    if (url.pathname === "/admin") return admin(request, env, url);
    const file = url.pathname.match(/^\/([0-9a-f]{40})\.(jpg|png)$/);
    if (request.method === "GET" && file) return picture(env, file[1]);
    if (request.method === "GET" && url.pathname === "/") return new Response(PAGE, { headers: { "Content-Type": "text/html; charset=utf-8" } });
    return text("Not found", 404);
  },

  async scheduled(event, env) {
    await setup(env);
    await env.DB.batch([
      env.DB.prepare("DELETE FROM covers WHERE kind = 'cover' AND added < ?").bind(Date.now() - KEEP_DAYS * 86400000),
      env.DB.prepare("DELETE FROM covers WHERE kind = 'own' AND added < ?").bind(Date.now() - OWN_KEEP_DAYS * 86400000),
      env.DB.prepare("DELETE FROM uploads WHERE at < ?").bind(Date.now() - 3600000),
    ]);
  },
};

async function setup(env) {
  if (ready) return;
  await env.DB.batch([
    env.DB.prepare("CREATE TABLE IF NOT EXISTS covers (id TEXT PRIMARY KEY, type TEXT, data BLOB, added INTEGER, kind TEXT DEFAULT 'cover', ip TEXT)"),
    env.DB.prepare("CREATE TABLE IF NOT EXISTS uploads (ip TEXT, at INTEGER)"),
    env.DB.prepare("CREATE TABLE IF NOT EXISTS blocked (id TEXT PRIMARY KEY)"),
    env.DB.prepare("CREATE TABLE IF NOT EXISTS banned (ip TEXT PRIMARY KEY)"),
  ]);
  // The first table had no kind and ip: added to it once.
  const columns = await env.DB.prepare("PRAGMA table_info(covers)").all();
  const names = columns.results.map(c => c.name);
  if (!names.includes("kind")) await env.DB.prepare("ALTER TABLE covers ADD COLUMN kind TEXT DEFAULT 'cover'").run();
  if (!names.includes("ip")) await env.DB.prepare("ALTER TABLE covers ADD COLUMN ip TEXT").run();
  ready = true;
}

async function upload(request, env, origin, kind) {
  // Only a hash of the address is kept: to limit uploads per hour, and with
  // a picture of one's own status, to ban whoever put up a bad one.
  const ip = hex(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(request.headers.get("CF-Connecting-IP") || ""))).slice(0, 16);
  if (await env.DB.prepare("SELECT 1 FROM banned WHERE ip = ?").bind(ip).first()) return text("Banned", 403);
  const count = await env.DB.prepare("SELECT COUNT(*) AS n FROM uploads WHERE ip = ? AND at > ?").bind(ip, Date.now() - 3600000).first("n");
  if (count >= PER_HOUR) return text("Too many uploads, try again later", 429);

  const body = await request.arrayBuffer();
  if (body.byteLength === 0 || body.byteLength > MAX_SIZE) return text("The picture must be up to 256 KB", 413);
  const type = format(new Uint8Array(body));
  if (!type) return text("Only JPEG or PNG", 415);

  // Named by its hash: the same cover from many people is stored once.
  const id = hex(await crypto.subtle.digest("SHA-1", body));
  if (await env.DB.prepare("SELECT 1 FROM blocked WHERE id = ?").bind(id).first()) return text("Blocked", 451);
  // A cover stays a cover: it is kept longer, and its uploader is not kept.
  await env.DB.batch([
    env.DB.prepare("INSERT INTO covers (id, type, data, added, kind, ip) VALUES (?, ?, ?, ?, ?, ?) " +
      "ON CONFLICT(id) DO UPDATE SET added = excluded.added, kind = CASE WHEN covers.kind = 'cover' THEN 'cover' ELSE excluded.kind END, " +
      "ip = CASE WHEN covers.kind = 'cover' THEN NULL ELSE excluded.ip END")
      .bind(id, type, body, Date.now(), kind, kind === "own" ? ip : null),
    env.DB.prepare("INSERT INTO uploads (ip, at) VALUES (?, ?)").bind(ip, Date.now()),
  ]);
  return text(origin + "/" + id + "." + type, 200);
}

async function picture(env, id) {
  const row = await env.DB.prepare("SELECT type, data FROM covers WHERE id = ?").bind(id).first();
  if (!row) return text("Not found", 404);
  return new Response(new Uint8Array(row.data), {
    headers: { "Content-Type": row.type === "png" ? "image/png" : "image/jpeg", "Cache-Control": "public, max-age=86400" },
  });
}

// The latest own pictures with buttons to remove one, or remove it and ban
// its uploader. A removed picture is never taken again.
async function admin(request, env, url) {
  const form = request.method === "POST" ? await request.formData() : null;
  const key = form ? form.get("key") : url.searchParams.get("key");
  if (!env.ADMIN_KEY || key !== env.ADMIN_KEY) return text("Not found", 404);
  let done = "";
  if (form) {
    const id = String(form.get("id") || "").replace(/^.*\//, "").replace(/\.(jpg|png)$/, "");
    if (/^[0-9a-f]{40}$/.test(id)) {
      const row = await env.DB.prepare("SELECT ip FROM covers WHERE id = ?").bind(id).first();
      const steps = [
        env.DB.prepare("DELETE FROM covers WHERE id = ?").bind(id),
        env.DB.prepare("INSERT OR IGNORE INTO blocked (id) VALUES (?)").bind(id),
      ];
      if (form.get("ban") && row && row.ip) steps.push(env.DB.prepare("INSERT OR IGNORE INTO banned (ip) VALUES (?)").bind(row.ip));
      await env.DB.batch(steps);
      done = "<p>Удалено" + (form.get("ban") && row && row.ip ? " и забанено" : "") + ": " + id + "</p>";
    }
  }
  const rows = await env.DB.prepare("SELECT id, type, added FROM covers WHERE kind = 'own' ORDER BY added DESC LIMIT 60").all();
  const k = escape(key);
  const cards = rows.results.map(r => `<div style="display:inline-block;margin:6px;text-align:center;vertical-align:top">
<img src="/${r.id}.${r.type}" style="width:150px;height:150px;object-fit:cover;background:#1e1f22"><br>
<small>${new Date(r.added).toLocaleString("ru-RU", { timeZone: "Europe/Moscow" })}</small><br>
<form method="post" style="display:inline"><input type="hidden" name="key" value="${k}"><input type="hidden" name="id" value="${r.id}"><button>Удалить</button></form>
<form method="post" style="display:inline"><input type="hidden" name="key" value="${k}"><input type="hidden" name="id" value="${r.id}"><input type="hidden" name="ban" value="1"><button>+ бан</button></form>
</div>`).join("");
  return new Response(`<!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width">
<title>nowcast admin</title><body style="font:15px sans-serif;background:#313338;color:#ddd;padding:12px">
<h3>Картинки своих статусов (последние 60)</h3>${done}
<form method="post"><input type="hidden" name="key" value="${k}">Ссылка или id: <input name="id" size="50"> <button>Удалить</button> <label><input type="checkbox" name="ban" value="1"> и забанить</label></form>
${cards || "<p>Пока нет.</p>"}`, { headers: { "Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store" } });
}

function escape(value) {
  return String(value).replace(/[&<>"']/g, c => "&#" + c.charCodeAt(0) + ";");
}

function format(b) {
  if (b[0] === 0xff && b[1] === 0xd8 && b[2] === 0xff) return "jpg";
  if (b[0] === 0x89 && b[1] === 0x50 && b[2] === 0x4e && b[3] === 0x47) return "png";
  return null;
}

function hex(buffer) {
  return [...new Uint8Array(buffer)].map(b => b.toString(16).padStart(2, "0")).join("");
}

function text(body, status) {
  return new Response(body, { status, headers: { "Content-Type": "text/plain; charset=utf-8" } });
}

const PAGE = `<!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width">
<title>nowcast covers test</title>
<body style="font:16px sans-serif;background:#313338;color:#ddd;padding:20px">
<p>Выбери любую картинку: она уменьшится до 300 px и загрузится, как обложка.</p>
<input type="file" accept="image/*" id="f"><p id="r"></p><img id="i" style="max-width:300px">
<script>
f.onchange = async () => {
  r.textContent = "Загружаю…";
  try {
    const img = await createImageBitmap(f.files[0]);
    const s = Math.min(1, 300 / Math.max(img.width, img.height));
    const c = document.createElement("canvas");
    c.width = Math.round(img.width * s); c.height = Math.round(img.height * s);
    c.getContext("2d").drawImage(img, 0, 0, c.width, c.height);
    const blob = await new Promise(ok => c.toBlob(ok, "image/jpeg", 0.88));
    const t0 = Date.now();
    const res = await fetch("/upload", { method: "POST", headers: { "Content-Type": "image/jpeg" }, body: blob });
    const answer = await res.text();
    r.textContent = (res.ok ? "Работает: " : "Ошибка " + res.status + ": ") + answer + " (" + (Date.now() - t0) + " мс, " + Math.round(blob.size / 1024) + " КБ)";
    if (res.ok) i.src = answer;
  } catch (e) { r.textContent = "Не вышло: " + e; }
};
</script>`;
