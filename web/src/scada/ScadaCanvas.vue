<template>
  <svg
    class="scada-svg"
    :viewBox="`0 0 ${doc.width} ${doc.height}`"
    preserveAspectRatio="xMidYMid meet"
    @mousedown.self="emitCanvasClick"
  >
    <rect :width="doc.width" :height="doc.height" :fill="doc.background || '#0b1220'" @mousedown="emitCanvasClick" />

    <g v-for="layer in visibleLayers" :key="layer.id">
      <template v-for="w in widgetsOf(layer.id)" :key="w.id">
        <g
          :transform="`translate(${w.x} ${w.y}) rotate(${w.rotation || 0} ${w.w / 2} ${w.h / 2})`"
          :opacity="blinkOpacity(w)"
          :class="['widget', { clickable: hasNavigate(w), selected: selectedId === w.id }]"
          @click.stop="onWidgetClick(w)"
        >
          <!-- 矩形 -->
          <rect
            v-if="w.type === 'rect'"
            :width="w.w" :height="w.h" :rx="num(w.props.radius, 0)"
            :fill="numOr(w.props.fill, '#101d2e')"
            :stroke="numOr(w.props.stroke, '#23384f')"
            :stroke-width="num(w.props.strokeWidth, 1.5)"
          />

          <!-- 圆形 -->
          <ellipse
            v-else-if="w.type === 'circle'"
            :cx="w.w / 2" :cy="w.h / 2" :rx="w.w / 2" :ry="w.h / 2"
            :fill="numOr(w.props.fill, '#132538')"
            :stroke="numOr(w.props.stroke, '#2b4a63')"
            :stroke-width="num(w.props.strokeWidth, 1.5)"
          />

          <!-- 直线 -->
          <line
            v-else-if="w.type === 'line'"
            x1="0" :y1="w.h / 2" :x2="w.w" :y2="w.h / 2"
            :stroke="numOr(w.props.stroke, '#2b4a63')"
            :stroke-width="num(w.props.strokeWidth, 3)"
            stroke-linecap="round"
          />

          <!-- 折线 -->
          <polyline
            v-else-if="w.type === 'polyline'"
            :points="w.props.points || `0,${w.h} ${w.w / 2},0 ${w.w},${w.h}`"
            fill="none"
            :stroke="numOr(w.props.stroke, '#2b4a63')"
            :stroke-width="num(w.props.strokeWidth, 3)"
            stroke-linejoin="round"
          />

          <!-- 文本 -->
          <text
            v-else-if="w.type === 'text'"
            :x="w.props.align === 'center' ? w.w / 2 : w.props.align === 'right' ? w.w : 0"
            :y="num(w.props.fontSize, 18)"
            :font-size="num(w.props.fontSize, 18)"
            :fill="qualityColor(w) || numOr(w.props.color, '#e2e8f0')"
            :font-weight="w.props.bold ? 700 : 400"
            :text-anchor="w.props.align === 'center' ? 'middle' : w.props.align === 'right' ? 'end' : 'start'"
          >{{ textOf(w) }}</text>

          <!-- 图片 -->
          <image
            v-else-if="w.type === 'image' && w.props.href"
            :href="w.props.href" :width="w.w" :height="w.h"
            :preserveAspectRatio="w.props.fit === 'fill' ? 'none' : 'xMidYMid meet'"
          />

          <!-- 指示灯 -->
          <template v-else-if="w.type === 'lamp'">
            <circle
              v-if="w.props.shape !== 'square'"
              :cx="w.w / 2" :cy="w.h / 2" :r="Math.min(w.w, w.h) / 2 - 2"
              :fill="fillOf(w)" :stroke="darken(fillOf(w))" stroke-width="2"
            />
            <rect
              v-else x="2" y="2" :width="w.w - 4" :height="w.h - 4" rx="6"
              :fill="fillOf(w)" :stroke="darken(fillOf(w))" stroke-width="2"
            />
            <circle :cx="w.w / 2" :cy="w.h / 2" :r="Math.min(w.w, w.h) / 6" fill="rgba(255,255,255,.55)" />
          </template>

          <!-- 储罐（液位动画） -->
          <template v-else-if="w.type === 'tank'">
            <rect :width="w.w" :height="w.h" :rx="10" :fill="numOr(w.props.fill, '#132538')" stroke="#2b4a63" stroke-width="2" />
            <rect
              x="3" :y="w.h - 3 - (w.h - 6) * levelOf(w)" :width="w.w - 6" :height="Math.max(0, (w.h - 6) * levelOf(w))"
              :rx="8" :fill="numOr(w.props.levelColor, '#1d4ed8')" opacity="0.85"
              style="transition: y .6s ease, height .6s ease"
            />
            <rect :width="w.w" :height="w.h" :rx="10" fill="none" stroke="#2b4a63" stroke-width="2" />
            <text :x="w.w / 2" :y="w.h / 2" text-anchor="middle" font-size="14" fill="#cbd5e1">
              {{ valueText(w, 1) }}
            </text>
          </template>

          <!-- 管道 -->
          <line
            v-else-if="w.type === 'pipe'"
            x1="0" :y1="w.h / 2" :x2="w.w" :y2="w.h / 2"
            :stroke="numOr(w.props.stroke, '#2b4a63')"
            :stroke-width="num(w.props.strokeWidth, w.h)"
            stroke-linecap="butt"
          />

          <!-- 泵 -->
          <g v-else-if="w.type === 'pump'" :class="{ spinning: isRunning(w) }" class="rotor">
            <circle :cx="w.w / 2" :cy="w.h / 2" :r="Math.min(w.w, w.h) / 2 - 3"
                    :fill="numOr(w.props.fill, '#1f6feb')" opacity=".9" />
            <path :d="impellerPath(w)" fill="none" stroke="#dbeafe" stroke-width="3" stroke-linecap="round" />
            <circle :cx="w.w / 2" :cy="w.h / 2" r="6" fill="#dbeafe" />
          </g>

          <!-- 风机 -->
          <g v-else-if="w.type === 'fan'" :class="{ spinning: isRunning(w) }" class="rotor">
            <circle :cx="w.w / 2" :cy="w.h / 2" :r="Math.min(w.w, w.h) / 2 - 3"
                    fill="none" :stroke="numOr(w.props.fill, '#1f6feb')" stroke-width="3" />
            <path :d="fanBladePath(w)" :fill="numOr(w.props.fill, '#1f6feb')" opacity=".85" />
          </g>

          <!-- 阀门 -->
          <g v-else-if="w.type === 'valve'">
            <polygon :points="`0,${w.h} ${w.w / 2},0 ${w.w},${w.h}`" :fill="fillOf(w)" stroke="#0f1c2b" stroke-width="2" />
            <polygon :points="`0,0 ${w.w / 2},${w.h} ${w.w},0`" :fill="fillOf(w)" opacity=".55" stroke="#0f1c2b" stroke-width="2" />
          </g>

          <!-- 电机 -->
          <g v-else-if="w.type === 'motor'">
            <rect x="2" y="2" :width="w.w - 4" :height="w.h - 4" rx="10" :fill="fillOf(w)" opacity=".9" />
            <text :x="w.w / 2" :y="w.h / 2 + 6" text-anchor="middle" font-size="18" font-weight="700" fill="#e0f2fe">M</text>
          </g>

          <!-- 仪表 -->
          <g v-else-if="w.type === 'gauge'">
            <path :d="arcPath(w, 210, 330)" fill="none" stroke="#1e3348" :stroke-width="16" stroke-linecap="round" />
            <path :d="arcPath(w, 210, 210 + 120 * ratio(w))" fill="none"
                  :stroke="numOr(w.props.color, '#38bdf8')" :stroke-width="16" stroke-linecap="round" />
            <line
              :x1="w.w / 2" :y1="w.h / 2 + 10" :x2="needleEnd(w).x" :y2="needleEnd(w).y"
              :stroke="numOr(w.props.color, '#38bdf8')" stroke-width="3" stroke-linecap="round"
              style="transition: all .5s ease"
            />
            <circle :cx="w.w / 2" :cy="w.h / 2 + 10" r="6" :fill="numOr(w.props.color, '#38bdf8')" />
            <text :x="w.w / 2" :y="w.h / 2 + 46" text-anchor="middle" font-size="22" font-weight="700" fill="#e2e8f0">
              {{ valueText(w, 1) }}
            </text>
            <text :x="w.w / 2" :y="w.h / 2 + 68" text-anchor="middle" font-size="12" fill="#64748b">
              {{ w.props.unit || '' }}
            </text>
          </g>

          <!-- 未知类型占位（设计态可见，运行态隐藏） -->
          <g v-else-if="mode === 'design'">
            <rect :width="w.w" :height="w.h" fill="none" stroke="#475569" stroke-dasharray="4 4" />
            <text :x="4" :y="16" font-size="12" fill="#64748b">{{ w.type }}</text>
          </g>

          <!-- 设计态选中框与 ID -->
          <rect
            v-if="mode === 'design' && selectedId === w.id"
            x="-2" y="-2" :width="w.w + 4" :height="w.h + 4"
            fill="none" stroke="#38bdf8" stroke-width="1.5" stroke-dasharray="6 3"
          />
          <text v-if="mode === 'design'" :x="0" :y="-6" font-size="11" fill="#64748b">{{ w.id }}</text>
        </g>
      </template>
    </g>
  </svg>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import type { RtValue, ScadaAnimation, ScadaDoc, ScadaWidget } from './types'

