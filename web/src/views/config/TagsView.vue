<template>
  <div class="page">
    <div class="page-title">
      <span>点位管理 <span class="sub">结构化地址 + 类型/量程/字节序 + 死区</span></span>
      <div class="toolbar" style="margin:0">
        <el-select v-model="deviceId" clearable placeholder="按设备筛选" size="small" style="width:220px" @change="reload">
          <el-option v-for="d in devices" :key="d.id" :label="`${d.code} ${d.name}`" :value="d.id" />
        </el-select>
        <el-input v-model="q" size="small" placeholder="搜索编码/名称/地址" style="width:200px" clearable
                  @keyup.enter="reload" @clear="reload" />
        <el-button size="small" @click="reload"><el-icon><Search /></el-icon>查询</el-button>
        <el-button size="small" type="primary" @click="openEdit()"><el-icon><Plus /></el-icon>新建点位</el-button>
      </div>
    </div>

    <div class="card">
      <el-table :data="rows" size="small" border height="calc(100vh - 250px)">
        <el-table-column prop="deviceCode" label="设备" width="120" fixed />
        <el-table-column prop="code" label="编码" width="170" fixed />
        <el-table-column prop="name" label="名称" width="180" />
        <el-table-column prop="addressExpr" label="地址" width="120" />
        <el-table-column label="类型" width="90">
          <template #default="{ row }">{{ row.dataType }}</template>
        </el-table-column>
        <el-table-column label="字节序" width="80">
          <template #default="{ row }">
            <span :class="{ 'q-uncertain': needsOrder(row) }">{{ row.byteOrder }}</span>
          </template>
        </el-table-column>
        <el-table-column label="量程" width="130">
          <template #default="{ row }">
            <span class="mono dim">{{ row.minValue ?? '-' }} ~ {{ row.maxValue ?? '-' }}</span>
          </template>
        </el-table-column>
        <el-table-column label="缩放/偏移" width="110">
          <template #default="{ row }"><span class="mono dim">{{ row.scale }} / {{ row.offset }}</span></template>
        </el-table-column>
        <el-table-column prop="unit" label="单位" width="70" />
        <el-table-column prop="scanGroupName" label="采集分组" width="110" />
        <el-table-column label="实时值" width="130">
          <template #default="{ row }">
            <span class="mono" :class="'q-' + qClass(row.quality)">
              {{ row.latestText ?? (row.latestValue ?? '---') }}
            </span>
          </template>
        </el-table-column>
        <el-table-column label="质量" width="90">
          <template #default="{ row }">
            <span class="pill" :class="'tag-' + qClass(row.quality)">{{ qText(row.quality) }}</span>
          </template>
        </el-table-column>
        <el-table-column label="可写" width="70">
          <template #default="{ row }">
            <el-tag v-if="row.writable" size="small" type="warning">可写</el-tag>
            <span v-else class="dim">只读</span>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="190" fixed="right">
          <template #default="{ row }">
            <el-button size="small" text @click="openEdit(row)">编辑</el-button>
            <el-button size="small" text type="primary" @click="openWrite(row)">写入</el-button>
            <el-button size="small" text type="danger" @click="remove(row)">删除</el-button>
          </template>
        </el-table-column>
      </el-table>

      <div class="pager">
        <el-pagination background layout="total, prev, pager, next, sizes" :total="total"
                       v-model:current-page="page" v-model:page-size="size"
                       :page-sizes="[50, 100, 200, 500]" @current-change="load" @size-change="load" />
      </div>
    </div>

    <el-dialog v-model="visible" :title="form.id ? '编辑点位' : '新建点位'" width="820px" top="6vh">
      <el-form label-width="110px">
        <div class="form-grid">
          <el-form-item label="所属设备">
            <el-select v-model="form.deviceId" style="width:100%" @change="validate">
              <el-option v-for="d in devices" :key="d.id" :label="`${d.code} ${d.name}`" :value="d.id" />
            </el-select>
          </el-form-item>
          <el-form-item label="点位编码">
            <el-input v-model="form.code" placeholder="VOLT" @blur="validate" />
          </el-form-item>
          <el-form-item label="点位名称">
            <el-input v-model="form.name" placeholder="A 相电压" />
          </el-form-item>
          <el-form-item label="地址表达式">
            <el-input v-model="form.addressExpr" placeholder="DB1.DBD4 / HR:0 / M10.0 / VW100" @blur="validate" />
          </el-form-item>
          <el-form-item label="数据类型">
            <el-select v-model="form.dataType" style="width:100%" @change="validate">
              <el-option v-for="t in dataTypes" :key="t" :label="t" :value="t" />
            </el-select>
          </el-form-item>
          <el-form-item label="字节序">
            <el-select v-model="form.byteOrder" style="width:100%" @change="validate">
              <el-option v-for="b in byteOrders" :key="b" :label="b" :value="b" />
            </el-select>
          </el-form-item>
          <el-form-item label="字符串长度">
            <el-input-number v-model="form.strLen" :min="0" :max="128" controls-position="right" />
          </el-form-item>
          <el-form-item label="采集分组">
            <el-select v-model="form.scanGroupId" style="width:100%">
              <el-option v-for="g in groups" :key="g.id" :label="`${g.name}（${g.periodMs}ms）`" :value="g.id" />
            </el-select>
          </el-form-item>
          <el-form-item label="量程下限">
            <el-input-number v-model="form.minValue" :controls="false" style="width:100%" />
          </el-form-item>
          <el-form-item label="量程上限">
            <el-input-number v-model="form.maxValue" :controls="false" style="width:100%" />
          </el-form-item>
          <el-form-item label="线性缩放">
            <el-input-number v-model="form.scale" :step="0.1" controls-position="right" style="width:100%" />
          </el-form-item>
          <el-form-item label="偏移量">
            <el-input-number v-model="form.offset" :step="0.1" controls-position="right" style="width:100%" />
          </el-form-item>
          <el-form-item label="单位">
            <el-input v-model="form.unit" placeholder="V / A / ℃" />
          </el-form-item>
          <el-form-item label="死区">
            <el-input-number v-model="form.deadband" :min="0" :step="0.1" controls-position="right" style="width:100%" />
          </el-form-item>
          <el-form-item label="可写">
            <el-switch v-model="form.writable" />
          </el-form-item>
          <el-form-item label="启用">
            <el-switch v-model="form.enabled" />
          </el-form-item>
        </div>
      </el-form>

      <div class="validate-box">
        <div class="vb-title">
          校验结果
          <span v-if="validation" class="dim" style="font-size:12px">
            地址键 {{ validation.addressKey }}
          </span>
        </div>
        <div v-if="!validation" class="dim" style="font-size:12px">修改地址/类型后自动校验</div>
        <div v-else-if="!validation.issues.length" class="q-good" style="font-size:12px">校验通过</div>
        <div v-for="(i, idx) in validation.issues" :key="idx" :class="i.isError ? 'q-bad' : 'q-uncertain'" style="font-size:12px">
          · [{{ i.field }}] {{ i.message }}
        </div>
      </div>

      <template #footer>
        <el-button @click="visible = false">取消</el-button>
        <el-button type="primary" :loading="saving" :disabled="!!validation?.issues?.some((i: any) => i.isError)" @click="save">
          保存
        </el-button>
      </template>
    </el-dialog>

    <WriteDialog v-model="writeVisible" :tag="writeTag" @done="load" />
  </div>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { useRoute } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { configApi } from '@/api'
