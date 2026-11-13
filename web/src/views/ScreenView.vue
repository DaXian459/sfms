<template>
  <div class="screen-page">
    <div class="bar">
      <div class="left">
        <el-button size="small" @click="$router.push('/screens')">
          <el-icon><Back /></el-icon> 画面列表
        </el-button>
        <span class="name">{{ meta.name || '加载中…' }}</span>
        <el-tag v-if="meta.status" size="small" :type="meta.status === 'Published' ? 'success' : 'info'">
          {{ meta.status === 'Published' ? `已发布 v${meta.publishedVersionNo}` : '草稿' }}
        </el-tag>
        <span class="dim src">{{ meta.source === 'published' ? '运行态取已发布版本' : '尚未发布，展示草稿' }}</span>
      </div>
      <div class="spacer" />
      <span class="dim">{{ tagIds.length }} 个绑定点位</span>
      <el-button size="small" :loading="loading" @click="load">
        <el-icon><Refresh /></el-icon> 刷新
      </el-button>
      <el-button size="small" @click="toggleFull">
        <el-icon><FullScreen /></el-icon> {{ isFull ? '退出全屏' : '全屏' }}
      </el-button>
      <el-button v-if="canEdit" size="small" type="primary" @click="$router.push(`/screens/${route.params.id}/edit`)">
        <el-icon><Edit /></el-icon> 编辑
      </el-button>
    </div>

    <div class="viewport" ref="viewport">
      <ScadaCanvas v-if="doc" :doc="doc" :values="values" mode="run" @navigate="onNavigate" @pick="onPick" />
      <div v-else class="empty">
        <el-empty :description="error || '画面加载中…'" />
      </div>
    </div>

    <div v-if="boundTags.length" class="dock">
      <div class="dock-head">
        <span>点位实时值</span>
        <span class="dim">单击可写点位可直接下发（需操作员及以上）</span>
      </div>
      <div class="dock-list">
        <div v-for="t in boundTags" :key="t.id" class="dock-item" :class="{ writable: t.writable }"
             @click="t.writable && canWrite ? openWrite(t) : null">
          <span class="n">{{ t.name }}</span>
          <span class="v mono" :class="'q-' + qClass(t.quality)">{{ displayOf(t) }}</span>
          <span class="u dim">{{ t.unit || '' }}</span>
        </div>
      </div>
    </div>

    <WriteDialog v-model="writeVisible" :tag="writeTag" @done="reloadTags" />
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import ScadaCanvas from '@/scada/ScadaCanvas.vue'
import WriteDialog from '@/components/WriteDialog.vue'
import { configApi, screenApi } from '@/api'
import type { ScadaDoc } from '@/scada/types'
import type { ScreenDto, TagDto } from '@/api/types'
import { useRealtimeStore } from '@/stores/realtime'
import { useAuthStore } from '@/stores/auth'

const route = useRoute()
const router = useRouter()
const rt = useRealtimeStore()
const auth = useAuthStore()

const doc = ref<ScadaDoc | null>(null)
const meta = ref<any>({})
const error = ref('')
const loading = ref(false)
const isFull = ref(false)
const screens = ref<ScreenDto[]>([])
const boundTags = ref<TagDto[]>([])
const writeVisible = ref(false)
const writeTag = ref<TagDto | null>(null)

const canEdit = computed(() => screens.value.find(s => s.id === Number(route.params.id))?.canEdit ?? auth.canConfig)
const canWrite = computed(() => auth.canWrite)

const values = computed(() => rt.values)

const tagIds = computed(() => {
  const set = new Set<number>()
  for (const w of doc.value?.widgets ?? []) {
    for (const b of w.bindings ?? []) if (b.tagId) set.add(b.tagId)
    for (const a of w.animations ?? []) if (a.tagId) set.add(a.tagId)
  }
  return Array.from(set)
})

