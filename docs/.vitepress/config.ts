import { defineConfig } from 'vitepress'

// https://vitepress.dev/reference/site-config
export default defineConfig({
  lang: 'zh-CN',
  title: 'LingFan.Media',
  titleTemplate: ':title - .NET 10 AOT 媒体基础设施',
  description: 'LingFan 引擎媒体子系统 · .NET 10 AOT 媒体基础设施文档',
  lastUpdated: true,
  cleanUrls: true,

  themeConfig: {
    nav: [
      { text: '首页', link: '/' },
      { text: '指南', link: '/guide/introduction' },
      { text: '架构', link: '/guide/architecture' },
    ],

    sidebar: [
      {
        text: '指南',
        items: [
          { text: '简介', link: '/guide/introduction' },
          { text: '架构总览', link: '/guide/architecture' },
        ],
      },
    ],

    socialLinks: [
      { icon: 'github', link: 'https://github.com/your-org/LingFanEngine.Media' },
    ],

    search: {
      provider: 'local',
    },

    docFooter: {
      prev: false,
      next: false,
    },

    outline: {
      label: '本页目录',
    },

    lastUpdated: {
      text: '最后更新于',
    },

    returnToTopLabel: '回到顶部',
    sidebarMenuLabel: '菜单',
    darkModeSwitchLabel: '主题',
    lightModeSwitchTitle: '切换到浅色模式',
    darkModeSwitchTitle: '切换到深色模式',
  },
})
