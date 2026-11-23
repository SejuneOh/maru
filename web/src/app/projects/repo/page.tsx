"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Empty, ErrorBox, Loading } from "@/components/Feedback";
import { Markdown } from "@/components/Markdown";
import { RepoTree } from "@/components/RepoTree";
import { Card, GitHubLink, PageHeader, ProgressBar, progressLine } from "@/components/ui";
import { ApiError, useApi } from "@/lib/api";
import { NO_FILTERS, issuesQuery } from "@/lib/filters";
import { useFormat, useT } from "@/lib/i18n";
import { isCanceled } from "@/lib/labels";
import type { BoardIssue, Project, RepoDependencyList, RepoLanguage, RepositoryInfo, StatusCounts } from "@/lib/types";

const ASCII = /^[\x20-\x7e]*$/;
const mono = (s: string) => (ASCII.test(s) ? "font-mono" : "");

/** Top four languages in grey steps from ink; everything after them shares one "other" slice. */
const LANG_TONES = ["bg-ink", "bg-ink-soft", "bg-muted", "bg-faint"];
const DEPS_SHOWN = 24;
const README_LINES = 40;
const README_CHARS = 4000;

function countByStatus(issues: BoardIssue[]): StatusCounts {
  const c: StatusCounts = { todo: 0, inProgress: 0, inReview: 0, done: 0 };
  for (const i of issues) {
    if (isCanceled(i)) continue;
    if (i.status === "todo") c.todo++;
    else if (i.status === "in_progress") c.inProgress++;
    else if (i.status === "in_review") c.inReview++;
    else c.done++;
  }
  return c;
}

/**
 * The opening of the README: HTML-only lines and comments dropped (they would show as literal text), cut at a blank
 * line near the limits. `cut` says whether anything was left out.
 */
