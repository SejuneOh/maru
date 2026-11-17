"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { ApiError, SYNC_CHANGED_EVENT, SYNC_STATUS_PATH, runSync, useApi } from "@/lib/api";
import { type Dict, type Format, useFormat, useT } from "@/lib/i18n";
import { useMe } from "@/lib/me";
import type { RunSyncError, SyncStatus as Status, TokenHealth } from "@/lib/types";
import { TokenMarks, urgency } from "./TokenConnection";

const IDLE_POLL_MS = 60_000;
const RUNNING_POLL_MS = 5_000;
const NOTICE_MS = 6_000;

/** "acme · @octocat의 토큰 · 12월 1일 만료": whose token keeps the sync going and until when. */
function tokenSummary(h: TokenHealth, t: Dict, fmt: Format): string {
  const tt = t.tokens;
  const owner = h.tokenOwnerLogin ? tt.kindToken(h.tokenOwnerLogin) : tt.status.token_required;
  const parts = [h.accountLogin, owner];
  if (h.tokenStatus !== "token_required") parts.push(h.tokenExpiresAt ? tt.expiresOn(fmt.date(h.tokenExpiresAt)) : tt.noExpiry);
  return parts.join(" · ");
}

/** "@octocat · 12.01 만료", or "토큰 필요 · acme" when stopped, for the crowded top bar. */
function tokenShort(h: TokenHealth, t: Dict, fmt: Format): string {
  const tt = t.tokens;
  const who = h.tokenOwnerLogin ? `@${h.tokenOwnerLogin}` : h.accountLogin;
  if (h.tokenStatus !== "active") return `${tt.status[h.tokenStatus]} · ${who}`;
  return `${who} · ${h.tokenExpiresAt ? tt.expiresOn(fmt.shortDate(h.tokenExpiresAt)) : tt.noExpiry}`;
}

/**
 * "Synced 3 min ago" for the top bar, refreshed every minute (every 5 s while a sync runs) while the tab is visible.
 * Members see nothing until GitHub is connected; admins get a link to settings and a "sync now" button.
 */
export function SyncStatus({ className = "" }: { className?: string }) {
  const { data, reload } = useApi<Status>(SYNC_STATUS_PATH);
  const admin = useMe().data?.member.role === "admin";
  const t = useT();
  const s = t.sync;
  const fmt = useFormat();
  const [starting, setStarting] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const running = data?.running === true || starting;

  useEffect(() => {
    const id = setInterval(() => {
      if (!document.hidden) reload();
    }, running ? RUNNING_POLL_MS : IDLE_POLL_MS);
    const onVisible = () => {
      if (!document.hidden) reload();
    };
    document.addEventListener("visibilitychange", onVisible);
    window.addEventListener(SYNC_CHANGED_EVENT, reload);
    return () => {
      clearInterval(id);
      document.removeEventListener("visibilitychange", onVisible);
      window.removeEventListener(SYNC_CHANGED_EVENT, reload);
    };
  }, [reload, running]);

  useEffect(() => {
    if (!notice) return;
    const id = setTimeout(() => setNotice(null), NOTICE_MS);
    return () => clearTimeout(id);
  }, [notice]);

  if (!data) return null;

  if (!data.connected) {
    if (!admin) return null;
    return (
      <p className={`items-center gap-2 whitespace-nowrap text-small text-muted ${className}`}>
        <span>{s.notConnected}</span>
        <Link href="/settings" className="link text-ink-soft">
          {s.connect}
        </Link>
      </p>
    );
  }

  const start = async () => {
    setStarting(true);
    setNotice(null);
    try {
      await runSync();
      setNotice(s.started);
    } catch (e) {
      const code = (e instanceof ApiError ? e.code : null) as RunSyncError | null;
      const messages: Record<RunSyncError, string> = {
        sync_running: s.alreadyRunning,
        not_connected: s.notConnectedError,
        github_app_not_configured: s.notConfigured,
        admin_only: s.adminOnly,
      };
      setNotice(code && code in messages ? messages[code] : s.runFailed);
    } finally {
      setStarting(false);
      reload();
    }
  };

  const failed = !data.running && data.lastResult === "failed";
  const reason = data.lastError ?? s.unknownReason;
  let label: string;
  let title: string | undefined;
  if (data.running) label = s.running;
  else if (failed) {
    // lastSyncedAt is the last successful sync, not the failed attempt.
    label = data.lastSyncedAt ? s.failedSince(fmt.relativeTime(data.lastSyncedAt)) : s.failed;
    title = reason;
  } else if (data.lastSyncedAt) label = s.syncedAgo(fmt.relativeTime(data.lastSyncedAt));
  else label = s.never;

  // Only tokens that need a look reach the sync row; healthy ones are on the settings rows.
  const attention = data.tokens.filter((h) => urgency(h) < 3).sort((a, b) => urgency(a) - urgency(b));
  const first = attention[0];

  return (
    <div className={`items-center gap-x-2 gap-y-1 text-small max-[1023px]:min-w-0 max-[1023px]:flex-wrap ${className}`}>
      {first && (
        <Link
          href="/settings"
          title={attention.map((h) => tokenSummary(h, t, fmt)).join("\n")}
          className="flex min-w-0 shrink items-center gap-2 overflow-hidden rounded-control hover:bg-hover max-[1023px]:basis-full min-[1024px]:max-[1279px]:min-w-fit"
        >
          <span className="shrink-0">
            <TokenMarks health={first} detailClassName="min-[1024px]:hidden" />
          </span>
          <span className="min-w-0 truncate text-muted min-[1024px]:hidden">{tokenSummary(first, t, fmt)}</span>
          <span className="min-w-0 max-w-[10rem] truncate text-muted max-[1279px]:hidden">{tokenShort(first, t, fmt)}</span>
          {attention.length > 1 && <span className="shrink-0 font-mono text-meta text-muted">+{attention.length - 1}</span>}
        </Link>
      )}
      <p className={`flex shrink-0 items-center gap-1.5 whitespace-nowrap ${first ? "min-[1024px]:max-[1279px]:sr-only" : ""}`} title={title}>
        <span
          aria-hidden
          className={`inline-block h-1.5 w-1.5 shrink-0 rounded-full ${data.running ? "animate-pulse bg-ink-soft" : failed ? "bg-ink" : "bg-faint"}`}
        />
        <span className={failed && !notice ? "font-medium text-ink" : "text-muted"}>{notice ?? label}</span>
        {failed && !notice && <span className="max-w-[16rem] truncate text-muted max-[1279px]:sr-only">· {reason}</span>}
      </p>
      <span aria-live="polite" className="sr-only">
        {notice}
      </span>
      {admin && (
        <button type="button" onClick={start} disabled={running} className="btn h-8 shrink-0 px-2.5 text-meta font-medium text-ink-soft hover:text-ink">
          {s.runNow}
        </button>
      )}
    </div>
  );
}
