using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Services
{
    public class TrailerIntroProvider : IIntroProvider
    {
        private readonly CinemaAssetRegistry _assetRegistry;
        private readonly ILogger<TrailerIntroProvider> _logger;

        public string Name => "Trailers4Jellyfin";

        public TrailerIntroProvider(
            CinemaAssetRegistry assetRegistry,
            ILogger<TrailerIntroProvider> logger)
        {
            _assetRegistry = assetRegistry;
            _logger = logger;
        }

        public Task<IEnumerable<IntroInfo>> GetIntros(BaseItem item, User user)
        {
            try
            {
                return GetIntrosInternal(item);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "|Trailers4Jellyfin| GetIntros threw unexpectedly — returning no intros to protect playback");
                return Task.FromResult(Enumerable.Empty<IntroInfo>());
            }
        }

        private Task<IEnumerable<IntroInfo>> GetIntrosInternal(BaseItem item)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.EnableCinemaMode)
                return Task.FromResult(Enumerable.Empty<IntroInfo>());

            if (item is not Movie)
                return Task.FromResult(Enumerable.Empty<IntroInfo>());

            var assets = _assetRegistry.SyncConfiguredAssets(config);
            var intros = new List<IntroInfo>();

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

        private List<Video> SelectTrailers(
            BaseItem feature,
            List<Video> trailerItems,
            Configuration.PluginConfiguration config)
        {
            if (config.NumberOfTrailers <= 0 || trailerItems.Count == 0)
                return new List<Video>();

            if (!string.IsNullOrWhiteSpace(feature.OfficialRating)
                && RatingSeverity.TryGetValue(feature.OfficialRating, out var movieSeverity))
            {
                var filtered = trailerItems
                    .Where(t => IsRatingAppropriate(t, movieSeverity))
                    .ToList();

                if (filtered.Count > 0)
                {
                    trailerItems = filtered;
                }
                else
                {
                    _logger.LogDebug(
                        "|Trailers4Jellyfin| No trailers at or below rating '{Rating}' for '{Movie}', skipping rating filter",
                        feature.OfficialRating,
                        feature.Name);
                }
            }

            if (config.EnableGenreMatching && feature.Genres != null && feature.Genres.Length > 0)
            {
                var movieGenres = new HashSet<string>(
                    feature.Genres,
                    StringComparer.OrdinalIgnoreCase);

                var scored = trailerItems
                    .Select(t => (trailer: t, score: GetGenreScore(t.Path!, movieGenres)))
                    .ToList();

                var matched = scored.Where(x => x.score > 0).ToList();
                var pool = matched.Count >= config.NumberOfTrailers ? matched : scored;

                return pool
                    .OrderByDescending(x => x.score)
                    .ThenBy(_ => Guid.NewGuid())
                    .Take(config.NumberOfTrailers)
                    .Select(x => x.trailer)
                    .ToList();
            }

            return trailerItems
                .OrderBy(_ => Guid.NewGuid())
                .Take(config.NumberOfTrailers)
                .ToList();
        }

        private static readonly Dictionary<string, int> RatingSeverity =
            new(StringComparer.OrdinalIgnoreCase)
            {
                { "G", 1 }, { "PG", 2 }, { "PG-13", 3 }, { "R", 4 }, { "NC-17", 5 },
                { "TV-Y", 1 }, { "TV-G", 1 }, { "TV-Y7", 2 }, { "TV-PG", 2 },
                { "TV-14", 3 }, { "TV-MA", 4 },
                { "U", 1 }, { "12A", 3 }, { "15", 4 }, { "R18", 6 },
                { "0", 1 }, { "6", 2 }, { "12", 3 }, { "16", 4 }, { "18", 5 },
            };

        private static bool IsRatingAppropriate(BaseItem trailer, int movieSeverity)
        {
            var rating = trailer.OfficialRating;
            if (string.IsNullOrWhiteSpace(rating))
                return true;

            return !RatingSeverity.TryGetValue(rating, out var trailerSeverity)
                || trailerSeverity <= movieSeverity;
        }

        private static int GetGenreScore(string trailerPath, HashSet<string> movieGenres)
        {
            var sidecarPath = Path.ChangeExtension(trailerPath, ".json");
            if (!File.Exists(sidecarPath))
                return 0;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(sidecarPath));
                if (!doc.RootElement.TryGetProperty("genres", out var genresEl))
                    return 0;

                return genresEl.EnumerateArray()
                    .Count(g => movieGenres.Contains(g.GetString() ?? string.Empty));
            }
            catch
            {
                return 0;
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
