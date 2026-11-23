"use client";

import { useEffect, useId, useRef, useState } from "react";
import {
  ApiError,
  MCP_CLIENTS_PATH,
  MCP_ENDPOINT_URL,
  createMcpClient,
  newIdempotencyKey,
  revokeMcpClient,
  useApi,
} from "@/lib/api";
import { useFormat, useT } from "@/lib/i18n";
import type { McpClient, Member } from "@/lib/types";
import { Avatar, Count } from "./ui";
import { Empty, ErrorBox, Loading } from "./Feedback";

type Scope = "read" | "write";
const EXPIRY_DAYS = [7, 30, 90] as const;

/** Settings card: a member mints and revokes AI tool (MCP) tokens; an admin can see and revoke the company's. */
export function AiTools({ me }: { me: Member }) {
  const t = useT();
  const at = t.aiTools;
  const isAdmin = me.role === "admin";
  const [all, setAll] = useState(false);
  const adminAll = isAdmin && all;
  const clients = useApi<McpClient[]>(adminAll ? `${MCP_CLIENTS_PATH}?all=true` : MCP_CLIENTS_PATH);

  const [open, setOpen] = useState(false);
  const [created, setCreated] = useState<{ name: string; token: string } | null>(null);
  const [disconnecting, setDisconnecting] = useState<McpClient | null>(null);
  const startRef = useRef<HTMLButtonElement>(null);

  const list = clients.data;
  const closeForm = () => {
    setOpen(false);
    startRef.current?.focus();
  };

  return (
    <section aria-labelledby="ai-tools-title" className="card reveal [animation-delay:45ms]">
      <div className="flex min-h-[60px] flex-wrap items-center gap-x-3 gap-y-2 px-5 pb-2 pt-4">
        <div className="flex min-w-0 items-center gap-3">
          <h2 id="ai-tools-title" className="whitespace-nowrap text-heading font-semibold">
            {at.title}
          </h2>
          {list && <Count n={list.length} />}
        </div>
        {!open && !created && (
          <button ref={startRef} type="button" className="btn ml-auto shrink-0 max-[639px]:ml-0" aria-expanded={false} onClick={() => setOpen(true)}>
            {at.new}
          </button>
        )}
      </div>
      <p className="px-5 pb-3 text-small text-ink-soft">{at.lead}</p>

      {isAdmin && (
        <div className="px-5 pb-3">
          <div className="seg" role="group" aria-label={at.scopeViewLabel}>
            <button type="button" aria-pressed={!all} onClick={() => setAll(false)}>
              {at.mine}
            </button>
            <button type="button" aria-pressed={all} onClick={() => setAll(true)}>
              {at.company}
            </button>
          </div>
        </div>
      )}

      {created ? (
        <div className="mx-5 mb-4">
          <TokenBox name={created.name} token={created.token} onClose={() => setCreated(null)} />
        </div>
      ) : (
        open &&
        !adminAll && (
          <div className="mx-5 mb-4 border-t border-rule pt-4">
            <CreateForm
              onClose={closeForm}
              onCreated={(name, token) => {
                setOpen(false);
                setCreated({ name, token });
                clients.reload();
              }}
            />
          </div>
        )
      )}

      {clients.error ? (
        <div className="px-5 pb-5">
          <ErrorBox error={clients.error} />
        </div>
      ) : !list ? (
        <Loading />
      ) : list.length === 0 ? (
        <div className="px-5 pb-5">
          <Empty>{at.none}</Empty>
        </div>
      ) : (
        <ul aria-label={at.listLabel}>
          {list.map((c) => (
            <ToolRow key={c.id} client={c} showOwner={adminAll} onDisconnect={() => setDisconnecting(c)} />
          ))}
        </ul>
      )}

      {disconnecting && (
        <DisconnectDialog
          client={disconnecting}
          onClose={() => setDisconnecting(null)}
          onDone={() => {
            setDisconnecting(null);
            clients.reload();
          }}
        />
      )}
    </section>
  );
}

