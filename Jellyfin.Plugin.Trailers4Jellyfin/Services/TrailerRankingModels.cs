using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Trailers4Jellyfin.Configuration;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Services;

internal sealed record TrailerRankingCandidate(
    TmdbMovieResult Movie,
    DateOnly? LifecycleReleaseDate,
    DateOnly? RegionLimitedReleaseDate,
    DateOnly? RegionWideReleaseDate,
    long? Budget,
    int? Runtime,
    bool HasCollection);

internal sealed record TrailerRankingScore
{
    public bool LifecyclePoolEligible { get; init; }

    public string ExclusionReason { get; init; } = string.Empty;

    public int? DaysFromRelease { get; init; }

    public string LifecycleLabel { get; init; } = "unknown";

    public bool CurrentTheatrical { get; init; }

    public bool LimitedOnly { get; init; }

    public double PopularitySignal { get; init; }

    public double RatingSignal { get; init; }

    public double RatingConfidence { get; init; }

    public double ConfidentRatingSignal { get; init; }

    public double VoteSignal { get; init; }

    public double QualityScore { get; init; }

    public double UncappedQualityScore { get; init; }

    public double QualityCap { get; init; }

    public double TimingSignal { get; init; }

    public double PopularityWeight { get; init; }

    public double QualityWeight { get; init; }

    public double TimingWeight { get; init; }

    public double EntryFloor { get; init; }

    public double AgeCap { get; init; }

    public bool MajorUpcoming { get; init; }

    public double MajorUpcomingBoost { get; init; }

    public double ProvenQualityFloor { get; init; }

    public double MatureRatingPenalty { get; init; }

    public double ScoreBeforeLimitedPenalty { get; init; }

    public double LimitedReleasePenalty { get; init; }

    public double PoolScore { get; init; }

    public double PoolRankBand { get; init; }

    public double ProofScore { get; init; }

    public double ReleasePlaybackBoost { get; init; }

    public double PlaybackScore { get; init; }
}

internal sealed record TrailerRankingEvaluation(
    TrailerRankingCandidate Candidate,
    TrailerRankingScore Score);

internal sealed record TrailerRankingManifest
{
    public int SchemaVersion { get; init; } = 1;

    public int ScoreModelVersion { get; init; } = 1;

    public DateTimeOffset GeneratedAtUtc { get; init; }

    public string Region { get; init; } = "US";

    public TrailerPoolRankingMode PoolRankingMode { get; init; }

    public TrailerPlaybackRankingMode PlaybackRankingMode { get; init; }

    public bool ApplyPlaybackReleaseBoost { get; init; }

    public double? Top100CutoffScore { get; init; }

    public Dictionary<string, TrailerRankingManifestEntry> Movies { get; init; } = new();
}

internal sealed record TrailerRankingManifestEntry
{
    public int TmdbId { get; init; }

    public string Title { get; init; } = string.Empty;

    public double RawPopularity { get; init; }

    public int VoteCount { get; init; }

    public double VoteAverage { get; init; }

    public DateOnly? LifecycleReleaseDate { get; init; }

    public int? DaysFromRelease { get; init; }

    public string Lifecycle { get; init; } = "unknown";

    public bool LifecyclePoolEligible { get; init; }

    public string ExclusionReason { get; init; } = string.Empty;

    public double PoolScore { get; init; }

    public double PoolRankBand { get; init; }

    public double LimitedReleasePenalty { get; init; }

    public double ReleasePlaybackBoost { get; init; }

    public double PlaybackScore { get; init; }

    public int? PoolRank { get; init; }

    public int? PlaybackRank { get; init; }

    public bool SelectedInPool { get; init; }
}
