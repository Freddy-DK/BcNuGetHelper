using System.IO.Compression;
using System.Xml.Linq;
using BcNuGetHelper.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace BcNuGetHelper.Functions;

/// <summary>
/// NuGet push endpoint that stores a prebuilt .nupkg verbatim into a feed. Used by the
/// runtime-generation workflow (and any client pushing prebuilt packages); the upload
/// endpoint remains the path that converts .app files into packages.
/// </summary>
public class PackagePublishFunction(FeedStorage storage, AccessKeyStore accessKeys, ILogger<PackagePublishFunction> logger)
{
    [Function("PackagePublish")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "{feed}/api/v2/package")] HttpRequest req,
        string feed,
        CancellationToken ct)
    {
        feed = feed.ToLowerInvariant();
        if (!PackageBuilder.Feeds.Contains(feed))
        {
            return new NotFoundResult();
        }
        if (!await IsAuthorizedAsync(req, feed, ct))
        {
            return new UnauthorizedResult();
        }

        var nupkg = await ReadPackageAsync(req, ct);
        if (nupkg is null)
        {
            return new BadRequestObjectResult("No .nupkg found in request body.");
        }

        var identity = ExtractIdentity(nupkg);
        if (identity is null)
        {
            return new BadRequestObjectResult("Package does not contain a valid .nuspec.");
        }

        var (id, version) = identity.Value;
        await storage.SavePackageAsync(feed, id, version, nupkg, ct);
        logger.LogInformation("Pushed {PackageId} {Version} to {Feed} feed", id, version, feed);
        return new CreatedResult($"/api/{feed}/package/{id.ToLowerInvariant()}/{version.ToLowerInvariant()}", null);
    }

    private async Task<bool> IsAuthorizedAsync(HttpRequest req, string feed, CancellationToken ct)
    {
        var token = ExtractApiKey(req);
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }
        var key = await accessKeys.FindByKeyAsync(token, ct);
        return key is not null && key.CanWrite && key.Feeds.Contains(feed, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<byte[]?> ReadPackageAsync(HttpRequest req, CancellationToken ct)
    {
        if (req.HasFormContentType)
        {
            var form = await req.ReadFormAsync(ct);
            var file = form.Files.FirstOrDefault();
            if (file is null)
            {
                return null;
            }
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            return ms.Length > 0 ? ms.ToArray() : null;
        }

        using var body = new MemoryStream();
        await req.Body.CopyToAsync(body, ct);
        return body.Length > 0 ? body.ToArray() : null;
    }

    private static (string Id, string Version)? ExtractIdentity(byte[] nupkg)
    {
        using var archive = new ZipArchive(new MemoryStream(nupkg), ZipArchiveMode.Read);
        var entry = archive.Entries.FirstOrDefault(
            e => e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return null;
        }

        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        var metadata = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
        var id = metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == "id")?.Value;
        var version = metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == "version")?.Value;
        return string.IsNullOrEmpty(id) || string.IsNullOrEmpty(version)
            ? null
            : (id, VersionHelper.Normalize(version));
    }

    private static string? ExtractApiKey(HttpRequest req)
    {
        if (req.Headers.TryGetValue("X-NuGet-ApiKey", out var apiKey) && !string.IsNullOrEmpty(apiKey))
        {
            return apiKey.ToString();
        }
        var auth = req.Headers.Authorization.ToString();
        return auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : null;
    }
}
