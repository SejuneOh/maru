// Mirrors docs/api-contract.md. Keep the two in sync.

/** One entry per membership: the same person appears once per tenant. */
export type DevUser = {
  githubLogin: string;
  githubUserId: number;
  displayName: string | null;
  tenantId: string;
  tenantName: string;
};

export type Member = {
  id: string;
  githubUserId: number;
  githubLogin: string;
  displayName: string | null;
  avatarUrl: string | null;
  role: "admin" | "member";
  /**
   * Set when GitHub reported the person left the connected organization; null otherwise. Only `GET /api/members` fills it.
   * Unrelated to removal by an admin: removed members are left out of `GET /api/members` altogether.
   */
  removedAt?: string | null;
};

export type Me = {
  member: Member;
  tenant: { id: string; name: string };
  settings: { sprintsEnabled: boolean; sizeEnabled: boolean; priorityEnabled: boolean };
};

/** Who sent an invitation; null once that member row is gone. */
export type InvitedBy = { githubLogin: string; displayName: string | null };

/** `GET /api/invitations` (admin): open invitations, newest first. Expired ones are included; compare `expiresAt`. */
export type Invitation = {
  id: string;
  /** Resolved from the login when invited; joining matches this id, never the login. */
  githubUserId: number;
  /** The login at invite time, for display only. */
  githubLogin: string;
  role: "member";
  invitedBy: InvitedBy | null;
  createdAt: string;
  expiresAt: string;
};

/** `POST /api/invitations` 201. `token` is shown once (null in an `Idempotency-Key` replay); the link is `/join#<token>`. */
export type CreatedInvitation = { invitation: Invitation; token: string | null };

export type InviteError =
  | "invalid_request"
  | "invalid_login"
  | "github_user_not_found"
  | "already_member"
  | "github_rate_limited"
  | "github_unavailable"
  | "admin_only";

/** `DELETE /api/members/{id}` and `DELETE /api/invitations/{id}` errors. */
export type RemoveError = "not_found" | "last_admin" | "admin_only";

/** `PATCH /api/members/{id}` errors (`error` field). */
export type ChangeRoleError = "invalid_request" | "not_found" | "last_admin" | "member_removed" | "admin_only";

/** `POST /api/invitations/preview` 200. Only returned to the invited GitHub account. */
export type InvitationPreview = {
  tenant: { id: string; name: string };
  githubLogin: string;
  role: "member";
  invitedBy: InvitedBy | null;
  expiresAt: string;
  alreadyMember: boolean;
};

/** `POST /api/invitations/accept` 200. */
export type JoinedTenant = { tenant: { id: string; name: string }; member: Member };

export type JoinError =
  | "unauthenticated"
  | "invalid_request"
  | "invite_not_found"
  | "invite_other_account"
  | "invite_used"
  | "invite_revoked"
  | "invite_expired"
  | "login_taken";

/** POST /api/tenants body. */
export type CreateTenantRequest = { name: string };

/** POST /api/tenants 201 response. Errors: 401 unauthenticated, 400 invalid_name, 409 tenant_limit. */
export type CreateTenantResult = {
  tenant: { id: string; name: string };
  member: Member;
};

export type GithubConnection = {
  id: string;
  accountId: number;
  accountLogin: string;
  accountType: "organization" | "user";
  /** GitHub App installation; null while the connection uses a token. */
  installationId: number | null;
  repositorySelection: "all" | "selected" | null;
  createdAt: string;
  disconnectedAt: string | null;
  /** Current organization members; null for personal accounts or when the members could not be read. */
  orgMemberCount: number | null;
  /** How the board reaches GitHub: the GitHub App or a personal access token. */
  authKind: "app" | "token";
  /** Token connections only. The token itself is never returned. */
  tokenLast4: string | null;
  /** GitHub login the token writes as. */
  tokenOwnerLogin: string | null;
  tokenExpiresAt: string | null;
  /** `token_required`: no token stored (owner left, or never set). Null for App connections. */
  tokenStatus: "active" | "invalid" | "expired" | "revoked" | "token_required" | null;
  /** Active and expiring within 7 days. */
  tokenExpiresSoon: boolean;
  tokenDaysLeft: number | null;
  /** Rate-limit pause of the periodic sync; null when not paused. */
  syncPausedUntil: string | null;
  /** Classic scopes; empty for a fine-grained token. */
  tokenScopes: string[] | null;
  /** Issue and comment text is mirrored. Off for token connections; there is no endpoint to change it. */
  storeBodies: boolean;
};

