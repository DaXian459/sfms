<template>
  <div class="page">
    <div class="page-title">
      <span>用户管理 <span class="sub">四级角色：Admin ≥ Engineer ≥ Operator ≥ Observer</span></span>
      <div class="toolbar" style="margin:0">
        <el-button size="small" type="primary" @click="openEdit()"><el-icon><Plus /></el-icon>新建用户</el-button>
      </div>
    </div>

    <div class="card">
      <el-table :data="rows" size="small" border>
        <el-table-column prop="id" label="ID" width="70" />
        <el-table-column prop="username" label="用户名" width="150" />
        <el-table-column prop="displayName" label="显示名" width="160" />
        <el-table-column label="角色" width="140">
          <template #default="{ row }">
            <el-tag size="small" :type="roleType(row.role)">{{ roleText(row.role) }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="状态" width="110">
          <template #default="{ row }">
            <span class="pill" :class="row.enabled ? 'tag-good' : 'tag-bad'">{{ row.enabled ? '启用' : '停用' }}</span>
          </template>
        </el-table-column>
        <el-table-column label="锁定至" width="180">
          <template #default="{ row }">{{ row.lockedUntil ? new Date(row.lockedUntil).toLocaleString('zh-CN') : '-' }}</template>
        </el-table-column>
        <el-table-column label="最后登录" width="180">
          <template #default="{ row }">{{ row.lastLoginAt ? new Date(row.lastLoginAt).toLocaleString('zh-CN') : '-' }}</template>
        </el-table-column>
        <el-table-column label="操作" width="160" fixed="right">
          <template #default="{ row }">
            <el-button size="small" text @click="openEdit(row)">编辑</el-button>
            <el-button size="small" text type="danger" :disabled="row.username === 'admin'" @click="remove(row)">删除</el-button>
          </template>
        </el-table-column>
      </el-table>
    </div>

    <el-dialog v-model="visible" :title="form.id ? '编辑用户' : '新建用户'" width="480px">
      <el-form label-width="100px">
        <el-form-item label="用户名">
          <el-input v-model="form.username" :disabled="!!form.id" />
        </el-form-item>
        <el-form-item label="显示名"><el-input v-model="form.displayName" /></el-form-item>
        <el-form-item label="角色">
          <el-select v-model="form.role" style="width:100%">
            <el-option label="系统管理员 Admin" value="Admin" />
            <el-option label="工程师 Engineer" value="Engineer" />
            <el-option label="操作员 Operator" value="Operator" />
            <el-option label="观察员 Observer" value="Observer" />
          </el-select>
        </el-form-item>
        <el-form-item :label="form.id ? '重置口令' : '口令'">
          <el-input v-model="form.password" type="password" show-password
                    :placeholder="form.id ? '留空表示不修改' : '至少 6 位'" />
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
import { authApi } from '@/api'

const rows = ref<any[]>([])
const visible = ref(false)
const saving = ref(false)
const form = reactive({ id: 0, username: '', displayName: '', role: 'Observer', password: '', enabled: true })

function roleText(r: string) {
  return ({ Admin: '管理员', Engineer: '工程师', Operator: '操作员', Observer: '观察员' } as Record<string, string>)[r] ?? r
}
function roleType(r: string) {
  return r === 'Admin' ? 'danger' : r === 'Engineer' ? 'warning' : r === 'Operator' ? 'success' : 'info'
}

async function load() { rows.value = await authApi.users() }

function openEdit(row?: any) {
  Object.assign(form, row
    ? { id: row.id, username: row.username, displayName: row.displayName, role: row.role, password: '', enabled: row.enabled }
    : { id: 0, username: '', displayName: '', role: 'Observer', password: '', enabled: true })
  visible.value = true
}

async function save() {
  if (!form.username) { ElMessage.warning('请填写用户名'); return }
  if (!form.id && form.password.length < 6) { ElMessage.warning('口令至少 6 位'); return }
  saving.value = true
  try {
    const body = { username: form.username, password: form.password, displayName: form.displayName, role: form.role, enabled: form.enabled }
    if (form.id) await authApi.updateUser(form.id, body)
    else await authApi.createUser(body)
    ElMessage.success('已保存')
    visible.value = false
    load()
  } catch (e: any) {
    ElMessage.error(e?.message || '保存失败')
  } finally { saving.value = false }
}

async function remove(row: any) {
  await ElMessageBox.confirm(`删除用户「${row.username}」？`, '确认删除', { type: 'warning' })
  await authApi.deleteUser(row.id)
  load()
}

onMounted(load)
</script>
