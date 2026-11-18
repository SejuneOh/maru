"use client";

import { useEffect, useId, useRef, useState } from "react";
import { useT } from "@/lib/i18n";
import { Kbd } from "./ui";

type Option<T> = { value: T; node: React.ReactNode; hint?: string };

/**
 * A value in the panel's property table that opens a short list to change it. Arrow keys move,
 * Enter picks, Esc closes the list without closing the panel. The picked option carries a check, not only a tint.
 */
export function PropertyMenu<T extends string | null>({
  label,
  valueText,
  value,
  options,
  onPick,
  disabled = false,
  children,
}: {
  label: string;
  valueText: string;
  value: T;
  options: Option<T>[];
  onPick: (v: T) => void;
  disabled?: boolean;
  children: React.ReactNode;
}) {
  const t = useT();
  const id = useId();
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(0);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const listRef = useRef<HTMLUListElement>(null);

  useEffect(() => {
    if (!open) return;
    const onDown = (e: PointerEvent) => {
      if (!listRef.current?.contains(e.target as Node) && !buttonRef.current?.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener("pointerdown", onDown);
    return () => document.removeEventListener("pointerdown", onDown);
  }, [open]);

  useEffect(() => {
    if (open) listRef.current?.focus();
  }, [open]);

  useEffect(() => {
    if (disabled) setOpen(false);
  }, [disabled]);

  const show = () => {
    setActive(Math.max(0, options.findIndex((o) => o.value === value)));
    setOpen(true);
  };
  const close = (refocus = true) => {
    setOpen(false);
    if (refocus) buttonRef.current?.focus();
  };
  const pick = (i: number) => {
    close();
    if (options[i].value !== value) onPick(options[i].value);
  };

  return (
    <span className="relative inline-flex">
      <button
        ref={buttonRef}
        type="button"
        disabled={disabled}
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-controls={open ? `${id}-list` : undefined}
        aria-label={t.edit.change(label, valueText)}
        onClick={() => (open ? close() : show())}
        onKeyDown={(e) => {
          if (e.key === "ArrowDown" || e.key === "ArrowUp") {
            e.preventDefault();
            show();
          }
        }}
        className="prop-trigger"
      >
        {children}
        <svg width="10" height="10" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden className="shrink-0 text-muted">
          <path d="m4 6 4 4 4-4" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      </button>
      {open && (
        <ul
          ref={listRef}
          id={`${id}-list`}
          role="listbox"
          tabIndex={-1}
          aria-label={label}
          aria-activedescendant={`${id}-${active}`}
          className="menu"
          onKeyDown={(e) => {
            if (e.key === "Escape") {
              e.preventDefault();
              e.stopPropagation();
              close();
            } else if (e.key === "ArrowDown") {
              e.preventDefault();
              setActive((a) => (a + 1) % options.length);
            } else if (e.key === "ArrowUp") {
              e.preventDefault();
              setActive((a) => (a - 1 + options.length) % options.length);
            } else if (e.key === "Home" || e.key === "End") {
              e.preventDefault();
              setActive(e.key === "Home" ? 0 : options.length - 1);
            } else if (e.key === "Enter" || e.key === " ") {
              e.preventDefault();
              pick(active);
            } else if (/^[1-9]$/.test(e.key) && options.some((o) => o.hint === e.key)) {
              e.preventDefault();
              e.stopPropagation();
              pick(options.findIndex((o) => o.hint === e.key));
            } else if (e.key === "Tab") {
              close(false);
            }
          }}
        >
          {options.map((o, i) => (
            <li
              key={o.value ?? "__none"}
              id={`${id}-${i}`}
              role="option"
              aria-selected={o.value === value}
              data-active={i === active || undefined}
              onPointerEnter={() => setActive(i)}
              onClick={() => pick(i)}
            >
              <span className="inline-flex min-w-0 flex-1 items-center gap-2">{o.node}</span>
              {o.value === value && (
                <svg width="12" height="12" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden className="shrink-0">
                  <path d="m3.5 8.5 3 3 6-7" strokeLinecap="round" strokeLinejoin="round" />
                </svg>
              )}
              {o.hint && <Kbd className="ml-1">{o.hint}</Kbd>}
            </li>
          ))}
        </ul>
      )}
    </span>
  );
}
