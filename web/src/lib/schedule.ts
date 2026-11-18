import { addDays, daysBetween } from "./sprints";
import type { ProjectSchedule, ScheduleMilestone, ScheduleSprint } from "./types";

/** Shortest span the axis shows, so a single date does not stretch across the whole track. */
const MIN_SPAN = 14;

export type Axis = { from: string; to: string; today: number; x: (ymd: string) => number };

export type ScheduleRow =
  | { kind: "sprint"; key: string; date: string; sprint: ScheduleSprint }
  | { kind: "milestone"; key: string; date: string; milestone: ScheduleMilestone & { dueDate: string } };

export type ScheduleView = {
  axis: Axis | null;
  sprints: ScheduleRow[];
  milestones: ScheduleRow[];
  undated: ScheduleMilestone[];
  overdue: number;
  /** The nearest date still ahead (or today) that is not finished: a milestone due date or a sprint end. */
  next: { name: string; date: string; days: number } | null;
};

/**
 * One time axis shared by every row, in percent of the track (0 = `from`, 100 = `to`). It covers every date on the
 * schedule and today, with a little room on both ends. Null when nothing has a date.
 */
export function scheduleAxis(dates: string[], today: string): Axis | null {
  if (dates.length === 0) return null;
  const all = [...dates, today].sort();
  let from = all[0];
  let to = all[all.length - 1];
  const span = daysBetween(from, to);
  if (span < MIN_SPAN) {
    const grow = MIN_SPAN - span;
    from = addDays(from, -Math.floor(grow / 2));
    to = addDays(to, Math.ceil(grow / 2));
  }
  const pad = Math.max(1, Math.round(daysBetween(from, to) * 0.04));
  from = addDays(from, -pad);
  to = addDays(to, pad);
  const total = daysBetween(from, to);
  const x = (ymd: string) => (daysBetween(from, ymd) / total) * 100;
  return { from, to, today: x(today), x };
}

export function scheduleView(s: ProjectSchedule): ScheduleView {
  const dated = s.milestones.filter((m): m is ScheduleMilestone & { dueDate: string } => m.dueDate !== null);
  const undated = s.milestones.filter((m) => m.dueDate === null);
  const axis = scheduleAxis([...s.sprints.flatMap((x) => [x.startDate, x.endDate]), ...dated.map((m) => m.dueDate)], s.today);

  const ahead = [
    ...dated.filter((m) => m.state === "open").map((m) => ({ name: m.title, date: m.dueDate })),
    ...s.sprints.filter((x) => x.state !== "closed").map((x) => ({ name: x.name, date: x.endDate })),
  ]
    .filter((d) => d.date >= s.today)
    .sort((a, b) => a.date.localeCompare(b.date))[0];

  return {
    axis,
    sprints: s.sprints.map((sprint) => ({ kind: "sprint", key: `s-${sprint.id}`, date: sprint.startDate, sprint })),
    milestones: dated.map((milestone) => ({ kind: "milestone", key: `m-${milestone.id}`, date: milestone.dueDate, milestone })),
    undated,
    overdue: s.milestones.filter((m) => m.overdueDays > 0).length + s.sprints.filter((x) => x.overdueDays > 0).length,
    next: ahead ? { ...ahead, days: daysBetween(s.today, ahead.date) } : null,
  };
}
