const apiKey = import.meta.env.VITE_API_KEY ?? (import.meta.env.DEV ? 'wechatbot-local-development-key-change-me' : '')

export type ApiContact = {
  id: string
  externalId: string
  displayName: string
  customerCode?: string
  systemRemark?: string
  currentWeChatRemark?: string
  manualRemarkProtected: boolean
  serviceExpiresAt?: string
  version: number
}

export type ApiGroup = {
  id: string
  externalId: string
  displayName: string
  businessName?: string
  systemRemark?: string
  currentWeChatRemark?: string
  manualRemarkProtected: boolean
  serviceExpiresAt?: string
  version: number
}

export type ApiMention = {
  id: string
  externalEventId: string
  groupId: string
  senderExternalId: string
  content: string
  capturedAt: string
  decision: 'accepted' | 'ignoredNotMentioned' | 'ignoredBotMessage' | 'activationRequired' | 'automationPaused'
}

export type ApiRemarkRule = {
  id: string
  name: string
  targetKind: 'contact' | 'group'
  template: string
  conflictPolicy: 'skip' | 'overwriteSystemGeneratedOnly'
  isEnabled: boolean
  maxLength: number
  version: number
}

export type ApiRemarkTask = {
  id: string
  ruleId: string
  targetKind: 'contact' | 'group'
  targetId: string
  generatedRemark: string
  status: 'pending' | 'completed' | 'failed' | 'conflict' | 'canceled'
  conflictReason?: string
  failureReason?: string
  createdAt: string
  completedAt?: string
  version: number
}

export type ApiEntitlement = {
  id: string
  targetKind: 'contact' | 'group'
  targetId: string
  packageCode: 'BASIC' | 'ADVANCED_GENERAL'
  duration: 'days30' | 'days60' | 'days90' | 'halfYear' | 'oneYear' | 'permanent'
  startsAt: string
  endsAt?: string
  state: 'active' | 'suspended' | 'revoked'
  effectiveStatus: 'active' | 'scheduled' | 'expired' | 'suspended' | 'revoked'
  source: string
  version: number
}

export type ApiBackup = {
  id: string
  createdAt: string
  fileName: string
  payloadSha256: string
  bytes: number
  status: 'created' | 'verified' | 'corrupt'
}

export type ApiBackupVerification = {
  backupId: string
  isValid: boolean
  expectedSha256: string
  actualSha256: string
  bytes: number
}

export type ApiAgent = {
  id: string
  agentId: string
  weChatInstanceId: string
  isEnabled: boolean
  configurationVersion: string
  registeredAt: string
  updatedAt: string
  version: number
  sentAt?: string
  receivedAt?: string
  runtimeState?: 'starting' | 'healthy' | 'pausedUnknownUi' | 'pausedControlPlane' | 'pausedOperator' | 'maintenance' | 'stopping'
  reasonCode?: string
  reason?: string
  changedAt?: string
  lastCommandCompletedAt?: string
  lastCommandCode?: string
  queueDepth?: number
  activeExecutions?: number
  dryRun?: boolean
  agentVersion?: string
  online: boolean
}

export type ApiSystemState = {
  tenantId: string
  name: string
  automationPaused: boolean
  updatedAt: string
  version: number
}

export type ApiAudit = {
  id: string
  createdAt: string
  actor: string
  action: string
  resourceType: string
  resourceId: string
  success: boolean
  correlationId: string
}

export type ConsoleSnapshot = {
  agents: ApiAgent[]
  systemState: ApiSystemState
  contacts: ApiContact[]
  groups: ApiGroup[]
  mentions: ApiMention[]
  remarkRules: ApiRemarkRule[]
  remarkTasks: ApiRemarkTask[]
  entitlements: ApiEntitlement[]
  backups: ApiBackup[]
  audits: ApiAudit[]
}

function idempotencyKey(prefix: string) {
  return `${prefix}-${crypto.randomUUID()}`
}

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers)
  if (apiKey) headers.set('X-Api-Key', apiKey)
  if (init.body && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json')
  const response = await fetch(path, { ...init, headers })
  if (!response.ok) {
    const problem = await response.json().catch(() => null) as { title?: string; detail?: string; errorCode?: string } | null
    throw new Error(problem?.detail ?? problem?.title ?? problem?.errorCode ?? `API request failed (${response.status})`)
  }
  if (response.status === 204) return undefined as T
  return response.json() as Promise<T>
}

export async function loadConsoleSnapshot(): Promise<ConsoleSnapshot> {
  const [agents, systemState, contacts, groups, mentions, remarkRules, remarkTasks, entitlements, backups, audits] = await Promise.all([
    request<ApiAgent[]>('/api/agents'),
    request<ApiSystemState>('/api/system-state'),
    request<ApiContact[]>('/api/contacts'),
    request<ApiGroup[]>('/api/groups'),
    request<ApiMention[]>('/api/group-mentions'),
    request<ApiRemarkRule[]>('/api/remark-rules'),
    request<ApiRemarkTask[]>('/api/remark-tasks'),
    request<ApiEntitlement[]>('/api/entitlements'),
    request<ApiBackup[]>('/api/backups'),
    request<ApiAudit[]>('/api/audit-logs?take=100'),
  ])
  return { agents, systemState, contacts, groups, mentions, remarkRules, remarkTasks, entitlements, backups, audits }
}

export async function setAutomationState(
  current: ApiSystemState,
  paused: boolean,
  reason: string,
): Promise<ApiSystemState> {
  return request<ApiSystemState>('/api/system-state/automation', {
    method: 'PUT',
    body: JSON.stringify({ expectedVersion: current.version, paused, reason }),
  })
}

export async function queueRemarkTask(ruleId: string, targetId: string): Promise<ApiRemarkTask> {
  return request<ApiRemarkTask>('/api/remark-tasks', {
    method: 'POST',
    headers: { 'Idempotency-Key': idempotencyKey('remark') },
    body: JSON.stringify({ ruleId, targetId }),
  })
}

export async function createLogicalBackup(reason: string): Promise<ApiBackupVerification> {
  const created = await request<ApiBackup>('/api/backups', {
    method: 'POST',
    headers: { 'Idempotency-Key': idempotencyKey('backup') },
    body: JSON.stringify({ reason }),
  })
  return request<ApiBackupVerification>(`/api/backups/${created.id}/verify`, { method: 'POST' })
}

export async function createControlledMergeRestore(backupId: string) {
  return request(`/api/backups/${backupId}/restore`, {
    method: 'POST',
    headers: { 'Idempotency-Key': idempotencyKey('restore') },
    body: JSON.stringify({ confirmation: 'RESTORE' }),
  })
}

export async function activateTarget(input: {
  targetId: string
  targetKind: 'contact' | 'group'
  packageCode: 'BASIC' | 'ADVANCED_GENERAL'
  duration: ApiEntitlement['duration']
}) {
  const issued = await request<{ code: string }>('/api/activation-codes', {
    method: 'POST',
    body: JSON.stringify({ packageCode: input.packageCode, duration: input.duration }),
  })
  return request('/api/activation-codes/redeem', {
    method: 'POST',
    headers: { 'Idempotency-Key': idempotencyKey('activation') },
    body: JSON.stringify({ code: issued.code, targetKind: input.targetKind, targetId: input.targetId }),
  })
}
