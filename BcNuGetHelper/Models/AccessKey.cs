namespace BcNuGetHelper.Models;

/// <summary>An access key granting read and/or write access to one or more feeds (containers).</summary>
public record AccessKey(
    string Name,
    string Key,
    string[] Feeds,
    string? Type = null,
    DateTimeOffset? Expires = null,
    string? Description = null,
    string? Email = null)
{
    /// <summary>True when the key may read its feeds (type "read" or "readwrite"; the default when unset).</summary>
    public bool CanRead =>
        string.IsNullOrEmpty(Type)
        || string.Equals(Type, AccessKeyTypes.Read, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Type, AccessKeyTypes.ReadWrite, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the key may push packages to its feeds (type "write" or "readwrite").</summary>
    public bool CanWrite =>
        string.Equals(Type, AccessKeyTypes.Write, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Type, AccessKeyTypes.ReadWrite, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the key has an expiry that has passed.</summary>
    public bool IsExpired => Expires is { } expires && expires <= DateTimeOffset.UtcNow;
}

public static class AccessKeyTypes
{
    public const string Read = "read";
    public const string Write = "write";
    public const string ReadWrite = "readwrite";

    public static readonly string[] All = [Read, Write, ReadWrite];
}
