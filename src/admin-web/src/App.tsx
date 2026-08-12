import { useCallback, useEffect, useId, useMemo, useRef, useState } from 'react'
import {
  Activity,
  ArchiveRestore,
  Bell,
  Bot,
  Check,
  ChevronDown,
  CircleAlert,
  CirclePause,
  Clock3,
  ContactRound,
  DatabaseBackup,
  FileClock,
  Group,
  KeyRound,
  LayoutDashboard,
  Menu,
  MessageSquareText,
  PackageCheck,
  Pencil,
  Play,
  Plus,
  RefreshCw,
  Search,
  ServerCog,
  ShieldCheck,
  Users,
  X,
} from 'lucide-react'
import {
  activateTarget,
  ApiConnectionError,
  createControlledMergeRestore,
  createLogicalBackup,
  loadConsoleSnapshot,
  queueRemarkTask,
  setAutomationState,
  type ApiAudit,
  type ApiAgent,
  type ApiEntitlement,
  type ApiRemarkRule,
  type ApiRemarkTask,
  type ApiSystemState,
  type ConsoleSnapshot,
} from './api'
import './App.css'

type PageId = 'dashboard' | 'targets' | 'mentions' | 'services' | 'backups' | 'audit'
type TargetType = 'contact' | 'group'

type Target = {
  id: string
  type: TargetType
  name: string
  remark: string
  account: string
  service: string
  expiresAt: string
  status: 'active' | 'expiring' | 'inactive'
}

type Mention = {
  id: string
  group: string
  sender: string
  content: string
  receivedAt: string
  priority: 'high' | 'normal'
  status: 'pending' | 'handled' | 'blocked'
  service: string
}

type Entitlement = {
  id: string
  targetId: string
  target: string
  type: string
  packageCode: ApiEntitlement['packageCode']
  plan: string
  period: string
  startsAt: string
  endsAt: string
  status: 'active' | 'expiring' | 'scheduled' | 'expired' | 'suspended' | 'revoked'
}

type Backup = {
  id: string
  createdAt: string
  type: 'full' | 'incremental'
  size: string
  checksum: string
  status: 'verified' | 'running' | 'failed'
}

type AuditRow = [string, string, string, string, string, string]

const navItems: Array<{ id: PageId; label: string; icon: typeof LayoutDashboard }> = [
  { id: 'dashboard', label: '运行总览', icon: LayoutDashboard },
  { id: 'targets', label: '联系人与群', icon: Users },
  { id: 'mentions', label: '@ 消息中心', icon: MessageSquareText },
  { id: 'services', label: '服务与权益', icon: PackageCheck },
  { id: 'backups', label: '备份与恢复', icon: DatabaseBackup },
  { id: 'audit', label: '审计日志', icon: FileClock },
]

const pageTitles: Record<PageId, { title: string; subtitle: string }> = {
  dashboard: { title: '运行总览', subtitle: '控制面实时状态' },
  targets: { title: '联系人与群', subtitle: '备注、服务状态和微信同步' },
  mentions: { title: '@ 消息中心', subtitle: '群内提及、权益门禁和处理队列' },
  services: { title: '服务与权益', subtitle: '套餐、激活期限和授权范围' },
  backups: { title: '备份与恢复', subtitle: '备份完整性和受控合并恢复' },
  audit: { title: '审计日志', subtitle: '不可变操作轨迹' },
}

function StatusBadge({ status }: { status: string }) {
  const labels: Record<string, string> = {
    active: '有效', expiring: '即将到期', inactive: '未激活', pending: '待处理', handled: '已处理',
    blocked: '已拦截', paused: '已暂停', verified: '校验通过', running: '执行中', failed: '失败', online: '在线', warning: '异常',
    completed: '已完成', conflict: '有冲突', canceled: '已取消', scheduled: '待生效', expired: '已过期',
    suspended: '已暂停', revoked: '已撤销',
  }
  return <span className={`status status-${status}`}><span className="status-dot" />{labels[status] ?? status}</span>
}

function Modal({ title, children, onClose }: { title: string; children: React.ReactNode; onClose: () => void }) {
  const modalRef = useRef<HTMLElement>(null)
  const onCloseRef = useRef(onClose)
  const titleId = useId()
  onCloseRef.current = onClose
  useEffect(() => {
    const previouslyFocused = document.activeElement instanceof HTMLElement ? document.activeElement : null
    const previousOverflow = document.body.style.overflow
    const focusFrame = window.requestAnimationFrame(() => {
      if (document.activeElement instanceof HTMLElement && modalRef.current?.contains(document.activeElement)) return
      const preferredFocus = modalRef.current?.querySelector<HTMLElement>('[autofocus]')
      const fallbackFocus = modalRef.current?.querySelector<HTMLElement>('button:not([disabled]), input:not([disabled]), select:not([disabled]), [tabindex]:not([tabindex="-1"])')
      ;(preferredFocus ?? fallbackFocus)?.focus()
    })
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        event.preventDefault()
        onCloseRef.current()
        return
      }
      if (event.key !== 'Tab' || !modalRef.current) return
      const focusable = Array.from(modalRef.current.querySelectorAll<HTMLElement>('button:not([disabled]), input:not([disabled]), select:not([disabled]), [tabindex]:not([tabindex="-1"])'))
      if (focusable.length === 0) return
      const first = focusable[0]
      const last = focusable[focusable.length - 1]
      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault(); last.focus()
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault(); first.focus()
      }
    }
    document.body.style.overflow = 'hidden'
    document.addEventListener('keydown', handleKeyDown)
    return () => {
      window.cancelAnimationFrame(focusFrame)
      document.body.style.overflow = previousOverflow
      document.removeEventListener('keydown', handleKeyDown)
      previouslyFocused?.focus()
    }
  }, [])
  return (
    <div className="modal-backdrop" role="presentation" onMouseDown={(event) => event.target === event.currentTarget && onClose()}>
      <section ref={modalRef} className="modal" role="dialog" aria-modal="true" aria-labelledby={titleId}>
        <header className="modal-header"><h2 id={titleId}>{title}</h2><button className="icon-button" type="button" title="关闭" aria-label="关闭" onClick={onClose}><X size={18} /></button></header>
        {children}
      </section>
    </div>
  )
}

