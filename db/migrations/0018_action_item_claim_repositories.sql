-- Every repository a create for the item was sent to since the first claim. A takeover looks in all of them, so an
-- issue an earlier attempt may have created is never missed because the person picked another repository since.
alter table meeting_action_items add column issue_claim_repository_ids uuid[];

update meeting_action_items
   set issue_claim_repository_ids = array[issue_claim_repository_id]
 where issue_claim_key is not null;

alter table meeting_action_items
  add constraint meeting_action_items_claim_repositories check (
    (issue_claim_key is null) = (issue_claim_repository_ids is null));
