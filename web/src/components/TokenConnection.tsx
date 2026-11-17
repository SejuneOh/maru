"use client";

import { useEffect, useId, useMemo, useRef, useState } from "react";
import {
  ApiError,
  connectGithubToken,
  deleteGithubToken,
  importOrganization,
  organizationPreviewPath,
  replaceGithubToken,
  updateRepositorySelection,
  useApi,
} from "@/lib/api";
import { type Dict, useFormat, useT } from "@/lib/i18n";
import type {
  GithubConnection,
  ImportOrganizationResult,
  OrganizationPreview,
  RepositorySelectionResult,
  TokenHealth,
} from "@/lib/types";
import { Flag } from "./ui";

type TokenErrors = Dict["tokens"]["errors"];
export type TokenErrorReason = keyof TokenErrors;

/** What went wrong, plus the extras some codes carry. `ssoUrl` is kept only when it points at github.com. */
export type TokenFailure = {
  reason: TokenErrorReason;
  code: string | null;
  ssoUrl: string | null;
  scopes: string[];
  missing: string[];
};

const FINE_GRAINED = "github_pat_";
const CLASSIC = "ghp_";
const LOGIN = /^[A-Za-z0-9](?:[A-Za-z0-9]|-(?=[A-Za-z0-9])){0,38}$/;
const TOKEN_CREATE_URL = "https://github.com/settings/personal-access-tokens/new";
const REPO_PAGE = 12;
const MEMBER_PAGE = 24;

export const STOPPED: ReadonlySet<string | null> = new Set(["expired", "revoked", "invalid", "token_required"]);

/** The SSO link GitHub sends back, only when it is an https page on github.com. */
export function githubSsoUrl(url: unknown): string | null {
  if (typeof url !== "string") return null;
  try {
    const u = new URL(url);
    return u.protocol === "https:" && u.hostname === "github.com" ? u.href : null;
  } catch {
    return null;
  }
}

function strings(v: unknown): string[] {
  return Array.isArray(v) ? v.filter((x): x is string => typeof x === "string") : [];
}

export function tokenFailureOf(e: unknown, known: TokenErrors): TokenFailure {
  if (!(e instanceof ApiError)) return { reason: "network", code: null, ssoUrl: null, scopes: [], missing: [] };
  const body = (e.body ?? {}) as { ssoUrl?: unknown; scopes?: unknown; missing?: unknown };
  const reason = e.code && e.code in known ? (e.code as TokenErrorReason) : "unknown";
  return {
    reason,
    code: e.code ?? String(e.status),
    ssoUrl: githubSsoUrl(body.ssoUrl),
    scopes: strings(body.scopes),
    missing: strings(body.missing),
  };
}

/** Error rule of design.md: no accent, a 2px ink-soft line, what happened and how to fix it. */
export function TokenErrorBox({ failure, className = "" }: { failure: TokenFailure; className?: string }) {
  const t = useT();
  const tt = t.tokens;
  const [what, fix] = tt.errors[failure.reason];
  return (
    <div role="alert" className={`rounded-control border-l-2 border-ink-soft bg-surface px-4 py-3 ${className}`}>
      <p className="text-ui text-ink">{what}</p>
      <p className="mt-1 text-small text-ink-soft">{fix}</p>
      {failure.scopes.length > 0 && (
        <p className="mt-2 text-small text-ink-soft">
          {tt.scopesFound} <span className="font-mono text-meta text-ink">{failure.scopes.join(", ")}</span>
        </p>
      )}
      {failure.missing.length > 0 && (
        <p className="mt-2 text-small text-ink-soft">
          {tt.scopesMissing} <span className="font-mono text-meta text-ink">{failure.missing.join(", ")}</span>
        </p>
      )}
      {failure.reason === "sso_not_authorized" && failure.ssoUrl && (
        <p className="mt-2 text-small">
          <a href={failure.ssoUrl} target="_blank" rel="noopener noreferrer" className="link">
            {tt.ssoLink}
          </a>
        </p>
      )}
      {failure.code && (
        <p className="mt-2 text-meta text-muted">
          {tt.code} <span className="font-mono">{failure.code}</span>
        </p>
      )}
    </div>
  );
}

type Health = Pick<TokenHealth, "tokenStatus" | "tokenExpiresAt" | "tokenExpiresSoon" | "tokenDaysLeft" | "syncPausedUntil">;

/** Lower is more urgent: a stopped connection, then one about to expire, then a rate-limit pause. */
export function urgency(h: Health): number {
  if (STOPPED.has(h.tokenStatus)) return 0;
  if (h.tokenExpiresSoon) return 1;
  if (h.syncPausedUntil) return 2;
  return 3;
}

