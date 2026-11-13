<template>
  <div class="page">
    <div class="toolbar">
      <el-select v-model="selectedTags" multiple filterable remote :remote-method="searchTags"
                 :loading="tagLoading" placeholder="搜索点位编码/名称" style="min-width:340px" collapse-tags>
        <el-option v-for="t in tagOptions" :key="t.id" :label="`${t.code} · ${t.name}`" :value="t.id" />
      </el-select>
      <el-radio-group v-model="range" size="small" @change="applyRange">
        <el-radio-button label="1h">1 小时</el-radio-button>
        <el-radio-button label="8h">8 小时</el-radio-button>
        <el-radio-button label="24h">24 小时</el-radio-button>
        <el-radio-button label="7d">7 天</el-radio-button>
        <el-radio-button label="30d">30 天</el-radio-button>
      </el-radio-group>
      <el-date-picker v-model="custom" type="datetimerange" size="small" start-placeholder="开始"
                      end-placeholder="结束" style="width:340px" @change="onCustom" />
      <el-button size="small" type="primary" :loading="loading" @click="query">查询</el-button>
      <el-button size="small" :disabled="!selectedTags.length" @click="exportCsv">导出 CSV</el-button>
      <div class="spacer" />
      <span class="dim" style="font-size:12px">{{ resolutionText }}</span>
    </div>

    <div class="card">
      <div ref="chartEl" class="chart" />
    </div>

    <div v-if="series.length" class="card">
      <div class="card-title"><span>数据摘要</span></div>
      <el-table :data="summary" size="small" border>
        <el-table-column prop="code" label="点位" width="180" />
        <el-table-column prop="name" label="名称" />
        <el-table-column prop="unit" label="单位" width="80" />
        <el-table-column prop="count" label="点数" width="90" />
        <el-table-column prop="min" label="最小" width="110" />
        <el-table-column prop="max" label="最大" width="110" />
        <el-table-column prop="avg" label="平均" width="110" />
        <el-table-column prop="bad" label="异常质量" width="100" />
      </el-table>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, shallowRef } from 'vue'
import * as echarts from 'echarts'
import { ElMessage } from 'element-plus'
import { configApi, historyApi } from '@/api'
import type { HistorySeries, TagDto } from '@/api/types'
import dayjs from 'dayjs'

const tagOptions = ref<TagDto[]>([])
const tagLoading = ref(false)
const selectedTags = ref<number[]>([])
const range = ref('1h')
const custom = ref<[Date, Date] | null>(null)
const loading = ref(false)
const series = ref<HistorySeries[]>([])
const chartEl = ref<HTMLElement | null>(null)
const chart = shallowRef<echarts.ECharts | null>(null)

const palette = ['#38bdf8', '#a78bfa', '#34d399', '#fbbf24', '#f472b6', '#60a5fa', '#f87171']

const resolutionText = computed(() => {
  if (!series.value.length) return ''
  const r = series.value[0].resolution
  return r === '1m' ? '分辨率：1 分钟聚合（跨度 > 24h）' : '分辨率：原始值'
})

const summary = computed(() => series.value.map((s, i) => {
  const vals = s.points.map(p => p.value).filter((v): v is number => v != null)
  const bad = s.points.filter(p => p.quality !== 0).length
  return {
    code: s.code ?? `#${s.tagId}`,
    name: s.name ?? '',
    unit: s.unit ?? '',
    count: s.points.length,
    min: vals.length ? round(Math.min(...vals)) : '-',
    max: vals.length ? round(Math.max(...vals)) : '-',
    avg: vals.length ? round(vals.reduce((a, b) => a + b, 0) / vals.length) : '-',
    bad,
    color: palette[i % palette.length]
  }
}))

function round(v: number) { return Math.round(v * 1000) / 1000 }

async function searchTags(q: string) {
  tagLoading.value = true
  try {
    const r = await configApi.tags({ page: 1, size: 50, q })
    tagOptions.value = r.items
  } finally { tagLoading.value = false }
}

function windowOf(): { from: string; to: string } {
  if (custom.value) {
    return { from: custom.value[0].toISOString(), to: custom.value[1].toISOString() }
  }
  const to = dayjs()
  const map: Record<string, [number, dayjs.ManipulateType]> = {
    '1h': [1, 'hour'], '8h': [8, 'hour'], '24h': [24, 'hour'], '7d': [7, 'day'], '30d': [30, 'day']
  }
  const [n, unit] = map[range.value] ?? [1, 'hour']
  return { from: to.subtract(n, unit).toISOString(), to: to.toISOString() }
}

function applyRange() {
  custom.value = null
  query()
}

function onCustom() {
  if (custom.value) query()
}

async function query() {
  if (!selectedTags.value.length) { ElMessage.info('请先选择点位'); return }
  loading.value = true
  try {
    const { from, to } = windowOf()
    const maxPoints = range.value === '30d' ? 3000 : range.value === '7d' ? 2400 : 1200
    series.value = await historyApi.query(selectedTags.value, from, to, maxPoints)
    await nextTick()
    render()
  } catch (e: any) {
    ElMessage.error(e?.message || '查询失败')
  } finally { loading.value = false }
}

function render() {
  if (!chart.value && chartEl.value) chart.value = echarts.init(chartEl.value, undefined, { renderer: 'canvas' })
  const c = chart.value
  if (!c) return
  const opt: echarts.EChartsOption = {
    backgroundColor: 'transparent',
    grid: { left: 56, right: 24, top: 36, bottom: 56 },
    tooltip: {
      trigger: 'axis',
      backgroundColor: 'rgba(13,24,38,.95)',
      borderColor: '#23384f',
      textStyle: { color: '#e2e8f0', fontSize: 12 }
    },
    legend: { textStyle: { color: '#94a3b8' }, top: 4, icon: 'roundRect', itemWidth: 12, itemHeight: 8 },
    dataZoom: [
      { type: 'inside', filterMode: 'none' },
      { type: 'slider', height: 18, bottom: 12, borderColor: '#23384f', textStyle: { color: '#64748b' } }
    ],
    xAxis: {
      type: 'time',
      axisLine: { lineStyle: { color: '#23384f' } },
      axisLabel: { color: '#94a3b8', fontSize: 11 },
      splitLine: { show: false }
    },
    yAxis: {
      type: 'value',
      axisLabel: { color: '#94a3b8', fontSize: 11 },
      splitLine: { lineStyle: { color: 'rgba(35,56,79,.6)' } }
    },
    series: series.value.map((s, i) => ({
      name: `${s.code ?? s.tagId}${s.unit ? ' / ' + s.unit : ''}`,
      type: 'line',
      showSymbol: false,
      sampling: 'lttb',
      connectNulls: false,
      lineStyle: { width: 1.6, color: palette[i % palette.length] },
      itemStyle: { color: palette[i % palette.length] },
      data: s.points.map(p => [p.ts, p.quality === 0 ? p.value : null])
    }))
  }
  c.setOption(opt, true)
}

function exportCsv() {
  if (!selectedTags.value.length) return
  const { from, to } = windowOf()
  window.open(historyApi.exportUrl(selectedTags.value, from, to), '_blank')
}

function resize() { chart.value?.resize() }

onMounted(async () => {
  await searchTags('')
  window.addEventListener('resize', resize)
})
onUnmounted(() => {
  window.removeEventListener('resize', resize)
  chart.value?.dispose()
})
</script>

<style scoped>
.chart { height: 460px; width: 100%; }
</style>