function readmeSummary(src: string): { text: string; cut: boolean } {
  const lines = src
    .replace(/\r\n?/g, "\n")
    .replace(/<!--[\s\S]*?-->/g, "")
    .split("\n")
    .filter((l) => !/^\s*(<\/?[a-zA-Z][^>]*>\s*)+$/.test(l));
  const out: string[] = [];
  let chars = 0;
  let fence = false;
  for (const l of lines) {
    if (/^\s*```/.test(l)) fence = !fence;
    if (!fence && l.trim() === "" && (out.length >= README_LINES || chars >= README_CHARS)) break;
    if (out.length >= README_LINES * 2 || chars >= README_CHARS * 2) break;
    out.push(l);
    chars += l.length + 1;
  }
  const text = out.join("\n").trim();
  return { text, cut: out.length < lines.length && lines.slice(out.length).some((l) => l.trim() !== "") };
}

export default function RepositoryPage() {
  const t = useT();
  const rt = t.repo;
  const id = useSearchParams().get("repo");
  const info = useApi<RepositoryInfo>(id ? `/api/repositories/${encodeURIComponent(id)}/info` : null);
  const projects = useApi<Project[]>(id ? "/api/projects" : null);
  const issues = useApi<BoardIssue[]>(id ? issuesQuery({ ...NO_FILTERS, repository: id }, { scope: "all" }) : null);

  if (!id)
    return (
      <div className="flex flex-col gap-4">
        <PageHeader meta={rt.meta} title={rt.title} />
        <Empty>
          {rt.noRepo}{" "}
          <Link href="/projects" className="link">
            {rt.backToProjects}
          </Link>
        </Empty>
      </div>
    );

  if (info.error) {
    const hint =
      info.error instanceof ApiError && info.error.status === 429
        ? rt.rateLimited
        : info.error instanceof ApiError && info.error.status === 409
          ? rt.credential
          : null;
    return (
      <div className="flex flex-col gap-4">
        <PageHeader meta={rt.meta} title={rt.title} />
        {hint ? (
          <div role="alert" className="rounded-control border-l-2 border-ink-soft bg-surface px-4 py-3 text-ui text-ink shadow-elev-1">
            {hint}
          </div>
        ) : (
          <ErrorBox error={info.error} />
        )}
      </div>
    );
  }
  if (!info.data)
    return (
      <div className="flex flex-col gap-4">
        <PageHeader meta={rt.meta} title={rt.title} />
        <Loading />
      </div>
    );

  const d = info.data;
  const project = projects.data?.find((p) => p.repositories.some((r) => r.id === id)) ?? null;
  const repoName = d.fullName.split("/").pop() ?? d.fullName;
  const folders = d.tree.filter((n) => n.type === "dir").length;
  const depCount = d.dependencies.reduce((sum, l) => sum + l.items.length, 0);

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-start justify-between gap-x-6 gap-y-3">
        <PageHeader
          meta={
            <>
              {project && (
                <>
                  <Link href={`/projects?project=${project.id}`} className="link text-muted">
                    {project.name}
                  </Link>
                  {" · "}
                </>
              )}
              <span className="font-mono">{d.fullName}</span>
              {" · "}
              <span className={mono(d.defaultBranch)}>{d.defaultBranch}</span>
            </>
          }
          title={<span className={mono(repoName)}>{repoName}</span>}
        >
          {rt.summary(d.languages[0]?.name ?? null, folders, d.activity.openIssues, depCount)}
        </PageHeader>
        <GitHubLink href={`https://github.com/${d.fullName}`} label={rt.openOnGithub} className="btn shrink-0">
          {rt.openOnGithub}
        </GitHubLink>
      </div>

      <Card title={rt.status} className="reveal [animation-delay:30ms]">
        <div className="@container px-5 pb-5">
          <div className="grid grid-cols-1 gap-6 @[48rem]:grid-cols-3">
            <Stat label={rt.progress}>
              {issues.error ? (
                <p className="text-small text-muted">{rt.progressError}</p>
              ) : !issues.data ? (
                <span aria-hidden className="bar" />
              ) : (
                <Progress counts={countByStatus(issues.data)} />
              )}
            </Stat>
            <Stat label={rt.languages}>
              <Languages languages={d.languages} />
            </Stat>
            <Stat label={rt.activity}>
              <dl className="flex flex-wrap gap-x-5 gap-y-2">
                {(
                  [
                    [rt.openIssues, d.activity.openIssues],
                    [rt.openPrs, d.activity.openPrs],
                    [rt.doneThisWeek, d.activity.doneThisWeek],
                  ] as const
                ).map(([name, n]) => (
                  <div key={name} className="flex items-baseline gap-1.5">
                    <dt className="text-small text-muted">{name}</dt>
                    <dd className="font-mono text-ui text-ink">{n}</dd>
                  </div>
                ))}
              </dl>
            </Stat>
          </div>
        </div>
      </Card>

      <div className="reveal grid grid-cols-1 items-start gap-4 [animation-delay:60ms] lg:grid-cols-[3fr_2fr]">
        <Readme readme={d.readme} fullName={d.fullName} />
        <Dependencies lists={d.dependencies} />
      </div>

      <div className="reveal [animation-delay:90ms]">
        <RepoTree tree={d.tree} truncated={d.truncated} fullName={d.fullName} branch={d.defaultBranch} />
      </div>

      <Footer commit={d.baseCommit} generatedAt={d.generatedAt} />
    </div>
  );
}

function Stat({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <section className="flex min-w-0 flex-col gap-2.5">
      <h3 className="text-small font-semibold text-ink-soft">{label}</h3>
      {children}
    </section>
  );
}

function Progress({ counts }: { counts: StatusCounts }) {
  const t = useT();
  const total = counts.todo + counts.inProgress + counts.inReview + counts.done;
  return (
    <>
      <ProgressBar counts={counts} />
      {total > 0 && <p className="text-small text-muted">{progressLine(counts, t)}</p>}
    </>
  );
}

function Languages({ languages }: { languages: RepoLanguage[] }) {
  const rt = useT().repo;
  if (languages.length === 0) return <p className="text-small text-muted">{rt.noLanguages}</p>;
  const top = languages.slice(0, LANG_TONES.length);
  const restRatio = languages.slice(LANG_TONES.length).reduce((s, l) => s + l.ratio, 0);
  const slices = [
    ...top.map((l, i) => ({ name: l.name, ratio: l.ratio, tone: LANG_TONES[i] })),
    ...(restRatio > 0 ? [{ name: rt.otherLanguages, ratio: restRatio, tone: "bg-rule-strong" }] : []),
  ];
  const pct = (r: number) => `${(r * 100).toFixed(r < 0.1 ? 1 : 0)}%`;
  return (
    <>
      <div className="bar" role="img" aria-label={rt.languagesAria(slices.map((s) => `${s.name} ${pct(s.ratio)}`).join(", "))}>
        {slices.map((s) => (
          <span key={s.name} className={s.tone} style={{ width: `${s.ratio * 100}%` }} />
        ))}
      </div>
      <ul className="flex flex-wrap gap-x-4 gap-y-1.5" aria-hidden>
        {slices.map((s) => (
          <li key={s.name} className="inline-flex min-w-0 items-center gap-1.5 text-small text-ink-soft">
            <span className={`h-2 w-2 shrink-0 rounded-mark ${s.tone}`} />
            <span className="truncate">{s.name}</span>
            <span className="font-mono text-meta text-muted">{pct(s.ratio)}</span>
          </li>
        ))}
      </ul>
    </>
  );
}

function Readme({ readme, fullName }: { readme: string | null; fullName: string }) {
  const rt = useT().repo;
  if (readme === null || readme.trim() === "")
    return (
      <Card title={rt.readme}>
        <div className="px-5 pb-5">
          <Empty>{rt.noReadme}</Empty>
        </div>
      </Card>
    );
  const { text, cut } = readmeSummary(readme);
  const full = `https://github.com/${fullName}#readme`;
  return (
    <Card
      title={rt.readme}
      action={
        <GitHubLink href={full} label={rt.readmeFull} className="link inline-flex items-center gap-1">
          {rt.readmeFull}
        </GitHubLink>
      }
    >
      <div className="min-w-0 px-5 pb-5 text-ink-soft">
        <Markdown text={text} compact />
        {cut && <p className="mt-2 text-small text-muted">…</p>}
      </div>
    </Card>
  );
}

function Dependencies({ lists }: { lists: RepoDependencyList[] }) {
  const rt = useT().repo;
  return (
    <Card title={rt.dependencies}>
      <div className="flex flex-col gap-4 px-5 pb-5">
        {lists.length === 0 ? (
          <Empty>{rt.noDependencies}</Empty>
        ) : (
          lists.map((l) => {
            const rest = l.items.length - DEPS_SHOWN;
            return (
              <section key={l.file} className="flex min-w-0 flex-col gap-2">
                <h3 className="flex min-w-0 items-center gap-2">
                  <span className={`truncate text-small font-semibold text-ink ${mono(l.file)}`} title={l.file}>
                    {l.file}
                  </span>
                  <span className="tag shrink-0">{l.manager}</span>
                </h3>
                <ul className="flex flex-wrap gap-1.5">
                  {l.items.slice(0, DEPS_SHOWN).map((item) => (
                    <li key={item} className={`tag max-w-full ${mono(item)}`} title={item}>
                      <span className="min-w-0 truncate">{item}</span>
                    </li>
                  ))}
                  {rest > 0 && <li className="inline-flex h-[22px] items-center text-meta text-muted">{rt.moreDeps(rest)}</li>}
                </ul>
              </section>
            );
          })
        )}
      </div>
    </Card>
  );
}

function Footer({ commit, generatedAt }: { commit: string; generatedAt: string }) {
  const rt = useT().repo;
  const fmt = useFormat();
  return (
    <p className="text-small text-muted">
      {rt.generated} · {rt.baseCommit} <span className="font-mono">{commit.slice(0, 7)}</span> ·{" "}
      <span className="tabular-nums">
        {fmt.dayLabel(generatedAt)} {fmt.time(generatedAt)}
      </span>
    </p>
  );
}
