# Remote Gateway web administration implementation plan

Date: 2026-10-03

Status: stages 1 and 2 are implemented and tested locally. Stages 3–6 remain pending. The administrative login surface is disabled unless explicitly configured; user management and quota endpoints are not exposed yet.

## Objective

Provide a web administration interface for the Remote Gateway so operators can review registration requests, manage accounts, and inspect and change quotas. Preserve the existing E2E trust boundary and retain the CLI for bootstrap, recovery, and emergency administration.

## Existing foundation

- `src/Enactive.Remote.Gateway/Accounts/AdminCommands.cs` already implements registration approval/refusal, account disable/enable, and session revocation.
- `Accounts/AccountService.cs` distinguishes admission of a new external identity from access by an existing account. Identities are keyed by provider and stable subject, not display name or email.
- `Services/Limits.cs` defines seven shared quotas, currently configured when the Gateway starts.
- Existing quota checks use account-row locking and transactions to serialize resource creation.
- The public panel already uses HTML/CSS and JavaScript modules. Reuse this stack with a separate admin entry point.

Issue status supplied by the project owner: [#8](https://github.com/StasEdward/Enactive/issues/8), [#9](https://github.com/StasEdward/Enactive/issues/9), and [#10](https://github.com/StasEdward/Enactive/issues/10) are closed. They are not outstanding release blockers for this plan. Preserve their regression coverage when changing authentication, sessions, connections, or command delivery.

Storage-quota behavior discussed in [#7](https://github.com/StasEdward/Enactive/issues/7) still needs to be reconciled with the quota design before the interface describes storage limits as hard guarantees. Recheck its resolution when implementation begins.

## First-release interface

| Screen | Information and actions |
| --- | --- |
| Overview | Waiting registrations, active and disabled accounts, connected computers, storage usage, and accounts exceeding quotas. |
| Registration requests | Provider, stable subject, display name, request time, and admission state; approve or refuse a request. |
| Users | Search, filters, pagination, account status, creation time, last activity where available, and resource usage. |
| User details | External identities, available computer/device metadata, sessions, quotas, and audit history; disable, enable, or revoke sessions. |
| Quotas | Shared defaults, per-user overrides, effective values, current usage, and reset-to-default actions. |
| Administrative audit | Administrator, timestamp, action, target, previous/new values where appropriate, and reason. |

Approval permits account creation at the person's next sign-in. Refusing admission does not disable an account that already exists. The interface must explain this distinction and direct the operator to the appropriate action.

Actions that affect access must show their scope and consequences before submission. Disabling an account stops Gateway access; it does not guarantee that work already running on a local computer stops.

## Administrative access and E2E boundary

Use a dedicated origin, such as `admin.example.com`, served by the same Gateway deployment. Route the admin interface and API only through the configured administrative host. Keep administrator cookies and sessions separate from ordinary user sessions; do not share cookies across subdomains.

- Bootstrap the first administrator through the CLI using an exact provider/subject identity.
- Do not provide public administrator registration or automatically promote the first registered user.
- Start with one Administrator role. Keep administrator assignment and recovery in the CLI for the first release.
- Require MFA, short-lived administrative sessions, and fresh authentication for sensitive operations. Ordinary GitHub/Google OAuth sign-in alone is not proof that MFA occurred. Select and document a verifiable MFA mechanism before implementing the login flow.
- Enforce authorization on every administrative endpoint, independently of whether navigation or controls are visible.
- Recheck administrator eligibility server-side so removing access invalidates existing sessions.
- Protect state-changing requests against CSRF; use secure cookies, restrictive browser policies, and request limits.
- Render external display names and other untrusted text safely; do not insert them as HTML.
- Administrative access must not depend on enrolling a user's E2E browser device.

Administrators may inspect account metadata, connection state, resource counts, and encrypted-storage sizes. The API must not expose task text, results, files, secret tokens, or decryption keys. User impersonation and signing commands on a user's behalf are outside the first-release scope.

Reference guidance: [ASP.NET Core authorization policies](https://learn.microsoft.com/en-us/aspnet/core/security/authorization/policies?view=aspnetcore-10.0), [CSRF protection](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0), and [OWASP MFA guidance](https://cheatsheetseries.owasp.org/cheatsheets/Multifactor_Authentication_Cheat_Sheet.html).

## Quota model and behavior

Retain the existing categories:

| Quota | Scope |
| --- | --- |
| Hosts | Per user |
| Devices | Per user |
| Active runs | Per user |
| Queued commands | Per host |
| Created tasks | Per user over the preceding 24 hours, matching the current rolling window |
| Open invitations | Per user |
| Sealed storage bytes | Per user |

For each quota, show usage, effective limit, and the source of that limit. Show queue usage per host rather than comparing an account-wide total against a per-host limit.

Resolve limits in this order: per-user override, shared default stored in the database, startup configuration fallback. A missing override means inheritance. Reset removes the override; it does not copy the current default into the user record. Validate values explicitly and do not overload zero to mean both blocked and unlimited.

Required behavior:

- Changes take effect without restarting the Gateway and are consistent across instances. Avoid indefinitely caching effective limits in the existing singleton configuration.
- Resolve and enforce the applicable limit within the resource-creation transaction, following the established lock order.
- Lowering a limit below existing usage preserves data and existing resources. Display the overage and restrict new resource creation.
- Cancellation, access revocation, cleanup, and terminal run reporting remain possible when an account is over quota.
- Detect concurrent administrative edits through a revision or equivalent optimistic concurrency mechanism; do not silently overwrite another operator's changes.
- Record old/new values and the operator's reason atomically with changes.
- Explain that sealed-storage accounting is not the total physical size of the database, indexes, backups, or logs.

Define bounded handling of host-generated output before presenting storage as a hard ceiling. Simply refusing every event after the account fills can prevent completion from being reported and leave the host outbox blocked. The resolution of issue #7 must determine the enforcement policy and its tests.

## Backend and persistence changes

Extract a shared `AdministrationService` from `AdminCommands`. Both CLI and HTTP endpoints should call it, preserving existing transaction boundaries, lock ordering, and account/session semantics. Do not invoke the CLI as a subprocess from web requests.

Add persisted administrative identities/sessions, shared quota defaults, per-user quota overrides, and an explicit effective-quota service used by all relevant write paths.

Introduce a separate `/admin/api` endpoint group with its own authentication and authorization requirements. Keep user-device filters scoped to the ordinary API rather than bypassing them globally to accommodate administration. Use explicit response models that contain only permitted administrative metadata.

Extend auditing to identify the actual administrator instead of the generic `operator` actor. Specify audit retention and personal-data deletion behavior: the current user audit cascades on account deletion, so it cannot automatically serve as an independent operator history. Avoid storing secrets or encrypted task payloads in audit details.

Use additive, rerunnable migrations. Keep environment configuration as a fallback for existing deployments and retain CLI access for recovery. Administrative list queries must be paginated and indexed; overview counts must not load task payloads or all user records into memory.

## Implementation sequence

| Stage | Deliverable | Acceptance condition |
| --- | --- | --- |
| 1. Shared administration service | Move existing operations behind a service consumed by the CLI. | Existing admission, disable/enable, session-revocation, and audit tests pass without behavior changes. |
| 2. Administrator access | CLI bootstrap/recovery, verified MFA, administrative sessions, host routing, authorization, and audit identity. | Ordinary users and revoked administrators cannot access any admin endpoint; sensitive changes require the intended authentication strength. |
| 3. Read-only interface | Registration list, user list/details, overview, search, filters, and pagination. | Metadata is correct and bounded; encrypted content and secrets are absent from responses. |
| 4. Access management | Approval/refusal, disable/enable, and session revocation. | Changes and audit commit together; repeated and concurrent requests have safe, explicit outcomes. |
| 5. Quotas | Migrations, defaults/overrides, effective-limit enforcement, usage views, and editor. | Limits survive restart, apply across instances, and remain correct under concurrent operations and quota reductions. |
| 6. Release validation | End-to-end tests, migration checks, deployment configuration, and operator documentation. | An existing deployment upgrades successfully; recovery is documented and security regression coverage remains passing. |

Stages 1 and 2 establish the shared behavior and security boundary before administrative mutations are exposed. The quota editor ships only with the corresponding server-side enforcement.

## Implementation progress

### Stage 1 — completed on 2026-10-03

- Added `Accounts/AdministrationService.cs` with structured results for waiting registrations, admission decisions, account disable/enable, and session revocation.
- Converted `Accounts/AdminCommands.cs` to a parsing/presentation adapter over that service. CLI commands, exit codes, messages, and terminal sanitization are preserved.
- Moved stable-identity validation into `Accounts/AdmissionIdentity.cs`, shared independently of the CLI. Admission decisions reject Waiting and undefined states before any database write.
- Preserved account/host/command lock ordering and transactional audit. Disabling still withdraws only undelivered commands; enabling does not revive old sessions.
- Added direct service integration tests, including a database-trigger fault that proves an audit failure rolls back account state, session revocation, and command withdrawal together. Added identity validation tests for newline/Unicode rejection and database-width boundaries.
- Removed the now-unused `AdminCommands.Actor` alias; the service retains the existing CLI `operator` audit identity until stage 2 introduces authenticated administrator identities.
- Validation: isolated build succeeded; the full Gateway suite passed 558 tests. After final identity tests and cleanup, the focused identity/service/account/audit suite passed 66 tests. No tests were skipped. Results are in `work/remote-admin-build/test-results/`.
- The running local Gateway was not restarted. Builds use `work/remote-admin-build/`; integration tests use disposable `enactive_test_*` databases.

### Stage 2 — completed on 2026-10-03

- Chosen MFA mechanism: a dedicated confidential OIDC client with a trusted provider that enforces one configured authentication context. Validate the signed `acr` and recent `auth_time`, in addition to the handler's signature, issuer, audience, nonce, state, and PKCE checks. Password-only OAuth has no fallback into administration. Actual provider configuration remains a deployment prerequisite.
- Added CLI `admin administrators grant <issuer> <subject>` and `revoke`. Exact issuer/subject identity is independent of ordinary accounts. Re-grant acts as recovery, increments the security version, and invalidates prior sessions.
- Added migration 010 for administrators, administrative sessions, and their security audit. Migration version 2 remains deliberately unknown because it identifies the incompatible protocol-1 database. Migration and legacy-schema regression tests cover this boundary.
- Added a separate HTTPS hostname gate, `__Host-Enactive.Admin` secure cookie, 30-minute absolute sessions, per-request database eligibility checks, five-minute fresh-authentication policy, CSRF-protected sign-out, and callback throttling before authentication. The admin hostname does not serve the ordinary panel, and the public hostname does not serve admin paths.
- Added a minimal login/session page under `/admin/`. It reads authoritative session state, confirms sign-out on the server, and displays failures without inserting external text as HTML. User lists and mutations are intentionally not present at this stage.
- Grants, revocations, session creation, and sign-out are audited transactionally. The hourly retention pass removes expired sessions and administrator audit older than 90 days. Audit-failure rollback and retention have integration tests.
- Added `Docs/REMOTE_ADMINISTRATION.md` covering provider requirements, environment settings, proxy routing, bootstrap, recovery, cookies, migration/rollback, and audit retention.
- Validation: isolated build passed without warnings; full Gateway suite passed 610 tests, with no skips. After adding the final callback-rate regression, all 23 administrative authentication tests passed. All 277 JavaScript panel tests passed, including four new administrative UI tests. Reports remain in `work/remote-admin-build/test-results/`.
- The running Gateway was not restarted, no deployment configuration was changed, and no real administrator was granted access. Integration tests used disposable databases and an in-process signed OIDC provider.

Next: stage 3, the read-only registration/user interface with bounded queries, search, filters, pagination, and metadata-only responses. The current shared waiting-list query preserves the CLI's full listing; add bounded pagination before exposing the web list. Stage 4 mutations must require the administrator/fresh policies themselves and carry the authenticated actor through their transactional audit.

## Test and release criteria

Each behavior change needs a regression test that fails without it. Explain non-obvious limits and rules in the code, following the repository's agent instructions.

- Authorization: unauthenticated users, ordinary accounts, host tokens, revoked administrators, and stale admin sessions are denied appropriately.
- Authentication: MFA and fresh-authentication requirements are verified, including recovery and session revocation.
- Browser boundary: cross-origin requests, missing/invalid CSRF tokens, unsafe display names, and public-host access to admin routes are covered.
- Admission: approval/refusal is idempotent where appropriate; concurrent decisions and sign-in have defined outcomes; existing-account refusal remains distinct from disablement.
- Account management: disablement and revocation preserve transaction semantics; the interface never promises to stop already-running local work.
- Quotas: inheritance, overrides, reset, validation, persistence, concurrent creation, concurrent edits, reductions below usage, and multiple Gateway instances are covered.
- Storage: host output and terminal-event handling follow the resolved storage policy without hiding overages or blocking necessary control operations.
- Audit: successful changes have corresponding records; rolled-back changes do not appear as committed; retention and deletion behavior are tested.
- E2E: administrative responses and logs exclude encrypted task bodies, plaintext content, credentials, and key material.
- UI: success appears only after server confirmation; failures preserve form input and show actionable errors; saving and reopening reproduces effective settings.
- Deployment: migrations can be rerun after partial failure; existing data is preserved; administrative bootstrap and emergency CLI access remain available.
- Regression: preserve the fixes and tests associated with closed issues #8, #9, and #10.

Before enabling the interface publicly, document the chosen MFA mechanism, administrative origin/cookie configuration, storage-overage behavior, and administrative audit retention policy.
