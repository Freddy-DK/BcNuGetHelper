using System.IO.Compression;
using System.Text;
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
public class NuGetFeedFunctions(FeedStorage storage, AccessKeyStore accessKeys)
{
    [Function("ServiceIndex")]
    public async Task<IActionResult> ServiceIndex(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "{feed}/index.json")] HttpRequest req,
        string feed,
        CancellationToken ct)
    {
        if (!IsValidFeed(feed))
        {
            return new NotFoundResult();
        }
        if (!AnyFeedPublic && !await IsAuthorizedAsync(req, feed, ct))
        {
            return Unauthorized(req);
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
        if (!AnyFeedPublic && !await IsAuthorizedAsync(req, feed, ct))
        {
            return Unauthorized(req);
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
        if (!AnyFeedPublic && !await IsAuthorizedAsync(req, feed, ct))
        {
            return Unauthorized(req);
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

        // Metadata (nuspec) is readable when any feed is public; the .nupkg content is always gated per feed.
        var isNuspec = fileName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase);
        var authorized = (isNuspec && AnyFeedPublic) || await IsAuthorizedAsync(req, feed, ct);
        if (!authorized)
        {
            return Unauthorized(req);
        }

        var stream = await storage.OpenPackageAsync(feed, id, version, ct);
        if (stream is null)
        {
            return new NotFoundResult();
        }

        if (isNuspec)
        {
            await using (stream)
            {
                var nuspec = await ExtractNuspecAsync(stream, ct);
                return nuspec is null ? new NotFoundResult() : new FileContentResult(nuspec, "application/xml");
            }
        }

        return new FileStreamResult(stream, "application/octet-stream")
        {
            FileDownloadName = $"{id.ToLowerInvariant()}.{version.ToLowerInvariant()}.nupkg",
        };
    }

    [Function("Logo")]
    public async Task<IActionResult> Logo(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "logo/{id}/{version?}")] HttpRequest req,
        string id,
        string? version,
        CancellationToken ct)
    {
        if (!AnyFeedPublic && !await HasAnyValidKeyAsync(req, ct))
        {
            return Unauthorized(req);
        }

        if (version is not null && version.Equals("latest", StringComparison.OrdinalIgnoreCase))
        {
            version = null;
        }

        var logo = await storage.OpenLogoAsync(id, version, ct);
        if (logo is null)
        {
            return new NotFoundResult();
        }
        return new FileStreamResult(logo.Value.Content, logo.Value.ContentType);
    }

    [Function("AppDownload")]
    public async Task<IActionResult> AppDownload(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "{feed}/download/{id}/{version}")] HttpRequest req,
        string feed,
        string id,
        string version,
        CancellationToken ct)
    {
        if (!IsValidFeed(feed))
        {
            return new NotFoundResult();
        }
        if (!await IsAuthorizedAsync(req, feed, ct))
        {
            return Unauthorized(req);
        }

        if (version.Equals("latest", StringComparison.OrdinalIgnoreCase))
        {
            var versions = await storage.GetVersionsAsync(feed, id, ct);
            if (versions.Count == 0)
            {
                return new NotFoundResult();
            }
            version = versions[^1];
        }

        var stream = await storage.OpenPackageAsync(feed, id, version, ct);
        if (stream is null)
        {
            return new NotFoundResult();
        }

        await using (stream)
        {
            var app = await ExtractAppAsync(stream, ct);
            if (app is null)
            {
                return new NotFoundResult();
            }
            return new FileContentResult(app.Value.Content, "application/octet-stream")
            {
                FileDownloadName = app.Value.FileName,
            };
        }
    }

    private static async Task<(byte[] Content, string FileName)?> ExtractAppAsync(Stream nupkg, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await nupkg.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);
        var entry = archive.Entries.FirstOrDefault(
            e => e.FullName.EndsWith(".app", StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return null;
        }
        using var entryStream = entry.Open();
        using var output = new MemoryStream();
        await entryStream.CopyToAsync(output, ct);
        return (output.ToArray(), entry.Name);
    }

    private static async Task<byte[]?> ExtractNuspecAsync(Stream nupkg, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await nupkg.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);
        var entry = archive.Entries.FirstOrDefault(
            e => e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return null;
        }
        using var entryStream = entry.Open();
        using var output = new MemoryStream();
        await entryStream.CopyToAsync(output, ct);
        return output.ToArray();
    }

    private static readonly HashSet<string> PublicFeeds = new(
        (Environment.GetEnvironmentVariable("PublicFeeds") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        StringComparer.OrdinalIgnoreCase);

    private async Task<bool> IsAuthorizedAsync(HttpRequest req, string feed, CancellationToken ct)
    {
        if (PublicFeeds.Contains(feed))
        {
            return true;
        }
        var token = ExtractToken(req);
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }
        var accessKey = await accessKeys.FindByKeyAsync(token, ct);
        return accessKey is not null && accessKey.Feeds.Contains(feed, StringComparer.OrdinalIgnoreCase);
    }

    // Metadata is public once any feed is public; a fully private deployment still requires an access key.
    private static bool AnyFeedPublic => PublicFeeds.Count > 0;

    private async Task<bool> HasAnyValidKeyAsync(HttpRequest req, CancellationToken ct)
    {
        var token = ExtractToken(req);
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }
        return await accessKeys.FindByKeyAsync(token, ct) is not null;
    }

    private static string? ExtractToken(HttpRequest req)
    {
        var auth = req.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return auth[7..].Trim();
        }
        if (auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                // NuGet clients send the token as the password part of basic credentials
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth[6..].Trim()));
                var separator = decoded.IndexOf(':');
                return separator >= 0 ? decoded[(separator + 1)..] : decoded;
            }
            catch (FormatException)
            {
                return null;
            }
        }
        if (req.Headers.TryGetValue("X-NuGet-ApiKey", out var apiKey))
        {
            return apiKey.ToString();
        }
        var query = req.Query["token"].ToString();
        return string.IsNullOrEmpty(query) ? null : query;
    }

    private static UnauthorizedResult Unauthorized(HttpRequest req)
    {
        req.HttpContext.Response.Headers.WWWAuthenticate = "Basic realm=\"BcNuGetHelper\"";
        return new UnauthorizedResult();
    }

    private static bool IsValidFeed(string feed) =>
        PackageBuilder.Feeds.Contains(feed, StringComparer.OrdinalIgnoreCase);

    private static string FeedBaseUrl(HttpRequest req, string feed) =>
        $"{req.Scheme}://{req.Host}/api/{feed.ToLowerInvariant()}";
}
