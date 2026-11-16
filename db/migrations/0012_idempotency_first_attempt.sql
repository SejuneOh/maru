-- Time of the first attempt with a key, kept across takeovers of a stale pending reservation, so a retry can
-- look for the result of any earlier attempt. Null on completed rows and on rows reserved before this column.
alter table idempotency_keys add column first_attempt_at timestamptz;
