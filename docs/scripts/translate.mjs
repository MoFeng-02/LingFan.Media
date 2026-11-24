// Fill the translation memory (TM) for generated API reference locales.
//
// Usage:
//   node scripts/translate.mjs [--locale en] [--engine edge] [--dry-run] [--limit N]
//   (or: pnpm docs:translate)
//
// Behavior:
//   - Extracts every translatable prose segment from the same source parse
//     used by gen-api (single source of truth: C# /// comments).
//   - Translates only segments whose sha1 key is missing from the TM.
//     Human-reviewed entries (reviewed: true) are never overwritten.
//   - Engine is a pluggable adapter. Default "edge" uses the free Microsoft
//     Translator Edge endpoint (no key, batch POST, ~50k chars/request cap).
//     The endpoint is an internal Microsoft interface and may change; failures
//     leave the TM untouched for the failed batch (offline docs:gen keeps
//     falling back to source text, so the site never breaks).
//   - Preserved-symbol conservation and phrase locks are enforced per segment;
//     a failed validation drops that segment (reported, retried next run).
//
// No third-party dependencies: Node built-ins only.

import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { walkCs, parseCsFile } from './lib/csharp-doc.mjs';
import { parseDocBlock } from './lib/csharp-doc.mjs';
import {
  loadTm, saveTm, segKey, validateTranslation, maskLockedPhrases, maskCodeSpans, applyGlossary, collectSegments,
} from './lib/i18n.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const docsRoot = path.resolve(here, '..');
const repoRoot = path.resolve(docsRoot, '..');

const PROJECTS = [
  { key: 'abstractions', dir: 'src/Infrastructure/LingFan.Media.Abstractions' },
  { key: 'extensions', dir: 'src/Infrastructure/LingFan.Media.Extensions' },
];

// ---- engine adapters ----

const ENGINES = {
  // Free Microsoft/Bing endpoint (Edge built-in translation). No auth.
  edge: {
    name: 'edge-free',
    targetLang: { en: 'en', ja: 'ja', ko: 'ko', fr: 'fr', de: 'de', es: 'es', ru: 'ru' },
    async translateBatch(texts, locale) {
      const to = this.targetLang[locale];
      if (!to) throw new Error(`engine edge: unsupported locale ${locale}`);
      const url = `https://edge.microsoft.com/translate/translatetext?from=zh-Hans&to=${to}&api-version=3.0`;
      const res = await fetch(url, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36 Edg/126.0.0.0',
        },
        body: JSON.stringify(texts),
      });
      if (!res.ok) {
        const body = await res.text().catch(() => '');
        const err = new Error(`edge endpoint HTTP ${res.status}: ${body.slice(0, 200)}`);
        err.status = res.status;
        throw err;
      }
      const data = await res.json();
      return data.map((d) => d.translations?.[0]?.text ?? '');
    },
  },
};

// ---- segment extraction (same parse as gen-api) ----

function surfaceMembers(surface) {
  const members = [];
  let cur = { doc: [], sig: [] };
  for (const line of surface) {
    if (line.trim().startsWith('///')) {
      if (cur.sig.length) { members.push(cur); cur = { doc: [], sig: [] }; }
      cur.doc.push(line.trim().replace(/^\/\/\/\s?/, ''));
    } else cur.sig.push(line);
  }
  if (cur.sig.length || cur.doc.length) members.push(cur);
  return members;
}

function allSegments() {
  const segs = new Set();
  for (const proj of PROJECTS) {
    for (const abs of walkCs(path.join(repoRoot, proj.dir))) {
      const parsed = parseCsFile(abs, repoRoot);
      for (const type of parsed.types) {
        for (const s of collectSegments(type, surfaceMembers, parseDocBlock)) segs.add(s);
      }
    }
  }
  return [...segs].sort();
}

// ---- batching ----

const MAX_BATCH_CHARS = 40000; // endpoint cap ~50k; keep headroom
const MAX_BATCH_ITEMS = 100;

function batches(items) {
  const out = [];
  let cur = [];
  let chars = 0;
  for (const it of items) {
    const len = it.length;
    if (cur.length && (chars + len > MAX_BATCH_CHARS || cur.length >= MAX_BATCH_ITEMS)) {
      out.push(cur); cur = []; chars = 0;
    }
    cur.push(it); chars += len;
  }
  if (cur.length) out.push(cur);
  return out;
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// ---- main ----

const args = process.argv.slice(2);
const flag = (name, dflt) => {
  const i = args.indexOf(`--${name}`);
  return i >= 0 ? (args[i + 1] && !args[i + 1].startsWith('--') ? args[i + 1] : true) : dflt;
};
const locale = flag('locale', 'en');
const engineName = flag('engine', 'edge');
const dryRun = args.includes('--dry-run');
const limit = Number(flag('limit', 0)) || 0;

const engine = ENGINES[engineName];
if (!engine) { console.error(`Unknown engine: ${engineName}`); process.exit(1); }

const tm = loadTm(locale);
const segments = allSegments();
const pending = segments.filter((s) => {
  const e = tm.entries[segKey(s)];
  return !(e && e.dst && e.reviewed);
}).filter((s) => !tm.entries[segKey(s)]?.dst);

console.log(`Locale ${locale}: ${segments.length} segments total, ${pending.length} untranslated.`);
if (dryRun) {
  for (const s of pending.slice(0, 10)) console.log('  PENDING ' + s.slice(0, 80).replace(/\n/g, ' '));
  process.exit(0);
}
if (!pending.length) { console.log('TM is up to date.'); process.exit(0); }

const work = limit ? pending.slice(0, limit) : pending;
let done = 0, failed = 0;
const groupList = batches(work);
for (let gi = 0; gi < groupList.length; gi++) {
  const group = groupList[gi];
  // Mask inline code spans first (identifiers verbatim), then locked contract
  // phrases; keep restorers in reverse order.
  const masked = group.map((s) => {
    const code = maskCodeSpans(s);
    const lock = maskLockedPhrases(code.text);
    return {
      text: lock.text,
      restore: (t) => code.restore(lock.restore(t)),
      codeCount: code.count,
    };
  });
  let results;
  try {
    results = await engine.translateBatch(masked.map((m) => m.text), locale);
  } catch (e) {
    console.error(`Batch ${gi + 1}/${groupList.length} failed: ${e.message}`);
    failed += group.length;
    if (e.status === 404 || e.status === 401) {
      console.error('Endpoint unavailable/auth-required — aborting (TM saved with completed batches).');
      break;
    }
    await sleep(2000);
    continue;
  }
  for (let i = 0; i < group.length; i++) {
    const src = group[i];
    const dst = applyGlossary(masked[i].restore(results[i] || ''), locale);
    const problem = validateTranslation(src, dst)
      || (dst.includes('⟦C') ? 'code placeholder not restored' : null);
    if (problem) {
      console.warn(`  SKIP (${problem}): ${src.slice(0, 60).replace(/\n/g, ' ')}`);
      failed++;
      continue;
    }
    tm.entries[segKey(src)] = { src, dst, engine: engine.name, reviewed: false };
    done++;
  }
  saveTm(tm); // persist after each batch: interrupted runs lose nothing
  console.log(`Batch ${gi + 1}/${groupList.length}: +${group.length} (total done ${done}, failed ${failed})`);
  await sleep(400); // be polite to the free endpoint
}

saveTm(tm);
console.log(`Done: ${done} translated, ${failed} failed/skipped. TM: docs/i18n/tm.${locale}.json`);
process.exit(failed && !done ? 1 : 0);