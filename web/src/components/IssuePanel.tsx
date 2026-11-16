"use client";

import Link from "next/link";
import { useCallback, useEffect, useRef, useState } from "react";
import { ApiError, ISSUES_CHANGED_EVENT, newIdempotencyKey, setAssignees, useApi } from "@/lib/api";
import { editIssue, useEditPhase, useEditedIssue } from "@/lib/boardEdits";
import { type Dict, useFormat, useT } from "@/lib/i18n";
import { daysSince, isStale, safeHttpUrl } from "@/lib/labels";
import { useSettings } from "@/lib/me";
import { isTyping } from "./useShortcuts";
import type { CreatedIssue, IssueDetail, Member, PostedComment, Priority, Size } from "@/lib/types";
import { AssigneeMenu } from "./AssigneeMenu";
import { CommentBox } from "./CommentBox";
import { STATUS_KEYS } from "./BoardNotice";
import { ErrorBox, Loading } from "./Feedback";
import { PropertyMenu } from "./PropertyMenu";
import { Avatar, Count, Dot, ExternalIcon, Flag, GitHubLink, Kbd, LabelChips, MilestoneIcon, StatusBadge } from "./ui";
import { STATUS_DOT } from "@/lib/labels";
import { NewIssueForm } from "./NewIssueForm";
import { NEW_ISSUE, useIssuePanel } from "./useIssuePanel";

