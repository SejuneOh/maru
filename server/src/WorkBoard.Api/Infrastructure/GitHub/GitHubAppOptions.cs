namespace WorkBoard.Api.Infrastructure.GitHub;

public sealed class GitHubAppOptions
{
    public const string Section = "GitHub:App";

    public long AppId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    // Absolute, or relative to the API content root.
    public string? PrivateKeyPath { get; set; }
    public string? Slug { get; set; }

    // Shared secret of the App's webhook. Without it POST /api/github/webhook answers 503 and does nothing.
    public string? WebhookSecret { get; set; }

    public bool WebhooksEnabled => !string.IsNullOrWhiteSpace(WebhookSecret);

    // The login GitHub shows for what this App writes; null without a Slug.
    public string? BotLogin => string.IsNullOrWhiteSpace(Slug) ? null : Slug.Trim() + "[bot]";

    public bool IsBot(string? login) => BotLogin is { } bot && string.Equals(login, bot, StringComparison.OrdinalIgnoreCase);

    // Enough to mint installation tokens and read data; the connect flow also needs the client secret.
    public bool CanReadInstallations => AppId > 0 && !string.IsNullOrWhiteSpace(PrivateKeyPath);

    public bool Configured =>
        AppId > 0
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && !string.IsNullOrWhiteSpace(PrivateKeyPath);
}
