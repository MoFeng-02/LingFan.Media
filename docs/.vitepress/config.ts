import { defineConfig } from 'vitepress'
// The API Reference sidebar is GENERATED from source by scripts/gen-api.mjs
// (docs/.vitepress/api-sidebar.json). Never hand-edit that file or duplicate its
// entries here — run `pnpm docs:gen` after changing the contract source. This is
// what keeps navigation from drifting away from the actual public surface.
import apiSidebar from './api-sidebar.json'

// https://vitepress.dev/reference/site-config
// VitePress 1.x has no built-in Mermaid support. We render diagrams with a
// custom <Mermaid> component (see .vitepress/theme/Mermaid.vue) wired through a
// markdown-it fence rule below. This avoids the fragile third-party
// vitepress-plugin-mermaid (its auto-injection is broken under VitePress 1.6.4
// and it force-lists Mermaid's transitive deps in optimizeDeps.include, which
// fails to resolve under pnpm's strict layout).

// Guide pages are hand-written, single-topic, and carry provenance frontmatter
// (sources / symbols) that scripts/check-docs.mjs validates against the source
// tree. The list below is the canonical guide outline; keep en and zh symmetric.
const guideEn = [
  { text: 'Introduction', link: '/guide/introduction' },
  { text: 'Getting Started', link: '/guide/getting-started' },
  { text: 'Architecture', link: '/guide/architecture' },
  { text: 'Design Philosophy', link: '/guide/design-philosophy' },
  { text: 'Async & Sync Discipline', link: '/guide/async-sync' },
  { text: 'Media Sources', link: '/guide/media-sources' },
  { text: 'Backends', link: '/guide/backends' },
  { text: 'Platform Support', link: '/guide/platforms' },
  { text: 'Linux Deployment', link: '/guide/linux' },
  { text: 'Licensing', link: '/guide/licensing' },
]
const guideZh = [
  { text: '简介', link: '/zh/guide/introduction' },
  { text: '快速开始', link: '/zh/guide/getting-started' },
  { text: '架构总览', link: '/zh/guide/architecture' },
  { text: '设计哲学', link: '/zh/guide/design-philosophy' },
  { text: '异步与同步纪律', link: '/zh/guide/async-sync' },
  { text: '媒体源', link: '/zh/guide/media-sources' },
  { text: '解码后端', link: '/zh/guide/backends' },
  { text: '平台支持', link: '/zh/guide/platforms' },
  { text: 'Linux 部署', link: '/zh/guide/linux' },
  { text: '许可与合规', link: '/zh/guide/licensing' },
]

export default defineConfig({
  // GitHub Pages project site is served at https://<user>.github.io/LingFan.Media/
  // so all asset/links must be prefixed with the repo name.
  base: '/LingFan.Media/',
  title: 'LingFan.Media',
  titleTemplate: ':title - .NET 10 AOT Media Infrastructure',
  description:
    'LingFan.Media — a .NET 10 AOT-first, cross-platform media infrastructure.',
  lastUpdated: false,
  cleanUrls: true,
  // Maintainer/AI documentation lives next to the scripts (scripts/README.md) and
  // is intentionally NOT part of the reader-facing site.
  srcExclude: ['scripts/**'],

  // NOTE: VitePress does NOT prefix `base` onto URLs inside `head`, so every
  // href here must carry the '/LingFan.Media/' prefix explicitly.
  head: [
    ['link', { rel: 'icon', type: 'image/x-icon', href: '/LingFan.Media/favicon.ico' }],
    ['link', { rel: 'icon', type: 'image/png', href: '/LingFan.Media/logo.png' }],
    ['link', { rel: 'apple-touch-icon', href: '/LingFan.Media/logo.png' }],
  ],

  markdown: {
    config: (md) => {
      const defaultFence = md.renderer.rules.fence!
      md.renderer.rules.fence = (tokens, idx, options, env, self) => {
        const token = tokens[idx]
        if (token.info.trim() === 'mermaid') {
          // Pass the diagram source as a base64 prop so no HTML-escaping of
          // newlines / quotes / CJK labels is needed. The <Mermaid> component
          // decodes it on the client.
          const b64 = Buffer.from(token.content).toString('base64')
          return `<Mermaid code="${b64}"></Mermaid>`
        }
        return defaultFence(tokens, idx, options, env, self)
      }
    },
  },

  vite: {
    optimizeDeps: {
      // Pre-bundle these CJS/ESM deps so the browser gets a proper ESM module
      // with a `default` export. Without this, dayjs (pulled in by VitePress's
      // footer) is served as raw CJS and the browser throws
      // "does not provide an export named 'default'".
      include: ['dayjs', 'mermaid'],
    },
  },

  locales: {
    root: {
      label: 'English',
      lang: 'en-US',
      themeConfig: {
        nav: [
          { text: 'Home', link: '/' },
          { text: 'Guide', link: '/guide/introduction' },
          { text: 'Platforms', link: '/guide/platforms' },
          { text: 'Licensing', link: '/guide/licensing' },
          { text: 'API', link: '/api/' },
        ],
        sidebar: [
          { text: 'Guide', items: guideEn },
          {
            text: 'API Reference',
            items: [{ text: 'Overview', link: '/api/' }, ...apiSidebar.en],
          },
        ],
      },
    },
    // Simplified-Chinese locale. Localized content lives under docs/zh/.
    // All nav/sidebar links carry the /zh/ prefix so the locale switch resolves correctly.
    zh: {
      label: '简体中文',
      lang: 'zh-CN',
      themeConfig: {
        nav: [
          { text: '首页', link: '/zh/' },
          { text: '指南', link: '/zh/guide/introduction' },
          { text: '平台支持', link: '/zh/guide/platforms' },
          { text: '许可与合规', link: '/zh/guide/licensing' },
          { text: 'API', link: '/zh/api/' },
        ],
        sidebar: [
          { text: '指南', items: guideZh },
          {
            text: 'API 参考',
            items: [{ text: '总览', link: '/zh/api/' }, ...apiSidebar.zh],
          },
        ],
      },
    },
  },

  themeConfig: {
    logo: '/logo.png',
    socialLinks: [
      { icon: 'github', link: 'https://github.com/MoFeng-02/LingFan.Media' },
    ],
    search: {
      provider: 'local',
    },
    docFooter: {
      prev: true,
      next: true,
    },
    outline: {
      label: 'On this page',
    },
    returnToTopLabel: 'Return to top',
    sidebarMenuLabel: 'Menu',
    darkModeSwitchLabel: 'Theme',
    lightModeSwitchTitle: 'Switch to light theme',
    darkModeSwitchTitle: 'Switch to dark theme',
  },
})
