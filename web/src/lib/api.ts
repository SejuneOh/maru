"use client";

import { useCallback, useEffect, useState } from "react";
import { AUTH_EVENT, GATE_EVENT, authEnabled, getAccessToken, getTenant, signOutIfStill } from "./auth";
import type {
  Project,
  ActionItemIssueResult,
  AddCommentRequest,
  AssignedIssue,
  BoardChangeRequest,
  BoardIssue,
  ConnectionRequest,
  ConnectionRequestCheck,
  ConnectGithubTokenRequest,
  CreateIssueRequest,
  CreateTenantRequest,
  CreateTenantResult,
  CreatedInvitation,
  CreatedIssue,
  GithubConnection,
  ImportOrganizationRequest,
  ImportOrganizationResult,
  InvitationPreview,
  JoinedTenant,
  MeetingNote,
  Member,
  MeetingNoteRequest,
  Milestone,
  PendingActionItemIssue,
  PendingIssue,
  PostedComment,
  ReplaceGithubTokenRequest,
  RepositorySelectionResult,
  SetAssigneesRequest,
  Sprint,
  SprintForm,
  SprintIssuesResult,
  SyncStatus,
  UpdateRepositorySelectionRequest,
} from "./types";

const API_URL = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5080";
const DEV_USER_KEY = "workboard.devUser";
const DEV_TENANT_KEY = "workboard.devTenant";
export const DEV_USER_EVENT = "workboard:dev-user";
/** Fired after a write that changes issues, so every board refetches its issue lists. */
export const ISSUES_CHANGED_EVENT = "workboard:issues-changed";
/** Fired after a connection or its token changes, so the sync status refetches. */
export const SYNC_CHANGED_EVENT = "workboard:sync-changed";

/** The same login can belong to several tenants, so a dev identity is the pair. */
export type DevIdentity = { login: string; tenantId: string | null };

function readStorage(key: string): string | null {
  try {
    return window.localStorage.getItem(key);
  } catch {
    return null;
  }
}

function writeStorage(key: string, value: string | null) {
  try {
    if (value) window.localStorage.setItem(key, value);
    else window.localStorage.removeItem(key);
  } catch {
    // Storage can be unavailable (private mode); the picker simply asks again next time.
  }
}

export function getDevIdentity(): DevIdentity | null {
  const login = readStorage(DEV_USER_KEY);
  return login ? { login, tenantId: readStorage(DEV_TENANT_KEY) } : null;
}

export function setDevIdentity(identity: DevIdentity | null) {
  writeStorage(DEV_USER_KEY, identity?.login ?? null);
  writeStorage(DEV_TENANT_KEY, identity?.tenantId ?? null);
  window.dispatchEvent(new Event(DEV_USER_EVENT));
}

export class ApiError extends Error {
  /** `code` is the `error` field of the response body, e.g. `tenant_required`. */
  constructor(public status: number, message: string, public code: string | null = null, public body: unknown = null) {
    super(message);
  }
}

async function readBody(res: Response): Promise<{ code: string | null; body: unknown }> {
  try {
    const body = (await res.json()) as { error?: unknown } | null;
    return { code: typeof body?.error === "string" ? body.error : null, body };
  } catch {
    return { code: null, body: null };
  }
}

async function authHeaders(): Promise<Record<string, string>> {
  const headers: Record<string, string> = {};
  if (authEnabled) {
    const token = await getAccessToken();
    if (token) headers.Authorization = `Bearer ${token}`;
    const tenant = getTenant();
    if (tenant) headers["X-Tenant"] = tenant;
    return headers;
  }
  const identity = getDevIdentity();
  if (identity) {
    headers["X-Dev-User"] = identity.login;
    if (identity.tenantId) headers["X-Dev-Tenant"] = identity.tenantId;
  }
  return headers;
}

type ReqInit = { method?: string; body?: unknown; headers?: Record<string, string>; keepalive?: boolean };

async function request<T>(path: string, init: ReqInit = {}): Promise<T> {
  return (await send<T>(path, init)).data;
}

