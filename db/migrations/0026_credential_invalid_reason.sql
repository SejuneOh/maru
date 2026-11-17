-- Why a credential is invalid, so the owner check can tell a first "not a member" answer (token kept) from a repeated
-- one (token deleted). Cleared whenever the credential becomes active again or is replaced.
-- Depends on 0009, so it applies on a fresh database and on one at 0025.
alter table github_credentials add column invalid_reason text;
