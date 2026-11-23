-- Access tokens for AI tools (MCP clients). The server mints the token (prefix wbm_ + 256-bit random) and shows
-- it once; only its SHA-256 is stored, like invitations. A read token is the default; write scope is opt-in.
-- token_prefix keeps the first few visible characters so the owner can tell the tokens apart in the list.
create table mcp_clients (
  id              uuid primary key default gen_random_uuid(),
  tenant_id       uuid not null,
  owner_member_id uuid not null,
  name            text not null,
  token_hash      bytea not null unique,
  token_prefix    text not null,
  scope           text not null default 'read' check (scope in ('read', 'write')),
  created_at      timestamptz not null default now(),
  expires_at      timestamptz not null,
  last_used_at    timestamptz,
  revoked_at      timestamptz,
  foreign key (tenant_id, owner_member_id) references members (tenant_id, id) on delete cascade
);

create index mcp_clients_owner on mcp_clients (tenant_id, owner_member_id);

alter table mcp_clients enable row level security;
alter table mcp_clients force row level security;
create policy tenant_isolation on mcp_clients
  using (tenant_id = app_tenant_id()) with check (tenant_id = app_tenant_id());

grant select, insert, update, delete on mcp_clients to workboard_sync;
grant select, insert on mcp_clients to workboard_app;
-- The API never deletes a token row; it revokes (revoked_at) and the /mcp endpoint stamps last_used_at.
grant update (last_used_at, revoked_at) on mcp_clients to workboard_app;
