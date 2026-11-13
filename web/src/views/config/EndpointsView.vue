<template>
  <div class="page">
    <div class="page-title">
      <span>端点管理 <span class="sub">一次物理通信连接：IP + 端口 + 协议 + 连接参数</span></span>
      <div class="toolbar" style="margin:0">
        <el-button size="small" :loading="loading" @click="load"><el-icon><Refresh /></el-icon>刷新</el-button>
        <el-button size="small" type="primary" @click="openEdit()"><el-icon><Plus /></el-icon>新建端点</el-button>
      </div>
    </div>

    <div class="card">
      <el-table :data="rows" size="small" border>
        <el-table-column prop="code" label="编码" width="140" />
        <el-table-column prop="name" label="名称" width="180" />
        <el-table-column prop="protocol" label="协议" width="170" />
        <el-table-column label="地址" width="170">
          <template #default="{ row }"><span class="mono">{{ row.ip }}:{{ row.port }}</span></template>
        </el-table-column>
        <el-table-column label="状态" width="100">
          <template #default="{ row }">
            <span class="pill" :class="row.status === 'Online' ? 'tag-good' : 'tag-bad'">{{ row.status }}</span>
          </template>
        </el-table-column>
        <el-table-column prop="deviceCount" label="设备" width="70" />
        <el-table-column prop="tagCount" label="点位" width="70" />
        <el-table-column label="采集/启用" width="110">
          <template #default="{ row }">
            {{ row.scanEnabled ? '采集' : '停采' }} / {{ row.enabled ? '启用' : '停用' }}
          </template>
        </el-table-column>
        <el-table-column label="最后成功" width="160">
          <template #default="{ row }">{{ row.lastOkAt ? fmt(row.lastOkAt) : '-' }}</template>
        </el-table-column>
        <el-table-column prop="remark" label="备注" min-width="160" show-overflow-tooltip />
        <el-table-column label="操作" width="200" fixed="right">
          <template #default="{ row }">
            <el-button size="small" text type="primary" @click="test(row)">测试</el-button>
            <el-button size="small" text @click="openEdit(row)">编辑</el-button>
            <el-button size="small" text type="danger" @click="remove(row)">删除</el-button>
          </template>
        </el-table-column>
      </el-table>
    </div>

    <el-dialog v-model="visible" :title="form.id ? '编辑端点' : '新建端点'" width="560px">
      <el-form label-width="110px">
        <el-form-item label="编码"><el-input v-model="form.code" placeholder="EP-GW-01" /></el-form-item>
        <el-form-item label="名称"><el-input v-model="form.name" placeholder="一号车间网关" /></el-form-item>
        <el-form-item label="协议">
          <el-select v-model="form.protocol" style="width:100%">
            <el-option label="S7 (ISO-on-TCP)" value="S7" />
            <el-option label="Modbus TCP" value="MODBUS_TCP" />
            <el-option label="Modbus RTU (串口服务器)" value="MODBUS_RTU" />
            <el-option label="Modbus RTU over TCP" value="MODBUS_RTU_OVER_TCP" />
          </el-select>
        </el-form-item>
        <el-form-item label="IP / 主机">
          <el-input v-model="form.ip" placeholder="127.0.0.1" />
        </el-form-item>
        <el-form-item label="端口">
          <el-input-number v-model="form.port" :min="1" :max="65535" controls-position="right" />
          <span class="dim" style="margin-left:8px;font-size:12px">S7 默认 102，Modbus 默认 502</span>
        </el-form-item>
        <el-form-item label="连接参数">
          <el-input v-model="form.connParams" type="textarea" :rows="4"
                    placeholder='{"rack":0,"slot":1,"timeoutMs":2000,"retry":2,"cpu":"S71200"}' />
        </el-form-item>
        <el-form-item label="采集开关">
          <el-switch v-model="form.scanEnabled" active-text="采集" />
          <el-switch v-model="form.enabled" active-text="启用" style="margin-left:20px" />
        </el-form-item>
        <el-form-item label="备注"><el-input v-model="form.remark" /></el-form-item>
      </el-form>
      <el-alert type="info" :closable="false" show-icon
                title="S7 需在 PLC 侧开启「允许来自远程对象的 PUT/GET 通信访问」，并关闭数据块的「优化的块访问」" />
      <template #footer>
        <el-button @click="visible = false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="save">保存</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { configApi } from '@/api'
import type { EndpointDto } from '@/api/types'

const rows = ref<EndpointDto[]>([])
const loading = ref(false)
const visible = ref(false)
const saving = ref(false)

const form = reactive({
  id: 0, code: '', name: '', protocol: 'MODBUS_TCP', ip: '127.0.0.1', port: 502,
  connParams: '{"timeoutMs":2000,"retry":2}', scanEnabled: true, enabled: true, remark: ''
})

function fmt(iso: string) { return new Date(iso).toLocaleString('zh-CN', { hour12: false }) }

async function load() {
  loading.value = true
  try { rows.value = await configApi.endpoints() } finally { loading.value = false }
}

function openEdit(row?: EndpointDto) {
  if (row) {
    Object.assign(form, {
      id: row.id, code: row.code, name: row.name, protocol: row.protocol, ip: row.ip, port: row.port,
      connParams: row.connParams || '{}', scanEnabled: row.scanEnabled, enabled: row.enabled, remark: row.remark || ''
    })
  } else {
    Object.assign(form, {
      id: 0, code: '', name: '', protocol: 'MODBUS_TCP', ip: '127.0.0.1', port: 502,
      connParams: '{"timeoutMs":2000,"retry":2}', scanEnabled: true, enabled: true, remark: ''
    })
  }
  visible.value = true
}

async function save() {
  if (!form.code || !form.name) { ElMessage.warning('请填写编码与名称'); return }
  saving.value = true
  const body = {
    code: form.code, name: form.name, protocol: form.protocol, ip: form.ip, port: form.port,
    connParams: form.connParams, scanEnabled: form.scanEnabled, enabled: form.enabled, remark: form.remark
  }
  try {
    if (form.id) await configApi.updateEndpoint(form.id, body)
    else await configApi.createEndpoint(body)
    ElMessage.success('已保存，采集服务将自动热加载')
    visible.value = false
    load()
  } catch (e: any) {
    ElMessage.error(e?.message || '保存失败')
  } finally { saving.value = false }
}

async function test(row: EndpointDto) {
  const r: any = await configApi.testEndpoint(row.id)
  const payload = r?.data?.data ?? r?.data ?? r
  ElMessage[payload?.ok ? 'success' : 'error'](`${row.code}：${payload?.message ?? '未知'}${payload?.maxPdu ? `（PDU ${payload.maxPdu}）` : ''}`)
}

async function remove(row: EndpointDto) {
  await ElMessageBox.confirm(`删除端点「${row.name}」将同时影响其下 ${row.deviceCount} 台设备与 ${row.tagCount} 个点位。`, '确认删除', { type: 'warning' })
  await configApi.deleteEndpoint(row.id)
  ElMessage.success('已删除')
  load()
}

onMounted(load)
</script>
