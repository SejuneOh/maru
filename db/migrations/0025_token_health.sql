-- Token connections: the earliest time the periodic sync may read the connection again. Set when GitHub's per-user
-- rate limit is nearly used up; a null or past value means the connection is not paused.
-- Depends only on 0001 (github_connections), so it applies on a fresh database and on one at 0024.
alter table github_connections add column sync_not_before timestamptz;
