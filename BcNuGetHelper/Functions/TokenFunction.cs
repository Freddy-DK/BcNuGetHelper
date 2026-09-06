using BcNuGetHelper.Models;
using BcNuGetHelper.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace BcNuGetHelper.Functions;

/// <summary>
/// Issues short-lived feed tokens to the runtime-generation workflow, which authenticates
/// with a Microsoft Entra token via GitHub OIDC (validated like the other admin endpoints).
/// Returns a read token for the apps feed and a read/write token for the runtime feed, so
/// no long-lived credential has to be passed when the workflow is dispatched.
/// </summary>
public class TokenFunction(AccessKeyStore store, AdminAuthenticator admin)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(6);

    public record TokenResponse(string AppsToken, string RuntimeToken, DateTimeOffset Expires);

    [Function("IssueToken")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "token")] HttpRequest req,
        CancellationToken ct)
    {
        if (!await admin.IsAuthorizedAsync(req, ct))
        {
            return new UnauthorizedResult();
        }

        var keys = await store.CreateEphemeralAsync(
            [([PackageBuilder.FeedApps], AccessKeyTypes.Read), ([PackageBuilder.FeedRuntime], AccessKeyTypes.ReadWrite)],
            Lifetime,
            ct);
        return new OkObjectResult(new TokenResponse(keys[0].Key, keys[1].Key, keys[0].Expires!.Value));
    }
}