async function send<T>(path: string, init: ReqInit = {}): Promise<{ status: number; data: T }> {
  const headers: Record<string, string> = { Accept: "application/json", ...(await authHeaders()), ...init.headers };
  if (init.body !== undefined) headers["Content-Type"] = "application/json";
  const res = await fetch(`${API_URL}${path}`, {
    method: init.method ?? "GET",
    headers,
    body: init.body === undefined ? undefined : JSON.stringify(init.body),
    keepalive: init.keepalive,
  });
  if (!res.ok) {
    const { code, body } = await readBody(res);
    if (res.status === 401 && code === "unauthenticated" && headers.Authorization) {
      // After the caller has stored this error, so the sign-in screen can say why it appeared.
      const token = headers.Authorization.slice("Bearer ".length);
      setTimeout(() => void signOutIfStill(token), 0);
    }
    const refused =
      (res.status === 401 && (code === "tenant_required" || !authEnabled)) || (res.status === 403 && code === "no_membership");
    if (refused && path !== "/api/me") setTimeout(() => window.dispatchEvent(new Event(GATE_EVENT)), 0);
    throw new ApiError(res.status, `${res.status} ${res.statusText}`, code, body);
  }
  return { status: res.status, data: (res.status === 204 ? null : await res.json()) as T };
}

export function apiGet<T>(path: string): Promise<T> {
  return request<T>(path);
}

export function apiPost<T>(path: string, body: unknown): Promise<T> {
  return request<T>(path, { method: "POST", body });
}

/** `POST /api/tenants`. The same key with the same name returns the company the first request created. */
export function createTenant(name: string, idempotencyKey: string): Promise<CreateTenantResult> {
  return request<CreateTenantResult>("/api/tenants", {
    method: "POST",
    body: { name } satisfies CreateTenantRequest,
    headers: { "Idempotency-Key": idempotencyKey },
  });
}

export const CONNECTION_REQUEST_PATH = "/api/connections/github/request";

/** `POST /api/connections/github/request` (admin): records the organization install request GitHub returned. */
export function createConnectionRequest(): Promise<ConnectionRequest> {
  return request<ConnectionRequest>(CONNECTION_REQUEST_PATH, { method: "POST" });
}

/** `POST .../request/check` (admin). Rejects with an `ApiError` whose `code` is a `ConnectionRequestError`. */
export function checkConnectionRequest(accountId?: number): Promise<ConnectionRequestCheck> {
  return apiPost<ConnectionRequestCheck>(`${CONNECTION_REQUEST_PATH}/check`, accountId === undefined ? {} : { accountId });
}

/** `DELETE /api/connections/github/request` (admin). */
export async function cancelConnectionRequest(): Promise<void> {
  await request<null>(CONNECTION_REQUEST_PATH, { method: "DELETE" });
}

/** `POST /api/connections/github-token` (admin). The token goes in the body once and is never returned. */
export function connectGithubToken(token: string, organization: string): Promise<GithubConnection> {
  return apiPost<GithubConnection>("/api/connections/github-token", { token, organization } satisfies ConnectGithubTokenRequest);
}

/** `PUT /api/connections/{id}/github-token` (admin). */
export function replaceGithubToken(id: string, token: string): Promise<GithubConnection> {
  return request<GithubConnection>(`/api/connections/${encodeURIComponent(id)}/github-token`, {
    method: "PUT",
    body: { token } satisfies ReplaceGithubTokenRequest,
  });
}

/** `DELETE /api/connections/{id}/github-token` (admin): disconnects and deletes the stored token. */
export async function deleteGithubToken(id: string): Promise<void> {
  await request<null>(`/api/connections/${encodeURIComponent(id)}/github-token`, { method: "DELETE" });
}

export function organizationPreviewPath(id: string): string {
  return `/api/connections/${encodeURIComponent(id)}/organization/preview`;
}

/** `POST .../organization/import` (admin): replaces the selection and optionally makes projects from teams. */
export function importOrganization(id: string, req: ImportOrganizationRequest): Promise<ImportOrganizationResult> {
  return apiPost<ImportOrganizationResult>(`/api/connections/${encodeURIComponent(id)}/organization/import`, req);
}

