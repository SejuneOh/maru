"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { useState } from "react";
import { ErrorBox, Loading } from "@/components/Feedback";
import { NoteEditor, canDelete, confirmAndDelete } from "@/components/NoteEditor";
import { useIssuePanel } from "@/components/useIssuePanel";
import { Avatar, Card, Flag, PageHeader, StatusBadge } from "@/components/ui";
import { useApi } from "@/lib/api";
import { useFormat, useT } from "@/lib/i18n";
import { useMe } from "@/lib/me";
import type { MeetingNote } from "@/lib/types";

/** `?id=` reads a note, `?id=&edit=1` edits it, `?new=1` writes a new one. */
export default function MeetingNotePage() {
  const params = useSearchParams();
  const id = params.get("id");
  const isNew = params.get("new") === "1";
  if (isNew || (id && params.get("edit") === "1")) return <EditScreen id={isNew ? null : id} />;
  return <ReadScreen id={id} />;
}

/**
 * Keeps the same editor across the URL change after the first save (`?new=1` → `?id=…&edit=1`), so the
 * person's typing and focus survive it.
 */
function EditScreen({ id }: { id: string | null }) {
  const router = useRouter();
  const [adopted, setAdopted] = useState<string | null>(null);
  const skip = id === null || id === adopted;
  const note = useApi<MeetingNote>(skip ? null : `/api/meeting-notes/${id}`);

  if (!skip && note.error) return <ErrorBox error={note.error} />;
  if (!skip && !note.data) return <Loading />;
  return (
    <NoteEditor
      initial={skip ? null : note.data}
      onCreated={(n) => {
        setAdopted(n.id);
        router.replace(`/notes/view?id=${n.id}&edit=1`);
      }}
    />
  );
}

function ReadScreen({ id }: { id: string | null }) {
  const router = useRouter();
  const { open } = useIssuePanel();
  const t = useT();
  const nt = t.notes;
  const fmt = useFormat();
  const me = useMe().data?.member;
  const note = useApi<MeetingNote>(id ? `/api/meeting-notes/${id}` : null);
  const [deleting, setDeleting] = useState(false);

  if (!id) return <p className="text-ui text-muted">{nt.pick}</p>;
  if (note.error) return <ErrorBox error={note.error} />;
  if (!note.data) return <Loading />;
  const n = note.data;
  const missing = n.actionItems.filter((a) => !a.issue).length;

  const remove = async () => {
    setDeleting(true);
    const gone = await confirmAndDelete(n, nt);
    setDeleting(false);
    if (gone) router.push("/notes");
  };

  return (
    <article className="flex max-w-[820px] flex-col gap-6">
      <Link href="/notes" className="link self-start text-small text-ink-soft">
        {nt.back}
      </Link>
      <div className="flex flex-wrap items-start justify-between gap-4">
        <PageHeader
          meta={[fmt.date(n.heldAt), n.project?.name, n.createdBy && nt.author(fmt.person(n.createdBy))].filter(Boolean).join(" · ")}
          title={n.title}
        >
          {nt.attendees(n.attendees.length ? n.attendees.map(fmt.person).join(", ") : nt.noAttendees)}
        </PageHeader>
        <div className="flex shrink-0 gap-2">
          {canDelete(n, me) && (
            <button type="button" className="btn" disabled={deleting} aria-busy={deleting || undefined} onClick={() => void remove()}>
              {deleting ? `${nt.deleting}…` : nt.delete}
            </button>
          )}
          <Link href={`/notes/view?id=${n.id}&edit=1`} className="btn btn-primary">
            {nt.edit}
          </Link>
        </div>
      </div>

      <Card title={nt.decisions} count={n.decisions.length} hint={nt.decisionsHint} className="reveal mt-2 [animation-delay:30ms]">
        {n.decisions.length === 0 ? (
          <p className="px-5 pb-4 text-small text-muted">{nt.noDecisions}</p>
        ) : (
          <ol className="pb-2">
            {n.decisions.map((d, i) => (
              <li
                key={i}
                className="relative flex gap-3 px-5 py-3 after:absolute after:inset-x-5 after:top-0 after:h-px after:bg-rule first:after:hidden"
              >
                <span aria-hidden className="w-4 shrink-0 pt-0.5 text-right font-mono text-meta text-muted">
                  {i + 1}
                </span>
                <span className="min-w-0 break-keep text-ui">{d}</span>
              </li>
            ))}
          </ol>
        )}
      </Card>

      <Card
        title={nt.actionItems}
        count={n.actionItems.length}
        hint={nt.actionHint}
        action={missing > 0 ? <span>{nt.missingCount(missing)}</span> : null}
        className="reveal [animation-delay:60ms]"
      >
        {n.actionItems.length === 0 ? (
          <p className="px-5 py-4 text-small text-muted">{nt.noActionItems}</p>
        ) : (
          <ul>
            {n.actionItems.map((a) => (
              <li key={a.id} className="flex flex-col gap-1 relative px-5 py-4 after:absolute after:inset-x-5 after:top-0 after:h-px after:bg-rule first:after:hidden">
                <span className="text-ui font-medium">{a.text}</span>
                <span className="flex flex-wrap items-center gap-x-3 gap-y-1.5 text-small text-ink-soft">
                  {a.assignee ? (
                    <span className="inline-flex items-center gap-1.5">
                      <Avatar person={a.assignee} size="sm" />
                      {fmt.person(a.assignee)}
                    </span>
                  ) : (
                    <span className="text-accent">{t.common.unassigned}</span>
                  )}
                  {a.dueDate && <span className="tabular-nums">{nt.due(fmt.date(`${a.dueDate}T00:00:00`))}</span>}
                  {a.issue ? (
                    <button
                      type="button"
                      onClick={() => open(a.issue!.id)}
                      className="inline-flex cursor-pointer items-center gap-2 [&>.font-mono]:hover:underline [&>.font-mono]:hover:underline-offset-3"
                    >
                      <span className="font-mono">
                        {a.issue.repositoryName} #{a.issue.number}
                      </span>
                      <StatusBadge status={a.issue.status} />
                    </button>
                  ) : (
                    <>
                      <Flag>{t.common.noIssue}</Flag>
                      <span className="text-muted" title={nt.createIssueTitle}>
                        {nt.createIssue}
                      </span>
                    </>
                  )}
                </span>
              </li>
            ))}
          </ul>
        )}
      </Card>

      <Card title={nt.body} hint={nt.bodyHint} className="reveal [animation-delay:90ms]">
        {n.bodyMd ? (
          <pre className="whitespace-pre-wrap break-keep p-4 font-sans text-body text-ink-soft">{n.bodyMd}</pre>
        ) : (
          <p className="px-5 pb-4 text-small text-muted">{nt.emptyBody}</p>
        )}
      </Card>
    </article>
  );
}
