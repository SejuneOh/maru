using System.Security.Cryptography;
using System.Text.Json;

namespace WorkBoard.Api.Infrastructure.Webhooks;

public static class WebhookSignature
{
    public const string Prefix = "sha256=";

    // header is the X-Hub-Signature-256 value. The comparison runs in constant time over the raw body bytes.
    public static bool IsValid(string secret, ReadOnlySpan<byte> body, string? header)
    {
        if (string.IsNullOrEmpty(secret) || header is null || !header.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        byte[] given;
        try
        {
            given = Convert.FromHexString(header.AsSpan(Prefix.Length));
        }
        catch (FormatException)
        {
            return false;
        }
        var expected = HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret), body);
        return CryptographicOperations.FixedTimeEquals(given, expected);
    }
}

// The routing fields of one delivery. Nothing else of the payload is kept.
public sealed record WebhookRoute(
    string Event,
    string? Action,
    long? InstallationId,
    long? RepositoryId,
    long? SubjectUserId,
    long? AccountId)
{
    public const string Installation = "installation";
    public const string Organization = "organization";

    // Events that make the worker re-read the repository.
    public static readonly IReadOnlySet<string> RepositoryEvents = new HashSet<string>(StringComparer.Ordinal)
    {
        "issues", "issue_comment", "pull_request", "pull_request_review", "pull_request_review_comment",
        "milestone", "label", "sub_issues",
    };

    public static readonly IReadOnlySet<string> InstallationActions = new HashSet<string>(StringComparer.Ordinal)
    {
        "deleted", "suspend", "unsuspend",
    };

    public static readonly IReadOnlySet<string> OrganizationActions = new HashSet<string>(StringComparer.Ordinal)
    {
        "member_removed", "member_added",
    };

    // Null when work-board does not act on the event.
    public static WebhookRoute? Parse(string eventName, ReadOnlySpan<byte> body)
    {
        var supported = RepositoryEvents.Contains(eventName) || eventName is Installation or Organization;
        if (!supported)
            return null;

        using var doc = JsonDocument.Parse(body.ToArray());
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("not an object");

        var action = Str(root, "action");
        if (eventName == Installation && (action is null || !InstallationActions.Contains(action)))
            return null;
        if (eventName == Organization && (action is null || !OrganizationActions.Contains(action)))
            return null;

        var installation = Obj(root, "installation");
        var repository = Obj(root, "repository");
        var membership = Obj(Obj(root, "membership"), "user");
        var organization = Obj(root, "organization");
        return new WebhookRoute(
            eventName,
            action,
            Long(installation, "id"),
            Long(repository, "id"),
            Long(membership, "id"),
            Long(organization, "id"));
    }

    private static JsonElement? Obj(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string? Str(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static long? Long(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt64(out var n) ? n : null;
}
