-- Meeting notes written on the board: decisions, action-item due dates and order. updated_at is the
-- optimistic-concurrency timestamp of the whole note (attendees, decisions and action items included).

alter table meeting_notes
  add column decisions text[] not null default '{}';

alter table meeting_action_items
  add column due_date date,
  add column position int not null default 0;

create index meeting_action_items_note on meeting_action_items (tenant_id, note_id, position);
create index meeting_notes_held_at on meeting_notes (tenant_id, held_at desc);
