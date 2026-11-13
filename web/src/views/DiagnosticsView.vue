<template>
  <div class="page">
    <div class="page-title">
      <span>通信诊断 <span class="sub">端点级请求/成功率/P95 时延 · 点位在线调试</span></span>
      <div class="toolbar" style="margin:0">
        <el-button size="small" :loading="loading" @click="loadAll"><el-icon><Refresh /></el-icon>刷新</el-button>
        <el-button v-if="auth.canAdmin" size="small" @click="maintenance">时序维护</el-button>
      </div>
    </div>

    <div class="card">
      <div class="card-title"><span>端点通信指标</span><span class="dim" style="font-size:12px">{{ queues }}</span></div>
      <el-table :data="rows" size="small" border>
        <el-table-column prop="code" label="端点" width="150" />
        <el-table-column prop="name" label="名称" width="180" />
        <el-table-column label="协议" width="120">
          <template #default="{ row }">{{ row.protocol }}</template>
        </el-table-column>
        <el-table-column label="地址" width="170">
          <template #default="{ row }"><span class="mono">{{ row.ip }}:{{ row.port }}</span></template>
        </el-table-column>
        <el-table-column label="状态" width="90">
          <template #default="{ row }">
            <span class="pill" :class="row.status === 'Online' ? 'tag-good' : 'tag-bad'">{{ row.status }}</span>
          </template>
        </el-table-column>
        <el-table-column prop="requests" label="请求" width="80" />
        <el-table-column label="成功率" width="90">
          <template #default="{ row }">
            <span :class="rate(row) >= 99 ? 'q-good' : rate(row) >= 90 ? 'q-uncertain' : 'q-bad'">{{ rate(row) }}%</span>
          </template>
        </el-table-column>
        <el-table-column prop="timeouts" label="超时" width="70" />
        <el-table-column prop="errors" label="错误" width="70" />
        <el-table-column label="平均/P95" width="120">
          <template #default="{ row }">
            <span class="mono">{{ row.avgMs.toFixed(0) }} / {{ row.p95Ms.toFixed(0) }} ms</span>
          </template>
        </el-table-column>
        <el-table-column prop="consecutiveFailures" label="连续失败" width="90" />
        <el-table-column label="点位(在线/总)" width="120">
          <template #default="{ row }">{{ row.onlineTags }} / {{ row.tagCount }}</template>
        </el-table-column>
        <el-table-column prop="lastError" label="最后错误" min-width="220" show-overflow-tooltip />
        <el-table-column label="操作" width="90">
          <template #default="{ row }">
            <el-button size="small" text type="primary" @click="testEp(row)">连通测试</el-button>
          </template>
        </el-table-column>
      </el-table>
    </div>

    <div class="card">
      <div class="card-title"><span>点位在线调试</span><span class="dim" style="font-size:12px">绕过采集周期直读一次，并按四种字节序解析</span></div>
      <div class="toolbar">
        <el-select v-model="debugTagId" filterable remote :remote-method="searchTags" placeholder="搜索点位编码/名称"
                   style="width:360px" @change="doRead">
          <el-option v-for="t in tagOptions" :key="t.id" :label="`${t.code} · ${t.name}`" :value="t.id" />
        </el-select>
        <el-button size="small" type="primary" :loading="reading" :disabled="!debugTagId" @click="doRead">读取</el-button>
      </div>

      <div v-if="debug" class="debug">
        <div class="dbg-head">
          <span class="code">{{ debug.code }}</span>
          <span class="dim">{{ debug.addressExpr }} · {{ debug.dataType }} · {{ debug.byteOrder }}</span>
          <span class="spacer" />
          <span class="dim">质量</span>
          <span class="pill" :class="'tag-' + qClass(debug.quality)">{{ qualityText(debug.quality) }}</span>
        </div>
        <div class="dbg-grid">
          <div class="box">
            <div class="k">原始字节</div>
            <div class="raw mono">{{ debug.rawHex || '(空)' }}</div>
          </div>
          <div class="box">
            <div class="k">解析值</div>
            <div class="raw mono">{{ debug.value ?? '-' }}</div>
          </div>
        </div>
        <div v-if="debug.error" class="q-bad" style="margin-top:8px">{{ debug.error }}</div>
        <div class="interp">
          <div v-for="(v, k) in debug.interpretations" :key="k" class="interp-item">
            <span class="k">{{ k }}</span><span class="v mono">{{ v ?? '-' }}</span>
          </div>
        </div>
      </div>
      <el-empty v-else description="选择点位后点击读取" :image-size="60" />
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { configApi, diagApi, screenApi } from '@/api'
import type { DebugReadResult, DiagEndpointDto, TagDto } from '@/api/types'
import { useAuthStore } from '@/stores/auth'

