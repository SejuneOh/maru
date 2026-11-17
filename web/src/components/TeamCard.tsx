"use client";

import { useEffect, useId, useRef, useState } from "react";
import { ApiError, inviteLink, inviteMember, newIdempotencyKey, removeMember, revokeInvitation, useApi } from "@/lib/api";
import { GATE_EVENT } from "@/lib/auth";
import { type Dict, useFormat, useT } from "@/lib/i18n";
import type { Invitation, Member } from "@/lib/types";
import { Empty, ErrorBox, Loading } from "./Feedback";
import { Avatar, Count } from "./ui";

type TeamErrors = Dict["team"]["errors"];
type TeamError = keyof TeamErrors;

const ROW = "relative flex items-center gap-3 px-5 py-3.5 after:absolute after:inset-x-5 after:top-0 after:h-px after:bg-rule first:after:hidden";
const SMALL_BTN = "btn h-8 px-3 text-small";

function errorOf(e: unknown, known: readonly TeamError[]): TeamError {
  const code = e instanceof ApiError ? e.code : null;
  return code && (known as readonly string[]).includes(code) ? (code as TeamError) : "failed";
}

/**
 * Admin-only: invite by GitHub login, pending invites, and the current members with remove.
 * The parent renders it only for admins; the API refuses everyone else anyway.
 */
export function TeamCard({ me }: { me: Member }) {
  const t = useT();
  const tt = t.team;
  const members = useApi<Member[]>("/api/members");
  const invitations = useApi<Invitation[]>("/api/invitations");
  const [link, setLink] = useState<{ login: string; url: string } | null>(null);
  const [notice, setNotice] = useState<TeamError | null>(null);
  const [removing, setRemoving] = useState<Member | null>(null);
  const [busyInvite, setBusyInvite] = useState<string | null>(null);

  const admins = members.data?.filter((m) => m.role === "admin").length ?? 0;
  const soleAdmin = me.role === "admin" && admins <= 1;

  const issued = (login: string, token: string | null) => {
    setLink(token ? { login, url: inviteLink(token) } : null);
    invitations.reload();
  };

  const reissue = async (inv: Invitation) => {
    setBusyInvite(inv.id);
    setNotice(null);
    try {
      const created = await inviteMember(inv.githubLogin, newIdempotencyKey());
      issued(created.invitation.githubLogin, created.token);
    } catch (e) {
      setNotice(errorOf(e, ["github_user_not_found", "already_member", "github_rate_limited", "github_unavailable", "admin_only"]));
      invitations.reload();
      members.reload();
    } finally {
      setBusyInvite(null);
    }
  };

  const revoke = async (inv: Invitation) => {
    setBusyInvite(inv.id);
    setNotice(null);
    try {
      await revokeInvitation(inv.id, newIdempotencyKey());
      if (link?.login === inv.githubLogin) setLink(null);
    } catch (e) {
      setNotice(errorOf(e, ["not_found", "admin_only"]));
    } finally {
      setBusyInvite(null);
      invitations.reload();
    }
  };

  return (
    <section aria-labelledby="team-title" className="card reveal [animation-delay:45ms]">
      <div className="flex min-h-[60px] flex-wrap items-center gap-x-3 gap-y-1 px-5 pb-2 pt-4">
        <h2 id="team-title" className="whitespace-nowrap text-heading font-semibold">
          {tt.title}
        </h2>
        {members.data && <Count n={members.data.length} />}
        <span className="min-w-0 truncate text-small text-muted">{tt.hint}</span>
      </div>

      <div className="px-5 pb-5">
        <InviteForm onInvited={issued} />
        {link && <LinkPanel login={link.login} url={link.url} onClose={() => setLink(null)} />}
        {notice && (
          <p role="alert" className="mt-4 border-l-2 border-ink-soft pl-3 text-small text-ink">
            {tt.errors[notice]}
          </p>
        )}
      </div>

      <h3 className="px-5 pb-1 pt-2 text-small font-semibold text-ink-soft">
        {tt.pending}
        {invitations.data && <span className="ml-2 font-mono text-meta font-normal text-muted">{invitations.data.length}</span>}
      </h3>
      {invitations.error ? (
        <div className="px-5 pb-5">
          <ErrorBox error={invitations.error} />
        </div>
      ) : !invitations.data ? (
        <Loading />
      ) : invitations.data.length === 0 ? (
        <div className="px-5 pb-4 pt-1">
          <Empty>{tt.noPending}</Empty>
        </div>
      ) : (
        <ul aria-label={tt.pending}>
          {invitations.data.map((inv) => (
            <InvitationRow
              key={inv.id}
              invitation={inv}
              busy={busyInvite === inv.id}
              onNewLink={() => void reissue(inv)}
              onRevoke={() => void revoke(inv)}
            />
          ))}
        </ul>
      )}

      <h3 className="mt-2 px-5 pb-1 pt-3 text-small font-semibold text-ink-soft">{tt.members}</h3>
      {members.error ? (
        <div className="px-5 pb-5">
          <ErrorBox error={members.error} />
        </div>
      ) : !members.data ? (
        <Loading />
      ) : (
        <ul aria-label={tt.members} className="pb-2">
          {members.data.map((m) => (
            <MemberRow key={m.id} member={m} self={m.id === me.id} locked={m.id === me.id && soleAdmin} onRemove={() => setRemoving(m)} />
          ))}
        </ul>
      )}

      {removing && (
        <RemoveDialog
          member={removing}
          self={removing.id === me.id}
          onClose={() => setRemoving(null)}
          onDone={(self) => {
            setRemoving(null);
            members.reload();
            invitations.reload();
            if (self) window.dispatchEvent(new Event(GATE_EVENT));
          }}
        />
      )}
    </section>
  );
}

