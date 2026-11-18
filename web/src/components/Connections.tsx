"use client";

import { useState } from "react";
import { ApiError, cancelConnectionRequest, checkConnectionRequest } from "@/lib/api";
import { type Dict, useFormat, useT } from "@/lib/i18n";
import type {
  ConnectionRequest,
  ConnectionRequestError,
  GithubConnection,
  OrganizationChoice,
} from "@/lib/types";
import { GitHubMark } from "./Auth";
import { TokenLine } from "./TokenConnection";

const APP_SLUG = process.env.NEXT_PUBLIC_GITHUB_APP_SLUG ?? "";

/** GitHub's install screen for the App, or `null` when the slug is not configured. */
export const githubInstallUrl: string | null = APP_SLUG
  ? `https://github.com/apps/${encodeURIComponent(APP_SLUG)}/installations/new`
  : null;

export const ROW_LINE = "relative after:absolute after:inset-x-5 after:top-0 after:h-px after:bg-rule first:after:hidden";

/**
 * Account login, how it connects (GitHub App or whose token), type, repository access, body storage and when it was
 * connected; a token connection adds its health line. A disconnected one steps back.
 */
export function ConnectionRow({
  connection: c,
  className = "",
  isAdmin = false,
  actions,
  children,
}: {
  connection: GithubConnection;
  className?: string;
  isAdmin?: boolean;
  actions?: React.ReactNode;
  children?: React.ReactNode;
}) {
  const t = useT();
  const fmt = useFormat();
  const ct = t.connections;
  const tt = t.tokens;
  const off = c.disconnectedAt !== null;
  const token = c.authKind === "token";
  const selection = token
    ? ct.selected
    : c.repositorySelection === "all"
      ? ct.all
      : c.repositorySelection === "selected"
        ? ct.selected
        : ct.unknownSelection;
  const kind = token ? (c.tokenOwnerLogin ? tt.kindToken(c.tokenOwnerLogin) : tt.kindTokenEmpty) : tt.kindApp;
  const dot = (
    <span aria-hidden className="mx-1.5 text-faint">
      ·
    </span>
  );
  return (
    <li className={`px-5 py-4 ${off ? "text-muted" : ""} ${className}`}>
      <div className="flex flex-wrap items-center gap-x-4 gap-y-1">
        <span className="flex min-w-0 max-w-full items-center gap-2.5">
          <span className={off ? "text-muted" : "text-ink"}>
            <GitHubMark size={16} />
          </span>
          <span className={`truncate font-mono text-ui font-medium ${off ? "" : "text-ink"}`}>{c.accountLogin}</span>
          <span className="tag min-w-0">
            <span className="truncate">{kind}</span>
          </span>
        </span>
        <span className="text-small text-ink-soft">
          {c.accountType === "organization" ? ct.organization : ct.user}
          {dot}
          {selection}
          {c.orgMemberCount !== null && (
            <>
              {dot}
              {ct.orgMembers(c.orgMemberCount)}
            </>
          )}
          {dot}
          <span className={c.storeBodies ? "" : "text-muted"}>{c.storeBodies ? tt.bodiesOn : tt.bodiesOff}</span>
        </span>
        <span className="w-full whitespace-nowrap text-small tabular-nums text-muted min-[640px]:ml-auto min-[640px]:w-auto">
          {off ? ct.disconnectedOn(fmt.date(c.disconnectedAt!)) : ct.connectedOn(fmt.date(c.createdAt))}
        </span>
      </div>
      {token && !off && <TokenLine connection={c} isAdmin={isAdmin} />}
      {actions && <div className="mt-3 flex flex-wrap gap-2 pl-[26px] max-[479px]:pl-0">{actions}</div>}
      {children}
    </li>
  );
}

type ErrorReason = keyof Dict["connect"]["errors"];

type CheckState =
  | { kind: "idle" }
  | { kind: "checking" }
  | { kind: "pending" }
  | { kind: "pick"; organizations: OrganizationChoice[] }
  | { kind: "failed"; reason: ErrorReason; code: string | null };

const CHECK_ERRORS: ReadonlySet<string> = new Set<Exclude<ConnectionRequestError, "unauthenticated" | "multiple_organizations" | "not_org_owner">>([
  "admin_only",
  "no_request",
  "account_connected",
  "org_members_not_granted",
  "github_unavailable",
  "github_app_not_configured",
]);

function choicesOf(body: unknown): OrganizationChoice[] {
  const list = (body as { organizations?: unknown } | null)?.organizations;
  return Array.isArray(list)
    ? list.filter((o): o is OrganizationChoice => typeof o?.accountId === "number" && typeof o?.login === "string")
    : [];
}

