<template>
  <div class="shell">
    <aside class="side" :class="{ collapsed }">
      <div class="brand">
        <div class="logo">S</div>
        <div v-if="!collapsed" class="brand-text">
          <div class="t">SFMS</div>
          <div class="s">厂区设备检测系统</div>
        </div>
      </div>

      <nav class="menu">
        <template v-for="group in menus" :key="group.title">
          <div v-if="!collapsed" class="menu-group">{{ group.title }}</div>
          <router-link
            v-for="item in group.items"
            :key="item.path"
            :to="item.path"
            class="menu-item"
            :class="{ active: isActive(item.path) }"
            :title="item.label"
          >
            <el-icon class="ico"><component :is="item.icon" /></el-icon>
            <span v-if="!collapsed">{{ item.label }}</span>
            <span v-if="item.badge && !collapsed && alarmCount > 0" class="badge">{{ alarmCount }}</span>
          </router-link>
        </template>
      </nav>

      <div class="side-foot">
        <el-button text size="small" @click="collapsed = !collapsed">
          <el-icon><component :is="collapsed ? 'Expand' : 'Fold'" /></el-icon>
        </el-button>
      </div>
    </aside>

    <section class="main">
      <header class="topbar">
        <div class="crumbs">
          <span class="title">{{ currentTitle }}</span>
          <span class="sub">{{ subtitle }}</span>
        </div>
        <div class="spacer" />
        <div class="ws-state" :class="rt.status" :title="wsTip">
          <span class="dot" />
          <span class="txt">{{ wsText }}</span>
        </div>
        <el-tooltip content="报警中心" placement="bottom">
          <router-link to="/alarms" class="alarm-entry">
            <el-icon><Bell /></el-icon>
            <span v-if="alarmCount > 0" class="alarm-count">{{ alarmCount }}</span>
          </router-link>
        </el-tooltip>
        <el-dropdown trigger="click" @command="onUserCommand">
          <div class="user">
            <div class="avatar">{{ (auth.displayName || 'U').slice(0, 1) }}</div>
            <div class="user-meta">
              <div class="name">{{ auth.displayName }}</div>
              <div class="role">{{ roleText }}</div>
            </div>
            <el-icon><ArrowDown /></el-icon>
          </div>
          <template #dropdown>
            <el-dropdown-menu>
              <el-dropdown-item command="password">修改口令</el-dropdown-item>
              <el-dropdown-item command="docs">接口文档（Swagger）</el-dropdown-item>
              <el-dropdown-item command="logout" divided>退出登录</el-dropdown-item>
            </el-dropdown-menu>
          </template>
        </el-dropdown>
      </header>

      <main class="content" :class="{ flush: isFull }">
        <router-view v-slot="{ Component }">
          <component :is="Component" />
        </router-view>
      </main>
    </section>

    <el-dialog v-model="pwdVisible" title="修改口令" width="420px">
      <el-form label-width="90px">
        <el-form-item label="原口令"><el-input v-model="pwd.old" type="password" show-password /></el-form-item>
        <el-form-item label="新口令"><el-input v-model="pwd.next" type="password" show-password /></el-form-item>
        <el-form-item label="确认新口令"><el-input v-model="pwd.confirm" type="password" show-password /></el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="pwdVisible = false">取消</el-button>
        <el-button type="primary" :loading="pwdLoading" @click="submitPassword">保存</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { authApi, alarmApi } from '@/api'
import { useAuthStore } from '@/stores/auth'
import { useRealtimeStore } from '@/stores/realtime'

const auth = useAuthStore()
const rt = useRealtimeStore()
const route = useRoute()
const router = useRouter()

const collapsed = ref(false)
const alarmCount = ref(0)
const pwdVisible = ref(false)
const pwdLoading = ref(false)
const pwd = reactive({ old: '', next: '', confirm: '' })

