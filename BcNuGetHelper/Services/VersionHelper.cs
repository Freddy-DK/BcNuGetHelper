namespace BcNuGetHelper.Services;

public static class VersionHelper
{
    /// <summary>Normalizes a version the way NuGet does (drops a trailing ".0" revision, pads to three parts).</summary>
    public static string Normalize(string version)
    {
        if (!Version.TryParse(version, out var v))
        {
            return version.ToLowerInvariant();
        }
        var normalized = new Version(
            Math.Max(v.Major, 0),
            Math.Max(v.Minor, 0),
            Math.Max(v.Build, 0),
            Math.Max(v.Revision, 0));
        return normalized.Revision > 0
            ? normalized.ToString(4)
            : normalized.ToString(3);
    }

    public static bool AreEqual(string a, string b) =>
        Normalize(a).Equals(Normalize(b), StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<string> Sort(IEnumerable<string> versions) =>
        versions.OrderBy(v => Version.TryParse(v, out var parsed) ? parsed : new Version(0, 0));
}
