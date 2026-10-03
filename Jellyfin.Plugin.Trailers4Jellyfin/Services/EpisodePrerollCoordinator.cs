using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Trailers4Jellyfin.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Services
{
    public enum EpisodePrerollOutcome
    {
        None = 0,
        CommercialOnly = 1,
        MovieTrailerOnly = 2,
        Both = 3,
    }

    public sealed class EpisodePrerollCoordinator : IDisposable
    {
        private readonly EpisodePrerollStateStore _stateStore;
        private readonly ILogger<EpisodePrerollCoordinator> _logger;
        private readonly TimeProvider _clock;
        private readonly Random _random;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Dictionary<(Guid UserId, Guid ItemId, string? PlaySessionId), DateTimeOffset> _processedStarts = new();
        private readonly Dictionary<(Guid UserId, Guid TrailerId), DateTimeOffset> _pendingMovieTrailers = new();

        public EpisodePrerollCoordinator(
            EpisodePrerollStateStore stateStore,
            ILogger<EpisodePrerollCoordinator> logger)
            : this(stateStore, logger, TimeProvider.System, Random.Shared)
        {
        }

        internal EpisodePrerollCoordinator(
            EpisodePrerollStateStore stateStore,
            ILogger<EpisodePrerollCoordinator> logger,
            TimeProvider clock,
            Random random)
        {
            _stateStore = stateStore;
            _logger = logger;
            _clock = clock;
            _random = random;
        }

        public async Task<EpisodePrerollOutcome> SelectOutcomeAsync(
            Guid userId,
            bool isResume,
            PluginConfiguration config,
            CancellationToken cancellationToken = default)
        {
            var commercialChance = Math.Clamp(config.EpisodeCommercialOnlyChancePercent, 0, 100);
            var movieTrailerChance = Math.Clamp(config.EpisodeMovieTrailerOnlyChancePercent, 0, 100);
            var bothChance = Math.Clamp(config.EpisodeBothChancePercent, 0, 100);
            var totalChance = commercialChance + movieTrailerChance + bothChance;

            if (isResume || totalChance == 0)
                return EpisodePrerollOutcome.None;

            if (totalChance > 100)
            {
                _logger.LogWarning(
                    "|Trailers4Jellyfin| Episode pre-roll chances total {Total}% (> 100%); skipping until configuration is corrected",
                    totalChance);
                return EpisodePrerollOutcome.None;
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var now = _clock.GetUtcNow();
                var state = await _stateStore.LoadUserAsync(userId, now, cancellationToken).ConfigureAwait(false);
                var cutoff = now - TimeSpan.FromHours(Math.Clamp(config.EpisodePreRollWindowHours, 1, 168));
                var maxPerWindow = Math.Clamp(config.EpisodePreRollMaxPerWindow, 0, 100);
                var cooldown = TimeSpan.FromMinutes(Math.Clamp(config.EpisodePreRollCooldownMinutes, 0, 10080));

                if (state.EpisodesSincePreroll < Math.Clamp(config.EpisodePreRollMinEpisodes, 0, 100)
                    || (maxPerWindow > 0 && state.PrerollStartsUtc.Count(t => t > cutoff) >= maxPerWindow)
                    || (state.PrerollStartsUtc.Count > 0 && now - state.PrerollStartsUtc.Max() < cooldown))
                {
                    return EpisodePrerollOutcome.None;
                }

                // One roll chooses one mutually exclusive outcome. The unallocated remainder is "nothing".
                var roll = _random.Next(100);
                if (roll < commercialChance)
                    return EpisodePrerollOutcome.CommercialOnly;
                if (roll < commercialChance + movieTrailerChance)
                    return EpisodePrerollOutcome.MovieTrailerOnly;
                if (roll < totalChance)
                    return EpisodePrerollOutcome.Both;
                return EpisodePrerollOutcome.None;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task RegisterPendingMovieTrailerAsync(
            Guid userId,
            Guid trailerId,
            CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var now = _clock.GetUtcNow();
                RemoveExpiredPendingMovieTrailers(now);
                _pendingMovieTrailers[(userId, trailerId)] = now + TimeSpan.FromMinutes(10);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<bool> RecordPendingMovieTrailerStartedAsync(
            Guid userId,
            Guid trailerId,
            string? playSessionId,
            PluginConfiguration config,
            CancellationToken cancellationToken = default)
        {
            var pending = false;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var now = _clock.GetUtcNow();
                RemoveExpiredPendingMovieTrailers(now);
                pending = _pendingMovieTrailers.Remove((userId, trailerId));
            }
            finally
            {
                _gate.Release();
            }

            if (!pending)
                return false;

            await RecordStartAsync(
                    userId,
                    trailerId,
                    playSessionId,
                    true,
                    config,
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        public Task RecordEpisodeStartedAsync(
            Guid userId,
            Guid episodeId,
            long? playbackPositionTicks,
            string? playSessionId,
            PluginConfiguration config,
            CancellationToken cancellationToken = default)
        {
            return playbackPositionTicks.GetValueOrDefault() > 0
                ? Task.CompletedTask
                : RecordStartAsync(userId, episodeId, playSessionId, false, config, cancellationToken);
        }

        public Task RecordPrerollStartedAsync(
            Guid userId,
            Guid prerollAssetId,
            string? playSessionId,
            PluginConfiguration config,
            CancellationToken cancellationToken = default)
        {
            return RecordStartAsync(userId, prerollAssetId, playSessionId, true, config, cancellationToken);
        }

        private async Task RecordStartAsync(
            Guid userId,
            Guid itemId,
            string? playSessionId,
            bool isPreroll,
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var now = _clock.GetUtcNow();
                foreach (var key in _processedStarts.Where(p => p.Value <= now).Select(p => p.Key).ToArray())
                    _processedStarts.Remove(key);

                var hasSession = !string.IsNullOrWhiteSpace(playSessionId);
                var identity = (userId, itemId, hasSession ? playSessionId : null);
                if (_processedStarts.ContainsKey(identity))
                    return;

                var state = await _stateStore.LoadUserAsync(userId, now, cancellationToken).ConfigureAwait(false);
                var needsSave = isPreroll || state.EpisodesSincePreroll < Math.Clamp(config.EpisodePreRollMinEpisodes, 0, 100);
                if (isPreroll)
                {
                    state.EpisodesSincePreroll = 0;
                    state.PrerollStartsUtc.Add(now);
                }
                else if (needsSave)
                {
                    state.EpisodesSincePreroll++;
                }
                if (needsSave)
                    await _stateStore.SaveUserAsync(userId, state, now, cancellationToken).ConfigureAwait(false);
                _processedStarts[identity] = now + (hasSession ? TimeSpan.FromHours(6) : TimeSpan.FromSeconds(30));

                if (isPreroll)
                    _logger.LogInformation("|Trailers4Jellyfin| Episode pre-roll started for user {UserId}", userId);
            }
            finally
            {
                _gate.Release();
            }
        }

        private void RemoveExpiredPendingMovieTrailers(DateTimeOffset now)
        {
            foreach (var key in _pendingMovieTrailers.Where(p => p.Value <= now).Select(p => p.Key).ToArray())
                _pendingMovieTrailers.Remove(key);
        }

        public void Dispose() => _gate.Dispose();
    }
}
