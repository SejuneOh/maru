"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useCallback, useEffect, useId, useRef, useState } from "react";
import { ApiError, apiGet, deleteMeetingNote, newIdempotencyKey, saveMeetingNote, useApi } from "@/lib/api";
import { showMessage } from "@/lib/boardEdits";
import { useFormat, useT } from "@/lib/i18n";
import { setLeaveGuard } from "@/lib/leaveGuard";
import { useMe } from "@/lib/me";
import {
  type DraftOwner,
  type NoteDraft,
  clearNoteDraft,
  latestNewDraft,
  newUuid,
  readNoteDraft,
  writeNoteDraft,
} from "@/lib/noteDrafts";
import {
  type Blocked,
  type ItemRow,
  type NoteForm,
  contentOf,
  emptyForm,
  formOf,
  isBlank,
  isHeld,
  localDate,
  onDate,
  rebaseOnto,
  requestOf,
  rowKey,
  snapshotOf,
} from "@/lib/noteForm";
import type { MeetingNote, Member, Project } from "@/lib/types";
import { Avatar, Card, PageHeader, StatusBadge } from "./ui";

const SAVE_AFTER_MS = 1200;
/** Browsers refuse a keepalive request body over 64 KB. */
const KEEPALIVE_MAX = 60_000;

type Failure = "network" | "invalid_request" | "not_found" | "request_in_progress" | "unknown";
type Phase = "idle" | "saving" | "failed" | "conflict";

export function canDelete(note: { createdBy: Member | null }, me: Member | null | undefined): boolean {
  return !!me && (me.role === "admin" || note.createdBy?.id === me.id);
}

/** Confirms, deletes, and says so in the notice slot. Resolves true when the note is gone. */
export async function confirmAndDelete(
  note: { id: string; title: string; actionItems: { issue: unknown }[] },
  nt: ReturnType<typeof useT>["notes"],
  owner: DraftOwner | null,
): Promise<boolean> {
  const linked = note.actionItems.filter((a) => a.issue).length;
  if (!window.confirm(nt.deleteConfirm(note.title, linked))) return false;
  try {
    await deleteMeetingNote(note.id);
    if (owner) clearNoteDraft(owner, note.id);
    showMessage({ title: nt.deleted(note.title) });
    return true;
  } catch (e) {
    const code = e instanceof ApiError ? e.code : null;
    if (code === "not_found") {
      if (owner) clearNoteDraft(owner, note.id);
      return true;
    }
    showMessage({
      title: nt.deleteFailed,
      detail: code === "forbidden" ? nt.deleteForbidden : nt.deleteRetry,
      failed: true,
    });
    return false;
  }
}

/**
 * Writes a meeting note and saves it as the person types: 1.2 s after the last change, when focus leaves a
 * field, and when the tab is hidden. One save runs at a time. A new note carries an id made here, so resending
 * its POST after a lost answer can never create a second note. Edits send the `updatedAt` they last saw; a 409
 * stops saving and shows the newer copy beside the person's own text until they choose (unless the newer copy
 * is this editor's own save whose answer was lost). Unsaved text is kept in localStorage for this member, and
 * leaving the screen with unsaved text asks first.
 */
