"use client";

import { useEffect, useState } from "react";
import { type EditFailure, type Notice, dismissNotice, editIssue, knownIssue, useNotice } from "@/lib/boardEdits";
import { useT } from "@/lib/i18n";
import { STATUS_DOT } from "@/lib/labels";
import type { Status } from "@/lib/types";
import { Dot, Kbd } from "./ui";
import { ISSUE_ATTR, isTyping } from "./useShortcuts";
import { NEW_ISSUE, useIssuePanel } from "./useIssuePanel";

/** `s` then a number: the order the columns read, with cancelled last. */
export const STATUS_KEYS: Status[] = ["todo", "in_progress", "in_review", "done", "cancelled"];
const KEY_TIMEOUT_MS = 2000;
const FAILURE_MS = 12_000;
const UNDONE_MS = 4000;

/**
 * Result of a card change, bottom left: a move to done or cancelled with a 10 second undo, an undo,
 * or a failure with what to do about it. Polite live region, so it never takes focus.
 */
export function BoardNotice() {
  const notice = useNotice();
  return (
    <div aria-live="polite" className="pointer-events-none fixed inset-x-4 bottom-4 z-50 flex justify-start min-[640px]:inset-x-auto min-[640px]:bottom-6 min-[640px]:left-6 min-[1024px]:left-[272px]">
      {notice && <NoticeCard key={notice.id} notice={notice} />}
    </div>
  );
}

function NoticeCard({ notice }: { notice: Notice }) {
  const t = useT();
  const e = t.edit;
  const [paused, setPaused] = useState(false);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (paused) return;
    const failed = notice.kind === "failed" || (notice.kind === "message" && notice.failed);
    const ms = notice.kind === "moved" ? Math.max(0, notice.until - Date.now()) : failed ? FAILURE_MS : UNDONE_MS;
    const timer = setTimeout(() => dismissNotice(notice.id), ms);
    return () => clearTimeout(timer);
  }, [notice, paused]);

  const close = (
    <button
      type="button"
      onClick={() => dismissNotice(notice.id)}
      aria-label={e.dismiss}
      title={e.dismiss}
      className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-control text-muted hover:bg-hover hover:text-ink"
    >
      <svg width="12" height="12" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.75" aria-hidden>
        <path d="m4 4 8 8M12 4l-8 8" strokeLinecap="round" />
      </svg>
    </button>
  );

  if (notice.kind === "message")
    return (
      <div
        role={notice.failed ? "alert" : "status"}
        onMouseEnter={() => setPaused(true)}
        onMouseLeave={() => setPaused(false)}
        className={`notice pointer-events-auto flex w-full max-w-[420px] items-start gap-3 rounded-item bg-surface py-2.5 pl-4 pr-2 shadow-elev-2 ${
          notice.failed ? "border-l-2 border-ink-soft" : ""
        }`}
      >
        <div className="flex min-w-0 flex-1 flex-col gap-0.5 py-0.5">
          <p className="text-ui text-ink">{notice.title}</p>
          {notice.detail && <p className="text-small text-ink-soft">{notice.detail}</p>}
          {notice.retry && (
            <button
              type="button"
              className="link mt-1 cursor-pointer self-start text-small font-medium"
              onClick={() => {
                dismissNotice(notice.id);
                notice.retry?.();
              }}
            >
              {e.retry}
            </button>
          )}
        </div>
        {close}
      </div>
    );

  return (
    <div
      role={notice.kind === "failed" ? "alert" : "status"}
      onMouseEnter={() => notice.kind !== "moved" && setPaused(true)}
      onMouseLeave={() => setPaused(false)}
      className={`notice pointer-events-auto flex w-full max-w-[420px] items-start gap-3 rounded-item bg-surface py-2.5 pl-4 pr-2 shadow-elev-2 ${
        notice.kind === "failed" ? "border-l-2 border-ink-soft" : ""
      }`}
    >
      {notice.kind === "failed" ? (
        <div className="flex min-w-0 flex-1 flex-col gap-0.5 py-0.5">
          <p className="text-ui text-ink">{e.failed(notice.ref)}</p>
          <p className="text-small text-ink-soft">{e.errors[notice.failure satisfies EditFailure]}</p>
          {notice.retry && (
            <button
              type="button"
              className="link mt-1 cursor-pointer self-start text-small font-medium"
              onClick={() => {
                dismissNotice(notice.id);
                notice.retry?.();
              }}
            >
              {e.retry}
            </button>
          )}
        </div>
      ) : (
        <div className="flex min-w-0 flex-1 flex-col gap-0.5 py-0.5">
          <p className="flex items-center gap-2 text-ui text-ink">
            <Dot className={STATUS_DOT[notice.to]} />
            <span className="min-w-0">
              {notice.kind === "moved" ? e.movedTo(notice.ref, t.status[notice.to]) : e.undone(notice.ref, t.status[notice.to])}
            </span>
          </p>
          {notice.kind === "moved" && <p className={`pl-4 text-small ${busy ? "text-ink" : "text-muted"}`}>{busy ? e.busy : e.closedNote}</p>}
        </div>
      )}
      {notice.kind === "moved" && notice.undo && (
        <button
          type="button"
          className="btn relative h-8 shrink-0 self-center px-3 text-small"
          onClick={() => {
            if (notice.undo?.() === "busy") setBusy(true);
            else dismissNotice(notice.id);
          }}
        >
          {e.undo}
          <span aria-hidden className="undo-clock" style={{ animationDuration: `${Math.max(0, notice.until - Date.now())}ms` }} />
        </button>
      )}
      {close}
    </div>
  );
}

