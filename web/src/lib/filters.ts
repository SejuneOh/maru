import { FILTER_STATUSES, isStale } from "./labels";
import type { BoardIssue, Status } from "./types";

/** Board filters carried in the URL so the overview can link straight into a filtered board. */
export type BoardFilters = {
  statuses: Status[];
  stale: boolean;
  /** Multi-select: any of these assignee logins. */
  people: string[];
  sprint: string | null;
  project: string | null;
  /** Multi-select: any of these label names. */
  labels: string[];
  repository: string | null;
  /** `doneSince=week`: only issues closed since Monday 00:00 Asia/Seoul. */
  doneThisWeek: boolean;
};

export const NO_FILTERS: BoardFilters = {
  statuses: [],
  stale: false,
  people: [],
  sprint: null,
  project: null,
  labels: [],
  repository: null,
  doneThisWeek: false,
};

type Params = { get(name: string): string | null };

export const FILTER_KEYS = ["status", "stale", "person", "sprint", "project", "label", "repository", "doneSince"] as const;
export type FilterKey = (typeof FILTER_KEYS)[number];

const csv = (value: string | null): string[] =>
  (value ?? "")
    .split(",")
    .map((s) => s.trim())
    .filter(Boolean);

export function readFilters(params: Params): BoardFilters {
  const statuses = csv(params.get("status"));
  const stale = params.get("stale");
  return {
    statuses: FILTER_STATUSES.filter((s) => statuses.includes(s)),
    stale: stale === "1" || stale === "true",
    people: csv(params.get("person")),
    sprint: params.get("sprint") || null,
    project: params.get("project") || null,
    labels: csv(params.get("label")),
    repository: params.get("repository") || null,
    doneThisWeek: params.get("doneSince") === "week",
  };
}

export function hasFilters(f: BoardFilters): boolean {
  return (
    f.statuses.length > 0 ||
    f.stale ||
    f.people.length > 0 ||
    f.sprint !== null ||
    f.project !== null ||
    f.labels.length > 0 ||
    f.repository !== null ||
    f.doneThisWeek
  );
}

/** Query string for `GET /api/issues`, including `extra` pairs such as `projectId` or `scope`. */
export function issuesQuery(f: BoardFilters, extra: Record<string, string> = {}): string {
  const q = new URLSearchParams(extra);
  if (f.statuses.length) q.set("status", f.statuses.join(","));
  if (f.stale) q.set("stale", "true");
  if (f.people.length) q.set("person", f.people.join(","));
  if (f.sprint) q.set("sprint", f.sprint);
  if (f.project) q.set("projectId", f.project);
  if (f.labels.length) q.set("label", f.labels.join(","));
  if (f.repository) q.set("repository", f.repository);
  if (f.doneThisWeek) q.set("doneSince", "week");
  const s = q.toString();
  return s ? `/api/issues?${s}` : "/api/issues";
}

/**
 * The server filters too; this keeps the board right if it is talking to an older API.
 * The week boundary itself is left to the server, which knows the Asia/Seoul Monday.
 */
export function applyFilters(issues: BoardIssue[], f: BoardFilters, projectRepoIds?: ReadonlySet<string> | null): BoardIssue[] {
  return issues.filter(
    (i) =>
      (f.statuses.length === 0 || f.statuses.includes(i.status)) &&
      (!f.stale || isStale(i)) &&
      (f.people.length === 0 || i.assignees.some((a) => f.people.includes(a.githubLogin))) &&
      (!f.sprint || i.sprint?.id === f.sprint) &&
      (f.labels.length === 0 || i.labels.some((l) => f.labels.includes(l.name))) &&
      (!f.repository || i.repository.id === f.repository) &&
      // The board resolves the project filter here from its repositories, so a change needs no refetch.
      (!f.project || (projectRepoIds?.has(i.repository.id) ?? false)) &&
      (!f.doneThisWeek || i.state === "closed"),
  );
}
