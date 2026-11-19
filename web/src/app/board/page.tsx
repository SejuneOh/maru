"use client";

import { useSearchParams } from "next/navigation";
import { ErrorBox, Loading } from "@/components/Feedback";
import { FilterBar } from "@/components/FilterBar";
import { FilterChips } from "@/components/FilterChips";
import { StatusColumns } from "@/components/StatusColumns";
import { PageHeader } from "@/components/ui";
import { useApi } from "@/lib/api";
import { useEditedIssues } from "@/lib/boardEdits";
import { applyFilters, hasFilters, issuesQuery, readFilters } from "@/lib/filters";
import { useT } from "@/lib/i18n";
import { STATUS_ORDER } from "@/lib/labels";
import { useMe } from "@/lib/me";
import type { BoardIssue, Member, Project } from "@/lib/types";

export default function BoardPage() {
  const filters = readFilters(useSearchParams());
  const me = useMe();
  const t = useT();
  const bt = t.board;
  const issues = useApi<BoardIssue[]>(issuesQuery(filters));
  const projects = useApi<Project[]>("/api/projects");
  const members = useApi<Member[]>("/api/members");
  // The full tenant label set, so the picker never narrows to a single active label.
  const labelList = useApi<{ id: string; name: string; color: string | null }[]>("/api/labels");
  const labels = labelList.data ?? [];
  const edited = useEditedIssues(issues.data);
  const filtered = edited ? applyFilters(edited, filters) : null;

  const name = {
    person: (login: string) => members.data?.find((m) => m.githubLogin === login)?.displayName ?? login,
    project: (id: string) => projects.data?.find((pr) => pr.id === id)?.name,
    label: (id: string) => labels.find((l) => l.id === id)?.name,
    sprint: (id: string) => issues.data?.find((i) => i.sprint?.id === id)?.sprint?.name,
  };

  const fact = filtered == null ? bt.meta : hasFilters(filters) ? bt.filtered(filtered.length) : bt.all(filtered.length);

  const header = (
    <PageHeader meta={bt.meta} title={bt.title}>
      {filtered == null ? bt.lead : fact}
    </PageHeader>
  );

  if (issues.error)
    return (
      <div className="flex flex-col gap-4">
        {header}
        <ErrorBox error={issues.error} />
      </div>
    );

  return (
    <div className="flex flex-col gap-4">
      {header}
      <FilterBar
        filters={filters}
        projects={projects.data ?? []}
        members={members.data ?? []}
        labels={labels}
        myLogin={me.data?.member.githubLogin ?? null}
      />
      <FilterChips
        filters={filters}
        personName={name.person}
        projectName={name.project}
        labelName={name.label}
        sprintName={name.sprint}
      />
      {filtered == null ? (
        <Loading />
      ) : filtered.length === 0 ? (
        <p className="rounded-item border border-dashed border-rule-strong px-4 py-3 text-small text-muted">{bt.noMatch}</p>
      ) : (
        <div className="reveal mt-2 [animation-delay:30ms]">
          <StatusColumns
            scope="board"
            issues={filtered}
            statuses={filters.statuses.length ? filters.statuses : STATUS_ORDER}
          />
        </div>
      )}
    </div>
  );
}