/** `PUT .../organization/repositories` (admin): replaces the selection; an empty list stops every repository. */
export function updateRepositorySelection(id: string, repositories: number[]): Promise<RepositorySelectionResult> {
  return request<RepositorySelectionResult>(`/api/connections/${encodeURIComponent(id)}/organization/repositories`, {
    method: "PUT",
    body: { repositories } satisfies UpdateRepositorySelectionRequest,
  });
}

export const MILESTONES_PATH = "/api/milestones";

/** `POST /api/projects` (admin): create a project and pick the repositories to group. */
export function createProject(name: string, repositoryIds: string[]): Promise<Project> {
  return request<Project>("/api/projects", { method: "POST", body: { name, repositoryIds } });
}

/** `PATCH /api/projects/{id}` (admin): rename and/or change the grouped repositories. */
export function updateProject(id: string, patch: { name?: string; repositoryIds?: string[] }): Promise<Project> {
  return request<Project>(`/api/projects/${encodeURIComponent(id)}`, { method: "PATCH", body: patch });
}

/** `DELETE /api/projects/{id}` (admin). */
export async function deleteProject(id: string): Promise<void> {
  await request<null>(`/api/projects/${encodeURIComponent(id)}`, { method: "DELETE" });
}
export const SYNC_STATUS_PATH = "/api/sync/status";

export function getMilestones(): Promise<Milestone[]> {
  return apiGet<Milestone[]>(MILESTONES_PATH);
}

export function getSyncStatus(): Promise<SyncStatus> {
  return apiGet<SyncStatus>(SYNC_STATUS_PATH);
}

/** Admin only. Resolves on 202; rejects with an `ApiError` whose `code` is a `RunSyncError`. */
export function runSync(): Promise<{ started: true }> {
  return request<{ started: true }>("/api/sync/run", { method: "POST" });
}

export const NEW_ISSUE_OPTIONS_PATH = "/api/issues/new-options";

/** One key per form submission; a retry of the same submission sends the same key so GitHub gets one issue. */
export function newIdempotencyKey(): string {
  if (typeof crypto.randomUUID === "function") return crypto.randomUUID();
  const b = crypto.getRandomValues(new Uint8Array(16));
  return Array.from(b, (x) => x.toString(16).padStart(2, "0")).join("");
}

export type CreateIssueResult = { kind: "created"; issue: CreatedIssue } | { kind: "pending"; issue: PendingIssue };

/** `POST /api/issues`. Rejects with an `ApiError` whose `code` is a `CreateIssueError`, or a `TypeError` when offline. */
export async function createIssue(req: CreateIssueRequest, idempotencyKey: string): Promise<CreateIssueResult> {
  const res = await send<CreatedIssue | PendingIssue>("/api/issues", {
    method: "POST",
    body: req,
    headers: { "Idempotency-Key": idempotencyKey },
  });
  return res.status === 202 ? { kind: "pending", issue: res.data as PendingIssue } : { kind: "created", issue: res.data as CreatedIssue };
}

export type ActionItemIssueOutcome =
  | { kind: "linked"; result: ActionItemIssueResult }
  | { kind: "pending"; issue: PendingActionItemIssue };

/**
 * `POST /api/meeting-action-items/{id}/issue`. The server makes at most one issue per item whatever the key; resend the
 * same key after an answer that never came. Rejects with an `ApiError` (`code` as for `createIssue`), or a `TypeError`.
 */
export async function createActionItemIssue(
  itemId: string,
  repositoryId: string,
  idempotencyKey: string,
): Promise<ActionItemIssueOutcome> {
  const res = await send<ActionItemIssueResult | PendingActionItemIssue>(
    `/api/meeting-action-items/${encodeURIComponent(itemId)}/issue`,
    { method: "POST", body: { repositoryId }, headers: { "Idempotency-Key": idempotencyKey } },
  );
  return res.status === 202
    ? { kind: "pending", issue: res.data as PendingActionItemIssue }
    : { kind: "linked", result: res.data as ActionItemIssueResult };
}

