<template>
  <div class="editor">
    <!-- 左：图元库 -->
    <aside class="palette">
      <div class="p-title">图元库</div>
      <div class="p-grid">
        <div v-for="t in paletteTypes" :key="t.type" class="p-item" :title="t.label" @click="addWidget(t.type)">
          <span class="p-icon" v-html="t.icon" />
          <span class="p-label">{{ t.label }}</span>
        </div>
      </div>
      <div class="p-title">图层</div>
      <div class="layers">
        <div v-for="l in doc?.layers ?? []" :key="l.id" class="layer-row" :class="{ on: currentLayer === l.id }"
             @click="currentLayer = l.id">
          <el-icon><Files /></el-icon>
          <span>{{ l.name }}</span>
          <span class="dim">{{ countOf(l.id) }}</span>
        </div>
      </div>
      <div class="p-title">画面</div>
      <div class="p-meta">
        <div>{{ doc?.width }} × {{ doc?.height }}</div>
        <div class="dim">{{ doc?.widgets?.length ?? 0 }} 个图元</div>
      </div>
    </aside>

    <!-- 中：画布 -->
    <section class="canvas-col">
      <div class="toolbar">
        <el-button size="small" @click="$router.push('/screens')"><el-icon><Back /></el-icon>返回</el-button>
        <el-input v-model="docName" size="small" style="width:200px" placeholder="画面名称" />
        <el-divider direction="vertical" />
        <el-button size="small" :disabled="!canUndo" @click="undo"><el-icon><RefreshLeft /></el-icon>撤销</el-button>
        <el-button size="small" :disabled="!canRedo" @click="redo"><el-icon><RefreshRight /></el-icon>重做</el-button>
        <el-divider direction="vertical" />
        <el-button size="small" :disabled="!selected" @click="duplicate"><el-icon><CopyDocument /></el-icon>复制</el-button>
        <el-button size="small" :disabled="!selected" @click="removeSelected"><el-icon><Delete /></el-icon>删除</el-button>
        <el-button size="small" :disabled="!selected" @click="bringFront">置顶</el-button>
        <el-button size="small" :disabled="!selected" @click="sendBack">置底</el-button>
        <div class="spacer" />
        <el-switch v-model="snap" size="small" active-text="8px 吸附" style="margin-right:10px" />
        <span class="dim" style="font-size:12px;margin-right:8px">{{ hint }}</span>
        <el-button size="small" :loading="saving" @click="saveDraft">保存草稿</el-button>
        <el-button size="small" type="primary" :loading="publishing" @click="publish">发布</el-button>
        <el-button size="small" @click="versionVisible = true">版本</el-button>
      </div>

      <div class="canvas-wrap" ref="wrap" @mousedown="onBackgroundDown" @mousemove="onMouseMove" @mouseup="onMouseUp" @mouseleave="onMouseUp">
        <div class="stage" :style="stageStyle">
          <ScadaCanvas v-if="doc" :doc="doc" :values="{}" mode="design" :selected-id="selectedId" @select="onSelect" />
          <!-- 选中框与 8 个缩放把手 -->
          <div v-if="selRect" class="sel-box" :style="selRect" @mousedown.stop="startMove">
            <span v-for="h in handles" :key="h.k" class="handle" :class="h.k" :style="h.style" @mousedown.stop="startResize(h.k, $event)" />
          </div>
        </div>
      </div>
    </section>

    <!-- 右：属性 -->
    <aside class="inspector">
      <el-tabs v-model="tab" stretch>
        <el-tab-pane label="属性" name="props">
          <div v-if="!selected" class="tip">在画布中选择图元，或从左侧图元库添加</div>
          <template v-else>
            <div class="sec-title">基础</div>
            <div class="grid2">
              <label>X</label><el-input-number v-model="selected.x" size="small" :step="8" controls-position="right" @change="commit('移动')" />
              <label>Y</label><el-input-number v-model="selected.y" size="small" :step="8" controls-position="right" @change="commit('移动')" />
              <label>宽</label><el-input-number v-model="selected.w" size="small" :min="4" :step="8" controls-position="right" @change="commit('调整尺寸')" />
              <label>高</label><el-input-number v-model="selected.h" size="small" :min="4" :step="8" controls-position="right" @change="commit('调整尺寸')" />
              <label>旋转</label><el-input-number v-model="selected.rotation" size="small" :step="15" controls-position="right" @change="commit('旋转')" />
              <label>图层</label>
              <el-select v-model="selected.layerId" size="small" @change="commit('切换图层')">
                <el-option v-for="l in doc?.layers ?? []" :key="l.id" :label="l.name" :value="l.id" />
              </el-select>
            </div>

            <div class="sec-title">外观</div>
            <div class="grid2">
              <template v-for="(val, key) in selected.props" :key="key">
                <label>{{ key }}</label>
                <el-color-picker v-if="typeof val === 'string' && String(val).startsWith('#')" v-model="selected.props[key]" size="small" @change="commit('改颜色')" />
                <el-input-number v-else-if="typeof val === 'number'" v-model="selected.props[key]" size="small" controls-position="right" @change="commit('改属性')" />
                <el-input v-else v-model="selected.props[key]" size="small" @change="commit('改属性')" />
              </template>
            </div>

            <div class="sec-title">
              数据绑定
              <el-button size="small" text @click="addBinding">+ 添加</el-button>
            </div>
            <div v-for="(b, i) in selected.bindings ?? []" :key="i" class="bind-row">
              <el-select v-model="b.prop" size="small" style="width:90px" @change="commit('改绑定')">
                <el-option label="text" value="text" />
                <el-option label="value" value="value" />
                <el-option label="level" value="level" />
                <el-option label="fill" value="fill" />
              </el-select>
              <el-select v-model="b.tagId" size="small" filterable placeholder="选择点位" style="flex:1" @change="commit('改绑定')">
                <el-option v-for="t in tags" :key="t.id" :label="`${t.code} · ${t.name}`" :value="t.id" />
              </el-select>
              <el-input v-model="b.format" size="small" style="width:70px" placeholder="{v}" @change="commit('改格式')" />
              <el-input v-model="b.unit" size="small" style="width:56px" placeholder="单位" @change="commit('改单位')" />
              <el-button size="small" text type="danger" @click="selected.bindings?.splice(i, 1); commit('删绑定')">×</el-button>
            </div>

            <div class="sec-title">
              动画
              <el-button size="small" text @click="addAnimation">+ 添加</el-button>
            </div>
            <div v-for="(a, i) in selected.animations ?? []" :key="'a' + i" class="anim-row">
              <el-select v-model="a.kind" size="small" style="width:100px" @change="commit('改动画')">
                <el-option label="stateMap" value="stateMap" />
                <el-option label="linear" value="linear" />
                <el-option label="blink" value="blink" />
                <el-option label="quality" value="quality" />
              </el-select>
              <el-select v-model="a.tagId" size="small" filterable placeholder="点位" style="flex:1" @change="commit('改动画')">
                <el-option v-for="t in tags" :key="t.id" :label="t.code" :value="t.id" />
              </el-select>
              <el-select v-model="a.prop" size="small" style="width:82px" clearable placeholder="属性" @change="commit('改动画')">
                <el-option label="fill" value="fill" />
                <el-option label="rotate" value="rotate" />
                <el-option label="level" value="level" />
                <el-option label="needle" value="needle" />
              </el-select>
              <el-button size="small" text type="danger" @click="selected.animations?.splice(i, 1); commit('删动画')">×</el-button>
            </div>

            <div class="sec-title">
              点击跳转
              <el-button size="small" text @click="addNavigate">+ 添加</el-button>
            </div>
            <div v-for="(e, i) in selected.events ?? []" :key="'e' + i" class="anim-row">
              <el-select v-model="e.target" size="small" filterable placeholder="目标画面" style="flex:1" @change="commit('改事件')">
                <el-option v-for="s in screens" :key="s.code" :label="`${s.name} (${s.code})`" :value="s.code" />
              </el-select>
              <el-button size="small" text type="danger" @click="selected.events?.splice(i, 1); commit('删事件')">×</el-button>
            </div>
          </template>
        </el-tab-pane>

        <el-tab-pane label="图元" name="list">
          <div class="tree">
            <div v-for="(w, i) in reversedWidgets" :key="w.id" class="tree-row"
                 :class="{ on: selectedId === w.id }" @click="onSelect(w.id)">
              <span class="t-type">{{ w.type }}</span>
              <span class="t-id">{{ w.id }}</span>
              <span class="spacer" />
              <span class="dim">{{ w.layerId }}</span>
            </div>
          </div>
        </el-tab-pane>
      </el-tabs>
    </aside>

    <el-dialog v-model="versionVisible" title="发布版本" width="640px">
      <el-table :data="versions" size="small" border>
        <el-table-column prop="versionNo" label="版本" width="80" />
        <el-table-column prop="note" label="说明" />
        <el-table-column prop="createdBy" label="发布人" width="110" />
        <el-table-column label="时间" width="170">
          <template #default="{ row }">{{ new Date(row.createdAt).toLocaleString('zh-CN') }}</template>
        </el-table-column>
        <el-table-column label="操作" width="100">
          <template #default="{ row }">
            <el-button size="small" text type="primary" @click="rollback(row)">回滚</el-button>
          </template>
        </el-table-column>
      </el-table>
      <template #footer>
        <el-button @click="versionVisible = false">关闭</el-button>
        <el-button @click="loadVersions">刷新</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, reactive, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import ScadaCanvas from '@/scada/ScadaCanvas.vue'