function DashboardPage({
  apiConnected,
  targets,
  mentions,
  entitlements,
  backups,
  remarkTasks,
  agents,
  systemState,
  onAutomationChange,
}: {
  apiConnected: boolean
  targets: Target[]
  mentions: Mention[]
  entitlements: Entitlement[]
  backups: Backup[]
  remarkTasks: ApiRemarkTask[]
  agents: ApiAgent[]
  systemState: ApiSystemState | null
  onAutomationChange: (paused: boolean, reason: string) => Promise<void>
}) {
  const [automationIntent, setAutomationIntent] = useState<'pause' | 'resume' | null>(null)
  const [automationReason, setAutomationReason] = useState('')
  const [automationConfirmation, setAutomationConfirmation] = useState('')
  const [automationBusy, setAutomationBusy] = useState(false)
  const [automationError, setAutomationError] = useState('')
  const activeEntitlements = entitlements.filter((item) => item.status === 'active' || item.status === 'expiring')
  const expiringEntitlements = entitlements.filter((item) => item.status === 'expiring')
  const pendingMentions = mentions.filter((item) => item.status === 'pending')
  const pendingRemarks = remarkTasks.filter((item) => item.status === 'pending')
  const failedRemarks = remarkTasks.filter((item) => item.status === 'failed' || item.status === 'conflict')
  const latestBackup = backups[0]
  const onlineAgents = agents.filter((item) => item.online)
  const expectedConfirmation = automationIntent === 'pause' ? 'PAUSE' : 'RESUME'
  const changeAutomationState = async () => {
    if (!apiConnected || !automationIntent || automationConfirmation !== expectedConfirmation || automationReason.trim().length < 3) return
    setAutomationBusy(true); setAutomationError('')
    try {
      await onAutomationChange(automationIntent === 'pause', automationReason.trim())
      setAutomationIntent(null); setAutomationReason(''); setAutomationConfirmation('')
    } catch (stateError) {
      setAutomationError(stateError instanceof Error ? stateError.message : '自动化状态变更失败')
    } finally {
      setAutomationBusy(false)
    }
  }
  return (
    <div className="page-stack">
      <section className={`automation-band ${!apiConnected || systemState?.automationPaused ? 'paused' : ''}`}>
        <span className="automation-icon">{!apiConnected || systemState?.automationPaused ? <CirclePause size={20} /> : <ShieldCheck size={20} />}</span>
        <div><strong>{!systemState ? '自动化状态未知' : systemState.automationPaused ? '自动化已暂停' : apiConnected ? '自动化运行许可已开启' : '控制面连接已中断'}</strong><span>{systemState ? `状态版本 ${systemState.version} · ${formatDate(systemState.updatedAt)} ${formatTime(systemState.updatedAt)}${apiConnected ? '' : ' · 最后快照'}` : '等待控制面返回实时状态'}</span></div>
        <button className={systemState?.automationPaused ? 'primary-button' : 'danger-button'} disabled={!apiConnected || !systemState} onClick={() => { if (!systemState) return; setAutomationIntent(systemState.automationPaused ? 'resume' : 'pause'); setAutomationError('') }}>{systemState?.automationPaused ? <Play size={16} /> : <CirclePause size={16} />}{systemState?.automationPaused ? '申请恢复' : '紧急暂停'}</button>
      </section>
      <section className="metrics" aria-label="关键指标">
        <div className="metric"><span>执行端在线</span><strong>{onlineAgents.length} / {agents.length}</strong><small className={onlineAgents.length ? 'positive' : 'attention'}>{agents.length ? '60 秒心跳窗口' : '尚无 Agent 注册'}</small></div>
        <div className="metric"><span>有效服务</span><strong>{activeEntitlements.length}</strong><small>{expiringEntitlements.length} 个将在 30 天内到期</small></div>
        <div className="metric"><span>待处理 @</span><strong>{pendingMentions.length}</strong><small className={pendingMentions.length ? 'attention' : 'positive'}>{pendingMentions.length ? '等待下游规则处理' : '当前无积压'}</small></div>
        <div className="metric"><span>最近备份</span><strong>{latestBackup ? latestBackup.createdAt : '暂无'}</strong><small className={latestBackup?.status === 'verified' ? 'positive' : 'attention'}>{latestBackup ? <StatusBadge status={latestBackup.status} /> : '尚未创建'}</small></div>
      </section>
      <div className="dashboard-grid">
        <section className="panel span-two">
          <div className="panel-header"><div><h2>执行端状态</h2><p>Agent 心跳、微信实例绑定和串行队列</p></div><ServerCog size={18} /></div>
          <div className="table-wrap"><table><thead><tr><th>执行端</th><th>微信实例</th><th>版本</th><th>队列</th><th>模式</th><th>状态</th></tr></thead><tbody>
            {agents.map((agent) => <tr key={agent.id}><td><strong>{agent.agentId}</strong><small className="cell-subtitle">{agent.reasonCode ?? '尚无心跳'}</small></td><td>{agent.weChatInstanceId}</td><td>{agent.agentVersion ?? '-'}</td><td>{agent.queueDepth ?? 0}</td><td>{agent.dryRun === false ? '写入' : '只读'}</td><td><StatusBadge status={agent.online && agent.runtimeState === 'healthy' ? 'online' : agent.isEnabled ? 'warning' : 'paused'} /></td></tr>)}
          </tbody></table></div>
          {agents.length === 0 && <div className="empty-state">尚无 Agent 注册；当前没有执行端在线数据</div>}
        </section>
        <section className="panel alert-panel">
          <div className="panel-header"><div><h2>需要处理</h2><p>按风险优先级排序</p></div><Bell size={18} /></div>
          <div className="alert-list">
            {!apiConnected && <div className="alert-row critical"><CircleAlert size={18} /><div><strong>控制面未连接</strong><span>{systemState ? '当前仅显示最后一次成功快照' : '当前没有可用的实时数据'}</span></div><time>现在</time></div>}
            {failedRemarks.length > 0 && <div className="alert-row critical"><CircleAlert size={18} /><div><strong>{failedRemarks.length} 个备注任务需复核</strong><span>发生冲突或执行失败</span></div><time>当前</time></div>}
            {pendingMentions.length > 0 && <div className="alert-row"><Clock3 size={18} /><div><strong>{pendingMentions.length} 条 @ 等待处理</strong><span>已通过权益门禁</span></div><time>当前</time></div>}
            {expiringEntitlements.length > 0 && <div className="alert-row"><Clock3 size={18} /><div><strong>{expiringEntitlements.length} 项服务即将到期</strong><span>请核对续费状态</span></div><time>30 天内</time></div>}
            {latestBackup?.status === 'verified' && <div className="alert-row"><ShieldCheck size={18} /><div><strong>最近备份校验通过</strong><span>{latestBackup.id}</span></div><time>{latestBackup.createdAt}</time></div>}
            {apiConnected && failedRemarks.length === 0 && pendingMentions.length === 0 && expiringEntitlements.length === 0 && !latestBackup && <div className="empty-alert">当前没有需要处理的控制面事件</div>}
          </div>
        </section>
      </div>
      <section className="panel">
        <div className="panel-header"><div><h2>控制面队列</h2><p>实时后端快照</p></div></div>
        <div className="table-wrap"><table><thead><tr><th>队列</th><th>总数</th><th>待处理</th><th>异常/冲突</th><th>状态</th></tr></thead><tbody>
          <tr><td className="strong-cell">群 @ 事件</td><td>{mentions.length}</td><td>{pendingMentions.length}</td><td>{mentions.filter((item) => item.status === 'blocked').length}</td><td><StatusBadge status={!apiConnected ? 'warning' : pendingMentions.length ? 'pending' : 'active'} /></td></tr>
          <tr><td className="strong-cell">自动备注任务</td><td>{remarkTasks.length}</td><td>{pendingRemarks.length}</td><td>{failedRemarks.length}</td><td><StatusBadge status={!apiConnected || failedRemarks.length ? 'warning' : pendingRemarks.length ? 'pending' : 'active'} /></td></tr>
          <tr><td className="strong-cell">联系人与群</td><td>{targets.length}</td><td>{targets.filter((item) => item.status === 'inactive').length}</td><td>{targets.filter((item) => item.status === 'expiring').length}</td><td><StatusBadge status={!apiConnected || targets.some((item) => item.status === 'expiring') ? 'warning' : 'active'} /></td></tr>
        </tbody></table></div>
      </section>
      {automationIntent && <Modal title={automationIntent === 'pause' ? '暂停全部自动化' : '恢复自动化许可'} onClose={() => setAutomationIntent(null)}><div className="modal-body"><div className="danger-notice"><CircleAlert size={18} /><div><strong>{automationIntent === 'pause' ? '所有 Agent 将收到急停状态' : '恢复许可不会绕过 Agent 本地安全门禁'}</strong><span>{automationIntent === 'pause' ? '新的自动备注与已激活群 @ 处理将被控制面拦截。' : '未知微信版本、窗口身份不确定或只读构建仍会保持暂停。'}</span></div></div><label className="field"><span>变更原因</span><input aria-label="变更原因" value={automationReason} onChange={(event) => setAutomationReason(event.target.value)} maxLength={500} autoFocus /></label><label className="field"><span>输入 {expectedConfirmation} 确认</span><input aria-label={`输入 ${expectedConfirmation} 确认`} value={automationConfirmation} onChange={(event) => setAutomationConfirmation(event.target.value)} autoComplete="off" /></label>{!apiConnected && <div className="error-inline" role="alert"><CircleAlert size={16} />控制面已断开，不能提交状态变更</div>}{automationError && <div className="error-inline" role="alert"><CircleAlert size={16} />{automationError}</div>}</div><footer className="modal-footer"><button className="secondary-button" onClick={() => setAutomationIntent(null)}>取消</button><button className={automationIntent === 'pause' ? 'danger-button' : 'primary-button'} disabled={!apiConnected || automationBusy || automationReason.trim().length < 3 || automationConfirmation !== expectedConfirmation} onClick={() => void changeAutomationState()}>{automationBusy ? '处理中' : automationIntent === 'pause' ? '确认暂停' : '确认恢复'}</button></footer></Modal>}
    </div>
  )
}