function InviteForm({ onInvited }: { onInvited: (login: string, token: string | null) => void }) {
  const t = useT();
  const tt = t.team;
  const id = useId();
  const input = useRef<HTMLInputElement>(null);
  const key = useRef<string | null>(null);
  const [login, setLogin] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<TeamError | null>(null);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (busy) return;
    const value = login.trim().replace(/^@/, "");
    if (!/^[A-Za-z0-9](?:[A-Za-z0-9]|-(?=[A-Za-z0-9])){0,38}$/.test(value)) {
      setError("invalid_login");
      input.current?.focus();
      return;
    }
    key.current ??= newIdempotencyKey();
    setBusy(true);
    setError(null);
    try {
      const created = await inviteMember(value, key.current);
      key.current = null;
      setLogin("");
      onInvited(created.invitation.githubLogin, created.token);
    } catch (err) {
      // A network failure keeps the key, so a retry of the same submission is replayed rather than repeated.
      if (err instanceof ApiError) key.current = null;
      setError(
        errorOf(err, ["invalid_login", "github_user_not_found", "already_member", "github_rate_limited", "github_unavailable", "admin_only"]),
      );
      input.current?.focus();
    } finally {
      setBusy(false);
    }
  };

  const fieldError = error === "invalid_login" || error === "github_user_not_found" || error === "already_member";
  return (
    <form onSubmit={submit} noValidate className="grid gap-2">
      <label htmlFor={`${id}-login`} className="text-small font-semibold text-ink">
        {tt.inviteLabel}
      </label>
      <div className="flex gap-2 max-[479px]:flex-col">
        <div className="relative min-w-0 flex-1">
          <span aria-hidden className="pointer-events-none absolute inset-y-0 left-3 grid place-items-center font-mono text-ui text-muted">
            @
          </span>
          <input
            ref={input}
            id={`${id}-login`}
            value={login}
            onChange={(e) => {
              setLogin(e.target.value);
              key.current = null;
              if (error) setError(null);
            }}
            autoComplete="off"
            autoCapitalize="none"
            spellCheck={false}
            placeholder={tt.invitePlaceholder}
            aria-invalid={fieldError || undefined}
            aria-describedby={error ? `${id}-error ${id}-hint` : `${id}-hint`}
            className={`h-10 w-full min-w-0 rounded-item bg-surface pl-7 pr-3 font-mono text-ui text-ink placeholder:text-muted focus:shadow-[inset_0_0_0_2px_var(--wb-focus)] focus:outline-none ${
              fieldError ? "shadow-[inset_0_0_0_1px_var(--wb-ink-soft)]" : "shadow-[inset_0_0_0_1px_var(--wb-rule-strong)]"
            }`}
          />
        </div>
        <button type="submit" className="btn btn-primary h-10 shrink-0 px-4" disabled={busy} aria-busy={busy}>
          {busy ? `${tt.inviting}…` : tt.invite}
        </button>
      </div>
      {error && (
        <p id={`${id}-error`} role="alert" className="border-l-2 border-ink-soft pl-3 text-small text-ink">
          {tt.errors[error]}
        </p>
      )}
      <p id={`${id}-hint`} className="text-small text-muted">
        {tt.inviteHint}
      </p>
    </form>
  );
}

