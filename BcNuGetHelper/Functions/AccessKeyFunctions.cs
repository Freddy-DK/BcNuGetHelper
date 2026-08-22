using System.Text.Json;
using BcNuGetHelper.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace BcNuGetHelper.Functions;

public class AccessKeyFunctions(AccessKeyStore store, AdminAuthenticator admin)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public record CreateAccessKeyRequest(string[]? Feeds);

    [Function("GetAccessKey")]
    public async Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "accesskeys/{name}")] HttpRequest req,
        string name,
        CancellationToken ct)
    {
        if (!await admin.IsAuthorizedAsync(req, ct))
        {
            return new UnauthorizedResult();
        }

        var key = await store.GetAsync(name, ct);
        return key is null ? new NotFoundResult() : new OkObjectResult(key);
    }

    [Function("CreateAccessKey")]
    public async Task<IActionResult> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "accesskeys/{name}")] HttpRequest req,
        string name,
        CancellationToken ct)
    {
        if (!await admin.IsAuthorizedAsync(req, ct))
        {
            return new UnauthorizedResult();
        }

        CreateAccessKeyRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<CreateAccessKeyRequest>(req.Body, JsonOptions, ct);
        }
        catch (JsonException)
        {
            return new BadRequestObjectResult("Invalid JSON body.");
        }

        var feeds = request?.Feeds ?? [];
        if (feeds.Length == 0 || feeds.Any(f => !PackageBuilder.Feeds.Contains(f, StringComparer.OrdinalIgnoreCase)))
        {
            return new BadRequestObjectResult(
                $"Body must specify \"feeds\" with any of: {string.Join(", ", PackageBuilder.Feeds)}.");
        }

        var normalizedFeeds = feeds.Select(f => f.ToLowerInvariant()).Distinct().ToArray();
        var key = await store.CreateAsync(name, normalizedFeeds, ct);
        return key is null
            ? new ConflictObjectResult($"Access key '{name}' already exists.")
            : new ObjectResult(key) { StatusCode = StatusCodes.Status201Created };
    }

    [Function("DeleteAccessKey")]
    public async Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "accesskeys/{name}")] HttpRequest req,
        string name,
        CancellationToken ct)
    {
        if (!await admin.IsAuthorizedAsync(req, ct))
        {
            return new UnauthorizedResult();
        }

        return await store.RemoveAsync(name, ct) ? new NoContentResult() : new NotFoundResult();
    }
}
