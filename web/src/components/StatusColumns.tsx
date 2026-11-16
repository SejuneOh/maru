"use client";

import { useT } from "@/lib/i18n";
import { STATUS_DOT, STATUS_ORDER, groupByStatus } from "@/lib/labels";
import type { BoardIssue, NewIssueStatus, Status } from "@/lib/types";
import { IssueCard } from "./IssueCard";
import { Count, Dot } from "./ui";
import { useIssuePanel } from "./useIssuePanel";

const ADDABLE: Status[] = ["todo", "in_progress", "in_review"];

/** `+` at the end of a column head: opens the new-issue form starting in that column's status. */
export function AddToColumn({ status, assignee }: { status: Status; assignee?: { githubUserId: number; name: string } }) {
  const t = useT();
  const { startNew } = useIssuePanel();
  if (!ADDABLE.includes(status)) return null;
  const label = assignee ? t.newIssue.addFor(t.status[status], assignee.name) : t.newIssue.addTo(t.status[status]);
  return (
    <button
      type="button"
      onClick={() => startNew({ status: status as NewIssueStatus, assignee: assignee?.githubUserId })}
      aria-label={label}
      title={label}
      className="ml-auto grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-control text-muted transition-colors duration-[120ms] ease-tool hover:bg-hover hover:text-ink"
    >
      <svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.75" aria-hidden>
        <path d="M8 3v10M3 8h10" strokeLinecap="round" />
      </svg>
    </button>
  );
}

type Props = {
  issues: BoardIssue[];
  showAssignee?: boolean;
  statuses?: Status[];
  /** Whose board this is: a new issue started from a column is assigned to them. */
  owner?: { githubUserId: number; name: string };
};

/**
 * Column count for a board grid inside an `@container`, from the width the board actually has:
 * one column below 544px, two up to 1024px, then all of them (so no column gets narrower than ~240px).
 */
export function boardCols(count: number): string {
  if (count >= 4) return "@[34rem]:grid-cols-2 @[64rem]:grid-cols-4";
  if (count === 3) return "@[34rem]:grid-cols-2 @[48rem]:grid-cols-3";
  if (count === 2) return "@[34rem]:grid-cols-2";
  return "";
}

export function StatusColumns({ issues, showAssignee = true, statuses = STATUS_ORDER, owner }: Props) {
  const t = useT();
  const groups = groupByStatus(issues);
  const foldCancelled = !statuses.includes("cancelled");

  return (
    <div className="@container">
    <div className={`grid grid-cols-1 items-start gap-x-4 gap-y-6 ${boardCols(statuses.length)}`}>
      {statuses.map((status) => (
        <section key={status} className="flex min-w-0 flex-col gap-2">
          <h3 className="flex min-h-7 items-center gap-2 px-1 pb-1 text-ui font-semibold text-ink">
            <Dot className={STATUS_DOT[status]} />
            {t.status[status]}
            <Count n={groups[status].length} />
            <AddToColumn status={status} assignee={owner} />
          </h3>
          <div className="flex flex-col gap-2">
            {groups[status].length === 0 && (
              <p className="px-1 py-2 text-small text-muted">{t.common.none}</p>
            )}
            {groups[status].map((issue) => (
              <IssueCard key={issue.id} issue={issue} showAssignee={showAssignee} />
            ))}
            {status === "done" && foldCancelled && <CanceledGroup issues={groups.cancelled} showAssignee={showAssignee} />}
          </div>
        </section>
      ))}
    </div>
    </div>
  );
}

/** Cancelled work folds under done, closed by default, instead of taking a fifth column; hidden when there is none. */
export function CanceledGroup({ issues, showAssignee = true }: { issues: BoardIssue[]; showAssignee?: boolean }) {
  const t = useT();
  if (issues.length === 0) return null;
  return (
    <details className="group mt-1">
      <summary
        title={t.cancelledHint}
        className="flex cursor-pointer list-none items-center gap-2 rounded-control px-1 py-1.5 text-small text-muted hover:text-ink-soft"
      >
        <span aria-hidden className="transition-transform duration-200 ease-tool group-open:rotate-90">›</span>
        <Dot className={STATUS_DOT.cancelled} />
        {t.status.cancelled}
        <span className="font-mono">{issues.length}</span>
      </summary>
      <div className="mt-2 flex flex-col gap-2">
        {issues.map((issue) => (
          <IssueCard key={issue.id} issue={issue} showAssignee={showAssignee} />
        ))}
      </div>
    </details>
  );
}
