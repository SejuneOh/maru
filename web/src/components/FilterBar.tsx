"use client";

import { useEffect, useRef, useState } from "react";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { type BoardFilters, FILTER_KEYS } from "@/lib/filters";
import { useT } from "@/lib/i18n";
import { STATUS_DOT, STATUS_ORDER } from "@/lib/labels";
import type { Member, Project, Status } from "@/lib/types";
import { Dot } from "./ui";

type Option = { value: string; label: string; dot?: string };

const CONTROL = "h-8 cursor-pointer rounded-full text-small font-medium transition-colors duration-[120ms] ease-tool";
const TRIGGER_IDLE = "bg-surface text-ink shadow-[inset_0_0_0_1px_var(--wb-rule)] hover:bg-hover";
const TRIGGER_ON = "bg-ink text-surface";

/** A modern popover filter: a pill trigger opening a checkable list (multi) or a single-choice list. */
function FilterMenu({
  label,
  options,
  selected,
  multi,
  onToggle,
  onClear,
}: {
  label: string;
  options: Option[];
  selected: string[];
  multi: boolean;
  onToggle: (value: string) => void;
  onClear: () => void;
}) {
  const t = useT();
  const p = t.filters.pick;
  const [open, setOpen] = useState(false);
  const [q, setQ] = useState("");
  const ref = useRef<HTMLDivElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    if (!open) {
      setQ("");
      return;
    }
    const onDown = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        setOpen(false);
        triggerRef.current?.focus();
      }
    };
    document.addEventListener("mousedown", onDown);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onDown);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);

  const searchable = options.length > 8;
  const shown = searchable && q ? options.filter((o) => o.label.toLowerCase().includes(q.toLowerCase())) : options;
  const on = selected.length > 0;
  const triggerText = !on ? `${label}: ${p.any}` : multi ? `${label} · ${selected.length}` : (options.find((o) => o.value === selected[0])?.label ?? label);

  return (
    <div className="relative" ref={ref}>
      <button ref={triggerRef} type="button" aria-haspopup="menu" aria-expanded={open} onClick={() => setOpen((o) => !o)} className={`${CONTROL} inline-flex items-center gap-1.5 pl-3.5 pr-3 ${on ? TRIGGER_ON : TRIGGER_IDLE}`}>
        <span className="max-w-[12rem] truncate">{triggerText}</span>
        <svg width="12" height="12" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.75" aria-hidden className={on ? "text-surface/80" : "text-muted"}>
          <path d="m4 6 4 4 4-4" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      </button>

      {open && (
        <div className="absolute left-0 z-40 mt-1.5 max-h-[18rem] w-56 max-w-[calc(100vw-2rem)] overflow-auto rounded-item bg-surface p-1 shadow-elev-2 ring-1 ring-[var(--wb-rule-strong)]">
          {searchable && (
            <input
              autoFocus
              value={q}
              onChange={(e) => setQ(e.target.value)}
              placeholder={p.search}
              className="mb-1 h-8 w-full rounded-control bg-sunken px-3 text-small text-ink placeholder:text-muted"
            />
          )}
          <div role="menu" aria-label={label}>
          {shown.length === 0 && <p className="px-2.5 py-2 text-small text-muted">{p.empty}</p>}
          {shown.map((o) => {
            const checked = selected.includes(o.value);
            return (
              <button
                key={o.value}
                type="button"
                role={multi ? "menuitemcheckbox" : "menuitemradio"}
                aria-checked={checked}
                onClick={() => {
                  onToggle(o.value);
                  if (!multi) {
                    setOpen(false);
                    triggerRef.current?.focus();
                  }
                }}
                className="flex w-full items-center gap-2.5 rounded-control px-2.5 py-1.5 text-left text-small text-ink hover:bg-hover"
              >
                <span className={`grid h-4 w-4 shrink-0 place-items-center ${multi ? "rounded-[5px]" : "rounded-full"} ${checked ? "bg-ink text-surface" : "ring-1 ring-[var(--wb-rule-strong)]"}`}>
                  {checked && (
                    <svg width="10" height="10" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2.5" aria-hidden>
                      <path d="m3.5 8.5 3 3 6-7" strokeLinecap="round" strokeLinejoin="round" />
                    </svg>
                  )}
                </span>
                {o.dot && <Dot className={o.dot} />}
                <span className="min-w-0 truncate">{o.label}</span>
              </button>
            );
          })}
          </div>
          {on && (
            <button type="button" onClick={onClear} className="mt-1 w-full rounded-control px-2.5 py-1.5 text-left text-small text-ink-soft hover:bg-hover hover:text-ink">
              {p.clear}
            </button>
          )}
        </div>
      )}
    </div>
  );
}

