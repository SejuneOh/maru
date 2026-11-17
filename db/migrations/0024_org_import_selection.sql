-- Repository selection for token connections. repositories.sync_enabled is the selection: only enabled
-- repositories are synced, and the mirrored issues of a disabled repository are kept but drop out of issue_status,
-- so every board list and count that joins it stops showing them. Enabling the repository again shows them again.
-- issue_status_all is the same status without the visibility rule, for sprint bookkeeping that must not depend on it.

-- Until an admin imports an organization, none of its repositories is selected. Token connections synced every
-- accessible repository before this migration.
update repositories r
   set sync_enabled = false
  from github_connections c
 where c.tenant_id = r.tenant_id and c.id = r.connection_id and c.auth_kind = 'token';

create view issue_status_all with (security_invoker = true) as
select s.issue_id,
       s.tenant_id,
       s.status,
       case s.status when 'todo' then 'todo' when 'done' then 'done' when 'cancelled' then 'done' else 'in_progress' end as category
from (
  select i.id as issue_id,
         i.tenant_id,
         case
           when i.state = 'closed' and i.state_reason = 'not_planned' then 'cancelled'
           when i.state = 'closed' then 'done'
           when bf.status_override is not null then bf.status_override
           when exists (select 1 from issue_pull_requests ipr
                          join pull_requests p on p.id = ipr.pull_request_id
                         where ipr.issue_id = i.id and p.state = 'merged') then 'in_review'
           when exists (select 1 from issue_pull_requests ipr
                          join pull_requests p on p.id = ipr.pull_request_id
                         where ipr.issue_id = i.id and p.state = 'open' and not p.draft) then 'in_review'
           when exists (select 1 from issue_pull_requests ipr
                          join pull_requests p on p.id = ipr.pull_request_id
                         where ipr.issue_id = i.id and p.state = 'open' and p.draft) then 'in_progress'
           else 'todo'
         end as status
    from issues i
    left join board_fields bf on bf.issue_id = i.id
) s;

grant select on issue_status_all to workboard_app, workboard_sync;

create or replace view issue_status with (security_invoker = true) as
select a.issue_id, a.tenant_id, a.status, a.category
  from issue_status_all a
  join issues i on i.tenant_id = a.tenant_id and i.id = a.issue_id
  join repositories r on r.id = i.repository_id and r.sync_enabled;
