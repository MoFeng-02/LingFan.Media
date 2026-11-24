// Translation memory (TM) for the generated API reference.
//
// Single source of truth stays the C# /// doc comments (Chinese). This module
// adds a per-locale translation layer keyed by the SHA-1 of each source prose
// segment:
//   - docs:gen reads the TM offline (zero network) and renders translated
//     prose for non-source locales; missing/stale segments fall back to the
//     original text and are counted as stale.
//   - scripts/translate.mjs fills the TM via a pluggable engine adapter
//     (default: Microsoft/Bing free Edge endpoint) and writes it back to disk.
//
// TM file layout (docs/i18n/tm.<locale>.json):
//   { "locale": "en", "sourceLocale": "zh-Hans", "entries": {
//       "<sha1(source)>": { "src": "...", "dst": "...", "engine": "...", "reviewed": false } } }
//
// `reviewed: true` entries are human-proofread and never overwritten by the
// machine engine while the source hash is unchanged.
//
// No third-party dependencies: Node built-ins only.

import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const docsRoot = path.resolve(here, '../..');

export const SOURCE_LOCALE = 'zh';

/** sha1 of a source prose segment (trimmed, newline-normalized). */
export function segKey(text) {
  const norm = String(text).replace(/\r\n/g, '\n').trim();
  return crypto.createHash('sha1').update(norm, 'utf8').digest('hex');
}

/** Load the TM for a locale; returns an empty memory when the file is absent. */
export function loadTm(locale) {
  const file = path.join(docsRoot, 'i18n', `tm.${locale}.json`);
  if (!fs.existsSync(file)) {
    return { locale, sourceLocale: 'zh-Hans', entries: {} };
  }
  const tm = JSON.parse(fs.readFileSync(file, 'utf8'));
  tm.entries = tm.entries || {};
  return tm;
}

/** Persist the TM with stable key ordering (diff-friendly). */
export function saveTm(tm) {
  const dir = path.join(docsRoot, 'i18n');
  fs.mkdirSync(dir, { recursive: true });
  const ordered = {};
  for (const k of Object.keys(tm.entries).sort()) ordered[k] = tm.entries[k];
  const out = { locale: tm.locale, sourceLocale: tm.sourceLocale, entries: ordered };
  fs.writeFileSync(path.join(dir, `tm.${tm.locale}.json`), JSON.stringify(out, null, 2) + '\n', 'utf8');
}

// Tokens that must survive translation verbatim: math/arrow symbols used in
// contract prose (→ × ≈ ≠ ≥ ≤ ± ∈ μ ° ∞ ÷ ↔) plus inline code spans and
// identifiers are protected by the caller extracting them before translation.
const PRESERVED_SYMBOLS = ['→', '↔', '×', '≈', '≠', '≥', '≤', '±', '∈', 'μ', '°', '∞', '÷'];

/** Count preserved symbols for the conservation check. */
export function symbolCounts(text) {
  const counts = {};
  for (const s of PRESERVED_SYMBOLS) {
    const n = text.split(s).length - 1;
    if (n) counts[s] = n;
  }
  return counts;
}

/** Validate a machine translation: preserved-symbol conservation.
 *  Returns null when OK, otherwise a human-readable reason. */
export function validateTranslation(src, dst) {
  if (!dst || !dst.trim()) return 'empty translation';
  const a = symbolCounts(src);
  const b = symbolCounts(dst);
  for (const s of PRESERVED_SYMBOLS) {
    if ((a[s] || 0) !== (b[s] || 0)) {
      return `symbol "${s}" count changed (${a[s] || 0} -> ${b[s] || 0})`;
    }
  }
  return null;
}

// Inline code spans (`...`) carry identifiers/API names that must survive
// verbatim; the free endpoint sometimes rewrites quotes/casing inside them.
// Mask each span to a ⟦C<n>⟧ placeholder before MT and restore afterwards.
export function maskCodeSpans(text) {
  const spans = [];
  const out = text.replace(/`[^`\n]+`/g, (m) => {
    const token = `⟦C${spans.length}⟧`;
    spans.push(m);
    return token;
  });
  return {
    text: out,
    restore(translated) {
      let r = translated;
      spans.forEach((span, i) => { r = r.split(`⟦C${i}⟧`).join(span); });
      // Some engines insert spaces around the placeholder brackets; clean
      // residual tokens only if the engine mangled them beyond recognition.
      return r;
    },
    count: spans.length,
  };
}

// Contract phrases whose meaning must not drift through sentence-level MT.
// Matched on the normalized source; the fixed translation is used verbatim.
const PHRASE_LOCKS = [
  { src: 'S_OK≠被接受', dst: 'S_OK ≠ acceptance' },
];

/** Apply phrase-level locks: replace locked phrases with placeholders before
 *  MT and restore the fixed translations afterwards. Returns
 *  { text, restore(fn) } so the engine never sees the locked phrase. */
export function maskLockedPhrases(text) {
  const masked = [];
  let out = text;
  PHRASE_LOCKS.forEach((lock, i) => {
    if (out.includes(lock.src)) {
      const token = `⟦LOCK${i}⟧`;
      out = out.split(lock.src).join(token);
      masked.push({ token, dst: lock.dst });
    }
  });
  return {
    text: out,
    restore(translated) {
      let r = translated;
      for (const { token, dst } of masked) r = r.split(token).join(dst);
      return r;
    },
  };
}

// Domain glossary applied to machine output (word-boundary, case-insensitive).
// Only unambiguous domain terms: generic words are left to the engine.
const GLOSSARY_EN = [
  [/\bsynthesizer(s)?\b/gi, 'compositor$1'],
  [/\bframe feeding\b/gi, 'frame submission'],
  [/\bpseudo-async\b/gi, 'fake async'],
  [/\bzero copy\b/gi, 'zero-copy'],
  [/\bback pressure\b/gi, 'backpressure'],
];

/** Apply the locale glossary to a machine translation. */
export function applyGlossary(dst, locale) {
  if (locale !== 'en') return dst;
  let r = dst;
  for (const [re, sub] of GLOSSARY_EN) r = r.replace(re, sub);
  return r;
}

/** Create a translator bound to a TM. Rendering is fully offline. */
export function createTranslator(locale, tm) {
  const stale = new Set();
  const isSource = locale === SOURCE_LOCALE;
  return {
    locale,
    stale,
    /** Translate one prose segment; falls back to source text on miss. */
    t(text) {
      if (isSource || !text || !text.trim()) return text;
      // Segments without any CJK characters are already language-neutral.
      if (!/[\u4e00-\u9fff]/.test(text)) return text;
      const key = segKey(text);
      const entry = tm.entries[key];
      if (entry && entry.dst) return entry.dst;
      stale.add(key);
      return text;
    },
  };
}

/** Collect every translatable prose segment of a parsed type (deduped). */
export function collectSegments(type, surfaceMembers, parseDocBlock) {
  const segs = new Set();
  const add = (s) => {
    if (s && /[\u4e00-\u9fff]/.test(s)) segs.add(s.replace(/\r\n/g, '\n').trim());
  };
  add(type.doc.summaryMd);
  add(type.doc.remarksMd);
  add(type.doc.returnsMd);
  for (const p of type.doc.params || []) add(p.text);
  for (const mem of surfaceMembers(type.surface || [])) {
    const doc = parseDocBlock(mem.doc);
    add(doc.summaryMd);
    add(doc.remarksMd);
    add(doc.returnsMd);
    for (const p of doc.params) add(p.text);
  }
  return [...segs];
}