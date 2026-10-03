using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Services
{
    public record TmdbVideo(string Key, string Name, string Language, bool Official, int Size);

    [Flags]
    public enum TmdbMovieSource
    {
        None = 0,
        InTheatres = 1,
        ComingSoon = 2,
        Popular = 4,
        TopRated = 8,
    }

    public record TmdbMovieResult(
        int Id,
        string Title,
        string ReleaseDate,
        IReadOnlyList<int> GenreIds,
        double Popularity,
        int VoteCount,
        double VoteAverage,
        TmdbMovieSource Sources)
    {
        public int? Year => DateTime.TryParse(ReleaseDate, out var d) ? d.Year : (int?)null;

        public double GetEffectivePopularity(double comingSoonMultiplier) =>
            Sources.HasFlag(TmdbMovieSource.ComingSoon)
                ? Popularity * Math.Max(0, comingSoonMultiplier)
                : Popularity;
    }

    public record TmdbMovieDetails(
        long? Budget,
        int? Runtime,
        bool HasCollection = false,
        DateOnly? PrimaryReleaseDate = null,
        DateOnly? FirstRegionalTheatricalDate = null,
        DateOnly? RegionLimitedReleaseDate = null,
        DateOnly? RegionWideReleaseDate = null);

    public class TmdbService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<TmdbService> _logger;
        private const string BaseUrl = "https://api.themoviedb.org/3";

        public TmdbService(ILogger<TmdbService> logger)
        {
            _logger = logger;

            // Force IPv4 to avoid ~80s delay when IPv6 is unreachable (Happy Eyeballs fallback).
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (ctx, ct) =>
                {
                    var entry = await Dns.GetHostEntryAsync(ctx.DnsEndPoint.Host, AddressFamily.InterNetwork, ct).ConfigureAwait(false);
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    socket.NoDelay = true;
                    try
                    {
                        await socket.ConnectAsync(entry.AddressList[0], ctx.DnsEndPoint.Port, ct).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            };
            _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        }

        public void Dispose() => _httpClient.Dispose();

        internal TmdbService(ILogger<TmdbService> logger, HttpClient httpClient)
        {
            _logger = logger;
            _httpClient = httpClient;
        }

        /// <summary>
        /// Gets movie details used by ranking and pool eligibility. The overload without
        /// a region is retained for callers/tests that only need budget/runtime.
        /// </summary>
        public Task<TmdbMovieDetails?> GetMovieDetailsAsync(
            string tmdbId,
            string apiKey,
            CancellationToken ct) =>
            GetMovieDetailsAsync(tmdbId, apiKey, "US", ct);

        public async Task<TmdbMovieDetails?> GetMovieDetailsAsync(
            string tmdbId,
            string apiKey,
            string region,
            CancellationToken ct)
        {
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"{BaseUrl}/movie/{tmdbId}?append_to_response=release_dates");
                ApplyAuth(request, apiKey);
                using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

                long? budget = null;
                if (doc.RootElement.TryGetProperty("budget", out var budgetValue)
                    && budgetValue.ValueKind == JsonValueKind.Number
                    && budgetValue.TryGetInt64(out var parsedBudget))
                {
                    budget = parsedBudget;
                }

                int? runtime = null;
                if (doc.RootElement.TryGetProperty("runtime", out var runtimeValue)
                    && runtimeValue.ValueKind == JsonValueKind.Number
                    && runtimeValue.TryGetInt32(out var parsedRuntime))
                {
                    runtime = parsedRuntime;
                }

                var hasCollection = doc.RootElement.TryGetProperty("belongs_to_collection", out var collection)
                    && collection.ValueKind == JsonValueKind.Object;

                DateOnly? primaryReleaseDate = null;
                if (doc.RootElement.TryGetProperty("release_date", out var primaryRelease)
                    && primaryRelease.ValueKind == JsonValueKind.String)
                {
                    primaryReleaseDate = ParseDateOnly(primaryRelease.GetString());
                }

                var regional = ParseRegionalTheatricalDates(
                    doc.RootElement,
                    NormalizeRegion(region));

                return new TmdbMovieDetails(
                    budget,
                    runtime,
                    hasCollection,
                    primaryReleaseDate,
                    regional.FirstTheatrical,
                    regional.Limited,
                    regional.Wide);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "|Trailers4Jellyfin| Failed to fetch movie details for TMDB ID {Id}; allowing unknown details", tmdbId);
                return null;
            }
        }

        /// <summary>
        /// Enriches one discovered TMDB movie with the regional release information and
        /// details required by TrailerRankingService. A failed detail lookup intentionally
        /// falls back to the list release date so a transient TMDB failure does not remove
        /// an otherwise usable title from the pool.
        /// </summary>
        internal async Task<TrailerRankingCandidate> GetRankingCandidateAsync(
            TmdbMovieResult movie,
            Configuration.PluginConfiguration config,
            CancellationToken ct)
        {
            var details = await GetMovieDetailsAsync(
                movie.Id.ToString(CultureInfo.InvariantCulture),
                config.TmdbApiKey,
                config.TheatricalRegion,
                ct).ConfigureAwait(false);

            var fallbackReleaseDate = details?.PrimaryReleaseDate ?? ParseDateOnly(movie.ReleaseDate);
            return new TrailerRankingCandidate(
                movie,
                details?.FirstRegionalTheatricalDate ?? fallbackReleaseDate,
                details?.RegionLimitedReleaseDate,
                details?.RegionWideReleaseDate,
                details?.Budget,
                details?.Runtime,
                details?.HasCollection == true);
        }

        internal static bool MeetsMovieDetailsRequirements(
            TmdbMovieDetails? details,
            long minimumBudget,
            long budgetMetadataFloor,
            int minimumRuntimeMinutes)
        {
            if (details is null) return true;

            if (minimumBudget > 0
                && details.Budget is long budget
                && budget > 0
                && budget >= Math.Max(0, budgetMetadataFloor)
                && budget < minimumBudget)
            {
                return false;
            }

            return minimumRuntimeMinutes <= 0
                || details.Runtime is not int runtime
                || runtime <= 0
                || runtime >= minimumRuntimeMinutes;
        }

        // JWT Read Access Tokens start with "eyJ"; v3 short keys (32 hex chars) use ?api_key=.
        private static void ApplyAuth(HttpRequestMessage request, string apiKey)
        {
            if (apiKey.StartsWith("eyJ", StringComparison.Ordinal))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }
            else
            {
                var uri = request.RequestUri!.ToString();
                var separator = uri.Contains('?') ? "&" : "?";
                request.RequestUri = new Uri($"{uri}{separator}api_key={apiKey}");
            }
        }

        /// <summary>
        /// Returns a map of TMDB genre ID → genre name (e.g. 28 → "Action").
        /// </summary>
        public async Task<Dictionary<int, string>> GetGenreMapAsync(string apiKey, CancellationToken ct)
        {
            try
            {
                var url = $"{BaseUrl}/genre/movie/list?language=en-US";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                ApplyAuth(request, apiKey);
                using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var map = new Dictionary<int, string>();
                foreach (var genre in doc.RootElement.GetProperty("genres").EnumerateArray())
                    map[genre.GetProperty("id").GetInt32()] = genre.GetProperty("name").GetString() ?? string.Empty;
                return map;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "|Trailers4Jellyfin| Failed to fetch genre map from TMDB");
                return new Dictionary<int, string>();
            }
        }

        /// <summary>
        /// Fetches, filters and merges the enabled TMDB sources. Discover filters are applied
        /// server-side before pagination; In Theatres keeps TMDB's dedicated Now Playing endpoint
        /// and applies its vote/rating/primary-date filters locally.
        /// </summary>
        public async Task<List<TmdbMovieResult>> GetCandidateMoviesAsync(
            Configuration.PluginConfiguration config,
            CancellationToken ct)
        {
            var today = DateTime.UtcNow.Date;
            var region = NormalizeRegion(config.TheatricalRegion);

            DateTime? releasedAfter = config.ReleaseDateRangeMonths > 0
                ? today.AddMonths(-config.ReleaseDateRangeMonths)
                : null;
            DateTime? comingSoonThrough = config.UpcomingReleaseDateRangeMonths > 0
                ? today.AddMonths(config.UpcomingReleaseDateRangeMonths)
                : null;

            var all = new List<TmdbMovieResult>();

            if (config.SourceNowPlaying)
            {
                all.AddRange(await FetchNowPlayingPagesAsync(
                    config.TmdbApiKey,
                    region,
                    config.InTheatresMinimumVotes,
                    config.InTheatresMinimumRating,
                    config.MaxPagesPerSource,
                    ct).ConfigureAwait(false));
            }

            if (config.SourceUpcoming)
            {
                all.AddRange(await FetchDiscoverPagesAsync(
                    config.TmdbApiKey,
                    region,
                    true,
                    today,
                    comingSoonThrough,
                    config.ComingSoonMinimumVotes,
                    null,
                    "popularity.desc",
                    TmdbMovieSource.ComingSoon,
                    config.MaxPagesPerSource,
                    ct).ConfigureAwait(false));
            }

            if (config.SourcePopular)
            {
                all.AddRange(await FetchDiscoverPagesAsync(
                    config.TmdbApiKey,
                    region,
                    false,
                    releasedAfter,
                    today,
                    config.PopularMinimumVotes,
                    config.PopularMinimumRating,
                    "popularity.desc",
                    TmdbMovieSource.Popular,
                    config.MaxPagesPerSource,
                    ct).ConfigureAwait(false));
            }

            if (config.SourceTopRated)
            {
                all.AddRange(await FetchDiscoverPagesAsync(
                    config.TmdbApiKey,
                    region,
                    false,
                    releasedAfter,
                    today,
                    config.TopRatedMinimumVotes,
                    null,
                    "vote_average.desc",
                    TmdbMovieSource.TopRated,
                    config.MaxPagesPerSource,
                    ct).ConfigureAwait(false));
            }

            var merged = new Dictionary<int, TmdbMovieResult>();
            foreach (var movie in all)
            {
                if (merged.TryGetValue(movie.Id, out var existing))
                    merged[movie.Id] = existing with { Sources = existing.Sources | movie.Sources };
                else
                    merged[movie.Id] = movie;
            }

            return merged.Values
                .OrderByDescending(m => m.GetEffectivePopularity(config.ComingSoonPopularityMultiplier))
                .ThenByDescending(m => m.VoteCount)
                .ThenBy(m => m.Id)
                .ToList();
        }

        private async Task<List<TmdbMovieResult>> FetchNowPlayingPagesAsync(
            string apiKey,
            string region,
            int minimumVotes,
            double minimumRating,
            int maxPages,
            CancellationToken ct)
        {
            var results = new List<TmdbMovieResult>();

            for (int page = 1; page <= maxPages; page++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var url = $"{BaseUrl}/movie/now_playing?language=en-US"
                        + $"&region={Uri.EscapeDataString(region)}&page={page}";
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    ApplyAuth(request, apiKey);
                    using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

                    foreach (var item in doc.RootElement.GetProperty("results").EnumerateArray())
                    {
                        var movie = ParseMovie(item, TmdbMovieSource.InTheatres);
                        if (movie.VoteCount < minimumVotes
                            || movie.VoteAverage < minimumRating)
                        {
                            continue;
                        }

                        results.Add(movie);
                    }

                    if (page >= doc.RootElement.GetProperty("total_pages").GetInt32())
                        break;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "|Trailers4Jellyfin| Failed to fetch In Theatres page {Page}", page);
                    break;
                }
            }

            return results;
        }

        private async Task<List<TmdbMovieResult>> FetchDiscoverPagesAsync(
            string apiKey,
            string region,
            bool regionalTheatricalDates,
            DateTime? releasedAfter,
            DateTime? releasedThrough,
            int minimumVotes,
            double? minimumRating,
            string sortBy,
            TmdbMovieSource source,
            int maxPages,
            CancellationToken ct)
        {
            var results = new List<TmdbMovieResult>();

            for (int page = 1; page <= maxPages; page++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var url = $"{BaseUrl}/discover/movie?language=en-US&include_adult=false&include_video=false"
                        + $"&vote_count.gte={Math.Max(0, minimumVotes)}"
                        + $"&sort_by={sortBy}&page={page}";

                    if (regionalTheatricalDates)
                    {
                        // TMDB's release_date filters honor region. Type 2 = limited
                        // theatrical and type 3 = theatrical. This fixes upcoming movies
                        // whose primary release date belongs to another territory/year.
                        url += $"&region={Uri.EscapeDataString(region)}&with_release_type=2%7C3";
                        if (releasedAfter.HasValue)
                            url += $"&release_date.gte={releasedAfter.Value:yyyy-MM-dd}";
                        if (releasedThrough.HasValue)
                            url += $"&release_date.lte={releasedThrough.Value:yyyy-MM-dd}";
                    }
                    else
                    {
                        // Keep Popular/Top Rated broad enough to retain major recent
                        // streaming/non-theatrical movies. Lifecycle scoring later prefers
                        // the first regional theatrical date when one exists.
                        if (releasedAfter.HasValue)
                            url += $"&primary_release_date.gte={releasedAfter.Value:yyyy-MM-dd}";
                        if (releasedThrough.HasValue)
                            url += $"&primary_release_date.lte={releasedThrough.Value:yyyy-MM-dd}";
                    }

                    if (minimumRating.HasValue)
                        url += $"&vote_average.gte={Math.Max(0, minimumRating.Value):0.0}";

                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    ApplyAuth(request, apiKey);
                    using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

                    foreach (var item in doc.RootElement.GetProperty("results").EnumerateArray())
                        results.Add(ParseMovie(item, source));

                    if (page >= doc.RootElement.GetProperty("total_pages").GetInt32())
                        break;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "|Trailers4Jellyfin| Failed to fetch TMDB source {Source} page {Page}", source, page);
                    break;
                }
            }

            return results;
        }

        private static TmdbMovieResult ParseMovie(JsonElement movie, TmdbMovieSource source)
        {
            var genreIds = new List<int>();
            if (movie.TryGetProperty("genre_ids", out var gids) && gids.ValueKind == JsonValueKind.Array)
            {
                foreach (var gid in gids.EnumerateArray())
                    if (gid.TryGetInt32(out var parsed)) genreIds.Add(parsed);
            }

            return new TmdbMovieResult(
                movie.GetProperty("id").GetInt32(),
                movie.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty,
                movie.TryGetProperty("release_date", out var release) ? release.GetString() ?? string.Empty : string.Empty,
                genreIds,
                movie.TryGetProperty("popularity", out var popularity) && popularity.TryGetDouble(out var p) ? p : 0,
                movie.TryGetProperty("vote_count", out var votes) && votes.TryGetInt32(out var vc) ? vc : 0,
                movie.TryGetProperty("vote_average", out var rating) && rating.TryGetDouble(out var va) ? va : 0,
                source);
        }

        internal static string NormalizeRegion(string? region)
        {
            var value = region?.Trim().ToUpperInvariant();
            return value is { Length: 2 }
                && value.All(static c => c is >= 'A' and <= 'Z')
                    ? value
                    : "US";
        }

        private static DateOnly? ParseDateOnly(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var datePart = value.Length >= 10 ? value[..10] : value;
            return DateOnly.TryParseExact(
                datePart,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed)
                ? parsed
                : null;
        }

        internal static RegionalTheatricalDates ParseRegionalTheatricalDates(
            JsonElement movieRoot,
            string region)
        {
            DateOnly? limited = null;
            DateOnly? wide = null;

            if (!movieRoot.TryGetProperty("release_dates", out var releaseDates)
                || releaseDates.ValueKind != JsonValueKind.Object
                || !releaseDates.TryGetProperty("results", out var countries)
                || countries.ValueKind != JsonValueKind.Array)
            {
                return new RegionalTheatricalDates(null, null, null);
            }

            foreach (var country in countries.EnumerateArray())
            {
                if (!country.TryGetProperty("iso_3166_1", out var countryCode)
                    || !string.Equals(countryCode.GetString(), region, StringComparison.OrdinalIgnoreCase)
                    || !country.TryGetProperty("release_dates", out var dates)
                    || dates.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var release in dates.EnumerateArray())
                {
                    if (!release.TryGetProperty("type", out var typeElement)
                        || !typeElement.TryGetInt32(out var type)
                        || !release.TryGetProperty("release_date", out var dateElement)
                        || dateElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var date = ParseDateOnly(dateElement.GetString());
                    if (!date.HasValue)
                        continue;

                    if (type == 2 && (!limited.HasValue || date.Value < limited.Value))
                        limited = date;
                    else if (type == 3 && (!wide.HasValue || date.Value < wide.Value))
                        wide = date;
                }

                break;
            }

            DateOnly? first = limited switch
            {
                null => wide,
                _ when wide is null => limited,
                _ => limited.Value <= wide.Value ? limited : wide,
            };

            return new RegionalTheatricalDates(first, limited, wide);
        }

        internal sealed record RegionalTheatricalDates(
            DateOnly? FirstTheatrical,
            DateOnly? Limited,
            DateOnly? Wide);

        private static bool TryParseReleaseDate(string value, out DateTime date)
        {
            if (DateTime.TryParse(value, out var parsed))
            {
                date = parsed.Date;
                return true;
            }

            date = default;
            return false;
        }

        /// <summary>Returns all regional movie certifications in release-type priority order.
        /// Null means lookup failed; an empty list means TMDB has no certifications.</summary>
        public async Task<List<TrailerCertification>?> GetCertificationsAsync(
            string tmdbId, string apiKey, CancellationToken ct)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/movie/{tmdbId}/release_dates");
                ApplyAuth(request, apiKey);
                using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                return ParseCertifications(doc.RootElement);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "|Trailers4Jellyfin| Failed to fetch parental ratings for TMDB ID {Id}", tmdbId);
                return null;
            }
        }

        internal static List<TrailerCertification> ParseCertifications(JsonElement root)
        {
            return root.GetProperty("results").EnumerateArray()
                .Where(c => !string.IsNullOrWhiteSpace(c.GetProperty("iso_3166_1").GetString()))
                .SelectMany(c => c.GetProperty("release_dates").EnumerateArray()
                    .OrderBy(d => d.GetProperty("type").GetInt32() switch
                    {
                        3 => 0, // Theatrical
                        2 => 1, // Limited theatrical
                        4 => 2, // Digital
                        6 => 3, // TV
                        5 => 4, // Physical
                        _ => 5,
                    })
                    .Select(d => new TrailerCertification(
                        c.GetProperty("iso_3166_1").GetString()!.Trim().ToUpperInvariant(),
                        d.GetProperty("certification").GetString()?.Trim() ?? string.Empty)))
                .Where(c => !string.IsNullOrWhiteSpace(c.Rating))
                .Distinct()
                .ToList();
        }

        public async Task<string?> SearchMovieAsync(string title, int? year, string apiKey, CancellationToken ct)
        {
            try
            {
                var url = $"{BaseUrl}/search/movie?query={Uri.EscapeDataString(title)}&language=en-US";
                if (year.HasValue) url += $"&year={year.Value}";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                ApplyAuth(request, apiKey);
                using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var res = doc.RootElement.GetProperty("results");
                if (res.GetArrayLength() > 0)
                    return res[0].GetProperty("id").GetInt32().ToString();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "|Trailers4Jellyfin| TMDB search failed for '{Title}'", title);
            }
            return null;
        }

        // All ISO 639-1 codes exposed in the UI. Used to request non-English trailers from TMDB
        // when no specific language filter is set (TMDB defaults to English-only without this).
        private const string AllSupportedLanguageCodes = "en,es,fr,de,it,pt,nl,ru,pl,sv,no,da,ja,ko,zh,ar,hi,tr,th,id,null";

        public async Task<List<TmdbVideo>> GetTrailersAsync(
            string tmdbId,
            string apiKey,
            IReadOnlySet<string>? allowedLanguages,
            CancellationToken ct)
        {
            try
            {
                // include_video_language tells TMDB to return videos beyond its en-US default.
                // Without it, only English trailers are returned regardless of iso_639_1 filtering.
                var includeLangs = (allowedLanguages != null && allowedLanguages.Count > 0)
                    ? string.Join(",", allowedLanguages)
                    : AllSupportedLanguageCodes;

                var url = $"{BaseUrl}/movie/{tmdbId}/videos?include_video_language={includeLangs}";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                ApplyAuth(request, apiKey);
                using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);

                var videos = new List<TmdbVideo>();
                foreach (var result in doc.RootElement.GetProperty("results").EnumerateArray())
                {
                    var type = result.GetProperty("type").GetString();
                    var site = result.GetProperty("site").GetString();
                    if (!string.Equals(type, "Trailer", StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(site, "YouTube", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var key = result.GetProperty("key").GetString();
                    if (string.IsNullOrEmpty(key)) continue;

                    var lang = result.TryGetProperty("iso_639_1", out var l) ? (l.GetString() ?? string.Empty) : string.Empty;

                    if (allowedLanguages != null && allowedLanguages.Count > 0 && !allowedLanguages.Contains(lang))
                        continue;

                    videos.Add(new TmdbVideo(
                        key,
                        result.GetProperty("name").GetString() ?? "Trailer",
                        lang,
                        result.GetProperty("official").GetBoolean(),
                        result.GetProperty("size").GetInt32()));
                }

                return videos
                    .OrderByDescending(v => v.Official)
                    .ThenByDescending(v => v.Size)
                    .ToList();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "|Trailers4Jellyfin| GetTrailers failed for TMDB ID {Id}", tmdbId);
                return new List<TmdbVideo>();
            }
        }
    }
}