/** Which card `s` acts on: the focused card on a board, else the issue open in the panel. */
function targetIssueId(panelIssue: string | null): string | null {
  const focused = document.activeElement instanceof HTMLElement ? document.activeElement.closest(`[${ISSUE_ATTR}]`) : null;
  if (focused) return focused.getAttribute(ISSUE_ATTR);
  return panelIssue && panelIssue !== NEW_ISSUE ? panelIssue : null;
}

/**
 * `s` then 1–5 sets the status of the focused card, or of the issue open in the panel. While waiting
 * for the number a small key guide shows the choices. Matched by key position, so the Korean IME does not matter.
 */
export function StatusKeys() {
  const t = useT();
  const { issueId } = useIssuePanel();
  const [armed, setArmed] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    let timer: ReturnType<typeof setTimeout> | undefined;
    let target: string | null = null;
    let lastG = 0;
    let busyTimer: ReturnType<typeof setTimeout> | undefined;
    const disarm = () => {
      target = null;
      clearTimeout(timer);
      setArmed(null);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.defaultPrevented || e.metaKey || e.ctrlKey || e.altKey || e.isComposing || e.keyCode === 229 || isTyping(e.target)) return;
      if (target) {
        const n = /^Digit([1-5])$/.exec(e.code) ?? /^Numpad([1-5])$/.exec(e.code);
        const id = target;
        disarm();
        if (!n || e.shiftKey) return;
        e.preventDefault();
        e.stopPropagation();
        const issue = knownIssue(id);
        if (issue && editIssue(issue, { status: STATUS_KEYS[Number(n[1]) - 1] }) === "busy") {
          clearTimeout(busyTimer);
          setBusy(true);
          busyTimer = setTimeout(() => setBusy(false), KEY_TIMEOUT_MS);
        }
        return;
      }
      if (e.code === "KeyG") lastG = e.timeStamp;
      // `g s` is the settings screen; leave that `s` to the screen shortcuts.
      if (e.code !== "KeyS" || e.shiftKey || e.timeStamp - lastG < 1200) return;
      const id = targetIssueId(issueId);
      if (!id || !knownIssue(id)) return;
      // Stops the `g s` follow-up from also reading this key.
      e.preventDefault();
      e.stopImmediatePropagation();
      target = id;
      setArmed(id);
      timer = setTimeout(disarm, KEY_TIMEOUT_MS);
    };
    window.addEventListener("keydown", onKey, true);
    window.addEventListener("pointerdown", disarm);
    return () => {
      window.removeEventListener("keydown", onKey, true);
      window.removeEventListener("pointerdown", disarm);
      clearTimeout(timer);
      clearTimeout(busyTimer);
    };
  }, [issueId]);

  if (busy && !armed)
    return (
      <div role="status" className="pointer-events-none fixed inset-x-0 top-[72px] z-50 flex justify-center px-4">
        <p className="notice rounded-item bg-surface px-4 py-2.5 text-small text-ink-soft shadow-elev-2">{t.edit.busy}</p>
      </div>
    );
  if (!armed) return null;
  return (
    <div role="status" className="pointer-events-none fixed inset-x-0 top-[72px] z-50 flex justify-center px-4">
      <p className="notice flex flex-wrap items-center gap-x-3 gap-y-1.5 rounded-item bg-surface px-4 py-2.5 text-small text-ink-soft shadow-elev-2">
        <span className="font-semibold text-ink">{t.edit.statusHint}</span>
        {STATUS_KEYS.map((s, i) => (
          <span key={s} className="inline-flex items-center gap-1.5">
            <Kbd>{i + 1}</Kbd>
            <Dot className={STATUS_DOT[s]} />
            {t.status[s]}
          </span>
        ))}
      </p>
    </div>
  );
}
