import http from './http'
import type {
  AlarmEventDto, AlarmRuleDto, AuditDto, DebugReadResult, DeviceDto, DiagEndpointDto,
  EndpointDto, HistorySeries, ImportResult, LoginResult, MetricDto, PagedResult,
  RealtimeItem, RealtimeSnapshot, ScanGroupDto, ScreenDto, TagDto, WriteExecuteResult,
  WritePrepareResult, WriteItemRequest
} from './types'

export const authApi = {
  login: (username: string, password: string) =>
    http.post('/auth/login', { username, password }).then(r => r.data),
  me: () => http.get('/auth/me').then(r => r.data.data),
  wsTicket: () => http.post('/auth/ws-ticket').then(r => r.data.data as { ticket: string; expiresIn: number }),
  changePassword: (oldPassword: string, newPassword: string) =>
    http.post('/auth/change-password', { oldPassword, newPassword }),
  users: () => http.get('/auth/users').then(r => r.data.data),
  createUser: (b: any) => http.post('/auth/users', b),
  updateUser: (id: number, b: any) => http.put(`/auth/users/${id}`, b),
  deleteUser: (id: number) => http.delete(`/auth/users/${id}`)
}

export const configApi = {
  endpoints: () => http.get<EndpointDto[]>('/endpoints').then(r => r.data),
  createEndpoint: (b: any) => http.post('/endpoints', b),
  updateEndpoint: (id: number, b: any) => http.put(`/endpoints/${id}`, b),
  deleteEndpoint: (id: number) => http.delete(`/endpoints/${id}`),
  testEndpoint: (id: number) => http.post(`/endpoints/${id}/test`).then(r => r.data),

  devices: (endpointId?: number) =>
    http.get<DeviceDto[]>('/devices', { params: { endpointId } }).then(r => r.data),
  createDevice: (b: any) => http.post('/devices', b),
  updateDevice: (id: number, b: any) => http.put(`/devices/${id}`, b),
  pauseDevice: (id: number, paused: boolean) => http.post(`/devices/${id}/pause`, { paused }),
  deleteDevice: (id: number) => http.delete(`/devices/${id}`),

  scanGroups: () => http.get<ScanGroupDto[]>('/scan-groups').then(r => r.data),
  createScanGroup: (b: any) => http.post('/scan-groups', b),
  updateScanGroup: (id: number, b: any) => http.put(`/scan-groups/${id}`, b),

  tags: (params: Record<string, any>) =>
    http.get<PagedResult<TagDto>>('/tags', { params }).then(r => r.data),
  tag: (id: number) => http.get<TagDto>(`/tags/${id}`).then(r => r.data),
  validateTag: (b: any) => http.post('/tags/validate', b).then(r => r.data),
  createTag: (b: any) => http.post('/tags', b),
  updateTag: (id: number, b: any) => http.put(`/tags/${id}`, b),
  deleteTag: (id: number) => http.delete(`/tags/${id}`)
}

export const realtimeApi = {
  snapshot: (params: { tagIds?: string; deviceId?: number; endpointId?: number }) =>
    http.get<RealtimeSnapshot>('/realtime/snapshot', { params }).then(r => r.data),
  item: (tagId: number) => http.get<RealtimeItem>(`/realtime/item/${tagId}`).then(r => r.data),
  version: () => http.get('/realtime/version').then(r => r.data),
  health: () => http.get('/realtime/health').then(r => r.data)
}

export const historyApi = {
  query: (tagIds: number[], from: string, to: string, maxPoints = 2000) =>
    http.get<HistorySeries[]>('/history/query', {
      params: { tagIds: tagIds.join(','), from, to, maxPoints }
    }).then(r => r.data),
  exportUrl: (tagIds: number[], from: string, to: string) =>
    `/api/history/export?tagIds=${tagIds.join(',')}&from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`,
  stats: (tagId?: number) => http.get('/history/stats', { params: { tagId } }).then(r => r.data)
}

export const writeApi = {
  prepare: (items: WriteItemRequest[], reason?: string) =>
    http.post<WritePrepareResult>('/writes/prepare', { items, reason }).then(r => r.data),
  execute: (confirmToken: string) =>
    http.post<WriteExecuteResult>('/writes/execute', { confirmToken }).then(r => r.data),
  batches: (page = 1, size = 20) => http.get('/writes/batches', { params: { page, size } }).then(r => r.data)
}

export const alarmApi = {
  list: (params: Record<string, any>) =>
    http.get<{ total: number; items: AlarmEventDto[]; activeCount: number; counts: Record<string, number> }>(
      '/alarms', { params }).then(r => r.data),
  ack: (id: number, note?: string) => http.post(`/alarms/${id}/ack`, { note }),
  ackBatch: (ids: number[], note?: string) => http.post('/alarms/ack-batch', { ids, note }),
  rules: () => http.get<AlarmRuleDto[]>('/alarms/rules').then(r => r.data),
  createRule: (b: any) => http.post('/alarms/rules', b),
  updateRule: (id: number, b: any) => http.put(`/alarms/rules/${id}`, b),
  deleteRule: (id: number) => http.delete(`/alarms/rules/${id}`)
}

export const screenApi = {
  list: () => http.get<ScreenDto[]>('/screens').then(r => r.data),
  get: (id: number, mode?: string) => http.get(`/screens/${id}`, { params: { mode } }).then(r => r.data),
  create: (b: any) => http.post('/screens', b),
  update: (id: number, b: any) => http.put(`/screens/${id}`, b),
  saveDraft: (id: number, json: string, note?: string) => http.put(`/screens/${id}/draft`, { json, note }),
  publish: (id: number, json?: string, note?: string) => http.post(`/screens/${id}/publish`, { json, note }),
  versions: (id: number) => http.get(`/screens/${id}/versions`).then(r => r.data),
  version: (id: number, versionId: number) => http.get(`/screens/${id}/versions/${versionId}`).then(r => r.data),
  rollback: (id: number, versionId: number) => http.post(`/screens/${id}/rollback/${versionId}`),
  remove: (id: number) => http.delete(`/screens/${id}`),
  roles: (id: number) => http.get(`/screens/${id}/roles`).then(r => r.data),
  setRoles: (id: number, roles: any[]) => http.put(`/screens/${id}/roles`, roles)
}

export const diagApi = {
  endpoints: () => http.get<DiagEndpointDto[]>('/diagnostics/endpoints').then(r => r.data),
  debugRead: (tagId: number) => http.post<DebugReadResult>(`/diagnostics/debug-read/${tagId}`).then(r => r.data),
  metrics: () => http.get<MetricDto[]>('/diagnostics/metrics').then(r => r.data),
  queues: () => http.get('/diagnostics/queues').then(r => r.data),
  audit: (params: Record<string, any>) => http.get<PagedResult<AuditDto>>('/diagnostics/audit', { params }).then(r => r.data),
  maintenance: () => http.post('/diagnostics/maintenance').then(r => r.data)
}

export const importApi = {
  templateUrl: '/api/import/template',
  validate: (file: File) => {
    const fd = new FormData()
    fd.append('file', file)
    return http.post<ImportResult>('/import/validate', fd, {
      headers: { 'Content-Type': 'multipart/form-data' }
    }).then(r => r.data)
  },
  commit: (jobId: number) => http.post<ImportResult>(`/import/commit/${jobId}`).then(r => r.data),
  jobs: () => http.get('/import/jobs').then(r => r.data),
  exportUrl: (deviceId?: number) => `/api/import/export${deviceId ? '?deviceId=' + deviceId : ''}`
}

export type { LoginResult }
