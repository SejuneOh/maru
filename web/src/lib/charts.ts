import { parseDay } from "./overview";
import type { SprintDay } from "./types";

export type Point = { x: number; y: number };

export type TrendRow = { day: string; remaining: number | null; ideal: number | null };

export type TrendGeometry = {
  /** Runs of consecutive recorded days; a missing day ends a run, so the line breaks there. */
  runs: Point[][];
  ideal: [Point, Point] | null;
  last: Point & { remaining: number };
  hasGaps: boolean;
  /** One row per sprint day, for the screen-reader table. */
  rows: TrendRow[];
};

const MAX_DAYS = 62;

function dayKey(d: Date): string {
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
}

export function sprintDays(startDate: string, endDate: string): string[] {
  const end = parseDay(endDate);
  const days: string[] = [];
  for (let d = parseDay(startDate); d <= end && days.length < MAX_DAYS; d.setDate(d.getDate() + 1)) days.push(dayKey(d));
  return days;
}

/**
 * Coordinates in percent of the plot box (x: 0 = start date, 100 = end date; y: 0 = top, 100 = zero issues).
 * The ideal line runs from the first recorded day's scope down to 0 on the end date. Returns null with no records.
 */
export function trendGeometry(startDate: string, endDate: string, trend: SprintDay[]): TrendGeometry | null {
  const days = sprintDays(startDate, endDate);
  const index = new Map(days.map((d, i) => [d, i]));
  const recorded = trend.filter((r) => index.has(r.day));
  if (days.length === 0 || recorded.length === 0) return null;

  const span = Math.max(days.length - 1, 1);
  const top = Math.max(1, ...recorded.map((r) => Math.max(r.scope, r.remaining))) * 1.15;
  const x = (i: number) => (days.length === 1 ? 50 : (i / span) * 100);
  const y = (v: number) => 100 - (v / top) * 100;

  const runs: Point[][] = [];
  let prev = -2;
  for (const r of recorded) {
    const i = index.get(r.day)!;
    if (i !== prev + 1) runs.push([]);
    runs[runs.length - 1].push({ x: x(i), y: y(r.remaining) });
    prev = i;
  }

  const first = recorded[0];
  const i0 = index.get(first.day)!;
  const last = days.length - 1;
  const idealAt = (i: number) => (i < i0 ? null : last === i0 ? 0 : first.scope * (1 - (i - i0) / (last - i0)));
  const ideal: [Point, Point] | null =
    i0 < last ? [{ x: x(i0), y: y(first.scope) }, { x: x(last), y: y(0) }] : null;

  const byDay = new Map(recorded.map((r) => [r.day, r.remaining]));
  const rows = days.map((d, i) => {
    const v = idealAt(i);
    return { day: d, remaining: byDay.get(d) ?? null, ideal: v === null ? null : Math.round(v * 10) / 10 };
  });

  const end = recorded[recorded.length - 1];
  return {
    runs,
    ideal,
    last: { x: x(index.get(end.day)!), y: y(end.remaining), remaining: end.remaining },
    hasGaps: runs.length > 1,
    rows,
  };
}

export function polyline(points: Point[]): string {
  return points.map((p) => `${p.x},${p.y}`).join(" ");
}
