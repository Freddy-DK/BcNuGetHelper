using System.Text.Json;
using BcNuGetHelper.Models;
using BcNuGetHelper.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace BcNuGetHelper.Functions;

public class AccessKeyFunctions(AccessKeyStore store, AdminAuthenticator admin, GitHubAuthenticator github)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const int MaxNameLength = 64;
    private const int MaxDescriptionLength = 200;
    private const int MaxEmailLength = 200;
    private static readonly System.Text.RegularExpressions.Regex NamePattern =
        new("^[A-Za-z0-9._-]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public record CreateAccessKeyRequest(string[]? Feeds, string? Type, string? Description, string? Email, int? ExpiresInDays);

    public record RenewRequest(int? ExpiresInDays);

    private async Task<bool> AuthorizedAsync(HttpRequest req, CancellationToken ct) =>
        await admin.IsAuthorizedAsync(req, ct) || await github.IsAuthorizedAsync(req, ct);

    [Function("ListAccessKeys")]
    public async Task<IActionResult> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "accesskeys")] HttpRequest req,
        CancellationToken ct)
    {
        if (!await AuthorizedAsync(req, ct))
        {
            return new UnauthorizedResult();
        }

        return new OkObjectResult(await store.ListAsync(ct));
    }

    [Function("GetAccessKey")]
    public async Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "accesskeys/{name}")] HttpRequest req,
        string name,
        CancellationToken ct)
    {
        if (!await AuthorizedAsync(req, ct))
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
        if (!await AuthorizedAsync(req, ct))
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

        if (name.Length > MaxNameLength || !NamePattern.IsMatch(name))
        {
            return new BadRequestObjectResult(
                $"Name must be 1-{MaxNameLength} characters using letters, digits, '.', '-' or '_'.");
        }

        var feeds = request?.Feeds ?? [];
        if (feeds.Length == 0 || feeds.Any(f => !PackageBuilder.Feeds.Contains(f, StringComparer.OrdinalIgnoreCase)))
        {
            return new BadRequestObjectResult(
                $"Body must specify \"feeds\" with any of: {string.Join(", ", PackageBuilder.Feeds)}.");
        }

        // Defaults to a read-only key; write/readwrite keys are allowed to push packages.
        var type = string.IsNullOrEmpty(request?.Type) ? AccessKeyTypes.Read : request.Type.ToLowerInvariant();
        if (!AccessKeyTypes.All.Contains(type))
        {
            return new BadRequestObjectResult(
                $"\"type\" must be one of: {string.Join(", ", AccessKeyTypes.All)}.");
        }

        var description = string.IsNullOrWhiteSpace(request?.Description) ? null : request.Description.Trim();
        if (description is { Length: > MaxDescriptionLength })
        {
            return new BadRequestObjectResult($"Description must be at most {MaxDescriptionLength} characters.");
        }

        // Email is required so the recipient can be notified when the key changes.
        var email = request?.Email?.Trim();
        if (string.IsNullOrEmpty(email))
        {
            return new BadRequestObjectResult("\"email\" is required.");
        }
        if (email.Length > MaxEmailLength || !IsValidEmail(email))
        {
            return new BadRequestObjectResult($"\"email\" must be a valid e-mail address of at most {MaxEmailLength} characters.");
        }

        if (request?.ExpiresInDays is <= 0)
        {
            return new BadRequestObjectResult("\"expiresInDays\" must be a positive number of days.");
        }
        var expires = request?.ExpiresInDays is { } days
            ? DateTimeOffset.UtcNow.AddDays(days)
            : (DateTimeOffset?)null;

        var normalizedFeeds = feeds.Select(f => f.ToLowerInvariant()).Distinct().ToArray();
        var key = await store.CreateAsync(name, normalizedFeeds, type, description, email, expires, ct);
        return key is null
            ? new ConflictObjectResult($"Access key '{name}' already exists.")
            : new ObjectResult(key) { StatusCode = StatusCodes.Status201Created };
    }

    [Function("RevokeAccessKey")]
    public async Task<IActionResult> Revoke(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "accesskeys/{name}/revoke")] HttpRequest req,
        string name,
        CancellationToken ct)
    {
        if (!await AuthorizedAsync(req, ct))
        {
            return new UnauthorizedResult();
        }

        // Revoking expires the key immediately; the record is kept for auditing and can be renewed.
        var key = await store.SetExpiryAsync(name, DateTimeOffset.UtcNow, ct);
        return key is null ? new NotFoundResult() : new OkObjectResult(key);
    }

    [Function("RenewAccessKey")]
    public async Task<IActionResult> Renew(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "accesskeys/{name}/renew")] HttpRequest req,
        string name,
        CancellationToken ct)
    {
        if (!await AuthorizedAsync(req, ct))
        {
            return new UnauthorizedResult();
        }

        RenewRequest? request = null;
        if (req.ContentLength is > 0)
        {
            try
            {
                request = await JsonSerializer.DeserializeAsync<RenewRequest>(req.Body, JsonOptions, ct);
            }
            catch (JsonException)
            {
                return new BadRequestObjectResult("Invalid JSON body.");
            }
        }

        if (request?.ExpiresInDays is <= 0)
        {
            return new BadRequestObjectResult("\"expiresInDays\" must be a positive number of days.");
        }

        // No expiry given means the key becomes permanent again.
        var expires = request?.ExpiresInDays is { } days
            ? DateTimeOffset.UtcNow.AddDays(days)
            : (DateTimeOffset?)null;
        var key = await store.SetExpiryAsync(name, expires, ct);
        return key is null ? new NotFoundResult() : new OkObjectResult(key);
    }

    [Function("DeleteAccessKey")]
    public async Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "accesskeys/{name}")] HttpRequest req,
        string name,
        CancellationToken ct)
    {
        if (!await AuthorizedAsync(req, ct))
        {
            return new UnauthorizedResult();
        }

        return await store.RemoveAsync(name, ct) ? new NoContentResult() : new NotFoundResult();
    }

    private static bool IsValidEmail(string email) =>
        System.Net.Mail.MailAddress.TryCreate(email, out _);
}
