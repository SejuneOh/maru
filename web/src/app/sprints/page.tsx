"use client";

import Link from "next/link";
import { useCallback, useEffect, useId, useRef, useState } from "react";
import { Empty, ErrorBox, Loading } from "@/components/Feedback";
import { SprintForm } from "@/components/SprintForm";
import { Count, Flag, Kbd, PageHeader, StatusBadge } from "@/components/ui";
import { useCanDrag } from "@/components/useBoardDrag";
import { useIssuePanel } from "@/components/useIssuePanel";
import { ISSUE_ATTR, ROW_ATTR, isTyping } from "@/components/useShortcuts";
import { ApiError, ISSUES_CHANGED_EVENT, changeSprintIssues, newIdempotencyKey, useApi } from "@/lib/api";
import { showMessage, useEditedIssues } from "@/lib/boardEdits";
import { type Dict, useFormat, useT } from "@/lib/i18n";
import { useMe, useSettings } from "@/lib/me";
import { SIZES, addDays, dateOnly, daysBetween, daysLeft, seoulToday, sprintTotals } from "@/lib/sprints";
import type { BacklogPage, BoardIssue, Sprint, SprintList, Status } from "@/lib/types";

/** Where an issue is shown while its move is on its way (`saving`) or until the lists have reloaded (`saved`). */
type Move = { to: string | null; phase: "saving" | "saved"; seen?: Lists };
type Lists = { b: unknown; c: unknown; n: unknown };
type Drag = { ids: string[]; from: string | null };
type FormState = { mode: "new"; start: string } | { mode: "edit"; id: string };
type MoveFailure = keyof Dict["plan"]["errors"];

const BACKLOG_PATH = "/api/backlog?pageSize=200";
const RANK: Record<Status, number> = { in_review: 0, in_progress: 1, todo: 2, done: 3, cancelled: 4 };
const BACKLOG_ZONE = "backlog";
const G_WINDOW_MS = 1200;

const byStatus = (a: BoardIssue, b: BoardIssue) => RANK[a.status] - RANK[b.status] || Date.parse(b.updatedAt) - Date.parse(a.updatedAt);
const sprintPath = (s: Sprint | null) => (s ? `/api/issues?sprint=${encodeURIComponent(s.id)}&scope=all` : null);

/** The backlog row that takes keyboard focus when `id` leaves the list. */
function rowNear(id: string): string | null {
  const li = document.querySelector<HTMLElement>(`[data-plan="backlog"][data-plan-id="${CSS.escape(id)}"]`);
  const sibling = (li?.nextElementSibling ?? li?.previousElementSibling) as HTMLElement | null;
  return sibling?.dataset.planId ?? null;
}

function moveFailure(e: unknown): MoveFailure {
  if (!(e instanceof ApiError)) return "network";
  switch (e.code) {
    case "sprint_ended":
    case "sprints_disabled":
    case "not_found":
    case "invalid_request":
      return e.code;
  }
  return "unknown";
}

export default function SprintPlanningPage() {
  const t = useT();
  const p = t.plan;
  const me = useMe();
  const settings = useSettings();
  const [disabled, setDisabled] = useState(false);
  const on = settings.sprintsEnabled && !disabled;

  const header = (fact: React.ReactNode) => (
    <PageHeader meta={p.meta} title={p.title}>
      {fact}
    </PageHeader>
  );

  if (!on)
    return (
      <div className="flex flex-col gap-4">
        {header(null)}
        <Empty>
          {p.off}{" "}
          <Link href="/settings" className="link">
            {p.toSettings}
          </Link>
        </Empty>
      </div>
    );

  return (
    <Planner
      header={header}
      onDisabled={() => {
        setDisabled(true);
        me.reload();
      }}
    />
  );
}

