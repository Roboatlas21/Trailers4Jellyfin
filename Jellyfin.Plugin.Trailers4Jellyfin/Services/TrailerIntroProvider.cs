using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        private readonly ILogger<TrailerIntroProvider> _logger;

        public string Name => "Trailers4Jellyfin";

        public TrailerIntroProvider(
            CinemaAssetRegistry assetRegistry,
            EpisodePrerollCoordinator episodePrerollCoordinator,
            IUserDataManager userDataManager,
            ILogger<TrailerIntroProvider> logger)
        {
            _assetRegistry = assetRegistry;
            _episodePrerollCoordinator = episodePrerollCoordinator;
            _userDataManager = userDataManager;
            _logger = logger;
        }

        public async Task<IEnumerable<IntroInfo>> GetIntros(BaseItem item, User user)
        {
            try
            {
                if (item is Episode episode)
                    return await GetEpisodeIntrosAsync(episode, user).ConfigureAwait(false);

                return await GetIntrosInternal(item).ConfigureAwait(false);
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

            var episodePreRolls = _assetRegistry.SyncEpisodePreRolls(config.EpisodePreRollFolder);
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

        private Task<IEnumerable<IntroInfo>> GetIntrosInternal(BaseItem item)
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
            AddRandom(intros, assets.TrailerPreRolls);

            var trailerItems = assets.DownloadedTrailers.ToList();
            var selected = SelectTrailers(item, trailerItems, config);
            intros.AddRange(selected.Select(ToIntroInfo));

            AddRandom(intros, assets.FeaturePreRolls);

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
            Configuration.PluginConfiguration config)
        {
            if (config.NumberOfTrailers <= 0 || trailerItems.Count == 0)
                return new List<Video>();

            int? movieSeverity = !string.IsNullOrWhiteSpace(feature.OfficialRating)
                && RatingSeverity.TryGetValue(feature.OfficialRating, out var severity)
                    ? severity : null;
            var movieGenres = new HashSet<string>(
                config.EnableGenreMatching ? feature.Genres ?? Array.Empty<string>() : Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            // Read each sidecar once. An invalid sidecar only excludes that trailer.
            // Missing/unknown ratings retain the existing permissive behavior.
            return trailerItems
                .Select(trailer => (trailer, metadata: ReadMetadata(trailer.Path)))
                .Where(x => x.metadata != null
                    && (movieSeverity == null
                        || string.IsNullOrWhiteSpace(x.metadata.OfficialRating)
                        || !RatingSeverity.TryGetValue(x.metadata.OfficialRating, out var trailerSeverity)
                        || trailerSeverity <= movieSeverity.Value))
                .OrderByDescending(x => x.metadata!.Genres?.Count(movieGenres.Contains) ?? 0)
                .ThenBy(_ => Random.Shared.Next())
                .Take(config.NumberOfTrailers)
                .Select(x => x.trailer)
                .ToList();
        }

        private static readonly Dictionary<string, int> RatingSeverity =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // MPAA / common US labels
                { "G", 1 }, { "PG", 2 }, { "PG-13", 3 }, { "R", 4 }, { "NC-17", 5 },
                { "US-G", 1 }, { "US-PG", 2 }, { "US-PG-13", 3 }, { "US-R", 4 }, { "US-NC-17", 5 },

                // Canadian labels
                { "CA-G", 1 }, { "CA-PG", 2 }, { "14A", 3 }, { "CA-14A", 3 },
                { "18A", 4 }, { "CA-18A", 4 }, { "CA-R", 4 },

                // US TV
                { "TV-Y", 1 }, { "TV-G", 1 }, { "TV-Y7", 2 }, { "TV-PG", 2 },
                { "TV-14", 3 }, { "TV-MA", 4 },

                // BBFC (UK)
                { "U", 1 }, { "12A", 3 }, { "15", 4 }, { "R18", 6 },

                // European age labels
                { "0", 1 }, { "6", 2 }, { "12", 3 }, { "16", 4 }, { "18", 5 },
            };

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

        private sealed class TrailerMetadata
        {
            [JsonPropertyName("genres")]
            public string[]? Genres { get; set; }

            [JsonPropertyName("officialRating")]
            public string? OfficialRating { get; set; }
        }
    }
}
