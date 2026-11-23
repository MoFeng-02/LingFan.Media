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
import { walkCs, parseCsFile, sha256, stripBom } from './lib/csharp-doc.mjs';

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

function checkDesensitize() {
  for (const abs of walkDocsMd()) {
    const text = fs.readFileSync(abs, 'utf8');
    const r = rel(abs);
    for (const { name, re } of DESENSITIZE) {
      re.lastIndex = 0;
      const m = re.exec(text);
      if (m) err(`Desensitize (${name}): ${r} contains "${m[0]}"`);
    }
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
