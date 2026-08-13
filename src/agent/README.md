# WeChatBot Windows UI Automation Agent

This directory contains the Windows-only `.NET 10` agent. It uses `FlaUI.Core`
and `FlaUI.UIA3` for read-only process, main-window, and control-tree inspection.

## Safety posture

- `dry-run` defaults to `true`.
- This build contains no implementation that sends a message or changes a
  contact/group remark. Passing `--dry-run=false` is rejected during startup,
  and a directly constructed live remark handler still fails closed with
  `LIVE_MUTATION_NOT_IMPLEMENTED`.
- Dry-run is enforced both by the host options and by the serialized executor's
  external-action boundary; a handler cannot obtain a commit permit in dry-run.
- A WeChat version must be explicitly listed in the tested compatibility
  allow-list. An unknown version, title, UI signature, missing window, or
  ambiguous process pauses command execution.
- The approved main window must be foreground, visible, enabled, free of other
  visible WeChat top-level windows, and contain the configured visible/enabled
  structural signatures. The full UIA probe has a hard deadline.
- UI names are never emitted by diagnostics. Each capture uses a fresh secret
  HMAC key, so names cannot be correlated across captures; the key is never
  emitted. Structural automation IDs use full SHA-256 for an allow-list, and
  idempotency keys are full SHA-256 hashes on disk.
- An action that might have started but cannot be confirmed becomes
  `Indeterminate`; the framework forbids blind retry.
- When a heartbeat endpoint is configured, restart remains paused until the
  control plane accepts the first heartbeat lease. Offline startup cannot
  bypass an existing control-plane emergency stop.
- A heartbeat endpoint requires the Agent's own independent credential. The
  agent sends it only in the `X-Api-Key` request header, never in the heartbeat
  payload or logs, and does not follow redirects that could forward it. The
  backend binds the credential to the registered Agent and WeChat instance;
  another Agent's identifier cannot be substituted in a request body.

## Commands

```powershell
dotnet run --project src/agent/WeChatBot.Agent -- --help
dotnet run --project src/agent/WeChatBot.Agent -- --self-check --dry-run --supported-version-prefixes=4.0.5 --required-automation-id-fingerprints="[structural:sha256=FULL_64_HEX_DIGEST]"
dotnet run --project src/agent/WeChatBot.Agent -- --diagnose
$env:WECHATBOT_AGENT_CREDENTIAL = "read-from-a-secret-store"
dotnet run --project src/agent/WeChatBot.Agent -- --run --dry-run --supported-version-prefixes=4.0.5 --heartbeat-uri=https://control.example/api/agents/heartbeat --remark-task-lease-uri=https://control.example/api/agents
```

The same settings can be supplied with these environment variables:

```text
WECHATBOT_AGENT_DRY_RUN
WECHATBOT_AGENT_ID
WECHATBOT_AGENT_INSTANCE_ID
WECHATBOT_AGENT_STATE_DIRECTORY
WECHATBOT_AGENT_HEARTBEAT_URI
WECHATBOT_AGENT_REMARK_TASK_LEASE_URI
WECHATBOT_AGENT_CREDENTIAL
WECHATBOT_AGENT_SUPPORTED_VERSION_PREFIXES
WECHATBOT_AGENT_REQUIRED_AUTOMATION_ID_FINGERPRINTS
```

`--agent-credential=value` is also supported, but the environment variable is
preferred because command-line arguments may be visible to local process
inspection tools. The deprecated `--control-plane-api-key` and
`WECHATBOT_AGENT_CONTROL_PLANE_API_KEY` names remain migration aliases. The
credential is required only when a heartbeat URI is set.

Do not add a version to the compatibility allow-list until its title and UIA
signature have passed the controlled compatibility test. Use `--diagnose` to
collect a redacted tree for that test. The fingerprint values can be copied
without exposing raw automation IDs. At least two distinct fingerprints are
required; a version allow-list without them cannot pass the safety gate.

## Integration surface

- `ObserveMentionsCommand` and `MentionObservation` define mention polling and
  observed-event contracts.
- `UpdateRemarkCommand` supports contact and group targets, including expected
  identity/current-value fields for optimistic conflict checks.
- `SerializedCommandExecutor` is the only command execution entry point for a
  WeChat instance. It provides a bounded queue, one active handler, validation,
  expiry, timeout, cancellation, durable idempotency, and indeterminate-state
  handling.
- Each command must match the executor's bound WeChat instance. A global named
  mutex prevents another Windows session from owning the same binding,
  and each binding has an isolated hashed SQLite WAL journal. Terminal entries
  use bounded retention and opportunistic batched pruning.
- `AgentHeartbeatPump` reports only operational metadata and pauses the runtime
  after repeated control-plane failures. A cleared emergency stop resumes an
  operator pause only when the same heartbeat is accepted and explicitly says
  `emergencyStop=false`. An unknown-UI pause remains closed until the full
  controlled environment/UI self-check passes again.
- An Agent credential may submit observed group events through
  `POST /api/agents/{agentId}/group-mentions`; the server requires a recent
  healthy dry-run heartbeat from the matching enabled Agent/WeChat binding.
  The Agent cannot list stored messages or use the administrative message
  endpoint.
- The backend exposes an atomic remote remark-task lease protocol through
  `POST /api/agents/{agentId}/remark-tasks/claim` plus per-task `renew`,
  `release`, and `complete` endpoints. It binds every transition to the Agent,
  WeChat instance, opaque lease-token hash, expiry, and task version, and uses
  a caller result ID for completion deduplication. Logical backups strip active
  leases, so restored pending tasks must be claimed again.
- Configuring `--remark-task-lease-uri` does not make the long-running Agent
  claim production tasks in this forced dry-run build. Repeatedly claiming and
  releasing the oldest pending task would starve later tasks and amplify
  database writes without producing a real completion. The explicit one-shot
  diagnostic path can still map a leased identity snapshot to the serialized
  `UpdateRemarkCommand`, perform the dry-run preview, and release that lease;
  it never sends a successful completion result.
- A claim or release request timeout is treated as a recoverable HTTP transport
  failure, while cancellation requested by the Agent host remains normal
  shutdown cancellation. Automatic production claiming must stay disabled
  until a separately reviewed live UI adapter can produce a verified result;
  `dry-run=false` remains rejected by this build.
- During shutdown, the host first cancels and waits for both the heartbeat and
  remark-task lease pumps, even if one pump has already failed, and only then
  stops the serialized executor. This prevents a lease pump from claiming or
  enqueueing work after executor shutdown has started.
- The backend issues a high-entropy credential per registered Agent, stores only
  its SHA-256 digest, and supports one-time issue, versioned rotation, and
  immediate revocation. Logical backup schema v5 excludes all credential
  material and restores registrations in a revoked state, so an administrator
  must reissue a credential before the Agent can heartbeat again.

No game package functionality is included.

## Test

```powershell
dotnet test tests/agent/WeChatBot.Agent.Tests/WeChatBot.Agent.Tests.csproj
```
