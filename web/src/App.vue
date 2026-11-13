<template>
  <router-view />
</template>

<script setup lang="ts">
import { onMounted, onUnmounted } from 'vue'
import { useAuthStore } from '@/stores/auth'
import { useRealtimeStore } from '@/stores/realtime'

const auth = useAuthStore()
const rt = useRealtimeStore()

// 断线兜底：每 5 秒检查一次，若 WebSocket 未连通则用 HTTP 快照补齐
let timer = 0
onMounted(() => {
  if (auth.isLoggedIn) auth.refreshMe()
  timer = window.setInterval(() => rt.fallbackSnapshot(), 5000)
})
onUnmounted(() => {
  if (timer) clearInterval(timer)
  rt.disconnect()
})
</script>
