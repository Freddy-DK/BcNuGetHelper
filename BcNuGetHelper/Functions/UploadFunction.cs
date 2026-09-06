using BcNuGetHelper.Models;
using BcNuGetHelper.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace BcNuGetHelper.Functions;

public class UploadFunction(
    FeedStorage storage,
    AlTool alTool,
    AdminAuthenticator admin,
    RuntimeWorkflowLauncher runtimeWorkflow,
    ILogger<UploadFunction> logger)
{
    public record UploadedPackage(string PackageId, string Version, string[] Feeds);

    [Function("Upload")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "upload")] HttpRequest req,
        CancellationToken ct)
    {
        if (!await admin.IsAuthorizedAsync(req, ct))
        {
            return new UnauthorizedResult();
        }

        var (appFiles, dependencyFiles) = await ReadUploadAsync(req, ct);
        if (appFiles.Count == 0)
        {
            return new BadRequestObjectResult("No .app file(s) found in request body.");
        }

        var runtimeOptions = ReadRuntimeOptions(req);

        var results = new List<UploadedPackage>();
        foreach (var appFile in appFiles)
        {
            AppManifest manifest;
            byte[] symbolsFile;
            var tempDir = Directory.CreateTempSubdirectory("bcnuget");
            try
            {
                var appPath = Path.Combine(tempDir.FullName, "app.app");
                await File.WriteAllBytesAsync(appPath, appFile, ct);
                manifest = await alTool.GetPackageManifestAsync(appPath, ct);

                var symbolsPath = Path.Combine(tempDir.FullName, "symbols.app");
                await alTool.CreateSymbolPackageAsync(appPath, symbolsPath, ct);
                symbolsFile = await File.ReadAllBytesAsync(symbolsPath, ct);
            }
            catch (AlToolException ex)
            {
                return new BadRequestObjectResult(ex.Message);
            }
            finally
            {
                tempDir.Delete(recursive: true);
            }

            var version = VersionHelper.Normalize(manifest.Version);
            foreach (var feed in PackageBuilder.DirectBuildFeeds)
            {
                var payload = feed == PackageBuilder.FeedSymbols ? symbolsFile : appFile;
                var nupkg = PackageBuilder.Build(manifest, payload);
                await storage.SavePackageAsync(feed, manifest.PackageId, version, nupkg, ct);
            }

            // Store the dependency artifact so runtime packages can be regenerated for future BC versions.
            foreach (var dependency in dependencyFiles)
            {
                await storage.SaveDependencyAsync(manifest.PackageId, version, dependency.FileName, dependency.Content, ct);
            }

            try
            {
                var logo = LogoExtractor.TryExtract(appFile);
                if (logo is not null)
                {
                    await storage.SaveLogoAsync(manifest.PackageId, version, logo.Content, logo.ContentType, ct);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to extract logo for {PackageId} {Version}", manifest.PackageId, version);
            }

            logger.LogInformation("Uploaded {PackageId} {Version} to all feeds", manifest.PackageId, version);

            // Runtime packages must be compiled per supported BC version, which only a build agent can do;
            // hand that off to the GitHub workflow, which pushes the results back to the runtime feed.
            await TryDispatchRuntimeWorkflowAsync(req, manifest, version, runtimeOptions, ct);

            results.Add(new UploadedPackage(manifest.PackageId, version, PackageBuilder.DirectBuildFeeds));
        }

        return new OkObjectResult(new { packages = results });
    }

    private static RuntimeOptions ReadRuntimeOptions(HttpRequest req) => new(
        req.Query["country"].ToString(),
        req.Query["additionalCountries"].ToString(),
        req.Query["artifactType"].ToString());

    private async Task TryDispatchRuntimeWorkflowAsync(HttpRequest req, AppManifest manifest, string version, RuntimeOptions options, CancellationToken ct)
    {
        if (!runtimeWorkflow.IsConfigured)
        {
            logger.LogInformation(
                "Runtime workflow not dispatched for {PackageId} {Version}: GitHub App settings (GitHubApp__ClientId/InstallationId/Repo/PrivateKey) are not all set",
                manifest.PackageId, version);
            return;
        }

        try
        {
            // The workflow fetches short-lived feed tokens via OIDC, so only the backend URL is passed.
            var baseUrl = $"{req.Scheme}://{req.Host}";
            await runtimeWorkflow.DispatchAsync(baseUrl, manifest.Id, manifest.PackageId, version, options, ct);
            logger.LogInformation("Dispatched runtime workflow for {PackageId} {Version}", manifest.PackageId, version);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to dispatch runtime workflow for {PackageId} {Version}", manifest.PackageId, version);
        }
    }

    private static async Task<(List<byte[]> Apps, List<(string FileName, byte[] Content)> Dependencies)> ReadUploadAsync(HttpRequest req, CancellationToken ct)
    {
        var apps = new List<byte[]>();
        var dependencies = new List<(string, byte[])>();
        if (req.HasFormContentType)
        {
            var form = await req.ReadFormAsync(ct);
            foreach (var file in form.Files)
            {
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms, ct);
                if (ms.Length == 0)
                {
                    continue;
                }
                // Files posted under the "dependencies" field are stored for runtime compilation, not published.
                if (file.Name.Equals("dependencies", StringComparison.OrdinalIgnoreCase))
                {
                    var name = string.IsNullOrEmpty(file.FileName) ? $"dependencies-{dependencies.Count}" : file.FileName;
                    dependencies.Add((name, ms.ToArray()));
                }
                else
                {
                    apps.Add(ms.ToArray());
                }
            }
        }
        else
        {
            using var ms = new MemoryStream();
            await req.Body.CopyToAsync(ms, ct);
            if (ms.Length > 0)
            {
                apps.Add(ms.ToArray());
            }
        }
        return (apps, dependencies);
    }
}
