-- A GitHub login is unique among current members only. A person who left keeps their old login on the row (history
-- shows the name), and a new person who now owns that login can join. Depends on 0001 and 0021 (left_at).
alter table members drop constraint members_tenant_id_github_login_key;
create unique index members_tenant_login_active on members (tenant_id, github_login) where left_at is null;