import type { DeviceDto, ScanGroupDto, TagDto } from '@/api/types'
import WriteDialog from '@/components/WriteDialog.vue'

const route = useRoute()
const rows = ref<TagDto[]>([])
const total = ref(0)
const page = ref(1)
const size = ref(100)
const q = ref('')
const deviceId = ref<number | null>(null)
const devices = ref<DeviceDto[]>([])
const groups = ref<ScanGroupDto[]>([])
const visible = ref(false)
const saving = ref(false)
const validation = ref<any>(null)
const writeVisible = ref(false)
const writeTag = ref<TagDto | null>(null)

const dataTypes = ['BOOL', 'INT16', 'UINT16', 'INT32', 'UINT32', 'FLOAT32', 'STRING']
const byteOrders = ['ABCD', 'CDAB', 'BADC', 'DCBA']

const form = reactive({
  id: 0, deviceId: 0, code: '', name: '', addressExpr: '', dataType: 'INT16', byteOrder: 'ABCD',
  strLen: 0, scanGroupId: 0, minValue: null as number | null, maxValue: null as number | null,
  scale: 1, offset: 0, unit: '', deadband: 0, writable: false, enabled: true
})

function qClass(q: number) { return (['good', 'uncertain', 'bad', 'timeout'] as string[])[q] ?? 'bad' }
function qText(q: number) { return (['良好', '不确定', '坏值', '超时'] as string[])[q] ?? '坏值' }
function needsOrder(row: TagDto) {
  return (row.dataType === 'INT32' || row.dataType === 'UINT32' || row.dataType === 'FLOAT32') && row.byteOrder === 'ABCD'
}

