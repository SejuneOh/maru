-- webhook_deliveries doubles as the dedup record (primary key = X-GitHub-Delivery) and the queue the worker drains.
-- Only routing fields are kept; the payload is never stored. result holds why a delivery changed nothing.
alter table webhook_deliveries
  add column action               text,
  add column installation_id      bigint,
  add column repository_id        bigint,
  add column subject_user_id      bigint,
  add column account_id           bigint,
  add column attempts             int not null default 0,
  add column available_at         timestamptz not null default now(),
  add column result               text;

create index webhook_deliveries_pending on webhook_deliveries (available_at) where processed_at is null;
create index webhook_deliveries_received on webhook_deliveries (received_at);
