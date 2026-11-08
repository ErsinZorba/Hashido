// Hashido raporlama şeması denetleyicisi. Uygulamanın src/Apfs.Windows/Reporting/ReportSchema.cs dosyasıyla AYNI
// anahtar sözcük alt kümesini, AYNI schema.json üzerinde uygular: $ref (üst düzey tanım adı), type, properties, required,
// additionalProperties (false), enum, const, pattern, maxLength, minimum, maximum, items, maxItems.
// Tanımlanmamış her alan reddedilir. Sorun iletileri yalnız alan yolu + kural adıdır (değer içermez).

const patternCache = new Map();

function pattern(p) {
  let r = patternCache.get(p);
  if (!r) {
    r = new RegExp(p);
    patternCache.set(p, r);
  }
  return r;
}

function deepEqual(a, b) {
  if (a === b) return true;
  if (typeof a !== typeof b || a === null || b === null || typeof a !== "object") return false;
  if (Array.isArray(a) !== Array.isArray(b)) return false;
  if (Array.isArray(a)) return a.length === b.length && a.every((x, i) => deepEqual(x, b[i]));
  const ka = Object.keys(a), kb = Object.keys(b);
  return ka.length === kb.length && ka.every((k) => Object.hasOwn(b, k) && deepEqual(a[k], b[k]));
}

function isPlainObject(v) {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}

function def(root, name) {
  const d = root[name];
  if (!isPlainObject(d)) throw new Error(`schema: ${name}`);
  return d;
}

function check(root, node, schema, path, problems) {
  if (typeof schema.$ref === "string") return check(root, node, def(root, schema.$ref), path, problems);
  if (node === null || node === undefined) { problems.push(`${path}: null`); return; }

  if ("const" in schema && !deepEqual(schema.const, node)) problems.push(`${path}: const`);
  if (Array.isArray(schema.enum) && !schema.enum.some((x) => deepEqual(x, node))) problems.push(`${path}: enum`);

  switch (schema.type) {
    case "object": {
      if (!isPlainObject(node)) { problems.push(`${path}: type`); return; }
      const props = isPlainObject(schema.properties) ? schema.properties : {};
      for (const req of schema.required ?? []) if (!Object.hasOwn(node, req)) problems.push(`${path}.${req}: required`);
      for (const [k, v] of Object.entries(node)) {
        if (Object.hasOwn(props, k) && isPlainObject(props[k])) check(root, v, props[k], `${path}.${k}`, problems);
        else problems.push(`${path}.${k}: unknown`);
      }
      break;
    }
    case "array": {
      if (!Array.isArray(node)) { problems.push(`${path}: type`); return; }
      if (typeof schema.maxItems === "number" && node.length > schema.maxItems) problems.push(`${path}: maxItems`);
      if (isPlainObject(schema.items)) node.forEach((x, i) => check(root, x, schema.items, `${path}[${i}]`, problems));
      break;
    }
    case "string": {
      if (typeof node !== "string") { problems.push(`${path}: type`); return; }
      if (typeof schema.maxLength === "number" && node.length > schema.maxLength) problems.push(`${path}: maxLength`);
      // JS'te $ (m bayrağı olmadan) yalnız metnin sonunda eşleşir; satır sonu yine de baştan reddedilir (.NET ile aynı)
      if (typeof schema.pattern === "string" && (node.includes("\n") || !pattern(schema.pattern).test(node)))
        problems.push(`${path}: pattern`);
      break;
    }
    case "integer": {
      if (typeof node !== "number" || !Number.isInteger(node)) { problems.push(`${path}: type`); return; }
      if (typeof schema.minimum === "number" && node < schema.minimum) problems.push(`${path}: minimum`);
      if (typeof schema.maximum === "number" && node > schema.maximum) problems.push(`${path}: maximum`);
      break;
    }
    case "boolean":
      if (typeof node !== "boolean") problems.push(`${path}: type`);
      break;
    case undefined:
      if (!("const" in schema) && !Array.isArray(schema.enum)) problems.push(`${path}: schema`);
      break;
    default:
      problems.push(`${path}: schema`);
  }
}

/** Gönderim zarfını ({"events":[…]}) denetler; sorun yoksa boş dizi. */
export function validateEnvelope(root, body) {
  const problems = [];
  check(root, body, def(root, "envelope"), "envelope", problems);
  return problems;
}

/** Tek olayı denetler; sorun yoksa boş dizi. */
export function validateEvent(root, event) {
  const problems = [];
  check(root, event, def(root, "event"), "event", problems);
  return problems;
}
