// Browser-delivered API keys are acceptable only for local development. Production
// deployments must authenticate at a same-origin BFF/session boundary.
const apiKey = import.meta.env.DEV
  ? (import.meta.env.VITE_API_KEY ?? 'wechatbot-local-development-key-change-me')
  : ''

const requestTimeoutMs = 10_000

export class ApiConnectionError extends Error {
  constructor(message: string, options?: ErrorOptions) {
    super(message, options)
    this.name = 'ApiConnectionError'
  }
}

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
  let response: Response
  try {
    response = await fetch(path, {
      ...init,
      headers,
      signal: init.signal ?? AbortSignal.timeout(requestTimeoutMs),
    })
  } catch (error) {
    const timedOut = error instanceof DOMException && (error.name === 'TimeoutError' || error.name === 'AbortError')
    throw new ApiConnectionError(timedOut ? '控制面请求超时' : '无法连接控制面', { cause: error })
  }
  if (!response.ok) {
    const problem = await response.json().catch(() => null) as { title?: string; detail?: string; errorCode?: string } | null
    const message = problem?.detail ?? problem?.title ?? problem?.errorCode
    if (response.status === 401 || response.status === 403) {
      throw new ApiConnectionError(message ?? '控制面会话已失效')
    }
    if (response.status >= 500) {
      throw new ApiConnectionError(message ?? `控制面暂不可用 (${response.status})`)
    }
    throw new Error(message ?? `API request failed (${response.status})`)
  }
  if (response.status === 204) return undefined as T
  return response.json() as Promise<T>
}

export async function loadConsoleSnapshot(signal?: AbortSignal): Promise<ConsoleSnapshot> {
  const [agents, systemState, contacts, groups, mentions, remarkRules, remarkTasks, entitlements, backups, audits] = await Promise.all([
    request<ApiAgent[]>('/api/agents', { signal }),
    request<ApiSystemState>('/api/system-state', { signal }),
    request<ApiContact[]>('/api/contacts', { signal }),
    request<ApiGroup[]>('/api/groups', { signal }),
    request<ApiMention[]>('/api/group-mentions', { signal }),
    request<ApiRemarkRule[]>('/api/remark-rules', { signal }),
    request<ApiRemarkTask[]>('/api/remark-tasks', { signal }),
    request<ApiEntitlement[]>('/api/entitlements', { signal }),
    request<ApiBackup[]>('/api/backups', { signal }),
    request<ApiAudit[]>('/api/audit-logs?take=100', { signal }),
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
}, operationKey: string) {
  return request('/api/entitlements/activate', {
    method: 'POST',
    headers: { 'Idempotency-Key': operationKey },
    body: JSON.stringify({
      packageCode: input.packageCode,
      duration: input.duration,
      targetKind: input.targetKind,
      targetId: input.targetId,
    }),
  })
}
