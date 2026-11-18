"use client";

import { useRouter } from "next/navigation";
import { useEffect, useId, useRef, useState } from "react";
import { CheckingScreen, GateFrame, GitHubMark, Notice, SignInButton } from "@/components/Auth";
import { ApiError, acceptInvitation, getDevIdentity, previewInvitation, setDevIdentity } from "@/lib/api";
import { JOIN_PATH, authEnabled, setTenant, signInWithGitHub, signOut } from "@/lib/auth";
import { type Dict, useFormat, useT } from "@/lib/i18n";
import { useLogin, useSignedIn } from "@/lib/me";
import type { InvitationPreview, JoinError, JoinedTenant } from "@/lib/types";

type Reason = keyof Dict["join"]["errors"];
type State =
  | { kind: "loading" }
  | { kind: "ready"; preview: InvitationPreview }
  | { kind: "failed"; reason: Reason };

const KNOWN: ReadonlySet<string> = new Set<Exclude<JoinError, "unauthenticated" | "invalid_request">>([
  "invite_not_found",
  "invite_other_account",
  "invite_used",
  "invite_revoked",
  "invite_expired",
  "login_taken",
]);

const TOKEN = /^[A-Za-z0-9_-]{43}$/;

function reasonOf(e: unknown): Reason {
  if (e instanceof ApiError) return e.code && KNOWN.has(e.code) ? (e.code as Reason) : "unknown";
  return "network";
}

/** Makes the joined company the current one, so the gate opens its board. */
function enter(joined: { tenant: { id: string } }) {
  if (authEnabled) {
    setTenant(joined.tenant.id);
    return;
  }
  const identity = getDevIdentity();
  if (identity) setDevIdentity({ ...identity, tenantId: joined.tenant.id });
}

