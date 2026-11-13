<template>
  <div class="page">
    <div class="page-title">
      <span>采集分组 <span class="sub">独立扫描周期与合并阈值，周期需为 50ms 倍数（200~60000）</span></span>
      <div class="toolbar" style="margin:0">
        <el-button size="small" type="primary" @click="openEdit()"><el-icon><Plus /></el-icon>新建分组</el-button>
      </div>
    </div>

    <div class="card">
      <el-table :data="rows" size="small" border>
        <el-table-column prop="name" label="分组名称" width="200" />
        <el-table-column prop="periodMs" label="扫描周期(ms)" width="140" />
        <el-table-column prop="priority" label="优先级" width="100" />
        <el-table-column prop="mergeGap" label="合并间隔(字节)" width="140" />
        <el-table-column prop="tagCount" label="点位数" width="100" />
        <el-table-column label="说明" />
        <el-table-column label="操作" width="120" fixed="right">
          <template #default="{ row }">
            <el-button size="small" text @click="openEdit(row)">编辑</el-button>
          </template>
        </el-table-column>
      </el-table>
    </div>

    <el-dialog v-model="visible" :title="form.id ? '编辑分组' : '新建分组'" width="460px">
      <el-form label-width="120px">
        <el-form-item label="分组名称"><el-input v-model="form.name" /></el-form-item>
        <el-form-item label="扫描周期(ms)">
          <el-input-number v-model="form.periodMs" :min="200" :max="60000" :step="50" controls-position="right" />
        </el-form-item>
        <el-form-item label="优先级">
          <el-input-number v-model="form.priority" :min="1" :max="10" controls-position="right" />
        </el-form-item>
        <el-form-item label="合并间隔(字节)">
          <el-input-number v-model="form.mergeGap" :min="0" :max="64" controls-position="right" />
        </el-form-item>
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
import { ElMessage } from 'element-plus'
import { configApi } from '@/api'
import type { ScanGroupDto } from '@/api/types'

const rows = ref<ScanGroupDto[]>([])
const visible = ref(false)
const saving = ref(false)
const form = reactive({ id: 0, name: '', periodMs: 1000, priority: 5, mergeGap: 16 })

async function load() { rows.value = await configApi.scanGroups() }

function openEdit(row?: ScanGroupDto) {
  Object.assign(form, row
    ? { id: row.id, name: row.name, periodMs: row.periodMs, priority: row.priority, mergeGap: row.mergeGap }
    : { id: 0, name: '', periodMs: 1000, priority: 5, mergeGap: 16 })
  visible.value = true
}

async function save() {
  if (!form.name) { ElMessage.warning('请填写分组名称'); return }
  if (form.periodMs % 50 !== 0) { ElMessage.warning('周期需为 50 的倍数'); return }
  saving.value = true
  try {
    const body = { name: form.name, periodMs: form.periodMs, priority: form.priority, mergeGap: form.mergeGap }
    if (form.id) await configApi.updateScanGroup(form.id, body)
    else await configApi.createScanGroup(body)
    ElMessage.success('已保存')
    visible.value = false
    load()
  } catch (e: any) {
    ElMessage.error(e?.message || '保存失败')
  } finally { saving.value = false }
}

onMounted(load)
</script>