/** `POST /api/connections/github-token` body. The token is sent once and never returned. */
export type ConnectGithubTokenRequest = { token: string; organization: string };

/** `PUT /api/connections/{id}/github-token` body. */
export type ReplaceGithubTokenRequest = { token: string };

/** `GET /api/connections/{id}/organization/preview`. Admin only; token connections of organizations. */
export type OrganizationPreview = {
  organization: {
    id: number;
    login: string;
    name: string | null;
    description: string | null;
    htmlUrl: string | null;
    avatarUrl: string | null;
  };
  repositories: {
    /** GitHub repository id, the value to send back in a selection. */
    id: number;
    name: string;
    fullName: string;
    htmlUrl: string | null;
    archived: boolean;
    private: boolean;
    description: string | null;
    /** Currently synced. */
    selected: boolean;
    /** Null beyond the first 30 non-archived repositories. */
    labelCount: number | null;
    milestoneCount: number | null;
  }[];
  teams: { id: number; name: string; slug: string; description: string | null; privacy: string | null }[];
  members: { id: number; login: string; avatarUrl: string | null; onBoard: boolean }[];
  counts: {
    repositories: number;
    teams: number;
    members: number;
    labels: number;
    milestones: number;
    countedRepositories: number;
  };
  /** True when the list hit the page cap; `counts` is true when labels and milestones were counted for a subset. */
  truncated: { repositories: boolean; teams: boolean; members: boolean; counts: boolean };
};

/** `POST /api/connections/{id}/organization/import` body. The selection replaces the previous one. */
export type ImportOrganizationRequest = {
  /** GitHub repository ids, 1 to 1000. */
  repositories: number[];
  /** Team slugs to create board projects from (optional, at most 50). */
  teamProjects?: string[];
};

export type ImportedProject = { id: string; name: string; repositoryCount: number };

/** A team that got no project: no selected repository, or a project with that name exists already. */
export type SkippedTeam = { slug: string; reason: "no_selected_repositories" | "name_taken" };

export type ImportOrganizationResult = {
  selected: number;
  /** Repositories that were selected before and are not any more. */
  deselected: number;
  projects: ImportedProject[];
  skippedTeams: SkippedTeam[];
  membersReferenced: number;
};

/** `PUT /api/connections/{id}/organization/repositories` body; 0 to 1000 ids. */
export type UpdateRepositorySelectionRequest = { repositories: number[] };

export type RepositorySelectionResult = { selected: number; deselected: number };

/** Error codes of the organization import endpoints (`error` field). */
export type OrganizationImportError =
  | "invalid_request"
  | "admin_only"
  | "not_found"
  | "credential_missing"
  | "credential_revoked"
  | "credential_invalid"
  | "credential_expired"
  | "unknown_repository" // also `ids`
  | "unknown_team" // also `slugs`
  | "github_rate_limited"
  | "github_unavailable";

/** Error codes of the token connection endpoints (`error` field). */
export type ConnectGithubTokenError =
  | "invalid_request"
  | "admin_only"
  | "not_found"
  | "invalid_token"
  | "unsupported_token"
  | "not_your_account"
  | "token_scope_too_broad" // also `scopes`: the refused scopes
  | "token_scope_missing" // also `missing`
  | "not_org_member"
  | "org_membership_inactive"
  | "org_access_denied"
  | "sso_not_authorized" // also `ssoUrl`
  | "organization_mismatch"
  | "account_connected"
  | "credential_store_not_configured"
  | "github_unavailable";

/** The company's open organization install request (GitHub `setup_action=request`). */
export type ConnectionRequest = {
  id: string;
  requestedAt: string;
  requestedByLogin: string | null;
  accountLogin: string | null;
};

export type ConnectionRequestState = { request: ConnectionRequest | null };

export type ConnectionRequestCheck =
  | { status: "pending"; connection: null }
  | { status: "connected"; connection: GithubConnection };

/** Body of `409 multiple_organizations` from the request check. */
export type OrganizationChoice = { accountId: number; login: string };

