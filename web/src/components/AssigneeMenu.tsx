"use client";

import { useEffect, useId, useRef, useState } from "react";
import { useFormat, useT } from "@/lib/i18n";
import type { Assignee, Member } from "@/lib/types";
import { Avatar, Kbd } from "./ui";

export const MAX_ASSIGNEES = 10;

/**
 * The assignee value in the issue panel. Opens a list of the company's members; several can be picked.
 * Arrow keys move, Space or Enter toggles, Ctrl+Enter or the save button sends, Esc closes without saving.
 * Nothing is sent until the person saves, so one change is one GitHub write.
 */
export function AssigneeMenu({
  assignees,
  members,
  membersFailed,
  disabled,
  openSignal,
  onSave,
  onRetryMembers,
  children,
}: {
  assignees: Assignee[];
  members: Member[] | null;
  membersFailed: boolean;
  disabled: boolean;
  /** Bumped by the `a` shortcut to open the list; a value from before mount is ignored. */
  openSignal: number;
  onSave: (ids: number[]) => void;
  onRetryMembers: () => void;
  children: React.ReactNode;
}) {
  const t = useT();
  const a = t.panel.assign;
  const fmt = useFormat();
  const id = useId();
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(0);
  const [picked, setPicked] = useState<number[]>([]);
  const [mac, setMac] = useState(false);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const boxRef = useRef<HTMLDivElement>(null);
  const listRef = useRef<HTMLUListElement>(null);

  // Current assignees who are not (or no longer) members still show, so saving never drops them silently.
  const extra = assignees.filter((x) => !members?.some((m) => m.githubUserId === x.githubUserId));
  const options: Assignee[] = [...(members ?? []), ...extra];
  const full = picked.length >= MAX_ASSIGNEES;
  const current = assignees.map((x) => x.githubUserId);
  const changed = picked.length !== current.length || picked.some((x) => !current.includes(x));
  const valueText = assignees.length ? assignees.map((x) => fmt.person(x)).join(", ") : t.common.unassigned;

  const show = () => {
    if (disabled) return;
    setPicked(current);
    setActive(0);
    setOpen(true);
  };
  const close = (refocus = true) => {
    setOpen(false);
    if (refocus) buttonRef.current?.focus();
  };
  const save = () => {
    close();
    if (changed) onSave([...picked].sort((x, y) => x - y));
  };
  const toggle = (i: number) => {
    const userId = options[i]?.githubUserId;
    if (userId === undefined) return;
    setPicked((p) => (p.includes(userId) ? p.filter((x) => x !== userId) : p.length >= MAX_ASSIGNEES ? p : [...p, userId]));
  };

  // Only a signal raised after this menu mounted opens it, and never while it is already open (that would reset the picks).
  const seenSignal = useRef(openSignal);
  useEffect(() => {
    if (openSignal === seenSignal.current) return;
    const raised = openSignal > seenSignal.current;
    seenSignal.current = openSignal;
    if (raised && !open) show();
    // Only a new signal opens the list.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [openSignal]);

  useEffect(() => {
    if (!open) return;
    const onDown = (e: PointerEvent) => {
      if (!boxRef.current?.contains(e.target as Node) && !buttonRef.current?.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener("pointerdown", onDown);
    return () => document.removeEventListener("pointerdown", onDown);
  }, [open]);

  useEffect(() => {
    if (open) (listRef.current ?? boxRef.current)?.focus();
  }, [open, members]);

  useEffect(() => setMac(/Mac|iPhone|iPad/.test(navigator.platform)), []);

  useEffect(() => {
    if (disabled) setOpen(false);
  }, [disabled]);

  return (
    <span className="relative inline-flex min-w-0 max-w-full">
      <button
        ref={buttonRef}
        type="button"
        disabled={disabled}
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-controls={open ? `${id}-list` : undefined}
        aria-label={t.edit.change(t.panel.assignees, valueText)}
        aria-keyshortcuts="a"
        onClick={() => (open ? close() : show())}
        onKeyDown={(e) => {
          if (e.key === "ArrowDown" || e.key === "ArrowUp") {
            e.preventDefault();
            show();
          }
        }}
        className="prop-trigger min-w-0 flex-wrap py-1"
      >
        {children}
        <svg width="10" height="10" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden className="shrink-0 text-muted">
          <path d="m4 6 4 4 4-4" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      </button>
      {open && (
        <div
          ref={boxRef}
          tabIndex={-1}
          className="menu menu-wide"
          onKeyDown={(e) => {
            if (e.key === "Escape") {
              e.preventDefault();
              e.stopPropagation();
              close();
            } else if (e.key === "Enter" && (e.ctrlKey || e.metaKey) && !e.nativeEvent.isComposing) {
              e.preventDefault();
              save();
            }
          }}
        >
          <p className="px-2 pb-1.5 pt-1 text-meta text-muted">{a.membersOnly}</p>
          {membersFailed && !members ? (
            <p className="flex flex-wrap items-center gap-x-2 border-l-2 border-ink-soft px-2 py-1.5 text-small text-ink">
              {a.loadFailed}
              <button type="button" className="link cursor-pointer" onClick={onRetryMembers}>
                {a.retry}
              </button>
            </p>
          ) : !members ? (
            <p className="px-2 py-1.5 text-small text-muted">{t.common.loading}…</p>
          ) : options.length === 0 ? (
            <p className="px-2 py-1.5 text-small text-muted">{a.noMembers}</p>
          ) : (
            <ul
              ref={listRef}
              id={`${id}-list`}
              role="listbox"
              aria-multiselectable
              aria-label={t.panel.assignees}
              tabIndex={0}
              aria-activedescendant={`${id}-${active}`}
              className="max-h-64 overflow-y-auto outline-none"
              onKeyDown={(e) => {
                if (e.key === "ArrowDown") {
                  e.preventDefault();
                  setActive((i) => (i + 1) % options.length);
                } else if (e.key === "ArrowUp") {
                  e.preventDefault();
                  setActive((i) => (i - 1 + options.length) % options.length);
                } else if (e.key === "Home" || e.key === "End") {
                  e.preventDefault();
                  setActive(e.key === "Home" ? 0 : options.length - 1);
                } else if ((e.key === "Enter" && !e.ctrlKey && !e.metaKey) || e.key === " ") {
                  e.preventDefault();
                  toggle(active);
                }
              }}
            >
              {options.map((o, i) => {
                const on = picked.includes(o.githubUserId);
                const blocked = !on && full;
                return (
                  <li
                    key={o.githubUserId}
                    id={`${id}-${i}`}
                    role="option"
                    aria-selected={on}
                    aria-disabled={blocked || undefined}
                    data-active={i === active || undefined}
                    title={blocked ? a.limit : undefined}
                    onPointerEnter={() => setActive(i)}
                    onClick={() => toggle(i)}
                  >
                    <span aria-hidden data-on={on || undefined} className="pick-box">
                      {on && (
                        <svg width="10" height="10" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2.5">
                          <path d="m3.5 8.5 3 3 6-7" strokeLinecap="round" strokeLinejoin="round" />
                        </svg>
                      )}
                    </span>
                    <Avatar person={o} size="sm" />
                    <span className="min-w-0 flex-1 truncate">{fmt.person(o)}</span>
                    {o.displayName && <span className="shrink-0 font-mono text-meta text-muted">{o.githubLogin}</span>}
                  </li>
                );
              })}
            </ul>
          )}
          <div className="menu-foot">
            <span className="text-meta text-muted">{full ? a.limit : a.picked(picked.length)}</span>
            <button type="button" className="btn btn-primary h-8 px-3 text-small" disabled={!changed || !members} onClick={save}>
              {a.save}
            </button>
          </div>
          <p className="hidden items-center gap-1 px-2 pb-1 text-meta text-muted min-[640px]:flex" aria-hidden>
            <Kbd>{mac ? "⌘" : "Ctrl"}</Kbd>
            <Kbd>Enter</Kbd> {a.saveHint} · <Kbd>Esc</Kbd> {a.cancelHint}
          </p>
        </div>
      )}
    </span>
  );
}