import { configApi, screenApi } from '@/api'
import type { TagDto, ScreenDto } from '@/api/types'
import {
  defaultProps, defaultSize, emptyDoc, type ScadaDoc, type ScadaWidget
} from '@/scada/types'

const route = useRoute()
const router = useRouter()

const doc = ref<ScadaDoc | null>(null)
const docName = ref('')
const selectedId = ref<string | null>(null)
const currentLayer = ref('L1')
const tab = ref('props')
const snap = ref(true)
const saving = ref(false)
const publishing = ref(false)
const versionVisible = ref(false)
const versions = ref<any[]>([])
const screens = ref<ScreenDto[]>([])
const tags = ref<TagDto[]>([])
const wrap = ref<HTMLElement | null>(null)
const scale = ref(0.5)
const offset = reactive({ x: 0, y: 0 })
const hint = ref('')

const history = ref<string[]>([])
const hIndex = ref(-1)

const paletteTypes = [
  { type: 'rect', label: '矩形', icon: '▭' },
  { type: 'circle', label: '圆形', icon: '◯' },
  { type: 'line', label: '直线', icon: '─' },
  { type: 'polyline', label: '折线', icon: '⌇' },
  { type: 'text', label: '文本', icon: 'T' },
  { type: 'lamp', label: '指示灯', icon: '◉' },
  { type: 'tank', label: '储罐', icon: '▯' },
  { type: 'pipe', label: '管道', icon: '═' },
  { type: 'pump', label: '泵', icon: '◍' },
  { type: 'fan', label: '风机', icon: '✳' },
  { type: 'valve', label: '阀门', icon: '◭' },
  { type: 'motor', label: '电机', icon: 'M' },
  { type: 'gauge', label: '仪表', icon: '◔' },
  { type: 'image', label: '图片', icon: '🖼' }
]