/** `undefined` until the fragment has been read on the client. */
function useToken(): string | null | undefined {
  const [token, setToken] = useState<string | null | undefined>(undefined);
  useEffect(() => {
    const read = () => {
      const raw = window.location.hash.replace(/^#/, "");
      setToken(TOKEN.test(raw) ? raw : null);
    };
    read();
    window.addEventListener("hashchange", read);
    return () => window.removeEventListener("hashchange", read);
  }, []);
  return token;
}

/**
 * Invite link landing. The person needs a sign-in but no membership; the API answers only when the signed-in
 * GitHub account is the invited one, so a forwarded link shows nothing about the company to anyone else.
 */
export default function JoinPage() {
  const t = useT();
  const j = t.join;
  const token = useToken();
  const signedIn = useSignedIn();

  if (token === undefined || signedIn === undefined) return <CheckingScreen title={j.checkingTitle} />;
  if (token === null) return <Failed reason="missing" token={null} />;
  if (!signedIn)
    return (
      <GateFrame kicker={j.kicker} title={j.signInTitle} lead={j.signInLead}>
        {authEnabled ? <SignInButton label={j.signIn} returnTo={`${JOIN_PATH}#${token}`} /> : <DevSignIn />}
      </GateFrame>
    );
  return <Invite token={token} />;
}

function Invite({ token }: { token: string }) {
  const t = useT();
  const j = t.join;
  const fmt = useFormat();
  const router = useRouter();
  const login = useLogin();
  const [state, setState] = useState<State>({ kind: "loading" });
  const [attempt, setAttempt] = useState(0);
  const [busy, setBusy] = useState(false);
  const [acceptError, setAcceptError] = useState<Reason | null>(null);

  useEffect(() => {
    let cancelled = false;
    setState({ kind: "loading" });
    previewInvitation(token)
      .then((preview) => !cancelled && setState({ kind: "ready", preview }))
      .catch((e: unknown) => !cancelled && setState({ kind: "failed", reason: reasonOf(e) }));
    return () => {
      cancelled = true;
    };
  }, [token, attempt, login]);

  const go = (joined: Pick<JoinedTenant, "tenant">) => {
    enter(joined);
    router.replace("/");
  };

  const accept = async () => {
    setBusy(true);
    setAcceptError(null);
    try {
      go(await acceptInvitation(token));
    } catch (e) {
      const reason = reasonOf(e);
      setBusy(false);
      if (reason === "network" || reason === "unknown") setAcceptError(reason);
      else setState({ kind: "failed", reason });
    }
  };

  if (state.kind === "loading") return <CheckingScreen title={j.checkingTitle} />;
  if (state.kind === "failed")
    return <Failed reason={state.reason} token={token} onRetry={() => setAttempt((n) => n + 1)} />;

  const p = state.preview;
  const by = p.invitedBy ? fmt.person(p.invitedBy) : null;
  if (p.alreadyMember)
    return (
      <GateFrame kicker={j.kicker} title={j.alreadyTitle(p.tenant.name)} lead={j.alreadyLead}>
        <button type="button" className="btn btn-primary btn-lg w-full" onClick={() => go(p)}>
          {j.open}
        </button>
      </GateFrame>
    );

  return (
    <GateFrame kicker={j.kicker} title={j.title(p.tenant.name)} lead={j.lead(by)}>
      <div className="flex items-center gap-3">
        <span aria-hidden className="grid h-9 w-9 shrink-0 place-items-center rounded-control bg-sunken font-mono text-small font-semibold text-ink-soft">
          {p.tenant.name.slice(0, 2)}
        </span>
        <div className="min-w-0">
          <p className="truncate text-body font-semibold">{p.tenant.name}</p>
          <p className="truncate text-small text-muted">{j.as(login ?? p.githubLogin)}</p>
        </div>
      </div>
      <p className="mt-4 text-small text-muted">{j.expires(fmt.date(p.expiresAt))}</p>
      {acceptError && (
        <div className="mt-4">
          <Notice>
            {j.errors[acceptError][0]} {j.errors[acceptError][1]}
          </Notice>
        </div>
      )}
      <button
        type="button"
        className="btn btn-primary btn-lg mt-6 w-full"
        onClick={() => void accept()}
        disabled={busy}
        aria-busy={busy}
      >
        {busy ? `${j.accepting}…` : j.accept}
      </button>
    </GateFrame>
  );
}

function Failed({ reason, token, onRetry }: { reason: Reason; token: string | null; onRetry?: () => void }) {
  const t = useT();
  const j = t.join;
  const login = useLogin();
  const [switching, setSwitching] = useState(false);
  const [message, detail] = j.errors[reason];
  const retryable = reason === "network" || reason === "unknown";

  const switchAccount = async () => {
    setSwitching(true);
    if (!authEnabled) {
      setDevIdentity(null);
      return;
    }
    await signOut();
    try {
      await signInWithGitHub(token ? `${JOIN_PATH}#${token}` : JOIN_PATH);
    } catch {
      setSwitching(false);
    }
  };

  return (
    <GateFrame
      kicker={j.kicker}
      title={j.failedTitle}
      footer={login ? <p className="text-small text-muted">{t.gate.signedInAs(login)}</p> : undefined}
    >
      <div role="alert" className="rounded-control border-l-2 border-ink-soft bg-sunken px-4 py-3">
        <p className="text-ui text-ink">{message}</p>
        <p className="mt-1 text-small text-ink-soft">{detail}</p>
      </div>
      {retryable && onRetry && (
        <button type="button" className="btn btn-primary btn-lg mt-5 w-full" onClick={onRetry}>
          {j.retry}
        </button>
      )}
      {reason === "invite_other_account" && (
        <>
          <button
            type="button"
            className="btn btn-primary btn-lg mt-5 w-full"
            onClick={() => void switchAccount()}
            disabled={switching}
            aria-busy={switching}
          >
            <GitHubMark size={18} />
            {j.switchAccount}
          </button>
          {authEnabled && <p className="mt-3 text-small text-muted">{j.switchHint}</p>}
        </>
      )}
    </GateFrame>
  );
}

/** Development without Supabase: type the invited login, since the user picker lists members only. */
function DevSignIn() {
  const t = useT();
  const j = t.join;
  const id = useId();
  const input = useRef<HTMLInputElement>(null);
  const [login, setLogin] = useState("");

  useEffect(() => {
    input.current?.focus();
  }, []);

  return (
    <form
      className="grid gap-2"
      onSubmit={(e) => {
        e.preventDefault();
        const value = login.trim();
        if (value) setDevIdentity({ login: value, tenantId: null });
      }}
    >
      <label htmlFor={id} className="text-small font-semibold text-ink">
        {j.devLabel}
      </label>
      <input
        ref={input}
        id={id}
        value={login}
        onChange={(e) => setLogin(e.target.value)}
        autoComplete="off"
        spellCheck={false}
        className="h-12 w-full min-w-0 rounded-item bg-surface px-4 font-mono text-body text-ink shadow-[inset_0_0_0_1px_var(--wb-rule-strong)] focus:shadow-[inset_0_0_0_2px_var(--wb-focus)] focus:outline-none"
      />
      <button type="submit" className="btn btn-primary btn-lg mt-3 w-full">
        {j.devContinue}
      </button>
    </form>
  );
}
