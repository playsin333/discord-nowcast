// Cloudflare entry point for Nowcast. All new images and moderation live on
// the VPS, so exhausted D1 quotas cannot stop uploads. Secret: BRIDGE_KEY.
// Optional existing D1 binding DB is used only for old, unmigrated links.
const VPS = "https://nowcast.85-192-42-111.sslip.io:10443";
const MAX_SIZE = 256 * 1024;
const KEEP_MS = 6 * 3600000;

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.pathname === "/admin") return Response.redirect(VPS + "/admin", 302);
    if (request.method === "GET" && url.pathname === "/")
      return new Response(PAGE, { headers: { "Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store" } });
    try {
      if (request.method === "POST" && url.pathname === "/upload") {
        const body = await limitedBody(request);
        if (!body || !body.byteLength) return text("The picture must be up to 256 KB", 413);
        const kind = url.searchParams.get("kind") === "own" ? "own" : "cover";
        const response = await bridge(env, "/bridge/upload?kind=" + kind, {
          method: "POST", body,
          headers: { "Content-Type": request.headers.get("Content-Type") || "application/octet-stream",
            "X-Nowcast-IP": request.headers.get("CF-Connecting-IP") || "" },
        });
        const answer = (await response.text()).trim();
        if (!response.ok) return text(answer, response.status);
        const link = new URL(answer);
        if (link.origin !== VPS || !/^\/[a-f0-9]{40}\.(jpg|png)$/.test(link.pathname) || link.search || link.hash)
          return text("Invalid reserve reply", 502);
        // Preserve the Worker's origin for existing clients that validate it.
        return text(url.origin + link.pathname, 200);
      }
      const file = url.pathname.match(/^\/([a-f0-9]{40})\.(jpg|png)$/);
      if ((request.method === "GET" || request.method === "HEAD") && file) {
        const response = await fetch(VPS + url.pathname, {
          method: request.method, signal: AbortSignal.timeout(5000), redirect: "error",
          headers: { "User-Agent": "nowcast-cover-worker" },
        });
        if (response.status !== 404) return pictureResponse(response);
        const policy = await bridge(env, "/bridge/check?id=" + file[1]);
        if (!policy.ok) return text("Moderation unavailable", 503);
        if ((await policy.json()).blocked) return text("Blocked", 451);
        return legacyPicture(env, file[1]);
      }
      return text("Not found", 404);
    } catch (error) {
      console.error("Nowcast upstream error", error?.name || "Error");
      return text("Cover service temporarily unavailable; try the reserve", 503);
    }
  },

  async scheduled(event, env) {
    // The VPS enforces expiry on every read and sweeps every minute.
    // D1 cleanup only retires old files; an exhausted quota is harmless here.
    if (!env.DB) return;
    try {
      await env.DB.batch([
        env.DB.prepare("DELETE FROM covers WHERE added <= ?").bind(Date.now() - KEEP_MS),
        env.DB.prepare("DELETE FROM uploads WHERE at < ?").bind(Date.now() - 3600000),
      ]);
    } catch { console.warn("Legacy D1 cleanup deferred; new uploads do not use D1"); }
  },
};

function pictureResponse(response) {
  return new Response(response.body, { status: response.status,
    headers: { "Content-Type": response.headers.get("Content-Type") || "application/octet-stream",
      "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff" } });
}

async function legacyPicture(env, id) {
  if (!env.DB) return text("Not found", 404);
  try {
    const row = await env.DB.prepare("SELECT type, data FROM covers WHERE id = ? AND added > ?")
      .bind(id, Date.now() - KEEP_MS).first();
    if (!row) return text("Not found", 404);
    return new Response(new Uint8Array(row.data), { headers: {
      "Content-Type": row.type === "png" ? "image/png" : "image/jpeg",
      "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff",
    } });
  } catch { return text("Legacy image temporarily unavailable", 503); }
}

async function bridge(env, path, options = {}) {
  if (!env.BRIDGE_KEY) throw new Error("Missing bridge secret");
  return fetch(VPS + path, { ...options,
    headers: { ...options.headers, Authorization: "Bearer " + env.BRIDGE_KEY, "User-Agent": "nowcast-cover-worker" },
    signal: AbortSignal.timeout(5000), redirect: "error" });
}

async function limitedBody(request) {
  if (Number(request.headers.get("Content-Length")) > MAX_SIZE || !request.body) return null;
  const reader = request.body.getReader();
  const chunks = []; let length = 0;
  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    length += value.byteLength;
    if (length > MAX_SIZE) { await reader.cancel(); return null; }
    chunks.push(value);
  }
  const data = new Uint8Array(length); let offset = 0;
  for (const chunk of chunks) { data.set(chunk, offset); offset += chunk.byteLength; }
  return data.buffer;
}

function text(body, status) {
  return new Response(body, { status, headers: { "Content-Type": "text/plain; charset=utf-8", "Cache-Control": "no-store" } });
}

const PAGE = `<!doctype html><html lang="ru"><meta charset="utf-8"><meta name="viewport" content="width=device-width">
<title>Nowcast — проверка обложек</title>
<body style="font:16px system-ui;background:#313338;color:#ddd;padding:24px">
<h1>Обложки Nowcast</h1><p>Выбери картинку: она уменьшится до 300 пикселей и загрузится на сервер. Хранение — 6 часов после последней загрузки.</p>
<input type="file" accept="image/*" id="file"><p id="result" role="status"></p><img id="preview" style="max-width:300px" alt=""><p><a href="/admin" style="color:#aebaff">Админка</a></p>
<script>
const file=document.querySelector('#file'),result=document.querySelector('#result'),preview=document.querySelector('#preview');
file.onchange=async()=>{if(!file.files[0])return;result.textContent='Загружаю…';try{
const image=await createImageBitmap(file.files[0]);const s=Math.min(1,300/Math.max(image.width,image.height));
const canvas=document.createElement('canvas');canvas.width=Math.max(1,Math.round(image.width*s));canvas.height=Math.max(1,Math.round(image.height*s));
canvas.getContext('2d').drawImage(image,0,0,canvas.width,canvas.height);image.close();
const blob=await new Promise(ok=>canvas.toBlob(ok,'image/jpeg',0.88));
const response=await fetch('/upload',{method:'POST',headers:{'Content-Type':'image/jpeg'},body:blob});const answer=await response.text();
result.textContent=response.ok?'Загружено: '+answer:'Ошибка '+response.status+': '+answer;if(response.ok)preview.src=answer;
}catch(e){result.textContent='Не вышло: '+e.message;}};
</script></html>`;
