"use client";

import { useSyncExternalStore } from "react";
import { ApiError, ISSUES_CHANGED_EVENT, changeBoard, newIdempotencyKey } from "./api";
import type { BoardChangeRequest, BoardIssue, Priority, Size, Status } from "./types";

/**
 * Card edits from the board, the panel and the `s` shortcut all go through here, so every screen shows the
 * same card in the same state. Moves between open statuses show at once and roll back on failure; a change
 * that has to close or reopen on GitHub waits for GitHub before the card moves.
 */

export type BoardChange = { status?: Status; priority?: Priority | null; size?: Size | null };

/** `saving`: shown already, waiting for the server. `closing` / `reopening`: waiting for GitHub, not shown yet. */
export type EditPhase = "saving" | "closing" | "reopening";

type Pending = { change: BoardChange; phase: EditPhase };

export type EditFailure =
  | "conflict"
  | "field_disabled"
  | "repository_archived"
  | "github_write_not_granted"
  | "not_found"
  | "github_unavailable"
  | "timeout"
  | "request_in_progress"
  | "network"
  | "unknown";

/** `busy`: a change for this card is still on its way, so nothing was sent. */
export type EditResult = "sent" | "same" | "busy";

export type Notice =
  | { id: number; kind: "moved"; ref: string; to: Status; undo: (() => EditResult) | null; until: number }
  | { id: number; kind: "undone"; ref: string; to: Status }
  | { id: number; kind: "failed"; ref: string; failure: EditFailure; retry: (() => void) | null };

export const UNDO_MS = 10_000;

const pending = new Map<string, Pending>();
/** Server answers to our own writes, used until a refetch brings something at least as new. */
const confirmed = new Map<string, BoardIssue>();
/** The newest copy of each card any screen has shown; the `s` shortcut edits from it. */
const known = new Map<string, BoardIssue>();
let notice: Notice | null = null;
let noticeSeq = 0;
let version = 0;
const listeners = new Set<() => void>();

function bump() {
  version++;
  listeners.forEach((l) => l());
}

function subscribe(l: () => void) {
  listeners.add(l);
  return () => {
    listeners.delete(l);
  };
}

const getVersion = () => version;

function freshness(i: BoardIssue): number {
  return Math.max(Date.parse(i.updatedAt), i.boardUpdatedAt ? Date.parse(i.boardUpdatedAt) : 0);
}

export const isClosedStatus = (s: Status) => s === "done" || s === "cancelled";

function categoryOf(s: Status): BoardIssue["category"] {
  return s === "todo" ? "todo" : s === "in_progress" || s === "in_review" ? "in_progress" : "done";
}

/**
 * Our write answer wins only over an older server copy (a refetch that left before the write). A copy at
 * least as new already holds the write, plus anything that does not move the timestamps (sprint, linked
 * PRs, the PR-driven status), so it replaces the answer for good.
 */
function merge<T extends BoardIssue>(issue: T): T {
  const mine = confirmed.get(issue.id);
  let out: T = issue;
  if (mine && freshness(mine) > freshness(issue)) out = { ...issue, ...mine };
  else if (mine) confirmed.delete(issue.id);
  const p = pending.get(issue.id);
  if (p?.phase === "saving") {
    out = { ...out, ...p.change };
    if (p.change.status) out = { ...out, category: categoryOf(p.change.status) };
  }
  return out;
}

function remember(issues: BoardIssue[]) {
  for (const i of issues) known.set(i.id, i);
}

/** The list as it should be shown: our confirmed writes and the ones still on their way laid over it. */
export function useEditedIssues(issues: BoardIssue[] | null): BoardIssue[] | null {
  useSyncExternalStore(subscribe, getVersion, getVersion);
  if (!issues) return null;
  const out = issues.map(merge);
  remember(out);
  return out;
}

export function useEditedIssue<T extends BoardIssue>(issue: T | null): T | null {
  useSyncExternalStore(subscribe, getVersion, getVersion);
  if (!issue) return null;
  const out = merge(issue);
  known.set(out.id, out);
  return out;
}

export function useEditPhase(id: string): EditPhase | null {
  useSyncExternalStore(subscribe, getVersion, getVersion);
  return pending.get(id)?.phase ?? null;
}

