<template>
  <div class="page">
    <div class="page-title">
      <span>报警规则 <span class="sub">阈值 + 延时（去抖）+ 回差（防抖）三维判定</span></span>
      <div class="toolbar" style="margin:0">
        <el-button size="small" type="primary" @click="openEdit()"><el-icon><Plus /></el-icon>新建规则</el-button>
      </div>
    </div>

    <div class="card">
      <el-table :data="rows" size="small" border>
        <el-table-column label="类型" width="110">
          <template #default="{ row }">{{ kindText(row.kind) }}</template>
        </el-table-column>
        <el-table-column label="对象" width="240">
          <template #default="{ row }">
            <span v-if="row.tagCode">{{ row.tagCode }} · {{ row.tagName }}</span>
            <span v-else-if="row.endpointId" class="dim">端点 #{{ row.endpointId }}</span>
            <span v-else class="dim">-</span>
          </template>
        </el-table-column>
        <el-table-column prop="threshold" label="阈值" width="110" />
        <el-table-column prop="hysteresis" label="回差" width="100" />
        <el-table-column label="延时" width="110">
          <template #default="{ row }">{{ row.onDelayMs }} ms</template>
        </el-table-column>
        <el-table-column prop="severity" label="级别" width="110" />
        <el-table-column label="启用" width="90">
          <template #default="{ row }">
            <el-tag size="small" :type="row.enabled ? 'success' : 'info'">{{ row.enabled ? '启用' : '停用' }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="140" fixed="right">
          <template #default="{ row }">
            <el-button size="small" text @click="openEdit(row)">编辑</el-button>
            <el-button size="small" text type="danger" @click="remove(row)">删除</el-button>
          </template>
        </el-table-column>
      </el-table>
    </div>

    <el-dialog v-model="visible" :title="form.id ? '编辑规则' : '新建规则'" width="540px">
      <el-form label-width="110px">
        <el-form-item label="报警类型">
          <el-select v-model="form.kind" style="width:100%">
            <el-option label="高限 Hi" value="Hi" />
            <el-option label="高高限 HiHi" value="HiHi" />
            <el-option label="低限 Lo" value="Lo" />
            <el-option label="低低限 LoLo" value="LoLo" />
            <el-option label="通信中断 Comm" value="Comm" />
            <el-option label="采集过载 Overload" value="Overload" />
          </el-select>
        </el-form-item>
        <el-form-item label="关联点位">
          <el-select v-model="form.tagId" filterable remote :remote-method="searchTags" clearable
                     placeholder="搜索点位" style="width:100%">
            <el-option v-for="t in tagOptions" :key="t.id" :label="`${t.code} · ${t.name}`" :value="t.id" />
          </el-select>
        </el-form-item>
        <el-form-item label="阈值">
          <el-input-number v-model="form.threshold" :controls="false" style="width:100%" />
        </el-form-item>
        <el-form-item label="回差">
          <el-input-number v-model="form.hysteresis" :min="0" :controls="false" style="width:100%" />
        </el-form-item>
        <el-form-item label="延时(ms)">
          <el-input-number v-model="form.onDelayMs" :min="0" :max="3600000" :step="500" controls-position="right" style="width:100%" />
        </el-form-item>
        <el-form-item label="级别">
          <el-select v-model="form.severity" style="width:100%">
            <el-option label="Critical 严重" value="Critical" />
            <el-option label="Warn 警告" value="Warn" />
            <el-option label="Info 提示" value="Info" />
          </el-select>
        </el-form-item>
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
import { onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { alarmApi, configApi } from '@/api'
import type { AlarmRuleDto, TagDto } from '@/api/types'

const rows = ref<AlarmRuleDto[]>([])
const tagOptions = ref<TagDto[]>([])
const visible = ref(false)
const saving = ref(false)

const form = reactive({
  id: 0, tagId: null as number | null, endpointId: null as number | null, kind: 'Hi',
  threshold: 0, hysteresis: 0, onDelayMs: 0, severity: 'Warn', enabled: true
})

function kindText(k: string) {
  return ({ Hi: '高限', HiHi: '高高限', Lo: '低限', LoLo: '低低限', Comm: '通信', Overload: '过载' } as Record<string, string>)[k] ?? k
}

async function load() { rows.value = await alarmApi.rules() }

async function searchTags(q: string) {
  const r = await configApi.tags({ page: 1, size: 50, q })
  tagOptions.value = r.items
}

async function openEdit(row?: AlarmRuleDto) {
  if (row) {
    Object.assign(form, {
      id: row.id, tagId: row.tagId ?? null, endpointId: row.endpointId ?? null, kind: row.kind,
      threshold: row.threshold, hysteresis: row.hysteresis, onDelayMs: row.onDelayMs,
      severity: row.severity, enabled: row.enabled
    })
    if (row.tagId) await searchTags(row.tagCode ?? '')
  } else {
    Object.assign(form, {
      id: 0, tagId: null, endpointId: null, kind: 'Hi',
      threshold: 0, hysteresis: 0, onDelayMs: 0, severity: 'Warn', enabled: true
    })
  }
  visible.value = true
}

async function save() {
  if (!form.tagId && !form.endpointId) { ElMessage.warning('请选择关联点位'); return }
  saving.value = true
  try {
    const body = {
      tagId: form.tagId, endpointId: form.endpointId, kind: form.kind, threshold: form.threshold,
      hysteresis: form.hysteresis, onDelayMs: form.onDelayMs, severity: form.severity, enabled: form.enabled
    }
    if (form.id) await alarmApi.updateRule(form.id, body)
    else await alarmApi.createRule(body)
    ElMessage.success('已保存，采集服务将热加载规则')
    visible.value = false
    load()
  } catch (e: any) {
    ElMessage.error(e?.message || '保存失败')
  } finally { saving.value = false }
}

async function remove(row: AlarmRuleDto) {
  await ElMessageBox.confirm('删除该报警规则？', '确认删除', { type: 'warning' })
  await alarmApi.deleteRule(row.id)
  load()
}

onMounted(async () => { await load(); await searchTags('') })
</script>