type Props = {
  filters: BoardFilters;
  projects: Project[];
  members: Member[];
  labels: { name: string }[];
  repositories: { id: string; name: string }[];
  myLogin?: string | null;
};

/** The pickers that set board filters. Each writes its key into the URL; FilterChips shows and clears them. */
export function FilterBar({ filters, projects, members, labels, repositories, myLogin }: Props) {
  const t = useT();
  const p = t.filters.pick;
  const router = useRouter();
  const pathname = usePathname();
  const params = useSearchParams();

  const apply = (mutate: (next: URLSearchParams) => void) => {
    const next = new URLSearchParams(params.toString());
    mutate(next);
    // With no filter left, land on the explicit "all" view, never a bare URL (the board's default-view memory).
    if (FILTER_KEYS.some((k) => next.has(k))) next.delete("view");
    else next.set("view", "all");
    router.replace(`${pathname}?${next.toString()}`, { scroll: false });
  };
  const setSingle = (key: string, value: string | null) => apply((n) => (value ? n.set(key, value) : n.delete(key)));
  const toggleMulti = (key: string, current: string[], value: string) => {
    const nextList = current.includes(value) ? current.filter((x) => x !== value) : [...current, value];
    apply((n) => (nextList.length ? n.set(key, nextList.join(",")) : n.delete(key)));
  };

  const mine = myLogin != null && filters.people.length === 1 && filters.people[0] === myLogin;
  const showAll = () => router.replace(`${pathname}?view=all`, { scroll: false });
  const allOn = !FILTER_KEYS.some((k) => params.has(k));
  const pill = (on: boolean) => `${CONTROL} px-3.5 ${on ? TRIGGER_ON : TRIGGER_IDLE}`;

  return (
    <div className="flex flex-wrap items-center gap-2" aria-label={t.filters.label}>
      <FilterMenu
        label={p.status}
        multi
        selected={filters.statuses}
        onToggle={(v) => toggleMulti("status", filters.statuses, v)}
        onClear={() => setSingle("status", null)}
        options={STATUS_ORDER.map((s: Status) => ({ value: s, label: t.status[s], dot: STATUS_DOT[s] }))}
      />
      <FilterMenu
        label={p.person}
        multi
        selected={filters.people}
        onToggle={(v) => toggleMulti("person", filters.people, v)}
        onClear={() => setSingle("person", null)}
        options={members.map((m) => ({ value: m.githubLogin, label: m.displayName ?? m.githubLogin }))}
      />
      <FilterMenu
        label={p.label}
        multi
        selected={filters.labels}
        onToggle={(v) => toggleMulti("label", filters.labels, v)}
        onClear={() => setSingle("label", null)}
        options={labels.map((l) => ({ value: l.name, label: l.name }))}
      />
      <FilterMenu
        label={p.project}
        multi={false}
        selected={filters.project ? [filters.project] : []}
        onToggle={(v) => setSingle("project", filters.project === v ? null : v)}
        onClear={() => setSingle("project", null)}
        options={projects.map((pr) => ({ value: pr.id, label: pr.name }))}
      />
      <FilterMenu
        label={p.repository}
        multi={false}
        selected={filters.repository ? [filters.repository] : []}
        onToggle={(v) => setSingle("repository", filters.repository === v ? null : v)}
        onClear={() => setSingle("repository", null)}
        options={repositories.map((r) => ({ value: r.id, label: r.name }))}
      />

      {myLogin != null && (
        <button type="button" aria-pressed={mine} onClick={() => setSingle("person", mine ? null : myLogin)} className={pill(mine)}>
          {p.mine}
        </button>
      )}
      <button type="button" aria-pressed={allOn} onClick={showAll} className={pill(allOn)}>
        {p.showAll}
      </button>
    </div>
  );
}
