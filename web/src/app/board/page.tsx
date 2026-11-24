"use client";

import { useEffect } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { BoardSkeleton, ErrorBox } from "@/components/Feedback";
import { FilterBar } from "@/components/FilterBar";
import { FilterChips } from "@/components/FilterChips";
import { FilterPresets } from "@/components/FilterPresets";
import { StatusColumns } from "@/components/StatusColumns";
import { PageHeader } from "@/components/ui";
import { useApi } from "@/lib/api";
import { useEditedIssues } from "@/lib/boardEdits";
import { FILTER_KEYS, NO_FILTERS, applyFilters, hasFilters, issuesQuery, readFilters } from "@/lib/filters";
import { useT } from "@/lib/i18n";
import { STATUS_ORDER } from "@/lib/labels";
import { useMe } from "@/lib/me";
import type { BoardIssue, Member, Project, SprintList } from "@/lib/types";

const VIEW_KEY = "maru:boardView";

export default function BoardPage() {
  const params = useSearchParams();
  const router = useRouter();
  const filters = readFilters(params);
  const me = useMe();
  const t = useT();
  const bt = t.board;
  const myLogin = me.data?.member.githubLogin ?? null;

  // Default and remember: a bare /board (nav click) restores the last view, or opens "mine" the first time.
  // "Show all" and "clear" go to ?view=all, never a bare URL, so clearing never bounces back to a stored view.
  const qs = params.toString();
  useEffect(() => {
    if (qs === "") {
      // Read the memory on its own, so a blocked storage still falls through to the default instead of
      // leaving the board stuck on the loading state.
      let stored: string | null = null;
      try {
        stored = localStorage.getItem(VIEW_KEY);
      } catch {
        /* storage unavailable */
      }
      if (stored) router.replace(`/board?${stored}`);
      else if (myLogin) router.replace(`/board?person=${encodeURIComponent(myLogin)}`);
      return;
    }
    // Remember only the filter and view keys, never the panel params (issue, newStatus...), so returning
    // to the board does not reopen an old panel.
    const keep = new URLSearchParams();
    for (const k of FILTER_KEYS) {
      const v = params.get(k);
      if (v) keep.set(k, v);
    }
    if (params.get("view")) keep.set("view", params.get("view")!);
    const v = keep.toString();
    if (v) {
      try {
        localStorage.setItem(VIEW_KEY, v);
      } catch {
        /* storage unavailable; the board still works without the memory */
      }
    }
  }, [qs, myLogin, router, params]);

  // A bare URL is on its way to a default via the effect above; don't flash the full unfiltered board.
  const resolving = qs === "";
  // Fetch a stable working set and filter it on the client, so status/person/label/project/repository
  // changes apply instantly with no server round trip. Only doneThisWeek narrows the set server-side.
  const issues = useApi<BoardIssue[]>(resolving ? null : issuesQuery({ ...NO_FILTERS, doneThisWeek: filters.doneThisWeek }));
  const projects = useApi<Project[]>("/api/projects");
  const sprints = useApi<SprintList>("/api/sprints");
  const members = useApi<Member[]>("/api/members");
  // Distinct label names and the tenant's repositories, so the pickers never narrow to the current filter.
  const labelList = useApi<{ name: string; color: string | null }[]>("/api/labels");
  const labels = labelList.data ?? [];
  const repoList = useApi<{ id: string; name: string }[]>("/api/repositories");
  const repositories = repoList.data ?? [];
  const edited = useEditedIssues(issues.data);
  const filtered = edited ? applyFilters(edited, filters) : null;

  const name = {
    person: (login: string) => members.data?.find((m) => m.githubLogin === login)?.displayName ?? login,
    project: (id: string) => projects.data?.find((pr) => pr.id === id)?.name,
    // The label filter is keyed by name, so the value is already the label text.
    label: (labelName: string) => labelName,
    repository: (id: string) => repositories.find((r) => r.id === id)?.name,
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
      <FilterPresets
        filters={filters}
        myLogin={myLogin}
        activeSprintId={sprints.data?.current[0]?.id ?? null}
      />
      <FilterBar
        filters={filters}
        projects={projects.data ?? []}
        members={members.data ?? []}
        labels={labels}
        repositories={repositories}
      />
      <FilterChips
        filters={filters}
        personName={name.person}
        projectName={name.project}
        labelName={name.label}
        repositoryName={name.repository}
        sprintName={name.sprint}
      />
      {filtered == null ? (
        <div className="mt-2">
          <BoardSkeleton />
        </div>
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