const selected = computed<ScadaWidget | null>(() =>
  doc.value?.widgets.find(w => w.id === selectedId.value) ?? null)

const reversedWidgets = computed(() => [...(doc.value?.widgets ?? [])].reverse())

const canUndo = computed(() => hIndex.value > 0)
const canRedo = computed(() => hIndex.value >= 0 && hIndex.value < history.value.length - 1)

const stageStyle = computed(() => {
  if (!doc.value) return {}
  return {
    width: doc.value.width * scale.value + 'px',
    height: doc.value.height * scale.value + 'px',
    left: offset.x + 'px',
    top: offset.y + 'px'
  }
})

const selRect = computed(() => {
  const w = selected.value
  if (!w) return null
  return {
    left: w.x * scale.value + 'px',
    top: w.y * scale.value + 'px',
    width: w.w * scale.value + 'px',
    height: w.h * scale.value + 'px'
  }
})

const handles = computed(() => {
  const s = 8
  const list = [
    { k: 'nw', style: { left: `-${s / 2}px`, top: `-${s / 2}px`, cursor: 'nwse-resize' } },
    { k: 'n', style: { left: `calc(50% - ${s / 2}px)`, top: `-${s / 2}px`, cursor: 'ns-resize' } },
    { k: 'ne', style: { right: `-${s / 2}px`, top: `-${s / 2}px`, cursor: 'nesw-resize' } },
    { k: 'e', style: { right: `-${s / 2}px`, top: `calc(50% - ${s / 2}px)`, cursor: 'ew-resize' } },
    { k: 'se', style: { right: `-${s / 2}px`, bottom: `-${s / 2}px`, cursor: 'nwse-resize' } },
    { k: 's', style: { left: `calc(50% - ${s / 2}px)`, bottom: `-${s / 2}px`, cursor: 'ns-resize' } },
    { k: 'sw', style: { left: `-${s / 2}px`, bottom: `-${s / 2}px`, cursor: 'nesw-resize' } },
    { k: 'w', style: { left: `-${s / 2}px`, top: `calc(50% - ${s / 2}px)`, cursor: 'ew-resize' } }
  ]
  return list
})

