"use client";

import Link from "next/link";
import { usePathname, useSearchParams } from "next/navigation";
import { useEffect, useId, useRef, useState } from "react";
import {
  ApiError,
  ISSUES_CHANGED_EVENT,
  NEW_ISSUE_OPTIONS_PATH,
  createIssue,
  newIdempotencyKey,
  useApi,
} from "@/lib/api";
import { type Dict, useFormat, useT } from "@/lib/i18n";
import { STATUS_DOT, labelStyle } from "@/lib/labels";
import { useMe, useSettings } from "@/lib/me";
import type {
  CreateIssueRequest,
  CreatedIssue,
  NewIssueOptions,
  NewIssueStatus,
  PendingIssue,
  Priority,
  Project,
  Size,
} from "@/lib/types";
import { Avatar, Dot, GitHubLink, Kbd } from "./ui";
import type { NewIssuePrefill } from "./useIssuePanel";

const LAST_REPO_KEY = "workboard.lastRepo";
const MAX_LABELS = 20;
const MAX_ASSIGNEES = 10;
const LABEL_FILTER_FROM = 12;
const STATUSES: NewIssueStatus[] = ["todo", "in_progress", "in_review"];
const PRIORITIES: Priority[] = ["urgent", "high", "normal", "low"];
const SIZES: Size[] = ["S", "M", "L"];

type Draft = {
  repositoryId: string;
  title: string;
  body: string;
  labels: string[];
  assignees: number[];
  /** Picked by the person; a column's `+` status is held apart in `prefilledStatus`. */
  status: NewIssueStatus | null;
  priority: Priority | null;
  size: Size | null;
};

const EMPTY: Draft = { repositoryId: "", title: "", body: "", labels: [], assignees: [], status: null, priority: null, size: null };

/** `locked`: GitHub may already have the issue, so only the same request may be sent again. */
type Failure = { lines: readonly string[]; locked: boolean };
type Attempt = { key: string; payload: string; req: CreateIssueRequest };

type Pending = { issue: PendingIssue; unsaved: string[] };

type Kept = {
  scope: string;
  draft: Draft;
  prefilled: number | undefined;
  prefilledStatus: NewIssueStatus | undefined;
  attempt: Attempt | null;
  failure: Failure | null;
  /** A request is on its way; nothing may be sent until it settles. */
  sending: boolean;
  pending: Pending | null;
};

/**
 * Lives outside the form so closing the panel, even mid-request, never throws away what was typed or
 * unlocks a request GitHub may have taken. The request's outcome is written here whether or not the form
 * is still mounted. Kept per tenant and member, and cleared once the issue exists. Column prefills are
 * only kept while locked; otherwise the next `+` brings its own.
 */
let kept: Kept | null = null;

type FormEvent = { type: "created"; issue: CreatedIssue; dropped: string[] } | { type: "kept" };
const listeners = new Set<(e: FormEvent) => void>();
const emit = (e: FormEvent) => listeners.forEach((l) => l(e));

function readLastRepo(): string | null {
  try {
    return window.localStorage.getItem(LAST_REPO_KEY);
  } catch {
    return null;
  }
}

function writeLastRepo(id: string) {
  try {
    window.localStorage.setItem(LAST_REPO_KEY, id);
  } catch {
    // Without storage the form falls back to the project's or the first repository.
  }
}

/**
 * No response, a 5xx (502 may be stored when GitHub could have acted) or a request still running: GitHub may
 * have the issue. 4xx answers are refusals before anything was created.
 */
function failureOf(e: unknown, t: Dict["newIssue"]): Failure {
  if (!(e instanceof ApiError)) return { lines: t.errors.network, locked: true };
  const code = e.code;
  if (code === "github_unavailable") return { lines: e.status === 504 ? t.errors.timeout : t.errors.github_unavailable, locked: true };
  if (code === "request_in_progress") return { lines: t.errors.request_in_progress, locked: true };
  if (
    code === "invalid_request" ||
    code === "repository_archived" ||
    code === "not_found" ||
    code === "github_write_not_granted" ||
    code === "assignee_not_collaborator" ||
    code === "idempotency_key_reused"
  )
    return { lines: t.errors[code], locked: false };
  return { lines: t.errors.unknown, locked: e.status >= 500 };
}

