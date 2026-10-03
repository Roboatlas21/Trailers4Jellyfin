using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Trailers4Jellyfin.Configuration;
using Jellyfin.Plugin.Trailers4Jellyfin.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Tests.Services;

public sealed class EpisodePrerollCoordinatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "t4j-policy-" + Guid.NewGuid());
    private readonly Guid _user = Guid.NewGuid();
    private readonly Guid _asset = Guid.NewGuid();
    private readonly TestClock _clock = new();
    private readonly RollSequence _random = new();
    private readonly EpisodePrerollStateStore _store;
    private readonly EpisodePrerollCoordinator _coordinator;
    private readonly PluginConfiguration _config = new();
    private CancellationToken Token => TestContext.Current.CancellationToken;

    public EpisodePrerollCoordinatorTests()
    {
        _store = new EpisodePrerollStateStore(Path.Combine(_directory, "history.json"), NullLogger<EpisodePrerollStateStore>.Instance);
        _coordinator = NewCoordinator();
    }

    [Fact]
    public void Defaults_UseRequestedFrequency()
    {
        Assert.Equal(14, _config.EpisodeCommercialOnlyChancePercent);
        Assert.Equal(6, _config.EpisodeMovieTrailerOnlyChancePercent);
        Assert.Equal(0, _config.EpisodeBothChancePercent);
        Assert.Equal(60, _config.EpisodePreRollCooldownMinutes);
        Assert.Equal(3, _config.EpisodePreRollMinEpisodes);
        Assert.Equal(2, _config.EpisodePreRollMaxPerWindow);
        Assert.Equal(4, _config.EpisodePreRollWindowHours);
    }

    [Theory]
    [InlineData(0, EpisodePrerollOutcome.CommercialOnly)]
    [InlineData(13, EpisodePrerollOutcome.CommercialOnly)]
    [InlineData(14, EpisodePrerollOutcome.MovieTrailerOnly)]
    [InlineData(19, EpisodePrerollOutcome.MovieTrailerOnly)]
    [InlineData(20, EpisodePrerollOutcome.None)]
    [InlineData(99, EpisodePrerollOutcome.None)]
    public async Task OutcomeRoll_UsesMutuallyExclusiveConfiguredRanges(
        int roll,
        EpisodePrerollOutcome expected)
    {
        _random.Rolls.Enqueue(roll);
        Assert.Equal(expected, await Select());
    }

    [Fact]
    public async Task BothChance_UsesItsOwnRange()
    {
        _config.EpisodeCommercialOnlyChancePercent = 10;
        _config.EpisodeMovieTrailerOnlyChancePercent = 20;
        _config.EpisodeBothChancePercent = 30;
        _random.Rolls.Enqueue(30);

        Assert.Equal(EpisodePrerollOutcome.Both, await Select());
    }

    [Fact]
    public async Task InvalidChanceTotal_DisablesEpisodeOutcome()
    {
        _config.EpisodeCommercialOnlyChancePercent = 100;
        _config.EpisodeMovieTrailerOnlyChancePercent = 50;
        _config.EpisodeBothChancePercent = 0;

        Assert.Equal(EpisodePrerollOutcome.None, await Select());
    }

    [Fact]
    public async Task LaterRequests_GetFreshRollsWithoutConsumingQuota()
    {
        _random.Rolls.Enqueue(20);
        _random.Rolls.Enqueue(0);
        Assert.Equal(EpisodePrerollOutcome.None, await Select());
        Assert.Equal(EpisodePrerollOutcome.CommercialOnly, await Select());
        Assert.Empty((await State()).PrerollStartsUtc);
    }

    [Fact]
    public async Task Spacing_UsesConfiguredEpisodeCount()
    {
        _config.EpisodePreRollMinEpisodes = 5;
        _config.EpisodePreRollCooldownMinutes = 0;
        await Preroll("commercial");
        for (var i = 0; i < 4; i++)
            await Episode(Guid.NewGuid(), "episode-" + i);
        Assert.Equal(EpisodePrerollOutcome.None, await Select());
        await Episode(Guid.NewGuid(), "episode-4");
        Assert.Equal(EpisodePrerollOutcome.CommercialOnly, await Select());
    }

    [Fact]
    public async Task Cooldown_AllowsExactlyAtConfiguredBoundary()
    {
        _config.EpisodePreRollMinEpisodes = 0;
        _config.EpisodePreRollCooldownMinutes = 30;
        await Preroll("first");
        _clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(EpisodePrerollOutcome.None, await Select());
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(EpisodePrerollOutcome.CommercialOnly, await Select());
    }

    [Fact]
    public async Task RollingQuota_UsesConfiguredCountAndWindow()
    {
        _config.EpisodePreRollMinEpisodes = 0;
        _config.EpisodePreRollCooldownMinutes = 0;
        _config.EpisodePreRollMaxPerWindow = 3;
        _config.EpisodePreRollWindowHours = 2;
        await Preroll("one");
        _clock.Advance(TimeSpan.FromMinutes(30));
        await Preroll("two");
        Assert.Equal(EpisodePrerollOutcome.CommercialOnly, await Select());
        _clock.Advance(TimeSpan.FromMinutes(30));
        await Preroll("three");
        Assert.Equal(EpisodePrerollOutcome.None, await Select());
        _clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(EpisodePrerollOutcome.CommercialOnly, await Select());
    }

    [Fact]
    public async Task ZeroLimits_DisableFrequencyRestrictions()
    {
        _config.EpisodePreRollMinEpisodes = 0;
        _config.EpisodePreRollCooldownMinutes = 0;
        _config.EpisodePreRollMaxPerWindow = 0;
        await Preroll("one");
        await Preroll("two");
        Assert.Equal(EpisodePrerollOutcome.CommercialOnly, await Select());
    }

    [Fact]
    public async Task Resume_NeitherSelectsNorCountsEpisode()
    {
        await Preroll("first");
        await _coordinator.RecordEpisodeStartedAsync(_user, Guid.NewGuid(), 100, "resume", _config, Token);
        Assert.Equal(0, (await State()).EpisodesSincePreroll);
        _clock.Advance(TimeSpan.FromHours(5));
        _config.EpisodePreRollMinEpisodes = 0;
        Assert.Equal(EpisodePrerollOutcome.None, await Select(isResume: true));
        Assert.Equal(EpisodePrerollOutcome.CommercialOnly, await Select());
    }

    [Theory]
    [InlineData("same-session")]
    [InlineData(null)]
    public async Task DuplicateEpisodeReports_CountOnce(string? session)
    {
        await Preroll("first");
        var episode = Guid.NewGuid();
        await Task.WhenAll(Episode(episode, session), Episode(episode, session));
        Assert.Equal(1, (await State()).EpisodesSincePreroll);
    }

    [Theory]
    [InlineData("same-session")]
    [InlineData(null)]
    public async Task DuplicatePrerollReports_CountOnce(string? session)
    {
        await Task.WhenAll(Preroll(session), Preroll(session));
        Assert.Single((await State()).PrerollStartsUtc);
    }

    [Fact]
    public async Task NewPlaybackSession_CanCountSameEpisodeAgain()
    {
        await Preroll("first");
        var episode = Guid.NewGuid();
        await Episode(episode, "viewing-one");
        await Episode(episode, "viewing-two");
        Assert.Equal(2, (await State()).EpisodesSincePreroll);
    }

    [Fact]
    public async Task History_SurvivesRestartAndIncreasingWindow()
    {
        _config.EpisodePreRollMinEpisodes = 0;
        _config.EpisodePreRollMaxPerWindow = 1;
        _config.EpisodePreRollWindowHours = 1;
        await Preroll("one");
        _clock.Advance(TimeSpan.FromHours(5));
        Assert.Equal(EpisodePrerollOutcome.CommercialOnly, await Select());
        _config.EpisodePreRollWindowHours = 6;
        var reloadedStore = new EpisodePrerollStateStore(Path.Combine(_directory, "history.json"), NullLogger<EpisodePrerollStateStore>.Instance);
        using var restarted = new EpisodePrerollCoordinator(reloadedStore, NullLogger<EpisodePrerollCoordinator>.Instance, _clock, _random);
        Assert.Equal(
            EpisodePrerollOutcome.None,
            await restarted.SelectOutcomeAsync(_user, false, _config, Token));
    }

    [Fact]
    public async Task Users_HaveIndependentHistory()
    {
        await Preroll("first");
        Assert.Equal(
            EpisodePrerollOutcome.CommercialOnly,
            await _coordinator.SelectOutcomeAsync(Guid.NewGuid(), false, _config, Token));
        Assert.Equal(EpisodePrerollOutcome.None, await Select());
    }

    [Fact]
    public async Task PendingMovieTrailerStart_CountsAsOneEpisodePrerollEvent()
    {
        var trailer = Guid.NewGuid();
        await _coordinator.RegisterPendingMovieTrailerAsync(_user, trailer, Token);

        Assert.True(await _coordinator.RecordPendingMovieTrailerStartedAsync(
            _user,
            trailer,
            "episode-trailer",
            _config,
            Token));
        Assert.False(await _coordinator.RecordPendingMovieTrailerStartedAsync(
            _user,
            trailer,
            "episode-trailer",
            _config,
            Token));
        Assert.Single((await State()).PrerollStartsUtc);
    }

    private EpisodePrerollCoordinator NewCoordinator() => new(_store, NullLogger<EpisodePrerollCoordinator>.Instance, _clock, _random);
    private Task<EpisodePrerollOutcome> Select(bool isResume = false) => _coordinator.SelectOutcomeAsync(_user, isResume, _config, Token);
    private Task Episode(Guid episode, string? session) => _coordinator.RecordEpisodeStartedAsync(_user, episode, 0, session, _config, Token);
    private Task Preroll(string? session) => _coordinator.RecordPrerollStartedAsync(_user, _asset, session, _config, Token);
    private Task<EpisodePrerollUserState> State() => _store.LoadUserAsync(_user, _clock.GetUtcNow(), Token);

    public void Dispose()
    {
        _coordinator.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private sealed class RollSequence : Random
    {
        public Queue<int> Rolls { get; } = new();
        public override int Next(int maxValue) => maxValue == 100 && Rolls.Count > 0 ? Rolls.Dequeue() : 0;
    }
}