// ---------------- 载入

async function load() {
  const id = Number(route.params.id)
  const data = await screenApi.get(id, 'draft')
  const parsed: ScadaDoc = typeof data.json === 'string' ? JSON.parse(data.json) : data.json
  if (!parsed.layers?.length) parsed.layers = [{ id: 'L1', name: '默认层', visible: true, z: 1 }]
  parsed.widgets ??= []
  doc.value = parsed
  docName.value = parsed.name || data.name
  currentLayer.value = parsed.layers[0].id
  selectedId.value = null
  resetHistory()
  await nextTick()
  fit()
  loadVersions()
}

async function loadStatic() {
  try { screens.value = await screenApi.list() } catch { /* 忽略 */ }
  try {
    const r = await configApi.tags({ page: 1, size: 2000 })
    tags.value = r.items
  } catch { /* 忽略 */ }
}

function fit() {
  const el = wrap.value
  if (!el || !doc.value) return
  const cw = el.clientWidth - 24
  const ch = el.clientHeight - 24
  scale.value = Math.min(cw / doc.value.width, ch / doc.value.height)
  offset.x = (el.clientWidth - doc.value.width * scale.value) / 2
  offset.y = (el.clientHeight - doc.value.height * scale.value) / 2
}

// ---------------- 历史（撤销重做）

function snapshot(): string {
  return JSON.stringify(doc.value)
}
function resetHistory() {
  history.value = [snapshot()]
  hIndex.value = 0
}
function commit(label = '编辑') {
  if (!doc.value) return
  const s = snapshot()
  if (history.value[hIndex.value] === s) return
  history.value = history.value.slice(0, hIndex.value + 1)
  history.value.push(s)
  if (history.value.length > 100) history.value.shift()
  hIndex.value = history.value.length - 1
  hint.value = label
  setTimeout(() => { hint.value = '' }, 1200)
}
function applyHistory(i: number) {
  const s = history.value[i]
  if (!s) return
  doc.value = JSON.parse(s)
  selectedId.value = selectedId.value && doc.value!.widgets.some(w => w.id === selectedId.value)
    ? selectedId.value : null
}
function undo() { if (canUndo.value) { hIndex.value--; applyHistory(hIndex.value) } }
function redo() { if (canRedo.value) { hIndex.value++; applyHistory(hIndex.value) } }

