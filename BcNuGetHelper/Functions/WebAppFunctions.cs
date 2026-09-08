using BcNuGetHelper.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace BcNuGetHelper.Functions;

/// <summary>
/// Serves the token-management single-page app and its supporting endpoints. The static assets are
/// bundled into <c>wwwroot</c> at build time and served from <c>/api/app</c> (same origin as the
/// API, so no CORS is required).
/// </summary>
public class WebAppFunctions(GitHubAuthenticator github)
{
    private static readonly string WebRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".ico"] = "image/x-icon",
        [".webmanifest"] = "application/manifest+json",
        [".woff2"] = "font/woff2",
    };

    /// <summary>Public configuration the SPA needs before sign-in (the client id is not a secret).</summary>
    [Function("WebAppConfig")]
    public IActionResult Config(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "config")] HttpRequest req)
    {
        // The web app signs in with the same GitHub App used for workflow dispatch.
        var clientId = Environment.GetEnvironmentVariable("GitHubApp__ClientId");
        return new OkObjectResult(new { clientId = clientId ?? "" });
    }

    /// <summary>Reports the signed-in GitHub user and whether they are allow-listed for the app.</summary>
    [Function("WebAppMe")]
    public async Task<IActionResult> Me(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "me")] HttpRequest req,
        CancellationToken ct)
    {
        var (user, allowed) = await github.AuthenticateAsync(req, ct);
        if (user is null)
        {
            return new UnauthorizedResult();
        }

        return new OkObjectResult(new
        {
            login = user.Login,
            name = user.Name,
            avatarUrl = user.AvatarUrl,
            hasAccess = allowed,
        });
    }

    [Function("WebAppRoot")]
    public IActionResult Root(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "app")] HttpRequest req) =>
        ServeFile("index.html");

    [Function("WebAppAssets")]
    public IActionResult Assets(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "app/{*path}")] HttpRequest req,
        string? path) =>
        ServeFile(path);

    private IActionResult ServeFile(string? relativePath)
    {
        var requested = string.IsNullOrEmpty(relativePath) ? "index.html" : relativePath;

        // Resolve within the web root and reject any path that escapes it.
        var fullPath = Path.GetFullPath(Path.Combine(WebRoot, requested));
        var rootPrefix = WebRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return new BadRequestResult();
        }

        // SPA fallback: unknown routes without a file extension serve index.html for client-side routing.
        if (!File.Exists(fullPath))
        {
            if (Path.HasExtension(requested))
            {
                return new NotFoundResult();
            }
            fullPath = Path.Combine(WebRoot, "index.html");
            if (!File.Exists(fullPath))
            {
                return new NotFoundResult();
            }
        }

        var contentType = ContentTypes.GetValueOrDefault(Path.GetExtension(fullPath), "application/octet-stream");
        return new PhysicalFileResult(fullPath, contentType);
    }
}