function failureOf(e: unknown): CheckState {
  if (e instanceof ApiError) {
    if (e.code === "multiple_organizations") {
      const organizations = choicesOf(e.body);
      if (organizations.length > 1) return { kind: "pick", organizations };
    }
    const reason: ErrorReason =
      e.code === "not_org_owner" ? "owner_required" : e.code && CHECK_ERRORS.has(e.code) ? (e.code as ErrorReason) : "unknown";
    return { kind: "failed", reason, code: e.code ?? String(e.status) };
  }
  return { kind: "failed", reason: "network", code: null };
}

/**
 * An organization install that waits for an owner's approval. Admins press "다시 확인"; the server finds the
 * approved installation itself, so nothing about the installation is sent from here.
 */
export function ConnectionRequestPanel({
  request: r,
  isAdmin,
  primary = false,
  onConnected,
  onCancelled,
}: {
  request: ConnectionRequest;
  isAdmin: boolean;
  primary?: boolean;
  onConnected: (connection: GithubConnection) => void;
  onCancelled?: () => void;
}) {
  const t = useT();
  const fmt = useFormat();
  const rt = t.connections.request;
  const [state, setState] = useState<CheckState>({ kind: "idle" });
  const [cancelFailed, setCancelFailed] = useState(false);
  const busy = state.kind === "checking";

  const check = async (accountId?: number) => {
    setState({ kind: "checking" });
    setCancelFailed(false);
    try {
      const result = await checkConnectionRequest(accountId);
      if (result.status === "connected") onConnected(result.connection);
      else setState({ kind: "pending" });
    } catch (e) {
      setState(failureOf(e));
    }
  };

  const cancel = async () => {
    setCancelFailed(false);
    try {
      await cancelConnectionRequest();
      onCancelled?.();
    } catch {
      setCancelFailed(true);
    }
  };

  return (
    <div role="group" aria-label={rt.label} className="rounded-control bg-sunken px-4 py-3.5">
      <div className="flex flex-wrap items-start gap-x-4 gap-y-3">
        <div className="min-w-0 flex-1 basis-[240px]">
          <p className="flex items-center gap-2.5 text-ui font-medium text-ink">
            {busy ? (
              <span aria-hidden className="busy-dot text-ink-soft" />
            ) : (
              <span aria-hidden className="h-1.5 w-1.5 shrink-0 rounded-full border border-ink-soft" />
            )}
            {rt.title}
          </p>
          <p className="mt-1 text-small text-muted">{rt.meta(fmt.date(r.requestedAt), r.requestedByLogin)}</p>
          <p className="mt-2 text-small text-ink-soft">{isAdmin ? rt.lead : rt.noRequestMember}</p>
        </div>
        {isAdmin && (
          <div className="flex shrink-0 items-center gap-4">
            <button
              type="button"
              className={primary ? "btn btn-primary" : "btn h-8 px-3 text-small"}
              onClick={() => void check()}
              disabled={busy}
              aria-busy={busy}
            >
              {busy ? rt.checking : rt.check}
            </button>
            {onCancelled && (
              <button type="button" className="link text-small" onClick={() => void cancel()} disabled={busy}>
                {rt.cancel}
              </button>
            )}
          </div>
        )}
      </div>

      <div role="status" aria-live="polite">
        {state.kind === "pending" && (
          <p className="mt-3 border-t border-rule pt-3 text-small">
            <span className="font-semibold text-ink">{rt.pending}</span>
            <span className="text-ink-soft"> · {rt.pendingHint}</span>
          </p>
        )}
        {state.kind === "pick" && (
          <div className="mt-3 border-t border-rule pt-3">
            <p className="text-small text-ink-soft">{rt.pick}</p>
            <ul className="mt-2 flex flex-wrap gap-2">
              {state.organizations.map((o) => (
                <li key={o.accountId}>
                  <button
                    type="button"
                    className="btn h-8 gap-2 px-3 text-small"
                    aria-label={rt.pickLabel(o.login)}
                    onClick={() => void check(o.accountId)}
                  >
                    <GitHubMark size={14} />
                    <span className="font-mono">{o.login}</span>
                  </button>
                </li>
              ))}
            </ul>
          </div>
        )}
      </div>

      {state.kind === "failed" && (
        <div role="alert" className="mt-3 rounded-control border-l-2 border-ink-soft bg-surface px-4 py-3">
          <p className="text-ui text-ink">{t.connect.errors[state.reason][0]}</p>
          <p className="mt-1 text-small text-ink-soft">{t.connect.errors[state.reason][1]}</p>
          {state.code && (
            <p className="mt-2 text-meta text-muted">
              {t.connect.code} <span className="font-mono">{state.code}</span>
            </p>
          )}
        </div>
      )}
      {cancelFailed && (
        <p role="alert" className="mt-3 border-l-2 border-ink-soft pl-3 text-small text-ink">
          {rt.cancelFailed}
        </p>
      )}
    </div>
  );
}
