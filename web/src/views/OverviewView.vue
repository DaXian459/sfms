<template>
  <div class="page">
    <div class="kpi-grid">
      <div class="kpi">
        <div class="k">在监控点位</div>
        <div class="v">{{ metrics.points ?? 0 }}<span class="u">个</span></div>
        <div class="dim" style="font-size:12px">质量良好 {{ metrics.good ?? 0 }} 个</div>
      </div>
      <div class="kpi">
        <div class="k">数据完整率</div>
        <div class="v" :class="rateClass">{{ metrics.goodRate ?? 0 }}<span class="u">%</span></div>
        <div class="dim" style="font-size:12px">Good 质量占比</div>
      </div>
      <div class="kpi">
        <div class="k">端点在用</div>
        <div class="v">{{ metrics.epOnline ?? 0 }}<span class="u">/ {{ metrics.epTotal ?? 0 }}</span></div>
        <div class="dim" style="font-size:12px">在线率 {{ metrics.epRate ?? 0 }}%</div>
      </div>
      <div class="kpi">
        <div class="k">活动报警</div>
        <div class="v" :style="{ color: (alarmCount || 0) > 0 ? '#ef4444' : '#22c55e' }">
          {{ alarmCount }}<span class="u">条</span>
        </div>
        <div class="dim" style="font-size:12px">
          <router-link to="/alarms">查看报警中心 →</router-link>
        </div>
      </div>
      <div class="kpi">
        <div class="k">采集心跳抖动</div>
        <div class="v">{{ jitter }}<span class="u">ms</span></div>
        <div class="dim" style="font-size:12px">50ms 时间轮调度</div>
      </div>
    </div>

    <div class="grid">
      <div class="card scada-card">
        <div class="card-title">
          <span>厂区总览画面</span>
          <div class="toolbar" style="margin:0">
            <el-tag size="small" :type="rt.isOnline ? 'success' : 'danger'">
              {{ rt.isOnline ? '实时推送中' : '离线/重连中' }}
            </el-tag>
            <el-button v-if="overviewId" size="small" @click="$router.push(`/screen/${overviewId}`)">放大</el-button>
          </div>
        </div>
        <div class="scada-box">
          <ScadaCanvas v-if="doc" :doc="doc" :values="rt.values" mode="run" @navigate="onNavigate" />
          <el-empty v-else description="画面加载中…" />
        </div>
      </div>

      <div class="right-col">
        <div class="card">
          <div class="card-title">
            <span>活动报警</span>
            <router-link to="/alarms" style="font-size:12px">全部</router-link>
          </div>
          <div v-if="!alarms.length" class="empty-line">暂无活动报警</div>
          <div v-for="a in alarms" :key="a.id" class="alarm-row" :class="a.severity.toLowerCase()">
            <div class="a-main">
              <div class="a-title">{{ a.tagName || a.deviceName || '设备' }} · {{ kindText(a.kind) }}</div>
              <div class="a-msg">{{ a.message }}</div>
            </div>
            <div class="a-time mono">{{ hhmm(a.startedAt) }}</div>
          </div>
        </div>

        <div class="card">
          <div class="card-title">
            <span>端点通信状态</span>
            <router-link to="/diagnostics" style="font-size:12px">诊断</router-link>
          </div>
          <div v-for="e in endpoints" :key="e.endpointId" class="ep-row">
            <span class="pill" :class="e.status === 'Online' ? 'tag-good' : 'tag-bad'">{{ e.status }}</span>
            <span class="ep-name">{{ e.name }}</span>
            <span class="mute mono">{{ e.ip }}:{{ e.port }}</span>
            <span class="spacer" />
            <span class="mono dim">{{ e.onlineTags }}/{{ e.tagCount }}</span>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import ScadaCanvas from '@/scada/ScadaCanvas.vue'
import { alarmApi, diagApi, screenApi } from '@/api'
import type { AlarmEventDto, DiagEndpointDto, MetricDto, ScreenDto } from '@/api/types'
import type { ScadaDoc } from '@/scada/types'
import { useRealtimeStore } from '@/stores/realtime'

const router = useRouter()
const rt = useRealtimeStore()

const metrics = ref<Record<string, number>>({})
const jitter = ref(0)
const alarmCount = ref(0)
const alarms = ref<AlarmEventDto[]>([])
const endpoints = ref<DiagEndpointDto[]>([])
const screens = ref<ScreenDto[]>([])
const overviewId = ref<number | null>(null)
const doc = ref<ScadaDoc | null>(null)

