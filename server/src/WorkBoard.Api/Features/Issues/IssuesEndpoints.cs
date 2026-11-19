using System.Text.Json;
using Microsoft.Extensions.Options;
using WorkBoard.Api.Infrastructure.GitHub;
using WorkBoard.Api.Features.Projects;
using WorkBoard.Api.Features.Sprints;
using WorkBoard.Api.Infrastructure.Data;
using WorkBoard.Api.Infrastructure.Identity;
using WorkBoard.Api.Infrastructure.Writes;

namespace WorkBoard.Api.Features.Issues;

public static class IssuesEndpoints
{
    private const int DefaultDoneWithinDays = 14;
    private const int MaxDoneWithinDays = 365;
    private static readonly HashSet<string> ValidStatuses = ["todo", "in_progress", "in_review", "done", "cancelled"];

    public static void MapIssues(this RouteGroupBuilder api)
    {
        api.MapGet("/issues", async (
            Guid? projectId, string? assignee, string? person, int? doneWithinDays, string? status, bool? stale,
            Guid? sprint, string? scope, string? doneSince, string? label, Guid? repository, TenantDb db, CancellationToken ct) =>
        {
            if (doneWithinDays is < 0 or > MaxDoneWithinDays)
                return Results.Json(new ErrorBody("invalid_doneWithinDays"), statusCode: StatusCodes.Status400BadRequest);

            string[]? statuses = null;
            if (!string.IsNullOrWhiteSpace(status))
            {
                statuses = status.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Distinct().ToArray();
                if (statuses.Length == 0 || statuses.Any(x => !ValidStatuses.Contains(x)))
                    return Results.Json(new ErrorBody("invalid_status"), statusCode: StatusCodes.Status400BadRequest);
            }

            if (!string.IsNullOrEmpty(scope) && scope != "all")
                return Results.Json(new ErrorBody("invalid_scope"), statusCode: StatusCodes.Status400BadRequest);

            if (!string.IsNullOrEmpty(doneSince) && doneSince != "week")
                return Results.Json(new ErrorBody("invalid_doneSince"), statusCode: StatusCodes.Status400BadRequest);

            var filter = new IssueFilter(
                projectId,
                CommaList(string.IsNullOrWhiteSpace(person) ? assignee : person),
                doneWithinDays ?? DefaultDoneWithinDays,
                statuses,
                stale ?? false,
                sprint,
                scope == "all",
                doneSince == "week",
                CommaList(label),
                repository);
            return Results.Ok(await db.ReadAsync(s => IssueQueries.ListAsync(s, filter), ct));
        });

        // Labels are stored per repository, so the same name can repeat; the board filters by name, so list
        // each distinct name once (with any one of its colors).
        api.MapGet("/labels", async (TenantDb db, CancellationToken ct) =>
            Results.Ok(await db.ReadAsync(s =>
                s.QueryAsync<LabelName>("select name, min(color) as color from labels group by name order by name"), ct)));

        api.MapGet("/repositories", async (TenantDb db, CancellationToken ct) =>
            Results.Ok(await db.ReadAsync(s =>
                s.QueryAsync<RepositoryRef>("select id, name from repositories where not archived order by name"), ct)));

        api.MapGet("/issues/{id:guid}", async (Guid id, TenantDb db, IOptions<GitHubAppOptions> app, CancellationToken ct) =>
        {
            var detail = await db.ReadAsync(s => IssueDetailQueries.GetAsync(s, id, app.Value), ct);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        api.MapPatch("/issues/{id:guid}/board", async (
            Guid id, HttpContext http, ICurrentUser user, BoardFieldWrites writes, CancellationToken ct) =>
        {
            var bytes = await RequestBodies.ReadAsync(http, SprintsEndpoints.MaxBodyBytes, ct);
            if (bytes is null || !http.Request.HasJsonContentType() || BoardPatch.Parse(bytes) is not { } patch)
                return ApiErrors.BadRequest();

            var key = http.Request.Headers[Idempotency.HeaderName].ToString();
            var hash = Idempotency.HashRequest(
                user.Identity.Member.Id, http.Request.Method, http.Request.Path + http.Request.QueryString, bytes);
            return await writes.ChangeAsync(id, patch, key, hash, ct);
        });

        api.MapPatch("/issues/{id:guid}", async (
            Guid id, HttpContext http, ICurrentUser user, AssigneeWrites writes, TenantDb db, CancellationToken ct) =>
        {
            var bytes = await RequestBodies.ReadAsync(http, SprintsEndpoints.MaxBodyBytes, ct);
            if (bytes is null || !http.Request.HasJsonContentType()
                || TenantAssignees.ParseIds(bytes, NewIssueEndpoints.MaxAssignees) is not { } ids)
                return ApiErrors.BadRequest();
            if (await TenantAssignees.ResolveForIssueAsync(db, id, ids, ct) is not { } assignees)
                return ApiErrors.BadRequest();

            var key = http.Request.Headers[Idempotency.HeaderName].ToString();
            var hash = Idempotency.HashRequest(
                user.Identity.Member.Id, http.Request.Method, http.Request.Path + http.Request.QueryString, bytes);
            return await writes.SetAsync(id, assignees, key, hash, ct);
        });

        api.MapPost("/issues/{id:guid}/comments", async (
            Guid id, HttpContext http, ICurrentUser user, CommentWrites writes, CancellationToken ct) =>
        {
            var key = http.Request.Headers[Idempotency.HeaderName].ToString();
            if (string.IsNullOrWhiteSpace(key))
                return ApiErrors.BadRequest();
            var bytes = await RequestBodies.ReadAsync(http, CommentMaxBodyBytes, ct);
            if (bytes is null || !http.Request.HasJsonContentType() || CommentWrites.Parse(bytes) is not { } body)
                return ApiErrors.BadRequest();

            var hash = Idempotency.HashRequest(
                user.Identity.Member.Id, http.Request.Method, http.Request.Path + http.Request.QueryString, bytes);
            return await writes.AddAsync(id, body, key, hash, ct);
        });
    }

    private const int CommentMaxBodyBytes = 256 * 1024;

    // A comma list for a multi-value filter (assignees, labels): null when empty, so the query skips the clause.
    private static IReadOnlyList<string>? CommaList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var items = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
        return items.Length == 0 ? null : items;
    }
}