/** The one-time link right after an invite; the token is not stored anywhere on this side. */
function LinkPanel({ login, url, onClose }: { login: string; url: string; onClose: () => void }) {
  const t = useT();
  const tt = t.team;
  const id = useId();
  const field = useRef<HTMLInputElement>(null);
  const [copied, setCopied] = useState<"yes" | "failed" | null>(null);

  useEffect(() => {
    setCopied(null);
    field.current?.focus();
    field.current?.select();
  }, [url]);

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(url);
      setCopied("yes");
    } catch {
      setCopied("failed");
      field.current?.select();
    }
  };

  return (
    <div role="region" aria-labelledby={`${id}-title`} className="mt-4 rounded-item bg-sunken p-4">
      <div className="flex items-start gap-3">
        <p id={`${id}-title`} className="min-w-0 flex-1 text-ui font-semibold text-ink">
          {tt.linkTitle(login)}
        </p>
        <button
          type="button"
          onClick={onClose}
          aria-label={tt.closeLink}
          className="-m-1 grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-control text-muted hover:bg-hover hover:text-ink"
        >
          <svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" aria-hidden>
            <path d="M4 4l8 8M12 4l-8 8" />
          </svg>
        </button>
      </div>
      <div className="mt-3 flex gap-2 max-[479px]:flex-col">
        <input
          ref={field}
          readOnly
          value={url}
          aria-label={tt.linkLabel}
          onFocus={(e) => e.currentTarget.select()}
          className="h-10 w-full min-w-0 flex-1 rounded-item bg-surface px-3 font-mono text-meta text-ink shadow-[inset_0_0_0_1px_var(--wb-rule-strong)] focus:shadow-[inset_0_0_0_2px_var(--wb-focus)] focus:outline-none"
        />
        <button type="button" className="btn h-10 shrink-0 px-4" onClick={() => void copy()}>
          {copied === "yes" ? tt.copied : tt.copy}
        </button>
      </div>
      <p role="status" className="mt-2 text-small text-ink-soft">
        {copied === "failed" ? tt.copyFailed : tt.linkNote}
      </p>
    </div>
  );
}

function InvitationRow({
  invitation,
  busy,
  onNewLink,
  onRevoke,
}: {
  invitation: Invitation;
  busy: boolean;
  onNewLink: () => void;
  onRevoke: () => void;
}) {
  const t = useT();
  const tt = t.team;
  const fmt = useFormat();
  const expired = Date.parse(invitation.expiresAt) <= Date.now();
  const by = invitation.invitedBy ? fmt.person(invitation.invitedBy) : null;
  return (
    <li className={`${ROW} flex-wrap`} aria-busy={busy}>
      <Avatar person={{ displayName: null, githubLogin: invitation.githubLogin }} ghost />
      <div className="min-w-0 flex-1">
        <p className="truncate font-mono text-ui text-ink">{invitation.githubLogin}</p>
        <p className="text-small text-muted">
          <span className={expired ? "font-semibold text-ink-soft" : undefined}>{expired ? tt.expired : tt.expiresOn(fmt.date(invitation.expiresAt))}</span>
          {by && <span> · {tt.invitedBy(by)}</span>}
        </p>
      </div>
      <div className="flex shrink-0 gap-2 max-[479px]:w-full max-[479px]:pl-9">
        <button type="button" className={SMALL_BTN} onClick={onNewLink} disabled={busy} aria-label={tt.newLinkLabel(invitation.githubLogin)}>
          {tt.newLink}
        </button>
        <button type="button" className={SMALL_BTN} onClick={onRevoke} disabled={busy} aria-label={tt.revokeLabel(invitation.githubLogin)}>
          {tt.revoke}
        </button>
      </div>
    </li>
  );
}

