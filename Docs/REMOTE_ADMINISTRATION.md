# Remote Gateway administration

Stages 2–4 provide administrator bootstrap, authentication, sessions, a directory with user details,
registration decisions, account enable/disable and session revocation. Quota editing is a later stage.
Existing account-management CLI commands remain available. Administration is disabled when all
`ENACTIVE_ADMIN_*` settings below are absent. Partial configuration stops startup.

## Identity provider and MFA

Use a separate confidential OpenID Connect client with authorization code flow, PKCE, HTTPS discovery,
and `form_post` responses. Register exactly `https://admin.example.com/admin/auth/callback` as its callback.
Replace the example hostname with the dedicated administrative hostname.

The provider must enforce an MFA authentication context and issue its exact identifier in the signed ID
token's `acr` claim. It must also issue `auth_time`, the time of actual authentication. Configure the client
and the provider's authentication policy so this context cannot be achieved through password-only login,
a weak fallback, or a self-asserted user attribute. Check the provider's documentation and verify both a
successful MFA login and a refused password-only login before enabling access. An arbitrary string named
"mfa" does not make an authentication context strong. Ordinary GitHub OAuth does not supply this evidence.

Gateway requests `prompt=login`, `max_age=0`, and the configured `acr_values`. It checks the signed context
and authentication time independently of those requests. Missing or different context, missing time,
authentication more than five minutes old, or a time more than 60 seconds in the future is refused.
The OIDC handler also validates state, correlation, signature, issuer, audience, expiry, and nonce before
any administrative session is created. No user-info response, email, display name, role claim, or ordinary
user cookie grants administrative access.

