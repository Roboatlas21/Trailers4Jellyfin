using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Trailers4Jellyfin.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailers4Jellyfin.ScheduledTasks
{
    public class DownloadTrailersTask : IScheduledTask
    {
        private readonly ILogger<DownloadTrailersTask> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly IUserDataManager _userDataManager;
        private readonly TmdbService _tmdbService;
        private readonly TrailerDownloadService _downloadService;
        private readonly CinemaAssetRegistry _assetRegistry;

        public string Name => "Download TMDB Trailers";
        public string Key => "Trailers4JellyfinDownload";
        public string Description => "Downloads trailers from TMDB for upcoming and recently released movies not in your library, for use with Jellyfin Cinema Mode.";
        public string Category => "Trailers4Jellyfin";

        public DownloadTrailersTask(
            ILogger<DownloadTrailersTask> logger,
            ILibraryManager libraryManager,
            IUserManager userManager,
            IUserDataManager userDataManager,
            TmdbService tmdbService,
            TrailerDownloadService downloadService,
            CinemaAssetRegistry assetRegistry)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _userManager = userManager;
            _userDataManager = userDataManager;
            _tmdbService = tmdbService;
            _downloadService = downloadService;
            _assetRegistry = assetRegistry;
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(24).Ticks,
            };
        }

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;

            if (string.IsNullOrWhiteSpace(config.TmdbApiKey))
            {
                _logger.LogWarning("|Trailers4Jellyfin| No TMDB API key configured. Skipping task.");
                return;
            }

            if (string.IsNullOrWhiteSpace(config.DownloadFolder))
            {
                _logger.LogWarning("|Trailers4Jellyfin| No download folder configured. Skipping task.");
                return;
            }

            Directory.CreateDirectory(config.DownloadFolder);

            var registeredTrailers = _assetRegistry.SyncDownloadedTrailers(config.DownloadFolder);

            // Upgrade every registered trailer, including older movies outside today's TMDB sources.
            foreach (var trailer in registeredTrailers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await TrailerMetadataRefresh.RefreshAsync(
                        trailer.Path,
                        (id, ct) => _tmdbService.GetCertificationsAsync(id.ToString(), config.TmdbApiKey, ct),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "|Trailers4Jellyfin| Could not refresh trailer metadata: {Path}", trailer.Path);
                }
            }

            if (!config.SourceNowPlaying && !config.SourceUpcoming && !config.SourcePopular && !config.SourceTopRated)
            {
                _logger.LogWarning("|Trailers4Jellyfin| No TMDB sources selected. Enable at least one source. Skipping task.");
                return;
            }

            CleanupTrailers(config, registeredTrailers);
            _assetRegistry.SyncDownloadedTrailers(config.DownloadFolder);
            progress.Report(5);

            var libraryTmdbIds = config.SkipMoviesInLibrary
                ? GetLibraryTmdbIds()
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            _logger.LogInformation("|Trailers4Jellyfin| Library contains {Count} movies with TMDB IDs (will skip these)", libraryTmdbIds.Count);

            progress.Report(10);

            var allowedLanguages = string.IsNullOrWhiteSpace(config.AllowedLanguages)
                ? null
                : new HashSet<string>(
                    config.AllowedLanguages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    StringComparer.OrdinalIgnoreCase) as IReadOnlySet<string>;

            _logger.LogInformation("|Trailers4Jellyfin| Fetching candidates from TMDB...");
            var candidates = await _tmdbService.GetCandidateMoviesAsync(config, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("|Trailers4Jellyfin| Found {Count} candidate movies across all sources", candidates.Count);

            // Fetch genre ID→name map once for sidecar metadata.
            var genreMap = await _tmdbService.GetGenreMapAsync(config.TmdbApiKey, cancellationToken).ConfigureAwait(false);

            progress.Report(20);

            if (config.SkipMoviesInLibrary)
            {
                candidates = candidates
                    .Where(m => !libraryTmdbIds.Contains(m.Id.ToString()))
                    .ToList();
                _logger.LogInformation("|Trailers4Jellyfin| {Count} candidates remain after filtering library movies", candidates.Count);
            }

            if (candidates.Count == 0)
            {
                _logger.LogInformation("|Trailers4Jellyfin| No new candidates to download. All done.");
                progress.Report(100);
                return;
            }

            int downloaded = 0;
            int processed = 0;

            foreach (var movie in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (downloaded >= config.MaxTrailersToDownload)
                    break;

                double taskProgress = 20 + (80.0 * processed / candidates.Count);
                progress.Report(taskProgress);
                processed++;

                var outputPath = BuildOutputPath(movie.Title, movie.Year, config);

                if (config.SkipAlreadyDownloaded && File.Exists(outputPath))
                {
                    _logger.LogDebug("|Trailers4Jellyfin| Already downloaded: {Path}", outputPath);
                    await EnsureTrailerMetadataAsync(
                        outputPath,
                        movie,
                        genreMap,
                        config.TmdbApiKey,
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!await _tmdbService.MeetsMinimumBudgetAsync(
                    movie.Id.ToString(), config.TmdbApiKey, config.MinimumMovieBudget, cancellationToken).ConfigureAwait(false))
                {
                    _logger.LogInformation("|Trailers4Jellyfin| Skipping '{Title}': movie budget is below {Minimum} USD", movie.Title, config.MinimumMovieBudget);
                    continue;
                }

                var trailers = await _tmdbService.GetTrailersAsync(
                    movie.Id.ToString(), config.TmdbApiKey, allowedLanguages, cancellationToken).ConfigureAwait(false);

                if (trailers.Count == 0)
                {
                    _logger.LogDebug("|Trailers4Jellyfin| No YouTube trailers on TMDB for '{Title}'", movie.Title);
                    continue;
                }

                var trailer = trailers[0];
                _logger.LogInformation("|Trailers4Jellyfin| Downloading '{Trailer}' for '{Movie}'", trailer.Name, movie.Title);

                var success = await TrailerDownloadService.DownloadAndPublishAsync(
                    outputPath,
                    stagingPath => _downloadService.DownloadAsync(
                        trailer.Key,
                        stagingPath,
                        config.PreferredVideoHeight,
                        config.YtDlpPath,
                        config.CookiesFilePath,
                        config.FfmpegPath,
                        cancellationToken),
                    () => WriteTrailerMetadataAsync(outputPath, movie, genreMap, config.TmdbApiKey, cancellationToken),
                    cancellationToken).ConfigureAwait(false);

                if (success)
                {
                    downloaded++;
                    _logger.LogInformation(
                        "|Trailers4Jellyfin| [{Done}/{Max}] Saved trailer for '{Movie}' → {Path}",
                        downloaded, config.MaxTrailersToDownload, movie.Title, outputPath);

                }
            }

            _assetRegistry.SyncDownloadedTrailers(config.DownloadFolder);
            _logger.LogInformation("|Trailers4Jellyfin| Task complete. Downloaded {Count} trailer(s).", downloaded);
            progress.Report(100);
        }

        private async Task EnsureTrailerMetadataAsync(
            string trailerPath,
            TmdbMovieResult movie,
            Dictionary<int, string> genreMap,
            string apiKey,
            CancellationToken ct)
        {
            var sidecarPath = Path.ChangeExtension(trailerPath, ".json");
            if (File.Exists(sidecarPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(sidecarPath, ct).ConfigureAwait(false));
                    var root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Object
                        && root.TryGetProperty("tmdbId", out _)
                        && root.TryGetProperty("genres", out var genres)
                        && genres.ValueKind == JsonValueKind.Array
                        && genres.EnumerateArray().All(g => g.ValueKind == JsonValueKind.String))
                    {
                        return;
                    }
                }
                catch (JsonException)
                {
                    // Rewrite malformed metadata below; valid sidecars are upgraded separately.
                }
            }

            await WriteTrailerMetadataAsync(trailerPath, movie, genreMap, apiKey, ct).ConfigureAwait(false);
        }

        private async Task WriteTrailerMetadataAsync(
            string trailerPath,
            TmdbMovieResult movie,
            Dictionary<int, string> genreMap,
            string apiKey,
            CancellationToken ct)
        {
            var genres = movie.GenreIds
                .Select(id => genreMap.TryGetValue(id, out var name) ? name : null)
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();

            var certifications = await _tmdbService
                .GetCertificationsAsync(movie.Id.ToString(), apiKey, ct)
                .ConfigureAwait(false);

            var sidecarPath = Path.ChangeExtension(trailerPath, ".json");
            var json = JsonSerializer.Serialize(new
            {
                tmdbId = movie.Id,
                title = movie.Title,
                year = movie.Year,
                genres,
                certifications,
            });
            await TrailerMetadataRefresh.WriteAsync(sidecarPath, json, ct).ConfigureAwait(false);
        }

        private HashSet<string> GetLibraryTmdbIds()
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var movies = _libraryManager
                .GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Movie }, Recursive = true })
                .OfType<Movie>();

            foreach (var movie in movies)
            {
                var tmdbId = movie.GetProviderId(MetadataProvider.Tmdb);
                if (!string.IsNullOrEmpty(tmdbId))
                    ids.Add(tmdbId);
            }

            return ids;
        }

        private void CleanupTrailers(
            Configuration.PluginConfiguration config,
            IReadOnlyList<Video> registeredTrailers)
        {
            var trailerItemsByPath = registeredTrailers
                .Where(t => !string.IsNullOrWhiteSpace(t.Path))
                .ToDictionary(t => Path.GetFullPath(t.Path!), CinemaAssetRegistry.PathComparer);

            // Sweep up intermediates from a previously interrupted download. Cleanup runs before
            // any download in this task, so nothing here is in flight.
            foreach (var partial in Directory.EnumerateFiles(config.DownloadFolder)
                         .Where(TrailerDownloadService.IsPartialDownloadArtifact)
                         .ToList())
            {
                try
                {
                    _logger.LogInformation("|Trailers4Jellyfin| Removing leftover partial download: {File}", Path.GetFileName(partial));
                    File.Delete(partial);
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "|Trailers4Jellyfin| Could not delete partial download {File}", Path.GetFileName(partial));
                }
            }

            var files = Directory.GetFiles(config.DownloadFolder, "*.mp4")
                .Where(f => !Path.GetFileName(f).StartsWith("._", StringComparison.Ordinal))
                .Where(f => !TrailerDownloadService.IsPartialDownloadArtifact(f))
                .ToList();

            // Delete watched trailers first.
            if (config.DeleteWatchedTrailers)
            {
                var users = _userManager.GetUsers().ToList();
                foreach (var file in files.ToList())
                {
                    if (!trailerItemsByPath.TryGetValue(Path.GetFullPath(file), out var item)) continue;
                    bool watched = users.Any(u => _userDataManager.GetUserData(u, item)?.Played == true);
                    if (!watched) continue;

                    _logger.LogInformation("|Trailers4Jellyfin| Deleting watched trailer: {File}", Path.GetFileName(file));
                    File.Delete(file);
                    var sidecar = Path.ChangeExtension(file, ".json");
                    if (File.Exists(sidecar)) File.Delete(sidecar);
                    files.Remove(file);
                }
            }

            // Delete oldest trailers when over the cap.
            if (config.MaxTotalTrailers > 0 && files.Count > config.MaxTotalTrailers)
            {
                var toDelete = files
                    .OrderBy(File.GetCreationTime)
                    .Take(files.Count - config.MaxTotalTrailers)
                    .ToList();

                foreach (var file in toDelete)
                {
                    _logger.LogInformation("|Trailers4Jellyfin| Deleting oldest trailer to stay under cap: {File}", Path.GetFileName(file));
                    File.Delete(file);
                    var sidecar = Path.ChangeExtension(file, ".json");
                    if (File.Exists(sidecar)) File.Delete(sidecar);
                }
            }
        }

        private string BuildOutputPath(string title, int? year, Configuration.PluginConfiguration config)
        {
            var safeName = string.Concat(title.Split(Path.GetInvalidFileNameChars())).Trim();
            var yearPart = year.HasValue ? $" ({year.Value})" : string.Empty;
            return Path.Combine(config.DownloadFolder, $"{safeName}{yearPart}.mp4");
        }
    }
}
