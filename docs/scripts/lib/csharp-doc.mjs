// Shared C# documentation parser for the LingFan.Media docs toolchain.
//
// Single source of truth: the public API surface and its /// XML doc comments
// live in the source code. This module READS source (read-only) and extracts a
// faithful, structured representation so that generated reference pages can
// never drift from the code they describe.
//
// It deliberately extracts only `///` doc comments (never `//` body comments),
// because body comments may carry internal narrative that must not reach docs.
//
// No third-party dependencies: Node built-ins only.

import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';

/** Recursively collect .cs files under dir, skipping bin/obj. Returns absolute paths sorted. */
export function walkCs(dir) {
  const out = [];
  const stack = [dir];
  while (stack.length) {
    const cur = stack.pop();
    let entries;
    try {
      entries = fs.readdirSync(cur, { withFileTypes: true });
    } catch {
      continue;
    }
    for (const e of entries) {
      if (e.name === 'bin' || e.name === 'obj' || e.name.startsWith('.')) continue;
      const full = path.join(cur, e.name);
      if (e.isDirectory()) stack.push(full);
      else if (e.isFile() && e.name.endsWith('.cs')) out.push(full);
    }
  }
  return out.sort();
}

export function stripBom(s) {
  return s.charCodeAt(0) === 0xfeff ? s.slice(1) : s;
}

export function sha256(s) {
  return crypto.createHash('sha256').update(s, 'utf8').digest('hex');
}

function decodeEntities(s) {
  return s
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/&apos;/g, "'")
    .replace(/&amp;/g, '&');
}

/**
 * Convert a fragment of C# XML doc markup into Markdown.
 * Handles the tags actually used in this codebase: summary/remarks/para/b/c/
 * code/see(cref|langword)/paramref/list/item/inheritdoc/returns.
 */
export function xmlToMd(xml) {
  if (!xml) return '';
  let s = xml;

  // <code>...</code> -> fenced block (protect content from inline transforms)
  const codeBlocks = [];
  s = s.replace(/<code>([\s\S]*?)<\/code>/g, (_, inner) => {
    const idx = codeBlocks.length;
    codeBlocks.push(inner.replace(/^\s*\n/, '').replace(/\s+$/, ''));
    return `\u0000CODE${idx}\u0000`;
  });

  // <list type="bullet|number"><item>...</item>...</list>
  s = s.replace(/<list[^>]*>([\s\S]*?)<\/list>/g, (_, inner) => {
    const items = [];
    const re = /<item>([\s\S]*?)<\/item>/g;
    let m;
    while ((m = re.exec(inner))) items.push(m[1].trim());
    if (!items.length) return inner.trim();
    return '\n' + items.map((it) => `- ${it}`).join('\n') + '\n';
  });

  // <para>...</para> -> paragraph breaks
  s = s.replace(/<para>([\s\S]*?)<\/para>/g, (_, inner) => `\n\n${inner.trim()}\n\n`);

  // inline references
  s = s.replace(/<see\s+cref="([^"]+)"\s*\/>/g, (_, cref) => {
    const short = cref.replace(/^.*\./, '');
    return '`' + short + '`';
  });
  s = s.replace(/<see\s+langword="([^"]+)"\s*\/>/g, (_, w) => '`' + w + '`');
  s = s.replace(/<paramref\s+name="([^"]+)"\s*\/>/g, (_, n) => '`' + n + '`');
  s = s.replace(/<c>([\s\S]*?)<\/c>/g, (_, inner) => '`' + inner.trim() + '`');
  s = s.replace(/<b>([\s\S]*?)<\/b>/g, (_, inner) => `**${inner.trim()}**`);
  s = s.replace(/<inheritdoc\s*\/>/g, '(inherits base documentation)');

  // drop remaining tags (summary/remarks wrappers etc.)
  s = s.replace(/<\/?[a-zA-Z][^>]*>/g, '');

  s = decodeEntities(s);

  // restore code blocks
  s = s.replace(/\u0000CODE(\d+)\u0000/g, (_, idx) => {
    return '\n\n```csharp\n' + codeBlocks[Number(idx)] + '\n```\n\n';
  });

  // normalize whitespace: collapse 3+ newlines, trim trailing spaces per line
  s = s
    .split('\n')
    .map((l) => l.replace(/[ \t]+$/g, ''))
    .join('\n')
    .replace(/\n{3,}/g, '\n\n')
    .trim();
  return s;
}

