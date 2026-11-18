-- The app role may write audit rows only as the current member (app.member_id, set per request next to
-- app.tenant_id) or with no member for system actions. The sync role bypasses RLS and is unaffected.

create or replace function app_member_id() returns uuid
  language sql stable
as $$ select nullif(current_setting('app.member_id', true), '')::uuid $$;

drop policy tenant_isolation on audit_log;

create policy audit_tenant_read on audit_log for select
  using (tenant_id = app_tenant_id());

create policy audit_member_insert on audit_log for insert
  with check (tenant_id = app_tenant_id() and (member_id is null or member_id = app_member_id()));