const props = defineProps<{
  doc: ScadaDoc
  values: Record<number, RtValue>
  mode?: 'run' | 'design'
  selectedId?: string | null
}>()

const emit = defineEmits<{
  (e: 'select', id: string | null): void
  (e: 'pick', id: string | null): void
  (e: 'navigate', code: string): void
}>()

const mode = computed(() => props.mode ?? 'run')

// 低频时钟：仅驱动闪烁与旋转动画，避免整页按帧重算
const clock = ref(Date.now())
let timer = 0
onMounted(() => { timer = window.setInterval(() => { clock.value = Date.now() }, 120) })
onUnmounted(() => { if (timer) clearInterval(timer) })

const visibleLayers = computed(() => {
  const layers = [...(props.doc.layers ?? [])].sort((a, b) => (a.z ?? 0) - (b.z ?? 0))
  const shown = layers.filter(l => l.visible !== false)
  return shown.length ? shown : [{ id: 'L1', name: '默认', visible: true, z: 1 }]
})

function widgetsOf(layerId: string): ScadaWidget[] {
  const list = props.doc.widgets ?? []
  const hasLayer = list.some(w => w.layerId === layerId)
  if (!hasLayer && (props.doc.layers ?? []).length <= 1) return list
  return list.filter(w => (w.layerId ?? 'L1') === layerId)
}

