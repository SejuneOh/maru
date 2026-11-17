namespace WorkBoard.Api.Features.MeetingNotes;

// Finishing the link of items whose issue GitHub created when the mirror could not store it (issue_claim_number set).
public static class PendingLink
{
    public const string ClearClaim = """
        issue_claim_key = null, issue_claim_at = null, issue_claim_first_at = null,
        issue_claim_repository_id = null, issue_claim_repository_ids = null, issue_claim_number = null
        """;

    // Run by the mirror in the transaction that stored issues of @repoId for tenant @tenantId.
    public const string LinkMirroredSql = $"""
        update meeting_action_items a
           set issue_id = i.id, {ClearClaim}
          from issues i
         where a.tenant_id = @tenantId and a.issue_id is null and a.issue_claim_number is not null
           and a.issue_claim_repository_id = @repoId
           and i.tenant_id = @tenantId and i.repository_id = a.issue_claim_repository_id and i.number = a.issue_claim_number
        """;
}
