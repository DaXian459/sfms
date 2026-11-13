<template>
  <div class="page">
    <div class="page-title">
      <span>审计日志 <span class="sub">配置变更 / 权限变更 / 写入 / 登录 四类事件全留痕</span></span>
      <div class="toolbar" style="margin:0">
        <el-input v-model="action" size="small" placeholder="动作，如 write / tag.update" style="width:200px" clearable />
        <el-input v-model="username" size="small" placeholder="操作人" style="width:140px" clearable />
        <el-date-picker v-model="range" type="datetimerange" size="small" start-placeholder="开始" end-placeholder="结束" style="width:330px" />
        <el-button size="small" type="primary" @click="reload">查询</el-button>
      </div>
    </div>

    <div class="card">
      <el-table :data="rows" size="small" border height="calc(100vh - 220px)">
        <el-table-column label="时间" width="170">
          <template #default="{ row }">{{ new Date(row.ts).toLocaleString('zh-CN', { hour12: false }) }}</template>
        </el-table-column>
        <el-table-column prop="username" label="操作人" width="110" />
        <el-table-column prop="action" label="动作" width="170" />
        <el-table-column label="对象" width="150">
          <template #default="{ row }">{{ row.targetType }}#{{ row.targetId }}</template>
        </el-table-column>
        <el-table-column label="结果" width="90">
          <template #default="{ row }">
            <span class="pill" :class="row.result === 'Success' ? 'tag-good' : 'tag-bad'">{{ row.result }}</span>
          </template>
        </el-table-column>
        <el-table-column prop="reason" label="原因/说明" width="200" show-overflow-tooltip />
        <el-table-column label="变更前" min-width="180" show-overflow-tooltip>
          <template #default="{ row }"><span class="mono dim">{{ row.before }}</span></template>
        </el-table-column>
        <el-table-column label="变更后" min-width="180" show-overflow-tooltip>
          <template #default="{ row }"><span class="mono dim">{{ row.after }}</span></template>
        </el-table-column>
        <el-table-column prop="ip" label="来源 IP" width="130" />
      </el-table>
      <div class="pager">
        <el-pagination background layout="total, prev, pager, next, sizes" :total="total"
                       v-model:current-page="page" v-model:page-size="size"
                       :page-sizes="[50, 100, 200]" @current-change="load" @size-change="load" />
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { diagApi } from '@/api'
import type { AuditDto } from '@/api/types'

const rows = ref<AuditDto[]>([])
const total = ref(0)
const page = ref(1)
const size = ref(50)
const action = ref('')
const username = ref('')
const range = ref<[Date, Date] | null>(null)

async function load() {
  const r = await diagApi.audit({
    page: page.value, size: size.value,
    action: action.value || undefined,
    username: username.value || undefined,
    from: range.value ? range.value[0].toISOString() : undefined,
    to: range.value ? range.value[1].toISOString() : undefined
  })
  rows.value = r.items
  total.value = r.total
}

function reload() { page.value = 1; load() }

onMounted(load)
</script>

<style scoped>
.pager { display: flex; justify-content: flex-end; margin-top: 12px; }
</style>
