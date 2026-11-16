"use client";

import { useEffect, useId, useRef, useState } from "react";
import { ApiError, ISSUES_CHANGED_EVENT, newIdempotencyKey, saveSprint } from "@/lib/api";
import { showMessage } from "@/lib/boardEdits";
import { type Dict, useT } from "@/lib/i18n";
import { SPRINT_NAME_MAX, nameHasBadChars } from "@/lib/sprints";
import type { Sprint, SprintForm as Values } from "@/lib/types";

type FormError = keyof Dict["plan"]["form"]["errors"];

const SERVER_ERRORS: FormError[] = ["invalid_dates", "name_taken", "invalid_request", "conflict", "not_found"];

function check(v: Values): FormError | null {
  const name = v.name.trim();
  if (!name) return "name_required";
  if (name.length > SPRINT_NAME_MAX) return "name_long";
  if (nameHasBadChars(name)) return "invalid_request";
  if (!v.startDate || !v.endDate) return "dates_required";
  if (v.endDate < v.startDate) return "invalid_dates";
  return null;
}

/**
 * Create or edit a sprint inline. Edits send the `updatedAt` of the newest copy of the sprint on screen,
 * so after a 409 the reloaded list makes the next save go through.
 */
export function SprintForm({
  editing,
  initial,
  onDone,
  onDisabled,
}: {
  /** The sprint as currently loaded; absent when creating. */
  editing: Sprint | null;
  initial: Values;
  onDone: (saved: Sprint | null) => void;
  onDisabled: () => void;
}) {
  const t = useT();
  const f = t.plan.form;
  const ids = useId();
  const [values, setValues] = useState<Values>(initial);
  const [error, setError] = useState<FormError | null>(null);
  const [saving, setSaving] = useState(false);
  const nameRef = useRef<HTMLInputElement>(null);
  /** One key per submitted value set; a resend of the same values after a dropped connection reuses it. */
  const attempt = useRef<{ json: string; key: string } | null>(null);

  useEffect(() => {
    nameRef.current?.focus();
  }, []);

  const set = (patch: Partial<Values>) => {
    setValues((v) => ({ ...v, ...patch }));
    setError(null);
  };

  const submit = async () => {
    if (saving) return;
    const body: Values = { ...values, name: values.name.trim() };
    const problem = check(body);
    if (problem) {
      setError(problem);
      if (problem === "name_required" || problem === "name_long" || problem === "invalid_request") nameRef.current?.focus();
      return;
    }
    const json = JSON.stringify(body);
    if (attempt.current?.json !== json) attempt.current = { json, key: newIdempotencyKey() };
    setSaving(true);
    try {
      const saved = await saveSprint(body, attempt.current.key, editing ? { id: editing.id, updatedAt: editing.updatedAt } : undefined);
      attempt.current = null;
      window.dispatchEvent(new Event(ISSUES_CHANGED_EVENT));
      showMessage({ title: editing ? f.updated(saved.name) : f.created(saved.name) });
      onDone(saved);
    } catch (e) {
      const code = e instanceof ApiError ? e.code : "network";
      if (code === "sprints_disabled") {
        onDisabled();
        return;
      }
      const known = SERVER_ERRORS.find((x) => x === code);
      setError(known ?? (code === "network" || (e instanceof ApiError && e.status >= 500) ? "network" : "unknown"));
      // A conflict or a lost sprint means the list on screen is old.
      if (known === "conflict" || known === "not_found" || known === "name_taken") {
        attempt.current = null;
        window.dispatchEvent(new Event(ISSUES_CHANGED_EVENT));
      }
    } finally {
      setSaving(false);
    }
  };

  const field =
    "w-full rounded-control bg-surface px-3 text-ink shadow-[inset_0_0_0_1px_var(--wb-rule-strong)] placeholder:text-muted focus:shadow-[inset_0_0_0_2px_var(--wb-focus)] focus:outline-none";
  const nameBad = error === "name_required" || error === "name_long" || error === "name_taken" || error === "invalid_request";
  const datesBad = error === "dates_required" || error === "invalid_dates";

  return (
    <form
      noValidate
      aria-labelledby={`${ids}-title`}
      onSubmit={(e) => {
        e.preventDefault();
        void submit();
      }}
      onKeyDown={(e) => {
        if (e.key === "Escape" && !e.nativeEvent.isComposing) {
          e.preventDefault();
          e.stopPropagation();
          onDone(null);
        }
      }}
      className="card reveal flex flex-col gap-4 px-5 py-5"
    >
      <div className="flex flex-col gap-1">
        <h2 id={`${ids}-title`} className="text-heading font-semibold">
          {editing ? f.editTitle(editing.name) : f.newTitle}
        </h2>
        <p className="text-small text-muted">{f.lead}</p>
      </div>
      <div className="grid grid-cols-1 gap-4 min-[640px]:grid-cols-[minmax(0,2fr)_minmax(0,1fr)_minmax(0,1fr)]">
        <div className="flex flex-col gap-2">
          <label htmlFor={`${ids}-name`} className="text-small font-semibold">
            {f.name}
          </label>
          <input
            ref={nameRef}
            id={`${ids}-name`}
            value={values.name}
            maxLength={SPRINT_NAME_MAX + 20}
            autoComplete="off"
            aria-invalid={nameBad || undefined}
            aria-describedby={error ? `${ids}-error` : undefined}
            onChange={(e) => set({ name: e.target.value })}
            placeholder={f.namePlaceholder}
            className={`${field} h-10 text-ui`}
          />
        </div>
        <div className="flex flex-col gap-2">
          <label htmlFor={`${ids}-start`} className="text-small font-semibold">
            {f.start}
          </label>
          <input
            id={`${ids}-start`}
            type="date"
            value={values.startDate}
            aria-invalid={datesBad || undefined}
            aria-describedby={error ? `${ids}-error` : undefined}
            onChange={(e) => set({ startDate: e.target.value })}
            className={`${field} h-10 text-ui tabular-nums`}
          />
        </div>
        <div className="flex flex-col gap-2">
          <label htmlFor={`${ids}-end`} className="text-small font-semibold">
            {f.end}
          </label>
          <input
            id={`${ids}-end`}
            type="date"
            value={values.endDate}
            min={values.startDate || undefined}
            aria-invalid={datesBad || undefined}
            aria-describedby={error ? `${ids}-error` : undefined}
            onChange={(e) => set({ endDate: e.target.value })}
            className={`${field} h-10 text-ui tabular-nums`}
          />
        </div>
      </div>
      {error && (
        <p id={`${ids}-error`} role="alert" className="border-l-2 border-ink-soft pl-3 text-small text-ink">
          {f.errors[error]}
        </p>
      )}
      <div className="flex flex-wrap items-center gap-2">
        <button type="submit" disabled={saving} aria-busy={saving || undefined} className="btn btn-primary">
          {saving ? (
            <>
              <span aria-hidden className="busy-dot" />
              {f.saving}…
            </>
          ) : editing ? (
            f.save
          ) : (
            f.create
          )}
        </button>
        <button type="button" onClick={() => onDone(null)} className="btn">
          {f.cancel}
        </button>
        <span className="ml-auto inline-flex items-center gap-1 text-meta text-muted max-[639px]:hidden">
          <kbd className="kbd">Esc</kbd> {f.cancel}
        </span>
      </div>
    </form>
  );
}
