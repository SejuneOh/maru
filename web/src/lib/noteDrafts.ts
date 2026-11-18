// Unsaved meeting-note text kept on this device. A draft belongs to one tenant, one member and one note id
// (for a new note, the id the editor generated), so another person on the same browser is never offered it and
// two tabs writing new notes do not overwrite each other. Sign-out removes every draft.

const PREFIX = "workboard.noteDraft.";

export type DraftOwner = { tenantId: string; memberId: string };

/** `created` is false while the note has never been saved: reopening `?new=1` offers such a draft. */
export type NoteDraft<F> = { noteId: string; form: F; base: string | null; created: boolean; at: string };

const keyOf = (o: DraftOwner, noteId: string) => `${PREFIX}${o.tenantId}.${o.memberId}.${noteId}`;

export function readNoteDraft<F>(o: DraftOwner, noteId: string): NoteDraft<F> | null {
  try {
    const raw = window.localStorage.getItem(keyOf(o, noteId));
    return raw ? (JSON.parse(raw) as NoteDraft<F>) : null;
  } catch {
    return null;
  }
}

/** The newest draft of a note this member started here and never saved. */
export function latestNewDraft<F>(o: DraftOwner): NoteDraft<F> | null {
  const mine = `${PREFIX}${o.tenantId}.${o.memberId}.`;
  let found: NoteDraft<F> | null = null;
  try {
    for (let i = 0; i < window.localStorage.length; i++) {
      const key = window.localStorage.key(i);
      if (!key?.startsWith(mine)) continue;
      const d = JSON.parse(window.localStorage.getItem(key) ?? "null") as NoteDraft<F> | null;
      if (d && !d.created && (!found || d.at > found.at)) found = d;
    }
  } catch {
    return found;
  }
  return found;
}

export function writeNoteDraft<F>(o: DraftOwner, d: Omit<NoteDraft<F>, "at">) {
  try {
    window.localStorage.setItem(keyOf(o, d.noteId), JSON.stringify({ ...d, at: new Date().toISOString() }));
  } catch {
    // Storage can be full or unavailable; the leave warning still protects the text.
  }
}

export function clearNoteDraft(o: DraftOwner, noteId: string) {
  try {
    window.localStorage.removeItem(keyOf(o, noteId));
  } catch {
    // Nothing to clear.
  }
}

export function clearAllNoteDrafts() {
  try {
    const keys: string[] = [];
    for (let i = 0; i < window.localStorage.length; i++) {
      const key = window.localStorage.key(i);
      if (key?.startsWith(PREFIX)) keys.push(key);
    }
    keys.forEach((k) => window.localStorage.removeItem(k));
  } catch {
    // Nothing to clear.
  }
}

/** A version 4 UUID, also where `crypto.randomUUID` is missing (plain http on a LAN address). */
export function newUuid(): string {
  if (typeof crypto.randomUUID === "function") return crypto.randomUUID();
  const b = crypto.getRandomValues(new Uint8Array(16));
  b[6] = (b[6] & 0x0f) | 0x40;
  b[8] = (b[8] & 0x3f) | 0x80;
  const h = Array.from(b, (x) => x.toString(16).padStart(2, "0")).join("");
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`;
}