function Planner({ header, onDisabled }: { header: (fact: React.ReactNode) => React.ReactNode; onDisabled: () => void }) {
  const t = useT();
  const p = t.plan;
  const settings = useSettings();
  const canDrag = useCanDrag();

  const sprints = useApi<SprintList>("/api/sprints");
  const [nextId, setNextId] = useState<string | null>(null);
  const current = sprints.data?.current[0] ?? null;
  const upcoming = sprints.data?.upcoming ?? [];
  const next = upcoming.find((s) => s.id === nextId) ?? upcoming[0] ?? null;

  const backlog = useApi<BacklogPage>(BACKLOG_PATH);
  const curList = useApi<BoardIssue[]>(sprintPath(current));
  const nextList = useApi<BoardIssue[]>(sprintPath(next));
  const bItems = useEditedIssues(backlog.data?.items ?? null);
  const cItems = useEditedIssues(current ? curList.data : null);
  const nItems = useEditedIssues(next ? nextList.data : null);

  const [moves, setMoves] = useState<Record<string, Move>>({});
  const [selected, setSelected] = useState<string[]>([]);
  const [form, setForm] = useState<FormState | null>(null);
  const [drag, setDrag] = useState<Drag | null>(null);
  const [over, setOver] = useState<string | null>(null);
  const formOpener = useRef<HTMLElement | null>(null);
  const newButton = useRef<HTMLButtonElement>(null);
  const focusAfter = useRef<string | null>(null);

  const lists: Lists = { b: backlog.data, c: curList.data, n: nextList.data };
  const latest = useRef(lists);
  latest.current = lists;
  const anyLoading = backlog.loading || curList.loading || nextList.loading || sprints.loading;

  // A finished move stays laid over the lists until every list on screen has been fetched again.
  useEffect(() => {
    if (anyLoading) return;
    setMoves((prev) => {
      const keep = Object.entries(prev).filter(
        ([, m]) =>
          m.phase === "saving" ||
          !m.seen ||
          (m.seen.b !== null && m.seen.b === backlog.data) ||
          (m.seen.c !== null && m.seen.c === curList.data) ||
          (m.seen.n !== null && m.seen.n === nextList.data),
      );
      return keep.length === Object.keys(prev).length ? prev : Object.fromEntries(keep);
    });
  }, [anyLoading, backlog.data, curList.data, nextList.data]);

  const pool = new Map<string, BoardIssue>();
  for (const list of [cItems, nItems, bItems]) for (const i of list ?? []) if (!pool.has(i.id)) pool.set(i.id, i);
  const where = (i: BoardIssue) => (i.id in moves ? moves[i.id].to : (i.sprint?.id ?? null));
  const all = [...pool.values()];
  const backlogRows = all
    .filter((i) => where(i) === null && i.state === "open" && i.status !== "done" && i.status !== "cancelled")
    .sort((a, b) => Date.parse(b.updatedAt) - Date.parse(a.updatedAt));
  const rowsOf = (s: Sprint | null) => (s ? all.filter((i) => where(i) === s.id).sort(byStatus) : []);
  const curRows = rowsOf(current);
  const nextRows = rowsOf(next);
  const picked = selected.filter((id) => backlogRows.some((i) => i.id === id));

  const send = useCallback(
    (ids: string[], to: Sprint | null, from: string | null, key: string) => {
      const sprintId = to?.id ?? from;
      if (!sprintId || ids.length === 0) return;
      setMoves((m) => ({ ...m, ...Object.fromEntries(ids.map((id) => [id, { to: to?.id ?? null, phase: "saving" } satisfies Move])) }));
      setSelected((s) => s.filter((id) => !ids.includes(id)));
      changeSprintIssues(sprintId, to ? "POST" : "DELETE", ids, key).then(
        () => {
          const seen = latest.current;
          setMoves((m) => ({ ...m, ...Object.fromEntries(ids.map((id) => [id, { to: to?.id ?? null, phase: "saved", seen } satisfies Move])) }));
          showMessage({ title: to ? p.added(ids.length, to.name) : p.removed(ids.length) });
          window.dispatchEvent(new Event(ISSUES_CHANGED_EVENT));
        },
        (e: unknown) => {
          setMoves((m) => Object.fromEntries(Object.entries(m).filter(([id]) => !ids.includes(id))));
          const failure = moveFailure(e);
          if (failure === "sprints_disabled") {
            onDisabled();
            return;
          }
          // Sprint writes store only successes under the key, so a resend under the same key is safe.
          const retry = failure === "network" || failure === "unknown" ? () => send(ids, to, from, key) : null;
          showMessage({ title: p.failedAdd(ids.length), detail: p.errors[failure], failed: true, retry });
          if (failure !== "invalid_request") window.dispatchEvent(new Event(ISSUES_CHANGED_EVENT));
        },
      );
    },
    [p, onDisabled],
  );

  const movesRef = useRef(moves);
  movesRef.current = moves;
  const moveIssues = useCallback(
    (ids: string[], to: Sprint | null, from: string | null) => {
      if (ids.some((id) => movesRef.current[id]?.phase === "saving")) {
        showMessage({ title: p.busy });
        return;
      }
      send(ids, to, from, newIdempotencyKey());
    },
    [p, send],
  );

  const toggle = useCallback((id: string) => setSelected((s) => (s.includes(id) ? s.filter((x) => x !== id) : [...s, id])), []);

  useEffect(() => {
    const id = focusAfter.current;
    if (!id) return;
    focusAfter.current = null;
    document.querySelector<HTMLElement>(`[data-plan-id="${CSS.escape(id)}"] [${ROW_ATTR}]`)?.focus();
  });

  const target = current ?? next;
  const keys = useRef({ picked, target, formOpen: form !== null });
  keys.current = { picked, target, formOpen: form !== null };

  useEffect(() => {
    let lastG = -Infinity;
    const focusedBacklogId = () =>
      document.activeElement instanceof HTMLElement
        ? (document.activeElement.closest<HTMLElement>('[data-plan="backlog"]')?.dataset.planId ?? null)
        : null;
    const onKey = (e: KeyboardEvent) => {
      if (e.defaultPrevented || e.metaKey || e.ctrlKey || e.altKey || e.shiftKey || e.isComposing || e.keyCode === 229 || isTyping(e.target)) return;
      if (document.querySelector('[role="dialog"]')) return;
      if (e.code === "KeyG") {
        lastG = e.timeStamp;
        return;
      }
      if (e.timeStamp - lastG < G_WINDOW_MS) return;
      const k = keys.current;
      if (e.code === "KeyX") {
        const id = focusedBacklogId();
        if (!id) return;
        e.preventDefault();
        toggle(id);
      } else if (e.code === "KeyA") {
        const focused = focusedBacklogId();
        const ids = k.picked.length ? k.picked : focused ? [focused] : [];
        if (ids.length === 0) return;
        e.preventDefault();
        if (!k.target) {
          showMessage({ title: p.noTarget, failed: true });
          return;
        }
        if (focused && ids.includes(focused)) focusAfter.current = rowNear(focused);
        moveIssues(ids, k.target, null);
      } else if (e.key === "Escape" && k.picked.length && !k.formOpen) {
        setSelected([]);
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [p, toggle, moveIssues]);

  const openForm = (state: FormState) => {
    formOpener.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    setForm(state);
  };
  const closeForm = (saved: Sprint | null) => {
    setForm(null);
    if (saved?.state === "planned") setNextId(saved.id);
    // The opener can be gone (an empty column's create button once its sprint exists).
    requestAnimationFrame(() => (formOpener.current?.isConnected ? formOpener.current : newButton.current)?.focus());
  };
  const newStart = () => {
    const ends = [current, ...upcoming].filter((s): s is Sprint => s !== null).map((s) => s.endDate);
    const today = seoulToday();
    const last = ends.sort().at(-1);
    return last && last >= today ? addDays(last, 1) : today;
  };

  const dropZone = (zone: string, accepts: boolean, onDrop: (d: Drag) => void) => {
    if (!drag) return { state: undefined, props: {} };
    if (!accepts) return { state: "off" as const, props: {} };
    return {
      state: over === zone ? ("over" as const) : ("can" as const),
      props: {
        onDragEnter: (e: React.DragEvent) => {
          e.preventDefault();
          setOver(zone);
        },
        onDragOver: (e: React.DragEvent) => {
          e.preventDefault();
          e.dataTransfer.dropEffect = "move";
        },
        onDragLeave: (e: React.DragEvent) => {
          if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setOver((o) => (o === zone ? null : o));
        },
        onDrop: (e: React.DragEvent) => {
          e.preventDefault();
          setOver(null);
          setDrag(null);
          onDrop(drag);
        },
      },
    };
  };
  const rowDrag = (issue: BoardIssue, from: string | null) => {
    if (!canDrag || moves[issue.id]?.phase === "saving") return { draggable: false };
    return {
      draggable: true,
      onDragStart: (e: React.DragEvent) => {
        const ids = from === null && picked.includes(issue.id) ? picked : [issue.id];
        e.dataTransfer.effectAllowed = "move";
        e.dataTransfer.setData("text/plain", ids.join(","));
        setDrag({ ids, from });
      },
      onDragEnd: () => {
        setDrag(null);
        setOver(null);
      },
    };
  };

  const error = sprints.error ?? backlog.error;
  if (error)
    return (
      <div className="flex flex-col gap-4">
        {header(null)}
        <ErrorBox error={error} />
      </div>
    );
  if (!sprints.data || !bItems)
    return (
      <div className="flex flex-col gap-4">
        {header(null)}
        <Loading />
      </div>
    );

  const backlogCount = backlog.data && backlog.data.total > backlog.data.items.length ? backlog.data.total : backlogRows.length;
  const today = seoulToday();
  let fact: string;
  if (current) fact = p.current(current.name, sprintTotals(curRows).count, daysLeft(current, today), backlogCount);
  else if (next) fact = p.nextOnly(next.name, Math.max(0, daysBetween(today, next.startDate)), backlogCount);
  else fact = p.noSprints;

  const editing = form?.mode === "edit" ? ([current, ...upcoming].find((s) => s?.id === form.id) ?? null) : null;
  const backlogDrop = dropZone(BACKLOG_ZONE, drag !== null && drag.from !== null, (d) => moveIssues(d.ids, null, d.from));
  const sprintDrop = (s: Sprint | null) =>
    dropZone(s?.id ?? "none", drag !== null && s !== null && s.state !== "closed" && drag.from !== s.id, (d) => s && moveIssues(d.ids, s, d.from));

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-end justify-between gap-x-6 gap-y-3">
        {header(fact)}
        <button
          ref={newButton}
          type="button"
          aria-expanded={form?.mode === "new"}
          className="btn h-9 gap-1.5 px-3 text-small"
          onClick={() => form?.mode !== "new" && openForm({ mode: "new", start: newStart() })}
        >
          <svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.75" aria-hidden>
            <path d="M8 3v10M3 8h10" strokeLinecap="round" />
          </svg>
          {p.newSprint}
        </button>
      </div>

      {form && (
        <SprintForm
          key={form.mode === "edit" ? form.id : "new"}
          editing={editing}
          initial={
            form.mode === "edit" && editing
              ? { name: editing.name, startDate: editing.startDate, endDate: editing.endDate }
              : { name: "", startDate: form.mode === "new" ? form.start : today, endDate: addDays(form.mode === "new" ? form.start : today, 13) }
          }
          onDone={closeForm}
          onDisabled={onDisabled}
        />
      )}

      <div className="@container reveal mt-2 [animation-delay:30ms]">
        <div className="grid grid-cols-1 items-start gap-6 @[40rem]:grid-cols-2 @[56rem]:grid-cols-3">
          <section
            aria-labelledby="plan-backlog"
            data-drop={backlogDrop.state}
            className="card drop-col flex min-w-0 flex-col pb-2"
            {...backlogDrop.props}
          >
            <div className="flex min-h-[60px] flex-wrap items-center gap-x-3 gap-y-1 px-5 pb-2 pt-4">
              <h2 id="plan-backlog" className="whitespace-nowrap text-heading font-semibold">
                {p.backlog}
              </h2>
              <Count n={backlogRows.length} />
              <span className="min-w-0 truncate text-small text-muted">{p.backlogHint}</span>
            </div>
            <BulkBar
              picked={picked.length}
              current={current}
              next={next}
              primary={form === null}
              onAdd={(s) => moveIssues(picked, s, null)}
              onClear={() => setSelected([])}
            />
            {backlogRows.length === 0 ? (
              <div className="px-5 py-2">
                <Empty>{p.backlogEmpty}</Empty>
              </div>
            ) : (
              <ul aria-label={p.backlog}>
                {backlogRows.map((i) => (
                  <PlanRow
                    key={i.id}
                    kind="backlog"
                    issue={i}
                    saving={moves[i.id]?.phase === "saving"}
                    lifted={drag?.ids.includes(i.id) ?? false}
                    selected={picked.includes(i.id)}
                    onToggle={() => toggle(i.id)}
                    drag={rowDrag(i, null)}
                  />
                ))}
              </ul>
            )}
            {backlog.data && backlog.data.total > backlog.data.items.length && (
              <p className="px-5 pb-2 pt-3 text-small text-muted">{p.backlogCapped(backlog.data.items.length, backlog.data.total)}</p>
            )}
          </section>

          <div className="flex min-w-0 flex-col gap-6 @[56rem]:contents">
            <SprintColumn
              label={p.currentCol}
              sprint={current}
              rows={curRows}
              loading={current !== null && !cItems}
              moves={moves}
              drop={sprintDrop(current)}
              lifted={drag?.ids ?? []}
              rowDrag={rowDrag}
              onRemove={(i) => current && moveIssues([i.id], null, current.id)}
              onEdit={(s) => openForm({ mode: "edit", id: s.id })}
              onCreate={() => openForm({ mode: "new", start: today })}
              emptyText={p.noCurrent}
              createText={p.makeCurrent}
              sizes={settings.sizeEnabled}
            />
            <SprintColumn
              label={p.nextCol}
              sprint={next}
              rows={nextRows}
              loading={next !== null && !nItems}
              moves={moves}
              drop={sprintDrop(next)}
              lifted={drag?.ids ?? []}
              rowDrag={rowDrag}
              onRemove={(i) => next && moveIssues([i.id], null, next.id)}
              onEdit={(s) => openForm({ mode: "edit", id: s.id })}
              onCreate={() => openForm({ mode: "new", start: newStart() })}
              emptyText={p.noNext}
              createText={p.makeNext}
              sizes={settings.sizeEnabled}
              choices={upcoming.length > 1 ? { list: upcoming, pick: setNextId } : undefined}
            />
          </div>
        </div>
      </div>
    </div>
  );
}

function BulkBar({
  picked,
  current,
  next,
  primary,
  onAdd,
  onClear,
}: {
  picked: number;
  current: Sprint | null;
  next: Sprint | null;
  primary: boolean;
  onAdd: (s: Sprint) => void;
  onClear: () => void;
}) {
  const t = useT();
  const p = t.plan;
  if (picked === 0)
    return (
      <p className="mx-5 mb-2 flex flex-wrap items-center gap-x-2 gap-y-1 text-meta text-muted max-[639px]:hidden">
        <Kbd>x</Kbd>
        <Kbd>a</Kbd>
        <span>{p.keys}</span>
        <span className="sr-only">{p.addHint}</span>
      </p>
    );
  const first = current ?? next;
  return (
    <div role="group" aria-label={p.selected(picked)} className="mx-5 mb-2 flex flex-wrap items-center gap-2 rounded-control bg-sunken px-3 py-2">
      <span className="mr-auto text-small font-semibold text-ink">{p.selected(picked)}</span>
      {first && (
        <button
          type="button"
          aria-keyshortcuts="a"
          onClick={() => onAdd(first)}
          className={`btn h-8 gap-2 px-3 text-small ${primary ? "btn-primary" : ""}`}
        >
          {p.addTo(first === current ? p.currentCol : p.nextCol)}
          <span aria-hidden className={`font-mono text-meta ${primary ? "opacity-70" : "text-muted"} max-[639px]:hidden`}>
            a
          </span>
        </button>
      )}
      {current && next && (
        <button type="button" onClick={() => onAdd(next)} className="btn h-8 px-3 text-small">
          {p.addTo(p.nextCol)}
        </button>
      )}
      <button type="button" onClick={onClear} className="link cursor-pointer px-1 text-small text-ink-soft">
        {p.clear}
      </button>
    </div>
  );
}

type DropZone = { state: "can" | "over" | "off" | undefined; props: Record<string, unknown> };
type RowDrag = (issue: BoardIssue, from: string | null) => Record<string, unknown>;

function SprintColumn({
  label,
  sprint,
  rows,
  loading,
  moves,
  drop,
  lifted,
  rowDrag,
  onRemove,
  onEdit,
  onCreate,
  emptyText,
  createText,
  sizes,
  choices,
}: {
  label: string;
  sprint: Sprint | null;
  rows: BoardIssue[];
  loading: boolean;
  moves: Record<string, Move>;
  drop: DropZone;
  lifted: string[];
  rowDrag: RowDrag;
  onRemove: (i: BoardIssue) => void;
  onEdit: (s: Sprint) => void;
  onCreate: () => void;
  emptyText: string;
  createText: string;
  sizes: boolean;
  choices?: { list: Sprint[]; pick: (id: string) => void };
}) {
  const t = useT();
  const p = t.plan;
  const fmt = useFormat();
  const id = useId();

  if (!sprint)
    return (
      <section aria-label={label} className="card flex min-w-0 flex-col gap-3 px-5 py-4">
        <p className="text-small text-muted">{label}</p>
        <p className="text-ui text-ink-soft">{emptyText}</p>
        <button type="button" className="btn h-9 self-start px-3 text-small" onClick={onCreate}>
          {createText}
        </button>
      </section>
    );

  const totals = sprintTotals(rows);
  const today = seoulToday();
  const timing = sprint.state === "planned" ? p.startsIn(Math.max(0, daysBetween(today, sprint.startDate))) : p.daysLeft(daysLeft(sprint, today));
  const sizeLine = SIZES.filter((s) => totals.sizes[s] > 0).map((s) => `${s} ${totals.sizes[s]}`);
  const summary = [p.count(totals.count), ...(sizes && sizeLine.length ? [`${p.size} ${sizeLine.join(" · ")}`] : []), timing];

  return (
    <section aria-labelledby={`${id}-name`} data-drop={drop.state} className="card drop-col flex min-w-0 flex-col pb-2" {...drop.props}>
      <div className="flex flex-col gap-1 px-5 pb-3 pt-4">
        <div className="flex min-h-7 items-center gap-2">
          {choices ? (
            <select
              aria-label={p.pickNext}
              value={sprint.id}
              onChange={(e) => choices.pick(e.target.value)}
              className="-ml-1 h-7 min-w-0 cursor-pointer rounded-control bg-transparent px-1 text-small text-muted hover:bg-hover"
            >
              {choices.list.map((s) => (
                <option key={s.id} value={s.id}>
                  {label} · {s.name}
                </option>
              ))}
            </select>
          ) : (
            <span className="text-small text-muted">{label}</span>
          )}
          <button
            type="button"
            onClick={() => onEdit(sprint)}
            aria-label={p.editSprint(sprint.name)}
            className="ml-auto h-7 shrink-0 cursor-pointer rounded-control px-2 text-small text-ink-soft hover:bg-hover hover:text-ink"
          >
            {p.edit}
          </button>
        </div>
        <h2 id={`${id}-name`} className="truncate text-heading font-semibold">
          {sprint.name}
        </h2>
        <p className="text-small tabular-nums text-muted">
          {fmt.shortDate(dateOnly(sprint.startDate))} – {fmt.shortDate(dateOnly(sprint.endDate))}
        </p>
        <p className="text-small tabular-nums text-ink-soft">{summary.join(" · ")}</p>
      </div>
      {loading ? (
        <Loading />
      ) : rows.length === 0 ? (
        <div className="px-5 py-2">
          <Empty>{p.sprintEmpty}</Empty>
        </div>
      ) : (
        <ul aria-label={sprint.name}>
          {rows.map((i) => (
            <PlanRow
              key={i.id}
              kind="sprint"
              issue={i}
              saving={moves[i.id]?.phase === "saving"}
              lifted={lifted.includes(i.id)}
              onRemove={i.state === "open" ? () => onRemove(i) : undefined}
              drag={rowDrag(i, sprint.id)}
            />
          ))}
        </ul>
      )}
    </section>
  );
}

function PlanRow({
  kind,
  issue,
  saving,
  lifted,
  selected = false,
  onToggle,
  onRemove,
  drag,
}: {
  kind: "backlog" | "sprint";
  issue: BoardIssue;
  saving: boolean;
  lifted: boolean;
  selected?: boolean;
  onToggle?: () => void;
  onRemove?: () => void;
  drag: Record<string, unknown>;
}) {
  const t = useT();
  const p = t.plan;
  const fmt = useFormat();
  const settings = useSettings();
  const { open } = useIssuePanel();
  const ref = `${issue.repository.name} #${issue.number}`;
  const done = issue.category === "done";
  const urgent = settings.priorityEnabled && issue.priority === "urgent" && !done;
  const carried = issue.carriedFrom && !done ? issue.carriedFrom : null;
  const unassigned = issue.state === "open" && issue.assignees.length === 0;

  return (
    <li
      data-plan={kind}
      data-plan-id={issue.id}
      className={`relative flex items-start gap-2 px-3 py-1 before:absolute before:inset-x-5 before:top-0 before:h-px before:bg-rule first:before:hidden ${
        lifted ? "card-lifted" : ""
      }`}
      {...drag}
    >
      {onToggle && (
        <button
          type="button"
          role="checkbox"
          aria-checked={selected}
          aria-label={p.select(ref)}
          aria-keyshortcuts="x"
          onClick={onToggle}
          className="mt-2.5 grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-control hover:bg-hover"
        >
          <span aria-hidden data-on={selected || undefined} className="pick-box">
            {selected && (
              <svg width="10" height="10" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2.5">
                <path d="m3.5 8.5 3 3 6-7" strokeLinecap="round" strokeLinejoin="round" />
              </svg>
            )}
          </span>
        </button>
      )}
      <button
        type="button"
        {...{ [ROW_ATTR]: "", [ISSUE_ATTR]: issue.id }}
        aria-busy={saving || undefined}
        onClick={() => open(issue.id)}
        className={`block min-w-0 flex-1 cursor-pointer rounded-control px-2 py-2 text-left transition-colors duration-[120ms] ease-tool hover:bg-hover ${
          selected ? "bg-hover" : ""
        } ${onToggle ? "" : "ml-2"}`}
      >
        <span className="flex min-h-[22px] items-center gap-2 text-meta text-muted">
          <span className="truncate">
            <span className="font-mono">{ref}</span>
            {issue.issueType && ` · ${fmt.issueType(issue.issueType)}`}
          </span>
          <span className="ml-auto flex shrink-0 items-center gap-1.5">
            {carried && (
              <span title={p.carriedFrom(carried.name)}>
                <Flag>{p.carried}</Flag>
                <span className="sr-only">{p.carriedFrom(carried.name)}</span>
              </span>
            )}
            {urgent && <Flag fill>{t.priority.urgent}</Flag>}
            {settings.sizeEnabled && issue.size && (
              <span className="font-mono font-medium text-ink-soft" title={`${p.size} ${t.size[issue.size]}`}>
                {issue.size}
              </span>
            )}
          </span>
        </span>
        <span className={`mt-1 line-clamp-2 block text-ui font-medium ${done ? "text-muted" : "text-ink"} ${issue.status === "cancelled" ? "line-through" : ""}`}>
          {issue.title}
        </span>
        <span className="mt-1.5 flex items-center justify-between gap-2 text-small text-muted">
          <span className="min-w-0 truncate">
            {unassigned ? (
              <span className="inline-flex items-center gap-1.5 text-accent">
                <span aria-hidden className="inline-block h-3 w-3 rounded-full border border-dashed border-accent" />
                {t.common.unassigned}
              </span>
            ) : (
              issue.assignees.map(fmt.person).join(", ")
            )}
          </span>
          {saving ? (
            <span role="status" className="inline-flex shrink-0 items-center gap-1.5 text-ink-soft">
              <span aria-hidden className="busy-dot" />
              {p.saving}…
            </span>
          ) : (
            <span className="shrink-0">
              <StatusBadge status={issue.status} />
            </span>
          )}
        </span>
      </button>
      {onRemove && (
        <button
          type="button"
          onClick={onRemove}
          disabled={saving}
          aria-label={p.removeRef(ref)}
          title={p.removeTo}
          className="mt-2.5 grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-control text-muted hover:bg-hover hover:text-ink disabled:cursor-default disabled:opacity-50"
        >
          <svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" aria-hidden>
            <path d="M6 4 2.5 7.5 6 11M3 7.5h7a3.5 3.5 0 0 1 0 7H8" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
        </button>
      )}
    </li>
  );
}