/** Parse an accumulated /// block (raw text without the leading ///) into parts. */
export function parseDocBlock(raw) {
  const text = raw.join('\n');
  const grab = (tag) => {
    const m = text.match(new RegExp(`<${tag}>([\\s\\S]*?)</${tag}>`));
    return m ? m[1].trim() : '';
  };
  const summary = grab('summary');
  const remarks = grab('remarks');
  const returns = grab('returns');
  const params = [];
  const pre = /<param\s+name="([^"]+)">([\s\S]*?)<\/param>/g;
  let m;
  while ((m = pre.exec(text))) params.push({ name: m[1], text: m[2].trim() });
  const inheritdoc = /<inheritdoc\s*\/>/.test(text);
  return {
    summaryRaw: summary,
    remarksRaw: remarks,
    returnsRaw: returns,
    params,
    inheritdoc,
    summaryMd: xmlToMd(summary),
    remarksMd: xmlToMd(remarks),
    returnsMd: xmlToMd(returns),
  };
}

const TYPE_RE =
  /^public\s+((?:(?:sealed|abstract|static|partial|readonly|ref|new|unsafe)\s+)*)(interface|class|record|struct|enum)\s+(\w+)([\s\S]*)$/;

/** Find the index of the matching closing brace for the opening brace at openIdx. */
function matchBrace(lines, openIdx) {
  let depth = 0;
  for (let i = openIdx; i < lines.length; i++) {
    for (const ch of lines[i]) {
      if (ch === '{') depth++;
      else if (ch === '}') {
        depth--;
        if (depth === 0) return i;
      }
    }
  }
  return lines.length - 1;
}

/** Extract positional parameter names from a record declaration signature. */
function parsePositionalParams(declSig) {
  const m = declSig.match(/\(([\s\S]*)\)/);
  if (!m) return [];
  const inner = m[1];
  const parts = [];
  let depth = 0;
  let cur = '';
  for (const ch of inner) {
    if (ch === '<' || ch === '(') depth++;
    else if (ch === '>' || ch === ')') depth--;
    if (ch === ',' && depth === 0) {
      parts.push(cur);
      cur = '';
    } else cur += ch;
  }
  if (cur.trim()) parts.push(cur);
  const names = [];
  for (const p of parts) {
    const id = p.trim().match(/(\w+)\s*(?:=[\s\S]*)?$/);
    if (id) names.push(id[1]);
  }
  return names;
}