const menus = computed(() => {
  const groups = [
    {
      title: '监控中心',
      items: [
        { path: '/overview', label: '厂区总览', icon: 'Odometer' },
        { path: '/screens', label: '组态画面', icon: 'Monitor' },
        { path: '/trends', label: '历史趋势', icon: 'TrendCharts' },
        { path: '/alarms', label: '报警中心', icon: 'Bell', badge: true }
      ]
    },
    {
      title: '诊断维护',
      items: [{ path: '/diagnostics', label: '通信诊断', icon: 'Cpu' }]
    }
  ]
  if (auth.canConfig) {
    groups.push({
      title: '组态配置',
      items: [
        { path: '/config/endpoints', label: '端点管理', icon: 'Connection' },
        { path: '/config/devices', label: '设备管理', icon: 'Box' },
        { path: '/config/tags', label: '点位管理', icon: 'Grid' },
        { path: '/config/groups', label: '采集分组', icon: 'Timer' },
        { path: '/config/rules', label: '报警规则', icon: 'WarnTriangleFilled' },
        { path: '/import', label: '批量导入', icon: 'Upload' }
      ]
    })
  }
  if (auth.canAdmin) {
    groups.push({
      title: '系统管理',
      items: [
        { path: '/audit', label: '审计日志', icon: 'Document' },
        { path: '/users', label: '用户管理', icon: 'User' }
      ]
    })
  } else if (auth.canConfig) {
    groups.push({ title: '系统管理', items: [{ path: '/audit', label: '审计日志', icon: 'Document' }] })
  }
  return groups
})

const currentTitle = computed(() => (route.meta.title as string) || 'SFMS')
const isFull = computed(() => route.meta.full === true)
const subtitle = computed(() => {
  if (route.name === 'screen') return '运行态 · 实时数据驱动'
  if (route.name === 'screen-edit') return '设计态 · 8px 吸附 / 撤销重做 / 发布回滚'
  return ''
})

const wsText = computed(() => ({
  open: '实时在线', connecting: '连接中', closed: '已断开', idle: '未连接', error: '连接异常'
} as Record<string, string>)[rt.status] ?? rt.status)
const wsTip = computed(() => `WebSocket ${rt.status}｜累计帧 ${rt.frames}｜重同步 ${rt.resyncs}`)

const roleText = computed(() => ({
  Admin: '系统管理员', Engineer: '工程师', Operator: '操作员', Observer: '观察员'
} as Record<string, string>)[auth.role] ?? auth.role)

function isActive(path: string) {
  if (path === '/screens') return route.path.startsWith('/screens')
  return route.path === path || route.path.startsWith(path + '/')
}

async function refreshAlarm() {
  try {
    const r = await alarmApi.list({ state: 'active', size: 1 })
    alarmCount.value = r.activeCount ?? 0
  } catch { /* 忽略 */ }
}

let timer = 0
onMounted(() => {
  refreshAlarm()
  timer = window.setInterval(refreshAlarm, 15000)
})
onUnmounted(() => { if (timer) clearInterval(timer) })

async function onUserCommand(cmd: string) {
  if (cmd === 'logout') {
    await ElMessageBox.confirm('确认退出登录？', '提示', { type: 'warning' }).catch(() => null)
      .then((r) => {
        if (r === 'confirm') {
          rt.disconnect()
          auth.logout()
          router.push('/login')
        }
      })
    return
  }
  if (cmd === 'password') { pwd.old = pwd.next = pwd.confirm = ''; pwdVisible.value = true; return }
  if (cmd === 'docs') { window.open('/swagger', '_blank') }
}

async function submitPassword() {
  if (pwd.next.length < 6) { ElMessage.warning('新口令至少 6 位'); return }
  if (pwd.next !== pwd.confirm) { ElMessage.warning('两次输入不一致'); return }
  pwdLoading.value = true
  try {
    await authApi.changePassword(pwd.old, pwd.next)
    ElMessage.success('口令已更新')
    pwdVisible.value = false
  } finally { pwdLoading.value = false }
}
</script>

