// Generates the API reference from source code (single source of truth).
//
// Reads the public contracts (LingFan.Media.Abstractions) and the composition
// root (LingFan.Media.Extensions) READ-ONLY, extracts their public surface and
// /// doc comments, and renders one reference page per source file in both
// locales. Signatures and doc prose come straight from source, so the reference
// cannot drift from the code. Also emits:
//   - docs/api/manifest.json        (machine/AI index: type -> page -> source)
//   - docs/.vitepress/api-sidebar.json (navigation derived from the same data)
//
// Run: node scripts/gen-api.mjs   (or: pnpm docs:gen)
// No third-party dependencies.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { walkCs, parseCsFile, parseDocBlock } from './lib/csharp-doc.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const docsRoot = path.resolve(here, '..');
const repoRoot = path.resolve(docsRoot, '..');
const REPO_URL = 'https://github.com/MoFeng-02/LingFan.Media/blob/main/';

const PROJECTS = [
  {
    key: 'abstractions',
    dir: 'src/Infrastructure/LingFan.Media.Abstractions',
    titleEn: 'Abstractions',
    titleZh: '抽象契约层',
    descEn:
      'The contract layer: interfaces, models, enums, events and exceptions. Zero implementation, zero external references beyond the BCL and logging abstractions.',
    descZh:
      '契约层：接口、模型、枚举、事件与异常。零实现，除 BCL 与日志抽象外零外部引用。',
  },
  {
    key: 'extensions',
    dir: 'src/Infrastructure/LingFan.Media.Extensions',
    titleEn: 'Extensions',
    titleZh: '组合根扩展',
    descEn:
      'The composition root: dependency-injection registration entry points (AddLingFanMedia and the per-module Add* extensions).',
    descZh:
      '组合根：依赖注入注册入口（AddLingFanMedia 与各模块 Add* 扩展）。',
  },
];

const L = {
  en: {
    source: 'Source',
    declaration: 'Declaration',
    members: 'Members',
    remarks: 'Remarks',
    values: 'Values',
    parameters: 'Parameters',
    returns: 'Returns',
    value: 'Value',
    description: 'Description',
    constructor: 'Constructor',
    inherits: 'Inherits documentation from its base contract.',
    catalog: 'Type catalog',
    kind: 'Kind',
    type: 'Type',
    summary: 'Summary',
  },
  zh: {
    source: '源码',
    declaration: '声明',
    members: '成员',
    remarks: '备注',
    values: '枚举值',
    parameters: '参数',
    returns: '返回',
    value: '值',
    description: '说明',
    constructor: '构造函数',
    inherits: '继承自基契约的文档。',
    catalog: '类型目录',
    kind: '类别',
    type: '类型',
    summary: '摘要',
  },
};

function slugFor(relWithinProject) {
  return relWithinProject
    .replace(/\.cs$/i, '')
    .toLowerCase()
    .replace(/[\\/]/g, '-');
}

