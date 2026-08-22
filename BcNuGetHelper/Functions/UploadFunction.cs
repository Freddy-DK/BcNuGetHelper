using BcNuGetHelper.Models;
using BcNuGetHelper.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace BcNuGetHelper.Functions;

public class UploadFunction(FeedStorage storage, ILogger<UploadFunction> logger)
{
    public record UploadedPackage(string PackageId, string Version, string[] Feeds);

    [Function("Upload")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "upload")] HttpRequest req,
        CancellationToken ct)
    {
        var appFiles = await ReadAppFilesAsync(req, ct);
        if (appFiles.Count == 0)
        {
            return new BadRequestObjectResult("No .app file(s) found in request body.");
        }

        var results = new List<UploadedPackage>();
        foreach (var appFile in appFiles)
        {
            AppManifest manifest;
            try
            {
                manifest = AppFileParser.Parse(appFile);
            }
            catch (InvalidDataException ex)
            {
                return new BadRequestObjectResult(ex.Message);
            }

            var version = VersionHelper.Normalize(manifest.Version);
            foreach (var feed in PackageBuilder.Feeds)
            {
                var nupkg = PackageBuilder.Build(manifest, appFile, feed);
                await storage.SavePackageAsync(feed, manifest.PackageId, version, nupkg, ct);
            }

            logger.LogInformation("Uploaded {PackageId} {Version} to all feeds", manifest.PackageId, version);
            results.Add(new UploadedPackage(manifest.PackageId, version, PackageBuilder.Feeds));
        }

        return new OkObjectResult(new { packages = results });
    }

    private static async Task<List<byte[]>> ReadAppFilesAsync(HttpRequest req, CancellationToken ct)
    {
        var files = new List<byte[]>();
        if (req.HasFormContentType)
        {
            var form = await req.ReadFormAsync(ct);
            foreach (var file in form.Files)
            {
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms, ct);
                if (ms.Length > 0)
                {
                    files.Add(ms.ToArray());
                }
            }
        }
        else
        {
            using var ms = new MemoryStream();
            await req.Body.CopyToAsync(ms, ct);
            if (ms.Length > 0)
            {
                files.Add(ms.ToArray());
            }
        }
        return files;
    }
}
