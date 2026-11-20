"use client";

import { ApiError } from "@/lib/api";
import { authEnabled } from "@/lib/auth";
import { useT } from "@/lib/i18n";

export function Loading({ label }: { label?: string }) {
  const t = useT();
  return <p className="px-5 py-5 text-ui text-muted">{label ?? t.common.loading}…</p>;
}

/** One shimmering placeholder block. Build larger skeletons by composing several. */
export function Skeleton({ className = "" }: { className?: string }) {
  return <span aria-hidden className={`skeleton block ${className}`} />;
}

/** A board-shaped skeleton: four columns, each with a heading and a few card placeholders. */
export function BoardSkeleton() {
  const t = useT();
  return (
    <div className="@container" aria-busy aria-label={t.common.loading}>
      <div className="grid grid-cols-1 items-start gap-x-4 gap-y-6 @[34rem]:grid-cols-2 @[64rem]:grid-cols-4">
        {[0, 1, 2, 3].map((col) => (
          <div key={col} className="flex min-w-0 flex-col gap-2">
            <div className="flex items-center gap-2 px-1 pb-1">
              <Skeleton className="h-2 w-2 rounded-full" />
              <Skeleton className="h-3.5 w-20" />
            </div>
            {Array.from({ length: 3 - col % 2 }).map((_, i) => (
              <div key={i} className="rounded-item bg-surface px-4 py-3.5 shadow-elev-1">
                <Skeleton className="h-2.5 w-24" />
                <Skeleton className="mt-2 h-3.5 w-full" />
                <Skeleton className="mt-2 h-2.5 w-16" />
              </div>
            ))}
          </div>
        ))}
      </div>
    </div>
  );
}

/** Errors are about the app, not the work, so they stay in ink: no vermilion here. */
export function ErrorBox({ error }: { error: Error }) {
  const t = useT();
  const message =
    error instanceof ApiError && error.status === 401
      ? authEnabled
        ? t.errors.signInAgain
        : t.errors.pickUser
      : error instanceof ApiError && error.status === 404
        ? t.errors.notFound
        : t.errors.noServer;
  return (
    <div role="alert" className="rounded-control border-l-2 border-ink-soft bg-surface px-4 py-3 text-ui text-ink shadow-elev-1">
      {message}
    </div>
  );
}

export function Empty({ children }: { children: React.ReactNode }) {
  return <p className="rounded-item border border-dashed border-rule-strong px-4 py-3 text-small text-muted">{children}</p>;
}
