import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import { fileURLToPath, URL } from 'node:url'

// 开发期把 /api 与 /ws 代理到 5080 的 .NET API，避免 CORS 与 ws 跨域
export default defineConfig({
  plugins: [vue()],
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) }
  },
  server: {
    port: 5173,
    proxy: {
      '/api': { target: 'http://127.0.0.1:5080', changeOrigin: true },
      '/ws': { target: 'ws://127.0.0.1:5080', ws: true, changeOrigin: true },
      '/health': { target: 'http://127.0.0.1:5080', changeOrigin: true }
    }
  },
  build: {
    outDir: '../src/SFMS.Api/wwwroot',
    emptyOutDir: true,
    chunkSizeWarningLimit: 1500,
    rollupOptions: {
      output: {
        manualChunks: {
          echarts: ['echarts'],
          element: ['element-plus']
        }
      }
    }
  }
})
