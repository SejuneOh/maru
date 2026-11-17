using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace WorkBoard.Api.Infrastructure.GitHub;

public sealed class GitHubAppClient(
    HttpClient http, IOptions<GitHubAppOptions> options, GitHubAppKey key, IGitHubInstallationTokens tokens) : IGitHubAppClient
{
    private const string ApiBase = "https://api.github.com";
    private const string TokenUrl = "https://github.com/login/oauth/access_token";
    private const int PageSize = 100;
    private const int MaxPages = 10;

    public async Task<string?> ExchangeCodeAsync(string code, CancellationToken ct)
    {
        var o = options.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = o.ClientId!,
                ["client_secret"] = o.ClientSecret!,
                ["code"] = code,
            }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return null;
        var body = await response.Content.ReadFromJsonAsync<TokenResponse>(ct);
        return string.IsNullOrEmpty(body?.AccessToken) ? null : body.AccessToken;
    }

    public async Task<GitHubUser?> GetUserAsync(string userToken, CancellationToken ct)
    {
        using var response = await GetAsync($"{ApiBase}/user", userToken, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return null;
        response.EnsureSuccessStatusCode();
        var user = await response.Content.ReadFromJsonAsync<UserResponse>(ct);
        return user is null ? null : new GitHubUser(user.Id, user.Login);
    }

    public async Task<IReadOnlyList<long>?> ListUserInstallationIdsAsync(string userToken, CancellationToken ct)
    {
        var ids = new List<long>();
        for (var page = 1; page <= MaxPages; page++)
        {
            using var response = await GetAsync($"{ApiBase}/user/installations?per_page={PageSize}&page={page}", userToken, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return null;
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<InstallationsResponse>(ct);
            var items = body?.Installations ?? [];
            ids.AddRange(items.Select(i => i.Id));
            if (items.Count < PageSize)
                break;
        }
        return ids;
    }

    public async Task<GitHubInstallation?> GetInstallationAsync(long installationId, CancellationToken ct)
    {
        var jwt = GitHubAppJwt.Create(options.Value.AppId, key.Get(), DateTimeOffset.UtcNow);
        using var response = await GetAsync($"{ApiBase}/app/installations/{installationId}", jwt, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<InstallationResponse>(ct);
        return body?.Account is null
            ? null
            : new GitHubInstallation(body.Account.Id, body.Account.Login, body.Account.Type, body.RepositorySelection);
    }

    public async Task<IReadOnlyList<GitHubAppInstallation>> ListAppInstallationsAsync(CancellationToken ct)
    {
        var jwt = GitHubAppJwt.Create(options.Value.AppId, key.Get(), DateTimeOffset.UtcNow);
        var list = new List<GitHubAppInstallation>();
        for (var page = 1; page <= MaxPages; page++)
        {
            using var response = await GetAsync($"{ApiBase}/app/installations?per_page={PageSize}&page={page}", jwt, ct);
            response.EnsureSuccessStatusCode();
            var items = await response.Content.ReadFromJsonAsync<List<AppInstallationResponse>>(ct) ?? [];
            list.AddRange(items
                .Where(i => i.Account is not null)
                .Select(i => new GitHubAppInstallation(
                    i.Id, i.Account!.Id, i.Account.Login, i.Account.Type, i.RepositorySelection, i.CreatedAt)));
            if (items.Count < PageSize)
                break;
        }
        return list;
    }

    public async Task<OrgMembership> GetOrganizationMembershipAsync(
        long installationId, string organization, long userId, string login, CancellationToken ct)
    {
        if (!GitHubUserLookup.IsValidLogin(organization) || !GitHubUserLookup.IsValidLogin(login))
            return OrgMembership.NotMember;
        using var response = await InstallationGetAsync(installationId, $"{ApiBase}/orgs/{organization}/memberships/{login}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return OrgMembership.NotMember;
        if (NotGranted(response))
            return OrgMembership.NotGranted;
        GitHubHttp.EnsureSuccess(response);
        var body = await response.Content.ReadFromJsonAsync<MembershipResponse>(ct);
        if (body?.User?.Id != userId || body.State != "active")
            return OrgMembership.NotMember;
        return body.Role == "admin" ? OrgMembership.Owner : OrgMembership.Member;
    }

    public async Task<IReadOnlyList<GitHubUser>?> ListOrganizationMembersAsync(
        long installationId, string organization, CancellationToken ct)
    {
        if (!GitHubUserLookup.IsValidLogin(organization))
            return [];
        var members = new List<GitHubUser>();
        for (var page = 1; page <= MaxPages; page++)
        {
            using var response = await InstallationGetAsync(installationId,
                $"{ApiBase}/orgs/{organization}/members?per_page={PageSize}&page={page}", ct);
            if (NotGranted(response))
                return null;
            GitHubHttp.EnsureSuccess(response);
            var items = await response.Content.ReadFromJsonAsync<List<UserResponse>>(ct) ?? [];
            members.AddRange(items.Select(u => new GitHubUser(u.Id, u.Login)));
            if (items.Count < PageSize)
                break;
        }
        return members;
    }

    // A 403 that is not a rate limit: the installation lacks the permission.
    private static bool NotGranted(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.Forbidden)
            return false;
        var limit = GitHubHttp.ReadRateLimit(response);
        return limit?.Remaining != 0 && response.Headers.RetryAfter is null;
    }

    private async Task<HttpResponseMessage> InstallationGetAsync(long installationId, string url, CancellationToken ct)
    {
        var response = await GetAsync(url, await tokens.GetAsync(installationId, ct), ct);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;
        response.Dispose();
        tokens.Invalidate(installationId);
        return await GetAsync(url, await tokens.GetAsync(installationId, ct), ct);
    }

    private Task<HttpResponseMessage> GetAsync(string url, string bearer, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return http.SendAsync(request, ct);
    }

    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string? AccessToken);

    private sealed record UserResponse(long Id, string Login);

    private sealed record InstallationsResponse(List<InstallationId>? Installations);

    private sealed record InstallationId(long Id);

    private sealed record InstallationResponse(
        AccountResponse? Account,
        [property: JsonPropertyName("repository_selection")] string? RepositorySelection);

    private sealed record AccountResponse(long Id, string Login, string Type);

    private sealed record AppInstallationResponse(
        long Id,
        AccountResponse? Account,
        [property: JsonPropertyName("repository_selection")] string? RepositorySelection,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

    private sealed record MembershipResponse(string? State, string? Role, UserResponse? User);
}