<style scoped>
.shell { display: flex; height: 100vh; overflow: hidden; }

.side {
  width: 216px; flex: 0 0 216px; background: #0d1826; border-right: 1px solid var(--line);
  display: flex; flex-direction: column; transition: width .18s ease, flex-basis .18s ease;
}
.side.collapsed { width: 60px; flex-basis: 60px; }

.brand { display: flex; align-items: center; gap: 10px; padding: 16px 14px; border-bottom: 1px solid var(--line); }
.logo {
  width: 32px; height: 32px; border-radius: 8px; flex: 0 0 32px;
  background: linear-gradient(135deg, #38bdf8, #6366f1); color: #04121f;
  display: grid; place-items: center; font-weight: 800; font-size: 18px;
}
.brand-text .t { font-weight: 700; letter-spacing: 2px; }
.brand-text .s { font-size: 11px; color: var(--text-mute); }

.menu { flex: 1; overflow-y: auto; padding: 10px 8px; }
.menu-group { font-size: 11px; color: var(--text-mute); padding: 12px 10px 6px; letter-spacing: 1px; }
.menu-item {
  display: flex; align-items: center; gap: 10px; padding: 9px 10px; margin-bottom: 2px;
  border-radius: 8px; color: var(--text-dim); font-size: 13.5px; position: relative;
}
.menu-item:hover { background: #14273a; color: var(--text); }
.menu-item.active { background: rgba(56,189,248,.14); color: #7dd3fc; }
.menu-item.active::before {
  content: ''; position: absolute; left: 0; top: 8px; bottom: 8px; width: 3px;
  border-radius: 2px; background: var(--accent);
}
.ico { font-size: 16px; }
.badge {
  margin-left: auto; background: #dc2626; color: #fff; border-radius: 999px;
  font-size: 11px; padding: 0 6px; line-height: 16px;
}
.side-foot { padding: 8px; border-top: 1px solid var(--line); display: flex; justify-content: center; }

.main { flex: 1; display: flex; flex-direction: column; min-width: 0; }
.topbar {
  height: 56px; flex: 0 0 56px; display: flex; align-items: center; gap: 14px;
  padding: 0 18px; border-bottom: 1px solid var(--line); background: #0d1826;
}
.crumbs .title { font-size: 16px; font-weight: 600; }
.crumbs .sub { font-size: 12px; color: var(--text-mute); margin-left: 10px; }

.ws-state { display: flex; align-items: center; gap: 6px; font-size: 12px; color: var(--text-dim); }
.ws-state .dot { width: 8px; height: 8px; border-radius: 50%; background: #64748b; }
.ws-state.open .dot { background: var(--good); box-shadow: 0 0 8px rgba(34,197,94,.8); }
.ws-state.connecting .dot { background: var(--warn); animation: pulse 1s infinite; }
.ws-state.error .dot, .ws-state.closed .dot { background: var(--bad); }
@keyframes pulse { 50% { opacity: .25; } }

.alarm-entry { position: relative; color: var(--text-dim); display: grid; place-items: center; padding: 6px; border-radius: 8px; }
.alarm-entry:hover { background: #14273a; color: var(--text); }
.alarm-count {
  position: absolute; top: -2px; right: -4px; background: #dc2626; color: #fff;
  font-size: 10px; border-radius: 999px; padding: 0 5px; line-height: 15px;
}

.user { display: flex; align-items: center; gap: 8px; cursor: pointer; padding: 4px 6px; border-radius: 8px; }
.user:hover { background: #14273a; }
.avatar {
  width: 30px; height: 30px; border-radius: 50%; display: grid; place-items: center;
  background: #1d4ed8; color: #e0f2fe; font-weight: 700;
}
.user-meta .name { font-size: 13px; line-height: 1.2; }
.user-meta .role { font-size: 11px; color: var(--text-mute); }

.content { flex: 1; overflow: auto; }
.content.flush { overflow: hidden; }
</style>
