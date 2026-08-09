<script setup lang="ts">
import { onMounted, ref, nextTick } from 'vue'

const props = defineProps<{ code: string }>()
const container = ref<HTMLElement | null>(null)

function decode(b64: string): string {
  const bin = atob(b64)
  const bytes = Uint8Array.from(bin, (c) => c.charCodeAt(0))
  return new TextDecoder().decode(bytes)
}

// Mermaid frequently under-allocates the foreignObject height for nodes whose
// label text wraps. The foreignObject defaults to overflow:hidden, so the
// overflowing bottom rows get clipped. We temporarily reveal the content to
// measure its true rendered height, then grow ONLY the height of the rect +
// foreignObject (keeping their horizontal center/position fixed so edges stay
// aligned).
function fixNodeHeights(root: HTMLElement) {
  const nodes = root.querySelectorAll<SVGGElement>('.node')
  nodes.forEach((node) => {
    const fo = node.querySelector<SVGForeignObjectElement>('foreignObject')
    const rect = node.querySelector<SVGRectElement>('rect')
    const label =
      fo?.querySelector<HTMLElement>('.nodeLabel') ??
      fo?.querySelector<HTMLElement>('div')
    if (!fo || !rect || !label) return

    const prevOverflow = fo.style.overflow
    fo.style.overflow = 'visible'
    const realHeight = label.getBoundingClientRect().height
    fo.style.overflow = prevOverflow

    const padY = 16
    const curH = parseFloat(rect.getAttribute('height') || '0')
    const newH = Math.max(realHeight + padY * 2, curH)
    if (newH <= curH) return

    const delta = newH - curH
    const y = parseFloat(rect.getAttribute('y') || '0')
    const foY = parseFloat(fo.getAttribute('y') || '0')

    rect.setAttribute('height', String(newH))
    rect.setAttribute('y', String(y - delta / 2))
    fo.setAttribute('height', String(newH))
    fo.setAttribute('y', String(foY - delta / 2))
  })
}

onMounted(async () => {
  await nextTick()
  if (!container.value) return

  // Wait for web fonts before measuring text, otherwise mermaid uses a
  // fallback font and allocates a too-narrow/too-short box.
  await Promise.race([
    document.fonts?.ready ?? Promise.resolve(),
    new Promise<void>((resolve) => setTimeout(resolve, 1200)),
  ])

  // Import mermaid lazily so it never runs during SSR (it touches `document`).
  const mermaid = (await import('mermaid')).default
  const isDark = document.documentElement.classList.contains('dark')
  mermaid.initialize({
    startOnLoad: false,
    theme: isDark ? 'dark' : 'default',
    securityLevel: 'loose',
    // Concrete font stack (no CSS variables) so off-SVG text measurement in
    // mermaid does not fall back to a narrower font.
    fontFamily:
      'Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif',
    flowchart: {
      useMaxWidth: true,
      htmlLabels: true,
      padding: 24,
    },
  })
  const id = 'mmd-' + Math.random().toString(36).slice(2)
  try {
    const { svg } = await mermaid.render(id, decode(props.code))
    container.value.innerHTML = svg
    requestAnimationFrame(() => {
      if (container.value) fixNodeHeights(container.value)
    })
  } catch (e) {
    container.value.innerHTML =
      '<pre class="mermaid-error">Mermaid render failed:\n' + String(e) + '</pre>'
  }
})
</script>

<template>
  <div class="mermaid-render" ref="container"></div>
</template>

<style scoped>
.mermaid-render {
  margin: 1rem 0;
  text-align: center;
  overflow-x: auto;
}
.mermaid-render :deep(svg) {
  max-width: 100%;
  height: auto;
  overflow: visible;
}
/* Last-resort guard: never clip label content inside nodes. */
.mermaid-render :deep(foreignObject) {
  overflow: visible;
}
.mermaid-error {
  color: #c0392b;
  white-space: pre-wrap;
  text-align: left;
}
</style>