async function load() {
  loading.value = true
  error.value = ''
  try {
    const id = Number(route.params.id)
    const data = await screenApi.get(id, 'published')
    doc.value = typeof data.json === 'string' ? JSON.parse(data.json) : data.json
    meta.value = data
    if (!screens.value.length) screens.value = await screenApi.list()
    const sub = tagIds.value
    if (sub.length) rt.connect(sub, 1000)
    else rt.updateSubscription([])
    await loadTags()
  } catch (e: any) {
    error.value = e?.message || '画面加载失败'
    doc.value = null
  } finally {
    loading.value = false
  }
}

async function loadTags() {
  if (!tagIds.value.length) { boundTags.value = []; return }
  try {
    const all: TagDto[] = []
    // 分页拉取这些点位的静态属性（含量程/单位/可写标记）
    const pageSize = 200
    for (let page = 1; page <= 10; page++) {
      const r = await configApi.tags({ page, size: pageSize, ids: tagIds.value.join(',') })
      all.push(...r.items)
      if (all.length >= r.total || r.items.length < pageSize) break
    }
    const map = new Map(all.map(t => [t.id, t]))
    boundTags.value = tagIds.value.map(id => map.get(id)).filter(Boolean) as TagDto[]
  } catch { boundTags.value = [] }
}

async function reloadTags() {
  await loadTags()
}

function displayOf(t: TagDto) {
  const v = rt.values[t.id]
  if (!v) return '---'
  if (v.quality !== 0) return '---'
  return v.text ?? (v.value != null ? String(Math.round(v.value * 100) / 100) : '---')
}
function qClass(q: number) {
  return (['good', 'uncertain', 'bad', 'timeout'] as string[])[q] ?? 'bad'
}

function onNavigate(code: string) {
  const target = screens.value.find(s => s.code === code)
  if (target) router.push(`/screen/${target.id}`)
  else ElMessage.warning(`未找到画面 ${code}`)
}

function onPick() { /* 运行态空白点击不处理 */ }

async function openWrite(t: TagDto) {
  if (t.dataType === 'STRING') { ElMessage.info('字符串点位请在点位管理中写入'); return }
  writeTag.value = t
  writeVisible.value = true
}

function toggleFull() {
  const el = document.documentElement
  if (!document.fullscreenElement) {
    el.requestFullscreen?.().then(() => { isFull.value = true }).catch(() => { })
  } else {
    document.exitFullscreen?.().then(() => { isFull.value = false }).catch(() => { })
  }
}

watch(() => route.params.id, (v) => { if (v) load() })

onMounted(load)
onUnmounted(() => { /* 保持 ws 连接，画面切换由 updateSubscription 复用 */ })
</script>

<style scoped>
.screen-page { height: 100%; display: flex; flex-direction: column; min-height: 0; }
.bar {
  display: flex; align-items: center; gap: 10px; padding: 8px 16px;
  border-bottom: 1px solid var(--line); background: #0d1826;
}
.bar .left { display: flex; align-items: center; gap: 10px; }
.bar .name { font-size: 15px; font-weight: 600; }
.bar .src { font-size: 12px; }
.viewport { flex: 1; min-height: 0; display: grid; place-items: center; background: #060c15; padding: 8px; }
.empty { color: var(--text-mute); }
.dock { border-top: 1px solid var(--line); background: #0d1826; padding: 8px 16px 10px; }
.dock-head { display: flex; justify-content: space-between; font-size: 12px; color: var(--text-dim); margin-bottom: 6px; }
.dock-list { display: flex; gap: 8px; overflow-x: auto; padding-bottom: 4px; }
.dock-item {
  flex: 0 0 auto; display: flex; align-items: baseline; gap: 6px; background: #14273a;
  border: 1px solid var(--line); border-radius: 8px; padding: 6px 10px; font-size: 12px;
}
.dock-item.writable { cursor: pointer; }
.dock-item.writable:hover { border-color: var(--accent); }
.dock-item .n { color: var(--text-dim); }
.dock-item .v { font-weight: 700; font-size: 14px; }
.dock-item .u { font-size: 11px; }
</style>
