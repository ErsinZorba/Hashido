-- Hashido raporlama veritabanı (D1). IP, istek başlığı ya da kalıcı kimlik sütunu YOKTUR.
-- Uygula: npx wrangler d1 execute hashido-reports --remote --file schema.sql   (yerelde --local)

CREATE TABLE IF NOT EXISTS events (
  id       INTEGER PRIMARY KEY AUTOINCREMENT,
  received TEXT    NOT NULL,              -- alınış günü (UTC, YYYY-MM-DD); 18 ay sonra cron siler
  test     INTEGER NOT NULL DEFAULT 0,    -- olayda "test": true ise 1 (deneme kayıtları ayıklanıp silinir)
  type     TEXT    NOT NULL,
  result   TEXT    NOT NULL,
  app      TEXT    NOT NULL,
  event    TEXT    NOT NULL               -- worker/schema.json'dan geçmiş olayın JSON'u
);

CREATE INDEX IF NOT EXISTS events_received ON events (received);
CREATE INDEX IF NOT EXISTS events_type ON events (type, result);

-- Dakikalık genel istek sayacı (hız sınırı; kişi başına değil). Cron bir günden eskileri siler.
CREATE TABLE IF NOT EXISTS rate (
  minute INTEGER PRIMARY KEY,             -- Unix zamanı / 60
  n      INTEGER NOT NULL
);
