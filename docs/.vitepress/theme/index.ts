import DefaultTheme from 'vitepress/theme'
import type { Theme } from 'vitepress'
import Mermaid from 'vitepress-plugin-mermaid/Mermaid.vue'

// vitepress-plugin-mermaid normally auto-registers this component by
// transforming VitePress's client app entry, but that injection does not
// reliably match the VitePress 1.6.4 module layout. Register it manually here
// so <Mermaid> tags emitted by the markdown-it fence rule always resolve.
export default {
  extends: DefaultTheme,
  enhanceApp({ app }) {
    app.component('Mermaid', Mermaid)
  },
} satisfies Theme
