-- An admin asked an organization owner to install the App (GitHub returned setup_action=request).
-- One open request per company. "다시 확인" links an organization installation created after requested_at
-- whose organization the checking admin is an active member of.
create table github_connection_requests (
  id                       uuid primary key default gen_random_uuid(),
  tenant_id                uuid not null references tenants(id) on delete cascade,
  requested_by             uuid,
  requester_github_user_id bigint not null,
  -- Organization login when known; GitHub's request redirect does not name it.
  account_login            text,
  requested_at             timestamptz not null default now(),
  resolved_at              timestamptz,
  connection_id            uuid,
  unique (tenant_id, id),
  foreign key (tenant_id, requested_by) references members (tenant_id, id) on delete set null (requested_by),
  foreign key (tenant_id, connection_id) references github_connections (tenant_id, id) on delete set null (connection_id)
);

create unique index github_connection_requests_open on github_connection_requests (tenant_id) where resolved_at is null;

-- Members of a connected GitHub organization, per connection. For display and assignee suggestions only:
-- a row never grants access to the company. removed_at is set when GitHub reports the person left that
-- organization; members.removed_at is derived from all of the company's active organization connections.
create table github_org_members (
  tenant_id      uuid   not null references tenants(id) on delete cascade,
  connection_id  uuid   not null,
  github_user_id bigint not null,
  -- Null when only a webhook has named the person (webhooks carry the id alone).
  github_login   text,
  removed_at     timestamptz,
  updated_at     timestamptz not null default now(),
  primary key (connection_id, github_user_id),
  foreign key (tenant_id, connection_id) references github_connections (tenant_id, id) on delete cascade
);

create index github_org_members_user on github_org_members (tenant_id, github_user_id);

alter table github_connection_requests enable row level security;
alter table github_connection_requests force row level security;
create policy tenant_isolation on github_connection_requests
  using (tenant_id = app_tenant_id()) with check (tenant_id = app_tenant_id());

alter table github_org_members enable row level security;
alter table github_org_members force row level security;
create policy tenant_isolation on github_org_members
  using (tenant_id = app_tenant_id()) with check (tenant_id = app_tenant_id());

grant select, insert, update, delete on github_connection_requests, github_org_members to workboard_sync;
grant select on github_connection_requests, github_org_members to workboard_app;
