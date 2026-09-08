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
    private const string EphemeralPrefix = "ephemeral-";
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

    /// <summary>Returns the named access keys, reloading from storage so the management UI stays fresh. Short-lived ephemeral keys (issued by the token endpoint) are never shown.</summary>
    public async Task<IReadOnlyList<AccessKey>> ListAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await LoadCoreAsync(ct);
            return _keys!.Values
                .Where(k => !IsEphemeral(k.Name))
                .OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<AccessKey?> FindByKeyAsync(string key, CancellationToken ct) =>
        (await EnsureLoadedAsync(ct)).Values.FirstOrDefault(k => !k.IsExpired && FixedTimeEquals(k.Key, key));

    /// <summary>Creates a new access key. Returns null if the name is already taken.</summary>
    public async Task<AccessKey?> CreateAsync(
        string name, string[] feeds, string type, string? description, string email, DateTimeOffset? expires, CancellationToken ct)
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
            var accessKey = new AccessKey(name, GenerateKey(), feeds, type, expires, description, email);
            var updated = WithoutExpiredEphemeral(_keys!.Values);
            updated[name] = accessKey;
            await SaveAsync(updated, ct);
            _keys = updated;
            return accessKey;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Sets (or clears) a key's expiry. Used to revoke (expire now) or renew (extend/clear) a key.
    /// Returns the updated key, or null when the name does not exist.
    /// </summary>
    public async Task<AccessKey?> SetExpiryAsync(string name, DateTimeOffset? expires, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await LoadCoreAsync(ct);
            if (!_keys!.TryGetValue(name, out var existing))
            {
                return null;
            }
            var updatedKey = existing with { Expires = expires };
            var updated = WithoutExpiredEphemeral(_keys!.Values);
            updated[name] = updatedKey;
            await SaveAsync(updated, ct);
            _keys = updated;
            return updatedKey;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Creates one or more short-lived keys in a single write, pruning expired keys and retrying
    /// on concurrent-write conflicts (the runtime matrix requests tokens in parallel).
    /// </summary>
    public async Task<IReadOnlyList<AccessKey>> CreateEphemeralAsync(
        IReadOnlyList<(string[] Feeds, string Type)> specs, TimeSpan lifetime, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                await LoadCoreAsync(ct);
                // Prune only expired ephemeral keys; revoked named keys are kept for auditing.
                var updated = WithoutExpiredEphemeral(_keys!.Values);
                var expires = DateTimeOffset.UtcNow.Add(lifetime);
                var created = specs
                    .Select(spec => new AccessKey($"ephemeral-{Guid.NewGuid():N}", GenerateKey(), spec.Feeds, spec.Type, expires))
                    .ToList();
                foreach (var key in created)
                {
                    updated[key.Name] = key;
                }

                try
                {
                    await SaveAsync(updated, ct);
                    _keys = updated;
                    return created;
                }
                catch (RequestFailedException ex) when (ex.Status == 412 && attempt < 5)
                {
                    // Another instance wrote concurrently; reload the ETag and retry.
                }
            }
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
            var updated = WithoutExpiredEphemeral(_keys.Values);
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

    private static bool IsEphemeral(string name) =>
        name.StartsWith(EphemeralPrefix, StringComparison.OrdinalIgnoreCase);

    // Drops expired ephemeral (token-endpoint) keys so they don't accumulate; keeps everything else.
    private static Dictionary<string, AccessKey> WithoutExpiredEphemeral(IEnumerable<AccessKey> keys) =>
        keys.Where(k => !(IsEphemeral(k.Name) && k.IsExpired))
            .ToDictionary(k => k.Name, StringComparer.OrdinalIgnoreCase);

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
