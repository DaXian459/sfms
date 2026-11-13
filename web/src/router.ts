import { createRouter, createWebHashHistory, type RouteRecordRaw } from 'vue-router'
import { useAuthStore } from '@/stores/auth'

const routes: RouteRecordRaw[] = [
  { path: '/login', name: 'login', component: () => import('@/views/LoginView.vue'), meta: { public: true } },
  {
    path: '/',
    component: () => import('@/layouts/MainLayout.vue'),
    children: [
      { path: '', redirect: '/overview' },
      { path: 'overview', name: 'overview', component: () => import('@/views/OverviewView.vue'), meta: { title: '厂区总览' } },
      { path: 'screen/:id', name: 'screen', component: () => import('@/views/ScreenView.vue'), meta: { title: '组态画面' } },
      { path: 'screens', name: 'screens', component: () => import('@/views/ScreensView.vue'), meta: { title: '画面管理' } },
      { path: 'screens/:id/edit', name: 'screen-edit', component: () => import('@/views/ScreenEditorView.vue'), meta: { title: '组态编辑器', full: true } },
      { path: 'trends', name: 'trends', component: () => import('@/views/TrendsView.vue'), meta: { title: '历史趋势' } },
      { path: 'alarms', name: 'alarms', component: () => import('@/views/AlarmsView.vue'), meta: { title: '报警中心' } },
      { path: 'diagnostics', name: 'diagnostics', component: () => import('@/views/DiagnosticsView.vue'), meta: { title: '通信诊断' } },
      { path: 'config/endpoints', name: 'endpoints', component: () => import('@/views/config/EndpointsView.vue'), meta: { title: '端点管理', level: 3 } },
      { path: 'config/devices', name: 'devices', component: () => import('@/views/config/DevicesView.vue'), meta: { title: '设备管理', level: 3 } },
      { path: 'config/tags', name: 'tags', component: () => import('@/views/config/TagsView.vue'), meta: { title: '点位管理', level: 3 } },
      { path: 'config/groups', name: 'groups', component: () => import('@/views/config/ScanGroupsView.vue'), meta: { title: '采集分组', level: 3 } },
      { path: 'config/rules', name: 'rules', component: () => import('@/views/config/AlarmRulesView.vue'), meta: { title: '报警规则', level: 3 } },
      { path: 'import', name: 'import', component: () => import('@/views/ImportView.vue'), meta: { title: '批量导入', level: 3 } },
      { path: 'audit', name: 'audit', component: () => import('@/views/AuditView.vue'), meta: { title: '审计日志', level: 3 } },
      { path: 'users', name: 'users', component: () => import('@/views/UsersView.vue'), meta: { title: '用户管理', level: 4 } }
    ]
  },
  { path: '/:pathMatch(.*)*', redirect: '/overview' }
]

const router = createRouter({ history: createWebHashHistory(), routes })

router.beforeEach((to) => {
  const auth = useAuthStore()
  if (to.meta.public) return true
  if (!auth.isLoggedIn) return { name: 'login', query: { redirect: to.fullPath } }
  const need = Number(to.meta.level ?? 0)
  if (need && auth.level < need) return { name: 'overview' }
  return true
})

router.afterEach((to) => {
  const t = to.meta.title as string | undefined
  document.title = t ? `${t} · SFMS` : 'SFMS · 厂区设备检测系统'
})

export default router