const rateClass = computed(() => ((metrics.value.goodRate ?? 0) >= 95 ? 'q-good' : (metrics.value.goodRate ?? 0) >= 80 ? 'q-uncertain' : 'q-bad'))

function kindText(k: string) {
  return ({ Hi: '高限', HiHi: '高高限', Lo: '低限', LoLo: '低低限', Comm: '通信中断', Overload: '采集过载' } as Record<string, string>)[k] ?? k
}
function hhmm(iso: string) {
  return new Date(iso).toLocaleTimeString('zh-CN', { hour12: false })
}

async function loadKpi() {
  try {
    const list: MetricDto[] = await diagApi.metrics()
    const m: Record<string, number> = {}
    for (const x of list) m[x.name] = x.value
    metrics.value = {
      points: m['points.total'] ?? 0,
      good: m['points.good'] ?? 0,
      goodRate: m['points.goodRate'] ?? 0,
      epTotal: m['endpoint.total'] ?? 0,
      epOnline: m['endpoint.online'] ?? 0,
      epRate: m['endpoint.onlineRate'] ?? 0
    }
    jitter.value = Math.round((m['collector.jitterMs'] ?? 0) * 10) / 10
  } catch { /* 忽略 */ }
}

async function loadAlarms() {
  try {
    const r = await alarmApi.list({ state: 'active', size: 6 })
    alarms.value = r.items
    alarmCount.value = r.activeCount
  } catch { /* 忽略 */ }
}

async function loadEndpoints() {
  try { endpoints.value = await diagApi.endpoints() } catch { /* 忽略 */ }
}

async function loadScreens() {
  screens.value = await screenApi.list()
  const ov = screens.value.find(s => s.code === 'overview') ?? screens.value[0]
  if (!ov) return
  overviewId.value = ov.id
  const data = await screenApi.get(ov.id, 'published')
  doc.value = typeof data.json === 'string' ? JSON.parse(data.json) : data.json
  const ids = new Set<number>()
  for (const w of doc.value?.widgets ?? []) {
    for (const b of w.bindings ?? []) if (b.tagId) ids.add(b.tagId)
    for (const a of w.animations ?? []) if (a.tagId) ids.add(a.tagId)
  }
  if (ids.size) rt.connect(Array.from(ids), 1000)
}

function onNavigate(code: string) {
  const t = screens.value.find(s => s.code === code)
  if (t) router.push(`/screen/${t.id}`)
}

let timers: number[] = []
onMounted(async () => {
  await Promise.all([loadKpi(), loadAlarms(), loadEndpoints(), loadScreens()])
  timers.push(window.setInterval(loadKpi, 10000))
  timers.push(window.setInterval(loadAlarms, 15000))
  timers.push(window.setInterval(loadEndpoints, 10000))
})
onUnmounted(() => timers.forEach(t => clearInterval(t)))
</script>

<style scoped>
.grid { display: grid; grid-template-columns: minmax(0, 1.55fr) minmax(320px, .95fr); gap: 14px; margin-top: 14px; }
.scada-card { display: flex; flex-direction: column; min-height: 520px; }
.scada-box { flex: 1; min-height: 440px; background: #060c15; border-radius: 8px; overflow: hidden; }
.right-col { display: flex; flex-direction: column; gap: 14px; }
.alarm-row {
  display: flex; align-items: center; gap: 10px; padding: 8px 10px; border-radius: 8px;
  background: #14273a; margin-bottom: 6px; border-left: 3px solid var(--warn);
}
.alarm-row.critical { border-left-color: var(--bad); }
.alarm-row.warn { border-left-color: var(--warn); }
.alarm-row.info { border-left-color: var(--accent); }
.a-main { flex: 1; min-width: 0; }
.a-title { font-size: 13px; font-weight: 600; }
.a-msg { font-size: 12px; color: var(--text-mute); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.a-time { font-size: 12px; color: var(--text-mute); }
.ep-row { display: flex; align-items: center; gap: 8px; padding: 6px 0; border-bottom: 1px dashed #1c3044; font-size: 13px; }
.ep-row:last-child { border-bottom: none; }
.ep-name { max-width: 150px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.empty-line { color: var(--text-mute); font-size: 13px; padding: 10px 0; text-align: center; }
</style>
