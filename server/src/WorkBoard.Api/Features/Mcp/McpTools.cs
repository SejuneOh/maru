using System.Text.Json;
using System.Text.Json.Serialization;
using WorkBoard.Api.Features.Issues;
using WorkBoard.Api.Features.Overview;
using WorkBoard.Api.Features.Search;
using WorkBoard.Api.Features.Sprints;
using WorkBoard.Api.Infrastructure.Data;
using WorkBoard.Api.Infrastructure.GitHub;

namespace WorkBoard.Api.Features.Mcp;

internal sealed record McpTool(string Name, string Description, object InputSchema)
{
    [JsonIgnore]
    public required Func<TenantSession, JsonElement, GitHubAppOptions, Task<object?>> RunAsync { get; init; }
}

// Read tools exposed over MCP. Descriptions are static strings and never carry board content, so external text
// cannot reach the model through a tool description.
internal static class McpTools
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    public static readonly McpTool[] All =
    [
        new("list_issues",
            "List board issues, newest activity first. Optional filters: status (comma list of todo,in_progress,in_review,done,cancelled), person (a GitHub login), label, stale (true for issues with no recent activity), doneWithinDays, limit.",
            Schema(new
            {
                status = Str("Comma list of statuses: todo, in_progress, in_review, done, cancelled."),
                person = Str("A GitHub login to filter assignees by."),
                label = Str("A label name to filter by."),
                stale = Bool("Only issues with no recent activity."),
                doneWithinDays = Int("How many days back a done issue still counts (default 14)."),
                limit = Int("Maximum issues to return (1-200, default 50)."),
            }))
        {
            RunAsync = (s, a, _) =>
            {
                var person = Get.Str(a, "person");
                var label = Get.Str(a, "label");
                var filter = new IssueFilter(
                    ProjectId: null,
                    People: person is null ? null : [person],
                    DoneWithinDays: Get.Int(a, "doneWithinDays") ?? 14,
                    Statuses: Split(Get.Str(a, "status")),
                    Stale: Get.Bool(a, "stale"),
                    Labels: label is null ? null : [label]);
                return Limited(IssueQueries.ListAsync(s, filter), Get.Int(a, "limit"));
            },
        },
        new("get_issue",
            "Full detail of one issue by its UUID: fields, assignees, linked PRs, sub-issues, comments and meetings.",
            Schema(new { id = Str("The issue UUID (from list_issues).") }))
        {
            RunAsync = async (s, a, gh) =>
            {
                var id = Get.Guid(a, "id") ?? throw new ToolException("id (a UUID) is required");
                return await IssueDetailQueries.GetAsync(s, id, gh) ?? throw new ToolException("issue not found");
            },
        },
        new("search",
            "Search issues, comments and meeting notes by text (minimum 2 characters).",
            Schema(new { query = Str("The text to search for (at least 2 characters).") }))
        {
            RunAsync = async (s, a, _) =>
            {
                var q = (Get.Str(a, "query") ?? "").Trim();
                if (q.Length < SearchEndpoints.MinSearchLength)
                    throw new ToolException("query must be at least 2 characters");
                return await SearchEndpoints.SearchAsync(s, q);
            },
        },
        new("get_overview",
            "The company's board overview: counts, what needs attention, people workload, projects and the current sprint.",
            Schema(new { }))
        {
            RunAsync = async (s, _, _) => await OverviewQueries.BuildAsync(s),
        },
        new("list_sprints",
            "Sprints grouped as active, planned and recently closed. Empty when sprints are off for the company.",
            Schema(new { }))
        {
            RunAsync = async (s, _, _) => await SprintQueries.ListAsync(s),
        },
        new("list_meeting_notes",
            "Meeting notes, newest first, with their project and how many action items each has. Optional projectId filter.",
            Schema(new { projectId = Str("Only notes of this project UUID.") }))
        {
            RunAsync = async (s, a, _) =>
            {
                var projectId = Get.Guid(a, "projectId");
                return await s.QueryAsync<NoteSummary>("""
                    select n.id, n.title, n.held_at as HeldAt, p.name as ProjectName,
                           (select count(*) from meeting_action_items ai where ai.note_id = n.id)::int as ActionItemCount
                      from meeting_notes n
                      left join projects p on p.id = n.project_id
                     where @projectId::uuid is null or n.project_id = @projectId::uuid
                     order by n.held_at desc, n.id
                     limit 100
                    """, new { projectId });
            },
        },
        new("list_repositories",
            "The company's synced GitHub repositories.",
            Schema(new { }))
        {
            RunAsync = async (s, _, _) =>
                await s.QueryAsync<RepoRow>("select id, name from repositories where sync_enabled order by name"),
        },
    ];

    public static readonly object[] List = All.Select(t => (object)new { name = t.Name, description = t.Description, inputSchema = t.InputSchema }).ToArray();

    public static McpTool? Find(string name) => Array.Find(All, t => t.Name == name);

    private static async Task<object?> Limited(Task<List<BoardIssue>> task, int? limit) =>
        (await task).Take(Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit)).ToList();

    private static IReadOnlyList<string>? Split(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? null
            : csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } parts
                ? parts
                : null;

    private static object Schema(object properties) => new { type = "object", properties, additionalProperties = false };
    private static object Str(string description) => new { type = "string", description };
    private static object Int(string description) => new { type = "integer", description };
    private static object Bool(string description) => new { type = "boolean", description };

    private sealed record NoteSummary(Guid Id, string Title, DateTime HeldAt, string? ProjectName, int ActionItemCount);
    private sealed record RepoRow(Guid Id, string Name);
}

internal static class Get
{
    public static string? Str(JsonElement a, string key) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static int? Int(JsonElement a, string key) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    public static bool Bool(JsonElement a, string key) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;

    public static Guid? Guid(JsonElement a, string key) =>
        Str(a, key) is { } s && System.Guid.TryParse(s, out var g) ? g : null;
}

internal static class JsonRpc
{
    public static IResult Ok(JsonElement? id, object result) =>
        Results.Json(new { jsonrpc = "2.0", id = Box(id), result }, JsonSerializerOptions.Web);

    public static IResult Error(JsonElement? id, int code, string message) =>
        Results.Json(new { jsonrpc = "2.0", id = Box(id), error = new { code, message } }, JsonSerializerOptions.Web);

    // A JsonElement serialises as its own value; a missing id becomes null.
    private static object? Box(JsonElement? id) => id;
}

internal static class ToolContent
{
    public static object Text(string text) => new { content = new[] { new { type = "text", text } }, isError = false };
    public static object Error(string message) => new { content = new[] { new { type = "text", text = message } }, isError = true };
}
