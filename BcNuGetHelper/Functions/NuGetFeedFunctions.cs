using BcNuGetHelper.Models;
using BcNuGetHelper.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace BcNuGetHelper.Functions;

/// <summary>
/// Read-only NuGet v3 feed endpoints (service index, search, flat container),
/// served per feed: apps, runtime and symbols.
/// </summary>
public class NuGetFeedFunctions(FeedStorage storage)
{
    [Function("ServiceIndex")]
    public IActionResult ServiceIndex(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "{feed}/index.json")] HttpRequest req,
        string feed)
    {
        if (!IsValidFeed(feed))
        {
            return new NotFoundResult();
        }

        var baseUrl = FeedBaseUrl(req, feed);
        return new OkObjectResult(new ServiceIndex("3.0.0",
        [
            new($"{baseUrl}/query", "SearchQueryService"),
            new($"{baseUrl}/query", "SearchQueryService/3.0.0-beta"),
            new($"{baseUrl}/query", "SearchQueryService/3.0.0-rc"),
            new($"{baseUrl}/package/", "PackageBaseAddress/3.0.0"),
        ]));
    }

    [Function("Search")]
    public async Task<IActionResult> Search(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "{feed}/query")] HttpRequest req,
        string feed,
        CancellationToken ct)
    {
        if (!IsValidFeed(feed))
        {
            return new NotFoundResult();
        }

        var q = req.Query["q"].ToString();
        var skip = int.TryParse(req.Query["skip"], out var s) ? Math.Max(s, 0) : 0;
        var take = int.TryParse(req.Query["take"], out var t) ? Math.Clamp(t, 1, 1000) : 100;
        var tokens = q.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var packages = await storage.ListPackagesAsync(feed, ct);
        var baseUrl = FeedBaseUrl(req, feed);

        var matches = packages
            .Where(p => tokens.All(token => p.Key.Contains(token, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var data = matches
            .Skip(skip)
            .Take(take)
            .Select(p => new SearchResult(
                RegistrationId: $"{baseUrl}/package/{p.Key}/index.json",
                Type: "Package",
                Id: p.Key,
                Version: p.Value[^1],
                Versions: p.Value
                    .Select(v => new SearchResultVersion($"{baseUrl}/package/{p.Key}/{v}/{p.Key}.{v}.nupkg", v))
                    .ToList()))
            .ToList();

        return new OkObjectResult(new SearchResponse(matches.Count, data));
    }

    [Function("PackageVersions")]
    public async Task<IActionResult> PackageVersions(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "{feed}/package/{id}/index.json")] HttpRequest req,
        string feed,
        string id,
        CancellationToken ct)
    {
        if (!IsValidFeed(feed))
        {
            return new NotFoundResult();
        }

        var versions = await storage.GetVersionsAsync(feed, id, ct);
        if (versions.Count == 0)
        {
            return new NotFoundResult();
        }
        return new OkObjectResult(new PackageVersionIndex(versions));
    }

    [Function("PackageDownload")]
    public async Task<IActionResult> PackageDownload(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "{feed}/package/{id}/{version}/{fileName}")] HttpRequest req,
        string feed,
        string id,
        string version,
        string fileName,
        CancellationToken ct)
    {
        if (!IsValidFeed(feed))
        {
            return new NotFoundResult();
        }

        var stream = await storage.OpenPackageAsync(feed, id, version, ct);
        if (stream is null)
        {
            return new NotFoundResult();
        }
        return new FileStreamResult(stream, "application/octet-stream")
        {
            FileDownloadName = $"{id.ToLowerInvariant()}.{version.ToLowerInvariant()}.nupkg",
        };
    }

    private static bool IsValidFeed(string feed) =>
        PackageBuilder.Feeds.Contains(feed, StringComparer.OrdinalIgnoreCase);

    private static string FeedBaseUrl(HttpRequest req, string feed) =>
        $"{req.Scheme}://{req.Host}/api/{feed.ToLowerInvariant()}";
}