// ---------------- 数值与动画

function num(v: any, d = 0): number {
  const n = Number(v)
  return Number.isFinite(n) ? n : d
}
function numOr(v: any, d: string): string {
  return typeof v === 'string' && v ? v : d
}
function tagIdOf(w: ScadaWidget): number | undefined {
  return w.bindings?.[0]?.tagId ?? w.animations?.[0]?.tagId
}
function valueOf(w: ScadaWidget): number | undefined {
  const id = tagIdOf(w)
  if (id === undefined) return undefined
  const v = props.values[id]
  if (!v || v.quality !== 0) return undefined
  return v.value
}
function qualityOf(w: ScadaWidget): number {
  const id = tagIdOf(w)
  if (id === undefined) return 0
  return props.values[id]?.quality ?? 2
}

function fmt(n: number): string {
  if (!Number.isFinite(n)) return '---'
  const a = Math.abs(n)
  if (a >= 10000) return n.toFixed(0)
  if (a >= 1000) return n.toFixed(1)
  if (a >= 100) return n.toFixed(1)
  return n.toFixed(2).replace(/\.?0+$/, '')
}

function valueText(w: ScadaWidget, decimals = 0): string {
  const id = tagIdOf(w)
  if (id === undefined) return ''
  const rt = props.values[id]
  if (!rt) return '---'
  if (rt.quality !== 0) return '---'
  if (rt.text) return rt.text
  if (rt.value === undefined || rt.value === null) return '---'
  return decimals ? rt.value.toFixed(decimals).replace(/\.?0+$/, '') : fmt(rt.value)
}

