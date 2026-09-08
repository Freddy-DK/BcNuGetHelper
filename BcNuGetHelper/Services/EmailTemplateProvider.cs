using System.Net;
using System.Text.RegularExpressions;
using BcNuGetHelper.Models;

namespace BcNuGetHelper.Services;

/// <summary>An e-mail subject/body pair with <c>{{placeholder}}</c> tokens.</summary>
public record EmailTemplate(string Subject, string Body);

/// <summary>
/// Provides the notification e-mail templates from the bundled <c>email-templates</c> folder, one
/// HTML file per event (created, revoked, renewed, deleted). The subject is read from a leading
/// <c>&lt;!-- subject: ... --&gt;</c> comment; the rest is the HTML body. Only customer-facing
/// details are exposed (feeds, access, key, expiry) — the internal name and description are not.
/// </summary>
public partial class EmailTemplateProvider
{
    private static readonly string TemplatesDir = Path.Combine(AppContext.BaseDirectory, "email-templates");

    private readonly IReadOnlyDictionary<string, EmailTemplate> _templates = Load();

    /// <summary>Renders the template for an event, or null when no template exists for it.</summary>
    public (string Subject, string Body)? Render(string eventName, AccessKey key, IReadOnlyDictionary<string, string>? extra = null)
    {
        if (!_templates.TryGetValue(eventName, out var template))
        {
            return null;
        }

        // Subject is plain text; body is HTML, so its values are HTML-encoded.
        return (Apply(template.Subject, key, extra, encode: false), Apply(template.Body, key, extra, encode: true));
    }

    private static IReadOnlyDictionary<string, EmailTemplate> Load()
    {
        var result = new Dictionary<string, EmailTemplate>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(TemplatesDir))
        {
            return result;
        }

        foreach (var file in Directory.EnumerateFiles(TemplatesDir, "*.html"))
        {
            var eventName = Path.GetFileNameWithoutExtension(file);
            result[eventName] = SplitSubject(File.ReadAllText(file), eventName);
        }
        return result;
    }

    private static EmailTemplate SplitSubject(string content, string fallbackSubject)
    {
        var match = SubjectComment().Match(content);
        if (!match.Success)
        {
            return new EmailTemplate(fallbackSubject, content.Trim());
        }

        var subject = match.Groups[1].Value.Trim();
        var body = content.Remove(match.Index, match.Length).Trim();
        return new EmailTemplate(subject, body);
    }

    private static string Apply(string template, AccessKey key, IReadOnlyDictionary<string, string>? extra, bool encode)
    {
        string V(string? value) => encode ? WebUtility.HtmlEncode(value ?? "") : value ?? "";

        // Only customer-facing fields are exposed; the internal name and description are omitted.
        var result = template
            .Replace("{{feeds}}", V(string.Join(", ", key.Feeds)))
            .Replace("{{type}}", V(key.Type ?? "read"))
            .Replace("{{key}}", V(key.Key))
            .Replace("{{expires}}", V(key.Expires?.ToString("u") ?? "never"))
            .Replace("{{email}}", V(key.Email))
            .Replace("{{sender}}", V(Sender()));

        if (extra is not null)
        {
            foreach (var (token, value) in extra)
            {
                result = result.Replace("{{" + token + "}}", V(value));
            }
        }
        return result;
    }

    // Sign-off name: the optional Smtp__FromName, falling back to the Smtp__From address.
    private static string Sender() =>
        Environment.GetEnvironmentVariable("Smtp__FromName")
        ?? Environment.GetEnvironmentVariable("Smtp__From")
        ?? "";

    [GeneratedRegex(@"<!--\s*subject:\s*(.*?)\s*-->", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SubjectComment();
}
