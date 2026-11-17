"use client";

import { useEffect, useId, useRef, useState } from "react";
import {
  ApiError,
  ISSUES_CHANGED_EVENT,
  NEW_ISSUE_OPTIONS_PATH,
  createActionItemIssue,
  newIdempotencyKey,
  useApi,
} from "@/lib/api";
import { showMessage } from "@/lib/boardEdits";
import { useFormat, useT } from "@/lib/i18n";
import type { MeetingActionItem, MeetingNote, NewIssueOptions, Project } from "@/lib/types";
import { readLastRepo, writeLastRepo } from "./NewIssueForm";
import { useIssuePanel } from "./useIssuePanel";
import { Avatar, Card, Flag, StatusBadge } from "./ui";

/** How a failed item may be sent again: under the same key and repository (GitHub may have it), a new key, or not at all. */
type Retry = { key: string; repositoryId: string } | "new" | null;

type Row =
  | { kind: "sending" }
  | { kind: "failed"; lines: string[]; retry: Retry }
  | { kind: "pending"; ref: string };

type ErrorLines = ReturnType<typeof useT>["newIssue"]["errors"];

/**
 * No answer, a 504 or a request still running: GitHub may have the issue, so the item is sent again only under the
 * same key. A 502 was stored under its key and would only be replayed; the server holds the item until its outcome is
 * known, so a new key is safe. 4xx answers are refusals before anything was created.
 */
function failureOf(
  e: unknown,
  errors: ErrorLines,
  earlierRepository: string[],
  sent: { key: string; repositoryId: string },
): { lines: string[]; retry: Retry } {
  if (!(e instanceof ApiError)) return { lines: errors.network, retry: sent };
  if (e.code === "earlier_repository_unavailable") return { lines: earlierRepository, retry: null };
  if (e.code === "request_in_progress") return { lines: errors.request_in_progress, retry: sent };
  if (e.code === "github_unavailable") return e.status === 504 ? { lines: errors.timeout, retry: sent } : { lines: errors.github_unavailable, retry: "new" };
  if (
    e.code === "invalid_request" ||
    e.code === "repository_archived" ||
    e.code === "not_found" ||
    e.code === "github_write_not_granted" ||
    e.code === "assignee_not_collaborator" ||
    e.code === "idempotency_key_reused"
  )
    return { lines: errors[e.code], retry: null };
  return { lines: errors.unknown, retry: e.status >= 500 ? "new" : null };
}

/**
 * The note's action items, each ending in its issue (number and status) or the 이슈 없음 flag with 이슈로 만들기.
 * Items can be picked and made in one go: they are sent one after another, each its own request, so one failure
 * stops nothing else and every item keeps its own retry.
 */