function TargetsPage({
  targets,
  remarkRules,
  remarkTasks,
  apiConnected,
  onQueueRemark,
}: {
  targets: Target[]
  remarkRules: ApiRemarkRule[]
  remarkTasks: ApiRemarkTask[]
  apiConnected: boolean
  onQueueRemark: (ruleId: string, targetId: string) => Promise<ApiRemarkTask>
}) {
  const [tab, setTab] = useState<TargetType>('group')
  const [query, setQuery] = useState('')
  const [editing, setEditing] = useState<Target | null>(null)
  const [ruleId, setRuleId] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  const filtered = targets.filter((item) => item.type === tab && `${item.name}${item.remark}${item.account}`.toLowerCase().includes(query.toLowerCase()))
  const matchingRules = editing ? remarkRules.filter((item) => item.targetKind === editing.type && item.isEnabled) : []
  const openEditor = (target: Target) => {
    const firstRule = remarkRules.find((item) => item.targetKind === target.type && item.isEnabled)
    setEditing(target); setRuleId(firstRule?.id ?? ''); setError('')
  }
  const latestTask = (targetId: string) => remarkTasks.find((item) => item.targetId === targetId)
  const saveRemark = async () => {
    if (!editing) return
    setSubmitting(true); setError('')
    try {
      if (!apiConnected || !ruleId) return
      const task = await onQueueRemark(ruleId, editing.id)
      setNotice(`备注任务 ${task.id.slice(0, 12)} 已创建：${task.generatedRemark}`)
      setEditing(null)
    } catch (queueError) {
      setError(queueError instanceof Error ? queueError.message : '备注任务创建失败')
    } finally {
      setSubmitting(false)
    }
  }
  return (
    <div className="page-stack">
      {notice && <div className="toast-inline" role="status"><Check size={16} />{notice}<button title="关闭" aria-label="关闭" onClick={() => setNotice('')}><X size={15} /></button></div>}
      <section className="panel data-panel">
        <div className="toolbar">
          <div className="segmented"><button aria-pressed={tab === 'group'} className={tab === 'group' ? 'active' : ''} onClick={() => setTab('group')}><Group size={16} />群聊</button><button aria-pressed={tab === 'contact'} className={tab === 'contact' ? 'active' : ''} onClick={() => setTab('contact')}><ContactRound size={16} />联系人</button></div>
          <div className="toolbar-actions"><label className="search"><Search size={16} /><input aria-label="搜索名称、备注或账号" value={query} onChange={(event) => setQuery(event.target.value)} placeholder="搜索名称、备注或账号" /></label></div>
        </div>
        <div className="table-wrap"><table><thead><tr><th>{tab === 'group' ? '群聊' : '联系人'}</th><th>系统备注</th><th>微信标识</th><th>服务</th><th>到期时间</th><th>备注任务</th><th>状态</th><th /></tr></thead><tbody>
          {filtered.map((target) => { const task = latestTask(target.id); const pending = task?.status === 'pending'; const actionLabel = !apiConnected ? '离线时不能创建任务' : pending ? '已有待执行备注任务' : `为 ${target.name} 创建备注同步任务`; return <tr key={target.id}><td><div className="identity"><span className={`identity-icon ${target.type}`}>{target.type === 'group' ? <Group size={16} /> : <ContactRound size={16} />}</span><div><strong>{target.name}</strong><small>{target.id}</small></div></div></td><td className="mono-cell">{target.remark}</td><td>{target.account}</td><td>{target.service}</td><td>{target.expiresAt}</td><td>{task ? <div><StatusBadge status={task.status} /><small className="cell-subtitle">{task.generatedRemark}</small></div> : '暂无'}</td><td><StatusBadge status={target.status} /></td><td><button className="icon-button small" title={actionLabel} aria-label={actionLabel} disabled={!apiConnected || pending} onClick={() => openEditor(target)}><Pencil size={15} /></button></td></tr> })}
        </tbody></table></div>
        <footer className="table-footer"><span>共 {filtered.length} 条</span><span>{apiConnected ? `后端任务 ${remarkTasks.length} 个` : '离线只读'}</span></footer>
      </section>
      {editing && <Modal title="创建备注同步任务" onClose={() => setEditing(null)}><div className="modal-body"><div className="target-summary"><span className={`identity-icon ${editing.type}`}>{editing.type === 'group' ? <Group size={17} /> : <ContactRound size={17} />}</span><div><strong>{editing.name}</strong><small>{editing.account} · {editing.id}</small></div></div><label className="field"><span>备注规则</span><select aria-label="备注规则" value={ruleId} onChange={(event) => setRuleId(event.target.value)} autoFocus><option value="">选择已启用的规则</option>{matchingRules.map((rule) => <option value={rule.id} key={rule.id}>{rule.name} · {rule.template}</option>)}</select></label><div className="notice"><ShieldCheck size={17} /><span>任务进入后端队列后，只有通过微信目标身份二次校验并由 Agent 回报一致结果，系统备注才会更新。</span></div>{error && <div className="danger-notice"><CircleAlert size={18} /><div><strong>创建失败</strong><span>{error}</span></div></div>}</div><footer className="modal-footer"><button className="secondary-button" onClick={() => setEditing(null)}>取消</button><button className="primary-button" onClick={() => void saveRemark()} disabled={!apiConnected || submitting || !ruleId}><Check size={16} />{submitting ? '处理中' : '确认排队'}</button></footer></Modal>}
    </div>
  )
}

