// Documentation drift gate for LingFan.Media docs.
//
// Verifies that the docs stay unified with the source code and follow the
// reader-facing documentation discipline. Run: node scripts/check-docs.mjs
// (or: pnpm docs:check). Exits non-zero when drift or leakage is found, so it
// can be used as a pre-commit / CI gate.
//
// Checks:
//   1. Freshness   — generated reference matches current source hashes.
//   2. Coverage    — every public contract type has a generated page (en + zh).
//   3. Provenance  — hand-written guide pages declare existing sources/symbols.
//   4. Symbol drift— interface-like tokens in prose resolve to real source symbols.
//   5. Desensitize — no absolute paths, internal codenames, crash hex, emoji,
//                    credentials or internal narrative reach the docs.
//
// No third-party dependencies.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { walkCs, parseCsFile, sha256, stripBom, parseDocBlock } from './lib/csharp-doc.mjs';
import { segKey, collectSegments } from './lib/i18n.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const docsRoot = path.resolve(here, '..');
const repoRoot = path.resolve(docsRoot, '..');

const errors = [];
const warnings = [];
const err = (m) => errors.push(m);
const warn = (m) => warnings.push(m);

function readManifest() {
  const p = path.join(docsRoot, 'api/manifest.json');
  if (!fs.existsSync(p)) {
    err('api/manifest.json missing — run `pnpm docs:gen` first.');
    return null;
  }
  return JSON.parse(fs.readFileSync(p, 'utf8'));
}

/** Collect every identifier appearing anywhere under src (cheap word scan).
 *  A docs symbol is valid iff it exists in the codebase — this accepts BCL and
 *  framework interfaces (IDisposable, IHttpClientFactory, ...) that are used in
 *  source, while rejecting invented names (IDemuxer, IFrameSink, ...). */
function buildSourceWordIndex() {
  const set = new Set();
  const srcDir = path.join(repoRoot, 'src');
  const re = /\b[A-Za-z_][A-Za-z0-9_]*\b/g;
  for (const abs of walkCs(srcDir)) {
    let text;
    try {
      text = stripBom(fs.readFileSync(abs, 'utf8'));
    } catch {
      continue;
    }
    let m;
    while ((m = re.exec(text))) set.add(m[0]);
  }
  return set;
}

/** Parse the small, controlled frontmatter format emitted by the generator/authored pages. */
function parseFrontmatter(text) {
  const m = text.match(/^---\r?\n([\s\S]*?)\r?\n---/);
  if (!m) return {};
  const body = m[1];
  const out = {};
  for (const line of body.split(/\r?\n/)) {
    const kv = line.match(/^(\w+):\s*(.*)$/);
    if (!kv) continue;
    const key = kv[1];
    let val = kv[2].trim();
    if (val.startsWith('[')) {
      try {
        out[key] = JSON.parse(val);
      } catch {
        out[key] = val;
      }
    } else if (val === 'true' || val === 'false') {
      out[key] = val === 'true';
    } else if (val.startsWith('"')) {
      try {
        out[key] = JSON.parse(val);
      } catch {
        out[key] = val;
      }
    } else {
      out[key] = val;
    }
  }
  return out;
}

/** Walk docs markdown, skipping build/cache/dependency dirs. */
function walkDocsMd() {
  const out = [];
  const skip = new Set(['node_modules', 'dist', 'cache', '.vitepress']);
  const stack = [docsRoot];
  while (stack.length) {
    const cur = stack.pop();
    let entries;
    try {
      entries = fs.readdirSync(cur, { withFileTypes: true });
    } catch {
      continue;
    }
    for (const e of entries) {
      if (e.name.startsWith('.') && e.name !== '.vitepress') continue;
      if (skip.has(e.name)) continue;
      const full = path.join(cur, e.name);
      if (e.isDirectory()) {
        if (e.name === '.vitepress') continue; // config/theme, not prose
        stack.push(full);
      } else if (e.isFile() && e.name.endsWith('.md')) out.push(full);
    }
  }
  return out.sort();
}

const rel = (abs) => path.relative(docsRoot, abs).split(path.sep).join('/');

