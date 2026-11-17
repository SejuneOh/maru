-- Set when GitHub reports the person left the connected organization; cleared when they are added back.
alter table members add column removed_at timestamptz;
