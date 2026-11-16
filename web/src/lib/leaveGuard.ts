// One screen at a time can hold unsaved work. Every in-app navigation that is not a link click (shortcuts,
// the search box) asks here first; link clicks and closing the tab are handled by the screen itself.

let guard: (() => string | null) | null = null;

/** `check` returns the question to ask, or null when there is nothing to lose. */
export function setLeaveGuard(check: (() => string | null) | null) {
  guard = check;
}

/** True when it is fine to navigate away. */
export function confirmLeave(): boolean {
  const question = guard?.();
  return !question || window.confirm(question);
}