/** "연결 멈춤" or "곧 만료" (accent, needs someone) or "동기화 쉬는 중" (neutral, clears itself). */
export function TokenMarks({ health: h }: { health: Health }) {
  const t = useT();
  const tt = t.tokens;
  const fmt = useFormat();
  if (STOPPED.has(h.tokenStatus)) {
    return (
      <span className="inline-flex flex-wrap items-center gap-x-2 gap-y-1">
        <Flag>{tt.stopped}</Flag>
        <span className="font-semibold text-ink-soft">{tt.status[h.tokenStatus as keyof typeof tt.status]}</span>
      </span>
    );
  }
  if (h.tokenExpiresSoon) {
    return (
      <span className="inline-flex flex-wrap items-center gap-x-2 gap-y-1">
        <Flag>{tt.expiresSoon}</Flag>
        {h.tokenDaysLeft !== null && <span className="font-semibold text-ink-soft tabular-nums">{tt.daysLeft(h.tokenDaysLeft)}</span>}
      </span>
    );
  }
  if (h.syncPausedUntil) {
    return (
      <span className="inline-flex flex-wrap items-center gap-x-2 gap-y-1" title={tt.pausedHint}>
        <span className="tag">{tt.paused}</span>
        <span className="text-muted tabular-nums">{tt.pausedUntil(fmt.time(h.syncPausedUntil))}</span>
      </span>
    );
  }
  return null;
}

/** The second line of a token connection row: marks, the masked token and its expiry, and what to do when stopped. */
export function TokenLine({ connection: c, isAdmin }: { connection: GithubConnection; isAdmin: boolean }) {
  const t = useT();
  const tt = t.tokens;
  const fmt = useFormat();
  if (c.tokenStatus === null) return null;
  const health: Health = c as Health;
  const stopped = STOPPED.has(c.tokenStatus);
  return (
    <div className="mt-2 flex flex-col gap-1.5 pl-[26px] text-small">
      <p className="flex flex-wrap items-center gap-x-3 gap-y-1.5">
        <TokenMarks health={health} />
        {c.tokenLast4 && <span className="secret-value text-meta text-ink-soft">{tt.masked(c.tokenLast4)}</span>}
        {c.tokenStatus !== "token_required" && (
          <span className="tabular-nums text-muted">{c.tokenExpiresAt ? tt.expiresOn(fmt.date(c.tokenExpiresAt)) : tt.noExpiry}</span>
        )}
      </p>
      {stopped && (
        <p className="text-ink-soft">
          {tt.statusHint[c.tokenStatus as keyof typeof tt.statusHint]}
          {!isAdmin && <span className="text-muted"> {tt.memberHint}</span>}
        </p>
      )}
      {!stopped && c.syncPausedUntil && <p className="text-muted">{tt.pausedHint}</p>}
    </div>
  );
}

