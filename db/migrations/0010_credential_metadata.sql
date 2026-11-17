-- Depends on 0009, so it applies after it on both fresh and existing databases.
-- What the board shows about a stored token without reading it, and whose items GitHub attributes to it.
alter table github_credentials
  add column last4       text check (last4 is null or char_length(last4) = 4),
  add column owner_login text;

grant select (last4, owner_login) on github_credentials to workboard_app;
