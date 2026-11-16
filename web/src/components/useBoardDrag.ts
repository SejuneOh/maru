"use client";

import { useEffect, useState, useSyncExternalStore } from "react";
import { editIssue, isEditing, knownIssue } from "@/lib/boardEdits";
import type { Status } from "@/lib/types";

/** Phones get no drag: the columns are stacked there and a long press scrolls. */
const DRAG_QUERY = "(min-width: 640px)";

export function useCanDrag(): boolean {
  const [can, setCan] = useState(false);
  useEffect(() => {
    const mq = window.matchMedia(DRAG_QUERY);
    const update = () => setCan(mq.matches);
    update();
    mq.addEventListener("change", update);
    return () => mq.removeEventListener("change", update);
  }, []);
  return can;
}

/** `scope` keeps a card inside its own board: on the people board, inside its own person. */
type Dragging = { id: string; scope: string; from: Status };

let dragging: Dragging | null = null;
const listeners = new Set<() => void>();
const setDragging = (d: Dragging | null) => {
  dragging = d;
  listeners.forEach((l) => l());
};
const subscribe = (l: () => void) => {
  listeners.add(l);
  return () => {
    listeners.delete(l);
  };
};
const get = () => dragging;
const getServer = () => null;

export function useDragging(): Dragging | null {
  return useSyncExternalStore(subscribe, get, getServer);
}

const MIME = "application/x-workboard-issue";

export function cardDragProps(id: string, scope: string, from: Status, enabled: boolean) {
  if (!enabled || isEditing(id)) return { draggable: false };
  return {
    draggable: true,
    onDragStart: (e: React.DragEvent) => {
      e.dataTransfer.effectAllowed = "move";
      e.dataTransfer.setData(MIME, id);
      e.dataTransfer.setData("text/plain", id);
      setDragging({ id, scope, from });
    },
    onDragEnd: () => setDragging(null),
  };
}

/** A column accepts cards from its own scope that are not in it already. Cancelled cards land in done as done. */
export function useDropTarget(scope: string, status: Status) {
  const d = useDragging();
  const [over, setOver] = useState(false);
  const accepts = d !== null && d.scope === scope && d.from !== status;
  useEffect(() => {
    if (!d) setOver(false);
  }, [d]);
  if (!accepts) return { state: d ? ("off" as const) : null, props: {} };
  return {
    state: over ? ("over" as const) : ("can" as const),
    props: {
      onDragEnter: (e: React.DragEvent) => {
        e.preventDefault();
        setOver(true);
      },
      onDragOver: (e: React.DragEvent) => {
        e.preventDefault();
        e.dataTransfer.dropEffect = "move";
      },
      onDragLeave: (e: React.DragEvent) => {
        if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setOver(false);
      },
      onDrop: (e: React.DragEvent) => {
        e.preventDefault();
        setOver(false);
        const issue = knownIssue(d.id);
        setDragging(null);
        if (issue) editIssue(issue, { status });
      },
    },
  };
}
