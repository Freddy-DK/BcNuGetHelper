using System.Collections.Concurrent;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Hosting;

namespace BcNuGetHelper.Services;

/// <summary>
/// Stores packages as blobs using a NuGet flat-container-friendly layout:
/// packages/{feed}/{packageIdLower}/{versionLower}/{packageIdLower}.{versionLower}.nupkg
/// The package list per feed is scanned once at startup, kept in memory and updated on upload.
/// </summary>
public class FeedStorage(BlobServiceClient blobServiceClient)
{
    private readonly BlobContainerClient _container = blobServiceClient.GetBlobContainerClient("packages");
    private readonly SemaphoreSlim _lock = new(1, 1);
    // feed -> package id -> sorted versions; snapshots replaced wholesale so reads are lock-free
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task LoadAsync(CancellationToken ct = default)
    {
        foreach (var feed in PackageBuilder.Feeds)
        {
            _cache[feed] = await ScanFeedAsync(feed, ct);
        }
    }

    public async Task SavePackageAsync(string feed, string packageId, string version, byte[] nupkg, CancellationToken ct)
    {
        feed = feed.ToLowerInvariant();
        // Ensure the feed cache exists before updating it below
        await ListPackagesAsync(feed, ct);

        await _container.CreateIfNotExistsAsync(cancellationToken: ct);
        var blob = _container.GetBlobClient(BlobPath(feed, packageId, version));
        await blob.UploadAsync(new BinaryData(nupkg), overwrite: true, cancellationToken: ct);

        await _lock.WaitAsync(ct);
        try
        {
            var current = _cache.GetValueOrDefault(feed) ?? EmptyFeed;
            var updated = current.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
            var id = packageId.ToLowerInvariant();
            var v = version.ToLowerInvariant();
            var versions = updated.GetValueOrDefault(id)?.ToList() ?? [];
            if (!versions.Contains(v))
            {
                versions.Add(v);
            }
            updated[id] = VersionHelper.Sort(versions).ToList();
            _cache[feed] = updated;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<string>> GetVersionsAsync(string feed, string packageId, CancellationToken ct)
    {
        var packages = await ListPackagesAsync(feed, ct);
        return packages.GetValueOrDefault(packageId.ToLowerInvariant()) ?? [];
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ListPackagesAsync(string feed, CancellationToken ct)
    {
        feed = feed.ToLowerInvariant();
        if (_cache.TryGetValue(feed, out var packages))
        {
            return packages;
        }

        await _lock.WaitAsync(ct);
        try
        {
            if (!_cache.TryGetValue(feed, out packages))
            {
                packages = await ScanFeedAsync(feed, ct);
                _cache[feed] = packages;
            }
            return packages;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> EmptyFeed =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ScanFeedAsync(string feed, CancellationToken ct)
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
            kvp => (IReadOnlyList<string>)VersionHelper.Sort(kvp.Value.Distinct()).ToList(),
            StringComparer.OrdinalIgnoreCase);
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

    public async Task SaveLogoAsync(string packageId, string version, byte[] content, string contentType, CancellationToken ct)
    {
        await _container.CreateIfNotExistsAsync(cancellationToken: ct);
        var blob = _container.GetBlobClient(LogoBlobPath(packageId, version));
        await blob.UploadAsync(
            new BinaryData(content),
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } },
            ct);
    }

    public async Task<(Stream Content, string ContentType)?> OpenLogoAsync(string packageId, string? version, CancellationToken ct)
    {
        // Logos are app-level; resolve against the apps feed versions (default to the latest).
        var versions = await GetVersionsAsync(PackageBuilder.FeedApps, packageId, ct);
        var resolved = version is null
            ? versions.LastOrDefault()
            : versions.FirstOrDefault(v => VersionHelper.AreEqual(v, version)) ?? version;
        if (resolved is null)
        {
            return null;
        }

        var blob = _container.GetBlobClient(LogoBlobPath(packageId, resolved));
        try
        {
            var response = await blob.DownloadStreamingAsync(cancellationToken: ct);
            return (response.Value.Content, response.Value.Details.ContentType ?? "application/octet-stream");
        }
        catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.BlobNotFound)
        {
            return null;
        }
    }

    private static string LogoBlobPath(string packageId, string version) =>
        $"logos/{packageId.ToLowerInvariant()}/{version.ToLowerInvariant()}/logo";
}

/// <summary>Scans the package blobs into memory during startup.</summary>
public class FeedStorageLoader(FeedStorage storage) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => storage.LoadAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