export function ActionItemList({ note, className = "" }: { note: MeetingNote; className?: string }) {
  const t = useT();
  const nt = t.notes;
  const ti = nt.toIssue;
  const fmt = useFormat();
  const ids = useId();
  const { open } = useIssuePanel();
  const options = useApi<NewIssueOptions>(NEW_ISSUE_OPTIONS_PATH);
  const projects = useApi<Project[]>(note.project ? "/api/projects" : null);

  const [items, setItems] = useState<MeetingActionItem[]>(note.actionItems);
  const [rows, setRows] = useState<Record<string, Row>>({});
  const [picked, setPicked] = useState<string[]>([]);
  const [repoId, setRepoId] = useState("");
  const [progress, setProgress] = useState<{ done: number; total: number } | null>(null);
  const running = useRef(false);
  const mounted = useRef(true);

  useEffect(() => setItems(note.actionItems), [note]);
  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const repos = (options.data?.repositories ?? []).filter((r) => !r.archived);
  const projectRepoIds = projects.data?.find((p) => p.id === note.project?.id)?.repositories.map((r) => r.id) ?? [];
  // Until one is picked: the note's project's first repository, then the last one used, then the first one.
  const repo =
    repos.find((r) => r.id === repoId) ??
    repos.find((r) => projectRepoIds.includes(r.id)) ??
    repos.find((r) => r.id === readLastRepo()) ??
    repos[0];

  const missingItems = items.filter((a) => !a.issue && !a.pendingIssue && !a.creatingIssue);
  const canMake = (a: MeetingActionItem) => !a.issue && !a.pendingIssue && !a.creatingIssue && rows[a.id]?.kind !== "sending";
  const pickable = items.filter(canMake);
  const pickedNow = picked.filter((id) => pickable.some((a) => a.id === id));
  const setRow = (id: string, row: Row | null) =>
    setRows((r) => {
      const next = { ...r };
      if (row) next[id] = row;
      else delete next[id];
      return next;
    });

  /** Sends one item and records its outcome on its row. Resolves true when the item has its issue. */
  const make = async (item: MeetingActionItem, sent: { key: string; repositoryId: string }): Promise<boolean> => {
    setRow(item.id, { kind: "sending" });
    try {
      const out = await createActionItemIssue(item.id, sent.repositoryId, sent.key);
      writeLastRepo(sent.repositoryId);
      window.dispatchEvent(new Event(ISSUES_CHANGED_EVENT));
      if (out.kind === "pending") {
        setRow(item.id, { kind: "pending", ref: `${out.issue.repository} #${out.issue.number}`.trim() });
        return true;
      }
      const linked = out.result.actionItem;
      setItems((list) => list.map((a) => (a.id === linked.id ? linked : a)));
      setRow(item.id, null);
      const dropped = out.result.droppedAssignees;
      const ref = linked.issue ? `${linked.issue.repositoryName} #${linked.issue.number}` : "";
      if (!out.result.created) showMessage({ title: ti.already(ref) });
      else if (dropped.length > 0) {
        const people = options.data?.assignees ?? [];
        const names = dropped.map((id) => {
          const p = people.find((x) => x.githubUserId === id);
          return p ? fmt.person(p) : String(id);
        });
        showMessage({ title: ti.made(ref), detail: ti.dropped(names.join(", ")) });
      }
      return true;
    } catch (e) {
      setRow(item.id, { kind: "failed", ...failureOf(e, t.newIssue.errors, ti.earlierRepository, sent) });
      return false;
    }
  };

  const retryOf = (item: MeetingActionItem): { key: string; repositoryId: string } | null => {
    const row = rows[item.id];
    if (row?.kind === "failed" && row.retry && row.retry !== "new") return row.retry;
    return repo ? { key: newIdempotencyKey(), repositoryId: repo.id } : null;
  };

  const makeOne = async (item: MeetingActionItem) => {
    if (running.current) return;
    const sent = retryOf(item);
    if (!sent) return;
    running.current = true;
    const ok = await make(item, sent);
    running.current = false;
    if (ok) setPicked((p) => p.filter((id) => id !== item.id));
  };

  const makePicked = async () => {
    if (running.current || !repo) return;
    const queue = items.filter((a) => pickedNow.includes(a.id));
    if (queue.length === 0) return;
    running.current = true;
    let ok = 0;
    for (let i = 0; i < queue.length; i++) {
      setProgress({ done: i + 1, total: queue.length });
      const sent = retryOf(queue[i]);
      if (sent && (await make(queue[i], sent))) {
        ok++;
        setPicked((p) => p.filter((id) => id !== queue[i].id));
      }
    }
    running.current = false;
    if (!mounted.current) return;
    setProgress(null);
    showMessage(
      ok === queue.length
        ? { title: ti.madeAll(ok) }
        : { title: ti.madeSome(ok, queue.length), detail: ti.madeSomeDetail, failed: true },
    );
  };

  const toggle = (id: string) => setPicked((p) => (p.includes(id) ? p.filter((x) => x !== id) : [...p, id]));
  const busy = progress !== null || Object.values(rows).some((r) => r.kind === "sending");
  const missing = missingItems.length;

  return (
    <Card
      title={nt.actionItems}
      count={items.length}
      hint={nt.actionHint}
      action={missing > 0 ? <span>{nt.missingCount(missing)}</span> : null}
      className={className}
    >
      {items.length === 0 ? (
        <p className="px-5 py-4 text-small text-muted">{nt.noActionItems}</p>
      ) : (
        <>
          {pickable.length > 0 && (
            <div
              role="group"
              aria-label={ti.make}
              className="mx-5 mb-2 flex flex-wrap items-center gap-x-3 gap-y-2 rounded-control bg-sunken px-3 py-2 text-small"
            >
              <label htmlFor={`${ids}-repo`} className="text-ink-soft">
                {ti.repository}
              </label>
              {options.error ? (
                <span className="border-l-2 border-ink-soft pl-3 text-ink">
                  {ti.optionsFailed}{" "}
                  <button type="button" className="link cursor-pointer" onClick={options.reload}>
                    {ti.retry}
                  </button>
                </span>
              ) : options.data && repos.length === 0 ? (
                <span className="text-muted">{ti.noRepos}</span>
              ) : (
                <select
                  id={`${ids}-repo`}
                  value={repo?.id ?? ""}
                  disabled={!options.data || busy}
                  onChange={(e) => setRepoId(e.target.value)}
                  className="h-8 min-w-0 max-w-full cursor-pointer rounded-control bg-surface px-2 font-mono text-small text-ink shadow-[inset_0_0_0_1px_var(--wb-rule-strong)] focus:shadow-[inset_0_0_0_2px_var(--wb-focus)] focus:outline-none disabled:cursor-default disabled:text-muted"
                >
                  {!options.data && <option value="">{t.common.loading}…</option>}
                  {repos.map((r) => (
                    <option key={r.id} value={r.id}>
                      {r.fullName}
                    </option>
                  ))}
                </select>
              )}
              <span className="ml-auto flex flex-wrap items-center gap-2">
                {progress ? (
                  <span role="status" className="inline-flex items-center gap-2 text-ink-soft">
                    <span aria-hidden className="busy-dot" />
                    {ti.progress(progress.done, progress.total)}…
                  </span>
                ) : pickedNow.length > 0 ? (
                  <>
                    <span className="text-ink-soft">{ti.selected(pickedNow.length)}</span>
                    <button type="button" className="btn h-8" onClick={() => setPicked([])}>
                      {ti.clear}
                    </button>
                    <button type="button" className="btn btn-primary h-8" disabled={!repo || busy} onClick={() => void makePicked()}>
                      {ti.makeSelected(pickedNow.length)}
                    </button>
                  </>
                ) : pickable.length > 1 ? (
                  <button type="button" className="btn h-8" disabled={!repo} onClick={() => setPicked(pickable.map((a) => a.id))}>
                    {ti.selectAll}
                  </button>
                ) : null}
              </span>
            </div>
          )}
          <ul>
            {items.map((a) => {
              const row = rows[a.id];
              const on = pickedNow.includes(a.id);
              return (
                <li
                  key={a.id}
                  className={`relative flex gap-2 py-3 pr-5 after:absolute after:inset-x-5 after:top-0 after:h-px after:bg-rule first:after:hidden ${pickable.length > 0 ? "pl-3" : "pl-5"}`}
                >
                  {canMake(a) ? (
                    <button
                      type="button"
                      role="checkbox"
                      aria-checked={on}
                      aria-label={ti.select(a.text)}
                      disabled={progress !== null}
                      onClick={() => toggle(a.id)}
                      className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-control hover:bg-hover disabled:cursor-default"
                    >
                      <span aria-hidden data-on={on || undefined} className="pick-box">
                        {on && (
                          <svg width="10" height="10" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2.5">
                            <path d="m3.5 8.5 3 3 6-7" strokeLinecap="round" strokeLinejoin="round" />
                          </svg>
                        )}
                      </span>
                    </button>
                  ) : pickable.length > 0 ? (
                    <span aria-hidden className="w-7 shrink-0" />
                  ) : null}
                  <div className="flex min-w-0 flex-1 flex-col gap-1 pt-0.5">
                    <span className="break-keep text-ui font-medium">{a.text}</span>
                    <span className="flex flex-wrap items-center gap-x-3 gap-y-1.5 text-small text-ink-soft">
                      {a.assignee ? (
                        <span className="inline-flex items-center gap-1.5">
                          <Avatar person={a.assignee} size="sm" />
                          {fmt.person(a.assignee)}
                        </span>
                      ) : (
                        <span className="text-accent">{t.common.unassigned}</span>
                      )}
                      {a.dueDate && <span className="tabular-nums">{nt.due(fmt.date(`${a.dueDate}T00:00:00`))}</span>}
                      <IssueSlot
                        item={a}
                        row={row}
                        onOpen={open}
                        onMake={() => void makeOne(a)}
                        disabled={!repo || progress !== null}
                      />
                    </span>
                    {row?.kind === "failed" && (
                      <p role="alert" className="mt-1 flex flex-col gap-0.5 border-l-2 border-ink-soft pl-3 text-small text-ink">
                        <span>{row.lines[0]}</span>
                        <span className="text-ink-soft">
                          {row.lines[1]}{" "}
                          {row.retry && row.retry !== "new" && (
                            <button
                              type="button"
                              className="link cursor-pointer"
                              disabled={progress !== null}
                              onClick={() => void makeOne(a)}
                            >
                              {ti.retrySame}
                            </button>
                          )}
                        </span>
                      </p>
                    )}
                  </div>
                </li>
              );
            })}
          </ul>
        </>
      )}
    </Card>
  );
}

