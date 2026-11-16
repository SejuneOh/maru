# API contract (local skeleton)

Base URL: `http://localhost:5080`. All JSON is camelCase. Times are ISO 8601 UTC strings.

## Identity

Logged-in requests send `Authorization: Bearer <Supabase access token>`. When the person belongs to
several tenants they also send `X-Tenant: <tenantId>`.

- The API validates signature, issuer (`{SupabaseUrl}/auth/v1`), audience `authenticated` and
  lifetime. Keys come from `{SupabaseUrl}/auth/v1/.well-known/jwks.json`, or from the shared secret
  when `Auth:Supabase:JwtSecret` is set (legacy HS256 projects).
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
| GET | `/api/members` | dev user | `Member[]` |
| GET | `/api/projects` | dev user | `Project[]` |
| GET | `/api/connections` | dev user | `GithubConnection[]`, active first |
| POST | `/api/connections/github` | dev user, admin | `GithubConnection` (`201` new, `200` existing updated) |
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
| GET | `/api/search?q=` | dev user | `SearchResult` |

`POST /api/tenants` creates a company for a signed-in person (bearer token, or `X-Dev-User` with a known
login in Development) and makes them its `admin`. Body `{"name": "<string>"}`; the name is trimmed and must be
1..80 characters without control or invisible format characters (zero-width, bidi overrides); the request must be `application/json`. One transaction on the `workboard_sync` role inserts the tenant,
its `tenant_settings` row (all flags off) and the member (GitHub id, login, display name and avatar from the
GitHub identity). Response `201 {"tenant": {"id", "name"}, "member": Member}`; send `X-Tenant: <tenant.id>`
afterwards. Errors: `401 unauthenticated` (no valid sign-in, or the GitHub identity has no login),
`400 invalid_name` (also for a body that is not JSON), `409 tenant_limit` (the person already created 5
tenants; tenants they only joined do not count).

