using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Services
{
    public class TrailerIntroProvider : IIntroProvider
    {
        private readonly CinemaAssetRegistry _assetRegistry;
        private readonly EpisodePrerollCoordinator _episodePrerollCoordinator;
        private readonly IUserDataManager _userDataManager;
        private readonly TrailerRatingPolicy _ratings;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<TrailerIntroProvider> _logger;

        public string Name => "Trailers4Jellyfin";

        public TrailerIntroProvider(
            CinemaAssetRegistry assetRegistry,
            EpisodePrerollCoordinator episodePrerollCoordinator,
            IUserDataManager userDataManager,
            TrailerRatingPolicy ratings,
            ILibraryManager libraryManager,
            ILogger<TrailerIntroProvider> logger)
        {
            _assetRegistry = assetRegistry;
            _episodePrerollCoordinator = episodePrerollCoordinator;
            _userDataManager = userDataManager;
            _ratings = ratings;
            _libraryManager = libraryManager;
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
            var featureTmdbId = ParseTmdbId(feature.GetProviderId(MetadataProvider.Tmdb));

            // Hard exclusions run before choosing a genre group, including all fallbacks.
            var candidates = trailerItems.DistinctBy(t => t.Id)
                .Select(t => (Trailer: t, Metadata: ReadMetadata(t.Path)))
                .Where(x => x.Metadata != null && _ratings.IsAllowed(x.Metadata, country, movieRating, user))
                .Where(x => !config.SkipCurrentMovieTrailers || featureTmdbId == null || x.Metadata!.TmdbId != featureTmdbId)
                .Select(x => (x.Trailer, Metadata: x.Metadata!, Genres: TrailerGenres.Normalize(x.Metadata!.Genres)))
                .ToList();

            if (config.SkipWatchedMovieTrailers && candidates.Count > 0)
            {
                var watchedMovies = GetWatchedMovieIds(candidates.Select(x => x.Metadata.TmdbId), user);
                candidates.RemoveAll(x => x.Metadata.TmdbId is int id && watchedMovies.Contains(id));
            }
            if (candidates.Count == 0) return new List<Video>();

            var movieGenres = TrailerGenres.Normalize(config.EnableGenreMatching ? feature.Genres : null);
            var scoringGenres = movieGenres;
            var group = candidates.Where(x => x.Genres.Overlaps(movieGenres)).ToList();
            if (group.Count == 0)
            {
                scoringGenres = TrailerGenres.GetRelated(movieGenres);
                group = candidates.Where(x => x.Genres.Overlaps(scoringGenres)).ToList();
            }
            if (group.Count == 0)
            {
                group = candidates;
                scoringGenres = new HashSet<string>();
            }

            // Group membership is independent of history. Fill only within this group.
            var history = config.PreferUnwatchedTrailers
                ? _userDataManager.GetUserDataBatch(group.Select(x => x.Trailer).ToArray(), user)
                : new Dictionary<Guid, UserItemData>();
            return group.Select(x =>
                {
                    history.TryGetValue(x.Trailer.Id, out var data);
                    return (x.Trailer, Score: x.Genres.Count(scoringGenres.Contains),
                        Played: data?.Played == true, LastPlayed: data?.LastPlayedDate ?? DateTime.MinValue);
                })
                .OrderBy(x => x.Played)
                .ThenByDescending(x => x.Played ? 0 : x.Score)
                .ThenBy(x => x.Played ? x.LastPlayed : DateTime.MinValue)
                .ThenBy(_ => Random.Shared.Next())
                .Take(config.NumberOfTrailers)
                .Select(x => x.Trailer)
                .ToList();
        }

        private HashSet<int> GetWatchedMovieIds(IEnumerable<int?> trailerMovieIds, User user)
        {
            var ids = trailerMovieIds.Where(id => id is > 0).Select(id => id!.Value).ToHashSet();
            if (ids.Count == 0) return new HashSet<int>();
            var movies = _libraryManager.GetItemList(new InternalItemsQuery(user)
                {
                    IncludeItemTypes = new[] { BaseItemKind.Movie },
                    Recursive = true,
                    GroupByPresentationUniqueKey = false,
                    EnableGroupByMetadataKey = false,
                })
                .OfType<Movie>()
                .Where(m => ParseTmdbId(m.GetProviderId(MetadataProvider.Tmdb)) is int id && ids.Contains(id))
                .ToArray();
            if (movies.Length == 0) return new HashSet<int>();
            var history = _userDataManager.GetUserDataBatch(movies, user);
            return movies.Where(m => history.TryGetValue(m.Id, out var data) && data.Played)
                .Select(m => ParseTmdbId(m.GetProviderId(MetadataProvider.Tmdb))!.Value)
                .ToHashSet();
        }

        private static int? ParseTmdbId(string? value) =>
            int.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;

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
