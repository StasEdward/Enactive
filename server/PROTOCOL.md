# Host protocol v1

## Authentication and transport

SignalR JSON endpoint: `/hubs/host`. Device authentication uses `Authorization: Bearer <token>` on negotiate and WebSocket requests. Query-string tokens are deliberately not accepted. Use TLS outside local development. Credentials come from the owner-only `POST /api/hosts` flow and are not interchangeable with owner session cookies.

The authenticated identity determines HostId. A Host cannot acknowledge another Host's command or publish events for another Host's run. Revocation aborts open connections; every hub method additionally checks that the device is still enabled.

## Host methods

### `Sync(workspaces)` → commands

Call on connection, then every 15 seconds. `workspaces` is a complete list:

```json
[{ "id": "stable-workspace-id", "name": "Enactive" }]
```

IDs should be stable and refer to Host-local configuration. The server does not receive or choose the actual root path. The Host is considered offline after 45 seconds without Sync.

Returns pending commands. `payload` is a JSON string; parse it using the command's `kind`. Each command includes `id`, `hostId`, `kind`, `payload`, `status`, `createdAt`, `expiresAt`. `fingerprint` is server deduplication metadata, not a signature.

| Kind | Payload |
|---|---|
| `StartTask` | `runId`, `task` with id, hostId, workspaceId, title, prompt, createdAt |
| `CancelRun` | `runId` |
| `ResolveApproval` | `approvalId`, `runId`, `toolCallId`, `actionHash`, `decision` (`allow` / `deny`) |

Delivery is at least once. Persist a command locally before acknowledging it. Deduplicate by CommandId, including across Host restarts. Do not execute expired commands. The server stops re-delivering accepted commands, so a Host must retain its accepted queue itself. Expired undelivered starts become Incomplete when Host next syncs.

### `Acknowledge(commandId)` → true

Means **durably accepted by Host**, not executed. An identical acknowledgement is harmless. Expired, rejected and other-Host commands are refused.

### `Publish(event)` → true

Host persists outgoing events before delivery and retries with the same EventId if the reply is lost. The server deduplicates events by HostId + EventId. The Host must never reuse an EventId for different content.

```json
{
  "eventId": "unique-event-id",
  "runId": "run-id-from-start-command",
  "kind": "Running",
  "detail": "Planning the task"
}
```

Kinds: `Running`, `Progress`, `ApprovalRequested`, `ApprovalResolved`, `Completed`, `Failed`, `Incomplete`, `Cancelled`, `Interrupted`.

- `Running`: queued run begins. If cancellation was already requested, reconcile it before starting; do not simply ignore CancelRun.
- `Progress`: human-readable timeline entry, without changing lifecycle state.
- Terminal kinds report the actual result. `Completed` cannot be emitted directly for a queued run. A terminal run cannot be reopened by a later event.
- `CancelRequested` comes from the owner's cancellation request; Host reports `Cancelled` only after stopping. External effects may already have happened.

## Approval events

```json
{
  "eventId": "unique-event-id",
  "runId": "run-id",
  "kind": "ApprovalRequested",
  "detail": "Run the project test suite",
  "approvalId": "unique-approval-id",
  "toolCallId": "exact-tool-call-id",
  "tool": "run_command",
  "arguments": "dotnet test Enactive.sln",
  "workingDirectory": "C:/work/Enactive",
  "actionHash": "sha256-of-the-complete-bound-action"
}
```

`arguments` is the complete human-reviewable action, not a shortened description. Host computes the hash from an unambiguous representation of RunId, ToolCallId, tool, exact arguments and working directory. Define that canonical representation in the Host implementation; the gateway treats it as an opaque identity and checks equality.

An owner answer moves the server projection from Pending to DecisionQueued and creates ResolveApproval. It does not mark the action Allowed. On receiving a decision, Host checks the local request is still pending, the bound action matches, the request has not expired, the run is not cancelled and the policy still permits approval. It then reports:

```json
{
  "eventId": "unique-event-id",
  "runId": "run-id",
  "kind": "ApprovalResolved",
  "approvalId": "unique-approval-id",
  "actionHash": "same-action-hash",
  "detail": "Allowed"
}
```

Allowed outcomes: `Allowed`, `Denied`, `Expired`, `Invalidated`. Host also publishes local desktop decisions this way, so a queued remote answer can lose to an earlier local decision. An old queued command may still be delivered: the Host must reject it against the local resolved request. Delivery of a command is not authorization to bypass that check.

Pending approvals have a server-side 24-hour window in this preview. Host should enforce its own equal or stricter timeout and publish Expired. Server refuses late owner answers; a local decision still belongs to the authoritative Host. Final run events invalidate outstanding server approval projections.

## Owner HTTP API

Call `GET /api/session` for authentication state and an anti-forgery token. POST requests require that token in `X-CSRF-TOKEN` and the same browser cookies. After login fetch a new token. All state-changing APIs validate CSRF, including login/logout.

| Method and route | Body / purpose |
|---|---|
| `POST /api/login` | `{ "key": "configured-owner-key" }` |
| `POST /api/logout` | `{}` |
| `GET /api/state` | Safe projection (no device secrets or hashes) |
| `POST /api/hosts` | `{ "name": "Studio PC" }`; returns id and one-time token |
| `POST /api/hosts/{id}/revoke` | `{}` |
| `POST /api/tasks` | hostId, workspaceId, title, prompt; creates draft |
| `POST /api/tasks/{id}/start` | `{ "commandId": "uuid" }` |
| `POST /api/runs/{id}/cancel` | `{ "commandId": "uuid" }` |
| `POST /api/approvals/{id}/resolve` | commandId, decision, actionHash |
| `POST /api/notices/read` | `{}` |

Start, cancel and resolve deduplicate CommandId. Reusing it for a different operation returns 409. Task creation itself is not yet idempotent; after an ambiguous create response refresh the task list before retrying. Errors use 400 for invalid input, 401 for unauthenticated requests, 404 for unavailable resources, 409 for state conflicts and 429 for login rate limiting.

`GET /health` is public and reports protocolVersion 1. There are no endpoints for arbitrary file reads, terminal commands or simulated execution.