`POST /api/connections/github` connects the caller's personal GitHub account after they install the GitHub App.
Body `{"code": "<oauth code>", "installationId": <number>}` (`application/json`), both from the redirect to
`/connect/github`. The server exchanges `code` for a user token (never stored or logged), checks that the token's
GitHub user is the caller and that the installation is in that user's installations, reads the account from the
App's own installation record, then inserts (or, for the same tenant's active account, updates) the
`github_connections` row on the `workboard_sync` role in one transaction. Response is the `GithubConnection`.
Errors: `400 invalid_request` (missing code or installationId, not JSON), `400 invalid_code`,
`401 unauthenticated`, `403 admin_only`, `403 not_your_account`, `403 not_your_installation`,
`409 account_connected` (active in another tenant), `422 organization_not_supported`,
`502 github_unavailable` (GitHub call failed), `503 github_app_not_configured`.
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
| `doneSince` | `week` keeps only issues closed since Monday 00:00 in Asia/Seoul (same boundary as the overview's `doneThisWeek`); open issues are dropped and `doneWithinDays` is ignored. Any other value → `400 {"error": "invalid_doneSince"}` |
| `scope` | `all` includes every closed issue regardless of `doneWithinDays` (use it for progress over all issues). Any other value → `400 {"error": "invalid_scope"}` |
| `doneWithinDays` | closed issues are included only if closed within N days, integer 0..365 (default 7). Ignored when `scope=all`. Out of range → `400 {"error": "invalid_doneWithinDays"}` |

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
  sprint, none has an `issue_events` row newer than the carry-over (`closed` / `reopened` rows with no member, recorded by a sync or when a board write mirrors an outside change, do not count), none is now `done`/`cancelled`, and none was deleted:
  otherwise `409 conflict` with `issueIds` listing the blocking issues, and a second undo is `409 already_undone`. Strict rule: an older carry-over cannot be undone once a later carry-over
  or undo touched the same issues. Records `carried_over` events in reverse.
- `GET /api/backlog`: open issues whose status is not `done`/`cancelled` and that are in no sprint. Optional `projectId`,
  `person` (alias `assignee`), `status` (comma list of `todo`, `in_progress`, `in_review`, else `400 invalid_status`),
  `page` (1..10000) and `pageSize` (1..200, default 50), else `400 invalid_request`. Sort like the issue list.
- `carriedFrom` on every `BoardIssue` (read only): the source sprint of the latest carry-over, not undone, that moved the
  issue into the sprint it is in now, as long as no `sprint_added` / `sprint_removed` event for the issue is newer than that
  carry-over. Otherwise `null`: no sprint, added by hand, or moved by hand since (also when it was moved away and back). An undo clears it. The planning screen shows it as the "넘어온 일" flag.

Daily sprint numbers (`sprint_daily`, table only, no endpoint yet; the remaining-work chart reads it later):

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

`GET /api/milestones` counts exclude `cancelled` issues in `issueCount`; `doneCount` counts status `done`.
`color` of a label is 6 hex characters without `#`, as GitHub sends it.

`GET /api/meeting-notes` accepts optional `projectId`. Sort: `heldAt` descending.

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
- `milestones` is always `null` until sync provides milestones.
- `attention.actionItemsWithoutIssue` covers every meeting action item without an issue (no date window);
  `actionItems` covers notes held in the last 14 days whose item has no issue or an unfinished one.
- `activity`: last 3 days, newest first, max 12. A PR event kind is inferred from its current state (merged,
  open with review requested, other open); closed-unmerged PRs are skipped. `actor` is null when the author is not a member.

## Sync

A background service syncs every tenant with an active connection that has an `installation_id` each
`Sync:IntervalMinutes` (default 10; `Sync:Enabled=false` turns the loop off, manual runs still work). A run lists the
installation's repositories, upserts them (`sync_enabled` defaults to true) and reads each enabled repository with
`since = last_synced_at - 2 minutes` (full read on the first run). One run per tenant at a time. Each run writes one
`sync_runs` row per connection; GitHub errors and a missing App key are recorded there, never thrown. A repository
that fails does not stop the others, except on `RateLimited` and `Unauthorized`, which end the run.

`GET /api/sync/status` (any member) -> `200`:
```
{ "connected": true, "running": false, "lastSyncedAt": "ISO" | null, "lastResult": "ok" | "failed" | null,
  "lastError": "short text" | null, "rateLimitRemaining": 4990 | null, "nextRunAt": "ISO" | null }
```
`connected` is false when the tenant has no active connection with an installation; the other fields are then null/false.
`lastSyncedAt` is the time of the most recent successful repository sync (not the last attempt); `lastResult`, `lastError` and `rateLimitRemaining` come from the most
recent finished run. `nextRunAt` is null while the periodic loop is off.

`POST /api/sync/run` (admin only) starts a run in the background -> `202 { "started": true }`. Errors, checked in this
order: `403 { "error": "admin_only" }`, `503 { "error": "github_app_not_configured" }`, `409 { "error": "not_connected" }`,
`409 { "error": "sync_running" }`.

## Shapes

```ts
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
            byStatus: StatusCounts } | null;
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
};

type Project = { id: string; name: string; color: string | null; repositories: { id: string; name: string }[] };

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
  body: string; createdAt: string; htmlUrl: string | null; mirrorPending: boolean;
};

type MeetingNoteSummary = {
  id: string; title: string; heldAt: string;
  project: { id: string; name: string } | null;
  actionItemCount: number; linkedActionItemCount: number;
};

type MeetingNote = {
  id: string; title: string; heldAt: string; bodyMd: string;
  project: { id: string; name: string } | null;
  attendees: Member[];
  actionItems: { id: string; text: string; assignee: Member | null;
                 issue: { id: string; number: number; repositoryName: string; status: Status } | null }[];
};

type SearchResult = {
  issues: { id: string; repositoryName: string; number: number; title: string; status: Status }[];
  comments: { issueId: string; issueTitle: string; snippet: string }[];
  meetingNotes: { id: string; title: string }[];
};
```

Comment authors and assignees are resolved to members by `github_user_id`; unknown users keep
`displayName: null`.
