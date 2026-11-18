"use client";

import { useEffect, useId, useRef, useState } from "react";
import { ApiError, ISSUES_CHANGED_EVENT, addComment, newIdempotencyKey } from "@/lib/api";
import { type Dict, useT } from "@/lib/i18n";
import { useMe } from "@/lib/me";
import type { PostedComment } from "@/lib/types";
import { GitHubLink, Kbd } from "./ui";

const MAX_LENGTH = 60_000;

/** `locked`: GitHub may already have the comment, so only the same text under the same key may be sent again. */
type Failure = { lines: readonly string[]; locked: boolean };
type Attempt = { key: string; body: string };
type Kept = { text: string; attempt: Attempt | null; failure: Failure | null; sending: boolean };

/**
 * Per issue (and tenant and member), outside the component: closing the panel mid-request never loses the text
 * or unlocks a comment GitHub may already have. The answer is written here even when no box is mounted.
 */
const kept = new Map<string, Kept>();
const listeners = new Set<(scope: string, posted: PostedComment | null) => void>();

/**
 * No response, a 5xx or a request still running: GitHub may have posted. 4xx answers are refusals before
 * anything was posted.
 */
function failureOf(e: unknown, t: Dict["panel"]["comment"]): Failure {
  if (!(e instanceof ApiError)) return { lines: t.errors.network, locked: true };
  const code = e.code;
  if (code === "github_unavailable") return { lines: e.status === 504 ? t.errors.timeout : t.errors.github_unavailable, locked: true };
  if (code === "request_in_progress") return { lines: t.errors.request_in_progress, locked: true };
  if (
    code === "invalid_request" ||
    code === "repository_archived" ||
    code === "not_found" ||
    code === "github_write_not_granted" ||
    code === "idempotency_key_reused"
  )
    return { lines: t.errors[code], locked: false };
  return { lines: t.errors.unknown, locked: e.status >= 500 };
}

