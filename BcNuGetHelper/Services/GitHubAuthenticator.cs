using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace BcNuGetHelper.Services;

/// <summary>
/// Authenticates the token-management web app. Callers present a GitHub token (personal access
/// token or an OAuth device-flow token) as a bearer token. The token is validated against the
/// GitHub API and the resulting login is checked against the allow-list configured in the
/// <c>WebAppUsers</c> app setting (sourced from the WEBAPPUSERS GitHub variable).
/// </summary>
public class GitHubAuthenticator(IHttpClientFactory httpClientFactory)
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    private readonly HashSet<string> _allowedUsers = new(
        Split(Environment.GetEnvironmentVariable("WebAppUsers")), StringComparer.OrdinalIgnoreCase);

    // Token -> (user, cached-at). Avoids calling the GitHub API on every request.
    private readonly ConcurrentDictionary<string, (GitHubUser User, DateTimeOffset At)> _cache = new();

    public record GitHubUser(
        [property: JsonPropertyName("login")] string Login,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("avatar_url")] string? AvatarUrl);

    /// <summary>True when the caller presents a valid GitHub token for an allow-listed user.</summary>
    public async Task<bool> IsAuthorizedAsync(HttpRequest req, CancellationToken ct)
    {
        var (user, allowed) = await AuthenticateAsync(req, ct);
        return user is not null && allowed;
    }

    /// <summary>
    /// Validates the caller's GitHub token and reports whether the user is allow-listed. Returns a
    /// null user when no valid token is presented.
    /// </summary>
    public async Task<(GitHubUser? User, bool Allowed)> AuthenticateAsync(HttpRequest req, CancellationToken ct)
    {
        var token = ExtractBearer(req);
        if (token is null)
        {
            return (null, false);
        }

        var user = await GetUserAsync(token, ct);
        return user is null ? (null, false) : (user, IsAllowed(user.Login));
    }

    public bool IsAllowed(string login) => _allowedUsers.Contains(login);

    private async Task<GitHubUser?> GetUserAsync(string token, CancellationToken ct)
    {
        if (_cache.TryGetValue(token, out var cached) && DateTimeOffset.UtcNow - cached.At < CacheLifetime)
        {
            return cached.User;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("BcNuGetHelper", "1.0"));

        var client = httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var user = await response.Content.ReadFromJsonAsync<GitHubUser>(cancellationToken: ct);
        if (user is not null && !string.IsNullOrEmpty(user.Login))
        {
            _cache[token] = (user, DateTimeOffset.UtcNow);
        }
        return user;
    }

    private static string? ExtractBearer(HttpRequest req)
    {
        var auth = req.Headers.Authorization.ToString();
        return auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : null;
    }

    private static string[] Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
