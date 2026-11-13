<template>
  <div class="page">
    <div class="page-title">
      <span>设备管理 <span class="sub">端点内可寻址单元：device_key = 从站地址 / 机架槽号</span></span>
      <div class="toolbar" style="margin:0">
        <el-select v-model="filterEp" clearable placeholder="按端点筛选" size="small" style="width:200px" @change="load">
          <el-option v-for="e in endpoints" :key="e.id" :label="`${e.code} ${e.name}`" :value="e.id" />
        </el-select>
        <el-input v-model="q" size="small" placeholder="搜索编码/名称" style="width:180px" clearable />
        <el-button size="small" type="primary" @click="openEdit()"><el-icon><Plus /></el-icon>新建设备</el-button>
      </div>
    </div>

    <div class="card">
      <el-table :data="filtered" size="small" border>
        <el-table-column prop="code" label="编码" width="150" />
        <el-table-column prop="name" label="名称" width="200" />
        <el-table-column label="所属端点" width="170">
          <template #default="{ row }">{{ epName(row.endpointId) }}</template>
        </el-table-column>
        <el-table-column prop="deviceKey" label="从站/槽号" width="110" />
        <el-table-column prop="model" label="型号" width="130" />
        <el-table-column prop="workshop" label="车间" width="110" />
        <el-table-column prop="area" label="区域" width="110" />
        <el-table-column label="点位(在线/总)" width="130">
          <template #default="{ row }">
            <span :class="row.badTags > 0 ? 'q-uncertain' : 'q-good'">{{ row.onlineTags }}</span>
            <span class="dim"> / {{ row.tagCount }}</span>
          </template>
        </el-table-column>
        <el-table-column label="状态" width="110">
          <template #default="{ row }">
            <span class="pill" :class="row.enabled ? 'tag-good' : 'tag-bad'">{{ row.enabled ? '启用' : '停用' }}</span>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="250" fixed="right">
          <template #default="{ row }">
            <el-button size="small" text type="primary" @click="$router.push({ path: '/config/tags', query: { deviceId: row.id } })">点位</el-button>
            <el-button size="small" text @click="openEdit(row)">编辑</el-button>
            <el-button size="small" text @click="togglePause(row)">{{ row.paused ? '恢复采集' : '暂停采集' }}</el-button>
            <el-button size="small" text type="danger" @click="remove(row)">删除</el-button>
          </template>
        </el-table-column>
      </el-table>
    </div>

    <el-dialog v-model="visible" :title="form.id ? '编辑设备' : '新建设备'" width="560px">
      <el-form label-width="110px">
        <el-form-item label="所属端点">
          <el-select v-model="form.endpointId" style="width:100%">
            <el-option v-for="e in endpoints" :key="e.id" :label="`${e.code} · ${e.name}`" :value="e.id" />
          </el-select>
        </el-form-item>
        <el-form-item label="设备编码"><el-input v-model="form.code" placeholder="METER-1" /></el-form-item>
        <el-form-item label="设备名称"><el-input v-model="form.name" placeholder="一号车间进线电表" /></el-form-item>
        <el-form-item label="从站地址/机架槽">
          <el-input v-model="form.deviceKey" placeholder="Modbus 填从站号 1；S7 填 0/1（机架/槽）" />
        </el-form-item>
        <el-form-item label="型号"><el-input v-model="form.model" /></el-form-item>
        <el-form-item label="车间"><el-input v-model="form.workshop" /></el-form-item>
        <el-form-item label="区域"><el-input v-model="form.area" /></el-form-item>
        <el-form-item label="启用"><el-switch v-model="form.enabled" /></el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="visible = false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="save">保存</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { configApi } from '@/api'
import type { DeviceDto, EndpointDto } from '@/api/types'

const rows = ref<DeviceDto[]>([])
const endpoints = ref<EndpointDto[]>([])
const filterEp = ref<number | null>(null)
const q = ref('')
const visible = ref(false)
const saving = ref(false)

const form = reactive({
  id: 0, endpointId: 0, code: '', name: '', deviceKey: '1',
  model: '', workshop: '', area: '', enabled: true
})

const filtered = computed(() => rows.value.filter(r => {
  const matchQ = !q.value || r.code.toLowerCase().includes(q.value.toLowerCase()) || r.name.includes(q.value)
  return matchQ
}))

function epName(id: number) {
  const e = endpoints.value.find(x => x.id === id)
  return e ? `${e.code} ${e.name}` : `#${id}`
}

async function load() {
  rows.value = await configApi.devices(filterEp.value ?? undefined)
}

function openEdit(row?: DeviceDto) {
  if (row) {
    Object.assign(form, {
      id: row.id, endpointId: row.endpointId, code: row.code, name: row.name, deviceKey: row.deviceKey,
      model: row.model || '', workshop: row.workshop || '', area: row.area || '', enabled: row.enabled
    })
  } else {
    Object.assign(form, {
      id: 0, endpointId: endpoints.value[0]?.id ?? 0, code: '', name: '', deviceKey: '1',
      model: '', workshop: '', area: '', enabled: true
    })
  }
  visible.value = true
}

async function save() {
  if (!form.code || !form.name) { ElMessage.warning('请填写编码与名称'); return }
  saving.value = true
  const body = {
    endpointId: form.endpointId, code: form.code, name: form.name, deviceKey: form.deviceKey,
    model: form.model, workshop: form.workshop, area: form.area, enabled: form.enabled
  }
  try {
    if (form.id) await configApi.updateDevice(form.id, body)
    else await configApi.createDevice(body)
    ElMessage.success('已保存')
    visible.value = false
    load()
  } catch (e: any) {
    ElMessage.error(e?.message || '保存失败')
  } finally { saving.value = false }
}

async function togglePause(row: DeviceDto) {
  const next = !row.paused
  await configApi.pauseDevice(row.id, next)
  ElMessage.success(next ? '已暂停采集（点位保持最后值，质量置 Uncertain）' : '已恢复采集')
  load()
}

async function remove(row: DeviceDto) {
  await ElMessageBox.confirm(`删除设备「${row.name}」及其 ${row.tagCount} 个点位？`, '确认删除', { type: 'warning' })
  await configApi.deleteDevice(row.id)
  ElMessage.success('已删除')
  load()
}

onMounted(async () => {
  endpoints.value = await configApi.endpoints()
  await load()
})
</script>