function CheckIcon() {
  return (
    <svg width="10" height="10" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2.5" aria-hidden>
      <path d="m3.5 8.5 3 3 6-7" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}

function PickBox({ on }: { on: boolean }) {
  return (
    <span aria-hidden data-on={on || undefined} className="pick-box">
      {on && <CheckIcon />}
    </span>
  );
}

/** 1 토큰 넣기 → 2 조직 고르기 → 3 가져올 것 확인. */
function Steps({ current }: { current: 1 | 2 | 3 }) {
  const t = useT();
  const tt = t.tokens;
  const names = [tt.steps.token, tt.steps.org, tt.steps.pick];
  return (
    <ol aria-label={tt.steps.label} className="flex flex-wrap items-center gap-x-2 gap-y-2 text-small">
      {names.map((name, i) => {
        const n = i + 1;
        const done = n < current;
        const now = n === current;
        return (
          <li key={name} className="flex items-center gap-2" aria-current={now ? "step" : undefined}>
            {i > 0 && <span aria-hidden className="h-px w-4 bg-rule-strong max-[479px]:w-2" />}
            <span
              aria-hidden
              className={`grid h-5 w-5 shrink-0 place-items-center rounded-full font-mono text-meta ${
                now ? "bg-ink text-on-ink" : done ? "bg-sunken text-ink" : "text-muted shadow-[inset_0_0_0_1px_var(--wb-rule-strong)]"
              }`}
            >
              {done ? <CheckIcon /> : n}
            </span>
            <span className={now ? "font-semibold text-ink" : done ? "text-ink-soft" : "text-muted"}>{name}</span>
            {(now || done) && <span className="sr-only">({now ? tt.stepNow : tt.stepDone})</span>}
          </li>
        );
      })}
    </ol>
  );
}

const FIELD =
  "h-10 w-full min-w-0 rounded-item bg-surface px-3 font-mono text-ui text-ink placeholder:font-sans placeholder:text-muted focus:shadow-[inset_0_0_0_2px_var(--wb-focus)] focus:outline-none";
const FIELD_RING = "shadow-[inset_0_0_0_1px_var(--wb-rule-strong)]";
const FIELD_BAD = "shadow-[inset_0_0_0_1px_var(--wb-ink-soft)]";

/** Token field that never holds the value in React state; the caller reads and clears it on submit. */
function SecretInput({
  id,
  inputRef,
  label,
  describedBy,
  invalid,
  onPresence,
}: {
  id: string;
  inputRef: React.RefObject<HTMLInputElement | null>;
  label: string;
  describedBy: string;
  invalid: boolean;
  onPresence?: (present: boolean) => void;
}) {
  const t = useT();
  return (
    <input
      ref={inputRef}
      id={id}
      type="password"
      aria-label={label}
      autoComplete="off"
      autoCapitalize="none"
      autoCorrect="off"
      spellCheck={false}
      data-1p-ignore
      data-lpignore="true"
      placeholder={t.tokens.tokenPlaceholder}
      aria-invalid={invalid || undefined}
      aria-describedby={describedBy}
      onInput={(e) => onPresence?.(e.currentTarget.value.length > 0)}
      className={`${FIELD} secret-value ${invalid ? FIELD_BAD : FIELD_RING}`}
    />
  );
}

/** Reads the token out of the field and empties it, so it never outlives the submit. */
function takeToken(input: HTMLInputElement | null): string {
  const value = input?.value.trim() ?? "";
  if (input) input.value = "";
  return value;
}

const ORG_ERRORS: ReadonlySet<TokenErrorReason> = new Set(["not_org_member", "org_membership_inactive", "account_connected"]);

/**
 * Admin-only, inside the connections card. Steps 1 and 2 are one form so the token is sent with the organization
 * in a single request and never kept between steps; step 3 picks what to import.
 */
export function TokenConnectFlow({ onClose, onConnected }: { onClose: () => void; onConnected: (c: GithubConnection) => void }) {
  const t = useT();
  const tt = t.tokens;
  const id = useId();
  const heading = useRef<HTMLHeadingElement>(null);
  const tokenRef = useRef<HTMLInputElement>(null);
  const orgRef = useRef<HTMLInputElement>(null);
  const [hasToken, setHasToken] = useState(false);
  const [org, setOrg] = useState("");
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<TokenFailure | null>(null);
  const [local, setLocal] = useState<"needToken" | "needOrg" | null>(null);
  const [connected, setConnected] = useState<GithubConnection | null>(null);

  useEffect(() => {
    tokenRef.current?.focus();
  }, []);

  useEffect(() => {
    if (connected) heading.current?.focus();
  }, [connected]);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (busy) return;
    const organization = org.trim().replace(/^@/, "");
    setFailure(null);
    setLocal(null);
    if (!(tokenRef.current?.value.trim() ?? "")) {
      setLocal("needToken");
      tokenRef.current?.focus();
      return;
    }
    if (!LOGIN.test(organization)) {
      setLocal("needOrg");
      orgRef.current?.focus();
      return;
    }
    const token = takeToken(tokenRef.current);
    setHasToken(false);
    if (!token.startsWith(FINE_GRAINED) && !token.startsWith(CLASSIC)) {
      setFailure({ reason: "unsupported_token", code: null, ssoUrl: null, scopes: [], missing: [] });
      tokenRef.current?.focus();
      return;
    }
    setBusy(true);
    try {
      const c = await connectGithubToken(token, organization);
      setOrg("");
      setConnected(c);
      onConnected(c);
    } catch (err) {
      const f = tokenFailureOf(err, tt.errors);
      setFailure(f);
      (ORG_ERRORS.has(f.reason) ? orgRef : tokenRef).current?.focus();
    } finally {
      setBusy(false);
    }
  };

  const step: 1 | 2 | 3 = connected ? 3 : hasToken ? 2 : 1;
  const tokenBad = local === "needToken" || (failure !== null && !ORG_ERRORS.has(failure.reason) && failure.reason !== "network");
  const orgBad = local === "needOrg" || (failure !== null && ORG_ERRORS.has(failure.reason));

  return (
    <section aria-labelledby={`${id}-title`} className="flex flex-col gap-4">
      <div className="flex items-start gap-3">
        <div className="min-w-0 flex-1">
          <h3 ref={heading} tabIndex={-1} id={`${id}-title`} className="text-heading font-semibold focus:outline-none">
            {connected ? tt.saved(connected.accountLogin) : tt.flowTitle}
          </h3>
          {!connected && <p className="mt-1 text-small text-ink-soft">{tt.flowLead}</p>}
        </div>
        <button type="button" className="btn h-8 shrink-0 px-3 text-small" onClick={onClose} disabled={busy}>
          {tt.close}
        </button>
      </div>
      <Steps current={step} />

      {connected ? (
        <>
          <div className="secret flex flex-wrap items-center gap-x-3 gap-y-1">
            <span className="text-small font-semibold text-ink">{tt.savedSecret}</span>
            {connected.tokenLast4 && <span className="secret-value text-ui">{tt.masked(connected.tokenLast4)}</span>}
            <span className="w-full text-small text-ink-soft">{tt.savedNote}</span>
          </div>
          <OrganizationImport connection={connected} fresh onClose={onClose} />
        </>
      ) : (
        <form method="post" onSubmit={submit} noValidate className="flex flex-col gap-5" aria-busy={busy}>
          <div className="grid gap-2">
            <label htmlFor={`${id}-token`} className="flex items-center gap-2 text-small font-semibold text-ink">
              <span aria-hidden className="font-mono text-meta text-muted">
                1
              </span>
              {tt.tokenLabel}
            </label>
            <div className="secret grid gap-3">
              <SecretInput
                id={`${id}-token`}
                inputRef={tokenRef}
                label={tt.tokenLabel}
                describedBy={`${id}-secret ${id}-perms`}
                invalid={tokenBad}
                onPresence={setHasToken}
              />
              <p id={`${id}-secret`} className="text-small text-ink-soft">
                {tt.secretNote}
              </p>
            </div>
            <div id={`${id}-perms`} className="grid gap-2 text-small text-ink-soft">
              <p>
                {tt.recommend}{" "}
                <a href={TOKEN_CREATE_URL} target="_blank" rel="noopener noreferrer" className="link whitespace-nowrap">
                  {tt.createLink}
                </a>
              </p>
              <div>
                <p className="font-semibold text-ink-soft">{tt.permsTitle}</p>
                <dl className="mt-1 grid grid-cols-[minmax(0,max-content)_1fr] gap-x-4 gap-y-0.5">
                  {tt.perms.map(([name, level]) => (
                    <div key={name} className="contents">
                      <dt className="font-mono text-meta leading-5 text-ink">{name}</dt>
                      <dd className="text-small">{level}</dd>
                    </div>
                  ))}
                </dl>
              </div>
              <p className="text-muted">{tt.classic}</p>
            </div>
          </div>

          <div className="grid gap-2">
            <label htmlFor={`${id}-org`} className="flex items-center gap-2 text-small font-semibold text-ink">
              <span aria-hidden className="font-mono text-meta text-muted">
                2
              </span>
              {tt.orgLabel}
            </label>
            <div className="relative max-w-[360px]">
              <span aria-hidden className="pointer-events-none absolute inset-y-0 left-3 grid place-items-center font-mono text-ui text-muted">
                @
              </span>
              <input
                ref={orgRef}
                id={`${id}-org`}
                value={org}
                onChange={(e) => {
                  setOrg(e.target.value);
                  if (local === "needOrg") setLocal(null);
                }}
                autoComplete="off"
                autoCapitalize="none"
                spellCheck={false}
                placeholder={tt.orgPlaceholder}
                aria-invalid={orgBad || undefined}
                aria-describedby={`${id}-org-hint`}
                className={`${FIELD} pl-7 ${orgBad ? FIELD_BAD : FIELD_RING}`}
              />
            </div>
            <p id={`${id}-org-hint`} className="text-small text-muted">
              {tt.orgHint}
            </p>
          </div>

          {local && (
            <p role="alert" className="border-l-2 border-ink-soft pl-3 text-small text-ink">
              {tt[local]}
            </p>
          )}
          {failure && <TokenErrorBox failure={failure} />}

          <div className="flex flex-wrap items-center gap-3">
            <button type="submit" className="btn btn-primary" disabled={busy} aria-busy={busy}>
              {busy && <span aria-hidden className="busy-dot" />}
              {busy ? `${tt.submitting}…` : tt.submit}
            </button>
          </div>
        </form>
      )}
    </section>
  );
}

