using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Services;

internal enum TrailerVideoVariantClass
{
    GenericFallback = 0,
    TicketOrHomeReleaseAd = 1,
    Featurette = 2,
    Clip = 3,
    SneakPeekOrFirstLook = 4,
    TvSpot = 5,
    SpecialLook = 6,
    EventOrNoveltyTrailer = 10,
    Teaser = 11,
    AlternateTrailer = 12,
    FullTrailer = 13,
}

internal sealed record TrailerUpgradeDecision(TmdbVideo Video, string Reason);

internal static class TrailerVideoSelector
{
    internal const int CurrentSelectorVersion = 2;

    internal static IReadOnlyList<TmdbVideo> OrderCandidates(IEnumerable<TmdbVideo> videos)
    {
        return videos
            .OrderByDescending(GetStructuralScore)
            .ThenByDescending(v => v.PublishedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(v => v.Official)
            .ThenBy(v => v.ReturnOrder)
            .ThenBy(v => v.Key, StringComparer.Ordinal)
            .ToList();
    }

    internal static TrailerVideoVariantClass GetVariantClass(TmdbVideo video)
    {
        var title = Normalize(video.Name);
        var type = Normalize(video.Type);

        // Fallback wording overrides TMDB's broad "Trailer" label.
        if (Matches(title, @"\bspecial\s+look\b|\bexclusive\s+look\b"))
            return TrailerVideoVariantClass.SpecialLook;

        if (Matches(title, @"\btv\s+spot\b|\btelevision\s+spot\b|\b(?:15|30|60)\s+second\s+spot\b|\bbig\s+game\s+spot\b|\bcountdown\b"))
            return TrailerVideoVariantClass.TvSpot;

        if (Matches(title, @"\bsneak\s+(?:peek|peak)\b|\bfirst\s+look\b"))
            return TrailerVideoVariantClass.SneakPeekOrFirstLook;

        if (Matches(title, @"\bclip\b") || string.Equals(type, "clip", StringComparison.Ordinal))
            return TrailerVideoVariantClass.Clip;

        if (Matches(title, @"\bfeaturette\b") || string.Equals(type, "featurette", StringComparison.Ordinal))
            return TrailerVideoVariantClass.Featurette;

        if (Matches(
                title,
                @"\b(?:get|buy|book)\s+tickets?\b|\btickets?\s+(?:on\s+sale|available|now)\b|\bwatch\s+at\s+home\b|\b(?:now|available)\s+on\s+digital\b|\bbuy\s+or\s+rent\b|\brent\s+or\s+buy\b"))
        {
            return TrailerVideoVariantClass.TicketOrHomeReleaseAd;
        }

        // Genuine trailer material. Resolution is intentionally stronger than class:
        // a 2160p teaser/event variant can beat a 1080p full trailer.
        if (Matches(title, @"\bsing\s+along\b|\bspecial\s+event\b|\bencore\b|\bvertical\s+trailer\b"))
            return TrailerVideoVariantClass.EventOrNoveltyTrailer;

        if (Matches(title, @"\bteaser\b") || string.Equals(type, "teaser", StringComparison.Ordinal))
            return TrailerVideoVariantClass.Teaser;

        if (Matches(
                title,
                @"\binternational\s+trailer\b|\bu\.?\s*k\.?\s+trailer\b|\bred\s+band\s+trailer\b|\bsubtitled\s+trailer\b|\btrailer\s+\[?eng\s+sub\b"))
        {
            return TrailerVideoVariantClass.AlternateTrailer;
        }

        // IMAX, re-release, anniversary, restored/remastered, final/new/numbered
        // and other ordinary trailer variants all intentionally remain FullTrailer.
        if (Matches(title, @"\btrailer\b") || string.Equals(type, "trailer", StringComparison.Ordinal))
            return TrailerVideoVariantClass.FullTrailer;

        return TrailerVideoVariantClass.GenericFallback;
    }

    internal static int GetStructuralScore(TmdbVideo video)
    {
        var variant = GetVariantClass(video);
        var resolutionRank = GetResolutionRank(video.Size);

        return variant switch
        {
            TrailerVideoVariantClass.FullTrailer => 1000 + (resolutionRank * 100) + 75,
            TrailerVideoVariantClass.AlternateTrailer => 1000 + (resolutionRank * 100) + 50,
            TrailerVideoVariantClass.Teaser => 1000 + (resolutionRank * 100) + 25,
            TrailerVideoVariantClass.EventOrNoveltyTrailer => 1000 + (resolutionRank * 100),
            TrailerVideoVariantClass.SpecialLook => 600 + (resolutionRank * 10),
            TrailerVideoVariantClass.TvSpot => 500 + (resolutionRank * 10),
            TrailerVideoVariantClass.SneakPeekOrFirstLook => 400 + (resolutionRank * 10),
            TrailerVideoVariantClass.Clip => 300 + (resolutionRank * 10),
            TrailerVideoVariantClass.Featurette => 200 + (resolutionRank * 10),
            TrailerVideoVariantClass.TicketOrHomeReleaseAd => 100 + (resolutionRank * 10),
            _ => resolutionRank * 10,
        };
    }

    internal static TrailerUpgradeDecision? SelectUpgrade(
        TrailerMetadata? existing,
        IReadOnlyList<TmdbVideo> orderedCandidates,
        bool upgradeHigherStructuralScore,
        bool upgradeWhenNewerTrailerReleased)
    {
        if (orderedCandidates.Count == 0)
            return null;

        // Sidecars created before selector v2 cannot identify the installed YouTube
        // variant. Refresh them once so future comparisons are deterministic.
        if (existing == null
            || existing.SelectorVersion < CurrentSelectorVersion
            || string.IsNullOrWhiteSpace(existing.YoutubeKey)
            || !existing.StructuralScore.HasValue)
        {
            return new TrailerUpgradeDecision(
                orderedCandidates[0],
                "Refreshing legacy trailer selection metadata");
        }

        var installedKey = existing.YoutubeKey;
        var installedScore = existing.StructuralScore.Value;

        if (upgradeHigherStructuralScore)
        {
            var structurallyBetter = orderedCandidates.FirstOrDefault(v =>
                !string.Equals(v.Key, installedKey, StringComparison.Ordinal)
                && GetStructuralScore(v) > installedScore);

            if (structurallyBetter != null)
            {
                return new TrailerUpgradeDecision(
                    structurallyBetter,
                    $"Upgrading structural score {installedScore} → {GetStructuralScore(structurallyBetter)}");
            }
        }

        if (upgradeWhenNewerTrailerReleased && existing.PublishedAt.HasValue)
        {
            var newerSameScore = orderedCandidates.FirstOrDefault(v =>
                !string.Equals(v.Key, installedKey, StringComparison.Ordinal)
                && GetStructuralScore(v) == installedScore
                && v.PublishedAt.HasValue
                && v.PublishedAt.Value > existing.PublishedAt.Value);

            if (newerSameScore != null)
            {
                return new TrailerUpgradeDecision(
                    newerSameScore,
                    $"Upgrading to newer same-score trailer ({existing.PublishedAt:O} → {newerSameScore.PublishedAt:O})");
            }
        }

        return null;
    }

    private static int GetResolutionRank(int size) => size switch
    {
        >= 2160 => 4,
        >= 1080 => 3,
        >= 720 => 2,
        >= 480 => 1,
        _ => 0,
    };

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = Regex.Replace(
            value.ToLowerInvariant(),
            @"[\p{Pd}_/|]+",
            " ",
            RegexOptions.CultureInvariant);
        return Regex.Replace(normalized, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
    }

    private static bool Matches(string value, string pattern) =>
        Regex.IsMatch(
            value,
            pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