function MentionsPage({ mentions }: { mentions: Mention[] }) {
  const [status, setStatus] = useState<'all' | Mention['status']>('all')
  const items = mentions.filter((item) => status === 'all' || item.status === status)
  return <section className="panel data-panel"><div className="toolbar"><div className="segmented compact"><button aria-pressed={status === 'all'} className={status === 'all' ? 'active' : ''} onClick={() => setStatus('all')}>全部</button><button aria-pressed={status === 'pending'} className={status === 'pending' ? 'active' : ''} onClick={() => setStatus('pending')}>待处理</button><button aria-pressed={status === 'handled'} className={status === 'handled' ? 'active' : ''} onClick={() => setStatus('handled')}>已处理</button><button aria-pressed={status === 'blocked'} className={status === 'blocked' ? 'active' : ''} onClick={() => setStatus('blocked')}>已拦截</button></div></div><div className="mention-list">
    {items.map((item) => <article className="mention-row" key={item.id}><span className={`priority-marker ${item.priority}`} /><div className="mention-main"><div className="mention-meta"><strong>{item.group}</strong><span>{item.sender}</span><time>{item.receivedAt}</time></div><p>{item.content}</p><div className="mention-tags"><span>{item.service}</span><span>{item.id}</span></div></div><div className="mention-actions"><StatusBadge status={item.status} /></div></article>)}
    {items.length === 0 && <div className="empty-state">当前筛选条件下没有 @ 事件</div>}
  </div></section>
}

