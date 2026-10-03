using System;
using Jellyfin.Plugin.Trailers4Jellyfin.Configuration;
using Jellyfin.Plugin.Trailers4Jellyfin.Services;
using Xunit;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Tests.Services;

public sealed class TrailerRankingServiceTests
{
    private static readonly DateOnly Today = new(2026, 10, 31);

    [Fact]
    public void LifecycleWindow_UsesConfiguredCalendarMonthBoundaries()
    {
        var config = new PluginConfiguration
        {
            ReleaseDateRangeMonths = 6,
            UpcomingReleaseDateRangeMonths = 6,
        };

        var oldestAllowed = Today.AddMonths(-6);
        var latestAllowed = Today.AddMonths(6);

        Assert.Null(TrailerRankingService.GetLifecycleWindowExclusionReason(oldestAllowed, config, Today));
        Assert.NotNull(TrailerRankingService.GetLifecycleWindowExclusionReason(oldestAllowed.AddDays(-1), config, Today));
        Assert.Null(TrailerRankingService.GetLifecycleWindowExclusionReason(latestAllowed, config, Today));
        Assert.NotNull(TrailerRankingService.GetLifecycleWindowExclusionReason(latestAllowed.AddDays(1), config, Today));
    }

    [Fact]
    public void LifecycleWindow_ZeroMonthsDisablesPastAndFutureLimits()
    {
        var config = new PluginConfiguration
        {
            ReleaseDateRangeMonths = 0,
            UpcomingReleaseDateRangeMonths = 0,
        };

        Assert.Null(TrailerRankingService.GetLifecycleWindowExclusionReason(Today.AddYears(-10), config, Today));
        Assert.Null(TrailerRankingService.GetLifecycleWindowExclusionReason(Today.AddYears(10), config, Today));
    }

    [Fact]
    public void Evaluate_ExtendedReleasedWindowAllowsMovieBeyondFormer365DayLimit()
    {
        var service = new TrailerRankingService();
        var releaseDate = Today.AddMonths(-18);
        var candidate = Candidate(releaseDate, TmdbMovieSource.Popular);
        var config = StrongDefaults();
        config.ReleaseDateRangeMonths = 24;

        var extended = service.Evaluate(candidate, config, Today);
        Assert.True(extended.LifecyclePoolEligible, extended.ExclusionReason);

        config.ReleaseDateRangeMonths = 12;
        var restricted = service.Evaluate(candidate, config, Today);
        Assert.False(restricted.LifecyclePoolEligible);
        Assert.Contains("12-month released window", restricted.ExclusionReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_ExtendedUpcomingWindowAllowsMovieBeyondFormer180DayLimit()
    {
        var service = new TrailerRankingService();
        var releaseDate = Today.AddMonths(9);
        var candidate = Candidate(releaseDate, TmdbMovieSource.ComingSoon);
        var config = StrongDefaults();
        config.UpcomingReleaseDateRangeMonths = 12;

        var extended = service.Evaluate(candidate, config, Today);
        Assert.True(extended.LifecyclePoolEligible, extended.ExclusionReason);

        config.UpcomingReleaseDateRangeMonths = 6;
        var restricted = service.Evaluate(candidate, config, Today);
        Assert.False(restricted.LifecyclePoolEligible);
        Assert.Contains("6-month upcoming window", restricted.ExclusionReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_MatureReleasedThresholdsUseConfiguredSettings()
    {
        var service = new TrailerRankingService();
        var releaseDate = Today.AddDays(-180);
        var candidate = Candidate(
            releaseDate,
            TmdbMovieSource.Popular,
            popularity: 500.0,
            voteCount: 499,
            rating: 6.7);
        var config = StrongDefaults();

        var calibrated = service.Evaluate(candidate, config, Today);
        Assert.False(calibrated.LifecyclePoolEligible);
        Assert.Contains("released rating", calibrated.ExclusionReason, StringComparison.Ordinal);
        Assert.Contains("released votes", calibrated.ExclusionReason, StringComparison.Ordinal);

        config.MatureReleasedMinimumRating = 6.7;
        config.MatureReleasedMinimumVotes = 499;

        var relaxed = service.Evaluate(candidate, config, Today);
        Assert.True(relaxed.LifecyclePoolEligible, relaxed.ExclusionReason);
    }

    [Fact]
    public void Evaluate_ProvenQualityThresholdsUseConfiguredSettings()
    {
        var service = new TrailerRankingService();
        var releaseDate = Today.AddDays(-240);
        var config = StrongDefaults();
        config.MatureReleasedMinimumRating = 6.5;

        var ratingCandidate = Candidate(
            releaseDate,
            TmdbMovieSource.Popular,
            popularity: 500.0,
            voteCount: 1_000,
            rating: 6.7);

        Assert.Equal(0.0, service.Evaluate(ratingCandidate, config, Today).ProvenQualityFloor);
        config.ProvenQualityMinimumRating = 6.7;
        Assert.True(service.Evaluate(ratingCandidate, config, Today).ProvenQualityFloor > 0.0);

        var votesCandidate = Candidate(
            releaseDate,
            TmdbMovieSource.Popular,
            popularity: 500.0,
            voteCount: 999,
            rating: 7.5);

        config.ProvenQualityMinimumRating = 6.8;
        Assert.Equal(0.0, service.Evaluate(votesCandidate, config, Today).ProvenQualityFloor);
        config.ProvenQualityMinimumVotes = 999;
        Assert.True(service.Evaluate(votesCandidate, config, Today).ProvenQualityFloor > 0.0);
    }

    [Theory]
    [InlineData(-120, 0.0)]
    [InlineData(-90, 2.0)]
    [InlineData(-60, 8.0)]
    [InlineData(-30, 14.0)]
    [InlineData(-14, 18.0)]
    [InlineData(0, 20.0)]
    [InlineData(3, 14.0)]
    [InlineData(7, 10.0)]
    [InlineData(14, 6.0)]
    [InlineData(30, 2.0)]
    [InlineData(45, 0.0)]
    public void PlaybackBoost_RegressionAnchorsRemainLocked(int daysFromRelease, double expected)
    {
        Assert.Equal(expected, TrailerRankingService.CalculatePlaybackBoost(daysFromRelease), 6);
    }

    private static PluginConfiguration StrongDefaults() => new()
    {
        MinimumMovieBudget = 500_000,
        BudgetMetadataFloor = 1_000,
        MinimumMovieRuntimeMinutes = 45,
        InTheatresMinimumVotes = 30,
        InTheatresMinimumRating = 6.5,
        ReleaseDateRangeMonths = 12,
        UpcomingReleaseDateRangeMonths = 6,
        MatureReleasedMinimumRating = 6.8,
        MatureReleasedMinimumVotes = 500,
        ProvenQualityMinimumRating = 6.8,
        ProvenQualityMinimumVotes = 1_000,
    };

    private static TrailerRankingCandidate Candidate(
        DateOnly releaseDate,
        TmdbMovieSource source,
        double popularity = 500.0,
        int voteCount = 10_000,
        double rating = 9.0)
    {
        var movie = new TmdbMovieResult(
            999,
            "Regression Movie",
            releaseDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            new[] { 28 },
            popularity,
            voteCount,
            rating,
            source);

        return new TrailerRankingCandidate(
            movie,
            releaseDate,
            null,
            releaseDate,
            100_000_000,
            120,
            true);
    }
}
