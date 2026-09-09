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
    // The '|' separator is rejected by the access key name validator, so these internal prefixes can
    // never collide with a user-assigned name.
    private const string EphemeralPrefix = "ephemeral|";
    // Short-lived tokens issued to the runtime-generation workflow by the token endpoint. Kept distinct
    // from rotation grace keys (EphemeralPrefix) so only genuine workflow tokens are trusted for the
    // internal dependency download endpoint.
    private const string WorkflowTokenPrefix = "token|";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly BlobContainerClient _container = blobServiceClient.GetBlobContainerClient("config");
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<string, AccessKey>? _keys;
    private ETag? _etag;
    // Bounds how long a revocation on another instance can go unnoticed: the cache is re-read from
    // shared storage once it is older than this.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private DateTimeOffset _loadedAt;

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
    /// Sets (or clears) a key's expiry. Used to renew (extend/clear) a key.
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
    /// Revokes a key: expires it immediately and removes any rotation grace keys derived from it, so no
    /// previously issued credential survives. Renewing afterwards therefore starts from a clean slate.
    /// Returns the revoked key, or null when the name does not exist.
    /// </summary>
    public async Task<AccessKey?> RevokeAsync(string name, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await LoadCoreAsync(ct);
            if (!_keys!.TryGetValue(name, out var existing))
            {
                return null;
            }
            var revoked = existing with { Expires = DateTimeOffset.UtcNow };
            var updated = WithoutExpiredEphemeral(_keys!.Values);
            updated[name] = revoked;
            RemoveGraceKeys(updated, name);
            await SaveAsync(updated, ct);
            _keys = updated;
            return revoked;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Rotates a key: issues a fresh active key under the same name and keeps the previous key value
    /// alive for a grace period under a hidden <c>ephemeral|{name}</c> entry that expires after
    /// <paramref name="oldKeyLifetime"/>. Returns the new key, or null when the name does not exist.
    /// </summary>
    public async Task<AccessKey?> RotateAsync(string name, TimeSpan oldKeyLifetime, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await LoadCoreAsync(ct);
            if (!_keys!.TryGetValue(name, out var existing))
            {
                return null;
            }

            var updated = WithoutExpiredEphemeral(_keys.Values);

            // Preserve the old key value under a hidden ephemeral name for the grace period.
            var graceName = $"{EphemeralPrefix}{name}";
            if (updated.ContainsKey(graceName))
            {
                graceName = $"{EphemeralPrefix}{name}-{Guid.NewGuid():N}";
            }
            var graceKey = existing with { Name = graceName, Expires = DateTimeOffset.UtcNow.Add(oldKeyLifetime) };

            // Issue a fresh active key under the original name, inheriting the old key's expiry.
            var newKey = existing with { Key = GenerateKey() };

            updated[newKey.Name] = newKey;
            updated[graceKey.Name] = graceKey;
            await SaveAsync(updated, ct);
            _keys = updated;
            return newKey;
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
                    .Select(spec => new AccessKey($"{WorkflowTokenPrefix}{Guid.NewGuid():N}", GenerateKey(), spec.Feeds, spec.Type, expires))
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
            RemoveGraceKeys(updated, name);
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
        if (_keys is null || DateTimeOffset.UtcNow - _loadedAt > CacheTtl)
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
        _loadedAt = DateTimeOffset.UtcNow;
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

    // Rotation grace keys and workflow tokens are both hidden from the UI and auto-pruned when expired.
    private static bool IsEphemeral(string name) =>
        name.StartsWith(EphemeralPrefix, StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(WorkflowTokenPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the key is a short-lived token issued to the runtime workflow by the token endpoint.</summary>
    public static bool IsWorkflowToken(AccessKey key) =>
        key.Name.StartsWith(WorkflowTokenPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when a name uses a reserved internal prefix (workflow tokens or rotation grace keys) and must not be user-assigned.</summary>
    public static bool IsReservedName(string name) => IsEphemeral(name);

    // Removes rotation grace keys derived from a key so revoking or deleting it also kills the retained
    // old credential (named "ephemeral|{name}" or, on collision, "ephemeral|{name}-{guid}").
    private static void RemoveGraceKeys(Dictionary<string, AccessKey> keys, string name)
    {
        foreach (var graceName in keys.Keys.Where(k => IsGraceKeyOf(k, name)).ToList())
        {
            keys.Remove(graceName);
        }
    }

    private static bool IsGraceKeyOf(string keyName, string baseName)
    {
        var prefix = EphemeralPrefix + baseName;
        if (!keyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        // Match only the exact grace name or the collision-avoidance "-{guid:N}" (32 hex) suffix, so a key
        // named "foo" never sweeps away the grace key of a different key named "foo-bar".
        var suffix = keyName[prefix.Length..];
        return suffix.Length == 0
            || (suffix.Length == 33 && suffix[0] == '-' && suffix[1..].All(Uri.IsHexDigit));
    }

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