async function load() {
  const r = await configApi.tags({
    page: page.value, size: size.value, q: q.value || undefined, deviceId: deviceId.value ?? undefined
  })
  rows.value = r.items
  total.value = r.total
}

function reload() { page.value = 1; load() }

function openEdit(row?: TagDto) {
  validation.value = null
  if (row) {
    Object.assign(form, {
      id: row.id, deviceId: row.deviceId, code: row.code, name: row.name, addressExpr: row.addressExpr,
      dataType: row.dataType, byteOrder: row.byteOrder, strLen: row.strLen, scanGroupId: row.scanGroupId,
      minValue: row.minValue ?? null, maxValue: row.maxValue ?? null, scale: row.scale, offset: row.offset,
      unit: row.unit || '', deadband: row.deadband, writable: row.writable, enabled: row.enabled
    })
  } else {
    Object.assign(form, {
      id: 0, deviceId: deviceId.value ?? devices.value[0]?.id ?? 0, code: '', name: '', addressExpr: '',
      dataType: 'INT16', byteOrder: 'ABCD', strLen: 0, scanGroupId: groups.value[0]?.id ?? 0,
      minValue: null, maxValue: null, scale: 1, offset: 0, unit: '', deadband: 0, writable: false, enabled: true
    })
  }
  visible.value = true
}

async function validate() {
  if (!form.deviceId || !form.addressExpr) return
  try {
    const r: any = await configApi.validateTag({ ...form })
    validation.value = r?.data ?? r
  } catch (e: any) {
    validation.value = { addressKey: '-', issues: [{ field: 'address', message: e?.message || '校验失败', isError: true }] }
  }
}

async function save() {
  saving.value = true
  const body = { ...form }
  try {
    if (form.id) await configApi.updateTag(form.id, body)
    else await configApi.createTag(body)
    ElMessage.success('已保存，采集服务将自动热加载')
    visible.value = false
    load()
  } catch (e: any) {
    ElMessage.error(e?.message || '保存失败')
  } finally { saving.value = false }
}

async function remove(row: TagDto) {
  await ElMessageBox.confirm(`删除点位「${row.name}」？历史数据保留。`, '确认删除', { type: 'warning' })
  await configApi.deleteTag(row.id)
  ElMessage.success('已删除')
  load()
}

async function openWrite(row: TagDto) {
  if (!row.writable) { ElMessage.info('该点位为只读'); return }
  writeTag.value = row
  writeVisible.value = true
}

onMounted(async () => {
  if (route.query.deviceId) deviceId.value = Number(route.query.deviceId)
  devices.value = await configApi.devices()
  groups.value = await configApi.scanGroups()
  await load()
})
</script>

<style scoped>
.form-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 0 18px; }
.validate-box { background: #14273a; border: 1px solid var(--line); border-radius: 8px; padding: 10px 12px; }
.vb-title { font-size: 12px; color: var(--text-dim); margin-bottom: 6px; display: flex; gap: 10px; align-items: center; }
.pager { display: flex; justify-content: flex-end; margin-top: 12px; }
</style>
