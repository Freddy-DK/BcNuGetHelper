using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace BcNuGetHelper.Services;

/// <summary>
/// Stores packages as blobs using a NuGet flat-container-friendly layout:
/// packages/{feed}/{packageIdLower}/{versionLower}/{packageIdLower}.{versionLower}.nupkg
/// </summary>
public class FeedStorage(BlobServiceClient blobServiceClient)
{
    private readonly BlobContainerClient _container = blobServiceClient.GetBlobContainerClient("packages");

    public async Task SavePackageAsync(string feed, string packageId, string version, byte[] nupkg, CancellationToken ct)
    {
        await _container.CreateIfNotExistsAsync(cancellationToken: ct);
        var blob = _container.GetBlobClient(BlobPath(feed, packageId, version));
        await blob.UploadAsync(new BinaryData(nupkg), overwrite: true, cancellationToken: ct);
    }

    public async Task<IReadOnlyList<string>> GetVersionsAsync(string feed, string packageId, CancellationToken ct)
    {
        var prefix = $"{feed}/{packageId.ToLowerInvariant()}/";
        var versions = new List<string>();
        await foreach (var blob in _container.GetBlobsAsync(prefix: prefix, cancellationToken: ct))
        {
            var segments = blob.Name.Split('/');
            if (segments.Length == 4)
            {
                versions.Add(segments[2]);
            }
        }
        return VersionHelper.Sort(versions.Distinct()).ToList();
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ListPackagesAsync(string feed, CancellationToken ct)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await foreach (var blob in _container.GetBlobsAsync(prefix: $"{feed}/", cancellationToken: ct))
            {
                var segments = blob.Name.Split('/');
                if (segments.Length != 4)
                {
                    continue;
                }
                if (!result.TryGetValue(segments[1], out var versions))
                {
                    versions = [];
                    result[segments[1]] = versions;
                }
                versions.Add(segments[2]);
            }
        }
        catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.ContainerNotFound)
        {
            // No packages uploaded yet
        }
        return result.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<string>)VersionHelper.Sort(kvp.Value.Distinct()).ToList());
    }

    public async Task<Stream?> OpenPackageAsync(string feed, string packageId, string version, CancellationToken ct)
    {
        var blob = _container.GetBlobClient(BlobPath(feed, packageId, version));
        if (!await blob.ExistsAsync(ct))
        {
            // Fall back to matching on normalized version (e.g. 1.0.0 vs 1.0.0.0)
            var match = (await GetVersionsAsync(feed, packageId, ct))
                .FirstOrDefault(v => VersionHelper.AreEqual(v, version));
            if (match is null)
            {
                return null;
            }
            blob = _container.GetBlobClient(BlobPath(feed, packageId, match));
        }

        try
        {
            return await blob.OpenReadAsync(cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.BlobNotFound)
        {
            return null;
        }
    }

    private static string BlobPath(string feed, string packageId, string version)
    {
        var id = packageId.ToLowerInvariant();
        var v = version.ToLowerInvariant();
        return $"{feed}/{id}/{v}/{id}.{v}.nupkg";
    }
}
