-- Supabase Realtime: push a per-tenant "sync finished" event to the browser.
--
-- This is NOT a migration. It uses Supabase-only objects (the `realtime` schema, `auth.uid()`,
-- the `authenticated` role) that do not exist on the plain Postgres used by local dev and CI, so it
-- must never go into db/migrations. Run it on the Supabase project (SQL editor) as the `postgres`
-- role. It is idempotent; re-running it is safe.
--
-- BEFORE this does anything useful, turn OFF "Allow public access" in the Supabase dashboard
-- (Project Settings -> Realtime). While it is on, anyone can join a public `tenant:<id>` channel and
-- the RLS below is bypassed. With Realtime unused today, turning it off has no other effect.
--
-- How it works:
--   - A trigger on public.sync_runs broadcasts {runId, status} to topic `tenant:<id>` when a run ends.
--   - The payload carries no issue data; the client only uses it to refetch through the API, which
--     enforces tenant access itself. So a stale subscriber (just removed from a tenant, JWT not yet
--     expired) learns nothing it could not already fetch, and gets nothing after its token refreshes.
--   - RLS on realtime.messages lets a browser receive a tenant's broadcasts only while the logged-in
--     user is an active member of that tenant. There is no INSERT policy, so a browser cannot forge events.

-- 1. Membership check. SECURITY DEFINER so the `authenticated` role needs no grant on the tables.
--    It reads auth.uid() itself and only returns a boolean, so it cannot probe other people.
create schema if not exists private;

create or replace function private.is_tenant_topic_member(topic text)
returns boolean
language sql
stable
security definer
set search_path = ''
as $$
  select exists (
    select 1
      from public.auth_links al
      join public.members m on m.github_user_id = al.github_user_id
     where al.auth_user_id = (select auth.uid())
       and m.left_at is null
       and topic = 'tenant:' || m.tenant_id::text
  )
$$;

alter function private.is_tenant_topic_member(text) owner to postgres;
revoke all on function private.is_tenant_topic_member(text) from public, anon;
grant usage on schema private to authenticated;
grant execute on function private.is_tenant_topic_member(text) to authenticated;

-- 2. Speeds up the membership join (members is indexed on (tenant_id, github_user_id), which does not
--    help a lookup by github_user_id alone).
create index if not exists members_github_user_id on public.members (github_user_id);

-- 3. Receive policy on realtime.messages. RLS is already enabled on that table by Supabase; do not
--    ALTER TABLE ... ENABLE (it errors). SELECT only: no INSERT policy means browsers cannot broadcast.
drop policy if exists "tenant members receive sync events" on realtime.messages;
create policy "tenant members receive sync events"
  on realtime.messages
  for select
  to authenticated
  using (
    extension = 'broadcast'
    and private.is_tenant_topic_member((select realtime.topic()))
  );

-- 4. Broadcast on sync completion. SECURITY DEFINER + owner postgres so sync_runs writers need no
--    realtime grant. The send is wrapped so a Realtime hiccup never rolls back the sync_runs update.
create or replace function public.sync_runs_notify()
returns trigger
language plpgsql
security definer
set search_path = ''
as $$
begin
  if new.finished_at is not null and old.finished_at is null then
    begin
      perform realtime.send(
        jsonb_build_object('runId', new.id, 'status', new.status),
        'sync_finished',
        'tenant:' || new.tenant_id::text,
        true);
    exception when others then
      -- Never fail the sync write because a broadcast could not be queued.
      null;
    end;
  end if;
  return null;
end
$$;

alter function public.sync_runs_notify() owner to postgres;

drop trigger if exists sync_runs_notify on public.sync_runs;
create trigger sync_runs_notify
  after update on public.sync_runs
  for each row
  execute function public.sync_runs_notify();