/** `PATCH /api/issues/{id}/board`. Rejects with an `ApiError` whose `code` is a `BoardChangeError`, or a `TypeError` when offline. */
export function changeBoard(id: string, req: BoardChangeRequest, idempotencyKey: string): Promise<BoardIssue> {
  return request<BoardIssue>(`/api/issues/${encodeURIComponent(id)}/board`, {
    method: "PATCH",
    body: req,
    headers: { "Idempotency-Key": idempotencyKey },
  });
}

/** `PATCH /api/issues/{id}`. Rejects with an `ApiError` whose `code` is a `SetAssigneesError`, or a `TypeError` when offline. */
export function setAssignees(id: string, req: SetAssigneesRequest, idempotencyKey: string): Promise<AssignedIssue> {
  return request<AssignedIssue>(`/api/issues/${encodeURIComponent(id)}`, {
    method: "PATCH",
    body: req,
    headers: { "Idempotency-Key": idempotencyKey },
  });
}

/** `POST /api/issues/{id}/comments`. Rejects with an `ApiError` whose `code` is an `AddCommentError`, or a `TypeError` when offline. */
export function addComment(id: string, req: AddCommentRequest, idempotencyKey: string): Promise<PostedComment> {
  return request<PostedComment>(`/api/issues/${encodeURIComponent(id)}/comments`, {
    method: "POST",
    body: req,
    headers: { "Idempotency-Key": idempotencyKey },
  });
}

/** `POST` / `DELETE /api/sprints/{id}/issues`, all ids in one request. Rejects with an `ApiError` whose `code` is a `SprintWriteError`. */
export function changeSprintIssues(
  sprintId: string,
  method: "POST" | "DELETE",
  issueIds: string[],
  idempotencyKey: string,
): Promise<SprintIssuesResult> {
  return request<SprintIssuesResult>(`/api/sprints/${encodeURIComponent(sprintId)}/issues`, {
    method,
    body: { issueIds },
    headers: { "Idempotency-Key": idempotencyKey },
  });
}

/** `POST /api/sprints`, or `PATCH /api/sprints/{id}` with the `updatedAt` the form was opened with. */
export function saveSprint(
  form: SprintForm,
  idempotencyKey: string,
  existing?: { id: string; updatedAt: string },
): Promise<Sprint> {
  return existing
    ? request<Sprint>(`/api/sprints/${encodeURIComponent(existing.id)}`, {
        method: "PATCH",
        body: { ...form, updatedAt: existing.updatedAt },
        headers: { "Idempotency-Key": idempotencyKey },
      })
    : request<Sprint>("/api/sprints", { method: "POST", body: form, headers: { "Idempotency-Key": idempotencyKey } });
}

/**
 * `POST /api/meeting-notes` (with the client-generated `req.id`) until the note exists, then `PATCH` with
 * `expectedUpdatedAt`. `created` is false when a POST found the note already there (200): it returns the stored
 * copy, not the body sent. `keepalive` lets a save started as the tab hides finish after it closes; browsers cap
 * such a body at 64 KB. Rejects with an `ApiError` whose `code` is a `MeetingNoteWriteError`, or a `TypeError`.
 */
export async function saveMeetingNote(
  req: MeetingNoteRequest,
  idempotencyKey: string,
  existingId: string | null,
  keepalive = false,
): Promise<{ note: MeetingNote; created: boolean }> {
  const headers = { "Idempotency-Key": idempotencyKey };
  if (existingId) {
    const note = await request<MeetingNote>(`/api/meeting-notes/${encodeURIComponent(existingId)}`, {
      method: "PATCH",
      body: req,
      headers,
      keepalive,
    });
    return { note, created: true };
  }
  const res = await send<MeetingNote>("/api/meeting-notes", { method: "POST", body: req, headers, keepalive });
  return { note: res.data, created: res.status === 201 };
}

/** `DELETE /api/meeting-notes/{id}`: the author or an admin, else `403 forbidden`. */
export async function deleteMeetingNote(id: string): Promise<void> {
  await request<null>(`/api/meeting-notes/${encodeURIComponent(id)}`, { method: "DELETE" });
}

