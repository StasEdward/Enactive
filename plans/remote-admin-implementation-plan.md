# Remote Gateway web administration implementation plan

Date: 2026-10-03

Status: stages 1–4 are installed by the operator. Stage 5 (persisted quotas and editor) is implemented and locally validated, awaiting operator installation. Stage 6 has local regression and Linux build validation; production upgrade, mutation/audit checks and recovery verification remain pending.

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

### Stage 3 — completed on 2026-10-04

- Added protected metadata-only overview, user/registration lists and user details. Explicit projections omit encrypted payloads, credentials and keys.
- Added primary-key cursor pagination (default 25, maximum 100), literal search, status filters, resource counts and last recorded computer contact. No database migration: schema remains 10.
- Added the read-only directory UI, safe text rendering, retryable errors, stale-response protection, detail cards, and desktop/mobile layouts.
- Added a separate administrator read budget so browsing does not consume the authentication/logout budget.
- Validation: full Gateway suite passed 657 tests before the final rate-limit change; the final targeted directory/authentication suite passed 37 tests. All 284 JavaScript tests passed. Both Chromium browser tests passed at 1280px and 390px with mocked API responses; directory deployment was subsequently confirmed by the operator.

### Stage 4 — completed on 2026-10-04

- Added web approval/refusal, account disable/enable and browser-session revocation through the shared administration service.
- Required administrator authorization, fresh MFA, CSRF and an expected record version. Rechecked session/administrator authority under locks inside the mutation transaction.
- Added migration 011 for registration revisions, complete provider-subject audit targets and JSON transition details. Both account and independent administrator audits commit with the action; independent history survives user deletion.
- Added explicit confirmation, reauthentication guidance, conflict handling and no automatic retry after uncertain outcomes. Explained existing-account refusal, withdrawn commands and work already running locally.
- Added regression coverage for migration from schema 10, preserved administrator sessions, audit failure rollback, concurrent decisions, stale retries and before/after audit attribution. Browser tests cover desktop/mobile confirmations and successful changes; JavaScript tests cover cancellation, CSRF, conflicts, freshness and network failures.
- Validation: the final full Gateway run passed 669 tests with one test-only SQL collation failure. After changing that assertion to decode JSON in C#, all six access-service tests passed. Earlier full and targeted runs covered all mutation and migration paths; all 288 JavaScript tests, two Chromium desktop/mobile scenarios and ten restore-verification checks passed.
- The operator installed the access-management package and confirmed the user detail card with Disable account and Revoke sessions controls. Actual production mutations and their audit effects have not been demonstrated. Keycloak settings remain unchanged.

Next: finish production validation in stage 6; the operator confirmed that quotas work.

### Compatibility with the deployed schema 3 (2026-10-03)

- Merged current master `f81ccee6142d`, preserving sign-in redemption, host connection, and storage quota fixes.
- Added an upgrade regression using the actual schema 1/3 SQL and existing account/revocation/redemption records. Only migration 10 is applied; a repeated migration changes nothing.
- Updated backup verification to require administrator tables only for schema 10 or later. Added seven checks covering valid older backups and missing administrator tables, wired into the reusable Gateway CI workflow.
- Validation: 644 Gateway integration tests, 277 panel tests, and seven restore completeness checks passed. Tests used disposable local MySQL databases; production was not changed.
- The initial Linux package was a stage-2 preview. The operator subsequently confirmed real Keycloak administrator login through the tunnel. The new directory package remains a separate deployment.

## Deployment checkpoint — 2026-10-04

Historical checkpoint before quota implementation; see the stage-5 checkpoint below for the current handoff.

