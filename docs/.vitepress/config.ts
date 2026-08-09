import { defineConfig } from 'vitepress'
import { withMermaid } from 'vitepress-plugin-mermaid'

// https://vitepress.dev/reference/site-config
// VitePress 1.x does not have built-in Mermaid support. The standard solution
// is vitepress-plugin-mermaid, which wraps the config and renders ```mermaid
// fences at build time / on the client.
export default withMermaid(defineConfig({
  // GitHub Pages project site is served at https://<user>.github.io/LingFan.Media/
  // so all asset/links must be prefixed with the repo name.
  base: '/LingFan.Media/',
  title: 'LingFan.Media',
  titleTemplate: ':title - .NET 10 AOT Media Infrastructure',
  description:
    'LingFan.Media — a .NET 10 AOT-first, cross-platform media infrastructure.',
  lastUpdated: true,
  cleanUrls: true,
  mermaid: {
    // Plugin-provided Mermaid config. Light-mode defaults here; the plugin
    // forces the dark theme automatically when the page body has a 'dark' class.
  },

  locales: {
    root: {
      label: 'English',
      lang: 'en-US',
      themeConfig: {
        nav: [
          { text: 'Home', link: '/' },
          { text: 'Guide', link: '/guide/introduction' },
          { text: 'Licensing', link: '/guide/licensing' },
          { text: 'API', link: '/api/abstractions/' },
        ],
        sidebar: [
          {
            text: 'Guide',
            items: [
              { text: 'Introduction', link: '/guide/introduction' },
              { text: 'Getting Started', link: '/guide/getting-started' },
              { text: 'Architecture', link: '/guide/architecture' },
              { text: 'Design Philosophy', link: '/guide/design-philosophy' },
              { text: 'Async & Sync Discipline', link: '/guide/async-sync' },
              { text: 'Media Sources', link: '/guide/media-sources' },
              { text: 'Backends & Roadmap', link: '/guide/backends' },
              { text: 'Licensing', link: '/guide/licensing' },
            ],
          },
          {
            text: 'API Reference',
            items: [
              { text: 'Contract Layer (Abstractions)', link: '/api/abstractions/' },
              { text: 'Infrastructure Layer', link: '/api/infrastructure/' },
            ],
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
          { text: '许可与合规', link: '/zh/guide/licensing' },
          { text: 'API', link: '/zh/api/abstractions/' },
        ],
        sidebar: [
          {
            text: '指南',
            items: [
              { text: '简介', link: '/zh/guide/introduction' },
              { text: '快速开始', link: '/zh/guide/getting-started' },
              { text: '架构总览', link: '/zh/guide/architecture' },
              { text: '设计哲学', link: '/zh/guide/design-philosophy' },
              { text: '异步与同步纪律', link: '/zh/guide/async-sync' },
              { text: '媒体源', link: '/zh/guide/media-sources' },
              { text: '后端与平台路线', link: '/zh/guide/backends' },
              { text: '许可与合规', link: '/zh/guide/licensing' },
            ],
          },
          {
            text: 'API 参考',
            items: [
              { text: '契约层（Abstractions）', link: '/zh/api/abstractions/' },
              { text: '基础设施层', link: '/zh/api/infrastructure/' },
            ],
          },
        ],
      },
    },
  },

  themeConfig: {
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
    lastUpdated: {
      text: 'Last updated',
    },
    returnToTopLabel: 'Return to top',
    sidebarMenuLabel: 'Menu',
    darkModeSwitchLabel: 'Theme',
    lightModeSwitchTitle: 'Switch to light theme',
    darkModeSwitchTitle: 'Switch to dark theme',
  },
}))
