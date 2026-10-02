using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Services
{
    public class TrailerIntroProvider : IIntroProvider
    {
        private readonly CinemaAssetRegistry _assetRegistry;
        private readonly EpisodePrerollCoordinator _episodePrerollCoordinator;
        private readonly IUserDataManager _userDataManager;
        private readonly TrailerRatingPolicy _ratings;
        private readonly ILogger<TrailerIntroProvider> _logger;

        public string Name => "Trailers4Jellyfin";

        public TrailerIntroProvider(
            CinemaAssetRegistry assetRegistry,
            EpisodePrerollCoordinator episodePrerollCoordinator,
            IUserDataManager userDataManager,
            TrailerRatingPolicy ratings,
            ILogger<TrailerIntroProvider> logger)
        {
            _assetRegistry = assetRegistry;
            _episodePrerollCoordinator = episodePrerollCoordinator;
            _userDataManager = userDataManager;
            _ratings = ratings;
            _logger = logger;
        }

        public async Task<IEnumerable<IntroInfo>> GetIntros(BaseItem item, User user)
        {
            try
            {
                if (item is Episode episode)
                    return await GetEpisodeIntrosAsync(episode, user).ConfigureAwait(false);

                return await GetIntrosInternal(item, user).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Intro providers must never be able to break main-feature playback.
                _logger.LogError(
                    ex,
                    "|Trailers4Jellyfin| GetIntros threw unexpectedly — returning no intros to protect playback");
                return Enumerable.Empty<IntroInfo>();
            }
        }

        private async Task<IEnumerable<IntroInfo>> GetEpisodeIntrosAsync(Episode episode, User user)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.EnableCinemaMode || config.EpisodePreRollChancePercent <= 0)
                return Enumerable.Empty<IntroInfo>();

            var isResume = _userDataManager.GetUserData(user, episode)?.PlaybackPositionTicks > 0;
            if (isResume)
                return Enumerable.Empty<IntroInfo>();

            var episodePreRolls = GetPreferredClips(
                _assetRegistry.SyncEpisodePreRolls(config.EpisodePreRollFolder), user, config.PreferUnwatchedEpisodePreRolls);
            if (episodePreRolls.Count == 0)
                return Enumerable.Empty<IntroInfo>();

            var selectedId = await _episodePrerollCoordinator
                .SelectPrerollAsync(
                    user.Id,
                    episodePreRolls.Select(static p => p.Id).ToArray(),
                    isResume,
                    config)
                .ConfigureAwait(false);

            if (!selectedId.HasValue)
                return Enumerable.Empty<IntroInfo>();

            var selected = episodePreRolls.FirstOrDefault(p => p.Id == selectedId.Value);
            if (selected == null)
                return Enumerable.Empty<IntroInfo>();

            _logger.LogInformation(
                "|Trailers4Jellyfin| Queuing episode pre-roll '{PreRoll}' before '{Episode}'",
                selected.Name,
                episode.Name);

            return new[] { ToIntroInfo(selected) };
        }

        private Task<IEnumerable<IntroInfo>> GetIntrosInternal(BaseItem item, User user)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.EnableCinemaMode)
                return Task.FromResult(Enumerable.Empty<IntroInfo>());

            // Only inject before movies.
            if (item is not Movie)
                return Task.FromResult(Enumerable.Empty<IntroInfo>());

            var assets = _assetRegistry.SyncConfiguredAssets(config);
            var intros = new List<IntroInfo>();

            // Match CherryFloors Cinema Mode's ordering:
            // trailer pre-roll -> trailers -> feature pre-roll -> main feature.
            AddRandom(intros, GetPreferredClips(assets.TrailerPreRolls, user, config.PreferUnwatchedTrailerPreRolls));

            var trailerItems = assets.DownloadedTrailers.ToList();
            var selected = SelectTrailers(item, trailerItems, config, user);
            intros.AddRange(selected.Select(ToIntroInfo));

            AddRandom(intros, GetPreferredClips(assets.FeaturePreRolls, user, config.PreferUnwatchedFeaturePreRolls));

            _logger.LogInformation(
                "|Trailers4Jellyfin| Queuing {Count} Cinema Mode item(s) before '{Movie}' " +
                "({TrailerPreRolls} trailer pre-roll, {Trailers} trailer(s), {FeaturePreRolls} feature pre-roll)",
                intros.Count,
                item.Name,
                intros.Count > 0 && assets.TrailerPreRolls.Count > 0 ? 1 : 0,
                selected.Count,
                intros.Count > selected.Count && assets.FeaturePreRolls.Count > 0 ? 1 : 0);

            return Task.FromResult<IEnumerable<IntroInfo>>(intros);
        }

        internal List<Video> SelectTrailers(
            BaseItem feature,
            IReadOnlyList<Video> trailerItems,
            Configuration.PluginConfiguration config,
            User user)
        {
            if (config.NumberOfTrailers <= 0 || trailerItems.Count == 0)
                return new List<Video>();

            var country = _ratings.GetMetadataCountry(feature);
            var movieRating = _ratings.GetFeatureRating(feature, country);
            var movieGenres = new HashSet<string>(
                config.EnableGenreMatching ? feature.Genres ?? Array.Empty<string>() : Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            var watchedIds = GetWatchedIds(trailerItems, user, config.PreferUnwatchedTrailers);

            // Read each sidecar once. Rating restrictions apply before any selection preferences.
            return trailerItems
                .Select(trailer => (trailer, metadata: ReadMetadata(trailer.Path)))
                .Where(x => x.metadata != null && _ratings.IsAllowed(x.metadata, country, movieRating, user))
                .OrderBy(x => watchedIds.Contains(x.trailer.Id))
                .ThenByDescending(x => x.metadata!.Genres?.Count(movieGenres.Contains) ?? 0)
                .ThenBy(_ => Random.Shared.Next())
                .Take(config.NumberOfTrailers)
                .Select(x => x.trailer)
                .ToList();
        }

        internal IReadOnlyList<Video> GetPreferredClips(IReadOnlyList<Video> items, User user, bool preferUnwatched)
        {
            var watchedIds = GetWatchedIds(items, user, preferUnwatched);
            if (watchedIds.Count == 0)
                return items;

            var unwatched = items.Where(item => !watchedIds.Contains(item.Id)).ToList();
            return unwatched.Count > 0 ? unwatched : items;
        }

        private HashSet<Guid> GetWatchedIds(IReadOnlyList<Video> items, User user, bool preferUnwatched)
        {
            if (!preferUnwatched || items.Count == 0)
                return new HashSet<Guid>();

            // Private items may not have user data loaded. Batch lookup also reads stored history.
            return _userDataManager.GetUserDataBatch(items, user)
                .Where(entry => entry.Value.Played)
                .Select(entry => entry.Key)
                .ToHashSet();
        }

        private TrailerMetadata? ReadMetadata(string? trailerPath)
        {
            var sidecarPath = Path.ChangeExtension(trailerPath, ".json");
            if (!File.Exists(sidecarPath))
                return new TrailerMetadata();

            try
            {
                return JsonSerializer.Deserialize<TrailerMetadata>(File.ReadAllText(sidecarPath));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "|Trailers4Jellyfin| Skipping unreadable trailer metadata: {Path}", sidecarPath);
                return null;
            }
        }

        private static IntroInfo ToIntroInfo(Video item) =>
            new() { ItemId = item.Id, Path = item.Path };

        private static void AddRandom(List<IntroInfo> intros, IReadOnlyList<Video> items)
        {
            if (items.Count == 0)
                return;

            intros.Add(ToIntroInfo(items[Random.Shared.Next(items.Count)]));
        }

    }
}