// ---------------- 图元操作

let seq = 1
function newId(type: string): string {
  let id = `${type}-${Date.now().toString(36)}${seq++}`
  while (doc.value!.widgets.some(w => w.id === id)) id = `${type}-${Date.now().toString(36)}${seq++}`
  return id
}

function addWidget(type: string) {
  if (!doc.value) return
  const size = defaultSize(type)
  const count = doc.value.widgets.length
  const x = Math.min(doc.value.width - size.w, 80 + (count % 8) * 24)
  const y = Math.min(doc.value.height - size.h, 80 + (count % 8) * 24)
  const w: ScadaWidget = {
    id: newId(type), type, layerId: currentLayer.value,
    x: snapV(x), y: snapV(y), w: size.w, h: size.h, rotation: 0,
    props: defaultProps(type), bindings: [], animations: [], events: []
  }
  doc.value.widgets.push(w)
  selectedId.value = w.id
  commit('添加 ' + type)
}

function duplicate() {
  const w = selected.value
  if (!w || !doc.value) return
  const copy: ScadaWidget = JSON.parse(JSON.stringify(w))
  copy.id = newId(w.type)
  copy.x = snapV(w.x + 16)
  copy.y = snapV(w.y + 16)
  doc.value.widgets.push(copy)
  selectedId.value = copy.id
  commit('复制图元')
}

function removeSelected() {
  if (!doc.value || !selected.value) return
  doc.value.widgets = doc.value.widgets.filter(w => w.id !== selectedId.value)
  selectedId.value = null
  commit('删除图元')
}

function bringFront() {
  const w = selected.value
  if (!w || !doc.value) return
  doc.value.widgets = [...doc.value.widgets.filter(x => x.id !== w.id), w]
  commit('置顶')
}

function sendBack() {
  const w = selected.value
  if (!w || !doc.value) return
  doc.value.widgets = [w, ...doc.value.widgets.filter(x => x.id !== w.id)]
  commit('置底')
}

function onSelect(id: string | null) {
  selectedId.value = id
  if (id) {
    const w = doc.value?.widgets.find(x => x.id === id)
    if (w?.layerId) currentLayer.value = w.layerId
  }
}

function countOf(layerId: string) {
  return (doc.value?.widgets ?? []).filter(w => w.layerId === layerId).length
}

function addBinding() {
  const w = selected.value
  if (!w) return
  w.bindings ??= []
  w.bindings.push({ prop: 'text', tagId: tags.value[0]?.id ?? 0, format: '{v}', unit: '', label: '' })
  commit('添加绑定')
}

function addAnimation() {
  const w = selected.value
  if (!w) return
  w.animations ??= []
  w.animations.push({ kind: 'quality', tagId: tags.value[0]?.id ?? 0 })
  commit('添加动画')
}

function addNavigate() {
  const w = selected.value
  if (!w) return
  w.events ??= []
  w.events.push({ kind: 'click', action: 'navigate', target: screens.value[0]?.code })
  commit('添加跳转')
}

// ---------------- 拖拽 / 缩放

function snapV(v: number): number {
  if (!snap.value) return Math.round(v)
  return Math.round(v / 8) * 8
}
function toDoc(ev: MouseEvent) {
  const el = wrap.value!
  const r = el.getBoundingClientRect()
  return {
    x: (ev.clientX - r.left - offset.x) / scale.value,
    y: (ev.clientY - r.top - offset.y) / scale.value
  }
}

