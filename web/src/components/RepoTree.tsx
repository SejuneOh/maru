"use client";

import { useId, useMemo, useState } from "react";
import { Card, Count, GitHubLink } from "@/components/ui";
import { useT } from "@/lib/i18n";
import type { RepoTreeNode } from "@/lib/types";

/** Entries shown per folder before the rest collapse into "N more". */
const SHOWN = 12;

const ASCII = /^[\x20-\x7e]*$/;
const mono = (s: string) => (ASCII.test(s) ? "font-mono" : "");

function sorted(nodes: RepoTreeNode[]): RepoTreeNode[] {
  return [...nodes].sort((a, b) =>
    a.type === b.type ? a.name.localeCompare(b.name, "en", { sensitivity: "base" }) : a.type === "dir" ? -1 : 1,
  );
}

function depthOf(path: string) {
  return path.split("/").length;
}

type Ctx = {
  open: Set<string>;
  toggle: (path: string) => void;
  /** GitHub page of a folder in the default branch. */
  folderUrl: (path: string) => string;
  /**
   * A folder with `children: []` is either cut off by the server or holds nothing the tree lists (git keeps no
   * empty folders; submodules are left out). It is cut off when the whole tree was truncated or it sits on the
   * deepest level the server read.
   */
  cutOff: (node: RepoTreeNode) => boolean;
  /** Stable DOM id per path, so two views of the same tree never share an id. */
  regionId: (path: string) => string;
};

export function RepoTree({
  tree,
  truncated,
  fullName,
  branch,
}: {
  tree: RepoTreeNode[];
  truncated: boolean;
  fullName: string;
  branch: string;
}) {
  const t = useT().repo;
  const [mode, setMode] = useState<"boxes" | "list">("boxes");
  const [open, setOpen] = useState<Set<string>>(() => new Set());
  const boxesId = useId();
  const listId = useId();

  const { index, deepest } = useMemo(() => {
    const index = new Map<string, number>();
    let deepest = 0;
    const walk = (nodes: RepoTreeNode[]) => {
      for (const n of nodes) {
        index.set(n.path, index.size);
        deepest = Math.max(deepest, depthOf(n.path));
        if (n.children) walk(n.children);
      }
    };
    walk(tree);
    return { index, deepest };
  }, [tree]);

  const base = `https://github.com/${fullName}/tree/${encodeURIComponent(branch)}`;
  const ctx = (viewId: string): Ctx => ({
    open,
    toggle: (path) =>
      setOpen((prev) => {
        const next = new Set(prev);
        if (next.has(path)) next.delete(path);
        else next.add(path);
        return next;
      }),
    folderUrl: (path) => `${base}/${path.split("/").map(encodeURIComponent).join("/")}`,
    cutOff: (node) => truncated || depthOf(node.path) >= deepest,
    regionId: (path) => `${viewId}-n${index.get(path) ?? 0}`,
  });

  const roots = sorted(tree);
  const folders = roots.filter((n) => n.type === "dir");
  const files = roots.filter((n) => n.type === "file");

  return (
    <Card
      title={t.tree}
      hint={t.treeHint}
      className="@container"
      action={
        <div role="group" aria-label={t.viewLabel} className="seg hidden @[35rem]:inline-flex">
          {(["boxes", "list"] as const).map((m) => (
            <button key={m} type="button" aria-pressed={mode === m} onClick={() => setMode(m)}>
              {m === "boxes" ? t.viewBoxes : t.viewList}
            </button>
          ))}
        </div>
      }
    >
      <div className="flex flex-col gap-3 px-5 pb-5">
        {truncated && <p className="text-small text-muted">{t.truncated}</p>}
        {roots.length === 0 ? (
          <p className="text-small text-muted">{t.noTree}</p>
        ) : (
          <>
            <div className={`${mode === "boxes" ? "hidden @[35rem]:grid" : "hidden"} grid-cols-2 items-start gap-3 @[56rem]:grid-cols-3`}>
              {folders.map((f) => (
                <Box key={f.path} title={f.name} titleTitle={f.path} nodes={f.children ?? []} folder={f} ctx={ctx(boxesId)} />
              ))}
              {files.length > 0 && <Box title={t.rootFiles} nodes={files} folder={null} ctx={ctx(boxesId)} />}
            </div>
            <div className={mode === "list" ? "" : "@[35rem]:hidden"}>
              <Entries nodes={roots} folder={null} level={1} ctx={ctx(listId)} />
            </div>
          </>
        )}
      </div>
    </Card>
  );
}