function CreateForm({ onClose, onCreated }: { onClose: () => void; onCreated: (name: string, token: string) => void }) {
  const t = useT();
  const at = t.aiTools;
  const fmt = useFormat();
  const key = useRef(newIdempotencyKey());
  const [name, setName] = useState("");
  const [scope, setScope] = useState<Scope>("read");
  const [days, setDays] = useState<number>(30);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const expiresOn = fmt.date(new Date(Date.now() + days * 86_400_000).toISOString());

  const submit = async () => {
    if (!name.trim() || busy) return;
    setBusy(true);
    setError(null);
    try {
      const res = await createMcpClient({ name: name.trim(), scope, expiresInDays: days }, key.current);
      if (!res.token) {
        // A replay of the same key created the token earlier; the plaintext is gone and cannot be shown again.
        key.current = newIdempotencyKey();
        setError(at.errors.noToken);
        setBusy(false);
        return;
      }
      onCreated(res.client.name, res.token);
    } catch (e) {
      key.current = newIdempotencyKey();
      const code = e instanceof ApiError ? e.code : "network";
      setError(code && code in at.errors ? at.errors[code as keyof typeof at.errors] : at.errors.unknown);
      setBusy(false);
    }
  };

  return (
    <div className="flex flex-col gap-4">
      <label className="flex flex-col gap-1.5">
        <span className="text-small font-medium text-ink-soft">{at.nameLabel}</span>
        <input
          autoFocus
          value={name}
          onChange={(e) => setName(e.target.value)}
          placeholder={at.namePlaceholder}
          maxLength={80}
          className="h-10 rounded-control bg-sunken px-3 text-ui text-ink placeholder:text-muted focus:shadow-[inset_0_0_0_2px_var(--wb-focus)] focus:outline-none"
        />
      </label>

      <fieldset className="flex flex-col gap-2">
        <legend className="mb-1 text-small font-medium text-ink-soft">{at.scopeLabel}</legend>
        {(["read", "write"] as Scope[]).map((s) => (
          <label
            key={s}
            className={`flex cursor-pointer items-start gap-2.5 rounded-control px-3 py-2.5 ${scope === s ? "shadow-[inset_0_0_0_1.5px_var(--wb-ink)]" : "shadow-[inset_0_0_0_1px_var(--wb-rule-strong)]"}`}
          >
            <input type="radio" name="mcp-scope" checked={scope === s} onChange={() => setScope(s)} className="mt-0.5" />
            <span className="min-w-0">
              <span className="block text-ui text-ink">{at.scopes[s].title}</span>
              <span className="block text-small text-muted">{at.scopes[s].hint}</span>
            </span>
          </label>
        ))}
      </fieldset>

      <div className="flex flex-col gap-1.5">
        <span className="text-small font-medium text-ink-soft">{at.expiryLabel}</span>
        <div className="seg w-fit" role="group" aria-label={at.expiryLabel}>
          {EXPIRY_DAYS.map((d) => (
            <button key={d} type="button" aria-pressed={days === d} onClick={() => setDays(d)}>
              {at.days(d)}
            </button>
          ))}
        </div>
        <span className="text-small text-muted">{at.expiresOn(expiresOn)}</span>
      </div>

      {error && <p role="alert" className="border-l-2 border-ink-soft pl-3 text-small text-ink">{error}</p>}

      <div className="flex items-center gap-2">
        <button type="button" onClick={submit} disabled={!name.trim() || busy} className="btn btn-primary h-9 px-4 text-small disabled:opacity-50">
          {at.create}
        </button>
        <button type="button" onClick={onClose} className="link text-small text-ink-soft">
          {at.cancel}
        </button>
      </div>
    </div>
  );
}

/** The token, shown once. The caller keeps it only in component state and drops it on close. */
function TokenBox({ name, token, onClose }: { name: string; token: string; onClose: () => void }) {
  const t = useT();
  const at = t.aiTools;
  const id = useId();
  const field = useRef<HTMLInputElement>(null);
  const [copied, setCopied] = useState<"token" | "config" | "failed" | null>(null);
  const config = JSON.stringify(
    { mcpServers: { maru: { url: MCP_ENDPOINT_URL, headers: { Authorization: `Bearer ${token}` } } } },
    null,
    2,
  );

  useEffect(() => {
    field.current?.focus();
    field.current?.select();
  }, []);

  const copy = async (what: "token" | "config", text: string) => {
    try {
      await navigator.clipboard.writeText(text);
      setCopied(what);
    } catch {
      setCopied("failed");
      if (what === "token") field.current?.select();
    }
  };

  return (
    <div role="region" aria-labelledby={`${id}-title`} className="rounded-item bg-sunken p-4">
      <p id={`${id}-title`} className="text-ui font-semibold text-ink">
        {at.tokenTitle(name)}
      </p>
      <div className="mt-3 flex gap-2 max-[479px]:flex-col">
        <input
          ref={field}
          readOnly
          value={token}
          aria-label={at.tokenLabel}
          onFocus={(e) => e.currentTarget.select()}
          className="h-10 w-full min-w-0 flex-1 rounded-item bg-surface px-3 font-mono text-meta text-ink shadow-[inset_0_0_0_1px_var(--wb-rule-strong)] focus:shadow-[inset_0_0_0_2px_var(--wb-focus)] focus:outline-none"
        />
        <button type="button" className="btn h-10 shrink-0 px-4" onClick={() => void copy("token", token)}>
          {copied === "token" ? at.copied : at.copy}
        </button>
      </div>
      <p className="mt-2 text-small text-ink-soft">{copied === "failed" ? at.copyFailed : at.tokenNote}</p>

      <p className="mt-4 text-small font-medium text-ink-soft">{at.configTitle}</p>
      <pre className="mt-1.5 overflow-x-auto rounded-control bg-surface p-3 font-mono text-meta text-ink-soft">{config}</pre>
      <div className="mt-3 flex flex-wrap gap-2">
        <button type="button" className="btn h-8 px-3 text-small" onClick={() => void copy("config", config)}>
          {copied === "config" ? at.copied : at.copyConfig}
        </button>
        <button type="button" className="btn btn-primary h-8 px-3 text-small" onClick={onClose}>
          {at.doneClose}
        </button>
      </div>
    </div>
  );
}

