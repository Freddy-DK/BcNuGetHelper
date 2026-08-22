using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using BcNuGetHelper.Models;
using Microsoft.Extensions.Hosting;

namespace BcNuGetHelper.Services;

/// <summary>
/// In-memory access key registry backed by a single private blob (config/accesskeys.json).
/// Loaded once at startup and only modified through the access key endpoints.
/// </summary>
public class AccessKeyStore(BlobServiceClient blobServiceClient)
{
    private const string BlobName = "accesskeys.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly BlobContainerClient _container = blobServiceClient.GetBlobContainerClient("config");
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<string, AccessKey>? _keys;
    private ETag? _etag;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await LoadCoreAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<AccessKey?> GetAsync(string name, CancellationToken ct) =>
        (await EnsureLoadedAsync(ct)).GetValueOrDefault(name);

    public async Task<AccessKey?> FindByKeyAsync(string key, CancellationToken ct) =>
        (await EnsureLoadedAsync(ct)).Values.FirstOrDefault(k => FixedTimeEquals(k.Key, key));

    /// <summary>Creates a new access key. Returns null if the name is already taken.</summary>
    public async Task<AccessKey?> CreateAsync(string name, string[] feeds, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            // Reload to pick up writes from other function app instances
            await LoadCoreAsync(ct);
            if (_keys!.ContainsKey(name))
            {
                return null;
            }
            var accessKey = new AccessKey(name, GenerateKey(), feeds);
            var updated = new Dictionary<string, AccessKey>(_keys, StringComparer.OrdinalIgnoreCase)
            {
                [name] = accessKey,
            };
            await SaveAsync(updated, ct);
            _keys = updated;
            return accessKey;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> RemoveAsync(string name, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await LoadCoreAsync(ct);
            if (!_keys!.ContainsKey(name))
            {
                return false;
            }
            var updated = new Dictionary<string, AccessKey>(_keys, StringComparer.OrdinalIgnoreCase);
            updated.Remove(name);
            await SaveAsync(updated, ct);
            _keys = updated;
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<Dictionary<string, AccessKey>> EnsureLoadedAsync(CancellationToken ct)
    {
        if (_keys is null)
        {
            await LoadAsync(ct);
        }
        return _keys!;
    }

    private async Task LoadCoreAsync(CancellationToken ct)
    {
        try
        {
            var response = await _container.GetBlobClient(BlobName).DownloadContentAsync(ct);
            var keys = response.Value.Content.ToObjectFromJson<List<AccessKey>>(JsonOptions) ?? [];
            _keys = keys.ToDictionary(k => k.Name, StringComparer.OrdinalIgnoreCase);
            _etag = response.Value.Details.ETag;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _keys = new Dictionary<string, AccessKey>(StringComparer.OrdinalIgnoreCase);
            _etag = null;
        }
    }

    private async Task SaveAsync(Dictionary<string, AccessKey> keys, CancellationToken ct)
    {
        await _container.CreateIfNotExistsAsync(cancellationToken: ct);
        var data = BinaryData.FromObjectAsJson(
            keys.Values.OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            JsonOptions);
        // ETag condition prevents lost updates when multiple instances write concurrently
        var options = new BlobUploadOptions
        {
            Conditions = _etag is { } etag
                ? new BlobRequestConditions { IfMatch = etag }
                : new BlobRequestConditions { IfNoneMatch = ETag.All },
        };
        var response = await _container.GetBlobClient(BlobName).UploadAsync(data, options, ct);
        _etag = response.Value.ETag;
    }

    private static string GenerateKey() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}

/// <summary>Loads the access key blob into memory during startup.</summary>
public class AccessKeyStoreLoader(AccessKeyStore store) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => store.LoadAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
