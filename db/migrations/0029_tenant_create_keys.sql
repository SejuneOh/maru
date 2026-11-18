-- Idempotency-Key of POST /api/tenants. A caller creating a company has no tenant yet, so the key is scoped
-- to the GitHub user instead. Global, not tenant data: no RLS, written by workboard_sync alone.
-- Only a completed (201) response is stored; rows older than 24 hours are treated as absent.
create table tenant_create_keys (
  github_user_id bigint not null,
  key            text not null,
  request_hash   text not null,
  status_code    int not null,
  response       text not null,
  created_at     timestamptz not null default now(),
  primary key (github_user_id, key)
);
create index tenant_create_keys_created on tenant_create_keys (created_at);

grant select, insert, update, delete on tenant_create_keys to workboard_sync;
