-- How a connection reaches GitHub, and the stored token of token connections.
-- Depends only on 0001 (tenants, members, github_connections, app_tenant_id), so it applies cleanly whether it
-- runs before 0012 on a fresh database or after 0023 on one that already has them.
alter table github_connections
  add column auth_kind    text    not null default 'app' check (auth_kind in ('app', 'token')),
  -- Whether issue and comment bodies are written to the mirror. The default suits App connections;
  -- the code that creates a token connection sets it to false.
  add column store_bodies boolean not null default true;

-- At most one credential per connection. ciphertext holds the AES-GCM ciphertext followed by its 16-byte tag.
-- The key lives outside the database; key_version names the key that sealed the row.
create table github_credentials (
  id               uuid primary key default gen_random_uuid(),
  tenant_id        uuid   not null references tenants(id) on delete cascade,
  connection_id    uuid   not null,
  owner_member_id  uuid   not null,
  ciphertext       bytea  not null,
  nonce            bytea  not null check (octet_length(nonce) = 12),
  key_version      int    not null check (key_version > 0),
  scopes           text[] not null default '{}',
  expires_at       timestamptz,
  status           text   not null default 'active' check (status in ('active', 'invalid', 'expired', 'revoked')),
  last_checked_at  timestamptz,
  last_used_at     timestamptz,
  created_at       timestamptz not null default now(),
  updated_at       timestamptz not null default now(),
  unique (connection_id),
  foreign key (tenant_id, connection_id) references github_connections (tenant_id, id) on delete cascade,
  foreign key (tenant_id, owner_member_id) references members (tenant_id, id) on delete cascade
);

create index github_credentials_tenant on github_credentials (tenant_id);

alter table github_credentials enable row level security;
alter table github_credentials force row level security;
create policy tenant_isolation on github_credentials
  using (tenant_id = app_tenant_id()) with check (tenant_id = app_tenant_id());

-- Only sync reads and writes the secret columns. The API role may read the metadata columns, not ciphertext or nonce.
grant select, insert, update, delete on github_credentials to workboard_sync;
grant select (id, tenant_id, connection_id, owner_member_id, key_version, scopes, expires_at, status,
              last_checked_at, last_used_at, created_at, updated_at)
  on github_credentials to workboard_app;