let drag: { mode: 'move' | 'resize' | 'none'; handle: string; start: { x: number; y: number }; orig: any } =
  { mode: 'none', handle: '', start: { x: 0, y: 0 }, orig: null }

function startMove(ev: MouseEvent) {
  const w = selected.value
  if (!w) return
  drag = { mode: 'move', handle: '', start: toDoc(ev), orig: { x: w.x, y: w.y, w: w.w, h: w.h } }
}

function startResize(handle: string, ev: MouseEvent) {
  const w = selected.value
  if (!w) return
  drag = { mode: 'resize', handle, start: toDoc(ev), orig: { x: w.x, y: w.y, w: w.w, h: w.h } }
}

function onMouseMove(ev: MouseEvent) {
  if (drag.mode === 'none' || !selected.value || !doc.value) return
  const p = toDoc(ev)
  const dx = p.x - drag.start.x
  const dy = p.y - drag.start.y
  const o = drag.orig

  if (drag.mode === 'move') {
    selected.value.x = snapV(clamp(o.x + dx, -200, doc.value.width))
    selected.value.y = snapV(clamp(o.y + dy, -200, doc.value.height))
    return
  }

  const h = drag.handle
  let x = o.x, y = o.y, w = o.w, ht = o.h
  if (h.includes('e')) w = o.w + dx
  if (h.includes('s')) ht = o.h + dy
  if (h.includes('w')) { w = o.w - dx; x = o.x + dx }
  if (h.includes('n')) { ht = o.h - dy; y = o.y + dy }
  if (w < 4) w = 4
  if (ht < 4) ht = 4
  selected.value.x = snapV(x)
  selected.value.y = snapV(y)
  selected.value.w = Math.max(4, snapV(w))
  selected.value.h = Math.max(4, snapV(ht))
}

function onMouseUp() {
  if (drag.mode !== 'none') {
    commit(drag.mode === 'move' ? '移动图元' : '缩放图元')
    drag.mode = 'none'
  }
}

function clamp(v: number, lo: number, hi: number) { return Math.min(hi, Math.max(lo, v)) }

function onBackgroundDown(ev: MouseEvent) {
  const t = ev.target as Element
  if (t.closest('.widget')) return
  selectedId.value = null
}

// ---------------- 保存 / 发布

async function saveDraft(silent = false) {
  if (!doc.value) return false
  saving.value = true
  try {
    doc.value.name = docName.value
    await screenApi.saveDraft(Number(route.params.id), JSON.stringify(doc.value))
    if (!silent) ElMessage.success('草稿已保存')
    return true
  } catch (e: any) {
    ElMessage.error(e?.message || '保存失败')
    return false
  } finally { saving.value = false }
}

async function publish() {
  if (!doc.value) return
  const note = await ElMessageBox.prompt('发布说明（记入版本历史）', '发布画面', {
    inputValue: `发布 v${((await screenApi.get(Number(route.params.id))).publishedVersionNo ?? 0) + 1}`,
    confirmButtonText: '发布', cancelButtonText: '取消'
  }).catch(() => null)
  if (!note) return
  publishing.value = true
  try {
    await screenApi.publish(Number(route.params.id), JSON.stringify({ ...doc.value, name: docName.value }), note.value)
    ElMessage.success('已发布，运行态立即生效')
    loadVersions()
  } catch (e: any) {
    ElMessage.error(e?.message || '发布失败')
  } finally { publishing.value = false }
}

async function loadVersions() {
  try { versions.value = await screenApi.versions(Number(route.params.id)) } catch { /* 忽略 */ }
}

async function rollback(row: any) {
  await ElMessageBox.confirm(`回滚到 v${row.versionNo}？当前草稿将被覆盖（需重新发布才生效）。`, '回滚', { type: 'warning' })
  await screenApi.rollback(Number(route.params.id), row.id)
  ElMessage.success('已回滚到草稿')
  await load()
}

