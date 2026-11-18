-- Issue lists look up, per issue, the carry-over that brought it into its current sprint (BoardIssue.carriedFrom).

create index sprint_carry_over_issues_issue on sprint_carry_over_issues (tenant_id, issue_id);
create index sprint_carry_overs_to on sprint_carry_overs (tenant_id, to_sprint_id, created_at desc);
