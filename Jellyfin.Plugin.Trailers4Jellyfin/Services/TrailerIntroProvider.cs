using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
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
            if (config == null || !config.EnableCinemaMode || config.NumberOfTrailers <= 0)
                return Task.FromResult(Enumerable.Empty<IntroInfo>());

            if (string.IsNullOrWhiteSpace(config.DownloadFolder))
                return Task.FromResult(Enumerable.Empty<IntroInfo>());

            if (item is not MediaBrowser.Controller.Entities.Movies.Movie)
                return Task.FromResult(Enumerable.Empty<IntroInfo>());

            var trailerItems = _assetRegistry
                .SyncDownloadedTrailers(config.DownloadFolder)
                .ToList();

            if (trailerItems.Count == 0)
            {
                _logger.LogDebug(
                    "|Trailers4Jellyfin| No downloaded trailer files found under '{Folder}'",
                    config.DownloadFolder);
                return Task.FromResult(Enumerable.Empty<IntroInfo>());
            }

            if (!string.IsNullOrWhiteSpace(item.OfficialRating)
                && RatingSeverity.TryGetValue(item.OfficialRating, out var movieSeverity))
            {
                var filtered = trailerItems.Where(t => IsRatingAppropriate(t, movieSeverity)).ToList();
                if (filtered.Count > 0)
                    trailerItems = filtered;
                else
                    _logger.LogDebug(
                        "|Trailers4Jellyfin| No trailers at or below rating '{Rating}' for '{Movie}', skipping rating filter",
                        item.OfficialRating,
                        item.Name);
            }

            List<Video> selected;

            if (config.EnableGenreMatching && item.Genres != null && item.Genres.Length > 0)
            {
                var movieGenres = new HashSet<string>(item.Genres, StringComparer.OrdinalIgnoreCase);

                var scored = trailerItems
                    .Select(t => (trailer: t, score: GetGenreScore(t.Path!, movieGenres)))
                    .ToList();

                var matched = scored.Where(x => x.score > 0).ToList();
                var pool = matched.Count >= config.NumberOfTrailers ? matched : scored;

                selected = pool
                    .OrderByDescending(x => x.score)
                    .ThenBy(_ => Guid.NewGuid())
                    .Take(config.NumberOfTrailers)
                    .Select(x => x.trailer)
                    .ToList();
            }
            else
            {
                selected = trailerItems
                    .OrderBy(_ => Guid.NewGuid())
                    .Take(config.NumberOfTrailers)
                    .ToList();
            }

            _logger.LogInformation(
                "|Trailers4Jellyfin| Queuing {Count} intro trailer(s) before '{Movie}'",
                selected.Count,
                item.Name);

            return Task.FromResult<IEnumerable<IntroInfo>>(
                selected.Select(t => new IntroInfo { ItemId = t.Id, Path = t.Path }));
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
    }
}
