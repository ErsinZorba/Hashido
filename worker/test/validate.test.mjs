// node --test test/*.test.mjs  (bağımlılıksız; Worker'ın kullandığı schema.json ve validate.js'i sınar)
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { validateEnvelope, validateEvent } from "../src/validate.js";

const schema = JSON.parse(readFileSync(new URL("../schema.json", import.meta.url), "utf8"));

export function sampleEvent(extra = {}) {
  return {
    v: 1, app: "1.4.0", sha: "80eae11", os: "10.0.26300", osArch: "x64", procArch: "x64", lang: "tr",
    process: "service", type: "mount", result: "ok", day: "2026-11-08", durLog2: 3,
    disk: {
      bus: "usb", sector: 512, scheme: "gpt", apfsParts: 1, blockSize: 4096, sizeLog2: 27, nxFeatures: 0,
      nxIncompat: 2, nxRoCompat: 0, swCrypto: false, unreadableVolumes: 0,
      volumes: [{ role: "none", enc: "none", caseSensitive: false, snapshots: false, filesLog2: 3, lastWriter: "apfs_kext (2313.1)" }],
    },
    volumes: { mounted: 1, locked: 0, hidden: 0, failed: 0 },
    ...extra,
  };
}

const ok = (p) => assert.deepEqual(p, []);
const has = (p, s) => assert.ok(p.includes(s), `${s} bekleniyordu: ${JSON.stringify(p)}`);

test("geçerli olaylar", () => {
  ok(validateEvent(schema, sampleEvent()));
  ok(validateEvent(schema, sampleEvent({ test: true })));
  ok(validateEvent(schema, { v: 1, app: "1.4.0", os: "10.0.26300", osArch: "arm64", procArch: "x64", lang: "other",
    type: "write-enable", result: "refused", day: "2026-11-08", gate: ["Gate.Snapshots", "Write.Encrypted"] }));
  ok(validateEvent(schema, { v: 1, app: "1.4.0", os: "10.0.26300", osArch: "x64", procArch: "x64", lang: "en",
    type: "crash", result: "fail", day: "2026-11-08", process: "tray",
    crash: { exType: "System.InvalidOperationException", frames: ["Apfs.Tray.MainForm.OnLoad+0x1a"] } }));
  ok(validateEnvelope(schema, { events: [sampleEvent(), sampleEvent({ type: "eject", result: "busy" })] }));
});

test("bilinmeyen alan reddedilir (her düzeyde)", () => {
  has(validateEvent(schema, sampleEvent({ name: "Ersin'in diski" })), "event.name: unknown");
  const e = sampleEvent();
  e.disk.serial = "X";
  has(validateEvent(schema, e), "event.disk.serial: unknown");
  const e2 = sampleEvent();
  e2.disk.volumes[0].label = "Belgeler";
  has(validateEvent(schema, e2), "event.disk.volumes[0].label: unknown");
  has(validateEnvelope(schema, { events: [], extra: 1 }), "envelope.extra: unknown");
  has(validateEvent(schema, JSON.parse('{"__proto__":{"x":1},"v":1}')), "event.__proto__: unknown");
});

test("tür, kalıp, sınır, enum, const, zorunlu", () => {
  const { day, ...noDay } = sampleEvent();
  void day;
  has(validateEvent(schema, noDay), "event.day: required");
  has(validateEvent(schema, sampleEvent({ v: 2 })), "event.v: const");
  has(validateEvent(schema, sampleEvent({ lang: "xx" })), "event.lang: enum");
  has(validateEvent(schema, sampleEvent({ app: "1.4.0-beta" })), "event.app: pattern");
  has(validateEvent(schema, sampleEvent({ app: "1.4.0\n" })), "event.app: pattern");
  has(validateEvent(schema, sampleEvent({ app: 140 })), "event.app: type");
  has(validateEvent(schema, sampleEvent({ durLog2: 65 })), "event.durLog2: maximum");
  has(validateEvent(schema, sampleEvent({ durLog2: -1 })), "event.durLog2: minimum");
  has(validateEvent(schema, sampleEvent({ durLog2: 1.5 })), "event.durLog2: type");
  has(validateEvent(schema, sampleEvent({ durLog2: "3" })), "event.durLog2: type");
  has(validateEvent(schema, sampleEvent({ test: "true" })), "event.test: type");
  has(validateEvent(schema, sampleEvent({ gate: Array(21).fill("Gate.X") })), "event.gate: maxItems");
  has(validateEvent(schema, sampleEvent({ gate: ["C:\\Users\\x"] })), "event.gate[0]: pattern");
  has(validateEvent(schema, sampleEvent({ disk: null })), "event.disk: null");
  const e = sampleEvent();
  e.disk.volumes[0].lastWriter = "apfs_kext (2313.1) " + "x".repeat(20);
  has(validateEvent(schema, e), "event.disk.volumes[0].lastWriter: maxLength");
  has(validateEnvelope(schema, { events: Array(51).fill(sampleEvent()) }), "envelope.events: maxItems");
  has(validateEnvelope(schema, []), "envelope: type");
  has(validateEnvelope(schema, {}), "envelope.events: required");
});
