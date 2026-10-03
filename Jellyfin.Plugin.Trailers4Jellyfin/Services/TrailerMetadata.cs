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
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [JsonPropertyName("tmdbId")]
    [JsonConverter(typeof(TmdbIdConverter))]
    public int? TmdbId { get; set; }

    [JsonPropertyName("genres")]
    public string[]? Genres { get; set; }

    [JsonPropertyName("popularity")]
    public double? Popularity { get; set; }

    // Kept for legacy sidecars; a bare label has no reliable country until refreshed.
    [JsonPropertyName("officialRating")]
    public string? OfficialRating { get; set; }

    [JsonPropertyName("certifications")]
    public List<TrailerCertification>? Certifications { get; set; }

    [JsonPropertyName("youtubeKey")]
    public string? YoutubeKey { get; set; }

    [JsonPropertyName("videoName")]
    public string? VideoName { get; set; }

    [JsonPropertyName("videoType")]
    public string? VideoType { get; set; }

    [JsonPropertyName("videoOfficial")]
    public bool? VideoOfficial { get; set; }

    [JsonPropertyName("videoSize")]
    public int? VideoSize { get; set; }

    [JsonPropertyName("publishedAt")]
    public System.DateTimeOffset? PublishedAt { get; set; }

    [JsonPropertyName("variantClass")]
    public string? VariantClass { get; set; }

    [JsonPropertyName("structuralScore")]
    public int? StructuralScore { get; set; }

    [JsonPropertyName("selectorVersion")]
    public int SelectorVersion { get; set; }

    // Metadata upgrades preserve fields added by other versions.
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