/** True if a trimmed interface-body line looks like a member declaration. */
function isInterfaceMember(t) {
  if (!t || t.startsWith('///') || t.startsWith('//') || t.startsWith('[')) return false;
  if (t === '{' || t === '}' || t === ';') return false;
  // method/property/event/indexer declaration
  return /[({;]/.test(t) || /^event\s/.test(t);
}

/**
 * Extract the verbatim public surface of a type body as C# lines (/// docs kept,
 * private members and method bodies dropped). Faithful to source => zero drift.
 */
function extractSurface(lines, bodyStart, bodyEnd, kind) {
  const out = [];
  let pendingDoc = [];
  let i = bodyStart + 1;
  const flushDoc = () => {
    if (pendingDoc.length) out.push(...pendingDoc);
    pendingDoc = [];
  };
  while (i < bodyEnd) {
    const raw = lines[i];
    const t = raw.trim();
    if (t.startsWith('///')) {
      pendingDoc.push(raw);
      i++;
      continue;
    }
    if (t === '' || t.startsWith('//') || t.startsWith('[') || t === '{' || t === '}') {
      i++;
      continue;
    }
    const isMember =
      kind === 'interface'
        ? isInterfaceMember(t)
        : kind === 'enum'
          ? /^\w+\s*(=|,|$)/.test(t)
          : /^public\s/.test(t);
    if (!isMember) {
      // non-public member: drop its doc; skip a body if it opens one
      pendingDoc = [];
      if (t.includes('{') && !/\{\s*(get|set|init|add|remove)\b/.test(t)) {
        const close = matchBrace(lines, i);
        i = close + 1;
      } else {
        i++;
      }
      continue;
    }
    // capture the member signature (may span multiple lines) up to ';' or a body '{'
    flushDoc();
    if (kind === 'enum') {
      // each enum value line is a complete member
      out.push(raw);
      i++;
      continue;
    }
    const sigLines = [raw];
    let j = i;
    let line = t;
    // auto-property / interface accessor on one line: keep as-is
    const oneLineAccessor = /\{\s*(get|set|init|add|remove)[^}]*\}\s*;?\s*$/.test(line);
    if (oneLineAccessor || line.endsWith(';')) {
      out.push(raw);
      i = j + 1;
      continue;
    }
    // multi-line signature: advance until ';' or body '{'
    while (j < bodyEnd && !line.endsWith(';') && !line.endsWith('{')) {
      j++;
      if (j >= bodyEnd) break;
      sigLines.push(lines[j]);
      line = lines[j].trim();
    }
    if (line.endsWith('{')) {
      // method/ctor body: render signature ending in ';' and skip the body
      let sig = sigLines.join('\n').replace(/\n\s*\{\s*$/, '').replace(/\{\s*$/, '');
      sig = sig.trimEnd() + ';';
      out.push(sig);
      const close = matchBrace(lines, j);
      i = close + 1;
    } else {
      out.push(sigLines.join('\n'));
      i = j + 1;
    }
  }
  return out;
}

/** Collect public member names (for the symbol index used by the drift checker). */
function collectMemberNames(lines, bodyStart, bodyEnd, kind) {
  const names = [];
  let i = bodyStart + 1;
  while (i < bodyEnd) {
    const t = lines[i].trim();
    if (t.startsWith('///') || t.startsWith('//') || t.startsWith('[') || t === '' || t === '{' || t === '}') {
      i++;
      continue;
    }
    const isMember =
      kind === 'interface'
        ? isInterfaceMember(t)
        : kind === 'enum'
          ? /^\w+\s*(=|,|$)/.test(t)
          : /^public\s/.test(t);
    if (isMember) {
      let m;
      if (kind === 'enum') {
        m = t.match(/^(\w+)\s*(=|,|$)/);
      } else {
        // event Type Name / Type Name(...) / Type Name { / Name(...)
        m =
          t.match(/^event\s+[\w<>\[\]?,.\s]+?\s+(\w+)\s*[;{(]/) ||
          t.match(/(?:^public\s+)?[\w<>\[\]?,.\s]+?\s+(\w+)\s*\(/) ||
          t.match(/(?:^public\s+)?[\w<>\[\]?,.\s]+?\s+(\w+)\s*\{/) ||
          t.match(/(?:^public\s+)?[\w<>\[\]?,.\s]+?\s+(\w+)\s*;/);
      }
      if (m) names.push(m[1]);
      // skip body
      if (t.includes('{') && !/\{\s*(get|set|init|add|remove)\b/.test(t)) {
        i = matchBrace(lines, i) + 1;
        continue;
      }
    }
    i++;
  }
  return names;
}

/** Parse one .cs file into { relSource, namespace, hash, types[] }. */
export function parseCsFile(absPath, repoRoot) {
  const rawText = fs.readFileSync(absPath, 'utf8');
  const text = stripBom(rawText);
  const lines = text.split(/\r?\n/);
  const relSource = path.relative(repoRoot, absPath).split(path.sep).join('/');
  const nsMatch = text.match(/namespace\s+([\w.]+)\s*[;{]/);
  const namespace = nsMatch ? nsMatch[1] : '';
  const types = [];

  let pendingDoc = [];
  let i = 0;
  while (i < lines.length) {
    const t = lines[i].trim();
    if (t.startsWith('///')) {
      pendingDoc.push(t.replace(/^\/\/\/\s?/, ''));
      i++;
      continue;
    }
    const m = t.match(TYPE_RE);
    if (m) {
      const kind = m[2];
      const name = m[3];
      // gather full declaration signature up to body '{' or ';'
      const sigLines = [lines[i]];
      let j = i;
      let acc = t;
      while (j < lines.length && !acc.includes('{') && !acc.includes(';')) {
        j++;
        if (j >= lines.length) break;
        sigLines.push(lines[j]);
        acc += '\n' + lines[j].trim();
      }
      const doc = parseDocBlock(pendingDoc);
      pendingDoc = [];
      const opensBody = acc.includes('{');
      let surface = [];
      let memberNames = [];
      let bodyEnd = j;
      if (opensBody) {
        const braceLine = j;
        bodyEnd = matchBrace(lines, braceLine);
        surface = extractSurface(lines, braceLine, bodyEnd, kind);
        memberNames = collectMemberNames(lines, braceLine, bodyEnd, kind);
      }
      // declaration signature without the trailing body brace
      let declSig = sigLines.join('\n');
      if (opensBody) declSig = declSig.replace(/\{\s*$/, '').trimEnd();
      else declSig = declSig.replace(/;\s*$/, ';');
      // positional record without a body: the declaration carries the surface,
      // and its positional parameters are the public members.
      if (!opensBody && kind === 'record') {
        memberNames = doc.params.length
          ? doc.params.map((p) => p.name)
          : parsePositionalParams(declSig);
      }
      types.push({
        kind,
        name,
        namespace,
        declaration: declSig,
        doc,
        surface,
        memberNames,
        relSource,
      });
      i = bodyEnd + 1;
      continue;
    }
    // any other top-level line resets pending doc (using/namespace/attribute handled loosely)
    if (t === '' || t.startsWith('//') || t.startsWith('using ') || t.startsWith('namespace ') || t.startsWith('[')) {
      // keep pendingDoc across attributes/blank; reset on using/namespace/comment
      if (t.startsWith('using ') || t.startsWith('namespace ') || t.startsWith('//')) pendingDoc = [];
      i++;
      continue;
    }
    pendingDoc = [];
    i++;
  }

  return { relSource, namespace, hash: sha256(text), types };
}
