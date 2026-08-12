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
- A heartbeat endpoint also requires a control-plane API key. The agent sends
  it only in the `X-Api-Key` request header, never in the heartbeat payload or
  logs, and does not follow redirects that could forward the credential.

## Commands

```powershell
dotnet run --project src/agent/WeChatBot.Agent -- --help
dotnet run --project src/agent/WeChatBot.Agent -- --self-check --dry-run --supported-version-prefixes=4.0.5 --required-automation-id-fingerprints="[structural:sha256=FULL_64_HEX_DIGEST]"
dotnet run --project src/agent/WeChatBot.Agent -- --diagnose
$env:WECHATBOT_AGENT_CONTROL_PLANE_API_KEY = "read-from-a-secret-store"
dotnet run --project src/agent/WeChatBot.Agent -- --run --dry-run --supported-version-prefixes=4.0.5 --heartbeat-uri=https://control.example/api/agents/heartbeat
```

The same settings can be supplied with these environment variables:

```text
WECHATBOT_AGENT_DRY_RUN
WECHATBOT_AGENT_ID
WECHATBOT_AGENT_INSTANCE_ID
WECHATBOT_AGENT_STATE_DIRECTORY
WECHATBOT_AGENT_HEARTBEAT_URI
WECHATBOT_AGENT_CONTROL_PLANE_API_KEY
WECHATBOT_AGENT_SUPPORTED_VERSION_PREFIXES
WECHATBOT_AGENT_REQUIRED_AUTOMATION_ID_FINGERPRINTS
```

`--control-plane-api-key=value` is also supported, but the environment variable
is preferred because command-line arguments may be visible to local process
inspection tools. The API key is required only when a heartbeat URI is set.

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
- Remote remark-task claiming and completion are intentionally not exposed to
  Agent credentials in this baseline. `RemarkTask` does not yet contain a
  claim owner, opaque lease token, lease expiry, attempt count, or result
  deduplication identity. Those fields and an atomic claim/renew/complete
  protocol must be added in a migration before multiple agents can safely
  consume tasks. The existing administrative completion endpoint is not an
  Agent execution protocol.

No game package functionality is included.

## Test

```powershell
dotnet test tests/agent/WeChatBot.Agent.Tests/WeChatBot.Agent.Tests.csproj
```
