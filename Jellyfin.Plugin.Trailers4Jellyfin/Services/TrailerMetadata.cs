using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Services;

public sealed record TrailerCertification(
    [property: JsonPropertyName("country")] string Country,
    [property: JsonPropertyName("rating")] string Rating);

internal sealed class TrailerMetadata
{
    [JsonPropertyName("tmdbId")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int? TmdbId { get; set; }

    [JsonPropertyName("genres")]
    public string[]? Genres { get; set; }

    // Kept for legacy sidecars; a bare label has no reliable country until refreshed.
    [JsonPropertyName("officialRating")]
    public string? OfficialRating { get; set; }

    [JsonPropertyName("certifications")]
    public List<TrailerCertification>? Certifications { get; set; }

    // Metadata upgrades preserve titles, years and any fields added by other versions.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}
