"use client";

import Link from "next/link";
import { Empty, ErrorBox, Loading } from "@/components/Feedback";
import { Count, Flag } from "@/components/ui";
import { useApi } from "@/lib/api";
import { useFormat, useT } from "@/lib/i18n";
import { safeHttpUrl } from "@/lib/labels";
import { type Axis, type ScheduleRow, scheduleView } from "@/lib/schedule";
import { addDays, daysBetween, dateOnly } from "@/lib/sprints";
import type { ProjectSchedule, ScheduleMilestone, ScheduleSprint } from "@/lib/types";

const rowGrid = "grid grid-cols-1 gap-x-6 gap-y-2 @[35rem]:grid-cols-[minmax(0,15rem)_minmax(0,1fr)] @[35rem]:items-center";
const rowClass = `${rowGrid} relative px-5 py-3 text-left transition-colors duration-[120ms] ease-tool after:absolute after:inset-x-5 after:top-0 after:h-px after:bg-rule`;
const linkRow = `${rowClass} hover:bg-hover focus-visible:bg-hover focus-visible:-outline-offset-2`;

/** One project's milestones and cycles on a shared date axis, with today as a vertical line. */
export function Schedule({ projectId }: { projectId: string }) {
  const t = useT();
  const sc = t.projects.schedule;
  const fmt = useFormat();
  const res = useApi<ProjectSchedule>(`/api/projects/${projectId}/schedule`);

  if (res.error) return <ErrorBox error={res.error} />;
  if (!res.data) return <Loading />;

  const s = res.data;
  const view = scheduleView(s);
  const day = (ymd: string) => fmt.date(dateOnly(ymd));
  const lead =
    view.overdue > 0
      ? sc.leadOverdue(view.overdue)
      : view.next
        ? sc.leadNext(view.next.name, day(view.next.date), view.next.days)
        : sc.leadClear;

  if (!view.axis && view.undated.length === 0) return <Empty>{sc.empty}</Empty>;

  return (
    <div className="card @container overflow-hidden pb-2">
      <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1 px-5 pb-1 pt-4">
        <h3 className="text-heading font-semibold">{sc.title}</h3>
        <span className="text-small text-muted">{sc.hint}</span>
      </div>
      <p className="px-5 pb-3 text-body text-ink-soft">
        {lead}
      </p>

      {!view.axis ? (
        <div className="px-5 pb-3">
          <Empty>{sc.onlyUndated}</Empty>
        </div>
      ) : (
        <>
          <AxisHead axis={view.axis} today={s.today} />
          {view.sprints.length > 0 && <Group title={sc.sprints} rows={view.sprints} axis={view.axis} today={s.today} />}
          {view.milestones.length > 0 && <Group title={sc.milestones} rows={view.milestones} axis={view.axis} today={s.today} />}
        </>
      )}

      {view.undated.length > 0 && (
        <section aria-label={sc.undated}>
          <GroupHead title={sc.undated} count={view.undated.length} hint={sc.undatedHint} />
          <ul>
            {view.undated.map((m) => (
              <li key={m.id}>
                <MilestoneRow m={m} />
              </li>
            ))}
          </ul>
        </section>
      )}
    </div>
  );
}

function GroupHead({ title, count, hint }: { title: string; count: number; hint?: string }) {
  return (
    <div className="flex flex-wrap items-baseline gap-x-2 px-5 pb-2 pt-5">
      <h4 className="text-small font-semibold text-ink-soft">{title}</h4>
      <Count n={count} />
      {hint && <span className="text-small text-muted">{hint}</span>}
    </div>
  );
}

function Group({ title, rows, axis, today }: { title: string; rows: ScheduleRow[]; axis: Axis; today: string }) {
  return (
    <section aria-label={title}>
      <GroupHead title={title} count={rows.length} />
      <ul>
        {rows.map((r) => (
          <li key={r.key}>
            {r.kind === "sprint" ? (
              <SprintRow s={r.sprint} axis={axis} today={today} />
            ) : (
              <MilestoneRow m={r.milestone} axis={axis} today={today} />
            )}
          </li>
        ))}
      </ul>
    </section>
  );
}

/** Dates on the axis sit at the middle of their day, so a one-day cycle and a due date line up with the today line. */
const mid = (axis: Axis, ymd: string) => (axis.x(ymd) + axis.x(addDays(ymd, 1))) / 2;

function AxisHead({ axis, today }: { axis: Axis; today: string }) {
  const t = useT();
  const fmt = useFormat();
  const at = mid(axis, today);
  const short = (ymd: string) => fmt.shortDate(dateOnly(ymd));
  return (
    <div className={`${rowGrid} px-5 pt-1`} aria-hidden>
      <span className="hidden @[35rem]:block" />
      <div className="relative h-5 text-meta text-muted">
        {at > 18 && <span className="absolute left-0 top-0 font-mono">{short(axis.from)}</span>}
        {at < 82 && <span className="absolute right-0 top-0 font-mono">{short(axis.to)}</span>}
        <span
          className="absolute top-0 whitespace-nowrap font-medium text-ink"
          style={{ left: `${at}%`, transform: `translateX(${at < 10 ? "0" : at > 90 ? "-100%" : "-50%"})` }}
        >
          {t.projects.schedule.today} <span className="font-mono font-normal text-muted">{short(today)}</span>
        </span>
      </div>
    </div>
  );
}