function textOf(w: ScadaWidget): string {
  const base = typeof w.props.text === 'string' ? w.props.text : ''
  const binding = w.bindings?.find(b => b.prop === 'text')
  if (!binding) return base

  const id = binding.tagId
  const rt = props.values[id]
  let shown = '---'
  if (rt && rt.quality === 0) {
    shown = rt.text ?? (rt.value !== undefined && rt.value !== null ? fmt(rt.value) : '---')
  } else if (rt && rt.quality !== 0) {
    shown = '---'
  }

  const label = binding.label || base
  const fmtStr = binding.format || '{v}'
  const withValue = fmtStr.includes('{v}') ? fmtStr.replace('{v}', shown) : `${fmtStr}${shown}`
  const unit = binding.unit ? ' ' + binding.unit : ''
  return label ? `${label} ${withValue}${unit}` : `${withValue}${unit}`
}

/** 质量异常时文字置灰/标红，提示数据不可信。 */
function qualityColor(w: ScadaWidget): string | null {
  const hasQualityAnim = (w.animations ?? []).some(a => a.kind === 'quality')
  if (!hasQualityAnim) return null
  const q = qualityOf(w)
  if (q === 0) return null
  return q === 1 ? '#fbbf24' : '#ef4444'
}

function anims(w: ScadaWidget, kind: string): ScadaAnimation[] {
  return (w.animations ?? []).filter(a => a.kind === kind)
}

function fillOf(w: ScadaWidget): string {
  const base = numOr(w.props.fill, numOr(w.props.offColor, '#475569'))
  for (const a of anims(w, 'stateMap')) {
    if (a.prop && a.prop !== 'fill') continue
    const v = props.values[a.tagId]
    if (!v || v.quality !== 0) return a.default || base
    const hit = (a.map ?? []).find(m => compare(m.op, v.value, m.value))
    if (hit) return hit.result
    return a.default || base
  }
  return base
}

function compare(op: string, actual: number | undefined, target: number | string): boolean {
  if (actual === undefined) return false
  const t = Number(target)
  switch (op) {
    case 'eq': return actual === t
    case 'ne': return actual !== t
    case 'gt': return actual > t
    case 'gte': return actual >= t
    case 'lt': return actual < t
    case 'lte': return actual <= t
    default: return false
  }
}

function levelOf(w: ScadaWidget): number {
  for (const a of anims(w, 'linear')) {
    if (a.prop !== 'level') continue
    const v = props.values[a.tagId]
    if (!v || v.quality !== 0 || v.value === undefined) return 0
    const min = num(a.min, 0)
    const max = num(a.max, 100)
    const r = max === min ? 0 : (v.value - min) / (max - min)
    return Math.min(1, Math.max(0, r))
  }
  return 0
}

function ratio(w: ScadaWidget): number {
  const a = anims(w, 'linear').find(x => x.prop === 'needle')
  const v = a ? props.values[a.tagId]?.value : undefined
  const min = num(a?.min ?? w.props.min, 0)
  const max = num(a?.max ?? w.props.max, 100)
  if (v === undefined) return 0
  const r = max === min ? 0 : (v - min) / (max - min)
  return Math.min(1, Math.max(0, r))
}

function needleEnd(w: ScadaWidget): { x: number; y: number } {
  const angle = (-120 + 240 * ratio(w)) * Math.PI / 180
  const r = Math.min(w.w, w.h) / 2 - 26
  const cx = w.w / 2
  const cy = w.h / 2 + 10
  // 以 12 点方向为 0°，向右为正
  return { x: cx + r * Math.sin(angle), y: cy - r * Math.cos(angle) }
}

