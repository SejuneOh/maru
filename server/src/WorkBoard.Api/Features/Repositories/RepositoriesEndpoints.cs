using WorkBoard.Api.Features.Overview;
using WorkBoard.Api.Infrastructure.Data;
using WorkBoard.Api.Infrastructure.GitHub;
using WorkBoard.Api.Infrastructure.Identity;
using WorkBoard.Api.Infrastructure.Writes;

namespace WorkBoard.Api.Features.Repositories;

public static class RepositoriesEndpoints
{
    public static void MapRepositories(this RouteGroupBuilder api) =>
        api.MapGet("/repositories/{id:guid}/info", InfoAsync);

    private sealed record ActivityRow(int OpenIssues, int DoneThisWeek, int OpenPrs);

    private static async Task<IResult> InfoAsync(
        Guid id, ICurrentUser user, IGitHubWriteTargets targets, RepositoryCodeReader code, TenantDb db, CancellationToken ct)
    {
        var target = await targets.ResolveAsync(id, ct);
        if (target is null)
            return ApiErrors.NotFoundResult();

        RepositoryCodeInfo info;
        bool cached;
        try
        {
            (info, cached) = await code.GetAsync(user.Identity.TenantId, id, target.Repo, ct);
        }
        catch (GitHubSourceException e)
        {
            return GitHubFailure(e);
        }
        catch (HttpRequestException)
        {
            return ApiErrors.GithubUnavailableResult();
        }

        var activity = await db.ReadAsync(s => ActivityAsync(s, id), ct);
        return Results.Ok(new RepositoryInfo(
            $"{target.Repo.Owner}/{target.Repo.Name}", info.DefaultBranch, info.BaseCommit, info.Languages, info.Readme,
            info.Tree, info.Truncated, info.Dependencies, activity, info.GeneratedAt, cached));
    }

    private static IResult GitHubFailure(GitHubSourceException e) => e.Kind switch
    {
        GitHubSourceErrorKind.RateLimited => ApiErrors.Of(StatusCodes.Status429TooManyRequests, "github_rate_limited"),
        GitHubSourceErrorKind.Unauthorized => ApiErrors.Of(StatusCodes.Status409Conflict, ApiErrors.CredentialMissing),
        GitHubSourceErrorKind.CredentialRevoked => ApiErrors.Of(StatusCodes.Status409Conflict, ApiErrors.CredentialRevoked),
        GitHubSourceErrorKind.CredentialInvalid => ApiErrors.Of(StatusCodes.Status409Conflict, ApiErrors.CredentialInvalid),
        GitHubSourceErrorKind.CredentialExpired => ApiErrors.Of(StatusCodes.Status409Conflict, ApiErrors.CredentialExpired),
        GitHubSourceErrorKind.NotFound => ApiErrors.NotFoundResult(),
        _ => ApiErrors.GithubUnavailableResult(),
    };

    // Issue and pull request counts of one repository from the mirror; doneThisWeek counts from Monday 00:00 Asia/Seoul.
    private static async Task<RepositoryActivity> ActivityAsync(TenantSession s, Guid repositoryId)
    {
        var row = (await s.QuerySingleOrDefaultAsync<ActivityRow>($"""
            select (select count(*) filter (where i.state = 'open')::int
                      from issues i where i.repository_id = @repositoryId) as open_issues,
                   (select count(*) filter (where st.status = 'done' and i.closed_at >= {OverviewQueries.WeekStartSql})::int
                      from issues i join issue_status st on st.issue_id = i.id
                     where i.repository_id = @repositoryId) as done_this_week,
                   (select count(*)::int from pull_requests p
                     where p.repository_id = @repositoryId and p.state = 'open') as open_prs
            """, new { repositoryId }))!;
        return new RepositoryActivity(row.OpenIssues, row.OpenPrs, row.DoneThisWeek);
    }
}
