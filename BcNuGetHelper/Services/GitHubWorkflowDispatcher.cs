using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace BcNuGetHelper.Services;

/// <summary>
/// Dispatches the runtime-package generation workflow in the GitHub repository using a
/// GitHub App: a short-lived JWT (signed with the app private key) is exchanged for an
/// installation access token, which then authorizes the workflow_dispatch call.
/// </summary>
public class GitHubWorkflowDispatcher
{
    private readonly string? _clientId;
    private readonly string? _installationId;
    private readonly string? _repo;
    private readonly string _workflowFile;
    private readonly string _ref;
    private readonly string? _privateKey;
    private readonly HttpClient _http = CreateClient();

    public GitHubWorkflowDispatcher()
    {
        _clientId = Environment.GetEnvironmentVariable("GitHubApp__ClientId");
        _installationId = Environment.GetEnvironmentVariable("GitHubApp__InstallationId");
        _repo = Environment.GetEnvironmentVariable("GitHubApp__Repo");
        _workflowFile = Environment.GetEnvironmentVariable("GitHubApp__WorkflowFile") ?? "generate-runtime-nuget.yml";
        _ref = Environment.GetEnvironmentVariable("GitHubApp__Ref") ?? "main";
        _privateKey = Environment.GetEnvironmentVariable("GitHubApp__PrivateKey");
    }

    /// <summary>True when the settings needed to dispatch a workflow are present.</summary>
    public bool IsConfigured =>
        !string.IsNullOrEmpty(_clientId)
        && !string.IsNullOrEmpty(_installationId)
        && !string.IsNullOrEmpty(_repo)
        && !string.IsNullOrEmpty(_privateKey);

    public async Task DispatchAsync(IReadOnlyDictionary<string, string> inputs, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("GitHub App settings are not configured.");
        }

        var jwt = CreateJwt(_clientId!, _privateKey!);
        var installationToken = await GetInstallationTokenAsync(jwt, ct);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"repos/{_repo}/actions/workflows/{_workflowFile}/dispatches")
        {
            Content = JsonContent.Create(new { @ref = _ref, inputs }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", installationToken);

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"workflow_dispatch failed ({(int)response.StatusCode}): {body}");
        }
    }

    private async Task<string> GetInstallationTokenAsync(string jwt, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"app/installations/{_installationId}/access_tokens");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to get installation token ({(int)response.StatusCode}): {body}");
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("Installation token response contained no token.");
    }

    private static string CreateJwt(string clientId, string pem)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        var now = DateTimeOffset.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = clientId,
            // 60s in the past guards against clock drift; GitHub allows a 10 minute lifetime.
            IssuedAt = now.AddSeconds(-60).UtcDateTime,
            Expires = now.AddMinutes(9).UtcDateTime,
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(rsa.ExportParameters(true)), SecurityAlgorithms.RsaSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { BaseAddress = new Uri("https://api.github.com/") };
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BcNuGetHelper", "1.0"));
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return http;
    }
}
