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
    internal sealed record DesiredTrailerCandidate(
        TmdbMovieResult Movie,
        string? ExistingPath,
        TmdbVideo? Trailer);

    internal static class RankedTrailerPoolSelector
    {
        public static async Task<List<DesiredTrailerCandidate>> SelectAsync(
            IReadOnlyList<TmdbMovieResult> rankedCandidates,
            int desiredLimit,
            Func<TmdbMovieResult, CancellationToken, Task<bool>> isMovieEligible,
            Func<TmdbMovieResult, string?> findExistingTrailer,
            Func<TmdbMovieResult, CancellationToken, Task<TmdbVideo?>> findTrailer,
            CancellationToken ct)
        {
            var desired = new List<DesiredTrailerCandidate>(Math.Min(desiredLimit, rankedCandidates.Count));

            foreach (var movie in rankedCandidates)
            {
                ct.ThrowIfCancellationRequested();

                if (!await isMovieEligible(movie, ct).ConfigureAwait(false))
                    continue;

                var existingPath = findExistingTrailer(movie);
                if (existingPath != null)
                {
                    desired.Add(new DesiredTrailerCandidate(movie, existingPath, null));
                }
                else
                {
                    var trailer = await findTrailer(movie, ct).ConfigureAwait(false);
                    if (trailer == null)
                        continue;

                    desired.Add(new DesiredTrailerCandidate(movie, null, trailer));
                }

                if (desired.Count >= desiredLimit)
                    break;
            }

            return desired;
        }
    }

    public class DownloadTrailersTask : IScheduledTask
    {
        private readonly ILogger<DownloadTrailersTask> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly TmdbService _tmdbService;
        private readonly TrailerDownloadService _downloadService;
        private readonly CinemaAssetRegistry _assetRegistry;
        private readonly TrailerRankingService _rankingService;
        private readonly TrailerRankingStore _rankingStore;

        public string Name => "Download TMDB Trailers";
        public string Key => "Trailers4JellyfinDownload";
        public string Description => "Builds and reconciles a ranked TMDB trailer pool for Jellyfin Cinema Mode.";
        public string Category => "Trailers4Jellyfin";

        public DownloadTrailersTask(
            ILogger<DownloadTrailersTask> logger,
            ILibraryManager libraryManager,
            TmdbService tmdbService,
            TrailerDownloadService downloadService,
            CinemaAssetRegistry assetRegistry,
            TrailerRankingService rankingService,
            TrailerRankingStore rankingStore)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _tmdbService = tmdbService;
            _downloadService = downloadService;
            _assetRegistry = assetRegistry;
            _rankingService = rankingService;
            _rankingStore = rankingStore;
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

            // Source methods deliberately tolerate individual TMDB failures. If every source
            // produced zero candidates, treat the run as incomplete and retain the last known
            // good manifest rather than atomically replacing it with an empty one.
            if (candidates.Count == 0)
            {
                _logger.LogWarning("|Trailers4Jellyfin| TMDB discovery returned no candidates; preserving the previous ranking manifest");
                progress.Report(100);
                return;
            }

            await RefreshTrailerPopularitiesAsync(
                registeredTrailers,
                candidates,
                cancellationToken).ConfigureAwait(false);

            if (config.SkipMoviesInLibrary)
            {
                candidates = candidates
                    .Where(m => !libraryTmdbIds.Contains(m.Id.ToString()))
                    .ToList();
            }

            _logger.LogInformation(
                "|Trailers4Jellyfin| {Count} candidates remain after source/library filters; evaluating {Mode} pool ranking",
                candidates.Count,
                config.PoolRankingMode);

            if (candidates.Count == 0)
            {
                await WriteRankingManifestAsync(
                    Array.Empty<TrailerRankingEvaluation>(),
                    Array.Empty<int>(),
                    config,
                    cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("|Trailers4Jellyfin| No eligible candidates found. All done.");
                progress.Report(100);
                return;
            }

            // Enrich all discovered candidates once. /movie/{id}?append_to_response=release_dates
            // supplies budget/runtime, collection membership, the first regional theatrical date,
            // and limited/wide dates used by LifecycleScore. This replaces the old per-candidate
            // details lookup inside the pool-selection callback.
            var rankingCandidates = new List<TrailerRankingCandidate>(candidates.Count);
            for (var index = 0; index < candidates.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rankingCandidate = await _tmdbService
                    .GetRankingCandidateAsync(candidates[index], config, cancellationToken)
                    .ConfigureAwait(false);
                rankingCandidates.Add(rankingCandidate);

                progress.Report(10 + (10.0 * (index + 1) / candidates.Count));
            }

            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var evaluations = _rankingService
                .EvaluateAll(rankingCandidates, config, today)
                .ToList();

            var orderedEvaluations = _rankingService
                .OrderForPool(evaluations, config)
                .ToList();

            // LifecycleScore already applies the configured lifecycle windows, budget/runtime,
            // and all score eligibility rules. Popularity mode preserves the legacy ranking
            // formula, but shares the same configured lifecycle windows and common filters.
            if (config.PoolRankingMode == Configuration.TrailerPoolRankingMode.Popularity)
            {
                orderedEvaluations = orderedEvaluations
                    .Where(e => TrailerRankingService.GetLifecycleWindowExclusionReason(
                        e.Candidate.LifecycleReleaseDate,
                        config,
                        today) is null)
                    .Where(e => TmdbService.MeetsMovieDetailsRequirements(
                        new TmdbMovieDetails(e.Candidate.Budget, e.Candidate.Runtime),
                        config.MinimumMovieBudget,
                        config.BudgetMetadataFloor,
                        config.MinimumMovieRuntimeMinutes))
                    .ToList();
            }

            var rankedCandidates = orderedEvaluations
                .Select(e => e.Candidate.Movie)
                .ToList();

            _logger.LogInformation(
                "|Trailers4Jellyfin| {Eligible} candidate(s) remain after {Mode} ranking/eligibility (from {Evaluated} evaluated)",
                rankedCandidates.Count,
                config.PoolRankingMode,
                evaluations.Count);

            if (rankedCandidates.Count == 0)
            {
                await WriteRankingManifestAsync(
                    evaluations,
                    Array.Empty<int>(),
                    config,
                    cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("|Trailers4Jellyfin| No candidates remain after ranking/eligibility filters.");
                progress.Report(100);
                return;
            }

            // Trailer availability lives on /movie/{id}/videos. Keep scanning below the
            // nominal top-N until the desired pool contains N movies that either already have
            // a local trailer or currently expose a suitable TMDB/YouTube trailer.
            int desiredLimit = config.MaxTotalTrailers > 0 ? config.MaxTotalTrailers : rankedCandidates.Count;
            var existingByTmdbId = BuildExistingTrailerIndex(config.DownloadFolder);

            var desired = await RankedTrailerPoolSelector.SelectAsync(
                rankedCandidates,
                desiredLimit,
                static (_, _) => Task.FromResult(true),
                movie =>
                {
                    if (existingByTmdbId.TryGetValue(movie.Id, out var indexedPath))
                        return indexedPath;

                    var legacyPath = BuildOutputPath(movie.Title, movie.Year, config);
                    return File.Exists(legacyPath) ? legacyPath : null;
                },
                async (movie, ct) =>
                {
                    var trailers = await _tmdbService.GetTrailersAsync(
                        movie.Id.ToString(), config.TmdbApiKey, allowedLanguages, ct).ConfigureAwait(false);

                    if (trailers.Count == 0)
                    {
                        _logger.LogDebug(
                            "|Trailers4Jellyfin| Skipping '{Title}' from desired pool: no suitable YouTube trailer on TMDB",
                            movie.Title);
                        return null;
                    }

                    return trailers[0];
                },
                cancellationToken).ConfigureAwait(false);

            if (desired.Count == 0)
            {
                _logger.LogWarning(
                    "|Trailers4Jellyfin| No trailer-capable candidates were resolved; preserving the previous ranking manifest");
                _logger.LogInformation("|Trailers4Jellyfin| No trailer-capable candidates remain after eligibility filters.");
                progress.Report(100);
                return;
            }

            foreach (var candidate in desired)
            {
                if (candidate.ExistingPath != null)
                    existingByTmdbId[candidate.Movie.Id] = candidate.ExistingPath;
            }

            _logger.LogInformation(
                "|Trailers4Jellyfin| Current desired pool contains {Count} trailer-capable movie(s) (target {Target})",
                desired.Count, config.MaxTotalTrailers);

            var genreMap = await _tmdbService.GetGenreMapAsync(config.TmdbApiKey, cancellationToken).ConfigureAwait(false);

            progress.Report(25);

            int attempted = 0;
            int downloaded = 0;
            int processed = 0;

            foreach (var candidate in desired)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (attempted >= config.MaxTrailersToDownload)
                    break;

                var movie = candidate.Movie;
                double taskProgress = 25 + (65.0 * processed / desired.Count);
                progress.Report(taskProgress);
                processed++;

                var outputPath = BuildOutputPath(movie.Title, movie.Year, config);
                var existingPath = candidate.ExistingPath;

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

                var trailer = candidate.Trailer;
                if (trailer == null)
                {
                    // This path is normally only used when SkipAlreadyDownloaded is disabled:
                    // an existing local trailer proved the movie usable during selection, but a
                    // fresh trailer is required because the user explicitly requested re-downloads.
                    var trailers = await _tmdbService.GetTrailersAsync(
                        movie.Id.ToString(), config.TmdbApiKey, allowedLanguages, cancellationToken).ConfigureAwait(false);

                    if (trailers.Count == 0)
                    {
                        _logger.LogDebug("|Trailers4Jellyfin| No replacement YouTube trailer on TMDB for '{Title}'", movie.Title);
                        continue;
                    }

                    trailer = trailers[0];
                }

                if (attempted > 0)
                {
                    var delaySeconds = Random.Shared.Next(3, 9);
                    _logger.LogInformation(
                        "|Trailers4Jellyfin| Waiting {DelaySeconds} second(s) before next YouTube trailer download",
                        delaySeconds);
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false);
                }

                attempted++;
                _logger.LogInformation(
                    "|Trailers4Jellyfin| [{Attempt}/{Max}] Downloading '{Trailer}' for '{Movie}'",
                    attempted, config.MaxTrailersToDownload, trailer.Name, movie.Title);

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
                        "|Trailers4Jellyfin| [{Attempt}/{Max}] Saved trailer for '{Movie}' ({Downloaded} successful) → {Path}",
                        attempted, config.MaxTrailersToDownload, movie.Title, downloaded, outputPath);
                }
            }

            // Reconcile only after replacements are downloaded. Out-of-target and legacy files
            // age out as new desired trailers arrive instead of shrinking a full pool immediately.
            _assetRegistry.SyncDownloadedTrailers(config.DownloadFolder);
            ReconcileRankedPool(config, desired.Select(x => x.Movie).ToList());
            _assetRegistry.SyncDownloadedTrailers(config.DownloadFolder);

            // Rewrite ranking state on every successful daily run even when no new trailer
            // was downloaded. PoolScore/PlaybackScore are date-sensitive, so the manifest
            // must advance with the release calendar independently of download activity.
            await WriteRankingManifestAsync(
                evaluations,
                desired.Select(x => x.Movie.Id).ToList(),
                config,
                cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "|Trailers4Jellyfin| Task complete. Attempted {Attempted} candidate(s), downloaded {Downloaded} trailer(s); trailer-capable desired pool size {Desired}.",
                attempted, downloaded, desired.Count);
            progress.Report(100);
        }

        private async Task WriteRankingManifestAsync(
            IReadOnlyList<TrailerRankingEvaluation> evaluations,
            IReadOnlyList<int> selectedPoolOrder,
            Configuration.PluginConfiguration config,
            CancellationToken ct)
        {
            var manifest = _rankingService.BuildManifest(
                evaluations,
                selectedPoolOrder,
                config,
                DateTimeOffset.UtcNow);

            await _rankingStore
                .WriteAsync(config.DownloadFolder, manifest, ct)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "|Trailers4Jellyfin| Updated {Manifest} ({Evaluated} evaluated, {Selected} selected)",
                TrailerRankingStore.FileName,
                evaluations.Count,
                selectedPoolOrder.Count);
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
                popularity = movie.Popularity,
                genres,
                certifications,
            });
            await TrailerMetadataRefresh.WriteAsync(sidecarPath, json, ct).ConfigureAwait(false);
        }

        private async Task RefreshTrailerPopularitiesAsync(
            IReadOnlyList<Video> registeredTrailers,
            IReadOnlyList<TmdbMovieResult> candidates,
            CancellationToken ct)
        {
            var popularityById = candidates.ToDictionary(m => m.Id, m => m.Popularity);

            foreach (var trailer in registeredTrailers)
            {
                ct.ThrowIfCancellationRequested();

                var tmdbId = ReadTrailerTmdbId(trailer.Path);
                if (tmdbId is not int id || !popularityById.TryGetValue(id, out var popularity))
                    continue;

                try
                {
                    await TrailerMetadataRefresh
                        .UpdatePopularityAsync(trailer.Path, popularity, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(
                        ex,
                        "|Trailers4Jellyfin| Could not refresh trailer popularity: {Path}",
                        trailer.Path);
                }
            }
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