function memberName(sig, kind, typeName) {
  const t = sig.trim();
  if (kind === 'enum') return (t.match(/^(\w+)/) || [])[1] || t;
  if (new RegExp('^public\\s+' + typeName + '\\s*\\(').test(t)) return 'Constructor';
  let m = t.match(/^event\s+[\w<>\[\]?,.\s]+?\s+(\w+)\s*[;{(]/);
  if (m) return m[1];
  m =
    t.match(/(\w+)\s*\(/) ||
    t.match(/(\w+)\s*\{/) ||
    t.match(/(\w+)\s*;/) ||
    t.match(/(\w+)\s*=>/);
  return m ? m[1] : t.split('\n')[0];
}

/** Group the verbatim surface lines into { doc: string[], sig: string[] } members. */
function surfaceMembers(surface) {
  const members = [];
  let cur = { doc: [], sig: [] };
  for (const line of surface) {
    if (line.trim().startsWith('///')) {
      if (cur.sig.length) {
        members.push(cur);
        cur = { doc: [], sig: [] };
      }
      cur.doc.push(line.trim().replace(/^\/\/\/\s?/, ''));
    } else {
      cur.sig.push(line);
    }
  }
  if (cur.sig.length || cur.doc.length) members.push(cur);
  return members;
}

function fence(code) {
  return '```csharp\n' + code.replace(/\s+$/, '') + '\n```';
}

// VitePress compiles markdown as Vue templates, so a raw generic such as
// Memory<byte> in prose is parsed as an unclosed <byte> element and breaks the
// build. Escape < and > to entities ONLY in prose text — fenced code blocks and
// inline code spans are preserved verbatim (markdown-it escapes those itself).
function escProse(md) {
  if (!md) return md;
  return md.replace(/(```[\s\S]*?```|`[^`\n]*`)|([<>])/g, (m, code, ch) => {
    if (code) return code;
    return ch === '<' ? '&lt;' : '&gt;';
  });
}

function paramList(params, locale) {
  if (!params.length) return '';
  const t = L[locale];
  const items = params
    .map((p) => `- \`${p.name}\`：${escProse(p.text)}`.replace('：', locale === 'en' ? ': ' : '：'))
    .join('\n');
  return `**${t.parameters}**\n\n${items}\n`;
}

function renderMemberBlock(mem, kind, typeName, locale) {
  const t = L[locale];
  const sig = mem.sig.join('\n').replace(/\s+$/, '').replace(/^ {4}/gm, '');
  const doc = parseDocBlock(mem.doc);
  const name = memberName(sig, kind, typeName);
  const heading = name === 'Constructor' ? t.constructor : name;
  const parts = [`### ${heading}`, '', fence(sig), ''];
  if (doc.summaryMd) parts.push(escProse(doc.summaryMd), '');
  if (doc.inheritdoc && !doc.summaryMd) parts.push(t.inherits, '');
  if (doc.remarksMd) parts.push(escProse(doc.remarksMd), '');
  if (doc.params.length) parts.push(paramList(doc.params, locale));
  if (doc.returnsMd) parts.push(`**${t.returns}**：${escProse(doc.returnsMd)}`.replace('：', locale === 'en' ? ': ' : '：'), '');
  return parts.join('\n').replace(/\n{3,}/g, '\n\n').trimEnd();
}

function renderEnumValues(surface, locale) {
  const t = L[locale];
  const rows = surfaceMembers(surface).map((mem) => {
    const sig = mem.sig.join('\n').trim();
    const name = (sig.match(/^(\w+)/) || [])[1] || sig;
    const doc = parseDocBlock(mem.doc);
    const desc = escProse(doc.summaryMd || '').replace(/\|/g, '\\|').replace(/\n+/g, ' ');
    return `| \`${name}\` | ${desc} |`;
  });
  return [`| ${t.value} | ${t.description} |`, '| --- | --- |', ...rows].join('\n');
}

function renderType(type, locale) {
  const t = L[locale];
  const parts = [];
  parts.push(`# ${type.name}`, '');
  parts.push(
    `${t.source}: [\`${path.basename(type.relSource)}\`](${REPO_URL}${type.relSource})`,
    '',
  );
  if (type.doc.summaryMd) parts.push(escProse(type.doc.summaryMd), '');

  // Declaration
  parts.push(`## ${t.declaration}`, '');
  const decl = type.declaration.replace(/\s+$/, '');
  if (type.kind === 'record' && !type.surface.length) {
    parts.push(fence(`namespace ${type.namespace};\n\n${decl}`), '');
    if (type.doc.params.length) parts.push(paramList(type.doc.params, locale));
  } else {
    parts.push(fence(`namespace ${type.namespace};\n\n${decl}`), '');
  }

  if (type.doc.remarksMd) parts.push(`## ${t.remarks}`, '', escProse(type.doc.remarksMd), '');

  if (type.kind === 'enum') {
    parts.push(`## ${t.values}`, '', renderEnumValues(type.surface, locale), '');
  } else if (type.surface.length) {
    parts.push(`## ${t.members}`, '');
    for (const mem of surfaceMembers(type.surface)) {
      if (!mem.sig.length) continue;
      parts.push(renderMemberBlock(mem, type.kind, type.name, locale), '');
    }
  }
  return parts.join('\n').replace(/\n{3,}/g, '\n\n').trimEnd() + '\n';
}

function frontmatter(obj) {
  const lines = ['---'];
  for (const [k, v] of Object.entries(obj)) {
    if (Array.isArray(v)) lines.push(`${k}: [${v.map((x) => JSON.stringify(x)).join(', ')}]`);
    else if (typeof v === 'string') lines.push(`${k}: ${JSON.stringify(v)}`);
    else lines.push(`${k}: ${v}`);
  }
  lines.push('---', '');
  return lines.join('\n');
}

function pageFrontmatter(type, page, locale) {
  return frontmatter({
    title: type.name,
    type: type.kind,
    namespace: type.namespace,
    source: type.relSource,
    generated: true,
    generator: 'scripts/gen-api.mjs',
    symbols: [...new Set([type.name, ...type.memberNames])],
    lang: locale === 'zh' ? 'zh-CN' : 'en-US',
  });
}

function ensureDir(p) {
  fs.mkdirSync(p, { recursive: true });
}

function rmDir(p) {
  fs.rmSync(p, { recursive: true, force: true });
}

// ---- collect ----
// Clean previous generated output so removed source files leave no stale pages.
for (const proj of PROJECTS) {
  rmDir(path.join(docsRoot, 'api', proj.key));
  rmDir(path.join(docsRoot, 'zh', 'api', proj.key));
}
const allTypes = [];
const pages = [];
const sidebar = { en: [], zh: [] };

for (const proj of PROJECTS) {
  const projAbs = path.join(repoRoot, proj.dir);
  const files = walkCs(projAbs);
  const groups = new Map(); // relDir -> [{file, parsed}]
  for (const abs of files) {
    const parsed = parseCsFile(abs, repoRoot);
    if (!parsed.types.length) continue; // e.g. GlobalUsings.cs
    const relWithin = path.relative(projAbs, abs).split(path.sep).join('/');
    const relDir = path.dirname(relWithin) === '.' ? '' : path.dirname(relWithin);
    if (!groups.has(relDir)) groups.set(relDir, []);
    groups.get(relDir).push({ relWithin, parsed });
  }

  // build sidebar tree for this project
  const tree = {}; // nested dir -> node
  function nodeFor(dirParts) {
    let cur = tree;
    let node = null;
    for (const part of dirParts) {
      cur[part] = cur[part] || { __items: [], __children: {} };
      node = cur[part];
      cur = node.__children;
    }
    return node;
  }

  const projPages = [];
  for (const [relDir, entries] of [...groups.entries()].sort((a, b) => a[0].localeCompare(b[0]))) {
    for (const { relWithin, parsed } of entries.sort((a, b) => a.relWithin.localeCompare(b.relWithin))) {
      const slug = slugFor(relWithin);
      const page = `api/${proj.key}/${slug}`;
      const pageZh = `zh/api/${proj.key}/${slug}`;
      const primary = parsed.types[0];
      const fileHash = parsed.hash;
      for (const type of parsed.types) {
        allTypes.push({
          name: type.name,
          kind: type.kind,
          namespace: type.namespace,
          source: type.relSource,
          page,
          pageZh,
          symbol: `${type.namespace}.${type.name}`,
          members: type.memberNames,
          sourceHash: fileHash,
          project: proj.key,
        });
        // render both locales
        for (const locale of ['en', 'zh']) {
          const outRel = locale === 'en' ? page : pageZh;
          const outFile = path.join(docsRoot, outRel + '.md');
          ensureDir(path.dirname(outFile));
          const body = renderType(type, locale);
          fs.writeFileSync(outFile, pageFrontmatter(type, outRel, locale) + body, 'utf8');
        }
      }
      pages.push({
        page,
        pageZh,
        source: primary.relSource,
        sourceHash: fileHash,
        project: proj.key,
        types: parsed.types.map((t) => t.name),
      });
      projPages.push({ slug, page, pageZh, types: parsed.types, relDir });
      // sidebar leaf
      const dirParts = relDir ? relDir.split('/') : [];
      const node = dirParts.length ? nodeFor(dirParts) : null;
      const leafText = parsed.types.length === 1 ? parsed.types[0].name : path.basename(relWithin, '.cs');
      const leaf = { text: leafText, slug, types: parsed.types.map((t) => t.name) };
      if (node) node.__items.push(leaf);
      else {
        tree.__root = tree.__root || { __items: [], __children: {} };
        tree.__root.__items.push(leaf);
      }
    }
  }

  // serialize sidebar tree -> VitePress nested items
  function serialize(node, projKey, locale) {
    const items = [];
    const prefix = locale === 'en' ? `/api/${projKey}/` : `/zh/api/${projKey}/`;
    for (const leaf of (node.__items || []).sort((a, b) => a.text.localeCompare(b.text))) {
      items.push({ text: leaf.text, link: prefix + leaf.slug });
    }
    const children = node.__children || {};
    for (const key of Object.keys(children).sort()) {
      items.push({ text: key, collapsed: true, items: serialize(children[key], projKey, locale) });
    }
    return items;
  }
  const rootItems = [];
  if (tree.__root) rootItems.push(...serialize(tree.__root, proj.key, 'en'));
  for (const key of Object.keys(tree).filter((k) => k !== '__root').sort()) {
    rootItems.push({ text: key, collapsed: true, items: serialize(tree[key], proj.key, 'en') });
  }
  const rootItemsZh = [];
  if (tree.__root) rootItemsZh.push(...serialize(tree.__root, proj.key, 'zh'));
  for (const key of Object.keys(tree).filter((k) => k !== '__root').sort()) {
    rootItemsZh.push({ text: key, collapsed: true, items: serialize(tree[key], proj.key, 'zh') });
  }
  sidebar.en.push({ text: proj.titleEn, collapsed: false, items: rootItems });
  sidebar.zh.push({ text: proj.titleZh, collapsed: false, items: rootItemsZh });

  // generated section index (type catalog)
  for (const locale of ['en', 'zh']) {
    const t = L[locale];
    const idxRel = locale === 'en' ? `api/${proj.key}/index` : `zh/api/${proj.key}/index`;
    const rows = projPages
      .flatMap((p) => p.types.map((ty) => ({ ty, p })))
      .sort((a, b) => a.ty.name.localeCompare(b.ty.name))
      .map(({ ty, p }) => {
        const link = (locale === 'en' ? `/api/${proj.key}/` : `/zh/api/${proj.key}/`) + p.slug;
        const sum = escProse(ty.doc.summaryMd || '').replace(/\|/g, '\\|').replace(/\n+/g, ' ');
        return `| [\`${ty.name}\`](${link}) | ${ty.kind} | ${sum} |`;
      });
    const head =
      locale === 'en'
        ? `# ${proj.titleEn}\n\n${proj.descEn}\n\n## ${t.catalog}\n\n`
        : `# ${proj.titleZh}\n\n${proj.descZh}\n\n## ${t.catalog}\n\n`;
    const table = [`| ${t.type} | ${t.kind} | ${t.summary} |`, '| --- | --- | --- |', ...rows].join('\n');
    const fm = frontmatter({ title: locale === 'en' ? proj.titleEn : proj.titleZh, generated: true, generator: 'scripts/gen-api.mjs' });
    const outFile = path.join(docsRoot, idxRel + '.md');
    ensureDir(path.dirname(outFile));
    fs.writeFileSync(outFile, fm + head + table + '\n', 'utf8');
  }
}

// ---- manifest ----
const manifest = {
  generator: 'scripts/gen-api.mjs',
  note: 'Machine-readable index of the generated API reference. Each type maps to its source file and doc page. Regenerate with: pnpm docs:gen',
  repoUrl: REPO_URL,
  projects: PROJECTS.map((p) => ({ key: p.key, dir: p.dir })),
  typeCount: allTypes.length,
  pageCount: pages.length,
  types: allTypes.sort((a, b) => a.symbol.localeCompare(b.symbol)),
  pages: pages.sort((a, b) => a.page.localeCompare(b.page)),
};
fs.writeFileSync(path.join(docsRoot, 'api/manifest.json'), JSON.stringify(manifest, null, 2) + '\n', 'utf8');
fs.writeFileSync(path.join(docsRoot, '.vitepress/api-sidebar.json'), JSON.stringify(sidebar, null, 2) + '\n', 'utf8');

console.log(`Generated ${allTypes.length} types across ${pages.length} pages (en + zh).`);
console.log('Wrote api/manifest.json and .vitepress/api-sidebar.json');
