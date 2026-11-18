using Npgsql;
using WorkBoard.Api.Features.MeetingNotes;
using WorkBoard.Api.Infrastructure.Credentials;
using WorkBoard.Api.Infrastructure.GitHub;

namespace WorkBoard.Api.Infrastructure.Hardening;

public sealed record GuardResult(IReadOnlyList<string> Problems, IReadOnlyList<string> Warnings);

// Refuses to start with settings that are only acceptable on a developer machine, or that leave the deployment
// half-configured. Hardening:SkipChecks exists for the test hosts and must never be set on a deployment.
public static class StartupGuard
{
    public const string SkipKey = "Hardening:SkipChecks";
    public const string AllowDevOnNetworkKey = "Hardening:AllowDevOnNetwork";

    public static readonly string[] DefaultPasswords = ["workboard_app", "workboard_sync", "postgres"];

    public static GuardResult Check(IConfiguration config, IHostEnvironment env)
    {
        var problems = new List<string>();
        var warnings = new List<string>();

        if (env.IsDevelopment())
        {
            CheckDevelopmentBinding(config, problems);
            return new GuardResult(problems, warnings);
        }

        if (config.GetValue<bool>(SkipKey))
            return new GuardResult([], [$"{SkipKey} is set: production checks are skipped."]);

        CheckRolePasswords(config, problems);
        CheckWeb(config, problems);
        CheckGitHub(config, env, problems, warnings);
        return new GuardResult(problems, warnings);
    }

    // The dev identity header trusts the caller, so a Development host must stay on this machine.
    private static void CheckDevelopmentBinding(IConfiguration config, List<string> problems)
    {
        if (config.GetValue<bool>(AllowDevOnNetworkKey))
            return;
        var urls = config["urls"] ?? "";
        foreach (var url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var host = HostOf(url);
            if (host is not ("localhost" or "127.0.0.1" or "[::1]"))
                problems.Add(
                    $"Development mode (X-Dev-User sign-in) must not listen on '{url}'. Bind to localhost, or run with ASPNETCORE_ENVIRONMENT=Production.");
        }
    }

    // `http://*:5080` and `http://+:5080` are not valid URIs but bind every interface.
    private static string HostOf(string url)
    {
        var rest = url.Contains("://") ? url[(url.IndexOf("://", StringComparison.Ordinal) + 3)..] : url;
        var hostPort = rest.Split('/')[0];
        if (hostPort.StartsWith('['))
            return hostPort[..(hostPort.IndexOf(']') + 1)].ToLowerInvariant();
        var colon = hostPort.LastIndexOf(':');
        return (colon < 0 ? hostPort : hostPort[..colon]).ToLowerInvariant();
    }

    private static void CheckRolePasswords(IConfiguration config, List<string> problems)
    {
        foreach (var name in new[] { "App", "Sync" })
        {
            var text = config.GetConnectionString(name);
            if (string.IsNullOrWhiteSpace(text))
            {
                problems.Add($"ConnectionStrings:{name} is not set.");
                continue;
            }
            NpgsqlConnectionStringBuilder builder;
            try
            {
                builder = new NpgsqlConnectionStringBuilder(text);
            }
            catch (ArgumentException)
            {
                problems.Add($"ConnectionStrings:{name} is not a valid connection string.");
                continue;
            }
            var password = builder.Password;
            if (string.IsNullOrEmpty(password)
                || DefaultPasswords.Contains(password, StringComparer.Ordinal)
                || string.Equals(password, builder.Username, StringComparison.Ordinal))
                problems.Add(
                    $"ConnectionStrings:{name} uses a default or empty password. Set a real role password (docs/deploy.md, 'Role passwords').");
        }
    }

    private static void CheckWeb(IConfiguration config, List<string> problems)
    {
        var web = config.GetSection(WebOptions.Section).Get<WebOptions>() ?? new WebOptions();
        if (string.IsNullOrWhiteSpace(web.BaseUrl))
            problems.Add("Web:BaseUrl is not set. Links written into GitHub issues would point at the wrong address.");
        else if (!IsPublicUrl(web.BaseUrl))
            problems.Add($"Web:BaseUrl '{web.BaseUrl}' must be an absolute https URL.");

        if (web.AllowedOrigins is not { Length: > 0 })
            problems.Add("Web:AllowedOrigins is not set. Browsers on the deployed web app would be blocked by CORS.");
        else
            foreach (var origin in web.AllowedOrigins.Where(o => !IsPublicUrl(o)))
                problems.Add($"Web:AllowedOrigins entry '{origin}' must be an absolute https URL.");
    }

    private static bool IsPublicUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    private static void CheckGitHub(IConfiguration config, IHostEnvironment env, List<string> problems, List<string> warnings)
    {
        var app = config.GetSection(GitHubAppOptions.Section).Get<GitHubAppOptions>() ?? new GitHubAppOptions();
        var anyApp = app.AppId > 0 || !string.IsNullOrWhiteSpace(app.ClientId) || !string.IsNullOrWhiteSpace(app.ClientSecret)
            || !string.IsNullOrWhiteSpace(app.PrivateKeyPath) || !string.IsNullOrWhiteSpace(app.Slug)
            || !string.IsNullOrWhiteSpace(app.WebhookSecret);
        if (anyApp)
        {
            var missing = new List<string>();
            if (app.AppId <= 0) missing.Add("GitHub:App:AppId");
            if (string.IsNullOrWhiteSpace(app.ClientId)) missing.Add("GitHub:App:ClientId");
            if (string.IsNullOrWhiteSpace(app.ClientSecret)) missing.Add("GitHub:App:ClientSecret");
            if (string.IsNullOrWhiteSpace(app.PrivateKeyPath)) missing.Add("GitHub:App:PrivateKeyPath");
            if (string.IsNullOrWhiteSpace(app.Slug)) missing.Add("GitHub:App:Slug");
            if (missing.Count > 0)
                problems.Add($"The GitHub App is partly configured. Missing: {string.Join(", ", missing)}.");
            else if (!File.Exists(Resolve(env, app.PrivateKeyPath!)))
                problems.Add("GitHub:App:PrivateKeyPath does not point to a readable file.");
            if (!app.WebhooksEnabled)
                warnings.Add("GitHub:App:WebhookSecret is not set: webhook deliveries are rejected and boards update on the sync interval only.");
        }
        else
        {
            warnings.Add("The GitHub App is not configured: only token connections work.");
        }

        var keyFile = config.GetSection(CredentialOptions.Section).Get<CredentialOptions>()?.KeyFile;
        if (string.IsNullOrWhiteSpace(keyFile))
            warnings.Add("GitHub:Credentials:KeyFile is not set: token connections cannot be stored.");
        else if (!File.Exists(Resolve(env, keyFile)))
            problems.Add("GitHub:Credentials:KeyFile does not point to a readable file.");
    }

    private static string Resolve(IHostEnvironment env, string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(env.ContentRootPath, path);
}

public sealed class StartupGuardService(IConfiguration config, IHostEnvironment env, ILogger<StartupGuardService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var result = StartupGuard.Check(config, env);
        foreach (var warning in result.Warnings)
            logger.LogWarning("{Warning}", warning);
        if (result.Problems.Count > 0)
            throw new InvalidOperationException(
                "Refusing to start:" + string.Concat(result.Problems.Select(p => Environment.NewLine + "  - " + p)));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
