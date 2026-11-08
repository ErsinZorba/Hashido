// Hashido raporlama alıcısı (Cloudflare Worker + D1). Bağımlılık yok.
// POST /v1/events  gövde {"events":[…]} (≤ 8 KB, ≤ 50 olay; worker/schema.json ile denetlenir)
//   200 kaydedildi · 400 bozuk JSON / şemaya uymayan ya da bilinmeyen alan · 413 gövde > 8 KB · 429 dakikalık genel sınır
//   aşıldı · 503 veritabanı hatası. Uygulama 400/413/422'yi kalıcı ret sayar (olayları siler), 429/5xx'te bekleyip yeniden dener.
// Gizlilik: IP adresi, User-Agent ve öbür istek başlıkları (yalnız Content-Length dışında) OKUNMAZ ve saklanmaz; günlük yazılmaz (wrangler.toml'da gözlemlenebilirlik
// kapalı). Saklanan: alınış günü (UTC), test işareti, olay türü/sonuç/uygulama sürümü ve şemadan geçmiş olayın kendisi.
// Saklama: günlük cron alınışı 18 aydan eski satırları siler.

import schema from "../schema.json";
import { validateEnvelope } from "./validate.js";

const MAX_BODY_BYTES = 8 * 1024;
const DEFAULT_RATE_PER_MINUTE = 60;

function json(status, obj, extra = {}) {
  return new Response(JSON.stringify(obj), {
    status,
    headers: { "content-type": "application/json; charset=utf-8", "cache-control": "no-store", ...extra },
  });
}

/** Gövdeyi en çok limit bayt okur; aşarsa null. */
async function readLimited(request, limit) {
  const declared = request.headers.get("content-length");
  if (declared !== null && Number(declared) > limit) return null;
  if (!request.body) return new Uint8Array(0);
  const reader = request.body.getReader();
  const chunks = [];
  let size = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    size += value.byteLength;
    if (size > limit) {
      await reader.cancel();
      return null;
    }
    chunks.push(value);
  }
  const out = new Uint8Array(size);
  let at = 0;
  for (const c of chunks) { out.set(c, at); at += c.byteLength; }
  return out;
}

function ratePerMinute(env) {
  const n = Number.parseInt(env.RATE_PER_MINUTE ?? "", 10);
  return Number.isInteger(n) && n > 0 ? n : DEFAULT_RATE_PER_MINUTE;
}

async function handleEvents(request, env) {
  const bytes = await readLimited(request, MAX_BODY_BYTES);
  if (bytes === null) return json(413, { error: "too-large" });

  let body;
  try {
    body = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes));
  } catch {
    return json(400, { error: "bad-json" });
  }

  const problems = validateEnvelope(schema, body);
  if (problems.length > 0) return json(400, { error: "schema", problems: problems.slice(0, 20) });
  if (body.events.length === 0) return json(200, { stored: 0 });

  try {
    // Dakikalık genel sayaç (kişi başına değil: IP okunmaz). Yalnız şemadan geçen istekler sayılır.
    const minute = Math.floor(Date.now() / 60000);
    const row = await env.DB.prepare(
      "INSERT INTO rate (minute, n) VALUES (?1, 1) ON CONFLICT(minute) DO UPDATE SET n = n + 1 RETURNING n",
    ).bind(minute).first();
    if (!row || row.n > ratePerMinute(env)) return json(429, { error: "rate" }, { "retry-after": "60" });

    const insert = env.DB.prepare(
      "INSERT INTO events (received, test, type, result, app, event) VALUES (date('now'), ?1, ?2, ?3, ?4, ?5)",
    );
    await env.DB.batch(body.events.map((e) =>
      insert.bind(e.test === true ? 1 : 0, e.type, e.result, e.app, JSON.stringify(e))));
    return json(200, { stored: body.events.length });
  } catch {
    return json(503, { error: "storage" });
  }
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.pathname !== "/v1/events") return json(404, { error: "not-found" });
    if (request.method !== "POST") return json(405, { error: "method" }, { allow: "POST" });
    return handleEvents(request, env);
  },

  async scheduled(_controller, env) {
    await env.DB.batch([
      env.DB.prepare("DELETE FROM events WHERE received < date('now', '-18 months')"),
      env.DB.prepare("DELETE FROM rate WHERE minute < ?1").bind(Math.floor(Date.now() / 60000) - 1440),
    ]);
  },
};