export function NoteEditor({
  initial,
  owner,
  onCreated,
}: {
  initial: MeetingNote | null;
  owner: DraftOwner;
  onCreated: (note: MeetingNote) => void;
}) {
  const t = useT();
  const nt = t.notes;
  const e = nt.editor;
  const fmt = useFormat();
  const router = useRouter();
  const ids = useId();
  const me = useMe().data?.member;
  const members = useApi<Member[]>("/api/members");
  const projects = useApi<Project[]>("/api/projects");

  const [form, setFormState] = useState<NoteForm>(() => (initial ? formOf(initial) : emptyForm()));
  const formRef = useRef(form);
  const [noteId, setNoteId] = useState(() => initial?.id ?? newUuid());
  const noteIdRef = useRef(noteId);
  const [created, setCreated] = useState(initial !== null);
  const createdRef = useRef(created);
  /** The `updatedAt` of the copy the form builds on. */
  const base = useRef<string | null>(initial?.updatedAt ?? null);
  const [saved, setSaved] = useState(() => snapshotOf(formRef.current));
  const savedRef = useRef(saved);
  const [savedAt, setSavedAt] = useState<string | null>(initial?.updatedAt ?? null);
  const [createdBy, setCreatedBy] = useState<Member | null>(initial?.createdBy ?? null);
  const [phase, setPhaseState] = useState<Phase>("idle");
  const phaseRef = useRef<Phase>("idle");
  const [failure, setFailure] = useState<Failure | null>(null);
  const failureRef = useRef<Failure | null>(null);
  const [blocked, setBlocked] = useState<Blocked | null>(null);
  const [restored, setRestored] = useState(false);
  const [latest, setLatest] = useState<MeetingNote | null>(null);
  const [latestFailed, setLatestFailed] = useState(false);
  const [draft, setDraft] = useState<NoteDraft<NoteForm> | null>(null);
  const [focusKey, setFocusKey] = useState<string | null>(null);
  const [, tick] = useState(0);

  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const inFlight = useRef(false);
  const again = useRef(false);
  const mounted = useRef(true);
  /** One key per request body; a retry after a dropped connection resends under the same key. */
  const attempt = useRef<{ json: string; key: string } | null>(null);
  /** An edit whose answer was lost: it may have been saved, so a 409 that holds exactly it is our own save. */
  const uncertain = useRef<{ content: string; keys: string[] } | null>(null);
  const leaving = useRef<string | null>(null);
  const titleRef = useRef<HTMLInputElement>(null);

  const setPhase = (p: Phase) => {
    phaseRef.current = p;
    setPhaseState(p);
  };
  const setFail = (f: Failure | null) => {
    failureRef.current = f;
    setFailure(f);
  };
  const markSaved = (snapshot: string) => {
    savedRef.current = snapshot;
    setSaved(snapshot);
  };
  const replaceForm = (next: NoteForm) => {
    formRef.current = next;
    setFormState(next);
  };
  const keepDraft = () => {
    if (snapshotOf(formRef.current) === savedRef.current) clearNoteDraft(owner, noteIdRef.current);
    else
      writeNoteDraft(owner, {
        noteId: noteIdRef.current,
        form: formRef.current,
        base: base.current,
        created: createdRef.current,
      });
  };

  const unsaved = snapshotOf(form) !== saved || phase === "saving";
  const unsavedRef = useRef(unsaved);
  unsavedRef.current = unsaved;

  const loadLatest = useCallback(async (id: string) => {
    setLatest(null);
    setLatestFailed(false);
    try {
      setLatest(await apiGet<MeetingNote>(`/api/meeting-notes/${encodeURIComponent(id)}`));
    } catch {
      setLatestFailed(true);
    }
  }, []);

  const flush = useCallback(async (opts: { keepalive?: boolean } = {}): Promise<void> => {
    if (timer.current) {
      clearTimeout(timer.current);
      timer.current = null;
    }
    if (inFlight.current) {
      again.current = true;
      return;
    }
    if (phaseRef.current === "conflict" || failureRef.current === "not_found") return;
    const sent = formRef.current;
    const built = requestOf(sent);
    if ("blocked" in built) {
      setBlocked(built.blocked);
      return;
    }
    setBlocked(null);
    if (JSON.stringify(built.req) === savedRef.current) {
      if (phaseRef.current === "failed") setPhase("idle");
      setFail(null);
      return;
    }
    const id = noteIdRef.current;
    const isCreated = createdRef.current;
    const req = isCreated ? { ...built.req, expectedUpdatedAt: base.current ?? undefined } : { ...built.req, id };
    const json = JSON.stringify([isCreated, req]);
    if (attempt.current?.json !== json) attempt.current = { json, key: newIdempotencyKey() };
    inFlight.current = true;
    setPhase("saving");
    try {
      const keepalive = !!opts.keepalive && new TextEncoder().encode(JSON.stringify(req)).length < KEEPALIVE_MAX;
      const { note, created: applied } = await saveMeetingNote(req, attempt.current.key, isCreated ? id : null, keepalive);
      attempt.current = null;
      uncertain.current = null;
      base.current = note.updatedAt;
      setCreatedBy(note.createdBy);
      setFail(null);
      if (!isCreated) {
        createdRef.current = true;
        setCreated(true);
        if (mounted.current) onCreated(note);
      }
      if (!applied) {
        // A repeated POST answers with the note as an earlier attempt stored it. This editor saved nothing after
        // that create, so a note changed since (updatedAt past createdAt) holds someone else's edit: the person
        // chooses, as on a 409. Only an untouched note lets the edit follow as a PATCH.
        if (note.updatedAt !== note.createdAt) {
          setLatest(note);
          setLatestFailed(false);
          setPhase("conflict");
        } else {
          // The stored items take the place of the form's rows by position, so the PATCH keeps their ids.
          const stored = new Map(built.keys.map((k, i) => [k, note.actionItems[i]?.id]));
          replaceForm({
            ...formRef.current,
            items: formRef.current.items.map((r) => (r.id === null && stored.get(r.key) ? { ...r, id: stored.get(r.key)! } : r)),
          });
          const theirs = requestOf(formOf(note));
          if ("req" in theirs) markSaved(JSON.stringify(theirs.req));
          setPhase("idle");
          again.current = true;
        }
        keepDraft();
        return;
      }
      const savedIds = new Map<string, string>();
      if (note.actionItems.length === built.keys.length) built.keys.forEach((k, i) => savedIds.set(k, note.actionItems[i].id));
      const withIds = (f: NoteForm): NoteForm => ({
        ...f,
        items: f.items.map((r): ItemRow => {
          const sid = savedIds.get(r.key);
          const fresh = note.actionItems.find((a) => a.id === (r.id ?? sid));
          const linked = fresh ? { issue: fresh.issue, held: isHeld(fresh) } : { issue: null, held: false };
          return sid && r.id === null ? { ...r, id: sid, ...linked } : fresh ? { ...r, ...linked } : r;
        }),
      });
      markSaved(snapshotOf(withIds(sent)));
      replaceForm(withIds(formRef.current));
      setSavedAt(new Date().toISOString());
      setPhase("idle");
      keepDraft();
    } catch (err) {
      const code = err instanceof ApiError ? err.code : null;
      if (code === "conflict" && isCreated) {
        attempt.current = null;
        setPhase("conflict");
        const lost = uncertain.current;
        uncertain.current = null;
        let fresh: MeetingNote | null = null;
        try {
          fresh = await apiGet<MeetingNote>(`/api/meeting-notes/${encodeURIComponent(id)}`);
        } catch {
          setLatestFailed(true);
        }
        const theirs = fresh ? requestOf(formOf(fresh)) : null;
        if (fresh && lost && theirs && "req" in theirs && contentOf(theirs.req) === lost.content) {
          const stored = fresh;
          base.current = stored.updatedAt;
          const ids = new Map(lost.keys.map((k, i) => [k, stored.actionItems[i]?.id]));
          replaceForm({ ...formRef.current, items: formRef.current.items.map((r) => (r.id === null && ids.get(r.key) ? { ...r, id: ids.get(r.key)! } : r)) });
          markSaved(JSON.stringify(theirs.req));
          setPhase("idle");
          again.current = true;
        } else {
          setLatest(fresh);
        }
      } else if (code === "action_item_linked" && isCreated) {
        attempt.current = null;
        try {
          const fresh = await apiGet<MeetingNote>(`/api/meeting-notes/${encodeURIComponent(id)}`);
          replaceForm(rebaseOnto(formRef.current, fresh));
          setRestored(true);
          setPhase("idle");
          again.current = true;
        } catch {
          setFail("network");
          setPhase("failed");
        }
      } else {
        const f: Failure =
          code === "invalid_request" || code === "not_found" || code === "request_in_progress"
            ? code
            : !(err instanceof ApiError) || err.status >= 500
              ? "network"
              : "unknown";
        if (f === "network" && isCreated) uncertain.current = { content: contentOf(req), keys: built.keys };
        if (f !== "network" && f !== "request_in_progress") attempt.current = null;
        setFail(f);
        setPhase("failed");
      }
    } finally {
      inFlight.current = false;
      const now = phaseRef.current as Phase;
      if (again.current && now !== "conflict" && now !== "failed") {
        again.current = false;
        void flush();
      }
      again.current = false;
    }
    // onCreated comes from the screen that owns the editor and only reads stable values.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const change = (patch: Partial<NoteForm> | ((f: NoteForm) => NoteForm)) => {
    const next = typeof patch === "function" ? patch(formRef.current) : { ...formRef.current, ...patch };
    replaceForm(next);
    setRestored(false);
    keepDraft();
    if (timer.current) clearTimeout(timer.current);
    timer.current = setTimeout(() => void flush(), SAVE_AFTER_MS);
  };

  useEffect(() => {
    mounted.current = true;
    const d = initial ? readNoteDraft<NoteForm>(owner, initial.id) : latestNewDraft<NoteForm>(owner);
    if (d && !isBlank(d.form) && snapshotOf(d.form) !== savedRef.current) setDraft(d);
    if (!initial) titleRef.current?.focus();
    const every = setInterval(() => tick((n) => n + 1), 30_000);
    const onHide = () => {
      if (document.visibilityState === "hidden") void flush({ keepalive: true });
    };
    document.addEventListener("visibilitychange", onHide);
    setLeaveGuard(() => (unsavedRef.current ? e.leave : null));
    return () => {
      mounted.current = false;
      clearInterval(every);
      document.removeEventListener("visibilitychange", onHide);
      setLeaveGuard(null);
      if (timer.current) clearTimeout(timer.current);
    };
    // Runs once: the draft belongs to the note the editor opened with.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    if (!unsaved) return;
    const onUnload = (ev: BeforeUnloadEvent) => {
      ev.preventDefault();
      ev.returnValue = "";
    };
    // Links inside the app change the route without unloading, so they ask here.
    const onClick = (ev: MouseEvent) => {
      const a = (ev.target as Element | null)?.closest?.("a[href]");
      if (!a || a.getAttribute("target") === "_blank" || ev.defaultPrevented) return;
      if (!window.confirm(e.leave)) {
        ev.preventDefault();
        ev.stopPropagation();
      }
    };
    window.addEventListener("beforeunload", onUnload);
    document.addEventListener("click", onClick, true);
    return () => {
      window.removeEventListener("beforeunload", onUnload);
      document.removeEventListener("click", onClick, true);
    };
  }, [unsaved, e.leave]);

  useEffect(() => {
    if (leaving.current && !unsaved && phase === "idle") {
      const to = leaving.current;
      leaving.current = null;
      router.push(to);
    }
  }, [unsaved, phase, router]);

  useEffect(() => {
    if (!focusKey) return;
    document.querySelector<HTMLInputElement>(`[data-row-input="${focusKey}"]`)?.focus();
    setFocusKey(null);
  }, [focusKey]);

  const keepMine = () => {
    if (!latest) return;
    base.current = latest.updatedAt;
    replaceForm(rebaseOnto(formRef.current, latest));
    setLatest(null);
    setPhase("idle");
    void flush();
  };

  const takeTheirs = () => {
    if (!latest || !window.confirm(e.conflict.theirsConfirm)) return;
    const next = formOf(latest);
    base.current = latest.updatedAt;
    replaceForm(next);
    markSaved(snapshotOf(next));
    setSavedAt(latest.updatedAt);
    clearNoteDraft(owner, noteIdRef.current);
    setLatest(null);
    setPhase("idle");
  };

  const restoreDraft = () => {
    if (!draft) return;
    if (!createdRef.current && draft.noteId !== noteIdRef.current) {
      // Continue under the draft's id: if its first POST did reach the server, resending finds that note.
      clearNoteDraft(owner, noteIdRef.current);
      noteIdRef.current = draft.noteId;
      setNoteId(draft.noteId);
    }
    if (draft.base) base.current = draft.base;
    setDraft(null);
    change(draft.form);
  };

  const finish = () => {
    const to = createdRef.current ? `/notes/view?id=${noteIdRef.current}` : "/notes";
    if (!unsaved) {
      router.push(to);
      return;
    }
    leaving.current = to;
    void flush();
  };

  const remove = async () => {
    if (!createdRef.current) return;
    const items = formRef.current.items.filter((r) => r.id).map((r) => ({ issue: r.held ? true : null }));
    if (timer.current) clearTimeout(timer.current);
    const gone = await confirmAndDelete(
      { id: noteIdRef.current, title: formRef.current.title.trim() || nt.newNote, actionItems: items },
      nt,
      owner,
    );
    if (!gone) return;
    markSaved(snapshotOf(formRef.current));
    unsavedRef.current = false;
    router.push("/notes");
  };

  const memberList = members.data ?? [];
  const nameOf = (id: string) => {
    const m = memberList.find((x) => x.id === id);
    return m ? fmt.person(m) : t.common.unknownPerson;
  };
  const field =
    "w-full rounded-control bg-surface px-3 text-ink shadow-[inset_0_0_0_1px_var(--wb-rule-strong)] placeholder:text-muted focus:shadow-[inset_0_0_0_2px_var(--wb-focus)] focus:outline-none disabled:bg-sunken";
  const locked = phase === "conflict";

  const addDecision = (after?: string) =>
    change((f) => {
      const row = { key: rowKey(), text: "" };
      const at = after ? f.decisions.findIndex((d) => d.key === after) + 1 : f.decisions.length;
      setFocusKey(row.key);
      return { ...f, decisions: [...f.decisions.slice(0, at), row, ...f.decisions.slice(at)] };
    });
  const addItem = (after?: string) =>
    change((f) => {
      const row: ItemRow = { key: rowKey(), id: null, text: "", assigneeId: null, dueDate: null, issue: null, held: false };
      const at = after ? f.items.findIndex((r) => r.key === after) + 1 : f.items.length;
      setFocusKey(row.key);
      return { ...f, items: [...f.items.slice(0, at), row, ...f.items.slice(at)] };
    });
  const setItem = (key: string, patch: Partial<ItemRow>) =>
    change((f) => ({ ...f, items: f.items.map((r) => (r.key === key ? { ...r, ...patch } : r)) }));
  const onEnter = (ev: React.KeyboardEvent, add: () => void) => {
    if (ev.key === "Enter" && !ev.nativeEvent.isComposing) {
      ev.preventDefault();
      add();
    }
  };

  const status =
    phase === "saving" ? (
      <span className="inline-flex items-center gap-2">
        <span aria-hidden className="busy-dot" />
        {e.saving}…
      </span>
    ) : phase === "failed" && failure ? (
      <span className="flex flex-wrap items-center gap-x-3 gap-y-1 border-l-2 border-ink-soft pl-3 text-ink">
        {e.failed[failure]}
        {failure !== "not_found" && (
          <button type="button" className="link cursor-pointer" onClick={() => void flush()}>
            {e.retry}
          </button>
        )}
      </span>
    ) : blocked ? (
      <span className="border-l-2 border-ink-soft pl-3 text-ink">{blocked === "needTitle" ? e.needTitle : e.emptyItem}</span>
    ) : phase === "conflict" || unsaved ? (
      e.unsaved
    ) : savedAt && created ? (
      e.saved(fmt.relativeTime(savedAt))
    ) : (
      e.notYet
    );

  return (
    <article className="flex max-w-[820px] flex-col gap-6">
      <Link href={created ? `/notes/view?id=${noteId}` : "/notes"} className="link self-start text-small text-ink-soft">
        {nt.back}
      </Link>
      <PageHeader
        meta={created ? e.metaEdit : e.metaNew}
        title={form.title.trim() || nt.newNote}
      >
        {e.lead}
      </PageHeader>

      {draft && (
        <div role="alert" className="flex flex-wrap items-center gap-x-4 gap-y-2 rounded-control border-l-2 border-ink-soft bg-surface px-4 py-3 text-ui shadow-elev-1">
          <span className="min-w-0 flex-1">{e.draft.found}</span>
          <button type="button" className="btn" onClick={restoreDraft}>
            {e.draft.restore}
          </button>
          <button
            type="button"
            className="btn"
            onClick={() => {
              if (draft) clearNoteDraft(owner, draft.noteId);
              setDraft(null);
            }}
          >
            {e.draft.discard}
          </button>
        </div>
      )}

      {phase === "conflict" && (
        <section aria-labelledby={`${ids}-conflict`} role="alert" className="card flex flex-col gap-4 px-5 py-5">
          <div className="flex flex-col gap-1 border-l-2 border-ink-soft pl-3">
            <h2 id={`${ids}-conflict`} className="text-heading font-semibold">
              {e.conflict.title}
            </h2>
            <p className="text-small text-ink-soft">{e.conflict.lead}</p>
          </div>
          {latest ? (
            <LatestCopy note={latest} />
          ) : latestFailed ? (
            <p className="text-small text-ink">
              {e.conflict.loadFailed}{" "}
              <button type="button" className="link cursor-pointer" onClick={() => void loadLatest(noteIdRef.current)}>
                {e.retry}
              </button>
            </p>
          ) : (
            <p className="text-small text-muted">{e.conflict.loading}…</p>
          )}
          <div className="flex flex-wrap gap-2">
            <button type="button" className="btn btn-primary" disabled={!latest} onClick={keepMine}>
              {e.conflict.mine}
            </button>
            <button type="button" className="btn" disabled={!latest} onClick={takeTheirs}>
              {e.conflict.theirs}
            </button>
          </div>
        </section>
      )}

      <form
        noValidate
        aria-label={created ? e.metaEdit : e.metaNew}
        onSubmit={(ev) => {
          ev.preventDefault();
          void flush();
        }}
        onBlur={(ev) => {
          if (!ev.currentTarget.contains(ev.relatedTarget as Node | null)) void flush();
          else if (timer.current) void flush();
        }}
        className="flex flex-col gap-6"
      >
        <fieldset disabled={locked} className="card flex min-w-0 flex-col gap-4 px-5 py-5">
          <div className="flex flex-col gap-2">
            <label htmlFor={`${ids}-title`} className="text-small font-semibold">
              {e.title}
            </label>
            <input
              ref={titleRef}
              id={`${ids}-title`}
              value={form.title}
              maxLength={200}
              autoComplete="off"
              aria-invalid={blocked === "needTitle" || undefined}
              onChange={(ev) => change({ title: ev.target.value })}
              placeholder={e.titlePlaceholder}
              className={`${field} h-12 rounded-item px-3.5 text-heading`}
            />
          </div>
          <div className="grid grid-cols-1 gap-4 min-[560px]:grid-cols-2">
            <div className="flex flex-col gap-2">
              <label htmlFor={`${ids}-date`} className="text-small font-semibold">
                {e.date}
              </label>
              <input
                id={`${ids}-date`}
                type="date"
                value={localDate(form.heldAt)}
                onChange={(ev) => ev.target.value && change({ heldAt: onDate(form.heldAt, ev.target.value) })}
                className={`${field} h-10 text-ui tabular-nums`}
              />
            </div>
            <div className="flex flex-col gap-2">
              <label htmlFor={`${ids}-project`} className="text-small font-semibold">
                {e.project}
              </label>
              <select
                id={`${ids}-project`}
                value={form.projectId ?? ""}
                onChange={(ev) => change({ projectId: ev.target.value || null })}
                className={`${field} h-10 text-ui`}
              >
                <option value="">{e.noProject}</option>
                {(projects.data ?? []).map((p) => (
                  <option key={p.id} value={p.id}>
                    {p.name}
                  </option>
                ))}
                {form.projectId && projects.data && !projects.data.some((p) => p.id === form.projectId) && (
                  <option value={form.projectId}>{initial?.project?.name ?? form.projectId}</option>
                )}
              </select>
            </div>
          </div>
          <div className="flex flex-col gap-2">
            <p id={`${ids}-attendees`} className="flex flex-wrap items-baseline gap-x-2 text-small font-semibold">
              {e.attendees}
              <span className="font-normal text-muted">{e.attendeesHint}</span>
            </p>
            {members.error ? (
              <p className="text-small text-ink">{e.noMembers}</p>
            ) : !members.data ? (
              <p className="text-small text-muted">{t.common.loading}…</p>
            ) : (
              <div role="group" aria-labelledby={`${ids}-attendees`} className="flex flex-wrap gap-1.5">
                {memberList.map((m) => {
                  const on = form.attendeeIds.includes(m.id);
                  return (
                    <button
                      key={m.id}
                      type="button"
                      aria-pressed={on}
                      onClick={() =>
                        change((f) => ({
                          ...f,
                          attendeeIds: on ? f.attendeeIds.filter((x) => x !== m.id) : [...f.attendeeIds, m.id],
                        }))
                      }
                      className={`inline-flex h-8 max-w-[14rem] cursor-pointer items-center gap-1.5 rounded-full pl-1 pr-2.5 text-small transition-shadow duration-[120ms] ease-tool ${
                        on
                          ? "bg-surface font-medium text-ink shadow-[inset_0_0_0_1.5px_var(--wb-ink)]"
                          : "text-ink-soft shadow-[inset_0_0_0_1px_var(--wb-rule-strong)] hover:text-ink hover:shadow-[inset_0_0_0_1px_var(--wb-ink-soft)]"
                      }`}
                    >
                      <Avatar person={m} size="sm" />
                      <span className="truncate">{fmt.person(m)}</span>
                      {on && <Check />}
                    </button>
                  );
                })}
              </div>
            )}
          </div>
        </fieldset>

        <Card title={nt.decisions} count={form.decisions.filter((d) => d.text.trim()).length} hint={nt.decisionsHint}>
          <fieldset disabled={locked} className="flex min-w-0 flex-col gap-2 px-5 pb-5 pt-1">
            <ol className="flex flex-col gap-2">
              {form.decisions.map((d, i) => (
                <li key={d.key} className="flex items-center gap-2">
                  <span aria-hidden className="w-5 shrink-0 text-right font-mono text-meta text-muted">
                    {i + 1}
                  </span>
                  <input
                    data-row-input={d.key}
                    value={d.text}
                    maxLength={500}
                    aria-label={e.decisionLabel(i + 1)}
                    placeholder={e.decisionPlaceholder}
                    onChange={(ev) =>
                      change((f) => ({ ...f, decisions: f.decisions.map((x) => (x.key === d.key ? { ...x, text: ev.target.value } : x)) }))
                    }
                    onKeyDown={(ev) => onEnter(ev, () => addDecision(d.key))}
                    className={`${field} h-10 min-w-0 flex-1 text-ui`}
                  />
                  <RemoveButton
                    label={e.removeDecision(i + 1)}
                    onClick={() => change((f) => ({ ...f, decisions: f.decisions.filter((x) => x.key !== d.key) }))}
                  />
                </li>
              ))}
            </ol>
            <AddRow onClick={() => addDecision()} label={e.addDecision} hint={form.decisions.length > 0 ? e.enterHint : null} />
          </fieldset>
        </Card>

        <Card title={nt.actionItems} count={form.items.filter((r) => r.text.trim()).length} hint={nt.actionHint}>
          <fieldset disabled={locked} className="flex min-w-0 flex-col gap-2 px-5 pb-5 pt-1">
            <ol className="flex flex-col">
              {form.items.map((r, i) => (
                <li
                  key={r.key}
                  className="relative grid grid-cols-[minmax(0,1fr)_auto] gap-2 py-3 after:absolute after:inset-x-0 after:top-0 after:h-px after:bg-rule first:pt-0 first:after:hidden min-[640px]:grid-cols-[minmax(0,1fr)_11rem_10rem_auto] min-[640px]:items-center"
                >
                  <input
                    data-row-input={r.key}
                    value={r.text}
                    maxLength={500}
                    aria-label={e.itemLabel(i + 1)}
                    aria-invalid={(blocked === "emptyItem" && r.id !== null && !r.text.trim()) || undefined}
                    placeholder={e.itemPlaceholder}
                    onChange={(ev) => setItem(r.key, { text: ev.target.value })}
                    onKeyDown={(ev) => onEnter(ev, () => addItem(r.key))}
                    className={`${field} h-10 text-ui`}
                  />
                  <select
                    value={r.assigneeId ?? ""}
                    aria-label={e.assigneeLabel(i + 1)}
                    onChange={(ev) => setItem(r.key, { assigneeId: ev.target.value || null })}
                    className={`${field} col-start-1 h-10 text-small min-[640px]:col-start-auto`}
                  >
                    <option value="">{e.noAssignee}</option>
                    {memberList.map((m) => (
                      <option key={m.id} value={m.id}>
                        {fmt.person(m)}
                      </option>
                    ))}
                    {r.assigneeId && members.data && !memberList.some((m) => m.id === r.assigneeId) && (
                      <option value={r.assigneeId}>{nameOf(r.assigneeId)}</option>
                    )}
                  </select>
                  <input
                    type="date"
                    value={r.dueDate ?? ""}
                    aria-label={e.dueLabel(i + 1)}
                    onChange={(ev) => setItem(r.key, { dueDate: ev.target.value || null })}
                    className={`${field} col-start-1 h-10 text-small tabular-nums min-[640px]:col-start-auto`}
                  />
                  <div className="col-start-2 row-start-1 flex items-center min-[640px]:col-start-4">
                    <RemoveButton
                      label={r.held ? e.linkedLocked : e.removeItem(i + 1)}
                      disabled={r.held}
                      onClick={() => change((f) => ({ ...f, items: f.items.filter((x) => x.key !== r.key) }))}
                    />
                  </div>
                  {r.issue && (
                    <p className="col-span-full flex flex-wrap items-center gap-2 text-small text-muted">
                      <span className="font-mono text-meta">
                        {r.issue.repositoryName} #{r.issue.number}
                      </span>
                      <StatusBadge status={r.issue.status} />
                    </p>
                  )}
                </li>
              ))}
            </ol>
            {restored && <p className="border-l-2 border-ink-soft pl-3 text-small text-ink">{e.linkedRestored}</p>}
            <AddRow onClick={() => addItem()} label={e.addItem} hint={form.items.length > 0 ? e.enterHint : null} />
          </fieldset>
        </Card>

        <Card title={nt.body} hint={nt.bodyHint}>
          <fieldset disabled={locked} className="min-w-0 px-5 pb-5 pt-1">
            <textarea
              value={form.bodyMd}
              maxLength={60000}
              rows={10}
              aria-label={nt.body}
              onChange={(ev) => change({ bodyMd: ev.target.value })}
              placeholder={e.bodyPlaceholder}
              className={`${field} min-h-48 resize-y rounded-item px-3.5 py-2.5 text-body`}
            />
          </fieldset>
        </Card>

        {created && canDelete({ createdBy }, me) && (
          <div>
            <button type="button" className="btn" onClick={() => void remove()}>
              {nt.delete}
            </button>
          </div>
        )}

        <div className="sticky bottom-0 z-10 -mx-4 flex flex-wrap items-center gap-x-4 gap-y-2 border-t border-rule bg-(--wb-topbar) px-4 py-3 backdrop-blur-md min-[640px]:mx-0 min-[640px]:rounded-t-card min-[640px]:px-5">
          <p role="status" className="min-w-0 flex-1 text-small text-ink-soft">
            {status}
          </p>
          <button type="button" className="btn btn-primary" onClick={finish}>
            {e.done}
          </button>
        </div>
      </form>
    </article>
  );
}

function LatestCopy({ note }: { note: MeetingNote }) {
  const t = useT();
  const nt = t.notes;
  const fmt = useFormat();
  return (
    <div className="flex flex-col gap-3 rounded-item bg-sunken px-4 py-4 text-small">
      <p className="text-meta text-muted">{nt.editor.conflict.latest(fmt.relativeTime(note.updatedAt))}</p>
      <p className="text-ui font-semibold">{note.title}</p>
      <p className="text-ink-soft">
        {fmt.date(note.heldAt)}
        {note.project && ` · ${note.project.name}`}
        {" · "}
        {nt.attendees(note.attendees.length ? note.attendees.map(fmt.person).join(", ") : nt.noAttendees)}
      </p>
      {note.decisions.length > 0 && (
        <div>
          <p className="font-semibold">{nt.decisions}</p>
          <ol className="list-decimal pl-5 text-ink-soft">
            {note.decisions.map((d, i) => (
              <li key={i}>{d}</li>
            ))}
          </ol>
        </div>
      )}
      {note.actionItems.length > 0 && (
        <div>
          <p className="font-semibold">{nt.actionItems}</p>
          <ul className="list-disc pl-5 text-ink-soft">
            {note.actionItems.map((a) => (
              <li key={a.id}>
                {a.text}
                {a.assignee && ` · ${fmt.person(a.assignee)}`}
                {a.dueDate && ` · ${nt.due(fmt.date(`${a.dueDate}T00:00:00`))}`}
              </li>
            ))}
          </ul>
        </div>
      )}
      {note.bodyMd && <pre className="max-h-60 overflow-auto whitespace-pre-wrap break-keep font-sans text-ink-soft">{note.bodyMd}</pre>}
    </div>
  );
}

function Check() {
  return (
    <svg width="12" height="12" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden className="shrink-0">
      <path d="m3.5 8.5 3 3 6-7" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}

function RemoveButton({ label, onClick, disabled = false }: { label: string; onClick: () => void; disabled?: boolean }) {
  return (
    <button
      type="button"
      aria-label={label}
      title={label}
      disabled={disabled}
      onClick={onClick}
      className="inline-flex size-10 shrink-0 cursor-pointer items-center justify-center rounded-control text-muted transition-colors duration-[120ms] ease-tool hover:bg-hover hover:text-ink disabled:cursor-default disabled:opacity-40 disabled:hover:bg-transparent"
    >
      <svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.75" aria-hidden>
        <path d="M4 4l8 8M12 4l-8 8" strokeLinecap="round" />
      </svg>
    </button>
  );
}

function AddRow({ onClick, label, hint }: { onClick: () => void; label: string; hint: string | null }) {
  return (
    <div className="flex flex-wrap items-center gap-3">
      <button type="button" onClick={onClick} className="btn h-9 self-start px-3">
        <span aria-hidden>+</span>
        {label}
      </button>
      {hint && (
        <span className="inline-flex items-center gap-1 text-meta text-muted max-[639px]:hidden">
          <kbd className="kbd">Enter</kbd> {hint}
        </span>
      )}
    </div>
  );
}
