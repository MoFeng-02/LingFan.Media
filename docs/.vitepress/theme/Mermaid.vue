<script setup lang="ts">
import { onMounted, ref, nextTick } from 'vue'

const props = defineProps<{ code: string }>()
const container = ref<HTMLElement | null>(null)

function decode(b64: string): string {
  const bin = atob(b64)
  const bytes = Uint8Array.from(bin, (c) => c.charCodeAt(0))
  return new TextDecoder().decode(bytes)
}

onMounted(async () => {
  await nextTick()
  if (!container.value) return
  // Import mermaid lazily so it never runs during SSR (it touches `document`).
  const mermaid = (await import('mermaid')).default
  const isDark = document.documentElement.classList.contains('dark')
  mermaid.initialize({
    startOnLoad: false,
    theme: isDark ? 'dark' : 'default',
    securityLevel: 'loose',
  })
  const id = 'mmd-' + Math.random().toString(36).slice(2)
  try {
    const { svg } = await mermaid.render(id, decode(props.code))
    container.value.innerHTML = svg
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
.mermaid-error {
  color: #c0392b;
  white-space: pre-wrap;
  text-align: left;
}
</style>