export type ConnectGithubRequest = {
  code: string;
  installationId: number;
};

export type ConnectGithubError =
  | "invalid_request"
  | "invalid_code"
  | "unauthenticated"
  | "admin_only"
  | "not_your_account"
  | "not_your_installation"
  | "not_org_owner"
  | "not_org_member"
  | "account_connected"
  | "org_members_not_granted"
  | "unsupported_account"
  | "github_unavailable"
  | "github_app_not_configured";

export type ConnectionRequestError =
  | "unauthenticated"
  | "admin_only"
  | "no_request"
  | "not_org_owner"
  | "account_connected"
  | "multiple_organizations"
  | "org_members_not_granted"
  | "github_unavailable"
  | "github_app_not_configured";

export type Project = {
  id: string;
  name: string;
  color: string | null;
  repositories: { id: string; name: string }[];
};

/** `cancelled` is a closed issue GitHub marks "not planned"; it is never counted as progress. */
export type Status = "todo" | "in_progress" | "in_review" | "done" | "cancelled";
export type Category = "todo" | "in_progress" | "done";
export type Size = "S" | "M" | "L";
export type Priority = "urgent" | "high" | "normal" | "low";

export type Assignee = { githubUserId: number; githubLogin: string; displayName: string | null };

/** `color` is six hex digits without `#`, as GitHub sends it. */
export type IssueLabel = { id: string; name: string; color: string | null };

export type BoardIssue = {
  id: string;
  repository: { id: string; name: string };
  projectIds: string[];
  number: number;
  title: string;
  issueType: string | null;
  state: "open" | "closed";
  status: Status;
  category: Category;
  assignees: Assignee[];
  openPullRequestCount: number;
  commentCount: number;
  parentIssueId: string | null;
  sprint: { id: string; name: string } | null;
  size: Size | null;
  priority: Priority | null;
  updatedAt: string;
  /** `board_fields.updated_at`; send it back as `expectedUpdatedAt` when changing the card. */
  boardUpdatedAt: string | null;
  closedAt: string | null;
  htmlUrl: string | null;
  labels: IssueLabel[];
  milestone: { id: string; title: string; dueOn: string | null } | null;
  /** The sprint a carry-over (not undone) moved this issue from into its current sprint. */
  carriedFrom: { id: string; name: string } | null;
};

export type IssueDetail = BoardIssue & {
  body: string | null;
  parent: { id: string; number: number; title: string } | null;
  children: { id: string; repositoryName: string; number: number; title: string; status: Status }[];
  pullRequests: {
    id: string;
    number: number;
    title: string;
    state: "open" | "closed" | "merged";
    draft: boolean;
    reviewRequested: boolean;
    htmlUrl: string | null;
  }[];
  comments: {
    id: string;
    author: { githubLogin: string | null; displayName: string | null };
    body: string;
    createdAt: string;
  }[];
  meetingNotes: { id: string; title: string; heldAt: string }[];
  /** The newest status, priority or size change someone made on the board; null when nobody has. */
  lastBoardChange: { at: string; by: { githubLogin: string | null; displayName: string | null } } | null;
};

/** `PATCH /api/issues/{id}/board` body. A present `null` clears the field. */
export type BoardChangeRequest = {
  status?: Status | null;
  priority?: Priority | null;
  size?: Size | null;
  expectedUpdatedAt?: string | null;
};

export type BoardChangeError =
  | "invalid_request"
  | "not_found"
  | "conflict"
  | "field_disabled"
  | "repository_archived"
  | "github_write_not_granted"
  | "credential_missing"
  | "credential_revoked"
  | "credential_invalid"
  | "credential_expired"
  | "github_unavailable"
  | "request_in_progress"
  | "idempotency_key_reused";

/** `GET /api/issues/new-options`: repositories the caller can create in (archived last, no labels), and every member. */
export type NewIssueOptions = {
  repositories: {
    id: string;
    name: string;
    fullName: string;
    archived: boolean;
    labels: { name: string; color: string | null }[];
  }[];
  assignees: { githubUserId: number; githubLogin: string; displayName: string | null; avatarUrl: string | null }[];
};

/** Board status a new issue may start in. */
export type NewIssueStatus = "todo" | "in_progress" | "in_review" | "done";

