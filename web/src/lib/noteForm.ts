import { newIdempotencyKey } from "./api";
import type { MeetingActionItem, MeetingNote, MeetingNoteRequest } from "./types";

export type DecisionRow = { key: string; text: string };

/** `id` is null until the server has saved the item; `issue` is read only (linked items cannot be removed). */
export type ItemRow = {
  key: string;
  id: string | null;
  text: string;
  assigneeId: string | null;
  dueDate: string | null;
  issue: MeetingActionItem["issue"];
};

export type NoteForm = {
  title: string;
  heldAt: string;
  projectId: string | null;
  bodyMd: string;
  attendeeIds: string[];
  decisions: DecisionRow[];
  items: ItemRow[];
};

export type Blocked = "needTitle" | "emptyItem";

export const rowKey = () => newIdempotencyKey();

export function emptyForm(now = new Date()): NoteForm {
  return { title: "", heldAt: now.toISOString(), projectId: null, bodyMd: "", attendeeIds: [], decisions: [], items: [] };
}

export function itemRow(a: MeetingActionItem): ItemRow {
  return { key: a.id, id: a.id, text: a.text, assigneeId: a.assignee?.id ?? null, dueDate: a.dueDate, issue: a.issue };
}

export function formOf(n: MeetingNote): NoteForm {
  return {
    title: n.title,
    heldAt: n.heldAt,
    projectId: n.project?.id ?? null,
    bodyMd: n.bodyMd,
    attendeeIds: n.attendees.map((a) => a.id),
    decisions: n.decisions.map((text) => ({ key: rowKey(), text })),
    items: n.actionItems.map(itemRow),
  };
}

/**
 * The request a form saves as, and which rows its action items came from (same order). New rows still empty are
 * left out; a saved row emptied out blocks the save, since leaving it out would delete it.
 */
export function requestOf(f: NoteForm): { req: MeetingNoteRequest; keys: string[] } | { blocked: Blocked } {
  const title = f.title.trim();
  if (!title) return { blocked: "needTitle" };
  const rows = f.items.filter((r) => r.id !== null || r.text.trim() !== "");
  if (rows.some((r) => r.text.trim() === "")) return { blocked: "emptyItem" };
  return {
    req: {
      title,
      heldAt: f.heldAt,
      projectId: f.projectId,
      bodyMd: f.bodyMd,
      decisions: f.decisions.map((d) => d.text.trim()).filter(Boolean),
      attendeeIds: [...f.attendeeIds].sort(),
      actionItems: rows.map((r) => ({ id: r.id ?? undefined, text: r.text.trim(), assigneeId: r.assigneeId, dueDate: r.dueDate })),
    },
    keys: rows.map((r) => r.key),
  };
}

export function isBlank(f: NoteForm): boolean {
  return !f.title.trim() && !f.bodyMd.trim() && f.decisions.every((d) => !d.text.trim()) && f.items.every((r) => !r.text.trim());
}

/** Comparable form of what a form would save; equal strings mean nothing new to save. */
export function snapshotOf(f: NoteForm): string {
  const r = requestOf(f);
  return "req" in r ? JSON.stringify(r.req) : JSON.stringify(f);
}

/**
 * Builds on someone else's newer copy: items they deleted become new items, and items linked to an issue that
 * this form no longer has come back (they cannot be removed).
 */
export function rebaseOnto(f: NoteForm, latest: MeetingNote): NoteForm {
  const theirs = new Set(latest.actionItems.map((a) => a.id));
  const mine = new Set(f.items.map((r) => r.id));
  return {
    ...f,
    items: [
      ...f.items.map((r) => (r.id !== null && !theirs.has(r.id) ? { ...r, id: null, issue: null } : r)),
      ...latest.actionItems.filter((a) => a.issue && !mine.has(a.id)).map(itemRow),
    ],
  };
}

/** `yyyy-MM-dd` of an instant in the viewer's time zone. */
export function localDate(iso: string): string {
  const d = new Date(iso);
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

/** Moves an instant to another day, keeping its local time of day. */
export function onDate(iso: string, date: string): string {
  const [y, m, d] = date.split("-").map(Number);
  if (!y || !m || !d) return iso;
  const at = new Date(iso);
  at.setFullYear(y, m - 1, d);
  return at.toISOString();
}

type Draft = { form: NoteForm; base: string | null; at: string };

const draftKey = (id: string | null) => `workboard.noteDraft.${id ?? "new"}`;

/** Text that was not saved yet, kept on this device so a failed save or a closed tab loses nothing. */
export function readDraft(id: string | null): Draft | null {
  try {
    const raw = window.localStorage.getItem(draftKey(id));
    return raw ? (JSON.parse(raw) as Draft) : null;
  } catch {
    return null;
  }
}

export function writeDraft(id: string | null, form: NoteForm, base: string | null) {
  try {
    window.localStorage.setItem(draftKey(id), JSON.stringify({ form, base, at: new Date().toISOString() } satisfies Draft));
  } catch {
    // Storage can be full or unavailable; the leave warning still protects the text.
  }
}

export function clearDraft(id: string | null) {
  try {
    window.localStorage.removeItem(draftKey(id));
  } catch {
    // Nothing to clear.
  }
}
