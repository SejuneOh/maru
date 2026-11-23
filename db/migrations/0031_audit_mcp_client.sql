-- When a write comes through the MCP endpoint, member_id stays the token owner (the human the tool acts for) and
-- this column records which AI tool token did it. Null for ordinary writes.
alter table audit_log add column mcp_client_id uuid;
