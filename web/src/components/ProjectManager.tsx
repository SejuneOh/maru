"use client";

import { useState } from "react";
import { ApiError, createProject, deleteProject, useApi } from "@/lib/api";
import { useT } from "@/lib/i18n";
import { useMe } from "@/lib/me";
import type { Project } from "@/lib/types";

type Repo = { id: string; name: string };

/** Admin-only: create a project by naming it and picking repositories, and delete projects. */
export function ProjectManager({ projects, onChanged }: { projects: Project[]; onChanged: () => void }) {
  const t = useT();
  const pm = t.projectManager;
  const isAdmin = useMe().data?.member.role === "admin";
  const repos = useApi<Repo[]>(isAdmin ? "/api/repositories" : null);
  const [open, setOpen] = useState(false);
  const [name, setName] = useState("");
  const [picked, setPicked] = useState<Set<string>>(new Set());
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  if (!isAdmin) return null;

  const toggle = (id: string) =>
    setPicked((s) => {
      const next = new Set(s);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });

  const reset = () => {
    setOpen(false);
    setName("");
    setPicked(new Set());
    setError(null);
  };

  const submit = async () => {
    if (!name.trim() || busy) return;
    setBusy(true);
    setError(null);
    try {
      await createProject(name.trim(), [...picked]);
      reset();
      onChanged();
    } catch (e) {
      setError(e instanceof ApiError && e.code === "name_taken" ? pm.nameTaken : pm.createFailed);
    } finally {
      setBusy(false);
    }
  };

  const remove = async (p: Project) => {
    if (!confirm(pm.confirmDelete(p.name))) return;
    try {
      await deleteProject(p.id);
      onChanged();
    } catch {
      setError(pm.deleteFailed);
    }
  };

  return (
    <section className="rounded-item bg-surface p-4 shadow-elev-1" aria-label={pm.title}>
      <div className="flex items-center justify-between gap-3">
        <h3 className="text-ui font-semibold text-ink">{pm.title}</h3>
        {!open && (
          <button type="button" onClick={() => setOpen(true)} className="btn h-8 px-3 text-small">
            {pm.new}
          </button>
        )}
      </div>

      {error && !open && <p role="alert" className="mt-2 text-small text-ink">{error}</p>}

      {open && (
        <div className="mt-3 flex flex-col gap-3">
          <input
            autoFocus
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder={pm.namePlaceholder}
            maxLength={80}
            className="h-9 rounded-control bg-sunken px-3 text-small text-ink placeholder:text-muted"
          />
          <div className="max-h-48 overflow-auto rounded-control bg-sunken p-1">
            {repos.data && repos.data.length === 0 && <p className="px-2 py-1.5 text-small text-muted">{pm.noRepos}</p>}
            {repos.data?.map((r) => (
              <label key={r.id} className="flex cursor-pointer items-center gap-2.5 rounded-control px-2 py-1.5 text-small text-ink hover:bg-hover">
                <input type="checkbox" checked={picked.has(r.id)} onChange={() => toggle(r.id)} />
                <span className="min-w-0 truncate">{r.name}</span>
              </label>
            ))}
          </div>
          {error && <p role="alert" className="text-small text-ink">{error}</p>}
          <div className="flex items-center gap-2">
            <button type="button" onClick={submit} disabled={!name.trim() || busy} className="btn btn-primary h-8 px-3 text-small disabled:opacity-50">
              {pm.create}
            </button>
            <button type="button" onClick={reset} className="link text-small text-ink-soft">
              {pm.cancel}
            </button>
          </div>
        </div>
      )}

      {projects.length > 0 && (
        <ul className="mt-3 flex flex-col gap-1">
          {projects.map((p) => (
            <li key={p.id} className="flex items-center justify-between gap-3 rounded-control px-2 py-1.5 text-small">
              <span className="min-w-0 truncate text-ink">
                {p.name} <span className="text-muted">· {p.repositories.length}</span>
              </span>
              <button type="button" onClick={() => remove(p)} className="shrink-0 text-muted hover:text-ink">
                {pm.delete}
              </button>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
