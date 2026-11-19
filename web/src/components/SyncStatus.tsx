"use client";

import Link from "next/link";
import { useEffect, useRef, useState } from "react";
import { ApiError, ISSUES_CHANGED_EVENT, SYNC_CHANGED_EVENT, SYNC_STATUS_PATH, runSync, useApi } from "@/lib/api";
import { supabase } from "@/lib/auth";
import { showMessage } from "@/lib/boardEdits";
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
export function SyncStatus({
  className = "",
  onAttention,
  onContentChange,
  compact = false,
}: {
  className?: string;
  onAttention?: (on: boolean) => void;
  /** Changes whenever the shown text may change width (notice, running, result, tokens), so the shell re-measures. */
  onContentChange?: (key: string) => void;
  /** The top bar is out of room: keep the token mark, drop its text. */
  compact?: boolean;
}) {
  const { data, reload } = useApi<Status>(SYNC_STATUS_PATH);
  const me = useMe().data;
  const admin = me?.member.role === "admin";
  const tenantId = me?.tenant.id ?? null;
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

  // A finished sync (lastSyncedAt moved) may have brought new issues; tell the issue-showing views to refetch,
  // so the overview and boards update after a connect or "지금 동기화" without a page reload. The first loaded
  // status only seeds the baseline (no event), and a user/tenant switch (data back to null) reseeds it.
  const lastSynced = data?.lastSyncedAt ?? null;
  const seenSync = useRef<string | null | undefined>(undefined);
  useEffect(() => {
    if (!data) {
      seenSync.current = undefined;
      return;
    }
    if (seenSync.current === undefined) {
      seenSync.current = lastSynced;
      return;
    }
    if (seenSync.current !== lastSynced) {
      seenSync.current = lastSynced;
      if (lastSynced !== null) window.dispatchEvent(new Event(ISSUES_CHANGED_EVENT));
    }
  }, [data, lastSynced]);

  // A sync that just failed raises a global toast, so a silent failure is noticed even away from this bar.
  // Only on the transition into "failed"; the first loaded status just seeds the baseline.
  const lastResult = data?.lastResult ?? null;
  const seenResult = useRef<string | null | undefined>(undefined);
  useEffect(() => {
    if (!data) {
      seenResult.current = undefined;
      return;
    }
    if (seenResult.current === undefined) {
      seenResult.current = lastResult;
      return;
    }
    if (seenResult.current !== lastResult) {
      seenResult.current = lastResult;
      if (lastResult === "failed") showMessage({ title: s.failed, detail: data.lastError ?? s.unknownReason, failed: true });
    }
  }, [data, lastResult, s]);

  // Push: a private Supabase Realtime channel per tenant gets a `sync_finished` event the moment a sync ends,
  // so we reload the status now instead of waiting for the poll. The reload moves lastSyncedAt, and the effect
  // above then refreshes the boards. The channel carries no issue data; the API still enforces access on refetch.
  // If Realtime is off or the subscribe fails, the polling above keeps everything fresh, so failures are silent.
  useEffect(() => {
    const sb = supabase();
    if (!sb || !tenantId) return;
    let active = true;
    let channel: ReturnType<typeof sb.channel> | null = null;
    (async () => {
      try {
        await sb.realtime.setAuth();
        if (!active) return;
        channel = sb
          .channel(`tenant:${tenantId}`, { config: { private: true } })
          .on("broadcast", { event: "sync_finished" }, () => reload())
          .subscribe();
      } catch {
        // Realtime unavailable; polling covers it.
      }
    })();
    return () => {
      active = false;
      if (channel) void sb.removeChannel(channel);
    };
  }, [tenantId, reload]);

  const alert = data?.tokens.some((h) => urgency(h) < 3) ?? false;
  useEffect(() => onAttention?.(alert), [alert, onAttention]);
  const contentKey = [
    notice,
    data?.running,
    data?.lastResult,
    data?.tokens.map((h) => `${h.connectionId}:${h.tokenStatus}:${h.tokenExpiresSoon}:${h.syncPausedUntil !== null}`).join(","),
  ].join("|");
  useEffect(() => onContentChange?.(contentKey), [contentKey, onContentChange]);

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
  if (data.running) label = s.running;
  else if (failed) {
    // lastSyncedAt is the last successful sync, not the failed attempt.
    label = data.lastSyncedAt ? s.failedSince(fmt.relativeTime(data.lastSyncedAt)) : s.failed;
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
          className="flex items-center gap-2 rounded-control px-1 py-0.5 hover:bg-hover max-[1023px]:min-w-0 max-[1023px]:basis-full max-[1023px]:overflow-hidden"
        >
          <span className="shrink-0">
            <TokenMarks health={first} detailClassName="min-[1024px]:hidden" />
          </span>
          <span className="min-w-0 truncate text-muted min-[1024px]:hidden">{tokenSummary(first, t, fmt)}</span>
          {!compact && <span className="min-w-0 max-w-[8rem] truncate text-muted max-[1279px]:hidden">{tokenShort(first, t, fmt)}</span>}
          {attention.length > 1 && <span className="shrink-0 font-mono text-meta text-muted">+{attention.length - 1}</span>}
        </Link>
      )}
      {/* Texts are capped in the top bar; the whole sentence stays in `title`, and a notice also in the live region. */}
      <p
        className="flex min-w-0 shrink-0 items-center gap-1.5 whitespace-nowrap"
        title={[notice ?? label, failed && !notice ? reason : null].filter(Boolean).join(" · ")}
      >
        <span
          aria-hidden
          className={`inline-block h-1.5 w-1.5 shrink-0 rounded-full ${data.running ? "animate-pulse bg-ink-soft" : failed ? "bg-ink" : "bg-faint"}`}
        />
        <span
          className={`truncate ${compact ? "min-[1024px]:max-w-[8rem]" : "min-[1024px]:max-w-[13rem]"} ${
            failed && !notice ? "font-medium text-ink" : "text-muted"
          } ${notice || data.running || failed ? "" : "min-[1024px]:max-[1279px]:sr-only"}`}
        >
          {notice ?? label}
        </span>
        {failed && !notice && (
          <span className={`max-w-[10rem] truncate text-muted ${compact ? "sr-only" : "max-[1279px]:sr-only"}`}>· {reason}</span>
        )}
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
