using System;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Services;

public sealed class TrailerRatingPolicy
{
    private static readonly string[] FallbackCountries = { "US", "CA", "GB", "AU" };
    private readonly ILocalizationManager _localization;
    private readonly ILibraryManager _library;
    private readonly IServerConfigurationManager _server;

    public TrailerRatingPolicy(ILocalizationManager localization, ILibraryManager library, IServerConfigurationManager server)
    {
        _localization = localization;
        _library = library;
        _server = server;
    }

    internal string GetMetadataCountry(BaseItem feature)
    {
        var country = _library.GetLibraryOptions(feature).MetadataCountryCode;
        return string.IsNullOrWhiteSpace(country) ? _server.Configuration.MetadataCountryCode : country;
    }

    internal ParentalRatingScore? GetFeatureRating(BaseItem feature, string country)
    {
        var rating = string.IsNullOrWhiteSpace(feature.CustomRating) ? feature.OfficialRating : feature.CustomRating;
        return string.IsNullOrWhiteSpace(rating) ? null : _localization.GetRatingScore(rating, country);
    }

    internal ParentalRatingScore? ResolveRating(TrailerMetadata metadata, string preferredCountry)
    {
        var certifications = metadata.Certifications;
        if (certifications == null)
        {
            // Legacy bare ratings lost their country. Do not reinterpret them using the viewer's locale.
            var legacy = metadata.OfficialRating;
            return !string.IsNullOrWhiteSpace(legacy) && legacy.Length > 3
                && char.IsAsciiLetter(legacy[0]) && char.IsAsciiLetter(legacy[1])
                && (legacy[2] == ':' || legacy[2] == '-')
                    ? _localization.GetRatingScore(legacy, legacy[..2]) : null;
        }

        var countries = new[] { preferredCountry }.Concat(FallbackCountries)
            .Concat(certifications.Where(c => c != null).Select(c => c.Country).OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var country in countries)
        {
            foreach (var certification in certifications.Where(c => c != null && string.Equals(c.Country, country, StringComparison.OrdinalIgnoreCase)))
            {
                if (string.IsNullOrWhiteSpace(certification.Rating)) continue;
                // Qualify the label as well as passing the country: Jellyfin's unqualified lookup can
                // otherwise search unrelated rating systems when this country does not recognize it.
                var score = _localization.GetRatingScore($"{country}:{certification.Rating}", country);
                if (score != null) return score;
            }
        }

        return null;
    }

    internal bool IsAllowed(TrailerMetadata metadata, string country, ParentalRatingScore? featureRating, User user)
    {
        var rating = ResolveRating(metadata, country);
        if (rating == null)
            return !user.GetPreferenceValues<UnratedItem>(PreferenceKind.BlockUnratedItems).Contains(UnratedItem.Trailer);

        if (user.MaxParentalRatingScore is int maximum
            && Exceeds(rating, maximum, user.MaxParentalRatingSubScore))
            return false;

        return featureRating == null || !Exceeds(rating, featureRating.Score, featureRating.SubScore ?? 0);
    }

    private static bool Exceeds(ParentalRatingScore rating, int score, int? subScore) =>
        rating.Score > score || (rating.Score == score && subScore.HasValue && (rating.SubScore ?? 0) > subScore.Value);
}