type SaveResult = { kind: "import"; result: ImportOrganizationResult } | { kind: "selection"; result: RepositorySelectionResult };

/**
 * Step 3, and later "가져올 것 고치기": repositories to sync, optional team projects, and the members for reference.
 * With teams chosen it imports (at least one repository); otherwise a later change only replaces the selection.
 */
export function OrganizationImport({
  connection,
  fresh = false,
  onClose,
  onSaved,
}: {
  connection: GithubConnection;
  fresh?: boolean;
  onClose: () => void;
  onSaved?: () => void;
}) {
  const t = useT();
  const tt = t.tokens;
  const id = useId();
  const preview = useApi<OrganizationPreview>(organizationPreviewPath(connection.id));
  const data = preview.data;
  const [picked, setPicked] = useState<Set<number> | null>(null);
  const [teams, setTeams] = useState<Set<string>>(new Set());
  const [filter, setFilter] = useState("");
  const [allRepos, setAllRepos] = useState(false);
  const [allMembers, setAllMembers] = useState(false);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<"needOne" | "needOneForTeams" | null>(null);
  const [failure, setFailure] = useState<TokenFailure | null>(null);
  const [saved, setSaved] = useState<SaveResult | null>(null);
  const resultRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (data && picked === null) setPicked(new Set(data.repositories.filter((r) => r.selected).map((r) => r.id)));
  }, [data, picked]);

  useEffect(() => {
    if (saved) resultRef.current?.focus();
  }, [saved]);

  const repos = useMemo(() => {
    if (!data) return [];
    const q = filter.trim().toLowerCase();
    const list = q ? data.repositories.filter((r) => r.name.toLowerCase().includes(q)) : data.repositories;
    return [...list].sort((a, b) => Number(a.archived) - Number(b.archived) || a.name.localeCompare(b.name));
  }, [data, filter]);

  const loadFailure = preview.error ? tokenFailureOf(preview.error, tt.errors) : null;
  const chosen = picked ?? new Set<number>();
  const visible = allRepos || filter ? repos : repos.slice(0, REPO_PAGE);

  const toggle = (rid: number) => {
    setProblem(null);
    setPicked((prev) => {
      const next = new Set(prev ?? []);
      if (next.has(rid)) next.delete(rid);
      else next.add(rid);
      return next;
    });
  };
  const toggleTeam = (slug: string) => {
    setProblem(null);
    setTeams((prev) => {
      const next = new Set(prev);
      if (next.has(slug)) next.delete(slug);
      else next.add(slug);
      return next;
    });
  };

  const save = async () => {
    if (busy) return;
    setFailure(null);
    const ids = [...chosen];
    const importing = fresh || teams.size > 0;
    if (importing && ids.length === 0) {
      setProblem(teams.size > 0 ? "needOneForTeams" : "needOne");
      return;
    }
    setBusy(true);
    try {
      if (importing) {
        const result = await importOrganization(connection.id, { repositories: ids, teamProjects: teams.size > 0 ? [...teams] : undefined });
        setSaved({ kind: "import", result });
      } else {
        const result = await updateRepositorySelection(connection.id, ids);
        setSaved({ kind: "selection", result });
      }
      onSaved?.();
    } catch (e) {
      setFailure(tokenFailureOf(e, tt.errors));
    } finally {
      setBusy(false);
    }
  };

  const titleId = `${id}-import`;
  const onBoard = data?.members.filter((m) => m.onBoard) ?? [];
  const offBoard = data?.members.filter((m) => !m.onBoard) ?? [];

  return (
    <section aria-labelledby={titleId} className="flex flex-col gap-4">
      <div>
        <h3 id={titleId} className="text-heading font-semibold">
          {tt.importTitle(data?.organization.login ?? connection.accountLogin)}
        </h3>
        <p className="mt-1 text-small text-ink-soft">{tt.importLead}</p>
      </div>

      {loadFailure ? (
        <TokenErrorBox failure={loadFailure} />
      ) : !data ? (
        <p className="flex items-center gap-2 text-ui text-muted" role="status">
          <span aria-hidden className="busy-dot" />
          {tt.importLoading}…
        </p>
      ) : saved ? (
        <div ref={resultRef} tabIndex={-1} role="status" className="flex flex-col gap-3 focus:outline-none">
          <p className="flex items-center gap-2.5 text-ui">
            <span aria-hidden className="h-2 w-2 shrink-0 rounded-full bg-ink" />
            <span className="font-medium text-ink">{tt.resultSelection(saved.result.selected, saved.result.deselected)}</span>
          </p>
          {saved.kind === "import" && (
            <>
              {saved.result.projects.length > 0 && (
                <div className="text-small text-ink-soft">
                  <p>{tt.resultProjects(saved.result.projects.length)}</p>
                  <ul className="mt-1 flex flex-wrap gap-2">
                    {saved.result.projects.map((p) => (
                      <li key={p.id} className="tag">
                        {p.name} <span className="font-mono text-muted">{p.repositoryCount}</span>
                      </li>
                    ))}
                  </ul>
                </div>
              )}
              {saved.result.skippedTeams.length > 0 && (
                <div className="text-small">
                  <p className="font-semibold text-ink-soft">{tt.skipped}</p>
                  <ul className="mt-1 grid gap-1">
                    {saved.result.skippedTeams.map((s) => (
                      <li key={s.slug} className="flex flex-wrap gap-x-2">
                        <span className="font-mono text-meta leading-5 text-ink">{s.slug}</span>
                        <span className="text-muted">{tt.skipReason[s.reason] ?? s.reason}</span>
                      </li>
                    ))}
                  </ul>
                </div>
              )}
              <p className="text-small text-muted">{tt.resultMembers(saved.result.membersReferenced)}</p>
            </>
          )}
          <p className="text-small text-ink-soft">{tt.resultNext}</p>
          <div>
            <button type="button" className="btn" onClick={onClose}>
              {tt.done}
            </button>
          </div>
        </div>
      ) : (
        <>
          <p className="flex flex-wrap gap-x-3 gap-y-1 text-small text-ink-soft tabular-nums">
            <span>{tt.countRepos(data.counts.repositories)}</span>
            <span aria-hidden className="text-faint">·</span>
            <span>{tt.countTeams(data.counts.teams)}</span>
            <span aria-hidden className="text-faint">·</span>
            <span>{tt.countMembers(data.counts.members)}</span>
            <span aria-hidden className="text-faint">·</span>
            <span>{tt.countLabels(data.counts.labels)}</span>
            <span aria-hidden className="text-faint">·</span>
            <span>{tt.countMilestones(data.counts.milestones)}</span>
          </p>

          <fieldset className="min-w-0 rounded-item shadow-[inset_0_0_0_1px_var(--wb-rule)]">
            <legend className="sr-only">{tt.repos}</legend>
            <div className="flex flex-wrap items-center gap-x-3 gap-y-2 px-4 pb-2 pt-3">
              <p aria-hidden className="text-small font-semibold text-ink-soft">
                {tt.repos}
              </p>
              <p role="status" className="font-mono text-meta text-muted tabular-nums">
                {tt.picked(chosen.size, data.repositories.length)}
              </p>
              <div className="ml-auto flex gap-3">
                <button
                  type="button"
                  className="link text-small"
                  onClick={() => {
                    setProblem(null);
                    setPicked(new Set(data.repositories.filter((r) => !r.archived).map((r) => r.id)));
                  }}
                >
                  {tt.selectAll}
                </button>
                <button type="button" className="link text-small" onClick={() => setPicked(new Set())}>
                  {tt.selectNone}
                </button>
              </div>
            </div>
            {data.repositories.length > REPO_PAGE && (
              <div className="px-4 pb-2">
                <input
                  type="search"
                  value={filter}
                  onChange={(e) => setFilter(e.target.value)}
                  placeholder={tt.filter}
                  aria-label={tt.filter}
                  className={`${FIELD} h-9 font-sans text-small ${FIELD_RING}`}
                />
              </div>
            )}
            {data.repositories.length === 0 ? (
              <p className="px-4 pb-4 text-small text-muted">{tt.noRepos}</p>
            ) : repos.length === 0 ? (
              <p className="px-4 pb-4 text-small text-muted">{tt.noMatch}</p>
            ) : (
              <ul className="pb-1">
                {visible.map((r) => {
                  const on = chosen.has(r.id);
                  return (
                    <li key={r.id} className="relative after:absolute after:inset-x-4 after:top-0 after:h-px after:bg-rule first:after:hidden">
                      <button
                        type="button"
                        role="checkbox"
                        aria-checked={on}
                        onClick={() => toggle(r.id)}
                        className="flex w-full cursor-pointer items-start gap-3 px-4 py-2.5 text-left hover:bg-hover"
                      >
                        <span className="pt-[3px]">
                          <PickBox on={on} />
                        </span>
                        <span className="flex min-w-0 flex-1 flex-col gap-0.5">
                          <span className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1">
                            <span className={`truncate font-mono text-ui ${r.archived ? "text-muted" : "text-ink"}`}>{r.name}</span>
                            {r.private && <span className="tag">{tt.private}</span>}
                            {r.archived && <span className="tag">{tt.archived}</span>}
                          </span>
                          {(r.description || r.labelCount !== null) && (
                            <span className="flex min-w-0 flex-wrap gap-x-2 text-small text-muted">
                              {r.description && <span className="min-w-0 truncate">{r.description}</span>}
                              {r.labelCount !== null && r.milestoneCount !== null && (
                                <span className="whitespace-nowrap tabular-nums">{tt.repoMeta(r.labelCount, r.milestoneCount)}</span>
                              )}
                            </span>
                          )}
                        </span>
                      </button>
                    </li>
                  );
                })}
              </ul>
            )}
            {!filter && repos.length > REPO_PAGE && (
              <div className="px-4 pb-3 pt-1">
                <button type="button" className="link text-small" aria-expanded={allRepos} onClick={() => setAllRepos((v) => !v)}>
                  {allRepos ? tt.showLess : tt.showMore(repos.length - REPO_PAGE)}
                </button>
              </div>
            )}
            {(data.truncated.repositories || data.truncated.counts) && (
              <div className="grid gap-1 px-4 pb-3 text-small text-muted">
                {data.truncated.repositories && <p>{tt.truncatedRepos}</p>}
                {data.truncated.counts && <p>{tt.truncatedCounts(data.counts.countedRepositories)}</p>}
              </div>
            )}
          </fieldset>

          <fieldset className="min-w-0">
            <legend className="text-small font-semibold text-ink-soft">{tt.teams}</legend>
            <p className="mt-1 text-small text-muted">{tt.teamsHint}</p>
            {data.teams.length === 0 ? (
              <p className="mt-2 text-small text-muted">{tt.noTeams}</p>
            ) : (
              <ul className="mt-2 flex flex-wrap gap-2">
                {data.teams.map((team) => {
                  const on = teams.has(team.slug);
                  return (
                    <li key={team.id}>
                      <button
                        type="button"
                        aria-pressed={on}
                        onClick={() => toggleTeam(team.slug)}
                        title={team.description ?? undefined}
                        className={`inline-flex h-8 cursor-pointer items-center gap-2 rounded-full px-3 text-small hover:bg-hover ${
                          on ? "font-medium text-ink shadow-[inset_0_0_0_1.5px_var(--wb-ink)]" : "text-ink-soft shadow-[inset_0_0_0_1px_var(--wb-rule-strong)]"
                        }`}
                      >
                        {on && <CheckIcon />}
                        {team.name}
                      </button>
                    </li>
                  );
                })}
              </ul>
            )}
            {data.truncated.teams && <p className="mt-2 text-small text-muted">{tt.truncatedTeams}</p>}
          </fieldset>

          <div className="min-w-0">
            <p className="text-small font-semibold text-ink-soft">{tt.members}</p>
            <p className="mt-1 text-small text-muted">{tt.membersHint}</p>
            <MemberGroup label={tt.onBoard(onBoard.length)} logins={onBoard.map((m) => m.login)} all={allMembers} />
            <MemberGroup label={tt.notOnBoard(offBoard.length)} logins={offBoard.map((m) => m.login)} all={allMembers} muted />
            {offBoard.length + onBoard.length > MEMBER_PAGE && (
              <button type="button" className="link mt-2 text-small" aria-expanded={allMembers} onClick={() => setAllMembers((v) => !v)}>
                {allMembers ? tt.showLess : tt.showAllMembers}
              </button>
            )}
            {data.truncated.members && <p className="mt-2 text-small text-muted">{tt.truncatedMembers}</p>}
          </div>

          {problem && (
            <p role="alert" className="border-l-2 border-ink-soft pl-3 text-small text-ink">
              {tt[problem]}
            </p>
          )}
          {failure && <TokenErrorBox failure={failure} />}

          <div className="flex flex-wrap items-center gap-3">
            <button type="button" className="btn btn-primary" onClick={() => void save()} disabled={busy} aria-busy={busy}>
              {busy && <span aria-hidden className="busy-dot" />}
              {busy ? `${tt.saving}…` : fresh ? tt.submitImport(chosen.size) : tt.submitSave}
            </button>
            <button type="button" className="btn" onClick={onClose} disabled={busy}>
              {fresh ? tt.later : tt.cancel}
            </button>
          </div>
        </>
      )}
    </section>
  );
}

