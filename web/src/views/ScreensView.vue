<template>
  <div class="page">
    <div class="page-title">
      <span>组态画面 <span class="sub">自研轻量组态：JSON schema + SVG 图元 + 数据绑定动画</span></span>
      <div class="toolbar" style="margin:0">
        <el-button size="small" @click="load"><el-icon><Refresh /></el-icon>刷新</el-button>
        <el-button v-if="auth.canConfig" size="small" type="primary" @click="createVisible = true">
          <el-icon><Plus /></el-icon>新建画面
        </el-button>
      </div>
    </div>

    <div class="cards">
      <div v-for="s in list" :key="s.id" class="scard">
        <div class="scard-head">
          <div>
            <div class="sname">{{ s.name }}</div>
            <div class="scode">{{ s.code }} · {{ typeText(s.type) }}</div>
          </div>
          <el-tag size="small" :type="s.status === 'Published' ? 'success' : 'info'">
            {{ s.status === 'Published' ? `v${s.publishedVersionNo}` : '草稿' }}
          </el-tag>
        </div>
        <div class="scard-meta">
          <span>{{ s.width }}×{{ s.height }}</span>
          <span class="dim">更新 {{ fmt(s.updatedAt) }}</span>
          <span class="dim">{{ s.updatedBy || '-' }}</span>
        </div>
        <div class="scard-ops">
          <el-button size="small" @click="$router.push(`/screen/${s.id}`)">运行</el-button>
          <el-button v-if="s.canEdit" size="small" type="primary" plain @click="$router.push(`/screens/${s.id}/edit`)">编辑</el-button>
          <el-button v-if="auth.canAdmin" size="small" text type="danger" @click="remove(s)">删除</el-button>
        </div>
      </div>
      <div v-if="!list.length" class="card"><el-empty description="暂无画面" /></div>
    </div>

    <el-dialog v-model="createVisible" title="新建画面" width="480px">
      <el-form label-width="90px">
        <el-form-item label="画面编码"><el-input v-model="form.code" placeholder="例如 device-press-1" /></el-form-item>
        <el-form-item label="画面名称"><el-input v-model="form.name" placeholder="例如 压机 1 号 · 设备详情" /></el-form-item>
        <el-form-item label="画面类型">
          <el-select v-model="form.type" style="width:100%">
            <el-option label="总览 overview" value="overview" />
            <el-option label="车间 workshop" value="workshop" />
            <el-option label="设备 device" value="device" />
            <el-option label="参数 params" value="params" />
            <el-option label="趋势 trend" value="trend" />
            <el-option label="报警 alarm" value="alarm" />
            <el-option label="诊断 diag" value="diag" />
            <el-option label="自定义 custom" value="custom" />
          </el-select>
        </el-form-item>
        <el-form-item label="尺寸">
          <el-input-number v-model="form.width" :min="320" :step="160" controls-position="right" />
          <span style="margin:0 6px">×</span>
          <el-input-number v-model="form.height" :min="240" :step="120" controls-position="right" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="createVisible = false">取消</el-button>
        <el-button type="primary" :loading="creating" @click="create">创建</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { screenApi } from '@/api'
import type { ScreenDto } from '@/api/types'
import { useAuthStore } from '@/stores/auth'

const auth = useAuthStore()
const router = useRouter()
const list = ref<ScreenDto[]>([])
const createVisible = ref(false)
const creating = ref(false)
const form = reactive({ code: '', name: '', type: 'custom', width: 1920, height: 1080 })

const typeMap: Record<string, string> = {
  overview: '总览', workshop: '车间', device: '设备', params: '参数',
  trend: '趋势', alarm: '报警', diag: '诊断', custom: '自定义'
}
function typeText(t: string) { return typeMap[t] ?? t }
function fmt(iso: string) { return new Date(iso).toLocaleString('zh-CN', { hour12: false }) }

async function load() {
  try { list.value = await screenApi.list() } catch { /* 已提示 */ }
}

async function create() {
  if (!form.code || !form.name) { ElMessage.warning('请填写编码与名称'); return }
  creating.value = true
  try {
    const r: any = await screenApi.create({ ...form })
    ElMessage.success('画面已创建')
    createVisible.value = false
    await load()
    const id = r?.data?.data?.id ?? r?.data?.id
    if (id) router.push(`/screens/${id}/edit`)
  } catch (e: any) {
    ElMessage.error(e?.message || '创建失败')
  } finally { creating.value = false }
}

async function remove(s: ScreenDto) {
  await ElMessageBox.confirm(`删除画面「${s.name}」？其版本记录也会一并删除。`, '确认删除', { type: 'warning' })
  await screenApi.remove(s.id)
  ElMessage.success('已删除')
  load()
}

onMounted(load)
</script>

<style scoped>
.cards { display: grid; grid-template-columns: repeat(auto-fill, minmax(300px, 1fr)); gap: 14px; }
.scard { background: var(--bg-panel); border: 1px solid var(--line); border-radius: var(--radius); padding: 14px 16px; }
.scard-head { display: flex; justify-content: space-between; align-items: flex-start; gap: 8px; }
.sname { font-size: 15px; font-weight: 600; }
.scode { font-size: 12px; color: var(--text-mute); margin-top: 2px; }
.scard-meta { display: flex; gap: 12px; font-size: 12px; color: var(--text-dim); margin: 12px 0; }
.scard-ops { display: flex; gap: 8px; }
</style>
