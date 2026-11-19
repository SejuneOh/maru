"use client";

import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { type BoardFilters, FILTER_KEYS } from "@/lib/filters";
import { useT } from "@/lib/i18n";
import { STATUS_ORDER } from "@/lib/labels";
import type { Member, Project, Status } from "@/lib/types";

type LabelOption = { name: string };
type RepoOption = { id: string; name: string };

type Props = {
  filters: BoardFilters;
  projects: Project[];
  members: Member[];
  labels: LabelOption[];
  repositories: RepoOption[];
  /** The signed-in person's GitHub login, for the "mine only" toggle. */
  myLogin?: string | null;
};

const CONTROL =
  "h-8 cursor-pointer rounded-full text-small font-medium transition-colors duration-[120ms] ease-tool focus:outline-none";
const SELECT = `${CONTROL} appearance-none bg-surface pl-3.5 pr-8 text-ink shadow-[inset_0_0_0_1px_var(--wb-rule)] hover:bg-hover focus:shadow-[inset_0_0_0_1.5px_var(--wb-focus)]`;

/** A pill-shaped native select with a chevron, so the board filters read as one clean row of controls. */
function Picker({
  label,
  value,
  onChange,
  children,
}: {
  label: string;
  value: string;
  onChange: (v: string | null) => void;
  children: React.ReactNode;
}) {
  return (
    <div className="relative">
      <select aria-label={label} value={value} onChange={(e) => onChange(e.target.value || null)} className={SELECT}>
        {children}
      </select>
      <svg
        width="12"
        height="12"
        viewBox="0 0 16 16"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.75"
        aria-hidden
        className="pointer-events-none absolute right-3 top-1/2 -translate-y-1/2 text-muted"
      >
        <path d="m4 6 4 4 4-4" strokeLinecap="round" strokeLinejoin="round" />
      </svg>
    </div>
  );
}

/** The pickers that set board filters. Each writes its key into the URL; FilterChips shows and clears them. */
export function FilterBar({ filters, projects, members, labels, repositories, myLogin }: Props) {
  const t = useT();
  const p = t.filters.pick;
  const router = useRouter();
  const pathname = usePathname();
  const params = useSearchParams();

  const set = (key: string, value: string | null) => {
    const next = new URLSearchParams(params.toString());
    if (value) next.set(key, value);
    else next.delete(key);
    // Never leave the board on a bare URL: with no filter left, make it the explicit "all" view so the
    // default-view memory does not bounce a just-removed filter back in.
    if (FILTER_KEYS.some((k) => next.has(k))) next.delete("view");
    else next.set("view", "all");
    router.replace(`${pathname}?${next.toString()}`, { scroll: false });
  };

  const mine = myLogin != null && filters.person === myLogin;
  const showAll = () => router.replace(`${pathname}?view=all`, { scroll: false });
  const allOn = !FILTER_KEYS.some((k) => params.has(k));
  const pill = (on: boolean) =>
    `${CONTROL} px-3.5 ${on ? "bg-ink text-surface" : "bg-surface text-ink shadow-[inset_0_0_0_1px_var(--wb-rule)] hover:bg-hover"}`;

  return (
    <div className="flex flex-wrap items-center gap-2" aria-label={t.filters.label}>
      <Picker label={p.status} value={filters.statuses[0] ?? ""} onChange={(v) => set("status", v)}>
        <option value="">{p.status}: {p.any}</option>
        {STATUS_ORDER.map((s: Status) => (
          <option key={s} value={s}>
            {t.status[s]}
          </option>
        ))}
      </Picker>

      <Picker label={p.person} value={filters.person ?? ""} onChange={(v) => set("person", v)}>
        <option value="">{p.person}: {p.any}</option>
        {members.map((m) => (
          <option key={m.githubLogin} value={m.githubLogin}>
            {m.displayName ?? m.githubLogin}
          </option>
        ))}
      </Picker>

      <Picker label={p.project} value={filters.project ?? ""} onChange={(v) => set("project", v)}>
        <option value="">{p.project}: {p.any}</option>
        {projects.map((pr) => (
          <option key={pr.id} value={pr.id}>
            {pr.name}
          </option>
        ))}
      </Picker>

      <Picker label={p.repository} value={filters.repository ?? ""} onChange={(v) => set("repository", v)}>
        <option value="">{p.repository}: {p.any}</option>
        {repositories.map((r) => (
          <option key={r.id} value={r.id}>
            {r.name}
          </option>
        ))}
      </Picker>

      <Picker label={p.label} value={filters.label ?? ""} onChange={(v) => set("label", v)}>
        <option value="">{p.label}: {p.any}</option>
        {labels.map((l) => (
          <option key={l.name} value={l.name}>
            {l.name}
          </option>
        ))}
      </Picker>

      {myLogin != null && (
        <button type="button" aria-pressed={mine} onClick={() => set("person", mine ? null : myLogin)} className={pill(mine)}>
          {p.mine}
        </button>
      )}
      <button type="button" aria-pressed={allOn} onClick={showAll} className={pill(allOn)}>
        {p.showAll}
      </button>
    </div>
  );
}