function Box({
  title,
  titleTitle,
  nodes,
  folder,
  ctx,
}: {
  title: string;
  titleTitle?: string;
  nodes: RepoTreeNode[];
  folder: RepoTreeNode | null;
  ctx: Ctx;
}) {
  return (
    <section className="tree-box">
      <h3 className="mb-1.5 flex min-w-0 items-center gap-2">
        <span className={`truncate text-ui font-semibold text-ink ${folder ? mono(title) : ""}`} title={titleTitle ?? title}>
          {title}
        </span>
        <Count n={nodes.length} />
      </h3>
      <Entries nodes={nodes} folder={folder} level={2} ctx={ctx} />
    </section>
  );
}

/**
 * The entries of one folder. Level 1 folders always show their children (in the list view; the box view puts each in a
 * box), level 2 folders open one more level on demand, and level 3 is the last the page draws.
 */
function Entries({ nodes, folder, level, ctx }: { nodes: RepoTreeNode[]; folder: RepoTreeNode | null; level: number; ctx: Ctx }) {
  const t = useT().repo;
  if (nodes.length === 0) {
    if (!folder) return null;
    return ctx.cutOff(folder) ? (
      <GitHubLink href={ctx.folderUrl(folder.path)} label={t.onGithub} className="link inline-flex items-center gap-1 text-small text-muted">
        {t.onGithub}
      </GitHubLink>
    ) : (
      <p className="text-small text-muted">{t.empty}</p>
    );
  }
  const list = sorted(nodes);
  const shown = list.slice(0, SHOWN);
  const rest = list.length - shown.length;
  return (
    <ul className="min-w-0">
      {shown.map((n) => (
        <Entry key={n.path} node={n} level={level} ctx={ctx} />
      ))}
      {rest > 0 && (
        <li className="pt-1">
          <GitHubLink
            href={folder ? ctx.folderUrl(folder.path) : ctx.folderUrl("").replace(/\/$/, "")}
            label={t.moreOnGithub(rest)}
            className="link inline-flex items-center gap-1 text-small text-muted"
          >
            {t.moreOnGithub(rest)}
          </GitHubLink>
        </li>
      )}
    </ul>
  );
}

function Entry({ node, level, ctx }: { node: RepoTreeNode; level: number; ctx: Ctx }) {
  const name = (
    <span className={`min-w-0 truncate text-ui ${node.type === "dir" ? "text-ink" : "text-muted"} ${mono(node.name)}`} title={node.path}>
      {node.name}
    </span>
  );
  if (node.type === "file") return <li className="tree-row pl-4">{name}</li>;

  const children = node.children ?? [];
  if (level === 1)
    return (
      <li className="min-w-0">
        <div className="tree-row">
          <Caret />
          {name}
        </div>
        <div className="tree-sub">
          <Entries nodes={children} folder={node} level={2} ctx={ctx} />
        </div>
      </li>
    );

  if (level === 2) {
    const expanded = ctx.open.has(node.path);
    const region = ctx.regionId(node.path);
    return (
      <li className="min-w-0">
        <button
          type="button"
          className="tree-row cursor-pointer rounded-control hover:bg-hover"
          aria-expanded={expanded}
          aria-controls={region}
          onClick={() => ctx.toggle(node.path)}
        >
          <Caret />
          {name}
        </button>
        <div id={region} hidden={!expanded}>
          {expanded && (
            <div className="tree-sub pb-1">
              <Entries nodes={children} folder={node} level={3} ctx={ctx} />
            </div>
          )}
        </div>
      </li>
    );
  }

  return (
    <li className="tree-row">
      <Caret />
      {name}
    </li>
  );
}

function Caret() {
  return (
    <svg data-caret width="10" height="10" viewBox="0 0 10 10" aria-hidden className="shrink-0">
      <path d="M3 1.5 7 5 3 8.5z" fill="currentColor" />
    </svg>
  );
}