function ServicesPage({
  entitlements,
  targets,
  apiConnected,
  onActivated,
}: {
  entitlements: Entitlement[]
  targets: Target[]
  apiConnected: boolean
  onActivated: (input: { targetId: string; targetKind: TargetType; packageCode: 'BASIC' | 'ADVANCED_GENERAL'; duration: ApiEntitlement['duration'] }, operationKey: string) => Promise<void>
}) {
  const [modal, setModal] = useState(false)
  const [targetId, setTargetId] = useState('')
  const [plan, setPlan] = useState('基础服务')
  const [period, setPeriod] = useState('30 天')
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState('')
  const [query, setQuery] = useState('')
  const activationAttempt = useRef<{ signature: string; key: string } | null>(null)
  const hasActiveBasic = (candidateId: string) => entitlements.some((item) => item.targetId === candidateId && item.packageCode === 'BASIC' && (item.status === 'active' || item.status === 'expiring'))
  const advancedSelectedWithoutBasic = plan === '高级通用群服务' && !!targetId && !hasActiveBasic(targetId)
  const activate = async () => {
    const selected = targets.find((item) => item.id === targetId)
    if (!selected) return
    if (plan === '高级通用群服务' && (selected.type !== 'group' || !hasActiveBasic(selected.id))) {
      setError('高级通用群服务只能开通给已有有效基础服务的群聊')
      return
    }
    setSubmitting(true); setError('')
    const durationMap: Record<string, ApiEntitlement['duration']> = { '30 天': 'days30', '60 天': 'days60', '90 天': 'days90', '半年': 'halfYear', '一年': 'oneYear', '永久': 'permanent' }
    try {
      if (!apiConnected) return
      const activationInput = {
        targetId: selected.id,
        targetKind: selected.type,
        packageCode: plan === '基础服务' ? 'BASIC' : 'ADVANCED_GENERAL',
        duration: durationMap[period],
      } as const
      const signature = JSON.stringify(activationInput)
      if (activationAttempt.current?.signature !== signature) {
        activationAttempt.current = { signature, key: `activation-${crypto.randomUUID()}` }
      }
      await onActivated(activationInput, activationAttempt.current.key)
      activationAttempt.current = null
      setModal(false); setTargetId('')
    } catch (activationError) {
      setError(activationError instanceof Error ? activationError.message : '服务开通失败')
    } finally {
      setSubmitting(false)
    }
  }
  const activeCount = entitlements.filter((item) => item.status === 'active' || item.status === 'expiring').length
  const expiringCount = entitlements.filter((item) => item.status === 'expiring').length
  const permanentCount = entitlements.filter((item) => item.period === '永久').length
  const eligibleTargets = plan === '高级通用群服务'
    ? targets.filter((item) => item.type === 'group')
    : targets
  const filteredEntitlements = entitlements.filter((item) => `${item.id}${item.target}${item.plan}${item.period}`.toLowerCase().includes(query.toLowerCase()))
  const closeActivation = () => { activationAttempt.current = null; setModal(false) }
  return <div className="page-stack"><section className="metrics service-metrics"><div className="metric"><span>有效权益</span><strong>{activeCount}</strong><small>基础与高级通用服务</small></div><div className="metric"><span>即将到期</span><strong>{expiringCount}</strong><small className="attention">30 天内</small></div><div className="metric"><span>永久授权</span><strong>{permanentCount}</strong><small>不含维护期限</small></div><div className="metric"><span>数据来源</span><strong>{apiConnected ? '实时' : '离线'}</strong><small className={apiConnected ? 'positive' : 'attention'}>{apiConnected ? '权益流水已连接' : '仅显示最后快照'}</small></div></section><section className="panel data-panel"><div className="toolbar"><strong>权益实例</strong><div className="toolbar-actions"><label className="search"><Search size={16} /><input aria-label="搜索服务单或目标" value={query} onChange={(event) => setQuery(event.target.value)} placeholder="搜索服务单或目标" /></label><button className="primary-button" disabled={!apiConnected} onClick={() => { activationAttempt.current = null; setTargetId(''); setError(''); setModal(true) }}><Plus size={16} />开通服务</button></div></div><div className="table-wrap"><table><thead><tr><th>服务单</th><th>授权目标</th><th>套餐</th><th>期限</th><th>开始时间</th><th>结束时间</th><th>状态</th></tr></thead><tbody>{filteredEntitlements.map((item) => <tr key={item.id}><td className="mono-cell">{item.id}</td><td><strong>{item.target}</strong><small className="cell-subtitle">{item.type}</small></td><td>{item.plan}</td><td>{item.period}</td><td>{item.startsAt}</td><td>{item.endsAt}</td><td><StatusBadge status={item.status} /></td></tr>)}</tbody></table></div>{filteredEntitlements.length === 0 && <div className="empty-state">没有匹配的权益记录</div>}</section>
      {modal && <Modal title="开通服务" onClose={closeActivation}><div className="modal-body form-grid"><label className="field full"><span>授权目标</span><select aria-label="授权目标" value={targetId} onChange={(event) => { setTargetId(event.target.value); setError('') }} autoFocus><option value="">{plan === '高级通用群服务' ? '选择已有有效基础服务的群聊' : '选择已验证的群聊或联系人'}</option>{eligibleTargets.map((item) => { const missingBasic = plan === '高级通用群服务' && !hasActiveBasic(item.id); return <option value={item.id} key={item.id} disabled={missingBasic}>{item.name} · {item.type === 'group' ? '群聊' : '联系人'}{missingBasic ? ' · 需先开通基础服务' : ''}</option> })}</select></label><label className="field"><span>服务套餐</span><select aria-label="服务套餐" value={plan} onChange={(event) => { const nextPlan = event.target.value; const selectedTarget = targets.find((item) => item.id === targetId); setPlan(nextPlan); setError(''); if (nextPlan === '高级通用群服务' && (selectedTarget?.type !== 'group' || !hasActiveBasic(targetId))) setTargetId('') }}><option>基础服务</option><option>高级通用群服务</option></select></label><label className="field"><span>服务期限</span><select aria-label="服务期限" value={period} onChange={(event) => setPeriod(event.target.value)}><option>30 天</option><option>60 天</option><option>90 天</option><option>半年</option><option>一年</option><option>永久</option></select></label><div className="activation-preview full"><KeyRound size={18} /><div><strong>{plan} · {period}</strong><span>{plan === '高级通用群服务' ? '仅支持群聊，并要求同一群聊已有当前有效的基础服务。' : '生效前将校验目标绑定、激活码和现有权益。'}</span></div></div>{advancedSelectedWithoutBasic && <div className="danger-notice full" role="alert"><CircleAlert size={18} /><div><strong>缺少基础服务</strong><span>请先为该群聊开通有效基础服务，再开通高级通用群服务。</span></div></div>}{error && <div className="danger-notice full" role="alert"><CircleAlert size={18} /><div><strong>开通失败</strong><span>{error}</span></div></div>}</div><footer className="modal-footer"><button className="secondary-button" onClick={closeActivation}>取消</button><button className="primary-button" onClick={() => void activate()} disabled={!apiConnected || !targetId || advancedSelectedWithoutBasic || submitting}><PackageCheck size={16} />{submitting ? '处理中' : '确认开通'}</button></footer></Modal>}
  </div>
}

