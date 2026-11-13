<template>
  <el-dialog :model-value="modelValue" title="点位写入" width="620px" @update:model-value="close">
    <div v-if="tag" class="head">
      <div>
        <div class="name">{{ tag.name }} <span class="code">{{ tag.code }}</span></div>
        <div class="meta">
          {{ tag.deviceCode || '' }} · {{ tag.addressExpr }} · {{ tag.dataType }}
          <span v-if="tag.unit"> · 单位 {{ tag.unit }}</span>
        </div>
      </div>
      <div class="current">
        <span class="k">当前值</span>
        <span class="v">{{ tag.latestText ?? tag.latestValue ?? '---' }}</span>
        <span class="pill" :class="'tag-' + qualityClass">{{ qualityText }}</span>
      </div>
    </div>

    <el-alert v-if="tag && !tag.writable" type="warning" :closable="false" show-icon
              title="该点位为只读，无法写入" style="margin-bottom:12px" />

    <el-steps :active="step" simple style="margin-bottom:16px">
      <el-step title="填入新值" />
      <el-step title="确认下发" />
      <el-step title="回读校验" />
    </el-steps>

    <div v-if="step === 0">
      <el-form label-width="90px">
        <el-form-item label="新值">
          <el-input v-model="newValue" :disabled="!tag?.writable" placeholder="仅支持数值；布尔点填 0 / 1" />
        </el-form-item>
        <el-form-item label="写入原因">
          <el-input v-model="reason" placeholder="可选，记入审计日志" />
        </el-form-item>
        <el-form-item v-if="tag && (tag.minValue != null || tag.maxValue != null)" label="允许范围">
          <span class="dim">{{ tag.minValue ?? '-∞' }} ~ {{ tag.maxValue ?? '+∞' }}</span>
        </el-form-item>
      </el-form>
    </div>

    <div v-else-if="step === 1">
      <el-alert type="warning" :closable="false" show-icon
                title="请核对改动，确认后立即下发到设备，且不可撤销（超时 60 秒需重新生成）"
                style="margin-bottom:12px" />
      <el-table :data="preview?.items ?? []" size="small" border>
        <el-table-column prop="code" label="点位" width="150" />
        <el-table-column prop="name" label="名称" />
        <el-table-column label="旧值" width="110">
          <template #default="{ row }">{{ row.oldText ?? row.oldValue ?? '---' }}</template>
        </el-table-column>
        <el-table-column label="新值" width="110">
          <template #default="{ row }">{{ row.newText ?? row.newValue ?? '---' }}</template>
        </el-table-column>
        <el-table-column label="校验" width="150">
          <template #default="{ row }">
            <span v-if="row.error" class="q-bad">{{ row.error }}</span>
            <span v-else class="q-good">通过</span>
          </template>
        </el-table-column>
      </el-table>
      <div v-if="preview?.blockers?.length" class="blockers">
        <div v-for="(b, i) in preview.blockers" :key="i" class="q-bad">· {{ b }}</div>
      </div>
      <div class="dim" style="margin-top:8px;font-size:12px">
        令牌有效期至 {{ preview ? fmtTime(preview.expiresAt) : '-' }}
      </div>
    </div>

    <div v-else>
      <el-result :icon="result?.status === 'Success' ? 'success' : 'warning'"
                 :title="result?.status === 'Success' ? '写入成功（已回读校验）' : '写入未完全成功'"
                 :sub-title="result?.message">
        <template #extra>
          <el-table :data="result?.items ?? []" size="small" border style="width:100%">
            <el-table-column prop="code" label="点位" width="150" />
            <el-table-column label="旧值" width="100">
              <template #default="{ row }">{{ row.oldValue ?? '---' }}</template>
            </el-table-column>
            <el-table-column label="目标值" width="100">
              <template #default="{ row }">{{ row.newValue ?? '---' }}</template>
            </el-table-column>
            <el-table-column label="回读值" width="110">
              <template #default="{ row }">
                <span :class="row.ok ? 'q-good' : 'q-bad'">{{ row.readBack ?? '---' }}</span>
              </template>
            </el-table-column>
            <el-table-column prop="error" label="说明" />
          </el-table>
        </template>
      </el-result>
    </div>

    <template #footer>
      <el-button @click="close(false)">关闭</el-button>
      <el-button v-if="step === 0" type="primary" :loading="loading" :disabled="!tag?.writable" @click="doPrepare">
        生成确认
      </el-button>
      <el-button v-if="step === 1" type="danger" :loading="loading" @click="doExecute">确认下发</el-button>
      <el-button v-if="step === 2" type="primary" @click="close(false)">完成</el-button>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { writeApi } from '@/api'
import type { TagDto, WriteExecuteResult, WritePrepareResult } from '@/api/types'

const props = defineProps<{ modelValue: boolean; tag?: TagDto | null }>()
const emit = defineEmits<{ (e: 'update:modelValue', v: boolean): void; (e: 'done'): void }>()

const step = ref(0)
const loading = ref(false)
const newValue = ref('')
const reason = ref('')
const preview = ref<WritePrepareResult | null>(null)
const result = ref<WriteExecuteResult | null>(null)

const qualityText = computed(() => (['良好', '不确定', '坏值', '超时'] as string[])[props.tag?.quality ?? 2] ?? '坏值')
const qualityClass = computed(() => (['good', 'uncertain', 'bad', 'timeout'] as string[])[props.tag?.quality ?? 2] ?? 'bad')

watch(() => props.modelValue, (v) => {
  if (v) {
    step.value = 0
    newValue.value = props.tag?.latestValue != null ? String(props.tag.latestValue) : ''
    reason.value = ''
    preview.value = null
    result.value = null
  }
})

function fmtTime(iso: string) {
  return new Date(iso).toLocaleTimeString('zh-CN', { hour12: false })
}

async function doPrepare() {
  if (!props.tag) return
  const n = Number(newValue.value)
  if (!Number.isFinite(n)) { ElMessage.warning('请输入数值'); return }
  loading.value = true
  try {
    preview.value = await writeApi.prepare([{ tagId: props.tag.id, numericValue: n }], reason.value || undefined)
    step.value = 1
  } catch (e: any) {
    ElMessage.error(e?.message || '生成确认失败')
  } finally { loading.value = false }
}

async function doExecute() {
  if (!preview.value) return
  if (preview.value.items.some(i => i.error) || preview.value.blockers.length) {
    ElMessage.warning('存在校验未通过项，无法下发')
    return
  }
  loading.value = true
  try {
    result.value = await writeApi.execute(preview.value.confirmToken)
    step.value = 2
    emit('done')
  } catch (e: any) {
    ElMessage.error(e?.message || '写入失败')
  } finally { loading.value = false }
}

function close(v: boolean) {
  emit('update:modelValue', false)
}
</script>

<style scoped>
.head { display: flex; justify-content: space-between; align-items: flex-start; margin-bottom: 14px; gap: 12px; }
.head .name { font-size: 15px; font-weight: 600; }
.head .code { font-size: 12px; color: var(--text-mute); margin-left: 6px; }
.head .meta { font-size: 12px; color: var(--text-mute); margin-top: 4px; }
.current { text-align: right; }
.current .k { font-size: 11px; color: var(--text-mute); display: block; }
.current .v { font-size: 20px; font-weight: 700; margin-right: 8px; }
.blockers { margin-top: 10px; font-size: 12px; }
</style>