// ---------- 1 & 2: freshness + coverage ----------
function checkFreshnessAndCoverage(manifest, projects) {
  if (!manifest) return;
  const manifestSources = new Map(manifest.pages.map((p) => [p.source, p]));
  const seen = new Set();
  for (const proj of projects) {
    const projAbs = path.join(repoRoot, proj.dir);
    for (const abs of walkCs(projAbs)) {
      const parsed = parseCsFile(abs, repoRoot);
      if (!parsed.types.length) continue;
      seen.add(parsed.relSource);
      const entry = manifestSources.get(parsed.relSource);
      if (!entry) {
        err(`Coverage: source ${parsed.relSource} has public types but no generated page (run docs:gen).`);
        continue;
      }
      if (entry.sourceHash !== parsed.hash) {
        err(`Stale: ${parsed.relSource} changed since generation (run docs:gen).`);
      }
      for (const locale of ['', 'zh/']) {
        const pageFile = path.join(docsRoot, (locale ? 'zh/' : '') + entry.page + '.md');
        if (!fs.existsSync(pageFile)) err(`Missing generated page: ${rel(pageFile)}`);
      }
    }
  }
  for (const p of manifest.pages) {
    if (!seen.has(p.source)) {
      const abs = path.join(repoRoot, p.source);
      if (!fs.existsSync(abs)) err(`Orphan: manifest references removed source ${p.source} (run docs:gen).`);
    }
  }
}

// ---------- 3 & 4: provenance + symbol drift ----------
function checkGuidePages(manifest, sourceWords) {
  const memberIndex = sourceWords;
  const mdFiles = walkDocsMd();
  const iTokenRe = /`((?:I[A-Z][A-Za-z0-9]+))`/g;
  for (const abs of mdFiles) {
    const text = fs.readFileSync(abs, 'utf8');
    const fm = parseFrontmatter(text);
    const r = rel(abs);
    const isGenerated = fm.generated === true;
    const isGuide = /(^|\/)guide\//.test(r) || /(^|\/)index\.md$/.test(r);

    // provenance for hand-written guide pages
    if (isGuide && !isGenerated) {
      const sources = Array.isArray(fm.sources) ? fm.sources : [];
      const symbols = Array.isArray(fm.symbols) ? fm.symbols : [];
      if (!sources.length && !/documentation-system/.test(r)) {
        warn(`Provenance: ${r} has no sources frontmatter.`);
      }
      for (const s of sources) {
        if (!fs.existsSync(path.join(repoRoot, s))) err(`Provenance: ${r} sources path does not exist: ${s}`);
      }
      for (const sym of symbols) {
        if (!memberIndex.has(sym)) err(`Provenance: ${r} symbol not found in source: ${sym}`);
      }
    }

    // symbol drift: interface-like backtick tokens must resolve (skip generated source links)
    if (!isGenerated) {
      let m;
      while ((m = iTokenRe.exec(text))) {
        const tok = m[1];
        if (!memberIndex.has(tok)) err(`Symbol drift: ${r} references unknown contract symbol \`${tok}\``);
      }
    }
  }
}

// ---------- 5: desensitization ----------
// Two rule groups:
//   DESENSITIZE       — applies to every docs markdown page.
//   DESENSITIZE_EXTRA — codename families that only make sense in API prose;
//                       applied to generated reference pages and to the ///
//                       doc comments of the projects that feed them.
// Whitelists guard known public terms so format/version/channel names do not
// false-positive (pixel & sample formats, HE-AAC v2, AudioUnit v2, Vulkan API
// versions, RFC1918 literals in SSRF docs, named-constant hex definitions).

const PUBLIC_TERM_CONTEXT = /(?:HE-AAC|AudioUnit|Vulkan|API|vulkan)\s*$/i;
const FORMAT_TOKENS = new Set([
  'S16', 'S32', 'S8', 'F32', 'F16', 'R8', 'R8G8', 'GR88', 'B8', 'A8', 'L0', 'R0',
  'U00', 'U01', 'U10', 'U11',
]);