function MemberGroup({ label, logins, all, muted = false }: { label: string; logins: string[]; all: boolean; muted?: boolean }) {
  const t = useT();
  if (logins.length === 0) return null;
  const shown = all ? logins : logins.slice(0, MEMBER_PAGE);
  return (
    <div className="mt-3">
      <p className="text-small text-ink-soft tabular-nums">{label}</p>
      <ul className="mt-1.5 flex flex-wrap gap-1.5">
        {shown.map((login) => (
          <li key={login} className={`tag font-mono ${muted ? "text-muted" : ""}`}>
            {login}
          </li>
        ))}
        {!all && logins.length > shown.length && <li className="text-small leading-[22px] text-muted">{t.tokens.moreMembers(logins.length - shown.length)}</li>}
      </ul>
    </div>
  );
}

/** Inline under the row. Any admin can take over a stopped connection with their own token. */
export function ReplaceTokenForm({
  connection,
  onReplaced,
  onCancel,
}: {
  connection: GithubConnection;
  onReplaced: (c: GithubConnection) => void;
  onCancel: () => void;
}) {
  const t = useT();
  const tt = t.tokens;
  const id = useId();
  const input = useRef<HTMLInputElement>(null);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<TokenFailure | null>(null);
  const [empty, setEmpty] = useState(false);

  useEffect(() => {
    input.current?.focus();
  }, []);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (busy) return;
    setFailure(null);
    const token = takeToken(input.current);
    if (!token) {
      setEmpty(true);
      input.current?.focus();
      return;
    }
    setEmpty(false);
    if (!token.startsWith(FINE_GRAINED) && !token.startsWith(CLASSIC)) {
      setFailure({ reason: "unsupported_token", code: null, ssoUrl: null, scopes: [], missing: [] });
      input.current?.focus();
      return;
    }
    setBusy(true);
    try {
      onReplaced(await replaceGithubToken(connection.id, token));
    } catch (err) {
      setFailure(tokenFailureOf(err, tt.errors));
      input.current?.focus();
      setBusy(false);
    }
  };

  return (
    <form method="post" onSubmit={submit} noValidate className="secret mt-3 grid gap-3" aria-busy={busy}>
      <label htmlFor={`${id}-token`} className="text-small font-semibold text-ink">
        {tt.replaceField(connection.accountLogin)}
      </label>
      <div className="flex gap-2 max-[479px]:flex-col">
        <div className="min-w-0 flex-1">
          <SecretInput
            id={`${id}-token`}
            inputRef={input}
            label={tt.replaceField(connection.accountLogin)}
            describedBy={`${id}-hint`}
            invalid={empty || failure !== null}
          />
        </div>
        <div className="flex shrink-0 gap-2">
          <button type="submit" className="btn h-10 px-4" disabled={busy} aria-busy={busy}>
            {busy ? `${tt.replacing}…` : tt.replaceSubmit}
          </button>
          <button
            type="button"
            className="btn h-10 px-4"
            onClick={() => {
              takeToken(input.current);
              onCancel();
            }}
            disabled={busy}
          >
            {tt.cancel}
          </button>
        </div>
      </div>
      <p id={`${id}-hint`} className="text-small text-ink-soft">
        {tt.secretNote} {tt.replaceHint}
      </p>
      {empty && (
        <p role="alert" className="border-l-2 border-ink-soft pl-3 text-small text-ink">
          {tt.needToken}
        </p>
      )}
      {failure && <TokenErrorBox failure={failure} />}
    </form>
  );
}