/** The reply box at the bottom of the panel's conversation. `Ctrl`/`⌘`+`Enter` sends. */
export function CommentBox({
  issueId,
  htmlUrl,
  onPosted,
}: {
  issueId: string;
  htmlUrl: string | null;
  onPosted: (c: PostedComment) => void;
}) {
  const t = useT();
  const c = t.panel.comment;
  const me = useMe().data;
  const scope = me ? `${me.tenant.id}:${me.member.id}:${issueId}` : "";
  const ids = useId();
  const areaRef = useRef<HTMLTextAreaElement>(null);
  const resendRef = useRef<HTMLButtonElement>(null);
  const [state, setState] = useState<Kept>(() => kept.get(scope) ?? { text: "", attempt: null, failure: null, sending: false });
  const [mac, setMac] = useState(false);
  const locked = state.failure?.locked === true;

  useEffect(() => setMac(/Mac|iPhone|iPad/.test(navigator.platform)), []);

  useEffect(() => {
    setState(kept.get(scope) ?? { text: "", attempt: null, failure: null, sending: false });
  }, [scope]);

  useEffect(() => {
    const on = (s: string, posted: PostedComment | null) => {
      if (s !== scope) return;
      setState(kept.get(s) ?? { text: "", attempt: null, failure: null, sending: false });
      if (posted) onPosted(posted);
    };
    listeners.add(on);
    return () => {
      listeners.delete(on);
    };
  }, [scope, onPosted]);

  useEffect(() => {
    if (locked && !state.sending) resendRef.current?.focus();
  }, [locked, state.sending]);

  const update = (next: Kept) => {
    if (scope) {
      if (next.text || next.attempt || next.failure || next.sending) kept.set(scope, next);
      else kept.delete(scope);
    }
    setState(next);
  };

  const submit = async () => {
    if (!scope || state.sending) return;
    let attempt: Attempt;
    if (locked && state.attempt) attempt = state.attempt;
    else {
      const body = state.text.trim();
      if (!body) {
        areaRef.current?.focus();
        return;
      }
      attempt = state.attempt?.body === body ? state.attempt : { key: newIdempotencyKey(), body };
    }
    const sendingScope = scope;
    const base = { text: state.text, attempt };
    update({ ...base, failure: { lines: c.errors.sending, locked: true }, sending: true });
    let posted: PostedComment | null = null;
    // From here on only `kept` and the listeners are used: the panel may close before the answer comes.
    try {
      posted = await addComment(issueId, { body: attempt.body }, attempt.key);
      kept.delete(sendingScope);
      window.dispatchEvent(new Event(ISSUES_CHANGED_EVENT));
    } catch (e) {
      kept.set(sendingScope, { ...base, failure: failureOf(e, c), sending: false });
    }
    listeners.forEach((l) => l(sendingScope, posted));
  };

  const unlock = () => {
    update({ text: state.text, attempt: null, failure: null, sending: false });
    requestAnimationFrame(() => areaRef.current?.focus());
  };

  const failure = state.failure && !state.sending ? state.failure : null;
  const tooLong = state.text.trim().length > MAX_LENGTH;

  return (
    <form
      noValidate
      aria-busy={state.sending}
      className="mt-4 flex flex-col gap-2"
      onSubmit={(e) => {
        e.preventDefault();
        void submit();
      }}
    >
      <label htmlFor={`${ids}-c`} className="sr-only">
        {c.label}
      </label>
      <textarea
        ref={areaRef}
        id={`${ids}-c`}
        value={state.text}
        rows={3}
        readOnly={locked}
        aria-readonly={locked || undefined}
        aria-describedby={`${ids}-note${failure ? ` ${ids}-err` : ""}`}
        aria-invalid={tooLong || undefined}
        placeholder={c.placeholder}
        onChange={(e) => update({ ...state, text: e.target.value })}
        onKeyDown={(e) => {
          if (e.key === "Enter" && (e.ctrlKey || e.metaKey) && !e.nativeEvent.isComposing) {
            e.preventDefault();
            void submit();
          }
        }}
        className={`min-h-24 w-full resize-y rounded-item px-3.5 py-2.5 text-body shadow-[inset_0_0_0_1px_var(--wb-rule-strong)] placeholder:text-muted focus:shadow-[inset_0_0_0_2px_var(--wb-focus)] focus:outline-none ${
          locked ? "cursor-default bg-sunken text-ink-soft" : "bg-surface text-ink"
        }`}
      />

      {failure && (
        <div id={`${ids}-err`} role="alert" className="flex flex-col gap-1 rounded-control border-l-2 border-ink-soft bg-surface px-4 py-3 shadow-elev-1">
          <p className="text-ui text-ink">{failure.lines[0]}</p>
          <p className="text-small text-ink-soft">{failure.lines[1]}</p>
          {locked && (
            <>
              <p className="mt-2 flex items-start gap-1.5 text-small font-medium text-ink">
                <svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" aria-hidden className="mt-[3px] shrink-0">
                  <rect x="3" y="7" width="10" height="7" rx="1.5" />
                  <path d="M5.5 7V5a2.5 2.5 0 0 1 5 0v2" />
                </svg>
                {c.lockedNote}
              </p>
              <p className="text-small text-ink-soft">{c.lockedHelp}</p>
              <div className="mt-2 flex flex-wrap items-center gap-2">
                {htmlUrl && (
                  <GitHubLink href={htmlUrl} label={c.checkOnGitHub} className="btn h-8 gap-1.5 px-3 text-small">
                    {c.checkOnGitHub}
                  </GitHubLink>
                )}
                <button type="button" className="btn h-8 px-3 text-small" onClick={unlock}>
                  {c.unlock}
                </button>
              </div>
            </>
          )}
        </div>
      )}

      <div className="flex flex-wrap items-center justify-between gap-x-3 gap-y-2">
        <p id={`${ids}-note`} className="min-w-0 flex-1 basis-56 text-meta text-muted">
          {tooLong ? c.tooLong : c.asApp}
        </p>
        <div className="flex items-center gap-3">
          <span className={`hidden items-center gap-1 text-meta text-muted ${locked ? "" : "min-[640px]:inline-flex"}`} aria-hidden>
            <Kbd>{mac ? "⌘" : "Ctrl"}</Kbd>
            <Kbd>Enter</Kbd>
          </span>
          <button
            ref={resendRef}
            type="submit"
            className="btn h-9 px-3.5 text-small"
            disabled={state.sending || (!locked && (!state.text.trim() || tooLong))}
            aria-keyshortcuts={mac ? "Meta+Enter" : "Control+Enter"}
          >
            {state.sending ? (
              <>
                <span aria-hidden className="busy-dot" />
                {c.submitting}…
              </>
            ) : locked ? (
              c.resend
            ) : (
              c.submit
            )}
          </button>
        </div>
      </div>
    </form>
  );
}
