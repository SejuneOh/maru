-- Turning an action item into a GitHub issue. While a create runs, or when its outcome is unknown, the item holds a
-- claim: the Idempotency-Key of that request, when it was taken, when the first claim was taken (kept across
-- takeovers of a stale claim, so the marker lookup reaches back far enough) and the repository it was sent to.
-- issue_claim_number is set when GitHub created the issue but it is not in the mirror yet; the link is made once it is.
alter table meeting_action_items
  add column issue_claim_key           text,
  add column issue_claim_at            timestamptz,
  add column issue_claim_first_at      timestamptz,
  add column issue_claim_repository_id uuid,
  add column issue_claim_number        int,
  add constraint meeting_action_items_claim_complete check (
    (issue_claim_key is null) = (issue_claim_at is null)
    and (issue_claim_key is null) = (issue_claim_first_at is null)
    and (issue_claim_key is null) = (issue_claim_repository_id is null)
    and (issue_claim_number is null or issue_claim_key is not null)),
  add constraint meeting_action_items_linked_or_claimed check (issue_id is null or issue_claim_key is null);
