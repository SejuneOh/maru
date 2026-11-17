"use client";

import { useRef, useState } from "react";
import { Empty, ErrorBox, Loading } from "@/components/Feedback";
import { GitHubMark } from "@/components/Auth";
import { ConnectionRequestPanel, ConnectionRow, ROW_LINE, githubInstallUrl } from "@/components/Connections";
import { TeamCard } from "@/components/TeamCard";
import { DisconnectTokenDialog, OrganizationImport, ReplaceTokenForm, STOPPED, TokenConnectFlow } from "@/components/TokenConnection";
import { Card, Count, PageHeader } from "@/components/ui";
import { CONNECTION_REQUEST_PATH, type Loadable, SYNC_CHANGED_EVENT, useApi } from "@/lib/api";
import { useT } from "@/lib/i18n";
import { useMe } from "@/lib/me";
import type { ConnectionRequestState, GithubConnection, Project } from "@/lib/types";

export default function SettingsPage() {
  const me = useMe();
  const t = useT();
  const st = t.settings;
  const projects = useApi<Project[]>("/api/projects");
  const connections = useApi<GithubConnection[]>("/api/connections");

  const error = me.error ?? projects.error;
  if (error) return <ErrorBox error={error} />;
  if (!me.data || !projects.data) return <Loading />;
  const s = me.data.settings;
  const features = [
    { name: st.sprints, hint: st.sprintsHint, on: s.sprintsEnabled },
    { name: st.size, hint: st.sizeHint, on: s.sizeEnabled },
    { name: st.priority, hint: st.priorityHint, on: s.priorityEnabled },
  ];

  return (
    <div className="flex max-w-[720px] flex-col gap-6">
      <PageHeader meta={me.data.tenant.name} title={st.title}>
        {st.lead}
      </PageHeader>

      <ConnectionsCard connections={connections} isAdmin={me.data.member.role === "admin"} />

      {me.data.member.role === "admin" && <TeamCard me={me.data.member} />}

      <Card title={st.advanced} hint={st.advancedHint} className="reveal [animation-delay:60ms]">
        <ul>
          {features.map((f) => (
            <li key={f.name} className="flex items-start justify-between gap-4 relative px-5 py-4 after:absolute after:inset-x-5 after:top-0 after:h-px after:bg-rule first:after:hidden">
              <div>
                <p className="text-ui font-medium">{f.name}</p>
                <p className="text-small text-muted">{f.hint}</p>
              </div>
              <span className={`inline-flex shrink-0 items-center gap-1.5 text-small font-semibold ${f.on ? "text-ink" : "text-muted"}`}>
                <span aria-hidden className={`h-[7px] w-[7px] rounded-full ${f.on ? "bg-status-done" : "border border-muted"}`} />
                {f.on ? st.on : st.off}
              </span>
            </li>
          ))}
        </ul>
      </Card>

      <Card title={st.projects} count={projects.data.length} hint={st.projectsHint} className="reveal [animation-delay:90ms]">
        {projects.data.length === 0 ? (
          <div className="px-5 pb-5">
            <Empty>{st.noProjects}</Empty>
          </div>
        ) : (
          <ul>
            {projects.data.map((p) => (
              <li key={p.id} className="flex items-center gap-2.5 relative px-5 py-4 after:absolute after:inset-x-5 after:top-0 after:h-px after:bg-rule first:after:hidden">
                <span aria-hidden className="h-2 w-2 shrink-0 rounded-mark" style={{ background: p.color ?? "var(--wb-muted)" }} />
                <span className="text-ui font-medium">{p.name}</span>
                <span className="truncate font-mono text-meta text-muted">{p.repositories.map((r) => r.name).join(" · ")}</span>
              </li>
            ))}
          </ul>
        )}
      </Card>
    </div>
  );
}

const SMALL_BTN = "btn h-8 px-3 text-small";

type Open = { kind: "connect" } | { kind: "import"; connection: GithubConnection } | null;

