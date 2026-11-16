-- issue_events.seq orders events by insert time instead of transaction start time. Carry-over, undo and
-- carriedFrom compare "after the carry-over" with it; sprint_carry_overs.event_seq is the carry-over's own mark.

alter table issue_events add column seq bigint;

update issue_events e set seq = o.n
  from (select id, row_number() over (order by at, id) as n from issue_events) o
 where e.id = o.id;

alter table issue_events alter column seq set not null;
alter table issue_events alter column seq add generated always as identity;

do $$
declare next_seq bigint;
begin
  select coalesce(max(seq), 0) + 1 into next_seq from issue_events;
  execute format('alter table issue_events alter column seq restart with %s', next_seq);
end $$;

create index issue_events_issue_seq on issue_events (tenant_id, issue_id, seq);

-- Highest issue_events.seq this carry-over wrote. Null on carry-overs made before this column existed:
-- those keep comparing by time.
alter table sprint_carry_overs add column event_seq bigint;