function onKey(e: KeyboardEvent) {
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'z') { e.preventDefault(); undo() }
  else if ((e.ctrlKey || e.metaKey) && (e.key.toLowerCase() === 'y' || (e.shiftKey && e.key.toLowerCase() === 'z'))) {
    e.preventDefault(); redo()
  } else if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') { e.preventDefault(); saveDraft() }
  else if (e.key === 'Delete' && selected.value) { removeSelected() }
  else if (e.key === 'Escape') { selectedId.value = null }
}

watch(() => route.params.id, (v) => { if (v) load() })
onMounted(async () => {
  await loadStatic()
  await load()
  window.addEventListener('keydown', onKey)
  window.addEventListener('resize', fit)
})
onUnmounted(() => {
  window.removeEventListener('keydown', onKey)
  window.removeEventListener('resize', fit)
})
</script>

<style scoped>
.editor { display: grid; grid-template-columns: 168px minmax(0, 1fr) 300px; height: 100%; overflow: hidden; }

.palette { background: #0d1826; border-right: 1px solid var(--line); overflow-y: auto; padding: 10px; }
.p-title { font-size: 11px; color: var(--text-mute); letter-spacing: 1px; margin: 10px 0 8px; }
.p-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 6px; }
.p-item {
  display: flex; flex-direction: column; align-items: center; gap: 2px; padding: 8px 4px;
  background: #14273a; border: 1px solid var(--line); border-radius: 8px; cursor: pointer;
}
.p-item:hover { border-color: var(--accent); color: #7dd3fc; }
.p-icon { font-size: 18px; line-height: 1; }
.p-label { font-size: 11px; color: var(--text-dim); }
.layers .layer-row {
  display: flex; align-items: center; gap: 6px; font-size: 12px; padding: 6px 8px;
  border-radius: 6px; cursor: pointer; color: var(--text-dim);
}
.layers .layer-row.on { background: rgba(56,189,248,.14); color: #7dd3fc; }
.p-meta { font-size: 12px; color: var(--text-dim); }

.canvas-col { display: flex; flex-direction: column; min-width: 0; }
.canvas-col .toolbar { padding: 6px 10px; border-bottom: 1px solid var(--line); background: #0d1826; }
.canvas-wrap {
  flex: 1; min-height: 0; position: relative; overflow: hidden;
  background: #060c15; background-image: radial-gradient(#16273a 1px, transparent 1px);
  background-size: 16px 16px;
}
.stage { position: absolute; box-shadow: 0 0 0 1px #1c3044; }
.sel-box { position: absolute; border: 1px solid var(--accent); pointer-events: auto; cursor: move; }
.handle { position: absolute; width: 8px; height: 8px; background: #0b1220; border: 1px solid var(--accent); }

.inspector { background: #0d1826; border-left: 1px solid var(--line); overflow-y: auto; padding: 6px 10px 20px; }
.sec-title {
  display: flex; align-items: center; justify-content: space-between;
  font-size: 12px; color: var(--text-mute); margin: 14px 0 8px; letter-spacing: .5px;
}
.grid2 { display: grid; grid-template-columns: 52px 1fr; gap: 6px 8px; align-items: center; }
.grid2 label { font-size: 12px; color: var(--text-dim); }
.bind-row, .anim-row { display: flex; gap: 4px; align-items: center; margin-bottom: 6px; }
.tip { color: var(--text-mute); font-size: 13px; padding: 20px 6px; text-align: center; }
.tree { max-height: 100%; }
.tree-row { display: flex; align-items: center; gap: 8px; font-size: 12px; padding: 5px 6px; border-radius: 6px; cursor: pointer; }
.tree-row:hover { background: #14273a; }
.tree-row.on { background: rgba(56,189,248,.14); }
.t-type { color: #7dd3fc; width: 64px; }
.t-id { color: var(--text-mute); }
</style>
