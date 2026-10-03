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
        public string Description => "Builds and reconciles a ranked TMDB trailer pool for Jellyfin Cinema Mode.";
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
            CleanupPartialDownloads(config.DownloadFolder);

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

            if (config.DeleteWatchedTrailers)
            {
                DeleteWatchedTrailers(config, registeredTrailers);
                registeredTrailers = _assetRegistry.SyncDownloadedTrailers(config.DownloadFolder);
            }

            progress.Report(5);

            var libraryTmdbIds = config.SkipMoviesInLibrary
                ? GetLibraryTmdbIds()
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (config.SkipMoviesInLibrary)
                _logger.LogInformation("|Trailers4Jellyfin| Library contains {Count} movies with TMDB IDs (will skip these)", libraryTmdbIds.Count);

            var allowedLanguages = string.IsNullOrWhiteSpace(config.AllowedLanguages)
                ? null
                : new HashSet<string>(
                    config.AllowedLanguages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    StringComparer.OrdinalIgnoreCase) as IReadOnlySet<string>;

            progress.Report(10);
            _logger.LogInformation("|Trailers4Jellyfin| Fetching ranked candidates from TMDB...");
            var candidates = await _tmdbService.GetCandidateMoviesAsync(config, cancellationToken).ConfigureAwait(false);

            if (config.SkipMoviesInLibrary)
            {
                candidates = candidates
                    .Where(m => !libraryTmdbIds.Contains(m.Id.ToString()))
                    .ToList();
            }

            _logger.LogInformation("|Trailers4Jellyfin| {Count} ranked candidates remain after source/library filters", candidates.Count);

            if (candidates.Count == 0)
            {
                _logger.LogInformation("|Trailers4Jellyfin| No eligible candidates found. All done.");
                progress.Report(100);
                return;
            }

            // Budget and runtime live on /movie/{id}, not list/discover responses.
            // Walk the already-ranked list only until the desired pool is full.
            int desiredLimit = config.MaxTotalTrailers > 0 ? config.MaxTotalTrailers : candidates.Count;
            var desired = new List<TmdbMovieResult>(Math.Min(desiredLimit, candidates.Count));

            foreach (var movie in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var details = await _tmdbService
                    .GetMovieDetailsAsync(movie.Id.ToString(), config.TmdbApiKey, cancellationToken)
                    .ConfigureAwait(false);

                if (!TmdbService.MeetsMovieDetailsRequirements(
                    details,
                    config.MinimumMovieBudget,
                    config.BudgetMetadataFloor,
                    config.MinimumMovieRuntimeMinutes))
                {
                    if (details?.Budget is long budget
                        && config.MinimumMovieBudget > 0
                        && budget > 0
                        && budget >= Math.Max(0, config.BudgetMetadataFloor)
                        && budget < config.MinimumMovieBudget)
                    {
                        _logger.LogInformation(
                            "|Trailers4Jellyfin| Skipping '{Title}': reliable budget {Budget} USD is below {Minimum} USD",
                            movie.Title, budget, config.MinimumMovieBudget);
                    }
                    else
                    {
                        _logger.LogInformation(
                            "|Trailers4Jellyfin| Skipping '{Title}': runtime {Runtime} minutes is below minimum {Minimum} minutes",
                            movie.Title, details?.Runtime ?? 0, config.MinimumMovieRuntimeMinutes);
                    }

                    continue;
                }

                desired.Add(movie);
                if (desired.Count >= desiredLimit)
                    break;
            }

            if (desired.Count == 0)
            {
                _logger.LogInformation("|Trailers4Jellyfin| No candidates remain after budget/runtime filters.");
                progress.Report(100);
                return;
            }

            _logger.LogInformation(
                "|Trailers4Jellyfin| Current desired pool contains {Count} ranked movie(s) (target {Target})",
                desired.Count, config.MaxTotalTrailers);

            var genreMap = await _tmdbService.GetGenreMapAsync(config.TmdbApiKey, cancellationToken).ConfigureAwait(false);
            var existingByTmdbId = BuildExistingTrailerIndex(config.DownloadFolder);

            progress.Report(25);

            int downloaded = 0;
            int processed = 0;

            foreach (var movie in desired)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (downloaded >= config.MaxTrailersToDownload)
                    break;

                double taskProgress = 25 + (65.0 * processed / desired.Count);
                progress.Report(taskProgress);
                processed++;

                var outputPath = BuildOutputPath(movie.Title, movie.Year, config);
                string? existingPath = existingByTmdbId.TryGetValue(movie.Id, out var indexedPath)
                    ? indexedPath
                    : File.Exists(outputPath) ? outputPath : null;

                if (config.SkipAlreadyDownloaded && existingPath != null)
                {
                    await EnsureTrailerMetadataAsync(
                        existingPath,
                        movie,
                        genreMap,
                        config.TmdbApiKey,
                        cancellationToken).ConfigureAwait(false);
                    existingByTmdbId[movie.Id] = existingPath;
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
                    existingByTmdbId[movie.Id] = outputPath;
                    _logger.LogInformation(
                        "|Trailers4Jellyfin| [{Done}/{Max}] Saved trailer for '{Movie}' → {Path}",
                        downloaded, config.MaxTrailersToDownload, movie.Title, outputPath);
                }
            }

            // Reconcile only after replacements are downloaded. Out-of-target and legacy files
            // age out as new desired trailers arrive instead of shrinking a full pool immediately.
            _assetRegistry.SyncDownloadedTrailers(config.DownloadFolder);
            ReconcileRankedPool(config, desired);
            _assetRegistry.SyncDownloadedTrailers(config.DownloadFolder);

            _logger.LogInformation(
                "|Trailers4Jellyfin| Task complete. Downloaded {Count} trailer(s); desired pool size {Desired}.",
                downloaded, desired.Count);
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

        private void CleanupPartialDownloads(string downloadFolder)
        {
            foreach (var partial in Directory.EnumerateFiles(downloadFolder)
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
        }

        private void DeleteWatchedTrailers(
            Configuration.PluginConfiguration config,
            IReadOnlyList<Video> registeredTrailers)
        {
            var trailerItemsByPath = registeredTrailers
                .Where(t => !string.IsNullOrWhiteSpace(t.Path))
                .ToDictionary(t => Path.GetFullPath(t.Path!), CinemaAssetRegistry.PathComparer);

            var users = _userManager.GetUsers().ToList();
            foreach (var file in GetTrailerFiles(config.DownloadFolder))
            {
                if (!trailerItemsByPath.TryGetValue(Path.GetFullPath(file), out var item)) continue;
                bool watched = users.Any(u => _userDataManager.GetUserData(u, item)?.Played == true);
                if (!watched) continue;

                _logger.LogInformation("|Trailers4Jellyfin| Deleting watched trailer: {File}", Path.GetFileName(file));
                DeleteTrailerAndSidecar(file);
            }
        }

        private Dictionary<int, string> BuildExistingTrailerIndex(string downloadFolder)
        {
            var result = new Dictionary<int, string>();
            foreach (var file in GetTrailerFiles(downloadFolder).OrderByDescending(File.GetLastWriteTimeUtc))
            {
                var tmdbId = ReadTrailerTmdbId(file);
                if (tmdbId is int id && !result.ContainsKey(id))
                    result[id] = file;
            }

            return result;
        }

        private void ReconcileRankedPool(
            Configuration.PluginConfiguration config,
            IReadOnlyList<TmdbMovieResult> desired)
        {
            if (config.MaxTotalTrailers <= 0)
                return;

            var files = GetTrailerFiles(config.DownloadFolder);
            int excess = files.Count - config.MaxTotalTrailers;
            if (excess <= 0)
                return;

            var desiredIds = desired.Select(m => m.Id).ToHashSet();
            var retainedDesiredIds = new HashSet<int>();
            var retireable = new List<string>();

            // Keep one (newest) file for every desired TMDB movie ID. Known out-of-target,
            // duplicate and legacy/unidentified files are retired oldest-first as replacements arrive.
            foreach (var file in files.OrderByDescending(File.GetLastWriteTimeUtc))
            {
                var tmdbId = ReadTrailerTmdbId(file);
                if (tmdbId is int id && desiredIds.Contains(id) && retainedDesiredIds.Add(id))
                    continue;

                retireable.Add(file);
            }

            foreach (var file in retireable.OrderBy(File.GetCreationTimeUtc).Take(excess))
            {
                _logger.LogInformation(
                    "|Trailers4Jellyfin| Retiring trailer outside current ranked pool: {File}",
                    Path.GetFileName(file));
                DeleteTrailerAndSidecar(file);
            }
        }

        private static List<string> GetTrailerFiles(string downloadFolder) =>
            Directory.GetFiles(downloadFolder, "*.mp4")
                .Where(f => !Path.GetFileName(f).StartsWith("._", StringComparison.Ordinal))
                .Where(f => !TrailerDownloadService.IsPartialDownloadArtifact(f))
                .ToList();

        private static int? ReadTrailerTmdbId(string trailerPath)
        {
            var sidecarPath = Path.ChangeExtension(trailerPath, ".json");
            if (!File.Exists(sidecarPath))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(sidecarPath));
                if (!doc.RootElement.TryGetProperty("tmdbId", out var value))
                    return null;

                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numericId) && numericId > 0)
                    return numericId;

                if (value.ValueKind == JsonValueKind.String
                    && int.TryParse(value.GetString()?.Trim(), out var stringId)
                    && stringId > 0)
                {
                    return stringId;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Legacy or unreadable sidecars are intentionally treated as unidentified.
            }

            return null;
        }

        private static void DeleteTrailerAndSidecar(string file)
        {
            File.Delete(file);
            var sidecar = Path.ChangeExtension(file, ".json");
            if (File.Exists(sidecar)) File.Delete(sidecar);
        }

        private string BuildOutputPath(string title, int? year, Configuration.PluginConfiguration config)
        {
            var safeName = string.Concat(title.Split(Path.GetInvalidFileNameChars())).Trim();
            var yearPart = year.HasValue ? $" ({year.Value})" : string.Empty;
            return Path.Combine(config.DownloadFolder, $"{safeName}{yearPart}.mp4");
        }
    }
}