function BackupsPage({
  backups,
  apiConnected,
  onCreate,
  onRestore,
}: {
  backups: Backup[]
  apiConnected: boolean
  onCreate: () => Promise<void>
  onRestore: (id: string) => Promise<void>
}) {
  const [restore, setRestore] = useState<Backup | null>(null)
  const [confirm, setConfirm] = useState('')
  const [notice, setNotice] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const createBackup = async () => {
    if (!apiConnected) return
    setBusy(true); setError('')
    try { await onCreate(); setNotice('备份创建并校验通过') }
    catch (backupError) { setError(backupError instanceof Error ? backupError.message : '备份创建失败') }
    finally { setBusy(false) }
  }
  const performRestore = async () => {
    if (confirm !== 'RESTORE' || !restore) return
    setBusy(true); setError('')
    try {
      if (!apiConnected) return
      await onRestore(restore.id)
      setNotice(`${restore.id} 已完成受控合并恢复，自动化保持暂停`); setRestore(null); setConfirm('')
    } catch (restoreError) {
      setError(restoreError instanceof Error ? restoreError.message : '恢复任务创建失败')
    } finally { setBusy(false) }
  }
  return <div className="page-stack"><section className="backup-summary"><div><ShieldCheck size={21} /><span>备份策略</span><strong>{apiConnected ? '正常' : '状态未知'}</strong><small>每次恢复前自动再备份</small></div><div><DatabaseBackup size={21} /><span>最近备份</span><strong>{backups.length ? backups[0].createdAt : '暂无'}</strong><small>{apiConnected ? '后端清单已连接' : '仅显示最后快照'}</small></div><div><ArchiveRestore size={21} /><span>恢复模式</span><strong>受控合并</strong><small>修改当前库并强制暂停自动化</small></div></section>{notice && <div className="toast-inline" role="status"><Check size={16} />{notice}<button title="关闭" aria-label="关闭" onClick={() => setNotice('')}><X size={15} /></button></div>}{error && <div className="error-inline" role="alert"><CircleAlert size={16} />{error}<button title="关闭" aria-label="关闭" onClick={() => setError('')}><X size={15} /></button></div>}<section className="panel data-panel"><div className="panel-header toolbar-heading"><div><h2>备份记录</h2><p>恢复会修改当前库；机器人保持暂停，旧发送任务不会自动重放</p></div><button className="primary-button" disabled={!apiConnected || busy} onClick={() => void createBackup()}><DatabaseBackup size={16} />{busy ? '处理中' : '立即备份'}</button></div><div className="table-wrap"><table><thead><tr><th>备份编号</th><th>创建时间</th><th>类型</th><th>大小</th><th>校验和</th><th>状态</th><th /></tr></thead><tbody>{backups.map((backup) => <tr key={backup.id}><td className="mono-cell">{backup.id}</td><td>{backup.createdAt}</td><td>{backup.type === 'full' ? '全量' : '逻辑备份'}</td><td>{backup.size}</td><td className="mono-cell">{backup.checksum}</td><td><StatusBadge status={backup.status} /></td><td><button className="secondary-button small-button" disabled={!apiConnected || backup.status !== 'verified'} onClick={() => setRestore(backup)}><ArchiveRestore size={15} />恢复</button></td></tr>)}</tbody></table></div>{backups.length === 0 && <div className="empty-state">暂无备份记录</div>}</section>
    {restore && <Modal title="执行受控合并恢复" onClose={() => { setRestore(null); setConfirm('') }}><div className="modal-body"><div className="danger-notice"><CircleAlert size={19} /><div><strong>此操作会直接修改当前数据库</strong><span>系统先自动创建恢复前备份，再覆盖联系人、群和备注规则配置；权益、兑换和流水仅补缺，不会用旧快照覆盖现有事实。恢复后自动化保持暂停。</span></div></div><dl className="restore-details"><div><dt>备份编号</dt><dd>{restore.id}</dd></div><div><dt>创建时间</dt><dd>{restore.createdAt}</dd></div><div><dt>完整性</dt><dd>校验通过</dd></div></dl><label className="field"><span>输入 RESTORE 确认</span><input aria-label="输入 RESTORE 确认" value={confirm} onChange={(event) => setConfirm(event.target.value)} autoComplete="off" /></label></div><footer className="modal-footer"><button className="secondary-button" onClick={() => setRestore(null)}>取消</button><button className="danger-button" disabled={!apiConnected || confirm !== 'RESTORE' || busy} onClick={() => void performRestore()}><ArchiveRestore size={16} />{busy ? '处理中' : '确认恢复当前库'}</button></footer></Modal>}
  </div>
}

function AuditPage({ rows }: { rows: AuditRow[] }) {
  const [query, setQuery] = useState('')
  const filteredRows = rows.filter((row) => row.join(' ').toLowerCase().includes(query.toLowerCase()))
  return <section className="panel data-panel"><div className="toolbar"><label className="search wide"><Search size={16} /><input aria-label="搜索审计编号、操作者或对象" value={query} onChange={(event) => setQuery(event.target.value)} placeholder="搜索审计编号、操作者或对象" /></label></div><div className="table-wrap"><table><thead><tr><th>审计编号</th><th>时间</th><th>操作者</th><th>操作</th><th>对象与结果</th><th>状态</th></tr></thead><tbody>{filteredRows.map((row) => <tr key={row[0]}><td className="mono-cell">{row[0]}</td><td>{row[1]}</td><td>{row[2]}</td><td className="strong-cell">{row[3]}</td><td>{row[4]}</td><td><span className={`result ${row[5] === '跳过' ? 'skipped' : ''}`}>{row[5]}</span></td></tr>)}</tbody></table></div>{filteredRows.length === 0 && <div className="empty-state">没有匹配的审计记录</div>}</section>
}

const durationLabels: Record<ApiEntitlement['duration'], string> = {
  days30: '30 天', days60: '60 天', days90: '90 天', halfYear: '半年', oneYear: '一年', permanent: '永久',
}

const packageLabels: Record<ApiEntitlement['packageCode'], string> = {
  BASIC: '基础服务', ADVANCED_GENERAL: '高级通用群服务',
}

