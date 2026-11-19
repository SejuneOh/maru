"use client";

import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { type BoardFilters } from "@/lib/filters";
import { useT } from "@/lib/i18n";
import { STATUS_ORDER } from "@/lib/labels";
import type { Member, Project, Status } from "@/lib/types";

type LabelOption = { id: string; name: string };

type Props = {
  filters: BoardFilters;
  projects: Project[];
  members: Member[];
  labels: LabelOption[];
  /** The signed-in person's GitHub login, for the "mine only" toggle. */
  myLogin?: string | null;
};

const SELECT =
  "h-8 cursor-pointer rounded-control bg-surface px-2 text-small font-medium text-ink shadow-[inset_0_0_0_1px_var(--wb-rule-strong)] transition-colors duration-[120ms] ease-tool hover:bg-hover focus:shadow-[inset_0_0_0_2px_var(--wb-focus)] focus:outline-none";

/** The pickers that set board filters. Each writes its key into the URL; FilterChips shows and clears them. */
export function FilterBar({ filters, projects, members, labels, myLogin }: Props) {
  const t = useT();
  const p = t.filters.pick;
  const router = useRouter();
  const pathname = usePathname();
  const params = useSearchParams();

  const set = (key: string, value: string | null) => {
    const next = new URLSearchParams(params.toString());
    if (value) next.set(key, value);
    else next.delete(key);
    const qs = next.toString();
    router.replace(qs ? `${pathname}?${qs}` : pathname, { scroll: false });
  };

  const mine = myLogin != null && filters.person === myLogin;

  return (
    <div className="flex flex-wrap items-center gap-2" aria-label={t.filters.label}>
      <select className={SELECT} aria-label={p.status} value={filters.statuses[0] ?? ""} onChange={(e) => set("status", e.target.value || null)}>
        <option value="">{p.status}: {p.any}</option>
        {STATUS_ORDER.map((s: Status) => (
          <option key={s} value={s}>
            {t.status[s]}
          </option>
        ))}
      </select>

      <select className={SELECT} aria-label={p.person} value={filters.person ?? ""} onChange={(e) => set("person", e.target.value || null)}>
        <option value="">{p.person}: {p.any}</option>
        {members.map((m) => (
          <option key={m.githubLogin} value={m.githubLogin}>
            {m.displayName ?? m.githubLogin}
          </option>
        ))}
      </select>

      <select className={SELECT} aria-label={p.project} value={filters.project ?? ""} onChange={(e) => set("project", e.target.value || null)}>
        <option value="">{p.project}: {p.any}</option>
        {projects.map((pr) => (
          <option key={pr.id} value={pr.id}>
            {pr.name}
          </option>
        ))}
      </select>

      <select className={SELECT} aria-label={p.label} value={filters.label ?? ""} onChange={(e) => set("label", e.target.value || null)}>
        <option value="">{p.label}: {p.any}</option>
        {labels.map((l) => (
          <option key={l.id} value={l.id}>
            {l.name}
          </option>
        ))}
      </select>

      {myLogin != null && (
        <button
          type="button"
          aria-pressed={mine}
          onClick={() => set("person", mine ? null : myLogin)}
          className={`h-8 cursor-pointer rounded-control px-3 text-small font-medium transition-colors duration-[120ms] ease-tool ${
            mine
              ? "bg-ink text-surface"
              : "bg-surface text-ink shadow-[inset_0_0_0_1px_var(--wb-rule-strong)] hover:bg-hover"
          }`}
        >
          {p.mine}
        </button>
      )}
    </div>
  );
}
