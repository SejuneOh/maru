# API contract (local skeleton)

Base URL: `http://localhost:5080`. All JSON is camelCase. Times are ISO 8601 UTC strings.

## Identity

Logged-in requests send `Authorization: Bearer <Supabase access token>`. When the person belongs to
several tenants they also send `X-Tenant: <tenantId>`.

- The API validates signature, issuer (`{SupabaseUrl}/auth/v1`), audience `authenticated` and
  lifetime. Keys come from `{SupabaseUrl}/auth/v1/.well-known/jwks.json`, or from the shared secret
  when `Auth:Supabase:JwtSecret` is set (legacy HS256 projects). Accepted algorithms: RS256 and ES256 with JWKS, HS256 with the secret. Outside Development `Auth:Supabase:Url` must be https. A user with no GitHub identity is remembered for 30 seconds (at most 1000 users) before the Admin API is asked again.
- Token `sub` is the Supabase auth user id. On first sight the API calls the Supabase Admin API
  (`GET /auth/v1/admin/users/{sub}`, service role key), takes the `github` identity's provider id and
  stores `auth_user_id -> github_user_id` in `auth_links` (global table, `workboard_sync` only).
  Login, display name and avatar (`user_name`/`preferred_username`, `full_name`/`name`, `avatar_url`) are read
  from the same identity's `identity_data`, only when a tenant is created. `user_metadata` is never used. Members are found by `github_user_id` across all tenants.
- One membership: `X-Tenant` is optional and ignored.
- Several memberships: `X-Tenant` must equal one of them.

Errors, JSON `{"error": "<code>"}`:

| Status | Code | When |
| --- | --- | --- |
| 401 | `unauthenticated` | no token, bad signature, wrong issuer or audience, expired, no GitHub identity |
| 401 | `tenant_required` | several memberships and `X-Tenant` missing or not one of them. Body also has `tenants`: `[{"id", "name"}]`, only the caller's own memberships |
| 403 | `no_membership` | valid login with a GitHub identity, but no membership in any tenant |
| 503 | `identity_unavailable` | the Supabase Admin API failed or timed out while looking up the caller's GitHub identity (any endpoint that needs it; the client may retry, the session is still valid) |

Write endpoints (`POST`, `PATCH`, `PUT`, `DELETE`) use these standard codes, always as `{"error": "<code>"}`:

