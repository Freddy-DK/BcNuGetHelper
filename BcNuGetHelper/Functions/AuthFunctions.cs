using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace BcNuGetHelper.Functions;

/// <summary>
/// Proxies the GitHub OAuth device flow so the browser-based token-management app can sign in
/// without hitting GitHub's OAuth endpoints directly (they do not return CORS headers). The
/// device flow is a public client, so no client secret is involved.
/// </summary>
public class AuthFunctions(IHttpClientFactory httpClientFactory)
{
    [Function("DeviceCode")]
    public Task<IActionResult> DeviceCode(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/device/code")] HttpRequest req,
        CancellationToken ct) =>
        ProxyAsync(req, "https://github.com/login/device/code", ct);

    [Function("DeviceToken")]
    public Task<IActionResult> DeviceToken(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/device/token")] HttpRequest req,
        CancellationToken ct) =>
        ProxyAsync(req, "https://github.com/login/oauth/access_token", ct);

    private async Task<IActionResult> ProxyAsync(HttpRequest req, string url, CancellationToken ct)
    {
        using var reader = new StreamReader(req.Body);
        var body = await reader.ReadToEndAsync(ct);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("BcNuGetHelper", "1.0"));

        var client = httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, ct);
        var content = await response.Content.ReadAsStringAsync(ct);

        return new ContentResult
        {
            Content = content,
            ContentType = "application/json",
            StatusCode = (int)response.StatusCode,
        };
    }
}