const DESENSITIZE = [
  { name: 'section symbol', re: /\u00A7/g },
  { name: 'absolute path', re: /\b[A-Za-z]:[\\/]/g },
  { name: 'crash hex code', re: /\b0x[8C][0-9A-Fa-f]{7}\b/g },
  {
    name: 'internal codename',
    re: /\b(?:VK-[A-Z]+|LOC-SEEK|ANDROID-FEED|FFPROBE-[A-Z]+|CONTRACT-AUDIT|R-\d+|T\d~T\d|V[12](?:-\d+)?)\b/g,
  },
  { name: 'emoji', re: /[\u{1F300}-\u{1FAFF}\u{2600}-\u{27BF}\u{2B00}-\u{2BFF}\u{FE0F}]/gu },
  { name: 'credential in URL', re: /[?&](?:token|Signature|sig|se|sp|Expires|OSSAccessKeyId)=/gi },
  { name: 'private key', re: /-----BEGIN [A-Z ]*PRIVATE KEY-----/g },
  { name: 'private IP', re: /\b(?:10\.\d{1,3}|192\.168|172\.(?:1[6-9]|2\d|3[01]))\.\d{1,3}\.\d{1,3}\b/g },
  { name: 'internal narrative', re: /(?:我们排查|我们发现|踩过|复审|拍板|留档|整轮|排查过程|调查过程)/g },
];

const DESENSITIZE_EXTRA = [
  { name: 'B-family codename', re: /\bB-[A-Z0-9]+\b/g },
  { name: 'AU-family codename', re: /\bAU\d+\b/g },
  {
    name: 'letter-digit codename',
    re: /\b(?:B\d{1,2}|L\d{1,2}(?:-L\d{1,2})?|[ESOA]\d{1,2}|U\d{1,2})\b/g,
    allow: (m) => FORMAT_TOKENS.has(m[0]),
  },
  {
    name: 'lowercase version codename',
    re: /\bv[12]\b/g,
    allow: (m, text) => PUBLIC_TERM_CONTEXT.test(text.slice(Math.max(0, m.index - 24), m.index)),
  },
  { name: 'C-line label', re: /C线/g },
  { name: 'phase label', re: /\bPhase\s*\d+[a-z]?\b/gi },
  { name: 'tier label', re: /\bTier\s*\d\b/gi },
  { name: 'gate label', re: /\bGate\s*\d\b/gi },
  { name: 'plan label', re: /[A-Z]\s?方案/g },
  { name: 'root-cure wording', re: /治本|隐患[AB]/g },
  { name: 'dispose channel codename', re: /\bDECODER-DISPOSE\b/g },
  { name: 'b-number marker', re: /\(b\)\(\d+\)/g },
  { name: 'headless-A label', re: /无头\s*A\s*形态/g },
  { name: 'category table term', re: /\b(?:config|sync|native)\s*分类/g },
];

/** First violating match of one rule, honoring its allow callback. */
function firstViolation(rule, text) {
  rule.re.lastIndex = 0;
  let m;
  while ((m = rule.re.exec(text))) {
    if (rule.allow && rule.allow(m, text)) continue;
    return m;
  }
  return null;
}

function checkDesensitize() {
  for (const abs of walkDocsMd()) {
    const text = fs.readFileSync(abs, 'utf8');
    const r = rel(abs);
    for (const rule of DESENSITIZE) {
      const m = firstViolation(rule, text);
      if (m) err(`Desensitize (${rule.name}): ${r} contains "${m[0]}"`);
    }
    const isGenerated = parseFrontmatter(text).generated === true;
    if (isGenerated) {
      for (const rule of DESENSITIZE_EXTRA) {
        const m = firstViolation(rule, text);
        if (m) err(`Desensitize (${rule.name}): ${r} contains "${m[0]}"`);
      }
    }
  }
}

/** Scan /// doc comments of the projects feeding the public reference.
 *  Body // comments are intentionally out of scope here (they never reach
 *  docs); line-level exemptions keep named-constant hex definitions and
 *  RFC1918 literals in SSRF-guard prose from false-positiving. */
