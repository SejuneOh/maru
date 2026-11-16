"use client";

import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { useCallback } from "react";
import type { NewIssueStatus } from "@/lib/types";

/** `?issue=new` opens the panel as the new-issue form. */
export const NEW_ISSUE = "new";
const NEW_PARAMS = ["newStatus", "newAssignee"] as const;

export type NewIssuePrefill = { status?: NewIssueStatus; assignee?: number };

/** The issue detail panel is driven by `?issue=<id>` so any screen can open it and links can be shared. */
export function useIssuePanel() {
  const router = useRouter();
  const pathname = usePathname();
  const params = useSearchParams();

  const go = useCallback(
    (id: string | null, prefill: NewIssuePrefill = {}) => {
      const next = new URLSearchParams(params.toString());
      for (const k of NEW_PARAMS) next.delete(k);
      if (id) next.set("issue", id);
      else next.delete("issue");
      if (id === NEW_ISSUE) {
        if (prefill.status && prefill.status !== "todo") next.set("newStatus", prefill.status);
        if (prefill.assignee) next.set("newAssignee", String(prefill.assignee));
      }
      const qs = next.toString();
      router.push(qs ? `${pathname}?${qs}` : pathname, { scroll: false });
    },
    [params, pathname, router],
  );
  const setIssue = useCallback((id: string | null) => go(id), [go]);
  const close = useCallback(() => go(null), [go]);
  const startNew = useCallback((prefill?: NewIssuePrefill) => go(NEW_ISSUE, prefill), [go]);

  const status = params.get("newStatus");
  const assignee = Number(params.get("newAssignee"));
  const prefill: NewIssuePrefill = {
    status: status === "in_progress" || status === "in_review" ? status : "todo",
    assignee: Number.isSafeInteger(assignee) && assignee > 0 ? assignee : undefined,
  };

  return { issueId: params.get("issue"), open: setIssue, close, startNew, prefill };
}