export function useNotice(): Notice | null {
  useSyncExternalStore(subscribe, getVersion, getVersion);
  return notice;
}

export function dismissNotice(id: number) {
  if (notice?.id !== id) return;
  notice = null;
  bump();
}

export function knownIssue(id: string): BoardIssue | null {
  const i = known.get(id);
  return i ? merge(i) : null;
}

type DistributiveOmit<T, K extends PropertyKey> = T extends unknown ? Omit<T, K> : never;

function show(n: DistributiveOmit<Notice, "id">) {
  notice = { ...n, id: ++noticeSeq };
  bump();
}

/** 5xx, a timeout or no answer: GitHub may have acted, so a retry must reuse the key. */
function failureOf(e: unknown): { failure: EditFailure; uncertain: boolean } {
  if (!(e instanceof ApiError)) return { failure: "network", uncertain: true };
  switch (e.code) {
    case "conflict":
    case "field_disabled":
    case "repository_archived":
    case "github_write_not_granted":
    case "not_found":
      return { failure: e.code, uncertain: false };
    case "github_unavailable":
      return { failure: e.status === 504 ? "timeout" : "github_unavailable", uncertain: true };
    case "request_in_progress":
      return { failure: "request_in_progress", uncertain: true };
  }
  return { failure: "unknown", uncertain: e.status >= 500 };
}

function sameValue(issue: BoardIssue, change: BoardChange): boolean {
  return (
    (change.status === undefined || change.status === issue.status) &&
    (change.priority === undefined || change.priority === issue.priority) &&
    (change.size === undefined || change.size === issue.size)
  );
}

export function isEditing(id: string): boolean {
  return pending.has(id);
}

type Options = { undoOf?: Status };

/** Sends one change for `issue` (the copy on screen, so `boardUpdatedAt` is what the person saw). */
export function editIssue(issue: BoardIssue, change: BoardChange, opts: Options = {}): EditResult {
  if (pending.has(issue.id)) return "busy";
  if (sameValue(issue, change)) return "same";
  const status = change.status;
  const phase: EditPhase =
    status && isClosedStatus(status) ? "closing" : status && issue.state === "closed" ? "reopening" : "saving";
  const req: BoardChangeRequest = { ...change, expectedUpdatedAt: issue.boardUpdatedAt };
  send(issue, change, req, newIdempotencyKey(), phase, opts);
  return "sent";
}

function send(issue: BoardIssue, change: BoardChange, req: BoardChangeRequest, key: string, phase: EditPhase, opts: Options) {
  const ref = `${issue.repository.name} #${issue.number}`;
  pending.set(issue.id, { change, phase });
  if (notice?.kind === "failed" && notice.ref === ref) notice = null;
  bump();
  changeBoard(issue.id, req, key).then(
    (saved) => {
      pending.delete(issue.id);
      confirmed.set(issue.id, saved);
      known.set(issue.id, saved);
      if (opts.undoOf) show({ kind: "undone", ref, to: saved.status });
      else if (phase === "closing")
        show({
          kind: "moved",
          ref,
          to: saved.status,
          until: Date.now() + UNDO_MS,
          undo: () => editIssue(saved, { status: issue.status }, { undoOf: saved.status }),
        });
      else bump();
      window.dispatchEvent(new Event(ISSUES_CHANGED_EVENT));
    },
    (e: unknown) => {
      pending.delete(issue.id);
      const { failure, uncertain } = failureOf(e);
      show({
        kind: "failed",
        ref,
        failure,
        retry:
          failure === "conflict" ||
          failure === "field_disabled" ||
          failure === "repository_archived" ||
          failure === "not_found" ||
          failure === "github_write_not_granted"
            ? null
            : () => {
                if (!pending.has(issue.id)) send(issue, change, req, uncertain ? key : newIdempotencyKey(), phase, opts);
              },
      });
      // The card may have changed under us (conflict) or on GitHub (no answer): show what the server has now.
      if (failure === "conflict" || uncertain) window.dispatchEvent(new Event(ISSUES_CHANGED_EVENT));
    },
  );
}
