using BcNuGetHelper.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace BcNuGetHelper.Functions;

/// <summary>
/// Removes all packages for an app id across every feed: the full app (apps), the symbols
/// package (symbols), and the runtime indirect + compiled packages (runtime). An app id of
/// "*" (or "all") removes every package. Secured with a Microsoft Entra token.
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
        var suffix = $".{guid:D}".ToLowerInvariant();

        var appsIds = await IdsEndingWithAsync(PackageBuilder.FeedApps, suffix, ct);
        var symbolsIds = await IdsEndingWithAsync(PackageBuilder.FeedSymbols, suffix, ct);
        var runtimeIds = await RuntimeIdsAsync(suffix, appsIds, symbolsIds, ct);

        if (appsIds.Count + symbolsIds.Count + runtimeIds.Count == 0)
        {
            return new NotFoundObjectResult($"No packages found for app id {guid:D}.");
        }

        await storage.DeletePackageIdsAsync(PackageBuilder.FeedApps, appsIds, ct);
        await storage.DeletePackageIdsAsync(PackageBuilder.FeedSymbols, symbolsIds, ct);
        await storage.DeletePackageIdsAsync(PackageBuilder.FeedRuntime, runtimeIds, ct);
        await storage.DeleteLogosAsync(appsIds.Concat(symbolsIds).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), ct);
        await storage.DeleteDependenciesAsync(appsIds.Concat(symbolsIds).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), ct);

        logger.LogInformation(
            "Deleted packages for {AppId}: apps={Apps} symbols={Symbols} runtime={Runtime}",
            guid, appsIds.Count, symbolsIds.Count, runtimeIds.Count);
        return new OkObjectResult(new
        {
            appId = guid.ToString("D"),
            deleted = new { apps = appsIds, symbols = symbolsIds, runtime = runtimeIds },
        });
    }

    private async Task<List<string>> IdsEndingWithAsync(string feed, string suffix, CancellationToken ct)
    {
        var ids = (await storage.ListPackagesAsync(feed, ct)).Keys;
        return ids.Where(id => id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    // Runtime packages are the indirect package (publisher.name.runtime.appid, ends with the app id)
    // plus the compiled packages (publisher.name.runtime-<version>), which are matched via the
    // publisher.name prefix derived from the app's other packages.
    private async Task<List<string>> RuntimeIdsAsync(string suffix, List<string> appsIds, List<string> symbolsIds, CancellationToken ct)
    {
        var runtimeAll = (await storage.ListPackagesAsync(PackageBuilder.FeedRuntime, ct)).Keys.ToList();
        var indirect = runtimeAll.Where(id => id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).ToList();

        var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in appsIds.Concat(symbolsIds))
        {
            prefixes.Add(id[..^suffix.Length]);
        }
        foreach (var id in indirect)
        {
            var stripped = id[..^suffix.Length];
            prefixes.Add(stripped.EndsWith(".runtime", StringComparison.OrdinalIgnoreCase)
                ? stripped[..^".runtime".Length]
                : stripped);
        }

        var compiled = runtimeAll.Where(id =>
            prefixes.Any(prefix => id.StartsWith($"{prefix}.runtime-", StringComparison.OrdinalIgnoreCase))).ToList();

        return indirect.Concat(compiled).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
