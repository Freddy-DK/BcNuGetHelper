using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace BcNuGetHelper.Services;

/// <summary>
/// Validates Microsoft Entra bearer tokens on the admin endpoints (upload, access-key
/// management), replacing Azure Functions host keys. A caller is authorized when the token
/// is signed by the configured tenant, targets an allowed audience, and (when configured)
/// was issued to an allowed client/application id.
/// </summary>
public class AdminAuthenticator
{
    private readonly string _tenantId;
    private readonly string[] _allowedAudiences;
    private readonly string[] _allowedClientIds;
    private readonly ConfigurationManager<OpenIdConnectConfiguration>? _configManager;
    private readonly JsonWebTokenHandler _handler = new();

    public AdminAuthenticator()
    {
        _tenantId = Environment.GetEnvironmentVariable("AdminAuth__TenantId") ?? "";
        _allowedAudiences = Split(Environment.GetEnvironmentVariable("AdminAuth__AllowedAudiences"));
        _allowedClientIds = Split(Environment.GetEnvironmentVariable("AdminAuth__AllowedClientIds"));

        if (!string.IsNullOrEmpty(_tenantId))
        {
            _configManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                $"https://login.microsoftonline.com/{_tenantId}/v2.0/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever());
        }
    }

    public async Task<bool> IsAuthorizedAsync(HttpRequest req, CancellationToken ct)
    {
        if (_configManager is null)
        {
            return false;
        }

        var token = ExtractBearer(req);
        if (token is null)
        {
            return false;
        }

        var config = await _configManager.GetConfigurationAsync(ct);
        var parameters = new TokenValidationParameters
        {
            ValidIssuers =
            [
                $"https://login.microsoftonline.com/{_tenantId}/v2.0",
                $"https://sts.windows.net/{_tenantId}/",
            ],
            ValidAudiences = _allowedAudiences,
            ValidateAudience = _allowedAudiences.Length > 0,
            IssuerSigningKeys = config.SigningKeys,
            ValidateLifetime = true,
        };

        var result = await _handler.ValidateTokenAsync(token, parameters);
        if (!result.IsValid)
        {
            return false;
        }
        if (_allowedClientIds.Length == 0)
        {
            return true;
        }

        // v2 tokens carry "azp", v1 tokens carry "appid".
        var clientId = Claim(result, "azp") ?? Claim(result, "appid");
        return clientId is not null && _allowedClientIds.Contains(clientId, StringComparer.OrdinalIgnoreCase);
    }

    private static string? Claim(TokenValidationResult result, string type) =>
        result.Claims.TryGetValue(type, out var value) ? value as string : null;

    private static string? ExtractBearer(HttpRequest req)
    {
        var auth = req.Headers.Authorization.ToString();
        return auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : null;
    }

    private static string[] Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
