using System;
using System.Linq;
using Jellyfin.Plugin.Trailers4Jellyfin.Services;
using Xunit;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Tests.Services;

public sealed class TrailerVideoSelectorTests
{
    [Fact]
    public void Buddy_NormalTrailerBeatsNewerSingAlongAtSameResolution()
    {
        var singAlong = Video("sing", "Buddy The Sing-Along Version October 2", 1080, "Trailer",
            new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero), official: false, order: 1);
        var normal = Video("normal", "BUDDY Official Trailer (2026)", 1080, "Trailer",
            new DateTimeOffset(2026, 8, 18, 0, 0, 0, TimeSpan.Zero), official: false, order: 2);

        var ordered = TrailerVideoSelector.OrderCandidates(new[] { singAlong, normal });

        Assert.Equal("normal", ordered[0].Key);
        Assert.Equal(TrailerVideoVariantClass.FullTrailer, TrailerVideoSelector.GetVariantClass(normal));
        Assert.Equal(TrailerVideoVariantClass.EventOrNoveltyTrailer, TrailerVideoSelector.GetVariantClass(singAlong));
    }

    [Fact]
    public void FourKTeaserBeats1080FullTrailer()
    {
        var full = Video("full", "RAGE OF STARS | Official Trailer (2026)", 1080, "Trailer", DateTimeOffset.UtcNow);
        var teaser = Video("teaser", "Official Teaser", 2160, "Teaser", DateTimeOffset.UtcNow.AddYears(-2));

        Assert.Equal("teaser", TrailerVideoSelector.OrderCandidates(new[] { full, teaser })[0].Key);
        Assert.True(TrailerVideoSelector.GetStructuralScore(teaser) > TrailerVideoSelector.GetStructuralScore(full));
    }

    [Fact]
    public void FourKReReleaseIsFullTrailerAndBeats1080Normal()
    {
        var normal = Video("normal", "Official Trailer", 1080, "Trailer", DateTimeOffset.UtcNow);
        var reRelease = Video("rerelease", "30th Anniversary Re-Release Trailer", 2160, "Trailer", DateTimeOffset.UtcNow);

        Assert.Equal(TrailerVideoVariantClass.FullTrailer, TrailerVideoSelector.GetVariantClass(reRelease));
        Assert.Equal("rerelease", TrailerVideoSelector.OrderCandidates(new[] { normal, reRelease })[0].Key);
    }

    [Fact]
    public void SameResolutionAndClassUsesNewestPublishedDate()
    {
        var official = Video("official", "Official Trailer", 1080, "Trailer",
            new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));
        var final = Video("final", "Final Trailer", 1080, "Trailer",
            new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal("final", TrailerVideoSelector.OrderCandidates(new[] { official, final })[0].Key);
    }

    [Fact]
    public void MissingPublishedDateLosesSameScoreTieToKnownDate()
    {
        var missing = Video("missing", "Official Trailer", 1080, "Trailer", null, official: true, order: 1);
        var dated = Video("dated", "Final Trailer", 1080, "Trailer",
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), official: false, order: 2);

        Assert.Equal("dated", TrailerVideoSelector.OrderCandidates(new[] { missing, dated })[0].Key);
    }

    [Fact]
    public void VerticalTrailerIsEventNoveltyAtSameResolution()
    {
        var vertical = Video("vertical", "Official Vertical Trailer", 1080, "Trailer", DateTimeOffset.UtcNow.AddDays(1));
        var normal = Video("normal", "Official Trailer", 1080, "Trailer", DateTimeOffset.UtcNow);

        Assert.Equal(TrailerVideoVariantClass.EventOrNoveltyTrailer, TrailerVideoSelector.GetVariantClass(vertical));
        Assert.Equal("normal", TrailerVideoSelector.OrderCandidates(new[] { vertical, normal })[0].Key);
    }

    [Fact]
    public void FallbackClassBeatsResolutionButAnyGenuineTrailerBeatsFallback()
    {
        var specialLook1080 = Video("special", "Official Special Look", 1080, "Featurette", DateTimeOffset.UtcNow);
        var tvSpot4k = Video("spot", "Official TV Spot", 2160, "Trailer", DateTimeOffset.UtcNow);
        var ticket4k = Video("ticket", "Get Tickets Now", 2160, "Trailer", DateTimeOffset.UtcNow);
        var teaser480 = Video("teaser", "Official Teaser", 480, "Teaser", DateTimeOffset.UtcNow);

        var ordered = TrailerVideoSelector.OrderCandidates(new[] { ticket4k, tvSpot4k, specialLook1080, teaser480 });

        Assert.Equal(new[] { "teaser", "special", "spot", "ticket" }, ordered.Select(v => v.Key).ToArray());
    }

    [Fact]
    public void LegacyMetadataAlwaysGetsOneTimeRefresh()
    {
        var best = Video("best", "Official Trailer", 1080, "Trailer", DateTimeOffset.UtcNow);
        var decision = TrailerVideoSelector.SelectUpgrade(
            new TrailerMetadata { TmdbId = 42 },
            new[] { best },
            upgradeHigherStructuralScore: false,
            upgradeWhenNewerTrailerReleased: false);

        Assert.NotNull(decision);
        Assert.Equal("best", decision!.Video.Key);
    }

    [Fact]
    public void ExistingTrailerOnlyUpgradesForEnabledStructuralOrNewerRules()
    {
        var installedDate = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var installed = Video("installed", "Official Trailer", 1080, "Trailer", installedDate);
        var newerSameScore = Video("newer", "Final Trailer", 1080, "Trailer", installedDate.AddDays(7));
        var better = Video("better", "Official Teaser", 2160, "Teaser", installedDate.AddYears(-1));
        var metadata = Stored(installed);
        var ordered = TrailerVideoSelector.OrderCandidates(new[] { installed, newerSameScore, better });

        Assert.Null(TrailerVideoSelector.SelectUpgrade(metadata, ordered, false, false));
        Assert.Equal("better", TrailerVideoSelector.SelectUpgrade(metadata, ordered, true, false)!.Video.Key);
        Assert.Equal("newer", TrailerVideoSelector.SelectUpgrade(metadata, ordered, false, true)!.Video.Key);
    }

    [Fact]
    public void NewerLowerScoreNeverReplacesBetterInstalledTrailer()
    {
        var installed = Video("installed", "Official Trailer", 1080, "Trailer",
            new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));
        var newerEvent = Video("newer-event", "Vertical Trailer", 1080, "Trailer",
            new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Null(TrailerVideoSelector.SelectUpgrade(
            Stored(installed),
            TrailerVideoSelector.OrderCandidates(new[] { newerEvent }),
            true,
            true));
    }

    private static TrailerMetadata Stored(TmdbVideo video) => new()
    {
        YoutubeKey = video.Key,
        PublishedAt = video.PublishedAt,
        StructuralScore = TrailerVideoSelector.GetStructuralScore(video),
        SelectorVersion = TrailerVideoSelector.CurrentSelectorVersion,
    };

    private static TmdbVideo Video(
        string key,
        string name,
        int size,
        string type,
        DateTimeOffset? publishedAt,
        bool official = true,
        int order = 1) =>
        new(key, name, "en", official, size, type, publishedAt, order);
}
