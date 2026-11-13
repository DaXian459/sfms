<template>
  <div class="page">
    <div class="page-title">
      <span>批量导入 <span class="sub">两步式：先全量校验（不落库）→ 再提交；冲突行单独报错</span></span>
      <div class="toolbar" style="margin:0">
        <el-button size="small" @click="download(importApi.templateUrl)">下载模板</el-button>
        <el-button size="small" @click="download(importApi.exportUrl())">导出当前点位表</el-button>
      </div>
    </div>

    <div class="card">
      <div class="card-title"><span>1. 上传文件</span></div>
      <el-upload drag :auto-upload="false" :show-file-list="false" accept=".xlsx"
                 :on-change="onFile" style="width:100%">
        <el-icon style="font-size:40px;color:#38bdf8"><UploadFilled /></el-icon>
        <div class="up-tip">把 .xlsx 拖到这里，或<em>点击选择</em></div>
        <div class="dim" style="font-size:12px">
          列顺序见模板：设备编码 / 设备名称 / 端点编码 / 从站号 / 点位编码 / 名称 / 地址 / 类型 / 字节序 / ...
        </div>
      </el-upload>
      <div v-if="fileName" class="file-line">
        <el-icon><Document /></el-icon>
        <span>{{ fileName }}</span>
        <span class="spacer" />
        <el-button size="small" type="primary" :loading="validating" @click="doValidate">开始校验</el-button>
      </div>
    </div>

    <div v-if="result" class="card">
      <div class="card-title">
        <span>2. 校验结果</span>
        <div class="toolbar" style="margin:0">
          <span class="dim" style="font-size:12px">
            共 {{ result.total }} 行，通过 {{ result.valid }} 行，失败 {{ result.invalid }} 行
          </span>
          <el-button size="small" type="primary" :disabled="result.valid === 0" :loading="committing" @click="doCommit">
            提交入库（{{ result.valid }} 行）
          </el-button>
        </div>
      </div>
      <el-table :data="result.rows" size="small" border max-height="440">
        <el-table-column prop="rowNo" label="行号" width="80" />
        <el-table-column label="结果" width="90">
          <template #default="{ row }">
            <el-tag size="small" :type="row.valid ? 'success' : 'danger'">{{ row.valid ? '通过' : '失败' }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="deviceCode" label="设备" width="140" />
        <el-table-column prop="tagCode" label="点位" width="160" />
        <el-table-column prop="message" label="说明" />
      </el-table>
    </div>

    <div class="card">
      <div class="card-title"><span>历史任务</span></div>
      <el-table :data="jobs" size="small" border>
        <el-table-column prop="id" label="任务" width="80" />
        <el-table-column prop="fileName" label="文件" />
        <el-table-column prop="status" label="状态" width="120" />
        <el-table-column prop="total" label="总行" width="80" />
        <el-table-column prop="valid" label="通过" width="80" />
        <el-table-column prop="invalid" label="失败" width="80" />
        <el-table-column prop="createdBy" label="操作人" width="120" />
        <el-table-column label="时间" width="180">
          <template #default="{ row }">{{ new Date(row.createdAt).toLocaleString('zh-CN', { hour12: false }) }}</template>
        </el-table-column>
      </el-table>
    </div>
  </div>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { importApi } from '@/api'
import type { ImportResult } from '@/api/types'

const fileName = ref('')
const file = ref<File | null>(null)
const validating = ref(false)
const committing = ref(false)
const result = ref<ImportResult | null>(null)
const jobs = ref<any[]>([])

function onFile(f: any) {
  file.value = f.raw as File
  fileName.value = f.name
  result.value = null
}

async function doValidate() {
  if (!file.value) return
  validating.value = true
  try {
    result.value = await importApi.validate(file.value)
    ElMessage.success(`校验完成：通过 ${result.value.valid} 行`)
    loadJobs()
  } catch (e: any) {
    ElMessage.error(e?.message || '校验失败')
  } finally { validating.value = false }
}

async function doCommit() {
  if (!result.value) return
  committing.value = true
  try {
    const r = await importApi.commit(result.value.jobId)
    result.value = r
    ElMessage.success(`入库完成：新增 ${r.valid} 行，失败 ${r.invalid} 行`)
    loadJobs()
  } catch (e: any) {
    ElMessage.error(e?.message || '提交失败')
  } finally { committing.value = false }
}

async function loadJobs() {
  try { jobs.value = await importApi.jobs() } catch { /* 忽略 */ }
}

function download(url: string) {
  window.open(url, '_blank')
}

onMounted(loadJobs)
</script>

<style scoped>
.up-tip { font-size: 14px; margin: 6px 0; }
.up-tip em { color: var(--accent); font-style: normal; }
.file-line { display: flex; align-items: center; gap: 8px; margin-top: 10px; font-size: 13px; }
</style>