/** Everything about one issue in one place: PRs, sub-issues, comments and the meetings it came from. */
export function IssuePanel() {
  const { issueId, open, close, prefill } = useIssuePanel();
  const isNew = issueId === NEW_ISSUE;
  const { data, error, loading } = useApi<IssueDetail>(issueId && !isNew ? `/api/issues/${issueId}` : null);
  const members = useApi<Member[]>(issueId && !isNew ? "/api/members" : null);
  /** The answer of our own assignee change, shown until a refetch at least as new arrives. */
  const [fresh, setFresh] = useState<IssueDetail | null>(null);
  const [assign, setAssign] = useState<AssignState | null>(null);
  const [posted, setPosted] = useState<{ issueId: string; comments: PostedComment[] }>({ issueId: "", comments: [] });
  const [assignSignal, setAssignSignal] = useState(0);
  const [created, setCreated] = useState<{ issue: CreatedIssue; dropped: string[] } | null>(null);
  const onCreated = useCallback(
    (issue: CreatedIssue, dropped: string[]) => {
      setCreated({ issue, dropped });
      open(issue.id);
    },
    [open],
  );
  const settings = useSettings();
  const t = useT();
  const p = t.panel;
  const fmt = useFormat();
  const closeRef = useRef<HTMLButtonElement>(null);

  const fetched = data && data.id === issueId ? data : created && created.issue.id === issueId ? created.issue : null;
  const loaded =
    fresh && fresh.id === issueId && (!fetched || Date.parse(fresh.updatedAt) > Date.parse(fetched.updatedAt)) ? fresh : fetched;
  const shown = useEditedIssue<IssueDetail>(loaded);
  const phase = useEditPhase(issueId ?? "");
  const isOpen = issueId !== null;
  useEffect(() => {
    if (!isOpen) return;
    const returnTo = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    // The new-issue form puts focus on its title field itself.
    if (!isNew) closeRef.current?.focus();
    return () => returnTo?.focus();
  }, [isOpen, isNew]);

  useEffect(() => {
    if (!isOpen) setCreated(null);
  }, [isOpen]);

  useEffect(() => {
    setAssign((a) => (a?.issueId === issueId ? a : null));
    setAssignSignal(0);
  }, [issueId]);

  const onPosted = useCallback(
    (c: PostedComment) =>
      setPosted((p) => ({ issueId: issueId ?? "", comments: [...(p.issueId === issueId ? p.comments : []), c] })),
    [issueId],
  );

  useEffect(() => {
    if (!isOpen || isNew) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.code !== "KeyA" || e.defaultPrevented || e.metaKey || e.ctrlKey || e.altKey || e.shiftKey || e.isComposing || isTyping(e.target)) return;
      e.preventDefault();
      setAssignSignal((n) => n + 1);
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [isOpen, isNew]);

  useEffect(() => {
    if (!isOpen) return;
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && !e.isComposing && close();
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [isOpen, close]);

  if (!issueId) return null;
  const urgent = shown && settings.priorityEnabled && shown.priority === "urgent" && shown.category !== "done";
  const justCreated = created && shown && created.issue.id === shown.id ? created : null;
  const assignHere = assign && assign.issueId === issueId ? assign : null;
  const people = members.data ?? [];
  // Our posted comments show at once; a refetch that already has one replaces it. One saved without an id
  // (mirrorPending) is matched by author, GitHub's creation time and text once the sync brings it.
  const conversation: ConversationItem[] = shown
    ? [
        ...shown.comments.map((c) => ({ ...c, mirrorPending: false })),
        ...(posted.issueId === shown.id ? posted.comments : []).filter(
          (c) =>
            !shown.comments.some((x) =>
              c.id
                ? x.id === c.id
                : x.author.githubLogin === c.author.githubLogin && Date.parse(x.createdAt) === Date.parse(c.createdAt) && x.body === c.body,
            ),
        ),
      ]
    : [];

  const saveAssignees = async (issue: IssueDetail, ids: number[]) => {
    setAssign({ issueId: issue.id, kind: "saving" });
    try {
      const saved = await setAssignees(issue.id, { assigneeGithubUserIds: ids }, newIdempotencyKey());
      setFresh(saved);
      const dropped = saved.droppedAssignees.map((id) => {
        const m = people.find((x) => x.githubUserId === id);
        return m ? fmt.person(m) : String(id);
      });
      setAssign(dropped.length ? { issueId: issue.id, kind: "dropped", names: dropped } : null);
      window.dispatchEvent(new Event(ISSUES_CHANGED_EVENT));
    } catch (e) {
      const code = e instanceof ApiError ? e.code : "network";
      const known: AssignFailure = code && code in p.assign.errors ? (code as AssignFailure) : "unknown";
      const reason: AssignFailure = known === "github_unavailable" && e instanceof ApiError && e.status === 504 ? "timeout" : known;
      if (reason === "invalid_request") members.reload();
      const retryable = reason === "timeout" || reason === "github_unavailable" || reason === "network" || reason === "request_in_progress" || reason === "unknown";
      setAssign({ issueId: issue.id, kind: "failed", reason, retry: retryable ? () => void saveAssignees(issue, ids) : null });
      // GitHub may have applied it; the reload shows what it has.
      if (retryable) window.dispatchEvent(new Event(ISSUES_CHANGED_EVENT));
    }
  };

  return (
    <>
      <div className="fixed inset-0 z-30 bg-scrim" onClick={close} aria-hidden />
      <aside
        className="fixed inset-y-0 right-0 z-40 flex w-full max-w-[560px] flex-col overflow-y-auto bg-surface shadow-panel min-[561px]:rounded-l-sheet"
        aria-label={p.aria}
        role="dialog"
      >
        <div className="sticky top-0 z-10 flex h-14 shrink-0 items-center justify-between gap-2 border-b border-rule bg-surface px-6">
          {isNew ? (
            <span className="min-w-0 truncate text-meta text-muted">{t.newIssue.head}</span>
          ) : (
            <span className="min-w-0 truncate font-mono text-meta text-muted">{shown ? `${shown.repository.name} #${shown.number}` : p.issue}</span>
          )}
          {shown?.htmlUrl && (
            <GitHubLink
              href={shown.htmlUrl}
              label={t.github.openRef(`${shown.repository.name} #${shown.number}`)}
              className="btn ml-auto h-8 gap-1.5 px-2.5 text-meta font-medium text-ink-soft hover:text-ink"
            >
              {t.github.open}
            </GitHubLink>
          )}
          <button
            ref={closeRef}
            type="button"
            onClick={close}
            aria-keyshortcuts="Escape"
            className="btn h-8 gap-1.5 px-2.5 text-meta font-medium text-ink-soft hover:text-ink"
          >
            {p.close} <Kbd>Esc</Kbd>
          </button>
        </div>

        {isNew ? (
          <NewIssueForm key={`${prefill.status ?? ""}:${prefill.assignee ?? ""}`} prefill={prefill} onCreated={onCreated} />
        ) : (
        <div className="flex flex-col gap-7 px-6 py-6 max-[639px]:px-4">
          {justCreated && <CreatedNote issue={justCreated.issue} dropped={justCreated.dropped} />}
          {loading && !shown && <Loading />}
          {error && <ErrorBox error={error} />}
          {shown && (
            <>
              <header>
                {shown.parent && (
                  <button type="button" onClick={() => open(shown.parent!.id)} className="link mb-2 cursor-pointer text-left text-small text-ink-soft">
                    {p.parent(shown.parent.number, shown.parent.title)}
                  </button>
                )}
                <h2 className="text-title font-semibold">{shown.title}</h2>
                <dl className="mt-5 grid grid-cols-[7rem_minmax(0,1fr)] rounded-item text-ui shadow-elev-1 [&>dd]:flex [&>dd]:min-h-10 [&>dd]:items-center [&>dd]:border-t [&>dd]:border-rule [&>dd]:px-3 [&>dd]:py-1.5 [&>dt]:flex [&>dt]:items-center [&>dt]:border-t [&>dt]:border-rule [&>dt]:px-3 [&>dt]:py-1.5 [&>dt]:text-small [&>dt:first-of-type]:border-t-0 [&>dd:first-of-type]:border-t-0">
                  <dt className="text-muted">{p.status}</dt>
                  <dd className="flex flex-wrap items-center gap-2">
                    <PropertyMenu
                      label={p.status}
                      valueText={t.status[shown.status]}
                      value={shown.status}
                      disabled={phase !== null}
                      options={STATUS_KEYS.map((s, i) => ({
                        value: s,
                        node: (
                          <>
                            <Dot className={STATUS_DOT[s]} />
                            {t.status[s]}
                          </>
                        ),
                        hint: String(i + 1),
                      }))}
                      onPick={(s) => editIssue(shown, { status: s })}
                    >
                      <StatusBadge status={shown.status} />
                    </PropertyMenu>
                    {phase && phase !== "saving" ? (
                      <span role="status" className="inline-flex items-center gap-1.5 text-small text-ink-soft">
                        <span aria-hidden className="busy-dot" />
                        {t.edit[phase]}…
                      </span>
                    ) : (
                      shown.status === "cancelled" && <span className="text-small text-muted">{t.cancelledHint}</span>
                    )}
                    {isStale(shown) && <Flag>{t.common.stalledFor(daysSince(shown.updatedAt))}</Flag>}
                  </dd>
                  <dt className="text-muted">{p.assignees}</dt>
                  <dd className="flex flex-wrap items-center gap-2">
                    <AssigneeMenu
                      assignees={shown.assignees}
                      members={members.data}
                      membersFailed={members.error !== null}
                      disabled={assignHere?.kind === "saving"}
                      openSignal={assignSignal}
                      onSave={(ids) => void saveAssignees(shown, ids)}
                      onRetryMembers={members.reload}
                    >
                      {shown.assignees.length ? (
                        shown.assignees.map((a) => (
                          <span key={a.githubUserId} className="inline-flex min-w-0 items-center gap-1.5">
                            <Avatar person={a} size="sm" />
                            <span className="truncate">{fmt.person(a)}</span>
                          </span>
                        ))
                      ) : shown.state === "open" ? (
                        <span className="text-accent">{t.common.unassigned}</span>
                      ) : (
                        <span className="text-muted">{t.common.none}</span>
                      )}
                    </AssigneeMenu>
                    {assignHere?.kind === "saving" && (
                      <span role="status" className="inline-flex items-center gap-1.5 text-small text-ink-soft">
                        <span aria-hidden className="busy-dot" />
                        {p.assign.saving}…
                      </span>
                    )}
                  </dd>
                  {shown.issueType && (
                    <>
                      <dt className="text-muted">{p.type}</dt>
                      <dd>{fmt.issueType(shown.issueType)}</dd>
                    </>
                  )}
                  {shown.milestone && (
                    <>
                      <dt className="text-muted">{p.milestone}</dt>
                      <dd className="gap-1.5">
                        <span className="text-muted">
                          <MilestoneIcon />
                        </span>
                        {shown.milestone.title}
                        {shown.milestone.dueOn && <span className="text-small text-muted">· {t.overview.projects.due(fmt.date(shown.milestone.dueOn))}</span>}
                      </dd>
                    </>
                  )}
                  {shown.labels.length > 0 && (
                    <>
                      <dt className="text-muted">{p.labels}</dt>
                      <dd>
                        <LabelChips labels={shown.labels} max={20} />
                      </dd>
                    </>
                  )}
                  {settings.sprintsEnabled && shown.sprint && (
                    <>
                      <dt className="text-muted">{p.sprint}</dt>
                      <dd>{shown.sprint.name}</dd>
                    </>
                  )}
                  {settings.priorityEnabled && (
                    <>
                      <dt className="text-muted">{p.priority}</dt>
                      <dd>
                        <PropertyMenu<Priority | null>
                          label={p.priority}
                          valueText={shown.priority ? t.priority[shown.priority] : t.edit.unset}
                          value={shown.priority}
                          disabled={phase !== null}
                          options={[null, ...PRIORITIES].map((v) => ({ value: v, node: v ? t.priority[v] : t.edit.unset }))}
                          onPick={(v) => editIssue(shown, { priority: v })}
                        >
                          {urgent ? (
                            <Flag fill>{t.priority.urgent}</Flag>
                          ) : shown.priority ? (
                            t.priority[shown.priority]
                          ) : (
                            <span className="text-muted">{t.edit.unset}</span>
                          )}
                        </PropertyMenu>
                      </dd>
                    </>
                  )}
                  {settings.sizeEnabled && (
                    <>
                      <dt className="text-muted">{p.size}</dt>
                      <dd>
                        <PropertyMenu<Size | null>
                          label={p.size}
                          valueText={shown.size ? t.size[shown.size] : t.edit.unset}
                          value={shown.size}
                          disabled={phase !== null}
                          options={[null, ...SIZES].map((v) => ({ value: v, node: v ? t.size[v] : t.edit.unset }))}
                          onPick={(v) => editIssue(shown, { size: v })}
                        >
                          {shown.size ? t.size[shown.size] : <span className="text-muted">{t.edit.unset}</span>}
                        </PropertyMenu>
                      </dd>
                    </>
                  )}
                  <dt className="text-muted">{p.lastActivity}</dt>
                  <dd className="text-small">{fmt.relativeDays(shown.updatedAt)}</dd>
                </dl>
                {assignHere?.kind === "failed" && (
                  <div role="alert" className="mt-3 flex flex-col gap-1 rounded-control border-l-2 border-ink-soft bg-surface px-4 py-3 shadow-elev-1">
                    <p className="text-ui text-ink">{p.assign.errors[assignHere.reason][0]}</p>
                    <p className="text-small text-ink-soft">{p.assign.errors[assignHere.reason][1]}</p>
                    <div className="mt-2 flex flex-wrap gap-2">
                      {assignHere.retry && (
                        <button type="button" className="btn h-8 px-3 text-small" onClick={assignHere.retry}>
                          {p.assign.retry}
                        </button>
                      )}
                      <button type="button" className="btn h-8 px-3 text-small" onClick={() => setAssign(null)}>
                        {p.assign.dismiss}
                      </button>
                    </div>
                  </div>
                )}
                {assignHere?.kind === "dropped" && (
                  <p role="status" className="mt-3 border-l-2 border-ink-soft pl-3 text-small text-ink">
                    {p.assign.dropped(assignHere.names.join(", "))}
                  </p>
                )}
                {shown.lastBoardChange && (
                  <p className="mt-2 px-1 text-small text-muted">
                    {t.edit.byBoard(fmt.person(shown.lastBoardChange.by), fmt.relativeTime(shown.lastBoardChange.at))}
                  </p>
                )}
              </header>

              {shown.body && <p className="whitespace-pre-wrap text-body text-ink-soft">{shown.body}</p>}

              <Section title={p.pullRequests} count={shown.pullRequests.length}>
                {shown.pullRequests.map((pr) => ({ pr, url: safeHttpUrl(pr.htmlUrl) })).map(({ pr, url }) => (
                  <li key={pr.id} className="flex justify-between gap-3 py-2 text-ui">
                    {url ? (
                      <a
                        href={url}
                        target="_blank"
                        rel="noopener noreferrer"
                        aria-label={`${p.openPr(pr.number)}: ${pr.title}`}
                        title={t.github.newTab}
                        className="group inline-flex min-w-0 items-center gap-1.5 hover:text-ink"
                      >
                        <span className="truncate">
                          <span className="font-mono text-meta text-muted">#{pr.number}</span>{" "}
                          <span className="underline decoration-rule-strong underline-offset-[3px] group-hover:decoration-ink-soft">{pr.title}</span>
                        </span>
                        <span className="text-muted">
                          <ExternalIcon size={12} />
                        </span>
                      </a>
                    ) : (
                      <span className="truncate">
                        <span className="font-mono text-meta text-muted">#{pr.number}</span> {pr.title}
                      </span>
                    )}
                    <span className="shrink-0 text-small text-muted">
                      {p.prState[pr.state]}
                      {pr.reviewRequested && pr.state === "open" ? p.reviewRequested : ""}
                      {pr.draft ? p.draft : ""}
                    </span>
                  </li>
                ))}
              </Section>

              <Section title={p.children} count={shown.children.length}>
                {shown.children.map((c) => (
                  <li key={c.id}>
                    <button
                      type="button"
                      onClick={() => open(c.id)}
                      className="-mx-1.5 flex w-[calc(100%+0.75rem)] cursor-pointer items-center justify-between gap-3 rounded-control px-1.5 py-2 text-left text-ui hover:bg-hover"
                    >
                      <span className="truncate">
                        <span className="font-mono text-meta text-muted">
                          {c.repositoryName} #{c.number}
                        </span>{" "}
                        {c.title}
                      </span>
                      <StatusBadge status={c.status} />
                    </button>
                  </li>
                ))}
              </Section>

              <Section title={p.meetings} count={shown.meetingNotes.length}>
                {shown.meetingNotes.map((n) => (
                  <li key={n.id} className="py-2 text-ui">
                    <Link href={`/notes/view?id=${n.id}`} className="link">
                      {n.title}
                    </Link>{" "}
                    <span className="text-small text-muted">{fmt.date(n.heldAt)}</span>
                  </li>
                ))}
              </Section>

              <Section
                title={p.conversation}
                count={conversation.length}
                divided={false}
                footer={<CommentBox issueId={shown.id} htmlUrl={shown.htmlUrl} onPosted={onPosted} />}
              >
                {conversation.map((c, i) => (
                  <li key={c.id ?? `posted-${i}`} className="border-l-2 border-rule-strong py-0.5 pl-3 text-ui">
                    <div className="mb-1 text-small text-muted">
                      <span className="font-medium text-ink-soft">{fmt.person(c.author)}</span> · <span>{fmt.relativeDays(c.createdAt)}</span>
                    </div>
                    <p className="whitespace-pre-wrap text-ink-soft">{c.body}</p>
                    {c.mirrorPending && <p className="mt-1 text-meta text-muted">{p.comment.pending}</p>}
                  </li>
                ))}
              </Section>
            </>
          )}
        </div>
        )}
      </aside>
    </>
  );
}

type ConversationItem = Pick<PostedComment, "id" | "author" | "body" | "createdAt" | "mirrorPending">;

type AssignState = { issueId: string } & (
  | { kind: "saving" }
  | { kind: "dropped"; names: string[] }
  | { kind: "failed"; reason: AssignFailure; retry: (() => void) | null }
);

type AssignFailure = keyof Dict["panel"]["assign"]["errors"];

const PRIORITIES: Priority[] = ["urgent", "high", "normal", "low"];
const SIZES: Size[] = ["S", "M", "L"];

/** Confirms the create in place; the card is already on the board, at the top of its column. */
function CreatedNote({ issue, dropped }: { issue: CreatedIssue; dropped: string[] }) {
  const t = useT();
  const ref = `${issue.repository.name} #${issue.number}`;
  return (
    <div role="status" className="flex flex-col gap-1 rounded-item bg-sunken px-4 py-3 text-small">
      <p className="flex flex-wrap items-center gap-x-2 gap-y-1 text-ink">
        <svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden className="shrink-0">
          <path d="m3.5 8.5 3 3 6-7" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
        <span>
          <span className="font-mono text-meta font-medium">{ref}</span> {t.newIssue.created}
        </span>
        {issue.htmlUrl && (
          <>
            <span aria-hidden className="text-faint">
              ·
            </span>
            <GitHubLink href={issue.htmlUrl} label={t.github.openRef(ref)} className="link inline-flex items-center gap-1 text-ink">
              {t.newIssue.openOnGitHub}
            </GitHubLink>
          </>
        )}
      </p>
      {dropped.length > 0 && <p className="pl-[22px] text-ink-soft">{t.newIssue.dropped(dropped.join(", "))}</p>}
    </div>
  );
}

function Section({
  title,
  count,
  divided = true,
  footer,
  children,
}: {
  title: string;
  count: number;
  divided?: boolean;
  footer?: React.ReactNode;
  children: React.ReactNode;
}) {
  const t = useT();
  return (
    <section>
      <h3 className="mb-2 flex items-center gap-2 text-heading font-semibold">
        {title} <Count n={count} />
      </h3>
      {count === 0 ? (
        <p className="text-small text-muted">{t.common.none}</p>
      ) : (
        <ul className={divided ? "divide-y divide-rule" : "flex flex-col gap-3"}>{children}</ul>
      )}
      {footer}
    </section>
  );
}
