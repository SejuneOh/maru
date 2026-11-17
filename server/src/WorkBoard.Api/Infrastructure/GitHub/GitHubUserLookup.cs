using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkBoard.Api.Infrastructure.GitHub;

public interface IGitHubUserLookup
{
    // Null when no GitHub user (type User) has this login. Throws GitHubSourceException (RateLimited, Unexpected)
    // when GitHub cannot answer. With an installation id the call uses that installation's token (5000 requests an
    // hour) when one can be minted; otherwise it is unauthenticated (60 an hour per server IP).
    Task<GitHubUser?> FindByLoginAsync(string login, long? installationId, CancellationToken ct);
}

public sealed partial class GitHubUserLookup(HttpClient http, IGitHubInstallationTokens tokens) : IGitHubUserLookup
{
    // GitHub logins: 1..39 alphanumerics or single hyphens, not starting or ending with a hyphen.
    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9]|-(?=[A-Za-z0-9])){0,38}$")]
    private static partial Regex LoginPattern();

    public static bool IsValidLogin(string login) => LoginPattern().IsMatch(login);

    public async Task<GitHubUser?> FindByLoginAsync(string login, long? installationId, CancellationToken ct)
    {
        if (!IsValidLogin(login))
            return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/users/{login}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (installationId is { } id && await TokenOrNullAsync(id, ct) is { } token)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            GitHubHttp.EnsureSuccess(response);
            var body = await response.Content.ReadFromJsonAsync<UserResponse>(ct);
            return body is { Id: > 0, Login.Length: > 0 } && string.Equals(body.Type, "User", StringComparison.Ordinal)
                ? new GitHubUser(body.Id, body.Login)
                : null;
        }
        catch (HttpRequestException e)
        {
            throw new GitHubSourceException(GitHubSourceErrorKind.Unexpected, "GitHub user lookup failed.", inner: e);
        }
        catch (JsonException e)
        {
            throw new GitHubSourceException(GitHubSourceErrorKind.Unexpected, "GitHub returned malformed JSON.", inner: e);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new GitHubSourceException(GitHubSourceErrorKind.Unexpected, "GitHub user lookup timed out.", inner: e);
        }
    }

    // A token that cannot be minted (App key missing, installation gone) falls back to an unauthenticated call.
    private async Task<string?> TokenOrNullAsync(long installationId, CancellationToken ct)
    {
        try
        {
            return await tokens.GetAsync(installationId, ct);
        }
        catch (Exception e) when (e is GitHubSourceException or GitHubAppKeyException)
        {
            return null;
        }
    }

    private sealed record UserResponse(long Id, string? Login, string? Type);
}