References: [OpenID Connect Core](https://openid.net/specs/openid-connect-core-1_0.html) defines `acr`,
`auth_time`, and `max_age`; [ASP.NET Core MFA guidance](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/mfa?view=aspnetcore-10.0)
describes authentication-strength requirements.

## Configuration and routing

Set these in the Gateway service environment, using your provider's actual values:

| Setting | Value |
| --- | --- |
| `ENACTIVE_ADMIN_ORIGIN` | `https://admin.example.com` (origin only, no path) |
| `ENACTIVE_ADMIN_AUTHORITY` | HTTPS discovery authority for the trusted tenant/issuer |
| `ENACTIVE_ADMIN_CLIENT_ID` | Dedicated OIDC client identifier |
| `ENACTIVE_ADMIN_CLIENT_SECRET` | Confidential client secret, stored with other service secrets |
| `ENACTIVE_ADMIN_MFA_ACR` | One exact, provider-enforced MFA authentication context |

Use a hostname different from `ENACTIVE_PUBLIC_ORIGIN`; a different port alone is insufficient because
cookies are shared across ports. Restrict the administrative hostname at the tunnel or reverse proxy as
appropriate for operators. Route it to the same Gateway, preserving the administrative Host header and
HTTPS scheme. Existing trusted-proxy/tunnel rules still apply; do not trust forwarded headers from
arbitrary clients. Never point the authority at an untrusted issuer: that issuer controls identity evidence.

Open `/admin/` on the administrative origin. Other Gateway paths on that origin return 404, except
`/health`. Administrative paths return 404 on the public origin and when administration is disabled.
Assets and API responses use `Cache-Control: no-store`. The release asset manifest still lists all shipped
assets; fetch `/admin/` assets from the administrative origin when comparing their hashes.

The cookie is `__Host-Enactive.Admin`, host-only, Secure, HttpOnly, SameSite=Lax, and Path=/.
Administrative sessions last 30 minutes without sliding renewal. Every authenticated request checks the
database for enabled access, session revocation, security version, and expiry. Sensitive operations must
require the fresh-administrator policy, whose window is five minutes. `/admin/api/fresh` only reports this
condition; its success never authorizes a later mutation by itself. Changes use antiforgery validation.

Persist and protect the Gateway Data Protection keys as for ordinary sessions. Multiple Gateway
instances need the same database and compatible shared key configuration. Configure proxy and service
logging to exclude cookies, authorization codes, tokens, and callback bodies. Do not enable authentication
debug logging or override the existing request/authentication logging filters.

## Bootstrap and recovery

1. Back up the database and install the new Gateway. Its startup applies migrations through 011. Version 3 and its sign-in replay protection are preserved. Version 2
   remains unused so the incompatible protocol-1 schema version 2 stays rejected. Reapplying migration
   010 after a partial migration is safe. Older binaries do not recognize the newer schema; rollback requires
   the corresponding database backup, not just swapping binaries.
2. Obtain the intended administrator's **exact signed `iss` and `sub`** for this OIDC client from the
   provider's trusted administration tools. These are case-sensitive. Pairwise subjects can change when
   the client changes; an email address is not a substitute.
3. On the server, with `ENACTIVE_REMOTE_DB` set, run:

   ```text
   Enactive.Remote.Gateway admin administrators grant "https://issuer.example.com/tenant" "exact-subject"
   ```

4. Configure the five settings, restart the service, and complete MFA at `/admin/`.

For emergency revocation, including a compromised session:

```text
Enactive.Remote.Gateway admin administrators revoke "https://issuer.example.com/tenant" "exact-subject"
```

Revocation disables the administrator and ends their sessions. Re-granting the same identity is the
recovery operation: it increments the security version and ends all previous sessions rather than
reviving them. The person must authenticate again with MFA. Recover compromised provider credentials
and MFA enrollment at the provider first. There is no web bootstrap, self-registration, first-user
promotion, or browser-based bypass for a lost second factor. The CLI needs database access, not an
available identity provider. No deployment settings or actual administrator grants are supplied by tests.

## Audit and retention

Grants, revocations, session creation, and sign-out write `administrator_audit` in the same transaction
as their database changes. CLI actor is `operator`; browser actor is `admin:<administrator-id>`.
The security log contains action names, internal IDs, registration provider/subject targets and bounded
state-transition details, never tokens, IP addresses, encrypted task bodies or keys. It is independent of ordinary user deletion.

The existing hourly retention job removes expired administrative sessions and audit older than 90 days,
including while the web interface is disabled. Expired or revoked sessions are denied immediately on
their next request; cleanup timing does not determine access. Requests already authorized before a
revocation commits may finish. Web mutations lock and recheck administrator/session authority inside their transaction before
changing the target record, using the same lock order as administrator revocation.

## Read-only directory (stage 3)

The signed-in page offers users and registrations, literal name search or exact ID/subject search,
status filters, 25-row pages and account details. Registrations initially show waiting requests.
Search/filter inputs remain available for retry after errors. Results and details are cleared on
sign-out; late requests cannot restore them or replace a newer search.

Authenticated endpoints on the administrative origin:

- `GET /admin/api/overview`: user, disabled-user and waiting-registration counts.
- `GET /admin/api/users`: `search`, `state` (`Active`/`Disabled`), `after`, `size`.
- `GET /admin/api/registrations`: `search`, `state` (`Waiting`/`Approved`/`Refused`), `after`, `size`.
- `GET /admin/api/users/{id}`: account metadata, resource counts and last recorded computer contact.

Lists return `items` and a nullable `next` cursor. Pass `next` unchanged as URL-encoded `after`,
keeping filters unchanged. Pages use existing primary-key indexes (user ID or provider/subject),
not chronological ordering or offsets. Refresh starts again; pages are live views, not a snapshot.
Maximum page size and search length are both 100. Literal substring search and aggregate overview
counts may scan metadata; neither reads encrypted bodies. Last contact is not a live connectivity claim.
Read endpoints share a separate 60-requests/minute budget per administrator so browsing cannot exhaust
the sign-in/sign-out budget. Stage 3 itself used schema 10 without new settings; stage 4 adds migration 11.

Responses deliberately omit credentials, session identifiers, tasks' encrypted content and keys.
Stored bytes describe sealed-content accounting rather than total database disk usage. Resource counts
include retained records; they are not yet effective quota usage or editable limits.

Validation commands: the Gateway test project; `node --test "tests/panel/*.test.mjs"`; and, from
`tests/panel-e2e`, `npx playwright test --config admin.config.mjs`. The latter uses the shipped UI
with mocked API responses at desktop and mobile widths; signed OIDC and real database authorization
are covered by the Gateway suite.

## Access management (stage 4)

The user card offers Disable account / Enable account and Revoke sessions. Registration rows offer
Approve and Refuse. Each action opens a confirmation naming the target and its consequences.
Success is shown only after the server confirms the change; the directory is then refreshed.

- `POST /admin/api/users/{id}/access`: JSON `{ "action": "disable|enable|revoke-sessions", "expectedVersion": 1 }`.
- `POST /admin/api/registrations/decision`: JSON `{ "provider": "github", "subject": "123", "decision": "approve|refuse", "expectedVersion": 0 }`.

Supply `expectedVersion` from the latest directory record's `version`, and the session's CSRF token
in `X-CSRF-TOKEN`. The server derives the actor from its authenticated administrator session, never
from request JSON. Each endpoint requires fresh MFA (five minutes); the transaction rechecks the
persisted session, administrator access and freshness while holding locks. A 403 asks the operator
to authenticate again and review the action. Authentication never automatically replays a mutation.

A stale record returns 409 without effects or audit. Refresh and review it before another attempt.
This also prevents a retried session revocation from ending new sessions opened after its first
successful execution. Enabling an already active account or disabling an already disabled one at
its current version is a no-op. Conflicting decisions serialize on the target row; only one request
with a given revision succeeds. CLI and open-admission decisions update registration revisions too.
After a network failure, the outcome may be unknown: refresh rather than automatically retrying.

Disable revokes browser sessions and withdraws pending-delivery commands. It does not promise to
stop work already running locally. Enable does not revive revoked sessions or withdrawn commands.
Session revocation does not revoke computer credentials. Refusing registration does not disable an
existing account. These controls affect ordinary users, not Keycloak or administrator assignments.

The shared CLI/web service writes the ordinary audit and independent administrator audit in the same
transaction as the change. Independent history records the actual actor, full target, before/after
states and withdrawn-command count where applicable; it survives ordinary account deletion and follows
the existing 90-day retention policy. A failed audit aborts the entire transaction.

Migration 011 adds admission revisions, widens the administrator audit target for provider subjects,
and adds JSON audit detail. It can be reapplied after partial execution. Existing accounts, administrator
sessions and audit rows are retained. See `REMOTE_ADMIN_ACCESS_UPGRADE.md` before installation.
