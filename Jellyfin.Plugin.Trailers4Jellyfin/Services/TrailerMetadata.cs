using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Services;

public sealed record TrailerCertification(
    [property: JsonPropertyName("country")] string Country,
    [property: JsonPropertyName("rating")] string Rating);

internal sealed class TrailerMetadata
{
    [JsonPropertyName("tmdbId")]
    [JsonConverter(typeof(TmdbIdConverter))]
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

// An invalid movie identity must not discard otherwise usable trailer metadata.
internal sealed class TmdbIdConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, System.Type type, JsonSerializerOptions options)
    {
        int id;
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out id)) return id > 0 ? id : null;
        if (reader.TokenType == JsonTokenType.String
            && int.TryParse(reader.GetString()?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out id))
            return id > 0 ? id : null;
        reader.Skip();
        return null;
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value.HasValue) writer.WriteNumberValue(value.Value);
        else writer.WriteNullValue();
    }
}