function MemberRow({ member, self, locked, onRemove }: { member: Member; self: boolean; locked: boolean; onRemove: () => void }) {
  const t = useT();
  const tt = t.team;
  const fmt = useFormat();
  const name = fmt.person(member);
  const hintId = useId();
  return (
    <li className={`${ROW} flex-wrap`}>
      <Avatar person={member} />
      <div className="min-w-0 flex-1">
        <p className="flex min-w-0 items-center gap-2 text-ui">
          <span className="truncate font-medium text-ink">{name}</span>
          {self && <span className="shrink-0 text-small text-muted">({tt.you})</span>}
          {member.role === "admin" && (
            <span className="shrink-0 rounded-flag px-1.5 text-meta leading-5 text-ink-soft shadow-[inset_0_0_0_1px_var(--wb-rule-strong)]">
              {tt.admin}
            </span>
          )}
        </p>
        {member.displayName && <p className="truncate font-mono text-meta text-muted">{member.githubLogin}</p>}
        {locked && (
          <p id={hintId} className="mt-1 text-small text-muted">
            {tt.lastAdminHint}
          </p>
        )}
      </div>
      <button
        type="button"
        className={SMALL_BTN}
        onClick={onRemove}
        disabled={locked}
        aria-describedby={locked ? hintId : undefined}
        aria-label={tt.removeLabel(name)}
      >
        {self ? tt.confirm.leave : tt.remove}
      </button>
    </li>
  );
}

/** Modal confirm: focus starts on the safe choice, Esc and the backdrop cancel. */
function RemoveDialog({
  member,
  self,
  onClose,
  onDone,
}: {
  member: Member;
  self: boolean;
  onClose: () => void;
  onDone: (self: boolean) => void;
}) {
  const t = useT();
  const tt = t.team;
  const fmt = useFormat();
  const id = useId();
  const dialog = useRef<HTMLDialogElement>(null);
  const keep = useRef<HTMLButtonElement>(null);
  const key = useRef(newIdempotencyKey());
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<TeamError | null>(null);

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
    setError(null);
    try {
      await removeMember(member.id, key.current);
      onDone(self);
    } catch (e) {
      if (e instanceof ApiError) key.current = newIdempotencyKey();
      const reason = errorOf(e, ["last_admin", "not_found", "admin_only"]);
      if (reason === "not_found") onDone(false);
      else setError(reason);
      setBusy(false);
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
          {self ? tt.confirm.titleSelf : tt.confirm.title(fmt.person(member))}
        </h2>
        <p id={`${id}-body`} className="mt-3 text-body text-ink-soft">
          {self ? tt.confirm.bodySelf : tt.confirm.body}
        </p>
        {error && (
          <p role="alert" className="mt-4 border-l-2 border-ink-soft pl-3 text-small text-ink">
            {tt.errors[error]}
          </p>
        )}
        <div className="mt-6 flex flex-wrap justify-end gap-2">
          <button ref={keep} type="button" className="btn" onClick={onClose} disabled={busy}>
            {tt.confirm.cancel}
          </button>
          <button type="button" className="btn btn-primary" onClick={() => void confirm()} disabled={busy} aria-busy={busy}>
            {busy ? `${tt.confirm.removing}…` : self ? tt.confirm.leave : tt.confirm.remove}
          </button>
        </div>
      </div>
    </dialog>
  );
}