function ToolRow({ client, showOwner, onDisconnect }: { client: McpClient; showOwner: boolean; onDisconnect: () => void }) {
  const t = useT();
  const at = t.aiTools;
  const fmt = useFormat();
  const inactive = client.revokedAt !== null || Date.parse(client.expiresAt) <= Date.now();
  const status = client.revokedAt
    ? at.revokedOn(fmt.date(client.revokedAt))
    : Date.parse(client.expiresAt) <= Date.now()
      ? at.expired
      : at.expiresOnShort(fmt.date(client.expiresAt));

  return (
    <li className={`relative flex items-start gap-3 px-5 py-4 after:absolute after:inset-x-5 after:top-0 after:h-px after:bg-rule first:after:hidden ${inactive ? "text-muted" : ""}`}>
      {showOwner && client.owner && <Avatar person={{ displayName: client.owner.displayName, githubLogin: client.owner.githubLogin, avatarUrl: null }} size="sm" />}
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
          <span className={`text-ui font-medium ${inactive ? "" : "text-ink"}`}>{client.name}</span>
          <span className="tag">{at.scopes[client.scope].short}</span>
          {showOwner && client.owner && <span className="font-mono text-meta text-muted">@{client.owner.githubLogin}</span>}
        </div>
        <p className="mt-0.5 flex flex-wrap items-center gap-x-1.5 text-small text-muted">
          <span className="font-mono">{client.tokenPrefix}…</span>
          <span aria-hidden>·</span>
          <span>{at.createdOn(fmt.date(client.createdAt))}</span>
          <span aria-hidden>·</span>
          <span>{client.lastUsedAt ? at.lastUsed(fmt.relativeTime(client.lastUsedAt)) : at.neverUsed}</span>
          <span aria-hidden>·</span>
          <span>{status}</span>
        </p>
      </div>
      {!inactive && (
        <button type="button" className="btn h-8 shrink-0 px-3 text-small" aria-label={at.disconnectLabel(client.name)} onClick={onDisconnect}>
          {at.disconnect}
        </button>
      )}
    </li>
  );
}

function DisconnectDialog({ client, onClose, onDone }: { client: McpClient; onClose: () => void; onDone: () => void }) {
  const t = useT();
  const at = t.aiTools;
  const id = useId();
  const dialog = useRef<HTMLDialogElement>(null);
  const keep = useRef<HTMLButtonElement>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState(false);

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
    setError(false);
    try {
      await revokeMcpClient(client.id);
      onDone();
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) onDone();
      else {
        setError(true);
        setBusy(false);
      }
    }
  };

  return (
    <dialog
      ref={dialog}
      aria-labelledby={`${id}-title`}
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
        <h2 id={`${id}-title`} className="text-title font-semibold">{at.confirmTitle(client.name)}</h2>
        <p className="mt-3 text-body text-ink-soft">{at.confirmBody}</p>
        {error && <p role="alert" className="mt-4 border-l-2 border-ink-soft pl-3 text-small text-ink">{at.errors.unknown}</p>}
        <div className="mt-6 flex flex-wrap justify-end gap-2">
          <button ref={keep} type="button" className="btn" onClick={onClose} disabled={busy}>
            {at.keep}
          </button>
          <button type="button" className="btn btn-primary" onClick={confirm} disabled={busy}>
            {at.disconnect}
          </button>
        </div>
      </div>
    </dialog>
  );
}