- Gateway host: `remoteenactive`; public origin `https://remote.enactive.dev`; admin origin `https://admin.enactive.dev`; loopback service `http://127.0.0.1:5099`; systemd unit `enactive-remote`.
- Keycloak runs in Docker on the separate `enactive.app` host. Authority: `https://auth.enactive.app/realms/enactive`; confidential client: `enactive-admin`; required MFA ACR: `2`. Client secrets remain on the servers and are not recorded here.
- The operator handles all server commands and file transfers. Do not execute production deployment remotely without a new instruction changing that arrangement.
- Installed access-management package was built from `890f02f`, with embedded schema 11 and protocol 2. Filename: `enactive-remote-admin-access-linux-x64.tar.gz`; SHA-256: `652bc38dd2e22524828ab737dd68711c1640ce19750bcdb4631e022fc73a524a`. Local package and checksum are under `work/remote-admin-access-release/` (ignored build outputs).
- Operator screenshots confirm a live administrator session, two active users, zero waiting registrations, populated resource counts and the Disable account / Revoke sessions buttons. They do not establish that production mutations, CSRF rejection or audit persistence have been exercised.
- The operator was instructed to keep `enactive-deploy.timer` stopped while preview builds are outside its tracked branch. No subsequent confirmation of its state was supplied. Re-enable only after the tracked branch contains the intended release and deployment prerequisites are checked.
- Migration 11 requires a pre-upgrade database/key backup. Older binaries reject the newer schema: rollback needs the matching database backup, not just a symlink change. The latest installation's backup output was not supplied. The installation/recovery procedure is in `Docs/REMOTE_ADMIN_ACCESS_UPGRADE.md`.
- Next implementation: quota defaults and per-user overrides in the database, effective-limit resolution and enforcement across instances, usage display, editing with fresh MFA/CSRF, atomic audit, and persistence/concurrency regression tests. Final release validation and deployment smoke checks remain stage 6.

## Stage 5 checkpoint — 2026-10-04

Implemented and installed; the operator confirmed that the quota interface works. Production enforcement and recovery checks remain below.

- Migration 012 adds a singleton shared-default row and per-user quota settings with independent revisions. Resets retain revisions; account deletion cascades user settings. Existing schemas 3, 10 and 11 upgrade without losing accounts, replay protection or administrator sessions.
- All seven resource quotas resolve user override → persisted default → startup fallback within the consuming transaction. Locking reads avoid stale transaction snapshots and work across Gateway instances. No process cache or restart is required.
- Protected defaults/user quota APIs and desktop/mobile editor show effective values, sources, current usage, overages and queues per computer. Reasons, before/after maps and the actor are audited atomically. Fresh MFA, CSRF, live administrator/session checks and optimistic revisions protect mutations, including inherited-default changes.
- Reductions preserve existing data. Live devices remain visible. Cancellation has at most one pending command per existing run; device-control commands keep a separate bounded budget. Terminal reports remain bounded and admitted once even after storage reductions; ordinary progress and new creation remain restricted.
- Connection admission follows persisted host quotas on the next connection. Connection counts remain per Gateway instance, as before; resource ceilings use the shared database.
- User quota settings are included in account exports and deleted with the account. Shared defaults and the independent administrative audit survive account deletion.
- Validation: full Gateway suite passed 691 tests with zero failures/skips; 296 JavaScript tests, both Chromium desktop/mobile scenarios and 18 restore-verification checks passed. The final queue presentation separates cancellation counts from ordinary commands and has an additional focused quota regression run. Linux package smoke checks report schema 12 and protocol 2.
- Package: `work/remote-quotas-release/enactive-remote-quotas-linux-x64.tar.gz` plus `.sha256` (SHA-256 `d609e738a2ca0d399c8ab473d31e6ebea2e55c75fa5517fbf0a3a90813b4163a`); installation and rollback are documented in `Docs/REMOTE_QUOTAS_UPGRADE.md`.
- Next: verify production audit, enforcement, post-upgrade backup/restore and deployment timer state using `Docs/REMOTE_QUOTAS_VALIDATION.md`.

## Stage 6 checkpoint — 2026-10-04

- Live public checks: public health returned 200; admin quota API returned 401 without authentication on the admin origin and 404 on the public origin; admin page returned 200 with `Cache-Control: no-store`.
- All 31 published panel manifest entries match the locally validated quota release, including the quota editor.
- Five backup/key-preservation checks passed locally, in addition to the 18 restore-verifier checks recorded above. These are regression tests, not evidence of a successful production restore.
- Production audit, enforcement and actual post-upgrade restore remain unverified: no server terminal is attached to this task. Operator commands are in `Docs/REMOTE_QUOTAS_VALIDATION.md`.
- Local `origin/master` does not contain quota commit `68399fc`; this is not a fresh remote fetch. Inspect the configured deployment branch and its successful build before enabling automatic deployment.

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
