using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Npgsql;
using WorkBoard.Api.Infrastructure.GitHub;
using WorkBoard.Api.Infrastructure.Identity;
using WorkBoard.Api.Infrastructure.Sync;
using WorkBoard.Api.Infrastructure.Webhooks;
using WorkBoard.Api.Infrastructure.Writes;

namespace WorkBoard.Api.Features.Webhooks;

public sealed record WebhookAck(string Status);

public static class WebhookEndpoints
{
    public const int MaxBodyBytes = 2 * 1024 * 1024;
    private const int MaxDeliveryIdLength = 100;

    public static void MapWebhooks(this RouteGroupBuilder api) => api.MapPost("/github/webhook", ReceiveAsync);

    // 503 also while Sync:Enabled=false, because the worker that drains the queue is off then.
    // Anonymous: the HMAC signature is the only proof the request came from GitHub. Everything before the insert is
    // memory-only, so unsigned or oversized requests never reach the database. Payload contents are never logged.
    private static async Task<IResult> ReceiveAsync(
        HttpContext http, IOptions<GitHubAppOptions> app, IOptions<SyncOptions> syncOptions, WebhookWake wake,
        [FromKeyedServices(DataKeys.Sync)] NpgsqlDataSource sync, CancellationToken ct)
    {
        var secret = app.Value.WebhookSecret;
        if (string.IsNullOrWhiteSpace(secret) || !syncOptions.Value.Enabled)
            return ApiErrors.Of(StatusCodes.Status503ServiceUnavailable, "webhook_not_configured");

        var request = http.Request;
        var signature = request.Headers["X-Hub-Signature-256"].ToString();
        if (signature.Length == 0)
            return ApiErrors.Of(StatusCodes.Status401Unauthorized, "invalid_signature");
        if (request.ContentLength is > MaxBodyBytes)
            return ApiErrors.Of(StatusCodes.Status413PayloadTooLarge, "payload_too_large");
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = MaxBodyBytes;

        byte[]? body;
        try
        {
            body = await RequestBodies.ReadAsync(http, MaxBodyBytes, ct);
        }
        catch (BadHttpRequestException)
        {
            body = null;
        }
        if (body is null)
            return ApiErrors.Of(StatusCodes.Status413PayloadTooLarge, "payload_too_large");
        if (!WebhookSignature.IsValid(secret, body, signature))
            return ApiErrors.Of(StatusCodes.Status401Unauthorized, "invalid_signature");

        var eventName = request.Headers["X-GitHub-Event"].ToString();
        var deliveryId = request.Headers["X-GitHub-Delivery"].ToString();
        if (eventName.Length == 0 || deliveryId.Length is 0 or > MaxDeliveryIdLength)
            return ApiErrors.BadRequest();
        if (eventName == "ping")
            return Results.Ok(new WebhookAck("pong"));

        WebhookRoute? route;
        try
        {
            route = WebhookRoute.Parse(eventName, body);
        }
        catch (JsonException)
        {
            return ApiErrors.BadRequest();
        }
        if (route is null)
            return Results.Json(new WebhookAck("ignored"), statusCode: StatusCodes.Status202Accepted);

        await using var conn = await sync.OpenConnectionAsync(ct);
        var inserted = await conn.ExecuteAsync(new CommandDefinition("""
            insert into webhook_deliveries (delivery_id, event, action, installation_id, repository_id, subject_user_id, account_id)
            values (@deliveryId, @event, @action, @installationId, @repositoryId, @subjectUserId, @accountId)
            on conflict (delivery_id) do nothing
            """, new
        {
            deliveryId, @event = route.Event, action = route.Action, installationId = route.InstallationId,
            repositoryId = route.RepositoryId, subjectUserId = route.SubjectUserId, accountId = route.AccountId,
        }, cancellationToken: ct));
        if (inserted == 0)
            return Results.Ok(new WebhookAck("duplicate"));

        wake.Pulse();
        return Results.Json(new WebhookAck("queued"), statusCode: StatusCodes.Status202Accepted);
    }
}
