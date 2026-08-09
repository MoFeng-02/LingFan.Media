import { defineConfig } from 'vitepress'

// https://vitepress.dev/reference/site-config
export default defineConfig({
  // GitHub Pages project site is served at https://<user>.github.io/LingFan.Media/
  // so all asset/links must be prefixed with the repo name.
  base: '/LingFan.Media/',
  title: 'LingFan.Media',
  titleTemplate: ':title - .NET 10 AOT Media Infrastructure',
  description:
    'LingFan.Media — a .NET 10 AOT-first, cross-platform media infrastructure.',
  lastUpdated: true,
  cleanUrls: true,

  locales: {
    root: {
      label: 'English',
      lang: 'en-US',
      themeConfig: {
        nav: [
          { text: 'Home', link: '/' },
          { text: 'Guide', link: '/guide/introduction' },
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
      { icon: 'github', link: 'https://github.com/your-org/LingFanEngine.Media' },
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
})