function checkSourceDocComments(projects) {
  const constHexLine = /(?:const|static\s+readonly|=\s*)[^;]*\b0x[8C][0-9A-Fa-f]{7}\b/;
  const rfc1918Line = /RFC\s*1918|私网|私有网|环回|link-local|loopback/i;
  for (const proj of projects) {
    for (const abs of walkCs(path.join(repoRoot, proj.dir))) {
      const lines = stripBom(fs.readFileSync(abs, 'utf8')).split(/\r?\n/);
      const relSrc = path.relative(repoRoot, abs).split(path.sep).join('/');
      for (let i = 0; i < lines.length; i++) {
        const t = lines[i].trimStart();
        if (!t.startsWith('///')) continue;
        const body = t.slice(3);
        for (const rule of [...DESENSITIZE, ...DESENSITIZE_EXTRA]) {
          if (rule.name === 'crash hex code' && constHexLine.test(lines[i])) continue;
          if (rule.name === 'private IP' && rfc1918Line.test(body)) continue;
          if (rule.name === 'section symbol' && /ISO\/IEC|VUID|\.h\b|规范|spec/i.test(body)) continue;
          const m = firstViolation(rule, body);
          if (m) err(`Desensitize src (${rule.name}): ${relSrc}:${i + 1} contains "${m[0]}"`);
        }
      }
    }
  }
}

// ---------- 6: translation freshness (warning-level) ----------
// Non-source locales render from the translation memory; missing/stale
// segments fall back to source text at generation time. This check reports
// the gap without blocking: refresh with `node scripts/translate.mjs`.
function checkTranslationFreshness(projects) {
  const i18nDir = path.join(docsRoot, 'i18n');
  if (!fs.existsSync(i18nDir)) return;
  for (const f of fs.readdirSync(i18nDir)) {
    const m = f.match(/^tm\.(\w+)\.json$/);
    if (!m || m[1] === 'zh') continue;
    const locale = m[1];
    const tm = JSON.parse(fs.readFileSync(path.join(i18nDir, f), 'utf8'));
    const segs = new Set();
    const add = (s) => { if (s && /[\u4e00-\u9fff]/.test(s)) segs.add(s.replace(/\r\n/g, '\n').trim()); };
    const surfaceMembers = (surface) => {
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
    };
    for (const proj of projects) {
      for (const abs of walkCs(path.join(repoRoot, proj.dir))) {
        const parsed = parseCsFile(abs, repoRoot);
        for (const type of parsed.types) {
          for (const s of collectSegments(type, surfaceMembers, parseDocBlock)) add(s);
        }
      }
    }
    let stale = 0;
    for (const s of segs) {
      const e = tm.entries[segKey(s)];
      if (!e || !e.dst) stale++;
    }
    if (stale) warn(`i18n ${locale}: ${stale}/${segs.size} segment(s) untranslated or stale — run: node scripts/translate.mjs --locale ${locale}`);
  }
}

// ---------- run ----------
const manifest = readManifest();
const projects = manifest ? manifest.projects : [
  { key: 'abstractions', dir: 'src/Infrastructure/LingFan.Media.Abstractions' },
  { key: 'extensions', dir: 'src/Infrastructure/LingFan.Media.Extensions' },
];
const sourceWords = buildSourceWordIndex();
checkFreshnessAndCoverage(manifest, projects);
checkGuidePages(manifest, sourceWords);
checkDesensitize();
checkSourceDocComments(projects);
checkTranslationFreshness(projects);

console.log(`Checked docs against source (${sourceWords.size} source identifiers indexed).`);
if (warnings.length) {
  console.log(`\n${warnings.length} warning(s):`);
  for (const w of warnings) console.log('  WARN  ' + w);
}
if (errors.length) {
  console.log(`\n${errors.length} error(s):`);
  for (const e of errors) console.log('  ERROR ' + e);
  console.log('\nDocs drift/leakage detected. Fix and re-run, or regenerate with `pnpm docs:gen`.');
  process.exit(1);
} else {
  console.log('\nOK: docs are unified with source and clean.');
}