/** Bar track: the today line, the item, and, for a row past its date and not finished, the overdue stretch up to today. */
function Track({ axis, today, title, children, overdueFrom }: {
  axis: Axis;
  today: string;
  title: string;
  children: React.ReactNode;
  overdueFrom?: number;
}) {
  const now = mid(axis, today);
  return (
    <div className="relative h-6" title={title} aria-hidden>
      <span className="absolute inset-x-0 top-1/2 h-px bg-rule" />
      {overdueFrom !== undefined && (
        <span className="absolute top-1/2 h-0.5 -translate-y-1/2 rounded-full bg-accent" style={{ left: `${overdueFrom}%`, width: `${Math.max(now - overdueFrom, 0)}%` }} />
      )}
      {children}
      <span className="absolute -bottom-1 -top-1 w-px @[35rem]:-bottom-3 @[35rem]:-top-3 -translate-x-1/2 bg-ink-soft" style={{ left: `${now}%` }} />
    </div>
  );
}

function SprintRow({ s, axis, today }: { s: ScheduleSprint; axis: Axis; today: string }) {
  const t = useT();
  const sc = t.projects.schedule;
  const fmt = useFormat();
  const day = (ymd: string) => fmt.date(dateOnly(ymd));
  const left = axis.x(s.startDate);
  const right = axis.x(addDays(s.endDate, 1));
  const done = s.issueCount > 0 ? (s.doneCount / s.issueCount) * 100 : 0;
  const finished = s.state === "closed" && s.overdueDays === 0;
  const timing =
    s.state === "planned"
      ? sc.startsIn(daysBetween(today, s.startDate))
      : s.state === "active"
        ? sc.daysLeft(daysBetween(today, s.endDate))
        : sc.sprintState.closed;
  const parts = [sc.range(day(s.startDate), day(s.endDate)), s.state === "active" ? sc.sprintState.active : null, timing, sc.doneOf(s.doneCount, s.issueCount)];
  if (s.overdueDays > 0) parts.push(sc.unfinished(s.unfinishedCount));
  const meta = parts.filter(Boolean).join(" · ");

  return (
    <Link href="/sprints" data-row="" className={`${linkRow} ${finished ? "text-muted" : ""}`}>
      <div className="min-w-0">
        <div className="flex min-w-0 items-center gap-2">
          <span className="sr-only">{sc.sprints}: </span>
          <span className={`truncate text-ui font-medium ${finished ? "text-muted" : "text-ink"}`}>{s.name}</span>
          {s.overdueDays > 0 && <Flag className="ml-auto shrink-0">{sc.overdue(s.overdueDays)}</Flag>}
        </div>
        <p className="text-small tabular-nums text-muted">{meta}</p>
      </div>
      <Track axis={axis} today={today} title={`${s.name} · ${meta}`} overdueFrom={s.overdueDays > 0 ? right : undefined}>
        <span
          className={`absolute top-1/2 flex h-1.5 min-w-1 -translate-y-1/2 overflow-hidden rounded-[3px] shadow-[0_0_0_2px_var(--wb-surface)] ${
            s.state === "planned" ? "bg-sunken ring-1 ring-inset ring-rule-strong" : "bg-rule-strong"
          } ${finished ? "opacity-55" : ""}`}
          style={{ left: `${left}%`, width: `${right - left}%` }}
        >
          {done > 0 && <span className="h-full bg-status-done" style={{ width: `${done}%` }} />}
        </span>
      </Track>
    </Link>
  );
}

function MilestoneRow({ m, axis, today }: { m: ScheduleMilestone; axis?: Axis; today?: string }) {
  const t = useT();
  const sc = t.projects.schedule;
  const fmt = useFormat();
  const day = (ymd: string) => fmt.date(dateOnly(ymd));
  const closed = m.state === "closed";
  const parts = [
    m.dueDate ? sc.due(day(m.dueDate)) : null,
    closed ? sc.milestoneClosed : m.dueDate && today && m.overdueDays === 0 ? sc.daysLeft(daysBetween(today, m.dueDate)) : null,
    sc.doneOf(m.doneCount, m.issueCount),
  ];
  const meta = parts.filter(Boolean).join(" · ");
  const href = safeHttpUrl(m.htmlUrl);
  const at = axis && m.dueDate ? mid(axis, m.dueDate) : null;

  const body = (
    <>
      <div className="min-w-0">
        <div className="flex min-w-0 items-center gap-2">
          <span className="sr-only">{sc.milestones}: </span>
          <span className={`truncate text-ui font-medium ${closed ? "text-muted" : "text-ink"}`}>{m.title}</span>
          {m.overdueDays > 0 && <Flag className="ml-auto shrink-0">{sc.overdue(m.overdueDays)}</Flag>}
        </div>
        <p className="text-small tabular-nums text-muted">
          <span className="font-mono text-meta">
            {m.repository.name} #{m.number}
          </span>
          {" · "}
          {meta}
          {href && <span className="sr-only"> ({sc.external})</span>}
        </p>
      </div>
      {axis && today && at !== null && (
        <Track axis={axis} today={today} title={`${m.title} · ${meta}`} overdueFrom={m.overdueDays > 0 ? at : undefined}>
          <span
            className={`absolute top-1/2 h-2.5 w-2.5 -translate-x-1/2 -translate-y-1/2 rotate-45 rounded-[2px] shadow-[0_0_0_2px_var(--wb-surface)] ${
              closed ? "bg-status-done opacity-55" : "bg-ink-soft"
            }`}
            style={{ left: `${at}%` }}
          />
        </Track>
      )}
    </>
  );

  return href ? (
    <a href={href} target="_blank" rel="noreferrer" data-row="" className={linkRow}>
      {body}
    </a>
  ) : (
    <div className={rowClass}>{body}</div>
  );
}
