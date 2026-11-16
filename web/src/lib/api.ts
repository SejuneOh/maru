"use client";

import { useCallback, useEffect, useState } from "react";
import { AUTH_EVENT, GATE_EVENT, authEnabled, getAccessToken, getTenant, signOutIfStill } from "./auth";
import type {
  AddCommentRequest,
  AssignedIssue,
  BoardChangeRequest,
  BoardIssue,
  CreateIssueRequest,
  CreatedIssue,
  Milestone,
  PendingIssue,
  PostedComment,
  SetAssigneesRequest,
  Sprint,
  SprintForm,
  SprintIssuesResult,
  SyncStatus,
} from "./types";

const API_URL = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5080";
const DEV_USER_KEY = "workboard.devUser";
const DEV_TENANT_KEY = "workboard.devTenant";
export const DEV_USER_EVENT = "workboard:dev-user";
/** Fired after a write that changes issues, so every board refetches its issue lists. */
export const ISSUES_CHANGED_EVENT = "workboard:issues-changed";

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

type ReqInit = { method?: string; body?: unknown; headers?: Record<string, string> };

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
  return { status: res.status, data: (await res.json()) as T };
}

export function apiGet<T>(path: string): Promise<T> {
  return request<T>(path);
}

export function apiPost<T>(path: string, body: unknown): Promise<T> {
  return request<T>(path, { method: "POST", body });
}

export const MILESTONES_PATH = "/api/milestones";
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