/** `POST /api/issues` body; the request also needs an `Idempotency-Key` header. */
export type CreateIssueRequest = {
  repositoryId: string;
  title: string;
  body?: string;
  labels?: string[];
  assigneeGithubUserIds?: number[];
  status?: NewIssueStatus;
  priority?: Priority;
  size?: Size;
};

/** 201: on GitHub and on the board. `droppedAssignees` are GitHub user ids GitHub did not assign. */
export type CreatedIssue = IssueDetail & { droppedAssignees: number[] };

/** 202: on GitHub, on the board only after the next sync; status, priority and size were not saved. */
export type PendingIssue = { repository: string; number: number; htmlUrl: string; mirrorPending: true };

/** `POST /api/issues` error codes (`error` field). */
export type CreateIssueError =
  | "invalid_request"
  | "not_found"
  | "repository_archived"
  | "github_write_not_granted"
  | "credential_missing"
  | "credential_revoked"
  | "credential_invalid"
  | "credential_expired"
  | "assignee_not_collaborator"
  | "github_unavailable"
  | "request_in_progress"
  | "idempotency_key_reused";

/** `PATCH /api/issues/{id}` body: the whole assignee list (members only, max 10). */
export type SetAssigneesRequest = { assigneeGithubUserIds: number[] };

/** `PATCH /api/issues/{id}` answer. `droppedAssignees` are GitHub user ids GitHub did not assign. */
export type AssignedIssue = IssueDetail & { droppedAssignees: number[] };

/** `PATCH /api/issues/{id}` error codes (`error` field). */
export type SetAssigneesError =
  | "invalid_request"
  | "not_found"
  | "repository_archived"
  | "github_write_not_granted"
  | "credential_missing"
  | "credential_revoked"
  | "credential_invalid"
  | "credential_expired"
  | "assignee_not_collaborator"
  | "github_unavailable"
  | "request_in_progress"
  | "idempotency_key_reused";

/** `POST /api/issues/{id}/comments` body; the request also needs an `Idempotency-Key` header. */
export type AddCommentRequest = { body: string };

/** 201. `id` is null with `mirrorPending`: on GitHub, in the panel after the next sync. */
export type PostedComment = {
  id: string | null;
  author: { githubLogin: string | null; displayName: string | null };
  body: string | null; // null in a replay when the connection does not store bodies
  createdAt: string;
  htmlUrl: string | null;
  mirrorPending: boolean;
};

/** `POST /api/issues/{id}/comments` error codes (`error` field). */
export type AddCommentError =
  | "invalid_request"
  | "not_found"
  | "repository_archived"
  | "github_write_not_granted"
  | "credential_missing"
  | "credential_revoked"
  | "credential_invalid"
  | "credential_expired"
  | "github_unavailable"
  | "request_in_progress"
  | "idempotency_key_reused";

export type MeetingNoteSummary = {
  id: string;
  title: string;
  heldAt: string;
  project: { id: string; name: string } | null;
  actionItemCount: number;
  linkedActionItemCount: number;
};

export type MeetingActionItem = {
  id: string;
  text: string;
  assignee: Member | null;
  /** `yyyy-MM-dd`. */
  dueDate: string | null;
  /** `state` as mirrored from GitHub. */
  issue: {
    id: string;
    number: number;
    repositoryName: string;
    status: Status;
    state: "open" | "closed";
    htmlUrl: string | null;
  } | null;
  /** GitHub created the issue but the board has not mirrored it yet. */
  pendingIssue: { repositoryName: string; number: number } | null;
  /** A request is creating the issue right now. */
  creatingIssue: boolean;
};

/** `POST /api/meeting-action-items/{id}/issue` (needs `Idempotency-Key`). */
export type ActionItemIssueRequest = { repositoryId: string };

/** 201 (`created: true`) or 200 (the item already had this issue). */
export type ActionItemIssueResult = { actionItem: MeetingActionItem; created: boolean; droppedAssignees: number[] };

/** 202: on GitHub, not on the board yet. */
export type PendingActionItemIssue = {
  actionItemId: string;
  repository: string;
  number: number;
  /** Null (and `repository` empty) when the repository is no longer in the mirror. */
  htmlUrl: string | null;
  mirrorPending: true;
};

