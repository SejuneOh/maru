-- One row per sprint per day (Asia/Seoul): the scope and the finished part of the sprint, for the remaining-work trend.
-- cancelled issues are in neither number. Weight is size S=1, M=2, L=3; an issue without a size weighs 1.
-- Only the sync job writes; the row of the current day is overwritten by later runs of that day.
create table sprint_daily (
  tenant_id    uuid not null references tenants(id) on delete cascade,
  sprint_id    uuid not null,
  day          date not null,
  scope_count  int not null check (scope_count >= 0),
  scope_weight int not null check (scope_weight >= 0),
  done_count   int not null check (done_count >= 0),
  done_weight  int not null check (done_weight >= 0),
  recorded_at  timestamptz not null default now(),
  primary key (tenant_id, sprint_id, day),
  check (done_count <= scope_count and done_weight <= scope_weight),
  foreign key (tenant_id, sprint_id) references sprints (tenant_id, id) on delete cascade
);

alter table sprint_daily enable row level security;
alter table sprint_daily force row level security;
create policy tenant_isolation on sprint_daily
  using (tenant_id = app_tenant_id()) with check (tenant_id = app_tenant_id());

grant select, insert, update, delete on sprint_daily to workboard_sync;
grant select on sprint_daily to workboard_app;
grant select on issue_status to workboard_sync;