function arcPath(w: ScadaWidget, startDeg: number, endDeg: number): string {
  const r = Math.min(w.w, w.h) / 2 - 26
  const cx = w.w / 2
  const cy = w.h / 2 + 10
  const s = (startDeg - 90) * Math.PI / 180
  const e = (endDeg - 90) * Math.PI / 180
  const x1 = cx + r * Math.cos(s)
  const y1 = cy + r * Math.sin(s)
  const x2 = cx + r * Math.cos(e)
  const y2 = cy + r * Math.sin(e)
  const large = Math.abs(endDeg - startDeg) > 180 ? 1 : 0
  return `M ${x1} ${y1} A ${r} ${r} 0 ${large} 1 ${x2} ${y2}`
}

function isRunning(w: ScadaWidget): boolean {
  const v = valueOf(w)
  return v !== undefined && v !== 0
}

function blinkOpacity(w: ScadaWidget): number {
  const a = anims(w, 'blink')[0]
  if (!a) return 1
  const v = props.values[a.tagId]
  if (!v || v.quality !== 0) return 1
  if (a.when !== undefined && v.value !== a.when) return 1
  const period = Math.max(200, num(a.periodMs, 1000))
  return Math.floor((clock.value % period) / (period / 2)) === 0 ? 1 : 0.28
}

function darken(color: string): string {
  return shade(color, -40)
}
function shade(hex: string, delta: number): string {
  const m = /^#([0-9a-f]{6})$/i.exec(hex)
  if (!m) return hex
  const n = parseInt(m[1], 16)
  const clamp = (x: number) => Math.min(255, Math.max(0, x + delta))
  const r = clamp((n >> 16) & 255)
  const g = clamp((n >> 8) & 255)
  const b = clamp(n & 255)
  return `#${((r << 16) | (g << 8) | b).toString(16).padStart(6, '0')}`
}

function impellerPath(w: ScadaWidget): string {
  const cx = w.w / 2
  const cy = w.h / 2
  const r = Math.min(w.w, w.h) / 2 - 12
  const pts: string[] = []
  for (let i = 0; i < 3; i++) {
    const a = (i * 120) * Math.PI / 180
    const x = cx + r * Math.cos(a)
    const y = cy + r * Math.sin(a)
    pts.push(`${i === 0 ? 'M' : 'L'} ${x} ${y}`)
  }
  return pts.join(' ') + ' Z'
}

function fanBladePath(w: ScadaWidget): string {
  const cx = w.w / 2
  const cy = w.h / 2
  const r = Math.min(w.w, w.h) / 2 - 10
  let d = ''
  for (let i = 0; i < 4; i++) {
    const a = (i * 90) * Math.PI / 180
    const x = cx + r * 0.9 * Math.cos(a)
    const y = cy + r * 0.9 * Math.sin(a)
    d += `M ${cx} ${cy} L ${x} ${y} `
  }
  return d
}

// ---------------- 交互

function hasNavigate(w: ScadaWidget): boolean {
  return (w.events ?? []).some(e => e.action === 'navigate' && !!e.target)
}

function onWidgetClick(w: ScadaWidget) {
  if (mode.value === 'design') {
    emit('select', w.id)
    return
  }
  const ev = (w.events ?? []).find(e => e.action === 'navigate' && e.target)
  if (ev?.target) emit('navigate', ev.target)
}

function emitCanvasClick() {
  if (mode.value === 'design') emit('select', null)
  else emit('pick', null)
}
</script>

<style scoped>
.scada-svg { width: 100%; height: 100%; display: block; user-select: none; }
.widget.clickable { cursor: pointer; }
.widget.clickable:hover { filter: drop-shadow(0 0 6px rgba(56,189,248,.65)); }
.rotor { transform-box: fill-box; transform-origin: center; }
.rotor.spinning { animation: sfms-spin 1.1s linear infinite; }
@keyframes sfms-spin { to { transform: rotate(360deg); } }
</style>
