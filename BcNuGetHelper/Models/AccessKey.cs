namespace BcNuGetHelper.Models;

/// <summary>An access key granting read access to one or more feeds (containers).</summary>
public record AccessKey(string Name, string Key, string[] Feeds);
