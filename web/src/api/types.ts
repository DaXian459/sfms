/** 与后端 Shared/Contracts.cs 一一对应的前端类型。 */

export interface ApiResult<T> { ok: boolean; data: T; error?: string; traceId?: string }
export interface PagedResult<T> { items: T[]; total: number; page: number; size: number }

export interface EndpointDto {
  id: number; code: string; name: string; protocol: string; ip: string; port: number
  scanEnabled: boolean; status: string; lastOkAt?: string; remark?: string
  connParams?: string; deviceCount: number; tagCount: number; enabled: boolean
}

export interface DeviceDto {
  id: number; endpointId: number; code: string; name: string; deviceKey: string
  model?: string; workshop?: string; area?: string; enabled: boolean
  tagCount: number; onlineTags: number; badTags: number; paused: boolean
}

export interface ScanGroupDto { id: number; name: string; periodMs: number; priority: number; mergeGap: number; tagCount: number }

export interface TagDto {
  id: number; deviceId: number; deviceCode: string; code: string; name: string
  addressExpr: string; area: string; areaNo: number; byteOffset: number; bitOffset: number
  dataType: string; byteOrder: string; strLen: number; writable: boolean
  minValue?: number; maxValue?: number; scale: number; offset: number; unit?: string
  scanGroupId: number; scanGroupName: string; deadband: number; enabled: boolean
  latestValue?: number; latestText?: string; quality: number; latestAt?: string
}

export interface RealtimeItem { tagId: number; value?: number; text?: string; quality: number; ts: string }
export interface RealtimeSnapshot { subId: string; serverTime: string; items: RealtimeItem[] }

export interface HistoryPoint { tagId: number; ts: string; value?: number; text?: string; quality: number }
export interface HistorySeries { tagId: number; code?: string; name?: string; unit?: string; points: HistoryPoint[]; resolution: string }

export interface WriteItemRequest { tagId: number; numericValue?: number; textValue?: string }
export interface WritePreview {
  tagId: number; code: string; name: string; oldValue?: number; oldText?: string
  newValue?: number; newText?: string; quality: number; writable: boolean; error?: string
}
export interface WritePrepareResult { confirmToken: string; expiresAt: string; items: WritePreview[]; blockers: string[] }
export interface WriteItemResult {
  tagId: number; code: string; ok: boolean; error?: string
  oldValue?: number; newValue?: number; readBack?: number
}
export interface WriteExecuteResult { batchId: string; status: string; message: string; items: WriteItemResult[] }

export interface AlarmEventDto {
  id: number; ruleId?: number; tagId?: number; tagCode?: string; tagName?: string
  deviceId?: number; deviceName?: string; kind: string; severity: string; state: string
  startedAt: string; endedAt?: string; triggerValue?: number
  ackBy?: string; ackAt?: string; ackNote?: string; message: string
}

export interface AlarmRuleDto {
  id: number; tagId?: number; endpointId?: number; kind: string; threshold: number
  hysteresis: number; onDelayMs: number; severity: string; enabled: boolean
  tagCode?: string; tagName?: string
}

export interface ScreenDto {
  id: number; code: string; name: string; type: string; width: number; height: number
  status: string; publishedVersionId?: number; publishedVersionNo: number
  updatedAt: string; updatedBy?: string; canView: boolean; canEdit: boolean
}

export interface DiagEndpointDto {
  endpointId: number; code: string; name: string; protocol: string; ip: string; port: number
  status: string; lastOkAt?: string; requests: number; success: number
  timeouts: number; errors: number; avgMs: number; p95Ms: number
  consecutiveFailures: number; lastError?: string; onlineTags: number; badTags: number; tagCount: number
}

export interface DebugReadResult {
  tagId: number; code: string; addressExpr: string; dataType: string; byteOrder: string
  rawHex: string; value?: string; quality: number; error?: string
  interpretations: Record<string, string>
}

export interface MetricDto { name: string; value: number; unit?: string; at: string }

export interface AuditDto {
  id: number; ts: string; username?: string; action: string; targetType?: string
  targetId?: string; before?: string; after?: string; result: string; reason?: string; ip?: string
}

export interface LoginResult {
  token: string; expiresAt: string; wsTicket: string; userId: number
  username: string; displayName: string; role: string; roleLevel: number
}

export interface ImportRowResult {
  rowNo: number; valid: boolean; message: string; deviceCode: string; tagCode: string
}
export interface ImportResult { jobId: number; total: number; valid: number; invalid: number; rows: ImportRowResult[] }