const auth = useAuthStore()
const rows = ref<DiagEndpointDto[]>([])
const loading = ref(false)
const queues = ref('')
const debugTagId = ref<number | null>(null)
const tagOptions = ref<TagDto[]>([])
const debug = ref<DebugReadResult | null>(null)
const reading = ref(false)

function rate(r: DiagEndpointDto) {
  return r.requests === 0 ? 0 : Math.round(r.success * 1000 / r.requests) / 10
}
function qClass(q: number) { return (['good', 'uncertain', 'bad', 'timeout'] as string[])[q] ?? 'bad' }
function qualityText(q: number) { return (['良好', '不确定', '坏值', '超时'] as string[])[q] ?? '坏值' }

async function loadAll() {
  loading.value = true
  try {
    rows.value = await diagApi.endpoints()
    const q = await diagApi.queues()
    queues.value = `调度抖动 ${q.tickJitterMs} ms · 会话 ${q.sessions} · 点位 ${q.tags} · 排队 ${Object.values(q.queues ?? {}).reduce((a: any, b: any) => a + b, 0)}`
  } catch { /* 已提示 */ } finally { loading.value = false }
}

async function searchTags(q: string) {
  const r = await configApi.tags({ page: 1, size: 50, q })
  tagOptions.value = r.items
}

async function doRead() {
  if (!debugTagId.value) return
  reading.value = true
  try {
    debug.value = await diagApi.debugRead(debugTagId.value)
  } catch (e: any) {
    ElMessage.error(e?.message || '读取失败')
  } finally { reading.value = false }
}

async function testEp(row: DiagEndpointDto) {
  try {
    const r: any = await configApi.testEndpoint(row.endpointId)
    const payload = r?.data?.data ?? r?.data ?? r
    ElMessage[payload?.ok ? 'success' : 'error'](`${row.code}：${payload?.message ?? '完成'}${payload?.maxPdu ? `（PDU ${payload.maxPdu}）` : ''}`)
  } catch (e: any) {
    ElMessage.error(e?.message || '测试失败')
  }
}

async function maintenance() {
  const r = await diagApi.maintenance()
  ElMessage.success(`分区新建 ${r.partitionsCreated} 个，聚合 ${r.rolledUpRows} 行`)
}

let timer = 0
onMounted(async () => { await searchTags(''); await loadAll(); timer = window.setInterval(loadAll, 10000) })
onUnmounted(() => { if (timer) clearInterval(timer) })
</script>

<style scoped>
.debug { margin-top: 8px; }
.dbg-head { display: flex; align-items: center; gap: 10px; margin-bottom: 10px; }
.dbg-head .code { font-size: 15px; font-weight: 600; }
.dbg-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
.box { background: #14273a; border: 1px solid var(--line); border-radius: 8px; padding: 10px 12px; }
.box .k { font-size: 11px; color: var(--text-mute); margin-bottom: 6px; }
.box .raw { font-size: 15px; word-break: break-all; }
.interp { display: grid; grid-template-columns: repeat(auto-fill, minmax(200px, 1fr)); gap: 8px; margin-top: 10px; }
.interp-item { display: flex; justify-content: space-between; background: #101d2e; border: 1px solid var(--line); border-radius: 6px; padding: 6px 10px; font-size: 12px; }
.interp-item .k { color: var(--text-mute); }
</style>