function formatDate(value?: string) {
  if (!value) return '永久'
  return new Intl.DateTimeFormat('zh-CN', { year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date(value)).replaceAll('/', '-')
}

function formatTime(value: string) {
  return new Intl.DateTimeFormat('zh-CN', { hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false }).format(new Date(value))
}

function mapAuditRows(audits: ApiAudit[]): AuditRow[] {
  return audits.map((item) => [
    item.id.slice(0, 12).toUpperCase(),
    formatTime(item.createdAt),
    item.actor,
    item.action,
    `${item.resourceType} · ${item.resourceId.slice(0, 12)}`,
    item.success ? '成功' : '失败',
  ])
}

function mapSnapshot(snapshot: ConsoleSnapshot) {
  const names = new Map<string, string>()
  snapshot.contacts.forEach((item) => names.set(item.id, item.displayName))
  snapshot.groups.forEach((item) => names.set(item.id, item.displayName))
  const activeByTarget = new Map<string, ApiEntitlement>()
  for (const entitlement of snapshot.entitlements.filter((item) => item.effectiveStatus === 'active')) {
    const current = activeByTarget.get(entitlement.targetId)
    const isHigherTier = entitlement.packageCode === 'ADVANCED_GENERAL' && current?.packageCode === 'BASIC'
    const isNewerSameTier = current?.packageCode === entitlement.packageCode && new Date(entitlement.startsAt) > new Date(current.startsAt)
    if (!current || isHigherTier || isNewerSameTier) activeByTarget.set(entitlement.targetId, entitlement)
  }
  const now = Date.now()
  const targetStatus = (targetId: string): Target['status'] => {
    const entitlement = activeByTarget.get(targetId)
    if (!entitlement) return 'inactive'
    if (entitlement.endsAt && new Date(entitlement.endsAt).getTime() - now <= 30 * 24 * 60 * 60 * 1000) return 'expiring'
    return 'active'
  }
  const targets: Target[] = [
    ...snapshot.groups.map((item) => {
      const entitlement = activeByTarget.get(item.id)
      return { id: item.id, type: 'group' as const, name: item.displayName, remark: item.systemRemark ?? item.currentWeChatRemark ?? '未设置', account: item.externalId, service: entitlement ? packageLabels[entitlement.packageCode] : '未激活', expiresAt: entitlement ? formatDate(entitlement.endsAt) : '-', status: targetStatus(item.id) }
    }),
    ...snapshot.contacts.map((item) => {
      const entitlement = activeByTarget.get(item.id)
      return { id: item.id, type: 'contact' as const, name: item.displayName, remark: item.systemRemark ?? item.currentWeChatRemark ?? '未设置', account: item.externalId, service: entitlement ? packageLabels[entitlement.packageCode] : '未激活', expiresAt: entitlement ? formatDate(entitlement.endsAt) : '-', status: targetStatus(item.id) }
    }),
  ]
  const mentions: Mention[] = snapshot.mentions.map((item) => ({
    id: item.id,
    group: names.get(item.groupId) ?? item.groupId.slice(0, 12),
    sender: item.senderExternalId,
    content: item.content,
    receivedAt: formatTime(item.capturedAt),
    priority: item.decision === 'accepted' ? 'high' : 'normal',
    status: item.decision === 'accepted' ? 'pending' : item.decision === 'activationRequired' || item.decision === 'automationPaused' ? 'blocked' : 'handled',
    service: item.decision === 'accepted' ? '权益校验通过' : item.decision === 'activationRequired' ? '未激活' : item.decision === 'automationPaused' ? '自动化已暂停' : '已忽略',
  }))
  const entitlements: Entitlement[] = snapshot.entitlements.map((item) => ({
    id: item.id,
    targetId: item.targetId,
    target: names.get(item.targetId) ?? item.targetId.slice(0, 12),
    type: item.targetKind === 'group' ? '群聊' : '联系人',
    packageCode: item.packageCode,
    plan: packageLabels[item.packageCode],
    period: durationLabels[item.duration],
    startsAt: formatDate(item.startsAt),
    endsAt: formatDate(item.endsAt),
    status: item.effectiveStatus === 'active'
      ? (item.endsAt && new Date(item.endsAt).getTime() - now <= 30 * 24 * 60 * 60 * 1000 ? 'expiring' : 'active')
      : item.effectiveStatus,
  }))
  const backups: Backup[] = snapshot.backups.map((item) => ({
    id: item.id,
    createdAt: `${formatDate(item.createdAt)} ${formatTime(item.createdAt)}`,
    type: 'full',
    size: `${Math.max(0.1, item.bytes / 1024).toFixed(1)} KB`,
    checksum: `${item.payloadSha256.slice(0, 6)}...${item.payloadSha256.slice(-4)}`,
    status: item.status === 'verified' ? 'verified' : item.status === 'corrupt' ? 'failed' : 'running',
  }))
  return {
    agents: snapshot.agents,
    systemState: snapshot.systemState,
    targets,
    mentions,
    remarkRules: snapshot.remarkRules,
    remarkTasks: snapshot.remarkTasks,
    entitlements,
    backups,
    audits: mapAuditRows(snapshot.audits),
  }
}

function App() {
  const [page, setPage] = useState<PageId>('dashboard')
  const [mobileNav, setMobileNav] = useState(false)
  const [targets, setTargets] = useState<Target[]>([])
  const [agents, setAgents] = useState<ApiAgent[]>([])
  const [systemState, setSystemState] = useState<ApiSystemState | null>(null)
  const [mentions, setMentions] = useState<Mention[]>([])
  const [remarkRules, setRemarkRules] = useState<ApiRemarkRule[]>([])
  const [remarkTasks, setRemarkTasks] = useState<ApiRemarkTask[]>([])
  const [entitlements, setEntitlements] = useState<Entitlement[]>([])
  const [backups, setBackups] = useState<Backup[]>([])
  const [audits, setAudits] = useState<AuditRow[]>([])
  const [apiConnected, setApiConnected] = useState(false)
  const [apiError, setApiError] = useState('')
  const [refreshing, setRefreshing] = useState(false)
  const refreshInFlight = useRef<Promise<void> | null>(null)
  const mutationEpoch = useRef(0)
  const mutationsInFlight = useRef(0)
  const refreshData = useCallback(async (forceAfterCurrent = false) => {
    if (refreshInFlight.current) {
      await refreshInFlight.current
      if (!forceAfterCurrent) return
    }
    while (refreshInFlight.current) await refreshInFlight.current
    if (mutationsInFlight.current > 0 && !forceAfterCurrent) return

    const requestEpoch = mutationEpoch.current
    setRefreshing(true)
    const operation = (async () => {
      try {
        const mapped = mapSnapshot(await loadConsoleSnapshot(AbortSignal.timeout(10_000)))
        if (requestEpoch !== mutationEpoch.current || mutationsInFlight.current > 0) return
        setAgents(mapped.agents); setSystemState(mapped.systemState); setTargets(mapped.targets); setMentions(mapped.mentions); setRemarkRules(mapped.remarkRules); setRemarkTasks(mapped.remarkTasks); setEntitlements(mapped.entitlements); setBackups(mapped.backups); setAudits(mapped.audits)
        setApiConnected(true); setApiError('')
      } catch (error) {
        if (requestEpoch !== mutationEpoch.current || mutationsInFlight.current > 0) return
        setApiConnected(false)
        setApiError(error instanceof Error ? error.message : '后端连接失败')
      }
    })()
    let tracked: Promise<void>
    tracked = operation.finally(() => {
      if (refreshInFlight.current === tracked) {
        refreshInFlight.current = null
        setRefreshing(false)
      }
    })
    refreshInFlight.current = tracked
    await tracked
  }, [])
  const runMutation = useCallback(async <T,>(operation: () => Promise<T>): Promise<T> => {
    if (mutationsInFlight.current > 0) throw new Error('另一项管理操作正在处理中，请稍后重试')
    mutationEpoch.current += 1
    mutationsInFlight.current += 1
    try {
      const result = await operation()
      mutationsInFlight.current -= 1
      await refreshData(true)
      return result
    } catch (error) {
      mutationsInFlight.current -= 1
      if (error instanceof ApiConnectionError) {
        setApiConnected(false)
        setApiError(error.message)
      }
      void refreshData(true)
      throw error
    }
  }, [refreshData])
  useEffect(() => {
    void refreshData()
    const interval = window.setInterval(() => void refreshData(), 15_000)
    const refreshWhenVisible = () => { if (document.visibilityState === 'visible') void refreshData() }
    const enterOfflineMode = () => {
      setApiConnected(false)
      setApiError('浏览器网络连接已断开')
    }
    window.addEventListener('online', refreshWhenVisible)
    window.addEventListener('offline', enterOfflineMode)
    document.addEventListener('visibilitychange', refreshWhenVisible)
    return () => {
      window.clearInterval(interval)
      window.removeEventListener('online', refreshWhenVisible)
      window.removeEventListener('offline', enterOfflineMode)
      document.removeEventListener('visibilitychange', refreshWhenVisible)
    }
  }, [refreshData])
  const handleActivation = async (
    input: { targetId: string; targetKind: TargetType; packageCode: 'BASIC' | 'ADVANCED_GENERAL'; duration: ApiEntitlement['duration'] },
    operationKey: string,
  ) => {
    await runMutation(() => activateTarget(input, operationKey))
  }
  const handleCreateBackup = async () => { await runMutation(() => createLogicalBackup('管理台手工备份')) }
  const handleRestore = async (id: string) => { await runMutation(() => createControlledMergeRestore(id)) }
  const handleQueueRemark = async (ruleId: string, targetId: string) => {
    return runMutation(() => queueRemarkTask(ruleId, targetId))
  }
  const handleAutomationChange = async (paused: boolean, reason: string) => {
    if (!apiConnected || !systemState) throw new Error('控制面状态尚未加载或连接已中断')
    await runMutation(() => setAutomationState(systemState, paused, reason))
  }
  const pendingMentions = useMemo(() => mentions.filter((item) => item.status === 'pending').length, [mentions])
  const navigate = (id: PageId) => { setPage(id); setMobileNav(false) }
  return (
    <div className="app-shell">
      <aside className={`sidebar ${mobileNav ? 'open' : ''}`}>
        <div className="brand"><span className="brand-mark"><Bot size={21} /></span><div><strong>微服中控</strong><small>Automation Console</small></div><button className="icon-button mobile-close" title="关闭导航" onClick={() => setMobileNav(false)}><X size={19} /></button></div>
        <nav aria-label="主导航">{navItems.map((item) => { const Icon = item.icon; return <button key={item.id} aria-current={page === item.id ? 'page' : undefined} className={page === item.id ? 'active' : ''} onClick={() => navigate(item.id)}><Icon size={18} /><span>{item.label}</span>{item.id === 'mentions' && pendingMentions > 0 && <b aria-label={`${pendingMentions} 条待处理`}>{pendingMentions}</b>}</button> })}</nav>
        <div className="sidebar-footer"><div className="operator"><span>管</span><div><strong>平台管理员</strong><small>{apiConnected ? `${import.meta.env.DEV ? '开发环境' : '安全会话'} · API 已连接` : '离线只读'}</small></div><ChevronDown size={15} aria-hidden="true" /></div></div>
      </aside>
      {mobileNav && <button className="nav-scrim" aria-label="关闭导航遮罩" onClick={() => setMobileNav(false)} />}
      <main className="main-area">
        <header className="topbar"><div className="title-row"><button className="icon-button menu-button" title="打开导航" onClick={() => setMobileNav(true)}><Menu size={20} /></button><div><h1>{pageTitles[page].title}</h1><p>{pageTitles[page].subtitle}</p></div></div><div className="topbar-actions"><span className={`environment ${apiConnected ? '' : 'offline'}`}><Activity size={14} />{apiConnected ? 'API 已连接' : '离线只读'}</span><button className={`icon-button bordered ${refreshing ? 'spinning' : ''}`} title="刷新数据" disabled={refreshing} onClick={() => void refreshData()}><RefreshCw size={17} /></button></div></header>
        <div className="page-content">
          {apiError && <div className="connection-banner" role="alert"><CircleAlert size={16} /><span>控制面未连接，{systemState ? '当前仅显示最后一次成功快照' : '当前没有可用数据'}：{apiError}</span><button disabled={refreshing} onClick={() => void refreshData(true)}>{refreshing ? '重连中' : '重试'}</button></div>}
          {page === 'dashboard' && <DashboardPage apiConnected={apiConnected} targets={targets} mentions={mentions} entitlements={entitlements} backups={backups} remarkTasks={remarkTasks} agents={agents} systemState={systemState} onAutomationChange={handleAutomationChange} />}
          {page === 'targets' && <TargetsPage targets={targets} remarkRules={remarkRules} remarkTasks={remarkTasks} apiConnected={apiConnected} onQueueRemark={handleQueueRemark} />}
          {page === 'mentions' && <MentionsPage mentions={mentions} />}
          {page === 'services' && <ServicesPage entitlements={entitlements} targets={targets} apiConnected={apiConnected} onActivated={handleActivation} />}
          {page === 'backups' && <BackupsPage backups={backups} apiConnected={apiConnected} onCreate={handleCreateBackup} onRestore={handleRestore} />}
          {page === 'audit' && <AuditPage rows={audits} />}
        </div>
      </main>
    </div>
  )
}

export default App