function unknownLabelsOf(e: unknown): string[] {
  if (!(e instanceof ApiError) || e.code !== "invalid_request") return [];
  const list = (e.body as { unknownLabels?: unknown } | null)?.unknownLabels;
  return Array.isArray(list) ? list.filter((x): x is string => typeof x === "string") : [];
}

type Props = {
  prefill: NewIssuePrefill;
  onCreated: (issue: CreatedIssue, droppedNames: string[]) => void;
};

/** The issue panel's new-issue mode: the same 2-column property table the issue will show once it exists. */
export function NewIssueForm({ prefill, onCreated }: Props) {
  const t = useT();
  const n = t.newIssue;
  const fmt = useFormat();
  const settings = useSettings();
  const me = useMe().data;
  const scope = me ? `${me.tenant.id}:${me.member.id}` : "";
  const pathname = usePathname();
  const params = useSearchParams();
  const options = useApi<NewIssueOptions>(NEW_ISSUE_OPTIONS_PATH);
  const projectId = pathname === "/projects" ? params.get("project") : null;
  const projects = useApi<Project[]>(projectId ? "/api/projects" : null);
  const ids = useId();
  const titleRef = useRef<HTMLInputElement>(null);
  const failureRef = useRef<HTMLDivElement>(null);
  const submitRef = useRef<HTMLButtonElement>(null);
  const [saved] = useState(() => (kept?.scope === scope ? kept : null));
  // Locked or still sending: restore exactly what was sent, prefills included.
  const whole = saved && (saved.sending || saved.failure?.locked) ? saved : null;
  const attempt = useRef<Attempt | null>(saved?.attempt ?? null);
  const busyRef = useRef(saved?.sending ?? false);

  const [draft, setDraftState] = useState<Draft>(() =>
    whole ? whole.draft : { ...(saved?.draft ?? EMPTY), status: prefill.status ? null : (saved?.draft.status ?? null) },
  );
  const [prefilled, setPrefilled] = useState<number | undefined>(() => (whole ? whole.prefilled : prefill.assignee));
  const [prefilledStatus, setPrefilledStatus] = useState<NewIssueStatus | undefined>(() =>
    whole ? whole.prefilledStatus : prefill.status,
  );
  const [busy, setBusy] = useState(saved?.sending ?? false);
  const [failure, setFailure] = useState<Failure | null>(whole?.failure ?? null);
  const locked = failure?.locked === true;
  const [titleMissing, setTitleMissing] = useState(false);
  const [pending, setPending] = useState<Pending | null>(saved?.pending ?? null);
  const status = draft.status ?? prefilledStatus ?? "todo";
  const [labelQuery, setLabelQuery] = useState("");
  const [mac, setMac] = useState(false);

  const setDraft = (patch: Partial<Draft>) => setDraftState((d) => ({ ...d, ...patch }));

  useEffect(() => {
    if (busyRef.current || !scope) return;
    kept = { scope, draft, prefilled, prefilledStatus, attempt: attempt.current, failure, sending: false, pending: null };
  }, [scope, draft, prefilled, prefilledStatus, failure]);

  // The outcome of a request lands in `kept`; whichever form is mounted then shows it.
  useEffect(() => {
    const on = (e: FormEvent) => {
      if (e.type === "created") {
        onCreated(e.issue, e.dropped);
        return;
      }
      if (!kept || kept.scope !== scope) return;
      const k = kept;
      attempt.current = k.attempt;
      busyRef.current = k.sending;
      setBusy(k.sending);
      setDraftState(k.draft);
      setPrefilled(k.prefilled);
      setPrefilledStatus(k.prefilledStatus);
      setFailure(k.failure);
      setPending(k.pending);
    };
    listeners.add(on);
    return () => {
      listeners.delete(on);
    };
  }, [scope, onCreated]);

  useEffect(() => {
    if (locked && !busy) submitRef.current?.focus();
  }, [locked, busy]);

  useEffect(() => {
    if (failure) failureRef.current?.scrollIntoView({ block: "nearest" });
  }, [failure]);

  useEffect(() => {
    // A locked form starts on its resend button instead (the effect above).
    if (!titleRef.current?.matches(":disabled")) titleRef.current?.focus();
    setMac(/Mac|iPhone|iPad/.test(navigator.platform));
  }, []);

  const repos = options.data?.repositories ?? [];
  // Until one is picked: the open project's first repository, then the last one used, then the first one.
  const openRepos = repos.filter((r) => !r.archived);
  const projectRepoIds = projects.data?.find((p) => p.id === projectId)?.repositories.map((r) => r.id) ?? [];
  const repo =
    openRepos.find((r) => r.id === draft.repositoryId) ??
    projectRepoIds.map((id) => openRepos.find((r) => r.id === id)).find((r) => r !== undefined) ??
    openRepos.find((r) => r.id === readLastRepo()) ??
    openRepos[0] ??
    null;

  const repoLabels = repo?.labels ?? [];
  const shownLabels = labelQuery.trim()
    ? repoLabels.filter((l) => l.name.toLowerCase().includes(labelQuery.trim().toLowerCase()))
    : repoLabels;

  const toggle = <T,>(list: T[], v: T): T[] => (list.includes(v) ? list.filter((x) => x !== v) : [...list, v]);

  const pickRepo = (id: string) => {
    const next = repos.find((r) => r.id === id);
    const names = new Set(next?.labels.map((l) => l.name.toLowerCase()));
    setDraft({ repositoryId: id, labels: draft.labels.filter((l) => names.has(l.toLowerCase())) });
    setLabelQuery("");
  };

  const assignees = prefilled && !draft.assignees.includes(prefilled) ? [...draft.assignees, prefilled] : draft.assignees;
  const toggleAssignee = (id: number) => {
    if (id === prefilled) {
      setPrefilled(undefined);
      setDraft({ assignees: draft.assignees.filter((x) => x !== id) });
    } else setDraft({ assignees: toggle(draft.assignees, id) });
  };

  const pickedLabels = draft.labels.filter((l) => repoLabels.some((x) => x.name.toLowerCase() === l.toLowerCase()));

  const request = (repositoryId: string): CreateIssueRequest => ({
    repositoryId,
    title: draft.title.trim(),
    ...(draft.body.trim() ? { body: draft.body } : {}),
    ...(pickedLabels.length ? { labels: pickedLabels } : {}),
    ...(assignees.length ? { assigneeGithubUserIds: assignees } : {}),
    // Todo is the default; setting it would pin the status so a linked PR could no longer move it.
    ...(status !== "todo" ? { status } : {}),
    ...(settings.priorityEnabled && draft.priority ? { priority: draft.priority } : {}),
    ...(settings.sizeEnabled && draft.size ? { size: draft.size } : {}),
  });

  const submit = async () => {
    if (busyRef.current) return;
    let req: CreateIssueRequest;
    let key: string;
    if (locked && attempt.current) {
      // Exactly the request GitHub may already have, under the same key.
      ({ req, key } = attempt.current);
    } else {
      if (!repo) return;
      if (!draft.title.trim()) {
        setTitleMissing(true);
        titleRef.current?.focus();
        return;
      }
      req = request(repo.id);
      const payload = JSON.stringify(req);
      key = attempt.current?.payload === payload ? attempt.current.key : newIdempotencyKey();
      attempt.current = { key, payload, req };
    }
    const base = { scope, draft, prefilled, prefilledStatus, attempt: attempt.current, pending: null };
    kept = { ...base, failure: { lines: n.errors.sending, locked: true }, sending: true };
    busyRef.current = true;
    setBusy(true);
    setFailure(null);
    const people = options.data?.assignees ?? [];
    // From here on only `kept` and `emit` are used: the panel may be closed before the answer comes.
    try {
      const result = await createIssue(req, key);
      writeLastRepo(req.repositoryId);
      if (result.kind === "created") {
        kept = null;
        const dropped = result.issue.droppedAssignees.map((id) => {
          const p = people.find((x) => x.githubUserId === id);
          return p ? fmt.person(p) : String(id);
        });
        window.dispatchEvent(new Event(ISSUES_CHANGED_EVENT));
        // Busy stays on: the panel switches to the issue, and nothing may go out again under a new key.
        emit({ type: "created", issue: result.issue, dropped });
        return;
      }
      const unsaved = [
        ...(req.status ? [t.panel.status] : []),
        ...(req.priority ? [t.panel.priority] : []),
        ...(req.size ? [t.panel.size] : []),
      ];
      kept = {
        scope,
        draft: { ...EMPTY, repositoryId: req.repositoryId },
        prefilled: undefined,
        prefilledStatus: undefined,
        attempt: null,
        failure: null,
        sending: false,
        pending: { issue: result.issue, unsaved },
      };
    } catch (e) {
      const unknown = unknownLabelsOf(e);
      if (unknown.length) {
        const drop = new Set(unknown.map((l) => l.toLowerCase()));
        kept = {
          ...base,
          draft: { ...draft, labels: draft.labels.filter((l) => !drop.has(l.toLowerCase())) },
          failure: { lines: n.errors.unknownLabels(unknown.join(", ")), locked: false },
          sending: false,
        };
        options.reload();
      } else {
        kept = { ...base, failure: failureOf(e, n), sending: false };
      }
    }
    emit({ type: "kept" });
  };

  const unlock = () => {
    attempt.current = null;
    setFailure(null);
    requestAnimationFrame(() => titleRef.current?.focus());
  };

  if (pending) {
    const ref = `${pending.issue.repository} #${pending.issue.number}`;
    return (
      <div role="status" className="flex flex-col gap-4 px-6 py-6 max-[639px]:px-4">
        <p className="font-mono text-meta text-muted">{ref}</p>
        <h2 className="text-title font-semibold">{n.pendingTitle}</h2>
        <p className="text-body text-ink-soft">{n.pendingLead}</p>
        {pending.unsaved.length > 0 && (
          <p className="border-l-2 border-ink-soft pl-3 text-small text-ink">{n.pendingFields(pending.unsaved.join(", "))}</p>
        )}
        <div className="flex flex-wrap gap-2">
          <GitHubLink href={pending.issue.htmlUrl} label={t.github.openRef(ref)} className="btn">
            {n.openOnGitHub}
          </GitHubLink>
          <button
            type="button"
            className="btn"
            onClick={() => {
              setPending(null);
              requestAnimationFrame(() => titleRef.current?.focus());
            }}
          >
            {n.another}
          </button>
        </div>
      </div>
    );
  }

  const field = "w-full rounded-control bg-surface px-3 text-ink shadow-[inset_0_0_0_1px_var(--wb-rule-strong)] placeholder:text-muted focus:shadow-[inset_0_0_0_2px_var(--wb-focus)] focus:outline-none";
  const lockedRepo = locked ? repos.find((r) => r.id === attempt.current?.req.repositoryId) : undefined;
  const noRepos = options.data !== null && !repos.some((r) => !r.archived);

  return (
    <form
      aria-labelledby={`${ids}-h`}
      aria-busy={busy}
      noValidate
      className="flex flex-1 flex-col"
      onSubmit={(e) => {
        e.preventDefault();
        void submit();
      }}
      onKeyDown={(e) => {
        if (e.key === "Enter" && (e.metaKey || e.ctrlKey) && !e.nativeEvent.isComposing) {
          e.preventDefault();
          void submit();
        }
      }}
    >
      <div className="flex flex-col gap-6 px-6 py-6 max-[639px]:px-4">
        <header className="flex flex-col gap-1">
          <h2 id={`${ids}-h`} className="text-title font-semibold">
            {n.title}
          </h2>
          <p className="text-small text-muted">{n.lead}</p>
        </header>

        <fieldset disabled={locked} className="contents [&_.seg>button:disabled]:cursor-default [&_.seg>button:disabled]:opacity-50">
        <div className="flex flex-col gap-2">
          <label htmlFor={`${ids}-repo`} className="text-small font-semibold">
            {n.repository}
          </label>
          {options.error ? (
            <p role="alert" className="border-l-2 border-ink-soft pl-3 text-small text-ink">
              {n.optionsFailed}{" "}
              <button type="button" className="link cursor-pointer" onClick={options.reload}>
                {n.retry}
              </button>
            </p>
          ) : noRepos ? (
            <div className="rounded-item border border-dashed border-rule-strong px-4 py-3 text-small text-muted">
              {n.noRepos} {n.noReposFix}{" "}
              <Link href="/settings" className="link">
                {n.toSettings}
              </Link>
            </div>
          ) : (
            <select
              id={`${ids}-repo`}
              value={repo?.id ?? ""}
              onChange={(e) => pickRepo(e.target.value)}
              disabled={!options.data}
              className={`${field} h-10 cursor-pointer font-mono text-small disabled:cursor-default disabled:text-muted`}
            >
              {!options.data && <option value="">{t.common.loading}…</option>}
              {repos
                .filter((r) => !r.archived)
                .map((r) => (
                  <option key={r.id} value={r.id}>
                    {r.fullName}
                  </option>
                ))}
              {repos.some((r) => r.archived) && (
                <optgroup label={n.archived} className="font-sans">
                  {repos
                    .filter((r) => r.archived)
                    .map((r) => (
                      <option key={r.id} value={r.id} disabled>
                        {r.fullName}
                      </option>
                    ))}
                </optgroup>
              )}
            </select>
          )}
        </div>

        <div className="flex flex-col gap-2">
          <label htmlFor={`${ids}-title`} className="text-small font-semibold">
            {n.titleLabel}
          </label>
          <input
            ref={titleRef}
            id={`${ids}-title`}
            value={draft.title}
            maxLength={256}
            required
            aria-required
            aria-invalid={titleMissing || undefined}
            aria-describedby={titleMissing ? `${ids}-title-msg` : undefined}
            onChange={(e) => {
              setDraft({ title: e.target.value });
              if (e.target.value.trim()) setTitleMissing(false);
            }}
            placeholder={n.titlePlaceholder}
            className={`${field} h-12 rounded-item px-3.5 text-heading font-medium`}
          />
          {titleMissing && (
            <p id={`${ids}-title-msg`} className="border-l-2 border-ink-soft pl-3 text-small text-ink">
              {n.titleRequired}
            </p>
          )}
        </div>

        <div className="flex flex-col gap-2">
          <label htmlFor={`${ids}-body`} className="text-small font-semibold">
            {n.body}
          </label>
          <textarea
            id={`${ids}-body`}
            value={draft.body}
            maxLength={60000}
            rows={6}
            onChange={(e) => setDraft({ body: e.target.value })}
            placeholder={n.bodyPlaceholder}
            className={`${field} min-h-32 resize-y rounded-item px-3.5 py-2.5 text-body`}
          />
        </div>

        <dl className="grid grid-cols-[6.5rem_minmax(0,1fr)] overflow-hidden rounded-item text-ui shadow-elev-1 max-[459px]:grid-cols-1 [&>dd]:flex [&>dd]:min-h-11 [&>dd]:flex-wrap [&>dd]:items-center [&>dd]:gap-1.5 [&>dd]:px-3 [&>dd]:py-2 [&>dt]:flex [&>dt]:items-center [&>dt]:border-t [&>dt]:border-rule [&>dt]:px-3 [&>dt]:py-2 [&>dt]:text-small [&>dt]:text-muted [&>dt:first-of-type]:border-t-0 min-[460px]:[&>dd]:border-t min-[460px]:[&>dd]:border-rule min-[460px]:[&>dd:first-of-type]:border-t-0 max-[459px]:[&>dd]:pt-0 max-[459px]:[&>dt]:pb-1">
          <dt id={`${ids}-status`}>{n.status}</dt>
          <dd>
            <div className="seg" role="group" aria-labelledby={`${ids}-status`}>
              {STATUSES.map((s) => (
                <button key={s} type="button" aria-pressed={status === s} onClick={() => {
                    setPrefilledStatus(undefined);
                    setDraft({ status: s });
                  }}>
                  <Dot className={STATUS_DOT[s]} />
                  {t.status[s]}
                </button>
              ))}
            </div>
          </dd>

          <dt id={`${ids}-assignees`}>{n.assignees}</dt>
          <dd>
            <Chips
              labelledBy={`${ids}-assignees`}
              items={(options.data?.assignees ?? []).map((p) => ({
                key: String(p.githubUserId),
                on: assignees.includes(p.githubUserId),
                lead: <Avatar person={p} size="sm" />,
                text: fmt.person(p),
                pick: () => toggleAssignee(p.githubUserId),
              }))}
              full={assignees.length >= MAX_ASSIGNEES}
              fullHint={n.assigneeLimit}
              empty={options.data ? t.common.none : `${t.common.loading}…`}
            />
          </dd>

          <dt id={`${ids}-labels`}>{n.labels}</dt>
          <dd className="flex-col !items-stretch">
            {repoLabels.length > LABEL_FILTER_FROM && (
              <input
                type="search"
                value={labelQuery}
                onChange={(e) => setLabelQuery(e.target.value)}
                placeholder={n.labelFilter}
                aria-label={n.labelFilter}
                className={`${field} h-8 text-small`}
              />
            )}
            <Chips
              labelledBy={`${ids}-labels`}
              className="max-h-40 overflow-y-auto"
              items={shownLabels.map((l) => {
                const style = labelStyle(l.color);
                return {
                  key: l.name,
                  on: draft.labels.some((x) => x.toLowerCase() === l.name.toLowerCase()),
                  lead: (
                    <span
                      aria-hidden
                      className="h-2.5 w-2.5 shrink-0 rounded-mark shadow-[inset_0_0_0_1px_var(--wb-ring)]"
                      style={{ background: style?.background ?? "var(--wb-sunken)" }}
                    />
                  ),
                  text: l.name,
                  pick: () =>
                    setDraft({
                      labels: draft.labels.some((x) => x.toLowerCase() === l.name.toLowerCase())
                        ? draft.labels.filter((x) => x.toLowerCase() !== l.name.toLowerCase())
                        : [...draft.labels, l.name],
                    }),
                };
              })}
              full={pickedLabels.length >= MAX_LABELS}
              fullHint={n.labelLimit}
              empty={!repo ? t.common.none : repoLabels.length ? n.noLabelMatch : n.noLabels}
            />
            {pickedLabels.length > 0 && <span className="text-meta text-muted">{n.selected(pickedLabels.length)}</span>}
          </dd>

          {settings.priorityEnabled && (
            <>
              <dt id={`${ids}-priority`}>{n.priority}</dt>
              <dd>
                <OneOf
                  labelledBy={`${ids}-priority`}
                  values={PRIORITIES}
                  value={draft.priority}
                  name={(v) => t.priority[v]}
                  unset={n.unset}
                  pick={(v) => setDraft({ priority: v })}
                />
              </dd>
            </>
          )}
          {settings.sizeEnabled && (
            <>
              <dt id={`${ids}-size`}>{n.size}</dt>
              <dd>
                <OneOf
                  labelledBy={`${ids}-size`}
                  values={SIZES}
                  value={draft.size}
                  name={(v) => t.size[v]}
                  unset={n.unset}
                  pick={(v) => setDraft({ size: v })}
                />
              </dd>
            </>
          )}
        </dl>
        </fieldset>

        {failure && (
          <div ref={failureRef} role="alert" className="flex scroll-mb-20 flex-col gap-1 rounded-control border-l-2 border-ink-soft bg-surface px-4 py-3 shadow-elev-1">
            <p className="text-ui text-ink">{failure.lines[0]}</p>
            <p className="text-small text-ink-soft">{failure.lines[1]}</p>
            {locked && (
              <>
                <p className="mt-2 flex items-start gap-1.5 text-small font-medium text-ink">
                  <svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" aria-hidden className="mt-[3px] shrink-0">
                    <rect x="3" y="7" width="10" height="7" rx="1.5" />
                    <path d="M5.5 7V5a2.5 2.5 0 0 1 5 0v2" />
                  </svg>
                  {n.lockedNote}
                </p>
                <p className="text-small text-ink-soft">{n.lockedHelp}</p>
                <div className="mt-2 flex flex-wrap items-center gap-2">
                  {lockedRepo && (
                    <GitHubLink href={`https://github.com/${lockedRepo.fullName}/issues`} label={n.checkOnGitHub} className="btn h-8 gap-1.5 px-3 text-small">
                      {n.checkOnGitHub}
                    </GitHubLink>
                  )}
                  <button type="button" className="btn h-8 px-3 text-small" disabled={busy} onClick={unlock}>
                    {n.unlock}
                  </button>
                </div>
              </>
            )}
          </div>
        )}
      </div>

      <div className="sticky bottom-0 mt-auto flex items-center justify-end gap-3 border-t border-rule bg-surface px-6 py-3 max-[639px]:px-4">
        <span className={`hidden items-center gap-1 text-meta text-muted ${locked ? "" : "min-[640px]:inline-flex"}`} aria-hidden>
          <Kbd>{mac ? "⌘" : "Ctrl"}</Kbd>
          <Kbd>Enter</Kbd> {n.submitHint}
        </span>
        <button
          ref={submitRef}
          type="submit"
          className="btn btn-primary"
          disabled={busy || (!locked && !repo)}
          aria-keyshortcuts={mac ? "Meta+Enter" : "Control+Enter"}
        >
          {busy ? `${n.submitting}…` : locked ? n.retrySameButton : n.submit}
        </button>
      </div>
    </form>
  );
}

