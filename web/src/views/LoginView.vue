<template>
  <div class="login-wrap">
    <div class="bg-grid" />
    <div class="login-card">
      <div class="head">
        <div class="logo">S</div>
        <div>
          <div class="t">SFMS · 厂区设备检测系统</div>
          <div class="s">Shop-floor Facility Monitoring System</div>
        </div>
      </div>

      <el-form :model="form" @submit.prevent="submit">
        <el-form-item>
          <el-input v-model="form.username" size="large" placeholder="用户名" :prefix-icon="User" autofocus />
        </el-form-item>
        <el-form-item>
          <el-input v-model="form.password" size="large" type="password" show-password
                    placeholder="口令" :prefix-icon="Lock" @keyup.enter="submit" />
        </el-form-item>
        <el-button type="primary" size="large" class="submit" :loading="loading" @click="submit">
          登 录
        </el-button>
      </el-form>

      <div class="hint">
        <div class="hint-title">演示账号（首次启动自动创建）</div>
        <div class="accounts">
          <span v-for="a in accounts" :key="a.u" class="acc" @click="fill(a.u, a.p)">
            {{ a.role }}<em>{{ a.u }} / {{ a.p }}</em>
          </span>
        </div>
      </div>
    </div>
    <div class="foot">.NET 8 + Vue 3 + PostgreSQL 16 · 三层端点-设备-点位模型 · 自研轻量组态</div>
  </div>
</template>

<script setup lang="ts">
import { reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { User, Lock } from '@element-plus/icons-vue'
import { useAuthStore } from '@/stores/auth'

const auth = useAuthStore()
const router = useRouter()
const route = useRoute()

const form = reactive({ username: '', password: '' })
const loading = ref(false)

const accounts = [
  { role: '管理员', u: 'admin', p: 'admin123' },
  { role: '工程师', u: 'engineer', p: 'eng123' },
  { role: '操作员', u: 'operator', p: 'op123' },
  { role: '观察员', u: 'observer', p: 'ob123' }
]

function fill(u: string, p: string) {
  form.username = u
  form.password = p
}

async function submit() {
  if (!form.username || !form.password) { ElMessage.warning('请输入用户名与口令'); return }
  loading.value = true
  try {
    const res = await auth.login(form.username.trim(), form.password)
    ElMessage.success(`欢迎，${res.displayName}`)
    const redirect = (route.query.redirect as string) || '/overview'
    router.replace(redirect)
  } catch (e: any) {
    ElMessage.error(e?.message || '登录失败')
  } finally {
    loading.value = false
  }
}
</script>

<style scoped>
.login-wrap {
  height: 100vh; display: grid; place-items: center; position: relative; overflow: hidden;
  background: radial-gradient(1200px 600px at 50% 0%, #12283f 0%, #0b1220 60%);
}
.bg-grid {
  position: absolute; inset: 0;
  background-image: linear-gradient(rgba(56,189,248,.06) 1px, transparent 1px),
                    linear-gradient(90deg, rgba(56,189,248,.06) 1px, transparent 1px);
  background-size: 44px 44px;
}
.login-card {
  position: relative; width: 380px; padding: 26px 26px 18px; border-radius: 14px;
  background: rgba(16,29,46,.92); border: 1px solid var(--line); box-shadow: 0 24px 60px rgba(0,0,0,.45);
}
.head { display: flex; align-items: center; gap: 12px; margin-bottom: 22px; }
.logo {
  width: 40px; height: 40px; border-radius: 10px; display: grid; place-items: center;
  background: linear-gradient(135deg, #38bdf8, #6366f1); color: #04121f; font-weight: 800; font-size: 22px;
}
.head .t { font-size: 15px; font-weight: 700; }
.head .s { font-size: 11px; color: var(--text-mute); letter-spacing: .5px; }
.submit { width: 100%; margin-top: 4px; }
.hint { margin-top: 18px; border-top: 1px dashed var(--line); padding-top: 12px; }
.hint-title { font-size: 12px; color: var(--text-mute); margin-bottom: 8px; }
.accounts { display: grid; grid-template-columns: 1fr 1fr; gap: 6px; }
.acc {
  font-size: 12px; color: var(--text-dim); background: #14273a; border-radius: 6px;
  padding: 5px 8px; cursor: pointer; display: flex; flex-direction: column; line-height: 1.5;
}
.acc:hover { background: #1b3348; color: var(--text); }
.acc em { font-style: normal; color: var(--text-mute); font-size: 11px; }
.foot { position: absolute; bottom: 18px; font-size: 12px; color: var(--text-mute); }
</style>