/** Modal confirm, same shape as removing a member: focus starts on "그대로 두기", Esc and the backdrop cancel. */
export function DisconnectTokenDialog({
  connection,
  onClose,
  onDone,
}: {
  connection: GithubConnection;
  onClose: () => void;
  onDone: () => void;
}) {
  const t = useT();
  const tt = t.tokens;
  const id = useId();
  const dialog = useRef<HTMLDialogElement>(null);
  const keep = useRef<HTMLButtonElement>(null);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<TokenFailure | null>(null);

  useEffect(() => {
    const d = dialog.current;
    if (!d) return;
    const opener = document.activeElement as HTMLElement | null;
    if (!d.open) d.showModal();
    keep.current?.focus();
    return () => opener?.focus?.();
  }, []);

  const confirm = async () => {
    setBusy(true);
    setFailure(null);
    try {
      await deleteGithubToken(connection.id);
      onDone();
    } catch (e) {
      const f = tokenFailureOf(e, tt.errors);
      if (f.reason === "not_found") onDone();
      else {
        setFailure(f);
        setBusy(false);
      }
    }
  };

  return (
    <dialog
      ref={dialog}
      aria-labelledby={`${id}-title`}
      aria-describedby={`${id}-body`}
      onCancel={(e) => {
        e.preventDefault();
        if (!busy) onClose();
      }}
      onClick={(e) => {
        if (e.target === e.currentTarget && !busy) onClose();
      }}
      className="m-auto w-[min(440px,calc(100vw-32px))] rounded-sheet bg-surface p-0 text-ink shadow-panel backdrop:bg-[var(--wb-scrim)]"
    >
      <div className="p-6 max-[639px]:p-5">
        <h2 id={`${id}-title`} className="text-title font-semibold">
          {tt.disconnectTitle(connection.accountLogin)}
        </h2>
        <p id={`${id}-body`} className="mt-3 text-body text-ink-soft">
          {tt.disconnectBody}
        </p>
        {failure && <TokenErrorBox failure={failure} className="mt-4" />}
        <div className="mt-6 flex flex-wrap justify-end gap-2">
          <button ref={keep} type="button" className="btn" onClick={onClose} disabled={busy}>
            {tt.keep}
          </button>
          <button type="button" className="btn btn-primary" onClick={() => void confirm()} disabled={busy} aria-busy={busy}>
            {busy ? `${tt.disconnecting}…` : tt.disconnect}
          </button>
        </div>
      </div>
    </dialog>
  );
}
