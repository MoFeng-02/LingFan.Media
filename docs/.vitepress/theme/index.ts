import DefaultTheme from 'vitepress/theme'
import type { Theme } from 'vitepress'
import Mermaid from './Mermaid.vue'

// Register the <Mermaid> component globally so the ```mermaid fence rule in
// config.ts (which emits <Mermaid code="..." />) always resolves.
export default {
  extends: DefaultTheme,
  enhanceApp({ app }) {
    app.component('Mermaid', Mermaid)
  },
} satisfies Theme
