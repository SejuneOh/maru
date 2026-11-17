using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace WorkBoard.Api.Infrastructure.GitHub;

public sealed class GitHubTokenClient(HttpClient http) : IGitHubTokenClient
{
    private const string ApiBase = "https://api.github.com";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<TokenIdentity?> GetIdentityAsync(string token, CancellationToken ct)
    {
        using var response = await SendAsync(token, $"{ApiBase}/user", ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return null;
        GitHubHttp.EnsureSuccess(response);
        var body = await ReadAsync<UserResponse>(response, ct);
        if (body is not { Id: > 0, Login.Length: > 0 })
            throw new GitHubSourceException(GitHubSourceErrorKind.Unexpected, "GitHub returned an unexpected answer.");

        IReadOnlyList<string>? scopes = null;
        if (response.Headers.TryGetValues("X-OAuth-Scopes", out var values))
            scopes = string.Join(',', values).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new TokenIdentity(new GitHubUser(body.Id, body.Login), scopes, ReadExpiry(response));
    }

    public async Task<OrgAccess?> GetOrganizationAccessAsync(string token, string organization, CancellationToken ct)
    {
        var org = Uri.EscapeDataString(organization);

        using var membership = await SendAsync(token, $"{ApiBase}/user/memberships/orgs/{org}", ct);
        if (membership.StatusCode == HttpStatusCode.Unauthorized)
            return null;
        if (Refused(membership) is { } refused)
            return refused;
        if (membership.StatusCode == HttpStatusCode.NotFound)
            return new OrgAccess(OrgAccessStatus.NotMember);
        GitHubHttp.EnsureSuccess(membership);
        var body = await ReadAsync<MembershipResponse>(membership, ct);
        if (body?.Organization is not { Id: > 0, Login.Length: > 0 } o)
            throw new GitHubSourceException(GitHubSourceErrorKind.Unexpected, "GitHub returned an unexpected answer.");
        if (!string.Equals(body.State, "active", StringComparison.OrdinalIgnoreCase))
            return new OrgAccess(OrgAccessStatus.NotActive, o.Id, o.Login);

        using var repos = await SendAsync(token, $"{ApiBase}/orgs/{org}/repos?per_page=1", ct);
        if (repos.StatusCode == HttpStatusCode.Unauthorized)
            return null;
        if (Refused(repos) is { } deniedRepos)
            return deniedRepos;
        GitHubHttp.EnsureSuccess(repos);
        return new OrgAccess(OrgAccessStatus.Active, o.Id, o.Login);
    }

    // A 403 that is not a rate limit: SAML SSO not authorized for the token, or an organization policy.
    private static OrgAccess? Refused(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.Forbidden)
            return null;
        if (response.Headers.TryGetValues("X-GitHub-SSO", out var sso))
            return new OrgAccess(OrgAccessStatus.SsoRequired, SsoUrl: ParseSsoUrl(sso.FirstOrDefault()));
        if (GitHubHttp.ReadRateLimit(response)?.Remaining == 0 || response.Headers.RetryAfter is not null)
            return null;
        return new OrgAccess(OrgAccessStatus.Forbidden);
    }

    // "required; url=https://github.com/orgs/<org>/sso?authorization_request=..."
    public static string? ParseSsoUrl(string? header)
    {
        const string Marker = "url=";
        var at = header?.IndexOf(Marker, StringComparison.OrdinalIgnoreCase) ?? -1;
        if (at < 0)
            return null;
        var url = header![(at + Marker.Length)..].Split(';')[0].Trim();
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
               && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            ? uri.AbsoluteUri
            : null;
    }

    // "2026-12-01 10:00:00 UTC"
    public static DateTimeOffset? ParseExpiry(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var text = value.Trim();
        if (text.EndsWith(" UTC", StringComparison.Ordinal))
            text = text[..^4] + "Z";
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at
            : null;
    }

    private static DateTimeOffset? ReadExpiry(HttpResponseMessage response) =>
        response.Headers.TryGetValues("github-authentication-token-expiration", out var v) ? ParseExpiry(v.FirstOrDefault()) : null;

    private async Task<HttpResponseMessage> SendAsync(string token, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        try
        {
            return await http.SendAsync(request, ct);
        }
        catch (HttpRequestException e)
        {
            throw new GitHubSourceException(GitHubSourceErrorKind.Unexpected, "GitHub request failed.", inner: e);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new GitHubSourceException(GitHubSourceErrorKind.Unexpected, "GitHub request timed out.", inner: e);
        }
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, ct);
        }
        catch (JsonException e)
        {
            throw new GitHubSourceException(GitHubSourceErrorKind.Unexpected, "GitHub returned malformed JSON.", inner: e);
        }
    }

    private sealed record UserResponse(long Id, string? Login);

    private sealed record MembershipResponse(string? State, OrganizationResponse? Organization);

    private sealed record OrganizationResponse(long Id, string? Login);
}
