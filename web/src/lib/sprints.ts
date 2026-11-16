import type { BoardIssue, Size, Sprint } from "./types";

const SEOUL_DAY = new Intl.DateTimeFormat("en-CA", { timeZone: "Asia/Seoul", year: "numeric", month: "2-digit", day: "2-digit" });

/** Today as yyyy-MM-dd in Asia/Seoul, the day the server uses for sprint states. */
export function seoulToday(now = new Date()): string {
  return SEOUL_DAY.format(now);
}

const dayNumber = (ymd: string) => Date.UTC(Number(ymd.slice(0, 4)), Number(ymd.slice(5, 7)) - 1, Number(ymd.slice(8, 10))) / 86_400_000;

/** Whole days from `from` to `to` (both yyyy-MM-dd); negative when `to` is earlier. */
export function daysBetween(from: string, to: string): number {
  return dayNumber(to) - dayNumber(from);
}

export function addDays(ymd: string, n: number): string {
  return new Date((dayNumber(ymd) + n) * 86_400_000).toISOString().slice(0, 10);
}

/** Same rule as the overview: days until the end date, never below 0. */
export function daysLeft(sprint: Pick<Sprint, "endDate">, today = seoulToday()): number {
  return Math.max(0, daysBetween(today, sprint.endDate));
}

/** Noon keeps a date-only value on the same calendar day in every time zone when it is formatted. */
export const dateOnly = (ymd: string) => `${ymd}T12:00:00`;

export const SIZES: Size[] = ["S", "M", "L"];

/** Issue count and sizes the way the sprint counts them: cancelled issues left out. */
export function sprintTotals(issues: BoardIssue[]): { count: number; sizes: Record<Size, number> } {
  const sizes: Record<Size, number> = { S: 0, M: 0, L: 0 };
  let count = 0;
  for (const i of issues) {
    if (i.status === "cancelled") continue;
    count++;
    if (i.size) sizes[i.size]++;
  }
  return { count, sizes };
}

/** Longest name the API takes; it also refuses control and invisible format characters. */
export const SPRINT_NAME_MAX = 80;
const BAD_RANGES: [number, number][] = [
  [0x00, 0x1f],
  [0x7f, 0x9f],
  [0x200b, 0x200f],
  [0x202a, 0x202e],
  [0x2060, 0x2064],
  [0xfeff, 0xfeff],
];

export function nameHasBadChars(name: string): boolean {
  for (const ch of name) {
    const c = ch.codePointAt(0) ?? 0;
    if (BAD_RANGES.some(([a, b]) => c >= a && c <= b)) return true;
  }
  return false;
}
