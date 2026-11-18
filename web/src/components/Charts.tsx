"use client";

import { Empty } from "@/components/Feedback";
import { polyline, trendGeometry } from "@/lib/charts";
import { useT } from "@/lib/i18n";
import { percent } from "@/lib/labels";
import type { SprintDay } from "@/lib/types";

const shortDay = (day: string) => day.slice(5).replace("-", ".");

/** Remaining issues per recorded day (solid) against the ideal line (dashed). Sits on the canvas-coloured sprint block. */
export function RemainingTrend({
  name,
  startDate,
  endDate,
  trend,
  showWeight,
}: {
  name: string;
  startDate: string;
  endDate: string;
  trend: SprintDay[];
  showWeight: boolean;
}) {
  const t = useT();
  const sp = t.overview.sprint;
  const g = trendGeometry(startDate, endDate, trend);
  const latest = trend[trend.length - 1];

  return (
    <div className="px-5 pb-5 pt-3 [--spark-bg:var(--wb-canvas)]">
      <div className="mb-2 flex flex-wrap items-baseline justify-between gap-x-3 gap-y-0.5">
        <h3 className="text-small font-semibold text-ink-soft">{sp.trendTitle}</h3>
        {g && latest && (
          <span className="text-small text-muted">
            {sp.remaining} <b className="font-mono font-medium text-ink">{latest.remaining}</b>
            {sp.unit(latest.remaining)}
          </span>
        )}
      </div>

      {!g ? (
        <Empty>{sp.trendEmpty}</Empty>
      ) : (
        <>
          <div className="relative h-14" aria-hidden>
            <svg className="spark" viewBox="0 0 100 100" preserveAspectRatio="none">
              <line className="spark-base" x1="0" y1="100" x2="100" y2="100" />
              {g.ideal && <polyline className="spark-ideal" points={polyline(g.ideal)} />}
              {g.runs
                .filter((run) => run.length > 1)
                .map((run, i) => (
                  <polyline key={i} className="spark-line" points={polyline(run)} />
                ))}
            </svg>
            {g.runs
              .filter((run) => run.length === 1)
              .map(([p], i) => (
                <span key={i} className="spark-dot" data-small="" style={{ left: `${p.x}%`, top: `${p.y}%` }} />
              ))}
            <span className="spark-dot" style={{ left: `${g.last.x}%`, top: `${g.last.y}%` }} />
            <div className="absolute inset-0 flex">
              {g.rows.map((r) => (
                <span key={r.day} className="h-full flex-1" title={sp.point(shortDay(r.day), r.remaining, r.ideal)} />
              ))}
            </div>
          </div>

          <div className="mt-1.5 flex justify-between font-mono text-meta text-muted" aria-hidden>
            <span>{shortDay(startDate)}</span>
            <span>{shortDay(endDate)}</span>
          </div>

          <div className="mt-2 flex flex-wrap items-center gap-x-4 gap-y-1 text-small text-muted" aria-hidden>
            <span className="inline-flex items-center gap-1.5">
              <i className="inline-block h-0.5 w-4 rounded-full bg-ink-soft" />
              {sp.remaining}
            </span>
            <span className="inline-flex items-center gap-1.5">
              <i className="inline-block w-4 border-t border-dashed border-muted" />
              {sp.ideal}
            </span>
            {showWeight && latest && latest.scopeWeight > 0 && (
              <span className="ml-auto">{sp.byWeight(percent(latest.remainingWeight, latest.scopeWeight))}</span>
            )}
          </div>
          {g.hasGaps && <p className="mt-1 text-small text-muted">{sp.gaps}</p>}

          <div className="sr-only">
            <table>
              <caption>{sp.tableCaption(name)}</caption>
              <thead>
                <tr>
                  <th scope="col">{sp.day}</th>
                  <th scope="col">{sp.remaining}</th>
                  <th scope="col">{sp.ideal}</th>
                </tr>
              </thead>
              <tbody>
                {g.rows.map((r) => (
                  <tr key={r.day}>
                    <th scope="row">{shortDay(r.day)}</th>
                    <td>{r.remaining ?? sp.noRecord}</td>
                    <td>{r.ideal ?? "-"}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {showWeight && latest && latest.scopeWeight > 0 && (
            <p className="sr-only">{sp.byWeight(percent(latest.remainingWeight, latest.scopeWeight))}</p>
          )}
        </>
      )}
    </div>
  );
}

/** Issues finished per week for the last 8 weeks, oldest first; the current week is the last column. */
export function WeeklyDone({ weeks, done }: { weeks: string[]; done: number[] }) {
  const t = useT();
  const pt = t.projects;
  const top = Math.max(1, ...done);
  const thisWeek = done[done.length - 1] ?? 0;
  const items = weeks.map((w, i) => pt.weekItem(shortDay(w), done[i] ?? 0)).join(", ");

  return (
    <div className="mt-3 border-t border-rule pt-2.5">
      <div className="mb-2 flex items-baseline justify-between gap-3 text-small text-muted">
        <span>{pt.weekly}</span>
        <span>
          {pt.thisWeek} <b className="font-mono font-medium text-ink">{thisWeek}</b>
          {pt.unit(thisWeek)}
        </span>
      </div>
      <div className="cols" role="img" aria-label={pt.weeklyAria(items)}>
        {done.map((n, i) => (
          <span
            key={weeks[i]}
            title={pt.weekItem(shortDay(weeks[i]), n)}
            data-past={i < done.length - 1 ? "" : undefined}
            data-zero={n === 0 ? "" : undefined}
            style={n > 0 ? { height: `${Math.max((n / top) * 100, 8)}%` } : undefined}
          />
        ))}
      </div>
      <div className="mt-1 flex justify-between text-meta text-muted" aria-hidden>
        <span>{pt.weeksAgo(weeks.length - 1)}</span>
        <span>{pt.thisWeek}</span>
      </div>
    </div>
  );
}