| Status | Code | When |
| --- | --- | --- |
| 400 | `invalid_request` | body missing, not JSON, or a field fails validation (a more specific code may be documented per endpoint) |
| 403 | `forbidden` | the caller may not change this resource |
| 403 | `admin_only` | the action needs the `admin` role |
| 404 | `not_found` | the target does not exist in the caller's tenant |
| 409 | `conflict` | the change clashes with the current state |
| 409 | `request_in_progress` | a request with the same `Idempotency-Key` is still running (writes that call GitHub only; a stuck one is retried after 2 minutes) |
| 409 | `idempotency_key_reused` | the `Idempotency-Key` was already used for a different request |
| 502 | `github_unavailable` | GitHub returned a 5xx or hit its rate limit, or the App token could not be obtained |
| 504 | `github_unavailable` | the GitHub call got no answer: network error, the client's own 30 second timeout, or the 60 second call limit |
| 403 | `github_write_not_granted` | the GitHub App lacks the permission the write needs (GitHub answered 403 with a permission message; a rate-limit or other 403 is `github_unavailable`) |
| 409 | `credential_missing` | a token connection has no stored token (token needed) |
| 409 | `credential_revoked` | a token connection's token was rejected by GitHub (401); replace it |
| 409 | `credential_invalid` | GitHub refuses a token connection's token for the organization (SAML SSO not authorized, owner not a member); replace it |
| 409 | `credential_expired` | a token connection's token has passed its expiry; replace it |
| 409 | `field_disabled` | priority or size is switched off for the tenant |
| 409 | `repository_archived` | the write would call GitHub on an archived repository |
| 422 | `assignee_not_collaborator` | GitHub rejected an assignee who cannot be assigned in that repository |
| 422 | `invalid_request` | GitHub rejected the request as invalid (a write that calls GitHub only; 400 is for the board's own validation) |

Idempotency: a write may send `Idempotency-Key: <1..200 chars>`. The key is scoped to the tenant. Repeating the
same request (same member, method, path with query and body) within 24 hours returns the stored status and body
without running again; a different request with the same key is `409 idempotency_key_reused`. Keys older than
24 hours are ignored and replaced on next use. Only 2xx responses are stored, except for writes that call GitHub: those reserve the key before the call and also
store a non-2xx response when GitHub may already have applied the change, so a retry never repeats it. Only a
failure known to happen before anything was sent releases the key. If the GitHub call times out (60 seconds, or the HTTP
client's own 30 second timeout), fails with a network error after the request was sent (all
`504 github_unavailable`), is aborted by the client, or the server dies, the key stays reserved: a retry gets
`409 request_in_progress` and after the 2 minute pending timeout it runs again, which can repeat the GitHub
side effect. A key longer than 200 characters is
`400 invalid_request`. Without the header the write just runs.

Every write also records who did it in `audit_log` and, for board-field changes, structured rows in
`issue_events`, in the same transaction as the change. Writes that call GitHub write these rows after GitHub answered.

Development only, when there is no `Authorization` header: `X-Dev-User: <githubLogin>` and optional
`X-Dev-Tenant: <tenantId>` still work (same tenant rules, unknown login or no membership is
`401 unauthenticated`). With an `Authorization` header the dev headers are ignored. Outside
Development they are always ignored.

CORS allows the `Authorization` and `X-Tenant` request headers.

## Tenant isolation

Every request that touches tenant data runs inside a transaction on the `workboard_app` role and
first calls `select set_config('app.tenant_id', @tenantId, true)`. RLS policies in
`db/init/001_schema.sql` then hide other tenants' rows. Never query tenant tables on the
`workboard_sync` role from a user request.

## Endpoints

| Method | Path | Auth | Returns |
| --- | --- | --- | --- |
| GET | `/api/health` | none | `{"status":"ok"}` |
| GET | `/api/dev/users` | none (Development only) | `DevUser[]`, one entry per membership |
| POST | `/api/tenants` | sign-in only, no membership needed | `201 CreateTenantResult`, see below |
| GET | `/api/me` | dev user | `Me` |
| GET | `/api/members` | dev user | `Member[]`, current members only (`removedAt` is set for people who left the connected organization) |
| DELETE | `/api/members/{id}` | dev user, admin | `204`, see "Invitations and removal" |
| PATCH | `/api/members/{id}` | dev user, admin | `Member`, role change, see "Invitations and removal" |
| GET | `/api/invitations` | dev user, admin | `Invitation[]`, open (not accepted or revoked) newest first, expired included |
| POST | `/api/invitations` | dev user, admin | `201 CreatedInvitation` |
| DELETE | `/api/invitations/{id}` | dev user, admin | `204` |
| POST | `/api/invitations/preview` | sign-in only, no membership needed | `InvitationPreview` |
| POST | `/api/invitations/accept` | sign-in only, no membership needed | `JoinedTenant` |
| POST | `/api/github/webhook` | HMAC signature | see "GitHub webhook" |
| GET | `/api/projects` | dev user | `Project[]` |
| GET | `/api/projects/done-by-week` | dev user | `DoneByWeek`, see below |
| GET | `/api/projects/{id}/schedule` | dev user | `ProjectSchedule` or 404, see below |
| GET | `/api/connections` | dev user | `GithubConnection[]`, active first |
| POST | `/api/connections/github` | dev user, admin | `GithubConnection` (`201` new, `200` existing updated) |
| POST | `/api/connections/github-token` | dev user, admin | `GithubConnection` (`201`), see "Token connections" |
| PUT | `/api/connections/{id}/github-token` | dev user, admin | `GithubConnection` (`200`), replaces the token |
| DELETE | `/api/connections/{id}/github-token` | dev user, admin | `204`, disconnects the token connection and deletes the token |
| GET | `/api/connections/{id}/organization/preview` | dev user, admin | `OrganizationPreview`, see "Organization import" |
| POST | `/api/connections/{id}/organization/import` | dev user, admin | `ImportResult`, selects repositories, optionally creates projects from teams |
| PUT | `/api/connections/{id}/organization/repositories` | dev user, admin | `SelectionResult`, replaces the repository selection |
| GET | `/api/connections/github/request` | dev user | `{ request: ConnectionRequest \| null }`, the company's open organization install request |
| POST | `/api/connections/github/request` | dev user, admin | `ConnectionRequest` (`201` new, `200` already open) |
| DELETE | `/api/connections/github/request` | dev user, admin | `204` (closes the open request, if any) |
| POST | `/api/connections/github/request/check` | dev user, admin | `ConnectionRequestCheck` (`200` pending, `201`/`200` connected), see below |
| GET | `/api/overview` | dev user | `Overview` |
| GET | `/api/issues` | dev user | `BoardIssue[]` |
| GET | `/api/issues/{id}` | dev user | `IssueDetail` or 404 |
| GET | `/api/issues/new-options` | dev user | `NewIssueOptions` |
| POST | `/api/issues` | dev user | `201 CreatedIssue` or `202 PendingIssue`, see below |
| PATCH | `/api/issues/{id}/board` | dev user | `BoardIssue` |
| PATCH | `/api/issues/{id}` | dev user | `AssignedIssue`, see below |
| POST | `/api/issues/{id}/comments` | dev user | `201 PostedComment`, see below |
| GET | `/api/sprints` | dev user | `SprintList` |
| POST | `/api/sprints` | dev user | `201 Sprint` |
| PATCH | `/api/sprints/{id}` | dev user | `Sprint` |
| POST | `/api/sprints/{id}/issues` | dev user | `SprintIssuesResult` |
| DELETE | `/api/sprints/{id}/issues` | dev user | `SprintIssuesResult` |
| POST | `/api/sprints/{id}/carry-over` | dev user | `CarryOverResult` |
| POST | `/api/sprints/{id}/carry-over/{carryOverId}/undo` | dev user | `CarryOverResult` |
| GET | `/api/backlog` | dev user | `BacklogPage` |
| GET | `/api/milestones` | dev user | `Milestone[]`, open first, then `dueOn` ascending (nulls last) |
| GET | `/api/meeting-notes` | dev user | `MeetingNoteSummary[]` |
| GET | `/api/meeting-notes/{id}` | dev user | `MeetingNote` or 404 |
| POST | `/api/meeting-notes` | dev user | `201 MeetingNote` (`200` for a repeat with the same `id`) |
| PATCH | `/api/meeting-notes/{id}` | dev user | `MeetingNote` |
| DELETE | `/api/meeting-notes/{id}` | dev user, author or admin | `204` |
| POST | `/api/meeting-action-items/{id}/issue` | dev user | `201`/`200 ActionItemIssueResult` or `202 PendingActionItemIssue`, see below |
| GET | `/api/search?q=` | dev user | `SearchResult` |

`POST /api/tenants` creates a company for a signed-in person (bearer token, or `X-Dev-User` with a known
login in Development) and makes them its `admin`. Body `{"name": "<string>"}`; the name is trimmed and must be
1..80 characters without control or invisible format characters (zero-width, bidi overrides); the request must be `application/json`. One transaction on the `workboard_sync` role inserts the tenant,
its `tenant_settings` row (all flags off) and the member (GitHub id, login, display name and avatar from the
GitHub identity). Response `201 {"tenant": {"id", "name"}, "member": Member}`; send `X-Tenant: <tenant.id>`
afterwards. Errors: `401 unauthenticated` (no valid sign-in), `403 github_login_missing` (the GitHub identity has no login; the session stays valid),
`503 identity_unavailable`,
`400 invalid_name` (also for a body that is not JSON), `409 tenant_limit` (the person already created 5
tenants; tenants they only joined do not count).
`Idempotency-Key` is optional (`400 invalid_request` when longer than 200 characters). Unlike other writes the key is
scoped to the caller's GitHub user (the caller has no tenant yet) and stored in `tenant_create_keys`. Repeating the
same name with the same key within 24 hours returns the stored `201` and body without creating another company; a
different name with the same key is `409 idempotency_key_reused`. Only the `201` is stored, so `409 tenant_limit` is
not replayed. Concurrent requests of one person are serialized by the same lock that enforces the limit. The web
sends one key per company name and keeps it while the person retries the same name.

`POST /api/connections/github` connects a GitHub account after the caller installs the GitHub App on it.
Body `{"code": "<oauth code>", "installationId": <number>}` (`application/json`), both from the redirect to
`/connect/github`. The server exchanges `code` for a user token (never stored or logged), checks that the token's
GitHub user is the caller and that the installation is in that user's installations, and reads the account from the
App's own installation record. A personal account must be the caller's own. An organization needs the caller to be
an active **owner** of it: the server asks `GET /orgs/{org}/memberships/{login}` with that installation's token and
compares the returned user id (not the login) with the caller's GitHub id. It then inserts (or, for the same
tenant's active account, updates) the `github_connections` row on the `workboard_sync` role in one transaction; for an
organization the same transaction closes the company's open install request. Organization members are then imported
(`GET /orgs/{org}/members`, see "Organization members"); a failed import keeps the connection with `orgMemberCount: null`.
Response is the `GithubConnection`.
Errors: `400 invalid_request` (missing code or installationId, not JSON), `400 invalid_code`,
`401 unauthenticated`, `403 admin_only`, `403 not_your_account`, `403 not_your_installation`,
`403 not_org_owner` (an organization member, not an owner: use the request flow), `403 not_org_member`,
`409 account_connected` (active in another tenant), `422 org_members_not_granted` (the installation lacks the
"Members: read" organization permission), `422 unsupported_account` (neither a user nor an organization),
`502 github_unavailable` (GitHub call failed), `503 github_app_not_configured`.

### Token connections

`POST /api/connections/github-token` connects an organization with a personal access token instead of the App.
Body `{"token": "<PAT>", "organization": "<login>"}`. Fine-grained tokens are recommended (resource owner: the
organization; repository permissions Issues, Pull requests, Contents and Metadata read, Issues write). Classic tokens
are accepted with `repo` and `read:org` only. The token is never returned, logged or written to `audit_log`; responses
show `tokenLast4`. The server checks, in order: `GET /user` (the id must equal the caller's GitHub id), the scopes of a
classic token (`X-OAuth-Scopes`: scopes other than `repo`, `read:org`, `public_repo`, `read:user`, `user:email` are
refused), the expiry (`github-authentication-token-expiration`, stored as `tokenExpiresAt`), then
`GET /user/memberships/orgs/{org}` (state `active`) and `GET /orgs/{org}/repos` (SAML SSO shows as a 403 with the
`X-GitHub-SSO` header). Only `ghp_` (classic, with the scopes header) and `github_pat_` (fine-grained) tokens are accepted; `gho_`, `ghu_`, `ghs_`, `ghr_` are `unsupported_token`. The permissions of a fine-grained token cannot be read, so an over-privileged one is not refused. The connection is created with
`authKind: "token"`, no installation id and `store_bodies = false` (issue and comment text is not mirrored). The
organization's members are not imported.
`PUT /api/connections/{id}/github-token` takes `{"token": ...}`, repeats the checks for the connection's organization
and replaces the stored token. `DELETE` sets `disconnectedAt` and deletes the token.
Errors: `400 invalid_request`, `400 invalid_token` (rejected or expired), `400 unsupported_token`, `403 admin_only`,
`403 not_your_account`, `403 not_org_member`, `403 org_membership_inactive`, `403 org_access_denied`,
`403 sso_not_authorized` (also `ssoUrl`), `404 not_found` (PUT and DELETE: not an active token connection of the
company), `409 account_connected` (active anywhere, this company included; `POST /api/connections/github` answers the same for an account connected with a token), `422 token_scope_too_broad` (also
`scopes`), `422 token_scope_missing` (also `missing`), `422 organization_mismatch` (PUT: the token reaches another
organization), `502 github_unavailable`, `503 credential_store_not_configured` (no usable key file).
`audit_log` actions: `connection.token_connect`, `connection.token_replace`, `connection.token_delete`,
`connection.token_failed` (with `reason`), target type `connection`.

Writes through a token connection show the token owner as the GitHub author; the board's "작성: @member" line names
the member. The duplicate check after a retried write recognises the token owner's login (the App's `<slug>[bot]`
for App connections). A write on a token connection without a usable token answers `409 credential_missing` (no token
stored), `409 credential_revoked` (GitHub answered 401), `409 credential_invalid` (owner check failed, see "Token health") or `409 credential_expired`, without calling GitHub.
Sync and the new-issue repository list include token connections; the organization's repositories come from
`GET /orgs/{org}/repos`, but only repositories selected through "Organization import" are read (see below). Webhooks do not reach token connections, so they rely on the periodic sync.

### Token health

A token connection reports `tokenStatus` on `GET /api/connections` and the write responses: `active`, `expired`,
`revoked`, `invalid` or `token_required` (no token stored). An `active` token past `tokenExpiresAt` is reported as
`expired` at once, even before a sync marks it. Other fields: `tokenExpiresSoon` (active and expiring within 7 days),
`tokenDaysLeft` (whole days left, rounded up, null without an expiry or when not active) and `syncPausedUntil`
(rate-limit pause, see below; null when not paused). App connections have `tokenStatus: null`, `tokenExpiresSoon: false`.

Transitions (each writes an `audit_log` row without a member and without token text, target type `connection`):

| From | Trigger | To | `audit_log` action |
| --- | --- | --- | --- |
| `active`, `invalid` | `expires_at` passed (checked at the start of every sync run) | `expired` | `connection.token_expired` |
| `active` | GitHub answered 401 for that token (sync, write, organization import, or the owner check), no retry | `revoked` | `connection.token_revoked` |
| `active` | the owner check finds the organization refuses the token (SAML SSO not authorized, policy), or the owner is not a member (404) or not an active member (`state` other than `active`) for the first time | `invalid` (token kept) | `connection.token_invalid` (reason `sso_not_authorized`, `org_access_denied`, `owner_not_member`, `owner_not_active`) |
| `invalid` | the next owner check passes | `active` | `connection.token_recovered` |
| `invalid` (reason `owner_not_member` or `owner_not_active`) | the next owner check says "not a member" or "not active" again for the same token | `token_required` (token deleted) | `connection.token_released`, reason `owner_left_organization` |
| any | the token owner left the company (`members.left_at`, set by `DELETE /api/members/{id}`; any sync run also sweeps) | `token_required` (token deleted) | `connection.token_released`, reason `owner_left_company` |
| `expired`, `revoked`, `invalid`, `token_required` | `PUT /api/connections/{id}/github-token` | `active` | `connection.token_replace` (with `previousStatus`) |

Every transition that follows a GitHub answer about a specific token (401, owner check, recovery, deletion) locks the
credential row and applies only if the stored token is still the one that was asked about; a token replaced in the
meantime is left alone. A single 404 can come from a renamed organization or a withdrawn fine-grained approval, so
losing membership needs two checks in a row for the same token (the checks run on `invalid` tokens too, one per sync
run, and a passing check in between resets the count). Before the membership question the organization is looked up
by its stored id (`GET /organizations/{id}`); a changed login updates `account_login` (`connection.org_renamed`). Syncing skips a token
connection unless its token is `active` and unexpired, so a stopped connection makes no GitHub call and no `sync_runs`
row. Writes answer `credential_missing`, `credential_revoked`, `credential_invalid` or `credential_expired`. Selected
repositories and mirrored data are kept. Handover: another admin replaces the token with `PUT`; the same checks run for
the replacing admin (`403 not_your_account` unless the token belongs to them), and the connection syncs again at the
next run, with the rate-limit pause cleared. The #40 organization webhooks do not reach token connections, so owner
membership is checked at sync time: one `GET /user/memberships/orgs/{org}` and one `GET /orgs/{org}/repos?per_page=1` with the owner's token per run.

Rate limits: GitHub allows 5,000 requests per hour to one user, shared with the owner's other activity. Token
connections only: GitHub counts REST and GraphQL separately, and a run tracks both (`X-RateLimit-Remaining` of REST
answers, `rateLimit.remaining` of GraphQL answers). When either is below 500 it stops reading further repositories, and
the connection is skipped until the later reset time of the low bucket(s) (`syncPausedUntil`; at least 1 minute and at
most 1 hour from now). `sync_runs.rate_limit_remaining` records the lower of the two. A `RateLimited` answer pauses the connection until its retry time (15 minutes when
unknown). Manual runs obey the pause. The repository list, labels and milestones of token connections are read with
`If-None-Match`; a 304 does not count against the limit. ETags and bodies are kept in server memory per connection and
token (32 MB in total, bodies above 1 MB are not kept, the oldest entries are dropped first), so a restart only costs full requests.

### Organization import

For an active token connection of an organization (`404 not_found` for anything else, including a connection of another
company). All three calls are admin only (`403 admin_only`) and use the connection's stored token.

`GET /api/connections/{id}/organization/preview` reads from GitHub (`/orgs/{org}`, `/repos`, `/teams`, `/members`, and
per repository one label and one milestone request) and returns, without writing anything:
```
{ "organization": { "id", "login", "name", "description", "htmlUrl", "avatarUrl" },
  "repositories": [{ "id": <GitHub repo id>, "name", "fullName", "htmlUrl", "archived", "private", "description",
                     "selected": bool, "labelCount": n | null, "milestoneCount": n | null }],
  "teams": [{ "id", "name", "slug", "description", "privacy" }],
  "members": [{ "id": <GitHub user id>, "login", "avatarUrl", "onBoard": bool }],
  "counts": { "repositories", "teams", "members", "labels", "milestones", "countedRepositories" },
  "truncated": { "repositories", "teams", "members", "counts": bool } }
```
`selected` is the current selection (`repositories.sync_enabled`); `onBoard` is true for an active board member (not removed, not left) with that
GitHub id. Lists are capped at 10 pages of 100 (`truncated.*` true when more exist). Labels and milestones are counted
for the first 30 non-archived repositories only (`labelCount` / `milestoneCount` are null for the others and
`truncated.counts` is true); `counts.labels` and `counts.milestones` sum those.

`POST /api/connections/{id}/organization/import` body `{"repositories": [<GitHub repo id>, ...], "teamProjects": ["<team slug>", ...]}`
(`repositories` needs 1 to 1000 ids, `teamProjects` is optional, at most 50). The ids must belong to the organization's
repositories. The selection replaces the previous one. For each team in `teamProjects` a board project named after the team
is created and linked to the selected repositories the team can access. A team is skipped (listed in `skippedTeams` with a
`reason`) when no selected repository is in it (`no_selected_repositories`) or when a project with that name exists
already (`name_taken`; that project is left untouched, including one an earlier import made). Organization members are written to `github_users` as reference rows
(login and avatar) and never become board members; access still only comes from invitations. Nothing else is read: no secrets, billing,
webhooks, security or SSO settings, or organization Projects. Response `200`:
`{"selected": n, "deselected": n, "projects": [{"id", "name", "repositoryCount"}], "skippedTeams": [{"slug", "reason"}], "membersReferenced": n}`.

`PUT /api/connections/{id}/organization/repositories` body `{"repositories": [...]}` (0 to 1000 ids) replaces the selection
later. Response `200` `{"selected": n, "deselected": n}` where `deselected` counts repositories that were selected before.

Selection and sync: `repositories.sync_enabled` is the selection. A sync run of a token connection still lists the
organization's repositories, but inserts unknown ones with `sync_enabled = false` and reads only enabled ones. Token
connections that existed before this feature start with nothing selected (migration 0024). When a repository moves to
another connection (e.g. reconnected with the App), `sync_enabled` is reset from the new connection: App enabled, token
disabled until imported. A deselected repository keeps its
mirrored rows, stops syncing, and its issues drop out of `issue_status`, so board lists, search, milestones, overview and
project views no longer show them. Sprint carry-over, its undo guard and `sprint_daily` read `issue_status_all` (same status, no visibility rule), so
hiding a repository never strands issues in a sprint or changes recorded scope. Selecting it again shows them again and the next run catches up from its last
`last_synced_at`. Projects keep their links to deselected repositories, which the project views hide.
Errors: `400 invalid_request`, `403 admin_only`, `404 not_found`, `409 credential_missing` (stored token missing or
rejected by GitHub), `422 unknown_repository` (also `ids`), `422 unknown_team` (also `slugs`), `429 github_rate_limited`,
`502 github_unavailable` (also a GitHub timeout). `audit_log` actions: `connection.org_import`, `connection.org_repositories_update`.

When someone who is not an organization owner installs the App, GitHub records an install request and returns to
`/connect/github?setup_action=request` without an installation id. The web then calls
`POST /api/connections/github/request`: one open request per company (requester taken from the sign-in, never from
the body; a second call returns the open one with `200`). `DELETE` closes it.

`POST /api/connections/github/request/check` ("다시 확인") looks for the approved installation. Body is optional:
`{"accountId": <organization id>}` narrows the search after a `multiple_organizations` answer. The server lists the
App's installations with the App JWT (`GET /app/installations`) and keeps those that are organizations, are not
suspended, were created no earlier than 5 minutes before the request, and are not actively connected anywhere. For each,
it checks with that installation's token that the calling admin is an active **owner** (the user id must match).
Exactly one match is linked like a direct install, and the request is closed. A request older than 7 days is expired:
`GET` returns `null`, and check/create close it first (a create then opens a fresh one). Response:
`200 {"status": "pending", "connection": null}` when nothing matches yet, or
`201 {"status": "connected", "connection": GithubConnection}` (`200` when it updated an existing row of the same company).
Errors: `401`, `403 admin_only`, `403 not_org_owner` (the caller is only a member of an approved organization; an owner
has to sign in and check), `404 no_request` (none open, or expired), `409 account_connected` (the caller owns an approved
organization that another company already connected; otherwise such installations are not mentioned and the answer is
pending),
`409 {"error": "multiple_organizations", "organizations": [{"accountId": <number>, "login": "<string>"}, ...]}`, `422 org_members_not_granted`
(a candidate's installation lacks "Members: read"), `502 github_unavailable`, `503 github_app_not_configured`
(App id or private key missing).

Organization members: `github_org_members` keeps, per organization connection, the GitHub id and login of each member
(imported on connect, then kept current by `organization` webhooks). They are for display only (the count in
`GithubConnection.orgMemberCount`); a row never makes anyone a member of the company, which still needs an invitation.
`orgMemberCount` is null for personal accounts and when the members could not be read.
Settings `GitHub:App:AppId`, `ClientId`, `ClientSecret`, `PrivateKeyPath`, `Slug`.

`GET /api/issues` query parameters, all optional:

| Name | Meaning |
| --- | --- |
| `projectId` | only issues in repositories of this project |
| `person` | only issues assigned to this GitHub login |
| `assignee` | alias of `person`; if both are given, `person` wins |
| `status` | comma list of `todo`, `in_progress`, `in_review`, `done`, `cancelled` (derived status). Any other value, or an empty list → `400 {"error": "invalid_status"}` |
| `stale` | `true` keeps only open issues whose `updatedAt` is 7 or more days old. `false` or absent: no filter |
| `sprint` | only issues in this sprint (uuid) |
| `label` | only issues carrying this label (uuid) |
| `doneSince` | `week` keeps only issues closed since Monday 00:00 in Asia/Seoul (same boundary as the overview's `doneThisWeek`); open issues are dropped and `doneWithinDays` is ignored. Any other value → `400 {"error": "invalid_doneSince"}` |
| `scope` | `all` includes every closed issue regardless of `doneWithinDays` (use it for progress over all issues). Any other value → `400 {"error": "invalid_scope"}` |
| `doneWithinDays` | closed issues are included only if closed within N days, integer 0..365 (default 14). Ignored when `scope=all`. Out of range → `400 {"error": "invalid_doneWithinDays"}` |

Filters combine with AND. Without `scope=all`, `status=done` therefore returns only issues closed within
`doneWithinDays`. Open issues are always included unless a filter excludes them. Sort: `updatedAt` descending.

Derived status, first matching rule wins (view `issue_status`):

1. closed with reason `not_planned` -> `cancelled`
2. closed -> `done`
3. board override (`status_override`) -> that value
4. a linked pull request is merged while the issue is open -> `in_review`
5. a linked pull request is open and not a draft -> `in_review`
6. a linked pull request is open and a draft -> `in_progress`
7. otherwise `todo`

`cancelled` has category `done` but is excluded from every progress count (overview projects, sprint, people,
`doneThisWeek`, urgent attention and action-item lists) and from milestone counts.

`PATCH /api/issues/{id}/board` (any member) changes card fields. Body `{status?, priority?, size?, expectedUpdatedAt?}`;
at least one of `status`, `priority`, `size` must be present, and a present `null` clears it. Response is the updated `BoardIssue`.

- `status`: `todo`, `in_progress`, `in_review` set (null clears) `board_fields.status_override`. `done` closes the GitHub
  issue as `completed`, `cancelled` as `not_planned`; both clear the override (`status_cleared` event). On an issue that is
  already closed, GitHub is called again only to switch between the two board statuses (`done` on a `not_planned` issue,
  `cancelled` on any other closed issue) and a new `closed` event is recorded; otherwise nothing is called or recorded (a `duplicate` or
  reason-less closed issue already counts as `done`). Moving a closed issue to `todo`, `in_progress` or `in_review` reopens
  it on GitHub and sets the override. Clearing the override never changes GitHub.
- `priority` (`urgent`, `high`, `normal`, `low`) and `size` (`S`, `M`, `L`) need the tenant's `priorityEnabled` / `sizeEnabled`,
  else `409 field_disabled` (also when clearing). Any other value, wrong type, or an empty body is `400 invalid_request`.
- GitHub is written first (with `Idempotency-Key` handling of GitHub writes); only after it succeeds are the mirror and
  `board_fields` changed, so a GitHub failure changes nothing and returns the GitHub error codes above. A repository that is archived
  is `409 repository_archived` before any GitHub call (only when GitHub would be called). A repository without an active
  installation, or an issue of another tenant, is `404 not_found`.
- `expectedUpdatedAt`: send the `boardUpdatedAt` of the `BoardIssue` you edited (`null` = it had no board row yet; omit the
  field to skip the check). A different value is `409 conflict`. The check runs before GitHub is called, or under the lock when
  GitHub is not called. Once GitHub has been written the change is always applied, so a change that already happened is never answered with an error.
- Records `status_set`/`status_cleared` (values are override values), `priority_set`, `size_set` events (only for real changes),
  the `closed`/`reopened` event, and an `issue.board_update` audit row. Board-field writes take the same per-tenant lock as sprint writes.

Creating issues (any member, GitHub write through the App):

- `GET /api/labels` returns the tenant's labels (`id`, `name`, `color`), ordered by name, for the board's label filter.
- `GET /api/issues/new-options` returns the repositories the caller can create in (active connection with an App
  installation, archived ones last and flagged), the labels of each non-archived repository, and every member as a possible assignee.
- `POST /api/issues` requires `Idempotency-Key` (missing or longer than 200 characters is `400 invalid_request`). Body:
  `repositoryId`, `title` (trimmed, 1..256, no control or invisible format characters), optional `body` (max 60000 characters),
  `labels` (max 20, names of labels already in the repository's mirrored label list, matched case-insensitively and sent to GitHub with the stored spelling; unknown names are `400 invalid_request` with `unknownLabels: string[]` (a label just created on GitHub is unknown until the next sync); duplicates ignored), `assigneeGithubUserIds` (max 10, must be members of the
  caller's tenant, else `400 invalid_request`), and the board fields `status` (`todo`, `in_progress`, `in_review`, `done`),
  `priority` (`urgent`, `high`, `normal`, `low`), `size` (`S`, `M`, `L`). Any other value is `400 invalid_request`.
  Order: GitHub create, then the mirror, then board fields (`issue_events` `status_set`, `priority_set`, `size_set`, under the
  per-tenant `sprint-assign` advisory lock) and an `issue.create` `audit_log` row. The GitHub body gets a footer naming the caller.
  Nothing is written locally when GitHub refuses. A repeat with the same key and body returns the stored `201`.
  Errors: `404 not_found` (repository not in the tenant or not connected), `409 repository_archived`, plus the GitHub write errors above
  (`github_write_not_granted`, `assignee_not_collaborator`, `github_unavailable`, `request_in_progress`, `idempotency_key_reused`).
  `droppedAssignees` lists assignees GitHub did not apply.
  If GitHub created the issue but saving it locally failed, the response is `202 PendingIssue` and is stored under the key, so a retry
  replays it and never creates a second issue; the next sync adds the issue to the board. The requested `status`/`priority`/`size` and the `issue.create` audit row are not saved in that case and the sync does not restore them, so the client should ask the person to set them again.
  No duplicate on retry: every issue created here carries an invisible marker `<!-- work-board:create:<id> -->` after the footer,
  where `<id>` is a hash of tenant, key and request (the raw key is never written to GitHub, and the same key with a different body gets a
  different marker). When a retry takes over a stale pending reservation (the earlier call timed out, lost its connection or the server died
  mid-call), the server first lists the repository's issues and pull requests (state all, newest first, 5 pages of 100, only those created since the
  first attempt with the key minus 5 minutes; the time of the first attempt is kept across repeated takeovers) and, if one carries the marker, returns it as the normal `201`/`202` without creating. If that
  list cannot be read, or its newest-first pages do not reach back to that time within the page bound, the server does not create: `504 github_unavailable`, the key stays pending and a later retry looks again.
  A marker only matches an item written by this App's bot (`GitHub:App:Slug` + `[bot]`), so a copied marker in a person's or another bot's issue is ignored.
  Remaining limits: the guarantee is lost if the marker is removed by editing the issue; a pending key older than 24 hours is treated as absent, so a resend after that
  creates normally without a lookup; and if the database fails while the key is being completed after GitHub answered (the retry then
  looks for the marker and normally finds the issue, but the earlier answer is not replayed).

Assignees and comments (any member, GitHub write through the App, same `Idempotency-Key` handling as other GitHub writes):

- `PATCH /api/issues/{id}` body `{"assigneeGithubUserIds": number[]}` replaces the issue's assignees (an empty list clears them).
  Max 10, duplicates ignored; every id must be a member of the caller's tenant or already an assignee of this issue in the mirror (an
  outside collaborator assigned on GitHub can be kept), else `400 invalid_request` (also for a missing field, a body that is not JSON, or more than 10). `Idempotency-Key` is optional (replacing twice is harmless). There is no concurrency check:
  the list sent wins, including over assignees changed on GitHub since the panel loaded. Order: GitHub `PATCH` of the issue, then the
  mirror (from GitHub's answer) and an `issue.assignees_update` `audit_log` row (`before`/`after` logins, `after.requested`). No `issue_events`
  row: assignees are not a board field, and an event would block undoing a carry-over. Response `200 AssignedIssue`;
  `droppedAssignees` lists requested ids GitHub did not apply. Errors: `404 not_found` (issue not in the tenant or repository not connected),
  `409 repository_archived`, `422 assignee_not_collaborator` (GitHub refused an assignee), and the GitHub write errors above. Nothing local changes on an error.
- `POST /api/issues/{id}/comments` requires `Idempotency-Key` (missing is `400 invalid_request`). Body `{"body": string}`, trimmed, 1..60000
  characters, else `400 invalid_request`. GitHub shows the App bot as the author; the body gets the footer naming the caller and an invisible marker
  `<!-- work-board:comment:<id> -->` (`<id>` hashed from tenant, key and request, like issue creation). Then the comment is mirrored
  into `comments`, and an `issue.comment` `audit_log` row is written even when mirroring fails (a takeover that finds an earlier
  attempt's comment can write a second audit row if that attempt died after its audit row). Response `201 PostedComment`: `author` is the caller and `body` the text sent (no footer).
  If GitHub posted but saving locally failed, the response is still `201` with `id: null` and `mirrorPending: true`; the next sync brings the comment.
  Errors: `404 not_found`, `409 repository_archived`, and the GitHub write errors above.
  No duplicate on retry: a comment is not safe to repeat, so a retry that takes over a stale pending reservation first lists that issue's comments
  updated since the first attempt minus 5 minutes (oldest first, 5 pages of 100) and returns the one written by this App's bot and whose
  body carries the marker, without posting. If the list cannot be read or is longer than the bound, nothing is posted: `504 github_unavailable`
  and the key stays pending. Same remaining limits as issue creation (marker removed by editing, keys older than 24 hours). The client must resend
  only the same body under the same key after a `504`, a network error or `409 request_in_progress`.
- `IssueDetail.comments`: a comment written by this App's bot (login `<GitHub:App:Slug>[bot]`, compared case-insensitively) whose body ends
  with the board footer is shown as the member the footer names (login, and display name when that login is a member) with the footer and marker
  removed. The same rule removes the footer from `IssueDetail.body` of issues created on the board. A footer typed by a person or another bot
  (`github-actions[bot]`, say) is left as text. Limit: anyone allowed to edit the App's comment on GitHub can change its text and it is still
  shown under the footer's member name.
- This App's bot is recognised only when `GitHub:App:Slug` is set. Without it no comment or body is rewritten, and the marker lookups of issue
  creation and comments cannot tell which items are ours, so a takeover answers `504 github_unavailable` and keeps the key pending instead of
  creating or posting again.

Sprints (UI word: 주기) and the backlog (대기 목록) exist only when the tenant has `sprintsEnabled`. When it is off,
`GET /api/sprints` and `GET /api/backlog` return empty results and every sprint write is `409 sprints_disabled`.
Any member may write. All sprint writes accept `Idempotency-Key`, take a JSON body (except undo), and record
`issue_events` (`sprint_added`, `sprint_removed`, `carried_over`) plus an `audit_log` row in the same transaction.
Event values hold sprint names; `sprint_id` is the sprint the issue was added to, removed from, or carried to.
A sprint is `active` when today (Asia/Seoul) is within its dates, `planned` before, `closed` after its end date (or when stored as `closed`; no endpoint sets that).
Names are unique per tenant, case-insensitive.

- `GET /api/sprints` returns `current` (active, newest start first), `upcoming` (by start) and `past` (latest 10 by end date).
- `POST /api/sprints` body `{name, startDate, endDate}` (`yyyy-MM-dd`, name 1..80 printable characters, end not before start).
  Errors: `400 invalid_request`, `400 invalid_dates`, `409 name_taken`.
- `PATCH /api/sprints/{id}` body `{name?, startDate?, endDate?, updatedAt}`: at least one change; `updatedAt` must equal the
  sprint's current value, else `409 conflict` (reload and retry). Also `400 invalid_dates`, `409 name_taken`, `404 not_found`.
- `POST /api/sprints/{id}/issues` body `{issueIds}` (1..200 ids, duplicates ignored) adds the issues; an issue in another sprint
  moves (a `sprint_removed` then a `sprint_added` event); one already in this sprint is `unchanged`. Adding to a sprint
  that has ended (or is closed) is `409 sprint_ended`; removing from one is allowed.
  `DELETE` with the same body removes them; issues not in this sprint are `unchanged`. If any id is not an issue of the
  caller's tenant, or the sprint is not, the whole request is `404 not_found` and nothing changes.
- `POST /api/sprints/{id}/carry-over` body `{toSprintId}` moves every issue of the sprint whose status is not `done` or
  `cancelled` to the target in one transaction (`carried_over` event per issue) and stores who did it. `toSprintId` equal to the
  source is `400 invalid_request`. A target that has ended is `409 sprint_ended`. With nothing unfinished it returns `carryOverId: null` and records nothing.
- `POST /api/sprints/{id}/carry-over/{carryOverId}/undo` moves the same issues back, only if every one is still in the target
  sprint, none has an `issue_events` row newer than the carry-over's own events (higher `seq`; `closed` / `reopened` rows with no member, recorded by a sync or when a board write mirrors an outside change, do not count), none is now `done`/`cancelled`, and none was deleted:
  otherwise `409 conflict` with `issueIds` listing the blocking issues, and a second undo is `409 already_undone`. Strict rule: an older carry-over cannot be undone once a later carry-over
  or undo touched the same issues. Records `carried_over` events in reverse.
- `GET /api/backlog`: open issues whose status is not `done`/`cancelled` and that are in no sprint. Optional `projectId`,
  `person` (alias `assignee`), `status` (comma list of `todo`, `in_progress`, `in_review`, else `400 invalid_status`),
  `page` (1..10000) and `pageSize` (1..200, default 50), else `400 invalid_request`. Sort like the issue list.
- `carriedFrom` on every `BoardIssue` (read only): the source sprint of the latest carry-over, not undone, that moved the
  issue into the sprint it is in now, as long as no `sprint_added` / `sprint_removed` event for the issue is newer (higher `seq`) than that
  carry-over's events. Otherwise `null`: no sprint, added by hand, or moved by hand since (also when it was moved away and back). An undo clears it. The planning screen shows it as the "넘어온 일" flag.

Event order: `issue_events.seq` (identity, assigned at insert) orders events; `at` is the transaction start time and is used for
display, charts and history only. Board and sprint writes take the advisory lock `sprint-assign:{tenant}` before they insert
events, so their `seq` order is the lock order even when an earlier lock holder has a later `at`. `closed` / `reopened` events
are written by the mirror without the lock, so their `seq` follows lock order only when writes do not overlap. Member-less
ones (a sync or a mirrored outside change) are ignored by the "newer" checks; a member's close also makes the issue done or
cancelled, which the undo's status check refuses on its own. Migrations must run as a role that bypasses RLS (as the local `postgres` does), or the
`0015` backfill updates no rows and stops at `set not null`. `sprint_carry_overs.event_seq` holds the highest
`seq` of the `carried_over` events that carry-over wrote; "newer than the carry-over" means `seq > event_seq`. Carry-overs made before
the column existed have `event_seq` null and keep comparing `at` with `created_at`.

Daily sprint numbers (`sprint_daily`; read by the overview's `sprint.trend`):

- The sync loop writes one row per sprint and day after every cycle, for tenants with `sprintsEnabled` and sprints whose
  dates contain the day and that are not stored as `closed`. The day is Asia/Seoul. Repeated cycles on one day overwrite that
  day's row (primary key `(tenant_id, sprint_id, day)`); earlier days are never rewritten, and days the job missed stay empty (no backfill).
- Columns: `scope_count`, `scope_weight` (issues in the sprint, `cancelled` excluded), `done_count`, `done_weight` (status `done`).
  Weight is size S=1, M=2, L=3; an issue without a size weighs 1. The loop runs even when the GitHub App is not configured.
- Rebuilding a day from `issue_events`: membership from the latest `sprint_added` / `carried_over` / `sprint_removed` event of each issue,
  size from `size_set`, done / cancelled from `closed` (`to_value` `not_planned` = cancelled) / `reopened`. A sync run records `closed` and `reopened`
  for issues it already stored whose state changed, or that stayed closed with a different reason (`closed`, `from_value`/`to_value` the
  reasons); `member_id` is null. Board writes that only mirror a response (assignees and similar) record the same member-less event when
  the applied response differs from the stored state or reason. An issue first seen already closed gets no event.

`GET /api/projects/done-by-week` (any member) counts finished issues per project and week for the last 8 weeks:
`weeks` holds the Monday (`yyyy-MM-dd`, Asia/Seoul) of each week, oldest first, the last one being the current week;
`projects[].done[i]` is the number of issues of the project's repositories that are closed with a reason other than
`not_planned` (status `done`; `cancelled` is left out) and whose `closed_at` falls in week `i` (Monday 00:00 to Sunday 24:00 in Asia/Seoul).
`closed_at` is the latest close, so an issue that was reopened and closed again counts once, in the week of that last close,
and an issue that is open again counts nowhere. Every project is listed, with zeros when nothing was finished; an issue in two
projects counts in both. Projects are ordered by name.

`GET /api/projects/{id}/schedule` (any member) returns one project's milestones and sprints for the schedule tab.
A project the caller's tenant does not have is `404 not_found`. `today` and every date are `yyyy-MM-dd` in Asia/Seoul.

- Milestones: those of the project's repositories (a milestone belongs to one repository, so a repository shared by two
  projects shows its milestones in both). Open ones are all listed; closed ones only while their `closed_at` (or, without
  it, `due_on`) is within the last 60 days. `dueDate` is `due_on` converted to the Seoul calendar day, `null` when GitHub
  has no due date. Counts as in `GET /api/milestones` (all issues of the milestone, `cancelled` left out of `issueCount`).
  Sorted by due date (undated last), then number.
- Sprints: empty when the tenant has `sprintsEnabled` off (`sprintsEnabled: false`). Sprints are tenant-wide, so counts
  cover only this project's issues in the sprint (`cancelled` left out of `issueCount`; `unfinishedCount` = status not
  `done`/`cancelled`). Listed: every sprint that has not ended (`active`, `planned`), plus ended ones that hold work of this
  project and either ended within the last 60 days or still have unfinished work. `state` follows `GET /api/sprints`.
  Sorted by start date.
- `overdueDays` is `today - date` in days when the row is past its date and not finished, else 0: an open milestone whose
  `dueDate` is before today; a sprint whose `endDate` is before today with `unfinishedCount > 0` (carrying the work over
  to another sprint clears it). Closed milestones are never overdue.

`GET /api/milestones` counts exclude `cancelled` issues in `issueCount`; `doneCount` counts status `done`.
`color` of a label is 6 hex characters without `#`, as GitHub sends it.

`GET /api/meeting-notes` accepts optional `projectId`. Sort: `heldAt` descending.

Meeting note writes (UI word: 회의록). Board-only data: nothing is sent to GitHub. Every write accepts `Idempotency-Key`
(optional; the web sends one per request so a resend after a dropped connection is not applied twice) and runs in one
transaction that also writes an `audit_log` row (`meeting_note.create`, `meeting_note.update`, `meeting_note.delete`;
`before`/`after` hold title, date, project, decisions, attendee ids and action items, and only the length of the body).

- `POST /api/meeting-notes` body `MeetingNoteRequest` (`application/json`, max 512 KB). Required: `title` (trimmed, 1..200,
  no control or invisible format characters) and `heldAt` (ISO date-time with offset). Optional: `projectId` (null = none),
  `bodyMd` (max 60000 characters, default empty), `decisions` (max 50; each trimmed, empty ones dropped, max 500 characters),
  `attendeeIds` (member ids of current members, max 100, duplicates ignored), `actionItems` (max 100, in display order; `text` trimmed 1..500,
  `assigneeId` a current member's id or null, `dueDate` `yyyy-MM-dd` or null; `id` must be absent). The caller becomes `createdBy`.
  Response `201 MeetingNote`.
  `id` (optional, a uuid the client generates, not all zeros) makes creation safe to repeat: if a note with that id already exists and the
  caller wrote it, the response is `200` with that note **as stored** (the body of the repeat is not applied; save changes
  with `PATCH`). A note with that id written by someone else, or in another tenant, is `409 id_taken`. The web always sends
  an `id`, so a POST whose answer was lost can be resent (even with newer text) without creating a second note. A note
  nobody changed since its creation has `updatedAt` equal to `createdAt`; when a repeat returns a note whose `updatedAt` is
  later, someone else edited it and the web shows the conflict view instead of saving over it.
- `PATCH /api/meeting-notes/{id}` replaces the whole note: send every field of `MeetingNoteRequest` (`bodyMd`, `decisions`,
  `attendeeIds`, `actionItems` must be present, empty lists allowed) plus `expectedUpdatedAt`, the `updatedAt` of the copy
  you edited. A different value is `409 conflict` (someone saved in between; reload and let the person choose). Any member may edit.
  Action items keep their id: send `id` for an item that already exists (its text, assignee, due date and position change; its
  issue link stays) and no `id` for a new one. An existing item left out of the list is deleted, **unless it is linked to an
  issue**: then the whole request is `409 action_item_linked` with `actionItemIds` listing those items and nothing changes. An `id`
  that is not an item of this note, or repeated, is `400 invalid_request`. A member who left (`left_at`) is refused with
  `400 invalid_request` as a new attendee or assignee, but a note that already has them keeps them: the same attendee, or the
  same item's assignee, may be sent again. `updatedAt` changes on every successful save.
- `DELETE /api/meeting-notes/{id}` (no body) deletes the note with its attendees and action items (linked issues stay). Only the
  note's author (`createdBy`) or an admin; anyone else gets `403 forbidden`. A note whose author is gone can be deleted by an admin only.
  Response `204`.
- Order of checks: the body (`400 invalid_request`), then the note (`404`; for POST with `id` the repeat / `409 id_taken`
  check), then `expectedUpdatedAt` (`409 conflict`), then attendees, assignees, project and action-item ids (`400 invalid_request`),
  then linked items (`409 action_item_linked`). So a stale PATCH that also names a removed member answers `409 conflict`.
- Errors: `400 invalid_request` (any rule above, a body that is not JSON, an attendee, assignee or project that is not in the
  caller's tenant, PATCH without `expectedUpdatedAt`), `404 not_found`, `409 conflict`, `409 action_item_linked`, `409 id_taken`, `403 forbidden`,
  plus `409 idempotency_key_reused` / `400` for a bad key as with other writes.

Turning an action item into a GitHub issue (any member, GitHub write through the App, UI word: 이슈로 만들기):

- `POST /api/meeting-action-items/{id}/issue` requires `Idempotency-Key` (missing or longer than 200 characters is `400 invalid_request`).
  Body `{"repositoryId": "<uuid>"}` (`application/json`), else `400 invalid_request`. An item of another tenant, or a repository
  that is not in the tenant or not connected, is `404 not_found`; an archived repository is `409 repository_archived`.
- The issue: title = the item's text (cut to 255 characters plus `…` when longer than 256; the whole text then opens the body). Body
  `회의록: [<note title>](<link>) (<held date, yyyy-MM-dd in Asia/Seoul>)` and, with a due date, `마감: <yyyy-MM-dd>` (GitHub has no
  due date, and no milestone is chosen), then the footer naming the caller as in `POST /api/issues`. Typed text in the body (the note
  title, the whole item text) is made inert: Markdown punctuation is backslash-escaped (no links, images, emphasis or `#` references),
  `<`, `>`, `&` become HTML entities (no HTML, so no comment that could pass for a create marker), and every `@` is followed by a zero-width
  joiner (no mention). Assignee = the item's owner (a member, by GitHub login); `droppedAssignees` lists the owner when GitHub did not assign them.
  No labels and no board fields are set.
- Link: `<Web:BaseUrl>/notes/view?id=<noteId>`. `Web:BaseUrl` is a setting (the public address of the web app, for example
  `https://board.example.com`; a path is kept). Without it the request's `Origin` header is used only when it is one of
  `Web:AllowedOrigins` (the same list the CORS policy allows; default `http://localhost:3000`). Otherwise, or when the address is not
  `http(s)`, the body names the note without a link.
- Answers: `201 ActionItemIssueResult` with `created: true` when this request made the link (by creating the issue, or by finding the
  one an earlier attempt created). `200` with `created: false` when the item already had its issue (any key, any member; nothing is sent
  to GitHub, also for a resend of the request that created it). `202 PendingActionItemIssue` when GitHub created the issue but saving it
  locally failed: the item remembers the repository and number (`MeetingActionItem.pendingIssue`). The mirror links the item in the same
  transaction that stores that issue (a sync run or any later board write in the repository), and clears the claim; until then the item
  counts as having no issue everywhere (overview, note list counts, linked notes of the issue). A request for a pending item links it if the
  mirror has the issue (`200`) or answers `202` again, without calling GitHub; its `htmlUrl` is null and `repository` empty when the
  repository is no longer in the mirror. Errors: those of `POST /api/issues` (`github_write_not_granted`, `assignee_not_collaborator`,
  `github_unavailable`, `request_in_progress`, `idempotency_key_reused`), and `409 earlier_repository_unavailable` (below).
- One issue per item, whatever the keys: while GitHub is called the item row holds a claim (the request's key, the time, the time of the
  first claim, and every repository a create was sent to since the first claim), taken under the item's row lock after the key's own
  reservation. The requested repository joins that list before GitHub is called and none leaves it until the claim ends. A request with another key while a claim
  younger than 2 minutes exists is `409 request_in_progress` and calls nothing. GitHub refusing before creating (`403`, `404`, `422`) frees
  the item; an unknown outcome (`502` that may have had an effect, `504`, a crash) keeps the claim. A request that finds a claim of its own
  key, or one older than 2 minutes, first lists the issues (as in `POST /api/issues`) of every repository in the claim and the requested one,
  created since the first claim minus 5 minutes, and links the issue carrying the item's marker instead of creating. If one of those
  repositories is no longer connected, nothing is looked up or created: `409 earlier_repository_unavailable` (reconnect it; the item stays held). The marker is
  `<!-- work-board:create:<id> -->` with `<id>` hashed from tenant and item only, so every key and member shares it. A lookup that cannot be
  read is `504 github_unavailable`: nothing is created and the claim stays. Other limits as in `POST /api/issues` (marker removed by editing,
  `GitHub:App:Slug` needed for lookups).
- Writes the `issue.create` audit row of `POST /api/issues`, plus `meeting_action_item.issue` (`after`: `issueId`, `repository`, `number`,
  `foundEarlier`). The note's `updatedAt` does not change, so an editor open on the note keeps saving.
- `PATCH /api/meeting-notes/{id}` treats an item with `pendingIssue` or `creatingIssue` like a linked item (`409 action_item_linked` when left
  out). An item whose create ended with an unknown outcome can be removed once its claim is older than 2 minutes; if GitHub did create that
  issue, it stays unlinked.

`GET /api/search` matches `q` (case-insensitive substring, min 2 chars) against issue titles,
comment bodies and meeting note titles/bodies. Up to 20 results per group.

`GET /api/overview` returns the whole overview screen in one response, computed per request inside the
tenant transaction (no cache). Rules:

- Open = `state = 'open'`. Stale = open and `updatedAt` 7 or more days ago. Urgent = `board_fields.priority = 'urgent'`
  and status not `done`; always empty when the tenant has priorities disabled.
- `counts.doneThisWeek` and `people[].doneThisWeek` count issues closed since Monday 00:00 in Asia/Seoul
  (not a rolling 7 days).
- `attention` groups are ordered urgent, reviewWaiting, stale, unassigned. An issue shows only in the first group
  it belongs to; each group is capped at 5, oldest first; `totals` are counted after de-duplication and before the cap.
  `totals.urgentNotStarted` is the number of listed-urgent issues whose status is `todo`.
  `counts.stale` is every stale open issue; `attention.totals.stale` only counts stale issues not already shown
  in an earlier group (urgent, reviewWaiting), so it can be smaller.
  Epics are excluded from `unassigned`.
- `reviewWaiting` lists open PRs with review requested that are linked to an open issue (one entry per issue, the
  longest waiting PR). `waitingSince` is the PR's `github_updated_at`, so it is **approximate**: it moves whenever the PR
  changes, not only when review was requested.
- `people[].overloaded` is true only when the person has 4 or more `in_progress` + `in_review` issues and at least
  2 times the median of that number over all members of the tenant (members with 0 included; even counts average
  the two middle values). People are ordered by open issue count, then login.
- `projects[].byStatus` counts all issues of the project's repositories, closed ones included, with no time window.
  An issue in two projects counts in both.
- `sprint` is the sprint, not stored as `closed`, whose dates contain today (Asia/Seoul); with overlapping sprints the one with
  the latest start date (then id). `null` when sprints are disabled or none is active. The stored `state` is ignored except `closed`.
  `startDate`/`endDate` are `yyyy-MM-dd`; `daysLeft` counts from today in Asia/Seoul and is never negative.
- `sprint.trend` is every `sprint_daily` row of that sprint, oldest day first: `remaining = scope_count - done_count`,
  `remainingWeight = scope_weight - done_weight`. Days the job missed have no entry; nothing is filled in (the chart breaks the
  line there). Empty until the first row is written. The ideal line is the client's: from the first row's `scope` on its day
  down to 0 on `endDate`.
- `milestones` is always `null` until sync provides milestones.
- `attention.actionItemsWithoutIssue` covers every meeting action item without an issue (no date window);
  `actionItems` covers notes held in the last 14 days whose item has no issue or an unfinished one.
- `activity`: last 3 days, newest first, max 12. A PR event kind is inferred from its current state (merged,
  open with review requested, other open); closed-unmerged PRs are skipped. `actor` is null when the author is not a member.

## Invitations and removal

`PATCH /api/members/{id}` body `{"role": "admin" | "member"}` promotes or demotes a current member (`left_at` null) of
the caller's company. `Idempotency-Key` is optional, as for the other member writes. The transaction locks every active
admin row (`for update`, the same lock as removal), so two admins demoting each other leave one admin. Demoting the
last active admin, including yourself, is `409 last_admin`; an admin can step down while another admin remains. Setting
the role a member already has is `200` without a change. A change writes `audit_log` `member.role` (before: GitHub id,
login, role; after: role). Errors: `400 invalid_request` (role missing or not one of the two), `403 admin_only`,
`404 not_found` (no such current member in this company, including another company's member id), `409 last_admin`,
`409 member_removed` (promoting a member whose `removedAt` is set, i.e. gone from the GitHub organization; demoting or
keeping the role is allowed).

An admin invites a person by GitHub login; the person joins by opening the link and signing in with that GitHub
account. Rules:

- `POST /api/invitations` body `{"githubLogin": string}` (`application/json`; a leading `@` and spaces are dropped).
  The server looks the login up once with `GET https://api.github.com/users/{login}` and stores the numeric GitHub
  user id; the login is kept for display only and never matched again. The lookup uses an installation token of the
  company's active connection when the App can mint one (5000 requests an hour), else it is unauthenticated (60 an
  hour per server IP). Only accounts of type `User` are accepted. The role is always `member`.
- The invitation expires 7 days after it is made. Inviting someone who already has an open invitation revokes the old
  one (its link stops working) and makes a new one, so this is also how an admin gets a new link.
- The response carries `token`, 43 URL-safe characters (32 random bytes). Only its SHA-256 is stored, so the token
  cannot be shown again; a replay with the same `Idempotency-Key` returns the same invitation with `token: null`.
  The web builds the link as `{origin}/join#{token}` (fragment, so the token is not sent to the static host).
- Errors: `403 admin_only`, `400 invalid_request` (not JSON), `400 invalid_login` (not a valid GitHub login),
  `400 github_user_not_found` (no such user, or an organization or bot), `409 already_member`,
  `429 github_rate_limited`, `502 github_unavailable`. A retry with an `Idempotency-Key` that already completed returns the
  stored answer before the GitHub lookup, so it costs no rate limit.
- `DELETE /api/invitations/{id}` revokes an open invitation: `204`, `404 not_found` (unknown, another company's,
  already accepted or revoked), `403 admin_only`.
- `POST /api/invitations/preview` and `/accept`, body `{"token": string}`, need a valid sign-in but no membership.
  Checks, in order: `401 unauthenticated`; `400 invalid_request` (not JSON); `404 invite_not_found` (malformed or
  unknown token); `403 invite_other_account` (the signed-in GitHub id is not the invited id; nothing else about the
  invitation is returned); `409 invite_used` (accepted, and the caller is not a current member, e.g. was removed
  since); `410 invite_revoked`; `410 invite_expired`. The GitHub id comes from the sign-in (the Supabase identity's
  `provider_id`, same as every other request), never from the invited login.
- Preview `200 {"tenant": {"id", "name"}, "githubLogin", "role", "invitedBy": {"githubLogin", "displayName"} | null,
  "expiresAt", "alreadyMember"}`. Accept `200 {"tenant": {"id", "name"}, "member": Member}`; send `X-Tenant` afterwards.
  Accepting an invitation already accepted by the same, still current member returns the same `200`, so accept takes
  no `Idempotency-Key`. A person removed earlier gets their old member row back (same id, role from the invitation,
  login, name and avatar from the sign-in). `409 login_taken` when another member row of the company still holds the
  login (a renamed and reused GitHub login). Only current members hold a login: the login of a member who left
  (`left_at`) does not block a new person who now owns it. Two tabs accepting at once both get the same `200`.
- `DELETE /api/members/{id}` (admin) removes a member: `204`, `404 not_found` (unknown, another company's, or already
  removed), `409 last_admin` (the target is the only admin left, so the last admin cannot remove themselves),
  `403 admin_only` (also when the caller stopped being an admin while the request waited). Admins may remove
  themselves when another admin remains. The row stays with `left_at` set: the person's next request to this company
  fails (`403 no_membership` with a bearer token; with other memberships the usual `X-Tenant` rules apply to those only),
  while notes, audit rows, assignments and comments keep their name. They drop out of `GET /api/members`, the
  overview people list and new-issue assignees, and cannot be newly assigned. Open invitations they created are
  revoked in the same transaction. Rejoining needs a new invitation. This is separate from `removedAt` (GitHub
  organization membership, #35), which changes no access.
- `audit_log` actions: `invitation.create`, `invitation.revoke` (also for replaced invitations and those revoked with
  a removed inviter), `member.join` (written by the joining member), `member.remove`. Invite, revoke and remove accept
  `Idempotency-Key` like other DB-only writes.
- Development: `X-Dev-User` also accepts a login that has an invitation, so the join can be tried without Supabase.

## Sync

A background service syncs every tenant with an active connection that has an `installation_id` each
`Sync:IntervalMinutes` (default 10, or 60 while a verified webhook delivery was stored within the last 30 minutes, because the loop then only reconciles; an explicit value always wins; `Sync:Enabled=false` turns the loop and the webhook worker off, manual runs still work). A run lists the
installation's repositories, upserts them (`sync_enabled` defaults to true) and reads each enabled repository with
`since = last_synced_at - 2 minutes` (full read on the first run). One run per tenant at a time. Each run writes one
`sync_runs` row per connection; GitHub errors and a missing App key are recorded there, never thrown. A repository
that fails does not stop the others, except on `RateLimited`, `Unauthorized`, `CredentialRevoked`, `CredentialInvalid` and `CredentialExpired`, which end the run.

`GET /api/sync/status` (any member) -> `200`:
```
{ "connected": true, "running": false, "lastSyncedAt": "ISO" | null, "lastResult": "ok" | "failed" | null,
  "lastError": "short text" | null, "rateLimitRemaining": 4990 | null, "nextRunAt": "ISO" | null,
  "tokens": [ { "connectionId": "uuid", "accountLogin": "acme", "tokenOwnerLogin": "octocat" | null, "tokenStatus": "active" | "invalid" | "expired" | "revoked" | "token_required",
                "tokenExpiresAt": "ISO" | null, "tokenExpiresSoon": false, "tokenDaysLeft": 30 | null, "syncPausedUntil": "ISO" | null } ] }
```
`tokens` has one entry per active token connection (see "Token health"); it is empty without any. `tokenOwnerLogin` is
null while no token is stored.
`connected` is false when the tenant has no active connection with an installation; the other fields are then null/false.
`lastSyncedAt` is the time of the most recent successful repository sync (not the last attempt); `lastResult`, `lastError` and `rateLimitRemaining` come from the most
recent finished run. `nextRunAt` is null while the periodic loop is off.

`POST /api/sync/run` (admin only) starts a run in the background -> `202 { "started": true }`. Errors, checked in this
order: `403 { "error": "admin_only" }`, `503 { "error": "github_app_not_configured" }`, `409 { "error": "not_connected" }`,
`409 { "error": "sync_running" }`.

## GitHub webhook

`POST /api/github/webhook` is anonymous; the only proof of origin is `X-Hub-Signature-256` (HMAC-SHA256 of the raw body
with `GitHub:App:WebhookSecret`, compared in constant time). Checks run in this order and touch no database until the last:

| Status | Body | When |
|---|---|---|
| 503 | `{ "error": "webhook_not_configured" }` | no secret configured, or `Sync:Enabled=false` (nothing would drain the queue); nothing is stored |
| 401 | `{ "error": "invalid_signature" }` | header missing, malformed, or not matching the body |
| 413 | `{ "error": "payload_too_large" }` | body over 2 MB |
| 400 | `{ "error": "invalid_request" }` | `X-GitHub-Event` / `X-GitHub-Delivery` missing, or the body is not JSON |
| 200 | `{ "status": "pong" }` | `ping` |
| 202 | `{ "status": "ignored" }` | an event or action work-board does not act on (no row is stored) |
| 200 | `{ "status": "duplicate" }` | this `X-GitHub-Delivery` was already received |
| 202 | `{ "status": "queued" }` | stored in `webhook_deliveries` for the worker |

The request never waits for GitHub or for the mirror: it only inserts the delivery id and its routing fields
(event, action, installation id, repository id, member id, organization id; never the payload). The primary key on
`delivery_id` is the dedup. `WebhookWorker` drains the table (woken on insert, polling every 5 s), claims rows with a 2 minute
lease, retries a failure up to 5 times with 30 s steps, and records the outcome in `result`. Processed rows are deleted after 14 days.

| Event | Handling |
|---|---|
| `issues`, `issue_comment`, `pull_request`, `pull_request_review`, `pull_request_review_comment`, `milestone`, `label`, `sub_issues` | The payload is not applied. The repository (matched by `repository.id`, so a rename does not drop events) is re-read through `IGitHubSource` since its `last_synced_at - 2 minutes` and applied by `MirrorWriter`, exactly like a periodic run (ordering rule, issue_events, PendingLink, label and milestone lists). Pending events of one repository share one read. Unknown installation, unknown repository (not listed by a sync yet) and `sync_enabled = false` are acknowledged and ignored. |
| `installation` `deleted`, `suspend` | Active connections with that `installation_id` get `disconnected_at = now()`, and `members.removed_at` of their companies is re-derived from the remaining active connections (the same happens on `unsuspend`). Every sync query already skips disconnected connections, so syncing and board writes stop; the connection list shows it as disconnected. |
| `installation` `unsuspend` | The most recently disconnected connection of that installation is reactivated unless the account or installation already has an active connection. |
| `organization` `member_removed`, `member_added` | For the active connection whose `installation_id` matches and whose `account_id` equals `organization.id`: that person's `github_org_members` row of this connection gets `removed_at` set (or cleared; a row is created when missing). `members.removed_at` is then derived for that GitHub user id from active connections only: null while they are a current member of any active organization connection of the company (or no active connection mentions them), otherwise the time GitHub reported them gone. The delivery result is `applied`, `unchanged` or `ignored: unknown_member` (not a member of the company). Someone removed from one organization but still in another connected one is not marked. A removed member stays in the data and keeps access; `GET /api/members` returns `removedAt` and the new-issue assignee options leave them out. |
| everything else (`installation` `created`, `installation_repositories`, `push`, ...) | `202 ignored`. New repositories appear with the next periodic run. |

## Shapes

```ts
type Invitation = {
  id: string; githubUserId: number; githubLogin: string; role: "member";
  invitedBy: { githubLogin: string; displayName: string | null } | null;
  createdAt: string; expiresAt: string;
};
type CreatedInvitation = { invitation: Invitation; token: string | null };
type InvitationPreview = {
  tenant: { id: string; name: string }; githubLogin: string; role: "member";
  invitedBy: { githubLogin: string; displayName: string | null } | null; expiresAt: string; alreadyMember: boolean;
};
type JoinedTenant = { tenant: { id: string; name: string }; member: Member };

type OverviewIssueRef = {
  id: string; repositoryName: string; number: number; title: string;
  status: Status; updatedAt: string;
  assignees: { githubUserId: number; githubLogin: string; displayName: string | null }[];
};

type StatusCounts = { todo: number; inProgress: number; inReview: number; done: number };

type Overview = {
  generatedAt: string;
  counts: { open: number; inProgress: number; inReview: number; stale: number; doneThisWeek: number };
  attention: {
    urgent: { issue: OverviewIssueRef; notStarted: boolean }[];
    reviewWaiting: { issue: OverviewIssueRef; pullRequest: { number: number; title: string;
                     authorGithubLogin: string | null; waitingSince: string } }[];
    stale: { issue: OverviewIssueRef; idleDays: number }[];
    unassigned: OverviewIssueRef[];
    actionItemsWithoutIssue: { id: string; text: string; assignee: Member | null;
                               note: { id: string; title: string; heldAt: string } }[];
    totals: { urgent: number; urgentNotStarted: number; reviewWaiting: number; stale: number; unassigned: number;
              actionItemsWithoutIssue: number };
  };
  people: {
    member: Member; todo: number; inProgress: number; inReview: number; doneThisWeek: number;
    overloaded: boolean;
    current: OverviewIssueRef | null;   // most recently updated in_progress/in_review issue
    lastActivityAt: string | null;      // latest of assigned issue updates and own comments
  }[];
  projects: {
    project: { id: string; name: string; color: string | null; repositories: string[] };
    byStatus: StatusCounts;             // all issues, all time
    openPullRequests: number;
    latestNote: { id: string; title: string; heldAt: string } | null;
  }[];
  sprint: { id: string; name: string; startDate: string; endDate: string; daysLeft: number;
            byStatus: StatusCounts; trend: SprintDay[] } | null;
  milestones: null;
  actionItems: {
    id: string; text: string; assignee: Member | null;
    issue: { id: string; repositoryName: string; number: number; status: Status } | null;
    note: { id: string; title: string; heldAt: string };
  }[];
  activity: {
    kind: "comment" | "pr_opened" | "pr_review_requested" | "pr_merged" | "issue_closed" | "meeting_note";
    at: string;
    actor: { githubLogin: string | null; displayName: string | null } | null;
    issue: { id: string; repositoryName: string; number: number; title: string } | null;
    pullRequest: { repositoryName: string; number: number; title: string } | null;
    note: { id: string; title: string } | null;
    snippet: string | null;             // first 80 chars of a comment
  }[];
};
```

```ts
type Sprint = {
  id: string; name: string; startDate: string; endDate: string;   // yyyy-MM-dd
  state: "planned" | "active" | "closed";
  issueCount: number; doneCount: number; unfinishedCount: number; // cancelled issues are not counted
  createdBy: string | null; createdAt: string; updatedAt: string; // updatedAt goes back in PATCH
};
type SprintList = { current: Sprint[]; upcoming: Sprint[]; past: Sprint[] };
type SprintIssuesResult = { sprint: Sprint; added: number; moved: number; removed: number; unchanged: number };
type CarryOverResult = {
  carryOverId: string | null; fromSprint: Sprint; toSprint: Sprint; movedCount: number; issueIds: string[];
};
type BacklogPage = { items: BoardIssue[]; total: number; page: number; pageSize: number };
```

```ts
type DevUser = {
  githubLogin: string; githubUserId: number; displayName: string | null;
  tenantId: string; tenantName: string;
};

type Member = {
  id: string; githubUserId: number; githubLogin: string;
  displayName: string | null; avatarUrl: string | null; role: "admin" | "member";
};

type Me = {
  member: Member;
  tenant: { id: string; name: string };
  settings: { sprintsEnabled: boolean; sizeEnabled: boolean; priorityEnabled: boolean };
};

type GithubConnection = {
  id: string; accountId: number; accountLogin: string; accountType: "organization" | "user"; installationId: number | null;
  repositorySelection: "all" | "selected" | null; createdAt: string; disconnectedAt: string | null;
  orgMemberCount: number | null;   // current organization members; null for personal accounts or when unread
  authKind: "app" | "token";
  tokenLast4: string | null; tokenOwnerLogin: string | null; tokenExpiresAt: string | null;   // token connections only
  tokenStatus: "active" | "invalid" | "expired" | "revoked" | "token_required" | null; tokenScopes: string[] | null;
  tokenExpiresSoon: boolean; tokenDaysLeft: number | null; syncPausedUntil: string | null;
  storeBodies: boolean;   // issue and comment text is mirrored; false for token connections, read-only
};

type ConnectionRequest = { id: string; requestedAt: string; requestedByLogin: string | null; accountLogin: string | null };
type ConnectionRequestCheck =
  | { status: "pending"; connection: null }
  | { status: "connected"; connection: GithubConnection };

type Project = { id: string; name: string; color: string | null; repositories: { id: string; name: string }[] };
type DoneByWeek = { weeks: string[]; projects: { projectId: string; done: number[] }[] };   // done[i] belongs to weeks[i]
type ProjectSchedule = {
  projectId: string; today: string; sprintsEnabled: boolean;
  milestones: { id: string; repository: { id: string; name: string }; number: number; title: string;
                state: "open" | "closed"; dueDate: string | null; htmlUrl: string | null;
                issueCount: number; doneCount: number; overdueDays: number }[];
  sprints: { id: string; name: string; startDate: string; endDate: string; state: "planned" | "active" | "closed";
             issueCount: number; doneCount: number; unfinishedCount: number; overdueDays: number }[];
};
type SprintDay = { day: string; scope: number; remaining: number; scopeWeight: number; remainingWeight: number };

type Status = "todo" | "in_progress" | "in_review" | "done" | "cancelled";
type Category = "todo" | "in_progress" | "done";

type BoardIssue = {
  id: string;
  repository: { id: string; name: string };
  projectIds: string[];
  number: number;
  title: string;
  issueType: string | null;
  state: "open" | "closed";
  status: Status;            // from the issue_status view
  category: Category;        // from the issue_status view
  assignees: { githubUserId: number; githubLogin: string; displayName: string | null }[];
  openPullRequestCount: number;
  commentCount: number;
  parentIssueId: string | null;
  sprint: { id: string; name: string } | null;
  size: "S" | "M" | "L" | null;
  priority: "urgent" | "high" | "normal" | "low" | null;
  updatedAt: string;         // github_updated_at
  boardUpdatedAt: string | null; // board_fields.updated_at, null without a row; send as expectedUpdatedAt
  closedAt: string | null;
  htmlUrl: string | null;
  labels: { id: string; name: string; color: string | null }[];   // sorted by name
  milestone: { id: string; title: string; dueOn: string | null } | null;
  carriedFrom: { id: string; name: string } | null; // see "carriedFrom" under sprints
};

type Milestone = {
  id: string; repository: { id: string; name: string }; number: number; title: string;
  state: "open" | "closed"; dueOn: string | null; htmlUrl: string | null;
  issueCount: number;   // excludes cancelled issues
  doneCount: number;    // issues with status done
};

type IssueDetail = BoardIssue & {
  body: string | null;
  parent: { id: string; number: number; title: string } | null;
  children: { id: string; repositoryName: string; number: number; title: string; status: Status }[];
  pullRequests: { id: string; number: number; title: string; state: "open" | "closed" | "merged";
                  draft: boolean; reviewRequested: boolean; htmlUrl: string | null }[];
  comments: { id: string; author: { githubLogin: string | null; displayName: string | null };
              body: string; createdAt: string }[];   // oldest first
  meetingNotes: { id: string; title: string; heldAt: string }[];  // notes with an action item linked to this issue
  // newest status/priority/size/closed/reopened event with a member (sync events have none); null if none.
  // Board fields set by POST /api/issues count too, so a new issue created with a status names its creator.
  lastBoardChange: { at: string; by: { githubLogin: string | null; displayName: string | null } } | null;
};

type NewIssueOptions = {
  repositories: { id: string; name: string; fullName: string; archived: boolean;
                  labels: { name: string; color: string | null }[] }[];   // archived last; labels empty for archived
  assignees: { githubUserId: number; githubLogin: string; displayName: string | null; avatarUrl: string | null }[];
};

type CreateIssueRequest = {
  repositoryId: string; title: string; body?: string; labels?: string[]; assigneeGithubUserIds?: number[];
  status?: "todo" | "in_progress" | "in_review" | "done"; priority?: "urgent" | "high" | "normal" | "low"; size?: "S" | "M" | "L";
};

type CreatedIssue = IssueDetail & { droppedAssignees: number[] };   // GitHub user ids GitHub did not assign
type PendingIssue = { repository: string; number: number; htmlUrl: string; mirrorPending: true };  // 202, not on the board yet

type SetAssigneesRequest = { assigneeGithubUserIds: number[] };
type AssignedIssue = IssueDetail & { droppedAssignees: number[] };   // GitHub user ids GitHub did not assign

type AddCommentRequest = { body: string };
type PostedComment = {
  id: string | null;                 // null with mirrorPending: on GitHub, in the panel after the next sync
  author: { githubLogin: string | null; displayName: string | null };
  body: string | null;               // null in a replay when the connection does not store bodies
  createdAt: string; htmlUrl: string | null; mirrorPending: boolean;
};

type MeetingNoteSummary = {
  id: string; title: string; heldAt: string;
  project: { id: string; name: string } | null;
  actionItemCount: number; linkedActionItemCount: number;
};

type MeetingNote = {
  id: string; title: string; heldAt: string; bodyMd: string;
  project: { id: string; name: string } | null;
  decisions: string[];                                  // 결정한 것, in order
  attendees: Member[];
  actionItems: MeetingActionItem[];                     // in order
  createdBy: Member | null;                             // null for seeded notes or a removed author
  createdAt: string; updatedAt: string;                 // send updatedAt back as expectedUpdatedAt
};

type MeetingActionItem = {
  id: string; text: string; assignee: Member | null; dueDate: string | null;   // yyyy-MM-dd
  issue: { id: string; number: number; repositoryName: string; status: Status;
           state: "open" | "closed"; htmlUrl: string | null } | null;        // state as mirrored from GitHub
  pendingIssue: { repositoryName: string; number: number } | null;           // created on GitHub, not mirrored yet
  creatingIssue: boolean;                                                     // a create request is running now
};

type ActionItemIssueRequest = { repositoryId: string };
type ActionItemIssueResult = { actionItem: MeetingActionItem; created: boolean; droppedAssignees: number[] };
type PendingActionItemIssue = { actionItemId: string; repository: string; number: number; htmlUrl: string | null; mirrorPending: true };

type MeetingNoteRequest = {
  id?: string;                                          // POST only: client-generated, makes a resent POST safe
  title: string; heldAt: string; projectId?: string | null; bodyMd?: string; decisions?: string[];
  attendeeIds?: string[];
  actionItems?: { id?: string; text: string; assigneeId?: string | null; dueDate?: string | null }[];
  expectedUpdatedAt?: string;                           // PATCH only, required there
};

type SearchResult = {
  issues: { id: string; repositoryName: string; number: number; title: string; status: Status }[];
  comments: { issueId: string; issueTitle: string; snippet: string }[];
  meetingNotes: { id: string; title: string }[];
};
```

Comment authors and assignees are resolved to members by `github_user_id`; unknown users keep
`displayName: null`.
