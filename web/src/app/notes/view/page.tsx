"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { useState } from "react";
import { ErrorBox, Loading } from "@/components/Feedback";
import { ActionItemList } from "@/components/ActionItemList";
import { NoteEditor, canDelete, confirmAndDelete } from "@/components/NoteEditor";
import { Card, PageHeader } from "@/components/ui";
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
  const me = useMe();
  const [adopted, setAdopted] = useState<string | null>(null);
  const skip = id === null || id === adopted;
  const note = useApi<MeetingNote>(skip ? null : `/api/meeting-notes/${id}`);

  if (me.error) return <ErrorBox error={me.error} />;
  if (!skip && note.error) return <ErrorBox error={note.error} />;
  if (!me.data || (!skip && !note.data)) return <Loading />;
  return (
    <NoteEditor
      initial={skip ? null : note.data}
      owner={{ tenantId: me.data.tenant.id, memberId: me.data.member.id }}
      onCreated={(n) => {
        setAdopted(n.id);
        router.replace(`/notes/view?id=${n.id}&edit=1`);
      }}
    />
  );
}

function ReadScreen({ id }: { id: string | null }) {
  const router = useRouter();
  const t = useT();
  const nt = t.notes;
  const fmt = useFormat();
  const meData = useMe().data;
  const me = meData?.member;
  const note = useApi<MeetingNote>(id ? `/api/meeting-notes/${id}` : null);
  const [deleting, setDeleting] = useState(false);

  if (!id) return <p className="text-ui text-muted">{nt.pick}</p>;
  if (note.error) return <ErrorBox error={note.error} />;
  if (!note.data) return <Loading />;
  const n = note.data;

  const remove = async () => {
    setDeleting(true);
    const gone = await confirmAndDelete(n, nt, meData ? { tenantId: meData.tenant.id, memberId: meData.member.id } : null);
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

      <ActionItemList note={n} className="reveal [animation-delay:60ms]" />

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
