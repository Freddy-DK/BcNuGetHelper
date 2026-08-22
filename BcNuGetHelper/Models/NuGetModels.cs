using System.Text.Json.Serialization;

namespace BcNuGetHelper.Models;

public record ServiceIndexResource(
    [property: JsonPropertyName("@id")] string Id,
    [property: JsonPropertyName("@type")] string Type);

public record ServiceIndex(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("resources")] IReadOnlyList<ServiceIndexResource> Resources);

public record SearchResultVersion(
    [property: JsonPropertyName("@id")] string Id,
    [property: JsonPropertyName("version")] string Version);

public record SearchResult(
    [property: JsonPropertyName("@id")] string RegistrationId,
    [property: JsonPropertyName("@type")] string Type,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("versions")] IReadOnlyList<SearchResultVersion> Versions);

public record SearchResponse(
    [property: JsonPropertyName("totalHits")] int TotalHits,
    [property: JsonPropertyName("data")] IReadOnlyList<SearchResult> Data);

public record PackageVersionIndex(
    [property: JsonPropertyName("versions")] IReadOnlyList<string> Versions);