function IssueSlot({
  item,
  row,
  onOpen,
  onMake,
  disabled,
}: {
  item: MeetingActionItem;
  row: Row | undefined;
  onOpen: (id: string) => void;
  onMake: () => void;
  disabled: boolean;
}) {
  const t = useT();
  const ti = t.notes.toIssue;
  if (item.issue) {
    const issue = item.issue;
    return (
      <button
        type="button"
        onClick={() => onOpen(issue.id)}
        className="inline-flex cursor-pointer items-center gap-2 [&>.font-mono]:hover:underline [&>.font-mono]:hover:underline-offset-3"
      >
        <span className="font-mono">
          {issue.repositoryName} #{issue.number}
        </span>
        <StatusBadge status={issue.status} />
      </button>
    );
  }
  const pendingRef = row?.kind === "pending" ? row.ref : item.pendingIssue ? `${item.pendingIssue.repositoryName} #${item.pendingIssue.number}` : null;
  if (pendingRef)
    return (
      <span className="inline-flex flex-wrap items-center gap-2">
        <span className="font-mono">{pendingRef}</span>
        <span className="text-muted">{ti.pending}</span>
      </span>
    );
  if (row?.kind === "sending" || item.creatingIssue)
    return (
      <span role="status" className="inline-flex items-center gap-2">
        <span aria-hidden className="busy-dot" />
        {row?.kind === "sending" ? `${ti.making}…` : ti.elsewhere}
      </span>
    );
  return (
    <>
      <Flag>{t.common.noIssue}</Flag>
      {!(row?.kind === "failed" && typeof row.retry === "object" && row.retry) && (
        <button type="button" className="btn h-7 px-2.5 text-small" aria-label={ti.makeOne(item.text)} disabled={disabled} onClick={onMake}>
          {ti.make}
        </button>
      )}
    </>
  );
}
