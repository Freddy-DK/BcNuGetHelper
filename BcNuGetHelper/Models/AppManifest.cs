namespace BcNuGetHelper.Models;

public record AppDependency(Guid Id, string Name, string Publisher, string MinVersion);

public record AppManifest(
    Guid Id,
    string Name,
    string Publisher,
    string Version,
    string Description,
    IReadOnlyList<AppDependency> Dependencies)
{
    /// <summary>NuGet package id following the BcContainerHelper convention: publisher.name.appId.</summary>
    public string PackageId
    {
        get
        {
            var publisher = Sanitize(Publisher);
            var name = Sanitize(Name);
            var id = Id.ToString("D").ToLowerInvariant();

            // NuGet package ids are limited to 100 characters; keep publisher and app id intact
            var maxNameLength = 100 - publisher.Length - id.Length - 2;
            if (name.Length > maxNameLength)
            {
                name = name[..Math.Max(maxNameLength, 1)];
            }
            return $"{publisher}.{name}.{id}";
        }
    }

    public static string Sanitize(string value)
    {
        var chars = value.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray();
        var result = new string(chars);
        return result.Length == 0 ? "x" : result;
    }
}
