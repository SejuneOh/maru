-- Set when an admin removes the person from the company. The row stays so authored notes, audit rows and
-- assignments keep their name; membership lookups skip it. Unrelated to removed_at (GitHub organization membership).
alter table members add column left_at timestamptz;

-- An admin's invitation for one GitHub user id. The link carries a random token; only its SHA-256 is stored.
-- Joining needs the token and a sign-in whose GitHub id equals github_user_id.
create table invitations (
  id             uuid primary key default gen_random_uuid(),
  tenant_id      uuid not null references tenants(id) on delete cascade,
  github_user_id bigint not null,
  -- Login at invite time, for display only.
  github_login   text   not null,
  role           text   not null default 'member' check (role = 'member'),
  token_hash     bytea  not null unique,
  invited_by     uuid,
  created_at     timestamptz not null default now(),
  expires_at     timestamptz not null,
  accepted_at    timestamptz,
  revoked_at     timestamptz,
  unique (tenant_id, id),
  foreign key (tenant_id, invited_by) references members (tenant_id, id) on delete set null (invited_by),
  check (accepted_at is null or revoked_at is null)
);

-- One open invitation per person per company. Expired ones are revoked before a new one is made.
create unique index invitations_open on invitations (tenant_id, github_user_id)
  where accepted_at is null and revoked_at is null;

alter table invitations enable row level security;
alter table invitations force row level security;
create policy tenant_isolation on invitations
  using (tenant_id = app_tenant_id()) with check (tenant_id = app_tenant_id());

grant select, insert, update, delete on invitations to workboard_sync;
grant select, insert on invitations to workboard_app;
grant update (revoked_at) on invitations to workboard_app;
grant update (left_at) on members to workboard_app;
