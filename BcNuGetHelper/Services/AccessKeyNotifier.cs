using BcNuGetHelper.Models;
using Microsoft.Extensions.Logging;

namespace BcNuGetHelper.Services;

/// <summary>
/// Sends access-key change notifications to the key's contact address. A no-op when SMTP is not
/// configured or the key has no e-mail. E-mail failures are logged, never thrown, so they don't
/// fail the originating API call.
/// </summary>
public class AccessKeyNotifier(SmtpEmailSender sender, EmailTemplateProvider templates, ILogger<AccessKeyNotifier> logger)
{
    public Task NotifyAsync(string eventName, AccessKey key, CancellationToken ct) =>
        NotifyAsync(eventName, key, null, ct);

    public async Task NotifyAsync(string eventName, AccessKey key, IReadOnlyDictionary<string, string>? extra, CancellationToken ct)
    {
        if (!sender.IsConfigured)
        {
            logger.LogWarning(
                "SMTP is not fully configured ({Missing}); skipping '{Event}' notification for key {Name}",
                sender.MissingSettings(), eventName, key.Name);
            return;
        }
        if (string.IsNullOrWhiteSpace(key.Email))
        {
            logger.LogWarning("Key {Name} has no contact e-mail; skipping '{Event}' notification", key.Name, eventName);
            return;
        }

        var rendered = templates.Render(eventName, key, extra);
        if (rendered is null)
        {
            logger.LogWarning("No e-mail template configured for event '{Event}'", eventName);
            return;
        }

        try
        {
            await sender.SendAsync(key.Email!, rendered.Value.Subject, rendered.Value.Body, ct);
            logger.LogInformation("Sent '{Event}' notification for key {Name} to {Email}", eventName, key.Name, key.Email);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send '{Event}' notification for key {Name} to {Email}: {Error}",
                eventName, key.Name, key.Email, (ex.InnerException ?? ex).Message);
        }
    }
}
