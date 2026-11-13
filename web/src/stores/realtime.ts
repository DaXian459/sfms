import { defineStore } from 'pinia'
import { authApi, realtimeApi } from '@/api'

export interface RtItem { tagId: number; value?: number; text?: string; quality: number; ts: string }

type Status = 'idle' | 'connecting' | 'open' | 'closed' | 'error'

/**
 * 实时值仓库：封装 WebSocket 订阅、增量合并、resync 兜底与断线重连。
 * 组件只需 watch(state) 或读取 values[tagId]，无需关心帧格式。
 */
export const useRealtimeStore = defineStore('realtime', {
  state: () => ({
    status: 'idle' as Status,
    values: {} as Record<number, RtItem>,
    lastFrameAt: 0,
    frames: 0,
    resyncs: 0,
    sessionId: '',
    _ws: null as WebSocket | null,
    _subId: 'main',
    _tags: [] as number[],
    _rateMs: 1000,
    _retry: 0,
    _pingTimer: 0 as any,
    _reconnectTimer: 0 as any
  }),

  getters: {
    isOnline: (s) => s.status === 'open',
    item: (s) => (tagId: number): RtItem | undefined => s.values[tagId],
    count: (s) => Object.keys(s.values).length
  },

  actions: {
    async connect(tags: number[], rateMs = 1000) {
      this._tags = Array.from(new Set(tags))
      this._rateMs = rateMs
      if (!this._tags.length) return
      if (this.status === 'open') { this.subscribe(); return }
      this.disconnect(false)
      this.status = 'connecting'

      let ticket = ''
      try {
        ticket = (await authApi.wsTicket()).ticket
      } catch {
        this.status = 'error'
        this.scheduleReconnect()
        return
      }

      const proto = location.protocol === 'https:' ? 'wss' : 'ws'
      const ws = new WebSocket(`${proto}://${location.host}/ws/realtime?ticket=${encodeURIComponent(ticket)}`)
      this._ws = ws

      ws.onopen = () => {
        this.status = 'open'
        this._retry = 0
        this.subscribe()
        this.startPing()
      }
      ws.onmessage = (ev) => this.onFrame(ev.data)
      ws.onclose = () => {
        this.status = 'closed'
        this.stopPing()
        this.scheduleReconnect()
      }
      ws.onerror = () => { this.status = 'error' }
    },

    /** 订阅变化时（切换画面）重发 subscribe，服务端会立即回快照。 */
    updateSubscription(tags: number[]) {
      this._tags = Array.from(new Set(tags))
      if (this.status === 'open') this.subscribe()
    },

    subscribe() {
      const ws = this._ws
      if (!ws || ws.readyState !== WebSocket.OPEN || !this._tags.length) return
      ws.send(JSON.stringify({
        type: 'subscribe', subId: this._subId, tags: this._tags, rateMs: this._rateMs
      }))
    },

    requestSnapshot() {
      const ws = this._ws
      if (ws && ws.readyState === WebSocket.OPEN)
        ws.send(JSON.stringify({ type: 'snapshot', subId: this._subId }))
    },

    onFrame(raw: string) {
      let msg: any
      try { msg = JSON.parse(raw) } catch { return }
      this.frames++
      this.lastFrameAt = Date.now()

      switch (msg.type) {
        case 'welcome':
          this.sessionId = msg.sessionId ?? ''
          break
        case 'snapshot':
        case 'delta':
          for (const it of msg.items ?? []) {
            this.values[it.t] = { tagId: it.t, value: it.v ?? undefined, text: it.txt ?? undefined, quality: it.q, ts: it.ts }
          }
          break
        case 'resync':
          this.resyncs++
          this.requestSnapshot()
          break
        case 'error':
          console.warn('[ws] 服务端提示：', msg.code, msg.msg)
          break
        default:
          break
      }
    },

    startPing() {
      this.stopPing()
      this._pingTimer = setInterval(() => {
        const ws = this._ws
        if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ type: 'ping' }))
      }, 20000)
    },

    stopPing() {
      if (this._pingTimer) { clearInterval(this._pingTimer); this._pingTimer = 0 }
    },

    scheduleReconnect() {
      if (this._reconnectTimer) return
      const delay = Math.min(30000, 1000 * Math.pow(2, this._retry++))
      this._reconnectTimer = setTimeout(() => {
        this._reconnectTimer = 0
        if (this._tags.length) this.connect(this._tags, this._rateMs)
      }, delay)
    },

    /** 断线期间用 HTTP 快照补齐，避免画面长时间空白。 */
    async fallbackSnapshot() {
      if (this.status === 'open' || !this._tags.length) return
      try {
        const snap = await realtimeApi.snapshot({ tagIds: this._tags.join(',') })
        for (const it of snap.items) this.values[it.tagId] = { ...it }
      } catch { /* 忽略，下一次重试 */ }
    },

    disconnect(reset = true) {
      this.stopPing()
      if (this._reconnectTimer) { clearTimeout(this._reconnectTimer); this._reconnectTimer = 0 }
      const ws = this._ws
      this._ws = null
      if (ws) {
        ws.onclose = null
        ws.onmessage = null
        try { ws.close() } catch { /* 已关闭 */ }
      }
      this.status = 'closed'
      if (reset) { this.values = {}; this._tags = [] }
    }
  }
})