export type MeetingNote = {
  id: string;
  title: string;
  heldAt: string;
  bodyMd: string;
  project: { id: string; name: string } | null;
  /** 결정한 것, in order. */
  decisions: string[];
  attendees: Member[];
  /** In display order. */
  actionItems: MeetingActionItem[];
  /** Null for seeded notes or when the author left. */
  createdBy: Member | null;
  createdAt: string;
  /** Sent back as `expectedUpdatedAt` on the next save. */
  updatedAt: string;
};

/** `POST /api/meeting-notes`, and `PATCH /api/meeting-notes/{id}` (every field, plus `expectedUpdatedAt`). */
export type MeetingNoteRequest = {
  /** POST only: an id the client generates, so a resent POST returns the note (200) instead of creating another. */
  id?: string;
  title: string;
  heldAt: string;
  projectId: string | null;
  bodyMd: string;
  decisions: string[];
  attendeeIds: string[];
  /** An existing item keeps its `id`; a new one has none. Leaving out an item linked to an issue is refused. */
  actionItems: { id?: string; text: string; assigneeId: string | null; dueDate: string | null }[];
  expectedUpdatedAt?: string;
};

/** Meeting note write errors (`error` field). `action_item_linked` carries `actionItemIds: string[]`. */
export type MeetingNoteWriteError =
  | "invalid_request"
  | "not_found"
  | "forbidden"
  | "conflict"
  | "action_item_linked"
  | "id_taken"
  | "idempotency_key_reused";

export type OverviewIssueRef = {
  id: string;
  repositoryName: string;
  number: number;
  title: string;
  status: Status;
  updatedAt: string;
  assignees: Assignee[];
};

export type StatusCounts = { todo: number; inProgress: number; inReview: number; done: number };

export type OverviewNoteRef = { id: string; title: string; heldAt: string };

export type ActivityKind = "comment" | "pr_opened" | "pr_review_requested" | "pr_merged" | "issue_closed" | "meeting_note";

/** `GET /api/overview`. doneThisWeek counts from Monday 00:00 Asia/Seoul; sprint dates are yyyy-MM-dd. */
export type Overview = {
  generatedAt: string;
  counts: { open: number; inProgress: number; inReview: number; stale: number; doneThisWeek: number };
  attention: {
    urgent: { issue: OverviewIssueRef; notStarted: boolean }[];
    reviewWaiting: {
      issue: OverviewIssueRef;
      pullRequest: { number: number; title: string; authorGithubLogin: string | null; waitingSince: string };
    }[];
    stale: { issue: OverviewIssueRef; idleDays: number }[];
    unassigned: OverviewIssueRef[];
    actionItemsWithoutIssue: { id: string; text: string; assignee: Member | null; note: OverviewNoteRef }[];
    /** De-duplicated counts before the 5-per-group cut; `stale` excludes issues already in an earlier group. */
    totals: {
      urgent: number;
      urgentNotStarted: number;
      reviewWaiting: number;
      stale: number;
      unassigned: number;
      actionItemsWithoutIssue: number;
    };
  };
  people: {
    member: Member;
    todo: number;
    inProgress: number;
    inReview: number;
    doneThisWeek: number;
    overloaded: boolean;
    current: OverviewIssueRef | null;
    lastActivityAt: string | null;
  }[];
  projects: {
    project: { id: string; name: string; color: string | null; repositories: string[] };
    byStatus: StatusCounts;
    openPullRequests: number;
    latestNote: OverviewNoteRef | null;
  }[];
  sprint: {
    id: string;
    name: string;
    startDate: string;
    endDate: string;
    daysLeft: number;
    byStatus: StatusCounts;
    /** Recorded days only, oldest first; a day the sync missed has no entry. */
    trend: SprintDay[];
  } | null;
  milestones: null;
  actionItems: {
    id: string;
    text: string;
    assignee: Member | null;
    issue: { id: string; repositoryName: string; number: number; status: Status } | null;
    note: OverviewNoteRef;
  }[];
  activity: {
    kind: ActivityKind;
    at: string;
    actor: { githubLogin: string | null; displayName: string | null } | null;
    issue: { id: string; repositoryName: string; number: number; title: string } | null;
    pullRequest: { repositoryName: string; number: number; title: string } | null;
    note: { id: string; title: string } | null;
    snippet: string | null;
  }[];
};

