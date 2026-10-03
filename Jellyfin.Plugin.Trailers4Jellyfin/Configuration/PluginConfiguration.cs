using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Configuration
{
    public enum TrailerPoolRankingMode
    {
        LifecycleScore = 0,
        Popularity = 1,
    }

    public enum TrailerPlaybackRankingMode
    {
        Score = 0,
        Popularity = 1,
    }

    public class PluginConfiguration : BasePluginConfiguration
    {
        // ── Configuration migration ──────────────────────────────────────────

        /// <summary>Ranking-default migration version. Older configurations deserialize as 0.</summary>
        public int RankingSettingsVersion { get; set; } = 0;

        // ── TMDB ──────────────────────────────────────────────────────────────

        public string TmdbApiKey { get; set; } = string.Empty;

        /// <summary>ISO 3166-1 region used for theatrical release dates and lifecycle scoring.</summary>
        public string TheatricalRegion { get; set; } = "US";

        // ── Sources ───────────────────────────────────────────────────────────

        public bool SourceNowPlaying { get; set; } = true;
        public bool SourceUpcoming { get; set; } = true;
        public bool SourcePopular { get; set; } = true;
        public bool SourceTopRated { get; set; } = true;

        public int InTheatresMinimumVotes { get; set; } = 30;
        public double InTheatresMinimumRating { get; set; } = 6.5;
        public int ComingSoonMinimumVotes { get; set; } = 0;
        public double ComingSoonPopularityMultiplier { get; set; } = 3.0;
        public int PopularMinimumVotes { get; set; } = 100;
        public double PopularMinimumRating { get; set; } = 6.0;
        public int TopRatedMinimumVotes { get; set; } = 500;

        // ── Date Range ────────────────────────────────────────────────────────

        /// <summary>Maximum age, in months, for released movies. 0 disables the past-date limit.</summary>
        public int ReleaseDateRangeMonths { get; set; } = 12;

        /// <summary>How many months ahead Coming Soon searches.</summary>
        public int UpcomingReleaseDateRangeMonths { get; set; } = 6;

        // ── Download Settings ─────────────────────────────────────────────────

        public string DownloadFolder { get; set; } = string.Empty;
        public int MaxTrailersToDownload { get; set; } = 20;

        /// <summary>
        /// Minimum TMDB discovery pages per enabled source. Larger trailer-pool targets
        /// automatically expand discovery depth, up to 10 pages per source.
        /// </summary>
        public int MaxPagesPerSource { get; set; } = 4;

        /// <summary>Minimum reliable known movie budget in USD. 0 disables filtering.</summary>
        public long MinimumMovieBudget { get; set; } = 500_000;

        /// <summary>Positive TMDB budgets below this value are treated as unreliable/unknown metadata.</summary>
        public long BudgetMetadataFloor { get; set; } = 1_000;

        /// <summary>Minimum known runtime in minutes. 0 disables filtering; missing/zero runtimes are allowed.</summary>
        public int MinimumMovieRuntimeMinutes { get; set; } = 45;

        public int PreferredVideoHeight { get; set; } = 720;
        public bool SkipAlreadyDownloaded { get; set; } = true;

        /// <summary>Replace an existing trailer when the selector finds a strictly higher structural score.</summary>
        public bool UpgradeHigherStructuralScore { get; set; } = true;

        /// <summary>Replace an existing trailer with a newer video only when its structural score is unchanged.</summary>
        public bool UpgradeWhenNewerTrailerReleased { get; set; } = false;

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

        /// <summary>Legacy serialized setting retained for upgrade compatibility; no longer used for selection.</summary>
        public int EpisodePreRollChancePercent { get; set; } = 75;

        /// <summary>Chance that an eligible episode gets only a commercial/pre-roll clip.</summary>
        public int EpisodeCommercialOnlyChancePercent { get; set; } = 14;

        /// <summary>Chance that an eligible episode gets only one downloaded movie trailer.</summary>
        public int EpisodeMovieTrailerOnlyChancePercent { get; set; } = 6;

        /// <summary>Chance that an eligible episode gets a commercial followed by one movie trailer.</summary>
        public int EpisodeBothChancePercent { get; set; } = 0;

        public int EpisodePreRollCooldownMinutes { get; set; } = 60;
        public int EpisodePreRollMinEpisodes { get; set; } = 3;
        public int EpisodePreRollMaxPerWindow { get; set; } = 2;
        public int EpisodePreRollWindowHours { get; set; } = 4;

        // ── Languages ─────────────────────────────────────────────────────────

        /// <summary>Comma-separated ISO 639-1 codes. Empty = all languages allowed.</summary>
        public string AllowedLanguages { get; set; } = string.Empty;

        // ── Trailer Ranking ───────────────────────────────────────────────────

        /// <summary>Determines which movies are selected for the maintained trailer pool.</summary>
        public TrailerPoolRankingMode PoolRankingMode { get; set; } = TrailerPoolRankingMode.LifecycleScore;

        /// <summary>Determines how eligible trailers are prioritized during Cinema Mode playback.</summary>
        public TrailerPlaybackRankingMode PlaybackRankingMode { get; set; } = TrailerPlaybackRankingMode.Score;

        /// <summary>Add the release-proximity boost to PoolScore when score playback is enabled.</summary>
        public bool ApplyPlaybackReleaseBoost { get; set; } = true;

        /// <summary>Minimum rating required once a released movie reaches full maturity.</summary>
        public double MatureReleasedMinimumRating { get; set; } = 6.8;

        /// <summary>Minimum vote count required once a released movie reaches full maturity.</summary>
        public int MatureReleasedMinimumVotes { get; set; } = 500;

        /// <summary>Minimum rating required for the proven-quality score floor.</summary>
        public double ProvenQualityMinimumRating { get; set; } = 6.8;

        /// <summary>Minimum vote count required for the proven-quality score floor.</summary>
        public int ProvenQualityMinimumVotes { get; set; } = 1_000;

        // ── Trailer Rotation ──────────────────────────────────────────────────

        /// <summary>Desired size of the current ranked trailer pool. 0 = unlimited.</summary>
        public int MaxTotalTrailers { get; set; } = 100;

        // ── Advanced ──────────────────────────────────────────────────────────

        /// <summary>
        /// Path to a cookies.txt file (Netscape format) for YouTube authentication.
        /// Fixes VideoUnavailableException when YouTube blocks server-side requests.
        /// </summary>
        public string CookiesFilePath { get; set; } = string.Empty;
    }
}