function ConnectionsCard({ connections, isAdmin }: { connections: Loadable<GithubConnection[]>; isAdmin: boolean }) {
  const t = useT();
  const ct = t.connections;
  const tt = t.tokens;
  const list = connections.data;
  const pending = useApi<ConnectionRequestState>(CONNECTION_REQUEST_PATH);
  const request = pending.data?.request ?? null;
  const [connected, setConnected] = useState<string | null>(null);
  const [open, setOpen] = useState<Open>(null);
  const [replacing, setReplacing] = useState<string | null>(null);
  const [disconnecting, setDisconnecting] = useState<GithubConnection | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const startRef = useRef<HTMLButtonElement>(null);
  const onConnected = (c: GithubConnection) => {
    setConnected(c.accountLogin);
    connections.reload();
    pending.reload();
  };
  const refresh = () => {
    connections.reload();
    window.dispatchEvent(new Event(SYNC_CHANGED_EVENT));
  };
  const close = () => {
    setOpen(null);
    startRef.current?.focus();
  };
  const hasToken = list?.some((c) => c.authKind === "token" && c.disconnectedAt === null) ?? false;
  const action = isAdmin ? (
    <div className="flex flex-wrap gap-2">
      <button
        ref={startRef}
        type="button"
        className="btn"
        aria-expanded={open?.kind === "connect"}
        onClick={() => {
          setNotice(null);
          setOpen({ kind: "connect" });
        }}
      >
        {tt.start}
      </button>
      {githubInstallUrl && (
        <a href={githubInstallUrl} className={open ? "btn" : "btn btn-primary"}>
          <GitHubMark />
          {list && list.length > 0 ? ct.reconnect : ct.connect}
        </a>
      )}
    </div>
  ) : null;

  return (
    <section aria-labelledby="connections-title" className="card reveal mt-2 [animation-delay:30ms]">
      <div className="flex min-h-[60px] flex-wrap items-center gap-x-3 gap-y-3 px-5 pb-2 pt-4">
        <div className="flex min-w-0 items-center gap-3">
          <h2 id="connections-title" className="whitespace-nowrap text-heading font-semibold">
            {ct.title}
          </h2>
          {list && <Count n={list.length} />}
          <span className="min-w-0 truncate text-small text-muted">{ct.hint}</span>
        </div>
        {action && <div className="ml-auto shrink-0 max-[639px]:ml-0">{action}</div>}
      </div>
      <p className="px-5 pb-3 text-small text-ink-soft">{tt.kinds}</p>
      {isAdmin && open && (
        <div className="mx-5 mb-4 border-t border-rule pt-4">
          {open.kind === "connect" ? (
            <TokenConnectFlow onClose={close} onConnected={refresh} />
          ) : (
            <OrganizationImport key={open.connection.id} connection={open.connection} onClose={close} onSaved={refresh} />
          )}
        </div>
      )}
      <div role="status" className="px-5 empty:pb-0 pb-3">
        {notice && (
          <p className="flex items-center gap-2.5 text-ui">
            <span aria-hidden className="h-2 w-2 shrink-0 rounded-full bg-ink" />
            <span className="font-medium text-ink">{notice}</span>
          </p>
        )}
      </div>
      {request && (
        <div className="px-5 pb-4 pt-2">
          <ConnectionRequestPanel
            key={request.id}
            request={request}
            isAdmin={isAdmin}
            onConnected={onConnected}
            onCancelled={pending.reload}
          />
        </div>
      )}
      <div role="status" className="empty:hidden px-5 pb-3 pt-1">
        {connected && !request && (
          <p className="flex items-center gap-2.5 text-ui">
            <span aria-hidden className="h-2 w-2 shrink-0 rounded-full bg-ink" />
            <span className="font-medium text-ink">{ct.request.connected}</span>
            <span className="font-mono text-small text-ink-soft">{connected}</span>
          </p>
        )}
      </div>
      {connections.error ? (
        <div className="px-5 pb-5">
          <ErrorBox error={connections.error} />
        </div>
      ) : !list ? (
        <Loading />
      ) : list.length === 0 ? (
        !request && (
          <div className="px-5 pb-5">
            <Empty>{isAdmin ? ct.none : ct.noneMember}</Empty>
          </div>
        )
      ) : (
        <ul aria-label={ct.listLabel}>
          {list.map((c) => {
            const live = c.authKind === "token" && c.disconnectedAt === null;
            const stopped = live && STOPPED.has(c.tokenStatus);
            const canImport = live && c.accountType === "organization" && c.tokenStatus === "active";
            const replaceButton = (
              <button
                key="replace"
                type="button"
                className={SMALL_BTN}
                aria-label={tt.replaceLabel(c.accountLogin)}
                aria-expanded={replacing === c.id}
                onClick={() => {
                  setNotice(null);
                  setReplacing(c.id);
                }}
              >
                {tt.replace}
              </button>
            );
            const actions =
              isAdmin && live ? (
                <>
                  {stopped && replaceButton}
                  {canImport && (
                    <button
                      type="button"
                      className={SMALL_BTN}
                      aria-label={tt.pickLabel(c.accountLogin)}
                      onClick={() => {
                        setNotice(null);
                        setOpen({ kind: "import", connection: c });
                      }}
                    >
                      {tt.pick}
                    </button>
                  )}
                  {!stopped && replaceButton}
                  <button type="button" className={SMALL_BTN} aria-label={tt.disconnectLabel(c.accountLogin)} onClick={() => setDisconnecting(c)}>
                    {tt.disconnect}
                  </button>
                </>
              ) : undefined;
            return (
              <ConnectionRow key={c.id} connection={c} className={ROW_LINE} isAdmin={isAdmin} actions={actions}>
                {isAdmin && replacing === c.id && (
                  <ReplaceTokenForm
                    connection={c}
                    onCancel={() => setReplacing(null)}
                    onReplaced={(next) => {
                      setReplacing(null);
                      setNotice(tt.replaced(next.accountLogin));
                      refresh();
                    }}
                  />
                )}
              </ConnectionRow>
            );
          })}
        </ul>
      )}
      {hasToken && (
        <div className="mx-5 mb-4 mt-1 flex flex-wrap items-baseline gap-x-2 gap-y-1 border-t border-rule pt-3 text-small">
          <span className="font-semibold text-ink-soft">{tt.bodies}</span>
          <span className="text-muted">{tt.bodiesNote}</span>
        </div>
      )}
      {isAdmin && !githubInstallUrl && <p className="px-5 pb-5 text-small text-muted">{ct.notConfigured}</p>}
      {disconnecting && (
        <DisconnectTokenDialog
          connection={disconnecting}
          onClose={() => setDisconnecting(null)}
          onDone={() => {
            setNotice(tt.disconnected(disconnecting.accountLogin));
            setDisconnecting(null);
            setReplacing(null);
            if (open?.kind === "import" && open.connection.id === disconnecting.id) setOpen(null);
            refresh();
          }}
        />
      )}
    </section>
  );
}