type ChipItem = { key: string; on: boolean; lead: React.ReactNode; text: string; pick: () => void };

/** Toggle chips: pressed ones carry an ink ring and a check, never colour alone. */
function Chips({
  items,
  labelledBy,
  full,
  fullHint,
  empty,
  className = "",
}: {
  items: ChipItem[];
  labelledBy: string;
  full: boolean;
  fullHint: string;
  empty: string;
  className?: string;
}) {
  if (items.length === 0) return <span className="text-small text-muted">{empty}</span>;
  return (
    <div role="group" aria-labelledby={labelledBy} className={`flex flex-wrap gap-1.5 ${className}`}>
      {items.map((it) => (
        <button
          key={it.key}
          type="button"
          aria-pressed={it.on}
          disabled={!it.on && full}
          title={!it.on && full ? fullHint : undefined}
          onClick={it.pick}
          className={`inline-flex h-7 max-w-[14rem] cursor-pointer items-center gap-1.5 rounded-full px-2.5 text-small transition-shadow duration-[120ms] ease-tool disabled:cursor-default disabled:opacity-50 ${
            it.on
              ? "bg-surface font-medium text-ink shadow-[inset_0_0_0_1.5px_var(--wb-ink)]"
              : "text-ink-soft shadow-[inset_0_0_0_1px_var(--wb-rule-strong)] hover:text-ink hover:shadow-[inset_0_0_0_1px_var(--wb-ink-soft)]"
          }`}
        >
          {it.lead}
          <span className="truncate">{it.text}</span>
          {it.on && (
            <svg width="12" height="12" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden className="shrink-0">
              <path d="m3.5 8.5 3 3 6-7" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
          )}
        </button>
      ))}
    </div>
  );
}

function OneOf<T extends string>({
  values,
  value,
  name,
  unset,
  pick,
  labelledBy,
}: {
  values: T[];
  value: T | null;
  name: (v: T) => string;
  unset: string;
  pick: (v: T | null) => void;
  labelledBy: string;
}) {
  return (
    <div className="seg max-w-full flex-wrap" role="group" aria-labelledby={labelledBy}>
      <button type="button" aria-pressed={value === null} onClick={() => pick(null)}>
        {unset}
      </button>
      {values.map((v) => (
        <button key={v} type="button" aria-pressed={value === v} onClick={() => pick(v)}>
          {name(v)}
        </button>
      ))}
    </div>
  );
}