export type SearchResult = {
  issues: { id: string; repositoryName: string; number: number; title: string; status: Status }[];
  comments: { issueId: string; issueTitle: string; snippet: string }[];
  meetingNotes: { id: string; title: string }[];
};

/** `GET /api/milestones`: open first, then by due date (none last). Counts leave out cancelled issues. */
export type Milestone = {
  id: string;
  repository: { id: string; name: string };
  number: number;
  title: string;
  state: "open" | "closed";
  dueOn: string | null;
  htmlUrl: string | null;
  issueCount: number;
  doneCount: number;
};

/** `GET /api/sync/status`. With no active GitHub connection, `connected` is false and the rest is empty. */
export type SyncStatus = {
  connected: boolean;
  running: boolean;
  lastSyncedAt: string | null;
  lastResult: "ok" | "failed" | null;
  lastError: string | null;
  rateLimitRemaining: number | null;
  nextRunAt: string | null;
  tokens: TokenHealth[];
};

/** One active token connection in `GET /api/sync/status`. */
export type TokenHealth = {
  connectionId: string;
  accountLogin: string;
  /** Null while no token is stored. */
  tokenOwnerLogin: string | null;
  tokenStatus: "active" | "invalid" | "expired" | "revoked" | "token_required";
  tokenExpiresAt: string | null;
  tokenExpiresSoon: boolean;
  tokenDaysLeft: number | null;
  syncPausedUntil: string | null;
};

/** `POST /api/sync/run` errors (`error` field). */
export type RunSyncError = "sync_running" | "admin_only" | "not_connected" | "github_app_not_configured";

/** `GET /api/sprints`. Dates are yyyy-MM-dd; counts leave out cancelled issues. */
export type Sprint = {
  id: string;
  name: string;
  startDate: string;
  endDate: string;
  state: "planned" | "active" | "closed";
  issueCount: number;
  doneCount: number;
  unfinishedCount: number;
  createdBy: string | null;
  createdAt: string;
  /** Send back in `PATCH /api/sprints/{id}`. */
  updatedAt: string;
};

export type SprintList = { current: Sprint[]; upcoming: Sprint[]; past: Sprint[] };

export type SprintIssuesResult = { sprint: Sprint; added: number; moved: number; removed: number; unchanged: number };

export type BacklogPage = { items: BoardIssue[]; total: number; page: number; pageSize: number };

export type SprintForm = { name: string; startDate: string; endDate: string };

/** Sprint write errors (`error` field). */
export type SprintWriteError =
  | "sprint_ended"
  | "sprints_disabled"
  | "name_taken"
  | "invalid_dates"
  | "invalid_request"
  | "conflict"
  | "not_found";

/** One day of `sprint_daily` (Asia/Seoul). `remaining` = scope - done; cancelled issues are in neither. */
export type SprintDay = { day: string; scope: number; remaining: number; scopeWeight: number; remainingWeight: number };

/** `GET /api/projects/done-by-week`: `weeks` are Monday dates, oldest first, the last is this week; `done[i]` belongs to `weeks[i]`. */
export type DoneByWeek = { weeks: string[]; projects: { projectId: string; done: number[] }[] };

/** `GET /api/projects/{id}/schedule`. Dates are yyyy-MM-dd in Asia/Seoul; `overdueDays` is 0 unless past its date and not finished. */
export type ProjectSchedule = {
  projectId: string;
  today: string;
  sprintsEnabled: boolean;
  /** The project's repositories' milestones: open ones, and closed ones from the last 60 days. Undated last. */
  milestones: ScheduleMilestone[];
  /** Sprints that have not ended, and ended ones with this project's work (last 60 days, or still unfinished). By start date. */
  sprints: ScheduleSprint[];
};

export type ScheduleMilestone = {
  id: string;
  repository: { id: string; name: string };
  number: number;
  title: string;
  state: "open" | "closed";
  dueDate: string | null;
  htmlUrl: string | null;
  issueCount: number;
  doneCount: number;
  overdueDays: number;
};

/** Counts cover only the project's issues in the sprint; cancelled left out of `issueCount`. */
export type ScheduleSprint = {
  id: string;
  name: string;
  startDate: string;
  endDate: string;
  state: "planned" | "active" | "closed";
  issueCount: number;
  doneCount: number;
  unfinishedCount: number;
  overdueDays: number;
};
