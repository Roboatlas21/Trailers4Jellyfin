using System.IO;
using System.Xml.Serialization;
using Jellyfin.Plugin.Trailers4Jellyfin.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Tests.Services;

public sealed class ConfigurationDefaultsTests
{
    [Theory]
    [InlineData("", 500_000L)]
    [InlineData("0", 0L)]
    [InlineData("5000000", 5_000_000L)]
    public void MinimumBudget_PreservesSavedValuesAndDefaultsOlderConfigurations(string saved, long expected)
    {
        var field = saved.Length == 0 ? "" : $"<MinimumMovieBudget>{saved}</MinimumMovieBudget>";
        using var reader = new StringReader($"<PluginConfiguration>{field}</PluginConfiguration>");
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        var config = (PluginConfiguration)serializer.Deserialize(reader)!;
        Assert.Equal(expected, config.MinimumMovieBudget);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void UnwatchedDefaults_ApplyOnlyToMissingSerializedValues(string saved, bool expected)
    {
        var fields = new[] { "PreferUnwatchedTrailers", "PreferUnwatchedTrailerPreRolls", "PreferUnwatchedFeaturePreRolls", "PreferUnwatchedEpisodePreRolls" };
        var xml = "<PluginConfiguration>";
        if (saved.Length > 0)
            foreach (var field in fields) xml += $"<{field}>{saved}</{field}>";
        xml += "<YtDlpPath>/config/bin/yt-dlp</YtDlpPath></PluginConfiguration>";
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var reader = new StringReader(xml);
        var config = (PluginConfiguration)serializer.Deserialize(reader)!;
        foreach (var field in fields) Assert.Equal(expected, typeof(PluginConfiguration).GetProperty(field)!.GetValue(config));
        Assert.Equal("/config/bin/yt-dlp", config.YtDlpPath);
        Assert.True(config.SkipCurrentMovieTrailers);
        Assert.True(config.SkipWatchedMovieTrailers);
    }

    [Fact]
    public void TrailerDiscovery_DefaultsMatchRankedPoolProposal()
    {
        var config = new PluginConfiguration();

        Assert.True(config.SourceNowPlaying);
        Assert.True(config.SourceUpcoming);
        Assert.True(config.SourcePopular);
        Assert.True(config.SourceTopRated);
        Assert.Equal(12, config.ReleaseDateRangeMonths);
        Assert.Equal(6, config.UpcomingReleaseDateRangeMonths);
        Assert.Equal(30, config.InTheatresMinimumVotes);
        Assert.Equal(6.5, config.InTheatresMinimumRating);
        Assert.Equal(0, config.ComingSoonMinimumVotes);
        Assert.Equal(3.0, config.ComingSoonPopularityMultiplier);
        Assert.Equal(100, config.PopularMinimumVotes);
        Assert.Equal(6.0, config.PopularMinimumRating);
        Assert.Equal(500, config.TopRatedMinimumVotes);
        Assert.Equal(500_000L, config.MinimumMovieBudget);
        Assert.Equal(1_000L, config.BudgetMetadataFloor);
        Assert.Equal(45, config.MinimumMovieRuntimeMinutes);
        Assert.False(config.SkipMoviesInLibrary);
        Assert.True(config.SkipAlreadyDownloaded);
        Assert.Equal(100, config.MaxTotalTrailers);
        Assert.Equal(20, config.MaxTrailersToDownload);
        Assert.Equal(4, config.MaxPagesPerSource);
        Assert.Equal(14, config.EpisodeCommercialOnlyChancePercent);
        Assert.Equal(6, config.EpisodeMovieTrailerOnlyChancePercent);
        Assert.Equal(0, config.EpisodeBothChancePercent);
        Assert.Equal(TrailerPoolRankingMode.LifecycleScore, config.PoolRankingMode);
        Assert.Equal(TrailerPlaybackRankingMode.Score, config.PlaybackRankingMode);
        Assert.True(config.ApplyPlaybackReleaseBoost);
        Assert.Equal("US", config.TheatricalRegion);
    }
}
