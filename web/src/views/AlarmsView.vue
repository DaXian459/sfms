<template>
  <div class="page">
    <div class="page-title">
      <span>报警中心 <span class="sub">阈值 / 延时 / 回差 判定 · 确认留痕</span></span>
      <div class="toolbar" style="margin:0">
        <el-radio-group v-model="state" size="small" @change="load">
          <el-radio-button label="active">活动</el-radio-button>
          <el-radio-button label="uncleared">未清除</el-radio-button>
          <el-radio-button label="cleared">已恢复</el-radio-button>
          <el-radio-button label="all">全部</el-radio-button>
        </el-radio-group>
        <el-button size="small" :loading="loading" @click="load"><el-icon><Refresh /></el-icon>刷新</el-button>
        <el-button v-if="auth.canWrite" size="small" :disabled="!selection.length" @click="ackSelected">批量确认</el-button>
      </div>
    </div>

    <div class="kpi-grid" style="margin-bottom:14px">
      <div class="kpi"><div class="k">活动报警</div><div class="v" style="color:#ef4444">{{ activeCount }}</div></div>
      <div class="kpi"><div class="k">已确认</div><div class="v" style="color:#f59e0b">{{ counts.Acked ?? 0 }}</div></div>
      <div class="kpi"><div class="k">已恢复</div><div class="v" style="color:#22c55e">{{ counts.Cleared ?? 0 }}</div></div>
    </div>

    <div class="card">
      <el-table :data="rows" size="small" border @selection-change="onSelection">
        <el-table-column type="selection" width="42" />
        <el-table-column label="级别" width="90">
          <template #default="{ row }">
            <span class="pill" :class="row.severity === 'Critical' ? 'tag-bad' : 'tag-uncertain'">{{ row.severity }}</span>
          </template>
        </el-table-column>
        <el-table-column label="类型" width="100">
          <template #default="{ row }">{{ kindText(row.kind) }}</template>
        </el-table-column>
        <el-table-column prop="tagCode" label="点位" width="170" />
        <el-table-column prop="tagName" label="名称" width="160" />
        <el-table-column prop="message" label="说明" />
        <el-table-column label="触发值" width="100">
          <template #default="{ row }">{{ row.triggerValue ?? '-' }}</template>
        </el-table-column>
        <el-table-column label="开始" width="160">
          <template #default="{ row }">{{ fmt(row.startedAt) }}</template>
        </el-table-column>
        <el-table-column label="结束" width="160">
          <template #default="{ row }">{{ row.endedAt ? fmt(row.endedAt) : '-' }}</template>
        </el-table-column>
        <el-table-column label="状态" width="90">
          <template #default="{ row }">
            <span class="pill" :class="row.state === 'Active' ? 'tag-bad' : row.state === 'Acked' ? 'tag-uncertain' : 'tag-good'">
              {{ stateText(row.state) }}
            </span>
          </template>
        </el-table-column>
        <el-table-column label="确认" width="160">
          <template #default="{ row }">
            <span v-if="row.ackBy">{{ row.ackBy }} · {{ fmt(row.ackAt) }}</span>
            <el-button v-else-if="row.state === 'Active' && auth.canWrite" size="small" text type="primary"
                       @click="ack(row)">确认</el-button>
            <span v-else class="dim">-</span>
          </template>
        </el-table-column>
      </el-table>

      <div class="pager">
        <el-pagination background layout="total, prev, pager, next, sizes" :total="total"
                       v-model:current-page="page" v-model:page-size="size"
                       :page-sizes="[20, 50, 100, 200]" @current-change="load" @size-change="load" />
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { alarmApi } from '@/api'
import type { AlarmEventDto } from '@/api/types'
import { useAuthStore } from '@/stores/auth'

const auth = useAuthStore()
const rows = ref<AlarmEventDto[]>([])
const total = ref(0)
const page = ref(1)
const size = ref(50)
const state = ref('active')
const counts = ref<Record<string, number>>({})
const activeCount = ref(0)
const loading = ref(false)
const selection = ref<AlarmEventDto[]>([])

function kindText(k: string) {
  return ({ Hi: '高限', HiHi: '高高限', Lo: '低限', LoLo: '低低限', Comm: '通信', Overload: '过载' } as Record<string, string>)[k] ?? k
}
function stateText(s: string) {
  return ({ Active: '活动', Acked: '已确认', Cleared: '已恢复' } as Record<string, string>)[s] ?? s
}
function fmt(iso?: string) {
  return iso ? new Date(iso).toLocaleString('zh-CN', { hour12: false }) : '-'
}

async function load() {
  loading.value = true
  try {
    const r = await alarmApi.list({ state: state.value, page: page.value, size: size.value })
    rows.value = r.items
    total.value = r.total
    counts.value = r.counts ?? {}
    activeCount.value = r.activeCount ?? 0
  } catch { /* 已提示 */ } finally { loading.value = false }
}

function onSelection(v: AlarmEventDto[]) { selection.value = v }

async function ack(row: AlarmEventDto) {
  const r = await ElMessageBox.prompt('确认备注（可选）', '确认报警', { inputPlaceholder: '例如：已通知维修班' })
    .catch(() => null)
  if (!r) return
  await alarmApi.ack(row.id, r.value)
  ElMessage.success('已确认')
  load()
}

async function ackSelected() {
  const ids = selection.value.filter(r => r.state === 'Active').map(r => r.id)
  if (!ids.length) { ElMessage.info('所选行均无需确认'); return }
  await alarmApi.ackBatch(ids)
  ElMessage.success(`已确认 ${ids.length} 条`)
  load()
}

let timer = 0
onMounted(() => { load(); timer = window.setInterval(load, 20000) })
onUnmounted(() => { if (timer) clearInterval(timer) })
</script>

<style scoped>
.pager { display: flex; justify-content: flex-end; margin-top: 12px; }
</style>