/** `POST /api/invitations` (admin). Rejects with an `ApiError` whose `code` is an `InviteError`. */
export function inviteMember(githubLogin: string, idempotencyKey: string): Promise<CreatedInvitation> {
  return request<CreatedInvitation>("/api/invitations", {
    method: "POST",
    body: { githubLogin },
    headers: { "Idempotency-Key": idempotencyKey },
  });
}

/** `DELETE /api/invitations/{id}` (admin). */
export async function revokeInvitation(id: string, idempotencyKey: string): Promise<void> {
  await request<null>(`/api/invitations/${encodeURIComponent(id)}`, {
    method: "DELETE",
    headers: { "Idempotency-Key": idempotencyKey },
  });
}

/** `DELETE /api/members/{id}` (admin). Rejects with an `ApiError` whose `code` is a `RemoveError`. */
export async function removeMember(id: string, idempotencyKey: string): Promise<void> {
  await request<null>(`/api/members/${encodeURIComponent(id)}`, {
    method: "DELETE",
    headers: { "Idempotency-Key": idempotencyKey },
  });
}

/** `PATCH /api/members/{id}` (admin). Rejects with an `ApiError` whose `code` is a `ChangeRoleError`. */
export function setMemberRole(id: string, role: Member["role"], idempotencyKey: string): Promise<Member> {
  return request<Member>(`/api/members/${encodeURIComponent(id)}`, {
    method: "PATCH",
    body: { role },
    headers: { "Idempotency-Key": idempotencyKey },
  });
}

/** `POST /api/invitations/preview`. Needs a sign-in, no membership. Rejects with an `ApiError` (`code` a `JoinError`). */
export function previewInvitation(token: string): Promise<InvitationPreview> {
  return apiPost<InvitationPreview>("/api/invitations/preview", { token });
}

/** `POST /api/invitations/accept`. Repeating a successful accept returns the same result. */
export function acceptInvitation(token: string): Promise<JoinedTenant> {
  return apiPost<JoinedTenant>("/api/invitations/accept", { token });
}

/** The address an invitee opens. The token sits in the fragment so it never reaches the static host. */
export function inviteLink(token: string): string {
  return `${window.location.origin}/join#${token}`;
}

/** Issue lists, sprint counts and the overview are what an issue write changes. */
function showsIssues(path: string): boolean {
  return (
    (path.startsWith("/api/issues") && path !== NEW_ISSUE_OPTIONS_PATH) ||
    path.startsWith("/api/overview") ||
    path.startsWith("/api/backlog") ||
    path.startsWith("/api/sprints")
  );
}

export type Loadable<T> = { data: T | null; error: ApiError | Error | null; loading: boolean; reload: () => void };

/**
 * Fetches `path` and refetches when it changes or the person or tenant switches. A switch drops the
 * previous person's data first. `null` skips the request.
 */
export function useApi<T>(path: string | null): Loadable<T> {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<Error | null>(null);
  const [loading, setLoading] = useState(path !== null);
  const [tick, setTick] = useState(0);
  const reload = useCallback(() => setTick((t) => t + 1), []);

  useEffect(() => {
    const onUser = () => {
      setData(null);
      setError(null);
      reload();
    };
    window.addEventListener(DEV_USER_EVENT, onUser);
    window.addEventListener(AUTH_EVENT, onUser);
    return () => {
      window.removeEventListener(DEV_USER_EVENT, onUser);
      window.removeEventListener(AUTH_EVENT, onUser);
    };
  }, [reload]);

  useEffect(() => {
    if (path === null || !showsIssues(path)) return;
    window.addEventListener(ISSUES_CHANGED_EVENT, reload);
    return () => window.removeEventListener(ISSUES_CHANGED_EVENT, reload);
  }, [path, reload]);

  useEffect(() => {
    if (path === null) {
      setLoading(false);
      return;
    }
    let cancelled = false;
    setLoading(true);
    apiGet<T>(path)
      .then((d) => {
        if (!cancelled) {
          setData(d);
          setError(null);
        }
      })
      .catch((e: Error) => {
        if (!cancelled) setError(e);
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [path, tick]);

  return { data, error, loading, reload };
}
