using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        // ── TMDB ──────────────────────────────────────────────────────────────

        public string TmdbApiKey { get; set; } = string.Empty;

        // ── Sources ───────────────────────────────────────────────────────────

        public bool SourceNowPlaying { get; set; } = true;
        public bool SourceUpcoming { get; set; } = true;
        public bool SourcePopular { get; set; } = true;
        public bool SourceTopRated { get; set; } = true;

        public int InTheatresMinimumVotes { get; set; } = 50;
        public double InTheatresMinimumRating { get; set; } = 6.5;
        public int ComingSoonMinimumVotes { get; set; } = 0;
        public double ComingSoonPopularityMultiplier { get; set; } = 3.0;
        public int PopularMinimumVotes { get; set; } = 100;
        public double PopularMinimumRating { get; set; } = 6.0;
        public int TopRatedMinimumVotes { get; set; } = 500;

        // ── Date Range ────────────────────────────────────────────────────────

        /// <summary>Maximum age, in months, for released movies. 0 disables the past-date limit.</summary>
        public int ReleaseDateRangeMonths { get; set; } = 12;

        /// <summary>How many months ahead Coming Soon searches by primary release date.</summary>
        public int UpcomingReleaseDateRangeMonths { get; set; } = 6;

        // ── Download Settings ─────────────────────────────────────────────────

        public string DownloadFolder { get; set; } = string.Empty;
        public int MaxTrailersToDownload { get; set; } = 20;
        public int MaxPagesPerSource { get; set; } = 3;
        /// <summary>Minimum reliable known movie budget in USD. 0 disables filtering.</summary>
        public long MinimumMovieBudget { get; set; } = 10_000_000;

        /// <summary>Positive TMDB budgets below this value are treated as unreliable/unknown metadata.</summary>
        public long BudgetMetadataFloor { get; set; } = 1_000;

        /// <summary>Minimum known runtime in minutes. 0 disables filtering; missing/zero runtimes are allowed.</summary>
        public int MinimumMovieRuntimeMinutes { get; set; } = 45;

        public int PreferredVideoHeight { get; set; } = 720;
        public bool SkipAlreadyDownloaded { get; set; } = true;
        public bool SkipMoviesInLibrary { get; set; } = false;
        public string YtDlpPath { get; set; } = string.Empty;

        /// <summary>Path to ffmpeg binary. Passed to yt-dlp via --ffmpeg-location for audio/video merging.</summary>
        public string FfmpegPath { get; set; } = string.Empty;

        // ── Cinema Mode ───────────────────────────────────────────────────────

        public bool EnableCinemaMode { get; set; } = true;
        public string TrailerPreRollFolder { get; set; } = string.Empty;
        public bool PreferUnwatchedTrailerPreRolls { get; set; } = true;
        public int NumberOfTrailers { get; set; } = 2;
        public bool PreferUnwatchedTrailers { get; set; } = true;
        public bool EnableGenreMatching { get; set; } = true;
        public bool SkipWatchedMovieTrailers { get; set; } = true;
        public bool SkipCurrentMovieTrailers { get; set; } = true;
        public string FeaturePreRollFolder { get; set; } = string.Empty;
        public bool PreferUnwatchedFeaturePreRolls { get; set; } = true;
        public string EpisodePreRollFolder { get; set; } = string.Empty;
        public bool PreferUnwatchedEpisodePreRolls { get; set; } = true;
        public int EpisodePreRollChancePercent { get; set; } = 75;
        public int EpisodePreRollCooldownMinutes { get; set; } = 60;
        public int EpisodePreRollMinEpisodes { get; set; } = 3;
        public int EpisodePreRollMaxPerWindow { get; set; } = 2;
        public int EpisodePreRollWindowHours { get; set; } = 4;

        // ── Languages ─────────────────────────────────────────────────────────

        /// <summary>Comma-separated ISO 639-1 codes. Empty = all languages allowed.</summary>
        public string AllowedLanguages { get; set; } = string.Empty;

        // ── Trailer Rotation ──────────────────────────────────────────────────

        /// <summary>Desired size of the current ranked trailer pool. 0 = unlimited.</summary>
        public int MaxTotalTrailers { get; set; } = 100;

        /// <summary>Delete trailers that any user has already watched, making room for fresh ones.</summary>
        public bool DeleteWatchedTrailers { get; set; } = false;

        // ── Advanced ──────────────────────────────────────────────────────────

        /// <summary>
        /// Path to a cookies.txt file (Netscape format) for YouTube authentication.
        /// Fixes VideoUnavailableException when YouTube blocks server-side requests.
        /// </summary>
        public string CookiesFilePath { get; set; } = string.Empty;
    }
}
