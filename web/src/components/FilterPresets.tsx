"use client";

import { useRouter, useSearchParams } from "next/navigation";
import { type BoardFilters, FILTER_KEYS } from "@/lib/filters";
import { useT } from "@/lib/i18n";

type Field = "statuses" | "stale" | "people" | "sprint" | "project" | "labels" | "repository" | "doneThisWeek";

const ON: Record<Field, (f: BoardFilters) => boolean> = {
  statuses: (f) => f.statuses.length > 0,
  stale: (f) => f.stale,
  people: (f) => f.people.length > 0,
  sprint: (f) => f.sprint !== null,
  project: (f) => f.project !== null,
  labels: (f) => f.labels.length > 0,
  repository: (f) => f.repository !== null,
  doneThisWeek: (f) => f.doneThisWeek,
};

/** True when `field` is the only filter turned on. */
function only(f: BoardFilters, field: Field): boolean {
  return (Object.keys(ON) as Field[]).every((k) => (k === field ? ON[k](f) : !ON[k](f)));
}

function anyOn(f: BoardFilters): boolean {
  return (Object.keys(ON) as Field[]).some((k) => ON[k](f));
}

/**
 * One-click presets above the filter bar. Each switches the whole board to a saved view by replacing the URL
 * filters. A preset is hidden when it has nothing to point at (no sign-in login, no active sprint).
 */
export function FilterPresets({
  filters,
  myLogin,
  activeSprintId,
}: {
  filters: BoardFilters;
  myLogin: string | null;
  activeSprintId: string | null;
}) {
  const t = useT();
  const p = t.board.presets;
  const router = useRouter();
  const params = useSearchParams();

  // Switch the view: clear every filter key (and the view marker) but keep the rest of the URL, such as an
  // open issue panel (?issue=), then set this preset's own keys.
  const go = (set: (next: URLSearchParams) => void) => {
    const next = new URLSearchParams(params.toString());
    FILTER_KEYS.forEach((k) => next.delete(k));
    next.delete("view");
    set(next);
    router.replace(`/board?${next.toString()}`, { scroll: false });
  };

  const presets = [
    myLogin && {
      key: "mine",
      label: p.mine,
      active: only(filters, "people") && filters.people.length === 1 && filters.people[0] === myLogin,
      set: (n: URLSearchParams) => n.set("person", myLogin),
    },
    activeSprintId && {
      key: "sprint",
      label: p.sprint,
      active: only(filters, "sprint") && filters.sprint === activeSprintId,
      set: (n: URLSearchParams) => n.set("sprint", activeSprintId),
    },
    { key: "stale", label: p.stale, active: only(filters, "stale"), set: (n: URLSearchParams) => n.set("stale", "1") },
    { key: "all", label: p.all, active: !anyOn(filters), set: (n: URLSearchParams) => n.set("view", "all") },
  ].filter((x): x is { key: string; label: string; active: boolean; set: (n: URLSearchParams) => void } => Boolean(x));

  return (
    <div role="group" aria-label={p.label} className="seg self-start">
      {presets.map((it) => (
        <button key={it.key} type="button" aria-pressed={it.active} onClick={() => go(it.set)}>
          {it.label}
        </button>
      ))}
    </div>
  );
}
