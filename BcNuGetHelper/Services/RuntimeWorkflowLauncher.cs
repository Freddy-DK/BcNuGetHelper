namespace BcNuGetHelper.Services;

/// <summary>Per-run runtime compilation options supplied by the caller.</summary>
public record RuntimeOptions(string Country, string AdditionalCountries, string ArtifactType);

/// <summary>
/// Builds the inputs for the runtime-generation workflow (app + stored dependency download URLs)
/// and dispatches it. Shared by the upload endpoint and the scheduled regeneration endpoint.
/// </summary>
public class RuntimeWorkflowLauncher(FeedStorage storage, GitHubWorkflowDispatcher dispatcher)
{
    /// <summary>True when the GitHub App settings needed to dispatch a workflow are present.</summary>
    public bool IsConfigured => dispatcher.IsConfigured;

    public async Task DispatchAsync(string baseUrl, Guid appId, string packageId, string version, RuntimeOptions options, CancellationToken ct)
    {
        var appUrl = AppDownloadUrl(baseUrl, packageId, version);
        var dependencies = await BuildDependencyUrlsAsync(baseUrl, packageId, version, ct);

        var inputs = new Dictionary<string, string>
        {
            // Drives the run title and, per app+version, the workflow concurrency group.
            ["run-name"] = $"Gen. Runtime {appId:D} {version}",
            ["backendUrl"] = baseUrl,
            ["apps"] = appUrl,
            ["dependencies"] = string.Join(',', dependencies),
            ["country"] = options.Country,
            ["additionalCountries"] = options.AdditionalCountries,
            ["artifactType"] = options.ArtifactType,
        };
        await dispatcher.DispatchAsync(inputs, ct);
    }

    private async Task<List<string>> BuildDependencyUrlsAsync(string baseUrl, string packageId, string version, CancellationToken ct)
    {
        var names = await storage.ListDependencyFileNamesAsync(packageId, version, ct);
        return names
            .Select(name => $"{baseUrl}/api/{PackageBuilder.FeedApps}/dependencies/{packageId}/{version}/{name}")
            .ToList();
    }

    private static string AppDownloadUrl(string baseUrl, string packageId, string version) =>
        $"{baseUrl}/api/{PackageBuilder.FeedApps}/download/{packageId}/{version}";
}
