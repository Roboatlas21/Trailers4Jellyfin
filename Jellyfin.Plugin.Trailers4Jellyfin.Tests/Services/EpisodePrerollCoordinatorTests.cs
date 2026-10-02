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
        Assert.Equal(75, _config.EpisodePreRollChancePercent);
        Assert.Equal(60, _config.EpisodePreRollCooldownMinutes);
        Assert.Equal(3, _config.EpisodePreRollMinEpisodes);
        Assert.Equal(2, _config.EpisodePreRollMaxPerWindow);
        Assert.Equal(4, _config.EpisodePreRollWindowHours);
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(25, 24, true)]
    [InlineData(25, 25, false)]
    [InlineData(75, 74, true)]
    [InlineData(75, 75, false)]
    [InlineData(100, 99, true)]
    [InlineData(-10, 0, false)]
    [InlineData(110, 99, true)]
    public async Task Chance_RespectsConfiguredThreshold(int chance, int roll, bool selected)
    {
        _config.EpisodePreRollChancePercent = chance;
        _random.Rolls.Enqueue(roll);
        Assert.Equal(selected, (await Select()).HasValue);
    }

    [Fact]
    public async Task LaterRequests_GetFreshRollsWithoutConsumingQuota()
    {
        _random.Rolls.Enqueue(75);
        _random.Rolls.Enqueue(74);
        Assert.Null(await Select());
        Assert.Equal(_asset, await Select());
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
        Assert.Null(await Select());
        await Episode(Guid.NewGuid(), "episode-4");
        Assert.Equal(_asset, await Select());
    }

    [Fact]
    public async Task Cooldown_AllowsExactlyAtConfiguredBoundary()
    {
        _config.EpisodePreRollMinEpisodes = 0;
        _config.EpisodePreRollCooldownMinutes = 30;
        await Preroll("first");
        _clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Null(await Select());
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(_asset, await Select());
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
        Assert.Equal(_asset, await Select());
        _clock.Advance(TimeSpan.FromMinutes(30));
        await Preroll("three");
        Assert.Null(await Select());
        _clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(_asset, await Select());
    }

    [Fact]
    public async Task ZeroLimits_DisableFrequencyRestrictions()
    {
        _config.EpisodePreRollMinEpisodes = 0;
        _config.EpisodePreRollCooldownMinutes = 0;
        _config.EpisodePreRollMaxPerWindow = 0;
        await Preroll("one");
        await Preroll("two");
        Assert.Equal(_asset, await Select());
    }

    [Fact]
    public async Task Resume_NeitherSelectsNorCountsEpisode()
    {
        await Preroll("first");
        await _coordinator.RecordEpisodeStartedAsync(_user, Guid.NewGuid(), 100, "resume", _config, Token);
        Assert.Equal(0, (await State()).EpisodesSincePreroll);
        _clock.Advance(TimeSpan.FromHours(5));
        _config.EpisodePreRollMinEpisodes = 0;
        Assert.Null(await Select(isResume: true));
        Assert.Equal(_asset, await Select());
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
        Assert.Equal(_asset, await Select());
        _config.EpisodePreRollWindowHours = 6;
        var reloadedStore = new EpisodePrerollStateStore(Path.Combine(_directory, "history.json"), NullLogger<EpisodePrerollStateStore>.Instance);
        using var restarted = new EpisodePrerollCoordinator(reloadedStore, NullLogger<EpisodePrerollCoordinator>.Instance, _clock, _random);
        Assert.Null(await restarted.SelectPrerollAsync(_user, new[] { _asset }, false, _config, Token));
    }

    [Fact]
    public async Task Users_HaveIndependentHistory()
    {
        await Preroll("first");
        Assert.Equal(_asset, await _coordinator.SelectPrerollAsync(Guid.NewGuid(), new[] { _asset }, false, _config, Token));
        Assert.Null(await Select());
    }

    private EpisodePrerollCoordinator NewCoordinator() => new(_store, NullLogger<EpisodePrerollCoordinator>.Instance, _clock, _random);
    private Task<Guid?> Select(bool isResume = false) => _coordinator.SelectPrerollAsync(_user, new[] { _asset }, isResume, _config, Token);
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
