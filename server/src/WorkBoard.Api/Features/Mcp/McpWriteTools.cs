using System.Text;
using System.Text.Json;
using WorkBoard.Api.Features.Issues;
using WorkBoard.Api.Infrastructure.Identity;
using WorkBoard.Api.Infrastructure.Writes;

namespace WorkBoard.Api.Features.Mcp;

// MCP write tools, visible and runnable only with a write-scope token. They reuse the same write services as the REST
// endpoints (so GitHub, the mirror, idempotency and the audit row all work the same), acting as the token's owner.
// Reversible-but-not-easily actions only: change a card's status/priority/size, add a comment. No delete or close.
internal static class McpWriteTools
{
    public static readonly HashSet<string> Names = ["change_card", "add_comment"];

    public static readonly object[] List =
    [
        new
        {
            name = "change_card",
            description = "Change one issue card's board fields: status (todo, in_progress, in_review, done, cancelled), priority (urgent, high, normal, low) and size (S, M, L). Moving to done or cancelled closes the issue on GitHub. Give the issue UUID and at least one field.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    id = new { type = "string", description = "The issue UUID." },
                    status = new { type = "string", description = "todo, in_progress, in_review, done or cancelled." },
                    priority = new { type = "string", description = "urgent, high, normal or low." },
                    size = new { type = "string", description = "S, M or L." },
                },
                required = new[] { "id" },
            },
        },
        new
        {
            name = "add_comment",
            description = "Add a comment to an issue. It is posted to GitHub under the app with the token owner's name.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    issueId = new { type = "string", description = "The issue UUID." },
                    body = new { type = "string", description = "The comment text." },
                },
                required = new[] { "issueId", "body" },
            },
        },
    ];

    public static async Task<IdempotentResponse> RunAsync(
        string name, JsonElement args, IServiceProvider services, AuthedClient client, CancellationToken ct)
    {
        // Act as the token's owner for this request: the write services read identity from CurrentUser, and the audit
        // row records this MCP client id alongside the owner as member.
        var user = services.GetRequiredService<CurrentUser>();
        user.Set(new DevIdentity(client.Member, client.TenantId, client.TenantName), client.Id);

        return name switch
        {
            "change_card" => await ChangeCardAsync(args, services, client, ct),
            "add_comment" => await AddCommentAsync(args, services, client, ct),
            _ => throw new ToolException("unknown write tool"),
        };
    }

    private static async Task<IdempotentResponse> ChangeCardAsync(
        JsonElement args, IServiceProvider services, AuthedClient client, CancellationToken ct)
    {
        var issueId = Get.Guid(args, "id") ?? throw new ToolException("id (a UUID) is required");
        var status = Choice(args, "status", BoardPatch.Statuses);
        var priority = Choice(args, "priority", BoardPatch.Priorities);
        var size = Choice(args, "size", BoardPatch.Sizes);
        if (status is null && priority is null && size is null)
            throw new ToolException("give at least one of status, priority or size");

        var patch = new BoardPatch(status is not null, status, priority is not null, priority, size is not null, size, false, null);
        var (key, hash) = Idem(client, "change_card", args);
        return await services.GetRequiredService<BoardFieldWrites>().ChangeAsync(issueId, patch, key, hash, ct);
    }

    private static async Task<IdempotentResponse> AddCommentAsync(
        JsonElement args, IServiceProvider services, AuthedClient client, CancellationToken ct)
    {
        var issueId = Get.Guid(args, "issueId") ?? throw new ToolException("issueId (a UUID) is required");
        var body = Get.Str(args, "body")?.Trim();
        if (string.IsNullOrEmpty(body))
            throw new ToolException("body is required");
        var (key, hash) = Idem(client, "add_comment", args);
        return await services.GetRequiredService<CommentWrites>().AddAsync(issueId, body, key, hash, ct);
    }

    // A value from a fixed set, or a ToolException when it is not. Absent is null (the field is left unchanged).
    private static string? Choice(JsonElement args, string key, string[] allowed)
    {
        var value = Get.Str(args, key);
        if (value is null)
            return null;
        if (!allowed.Contains(value))
            throw new ToolException($"{key} must be one of: {string.Join(", ", allowed)}");
        return value;
    }

    // The write services dedupe on an Idempotency-Key. A tool may pass its own; otherwise each call is a new one.
    // The hash binds the key to this owner, tool and arguments, matching how the REST endpoints bind it.
    private static (string Key, string Hash) Idem(AuthedClient client, string tool, JsonElement args)
    {
        var key = Get.Str(args, "idempotencyKey") ?? Guid.NewGuid().ToString("N");
        var body = args.ValueKind == JsonValueKind.Object ? args.GetRawText() : "{}";
        var hash = Idempotency.HashRequest(client.OwnerMemberId, "MCP", "/mcp/" + tool, Encoding.UTF8.GetBytes(body));
        return (key, hash);
    }
}
