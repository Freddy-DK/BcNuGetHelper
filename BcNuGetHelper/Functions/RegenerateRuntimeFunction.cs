using BcNuGetHelper.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace BcNuGetHelper.Functions;

/// <summary>
/// Re-dispatches the runtime-generation workflow for stored apps so runtime packages are produced
/// for newly released Business Central versions. The workflow only builds versions that are missing,
/// so this is safe to run on a schedule. Secured with a Microsoft Entra token.
/// </summary>
public class RegenerateRuntimeFunction(
    FeedStorage storage,
    AdminAuthenticator admin,
    RuntimeWorkflowLauncher launcher,
    ILogger<RegenerateRuntimeFunction> logger)
{
    [Function("RegenerateRuntime")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "regenerate")] HttpRequest req,
        CancellationToken ct)
    {
        if (!await admin.IsAuthorizedAsync(req, ct))
        {
            return new UnauthorizedResult();
        }
        if (!launcher.IsConfigured)
        {
            return new ConflictObjectResult("Runtime generation is not configured (GitHub App settings missing).");
        }

        var allVersions = req.Query["allVersions"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase);
        var options = new RuntimeOptions(
            req.Query["country"].ToString(),
            req.Query["additionalCountries"].ToString(),
            req.Query["artifactType"].ToString());
        var baseUrl = $"{req.Scheme}://{req.Host}";

        var packages = await storage.ListPackagesAsync(PackageBuilder.FeedApps, ct);
        var dispatched = new List<object>();
        foreach (var (packageId, versions) in packages)
        {
            var appId = ExtractAppId(packageId);
            if (appId is null || versions.Count == 0)
            {
                continue;
            }
            var targets = allVersions ? versions : [versions[^1]];
            foreach (var version in targets)
            {
                try
                {
                    await launcher.DispatchAsync(baseUrl, appId.Value, packageId, version, options, ct);
                    dispatched.Add(new { packageId, version });
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to dispatch regeneration for {PackageId} {Version}", packageId, version);
                }
            }
        }

        logger.LogInformation("Regeneration dispatched {Count} runtime workflow run(s)", dispatched.Count);
        return new OkObjectResult(new { dispatched });
    }

    // Package ids follow publisher.name.appid; the app id is the last dot-separated segment.
    private static Guid? ExtractAppId(string packageId)
    {
        var lastSegment = packageId[(packageId.LastIndexOf('.') + 1)..];
        return Guid.TryParse(lastSegment, out var id) ? id : null;
    }
}
