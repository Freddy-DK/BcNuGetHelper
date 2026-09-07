using BcNuGetHelper.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace BcNuGetHelper.Functions;

/// <summary>
/// Removes packages for an app id across every feed: the full app (apps), the symbols package
/// (symbols), and the runtime indirect + compiled packages (runtime). A <c>version</c> query
/// parameter (default "*" = all) narrows deletion to a single app version, which also removes the
/// version-specific compiled runtime packages. An app id of "*" (or "all") removes every package.
/// Secured with a Microsoft Entra token.
/// </summary>
public class PackageDeleteFunction(FeedStorage storage, AdminAuthenticator admin, ILogger<PackageDeleteFunction> logger)
{
    [Function("DeletePackages")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "packages/{appId}")] HttpRequest req,
        string appId,
        CancellationToken ct)
    {
        if (!await admin.IsAuthorizedAsync(req, ct))
        {
            return new UnauthorizedResult();
        }

        if (appId is "*" or "all")
        {
            await storage.DeleteAllAsync(ct);
            logger.LogWarning("Deleted ALL packages from every feed");
            return new OkObjectResult(new { deleted = "all" });
        }

        if (!Guid.TryParse(appId, out var guid))
        {
            return new BadRequestObjectResult("appId must be a GUID or '*'.");
        }

        var versionInput = req.Query["version"].ToString();
        var allVersions = string.IsNullOrEmpty(versionInput) || versionInput is "*" or "all";
        var version = allVersions ? null : VersionHelper.Normalize(versionInput);

        var suffix = $".{guid:D}".ToLowerInvariant();
        var appsIds = await IdsEndingWithAsync(PackageBuilder.FeedApps, suffix, ct);
        var symbolsIds = await IdsEndingWithAsync(PackageBuilder.FeedSymbols, suffix, ct);
        var runtimeAll = (await storage.ListPackagesAsync(PackageBuilder.FeedRuntime, ct)).Keys.ToList();
        var indirectIds = runtimeAll.Where(id => id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).ToList();

        if (appsIds.Count + symbolsIds.Count + indirectIds.Count == 0)
        {
            return new NotFoundObjectResult($"No packages found for app id {guid:D}.");
        }

        // publisher.name prefixes used to find the compiled runtime packages.
        var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in appsIds.Concat(symbolsIds))
        {
            prefixes.Add(id[..^suffix.Length]);
        }
        foreach (var id in indirectIds)
        {
            var stripped = id[..^suffix.Length];
            prefixes.Add(stripped.EndsWith(".runtime", StringComparison.OrdinalIgnoreCase)
                ? stripped[..^".runtime".Length]
                : stripped);
        }

        var appPackageIds = appsIds.Concat(symbolsIds).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (allVersions)
        {
            var compiledIds = runtimeAll
                .Where(id => prefixes.Any(prefix => id.StartsWith($"{prefix}.runtime-", StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var runtimeIds = indirectIds.Concat(compiledIds).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            await storage.DeletePackageIdsAsync(PackageBuilder.FeedApps, appsIds, ct);
            await storage.DeletePackageIdsAsync(PackageBuilder.FeedSymbols, symbolsIds, ct);
            await storage.DeletePackageIdsAsync(PackageBuilder.FeedRuntime, runtimeIds, ct);
            await storage.DeleteLogosAsync(appPackageIds, ct);

            logger.LogInformation("Deleted all versions for {AppId}", guid);
            return new OkObjectResult(new
            {
                appId = guid.ToString("D"),
                version = "all",
                deleted = new { apps = appsIds, symbols = symbolsIds, runtime = runtimeIds },
            });
        }

        // Compiled runtime packages are per app version: publisher.name.runtime-<version with dashes>.
        var dashed = Version.TryParse(version, out var v)
            ? $"{v.Major}-{v.Minor}-{Math.Max(v.Build, 0)}-{Math.Max(v.Revision, 0)}"
            : version!.Replace('.', '-');
        var compiledForVersion = prefixes
            .Select(prefix => $"{prefix}.runtime-{dashed}")
            .Where(id => runtimeAll.Contains(id, StringComparer.OrdinalIgnoreCase))
            .ToList();

        await storage.DeletePackageVersionsAsync(PackageBuilder.FeedApps, appsIds, version!, ct);
        await storage.DeletePackageVersionsAsync(PackageBuilder.FeedSymbols, symbolsIds, version!, ct);
        await storage.DeletePackageVersionsAsync(PackageBuilder.FeedRuntime, indirectIds, version!, ct);
        // The compiled runtime packages for this app version are removed in full (every BC version).
        await storage.DeletePackageIdsAsync(PackageBuilder.FeedRuntime, compiledForVersion, ct);
        await storage.DeleteLogoVersionsAsync(appPackageIds, version!, ct);

        logger.LogInformation("Deleted version {Version} for {AppId}", version, guid);
        return new OkObjectResult(new
        {
            appId = guid.ToString("D"),
            version,
            deleted = new { apps = appsIds, symbols = symbolsIds, runtimeIndirect = indirectIds, runtimeCompiled = compiledForVersion },
        });
    }

    private async Task<List<string>> IdsEndingWithAsync(string feed, string suffix, CancellationToken ct)
    {
        var ids = (await storage.ListPackagesAsync(feed, ct)).Keys;
        return ids.Where(id => id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).ToList();
    }
}
