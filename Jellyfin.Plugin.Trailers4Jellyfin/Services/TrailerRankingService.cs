using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Trailers4Jellyfin.Configuration;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Services;

public sealed class TrailerRankingService
{
    private const int MaxFutureDays = 180;
    private const int MaxReleaseAgeDays = 365;
    private const int CurrentTheatricalMaxAgeDays = 60;

    private const int LimitedReleaseWideWindowDays = 60;
    private const double LimitedMaxPenalty = 3.0;
    private const double LimitedPopularitySupportStart = 8.0;
    private const double LimitedPopularitySupportFull = 20.0;
    private const long LimitedBudgetSupportStart = 500_000;
    private const long LimitedBudgetSupportFull = 5_000_000;

    private const long MajorUpcomingMinimumBudget = 50_000_000;
    private const double MajorUpcomingMaximumBoost = 8.0;

    private const double RatingConfidencePriorVotes = 300.0;
    private const int ProvenQualityMinimumVotes = 1_000;
    private const double LowVoteFullConfidenceVotes = 1_000.0;
    private const double LowVoteTransitionVotes = 700.0;
    private const double LowVoteQualityCapReduction = 40.0;
    private const double MatureRatingMaximumPenalty = 4.5;

    // day, popularity weight, quality weight, timing weight, entry floor, age cap
    private static readonly LifecycleAnchor[] LifecycleAnchors =
    {
        new(-180, 0.85, 0.00, 0.15, 55.0, 64.0),
        new(-90,  0.80, 0.05, 0.15, 50.0, 72.0),
        new(-30,  0.72, 0.10, 0.18, 46.0, 82.0),
        new(0,    0.62, 0.18, 0.20, 45.0, 90.0),
        new(30,   0.60, 0.25, 0.15, 45.0, 94.0),
        new(90,   0.55, 0.35, 0.10, 48.0, 92.0),
        new(180,  0.45, 0.50, 0.05, 52.0, 90.0),
        new(365,  0.35, 0.65, 0.00, 55.0, 88.0),
    };

    // PlaybackScore is intentionally asymmetric: upcoming releases keep a long lead-in,
    // while the post-release boost falls quickly and reaches zero at day 45.
    private static readonly ScorePoint[] PlaybackBoostAnchors =
    {
        new(-120, 0.0),
        new(-90,  2.0),
        new(-60,  8.0),
        new(-30, 14.0),
        new(-14, 18.0),
        new(0,   20.0),
        new(3,   14.0),
        new(7,   10.0),
        new(14,   6.0),
        new(30,   2.0),
        new(45,   0.0),
    };

    internal IReadOnlyList<TrailerRankingEvaluation> EvaluateAll(
        IEnumerable<TrailerRankingCandidate> candidates,
        PluginConfiguration config,
        DateOnly today) => candidates.Select(c => new TrailerRankingEvaluation(c, Evaluate(c, config, today))).ToList();

    internal IReadOnlyList<TrailerRankingEvaluation> OrderForPool(
        IEnumerable<TrailerRankingEvaluation> evaluations,
        PluginConfiguration config)
    {
        return config.PoolRankingMode switch
        {
            TrailerPoolRankingMode.Popularity => evaluations
                .OrderByDescending(e => e.Candidate.Movie.GetEffectivePopularity(config.ComingSoonPopularityMultiplier))
                .ThenByDescending(e => e.Candidate.Movie.VoteCount)
                .ThenBy(e => e.Candidate.Movie.Id)
                .ToList(),

            _ => evaluations
                .Where(e => e.Score.LifecyclePoolEligible)
                .OrderByDescending(e => e.Score.PoolRankBand)
                .ThenByDescending(e => e.Score.ProofScore)
                .ThenByDescending(e => e.Score.PoolScore)
                .ThenByDescending(e => e.Score.PopularitySignal)
                .ThenByDescending(e => e.Score.VoteSignal)
                .ThenBy(e => Math.Abs(e.Score.DaysFromRelease ?? int.MaxValue))
                .ThenBy(e => e.Candidate.Movie.Id)
                .ToList(),
        };
    }

    internal TrailerRankingScore Evaluate(
        TrailerRankingCandidate candidate,
        PluginConfiguration config,
        DateOnly today)
    {
        var movie = candidate.Movie;
        var reasons = new List<string>();
        int? t = candidate.LifecycleReleaseDate is DateOnly releaseDate
            ? today.DayNumber - releaseDate.DayNumber
            : null;

        var (p, r, ratingConfidence, rConf, v, qUncapped) = NormalizeSignals(movie);

        if (t is null)
        {
            reasons.Add("no usable lifecycle release date");
            return EmptyScore(candidate, reasons, p, r, ratingConfidence, rConf, v, qUncapped);
        }

        if (t < -MaxFutureDays)
            reasons.Add($"release is {-t.Value} days away (> {MaxFutureDays})");
        if (t > MaxReleaseAgeDays)
            reasons.Add($"release is {t.Value} days old (> {MaxReleaseAgeDays})");

        var budgetMetadataFloor = Math.Max(0, config.BudgetMetadataFloor);
        var reliableBudget = candidate.Budget is > 0 && candidate.Budget.Value >= budgetMetadataFloor
            ? candidate.Budget.Value
            : 0;

        if (config.MinimumMovieBudget > 0
            && reliableBudget > 0
            && reliableBudget < config.MinimumMovieBudget)
        {
            reasons.Add($"reliable budget ${reliableBudget:N0} < ${config.MinimumMovieBudget:N0}");
        }

        if (config.MinimumMovieRuntimeMinutes > 0
            && candidate.Runtime is > 0
            && candidate.Runtime.Value < config.MinimumMovieRuntimeMinutes)
        {
            reasons.Add($"runtime {candidate.Runtime.Value} < {config.MinimumMovieRuntimeMinutes} minutes");
        }

        var currentTheatrical = movie.Sources.HasFlag(TmdbMovieSource.InTheatres)
            && t is >= 0 and <= CurrentTheatricalMaxAgeDays;

        if (currentTheatrical)
        {
            if (movie.VoteCount < config.InTheatresMinimumVotes)
                reasons.Add($"current theatrical votes {movie.VoteCount} < {config.InTheatresMinimumVotes}");
            if (movie.VoteAverage < config.InTheatresMinimumRating)
                reasons.Add($"current theatrical rating {movie.VoteAverage:F3} < {config.InTheatresMinimumRating:F1}");
        }
        else if (t >= 0)
        {
            var maturity = SmoothStep(Clamp((t.Value - 45.0) / 135.0, 0.0, 1.0));
            var minimumRating = 6.0 + (0.8 * maturity);
            var minimumVotes = 100.0 + (400.0 * maturity);

            if (movie.VoteAverage < minimumRating)
                reasons.Add($"released rating {movie.VoteAverage:F3} < {minimumRating:F3}");
            if (movie.VoteCount < minimumVotes)
                reasons.Add($"released votes {movie.VoteCount} < {Math.Ceiling(minimumVotes):F0}");
        }

        var limitedOnly = candidate.RegionLimitedReleaseDate.HasValue
            && (!candidate.RegionWideReleaseDate.HasValue
                || Math.Abs(candidate.RegionWideReleaseDate.Value.DayNumber - candidate.RegionLimitedReleaseDate.Value.DayNumber)
                    > LimitedReleaseWideWindowDays);

        var q = qUncapped;
        var qualityCap = 100.0;
        if (t >= 0)
        {
            var qualityMaturity = SmoothStep(Clamp((t.Value - 90.0) / 90.0, 0.0, 1.0));
            var lowVoteFactor = SmoothStep(Clamp(
                (LowVoteFullConfidenceVotes - movie.VoteCount) / LowVoteTransitionVotes,
                0.0,
                1.0));
            qualityCap = 100.0 - (LowVoteQualityCapReduction * qualityMaturity * lowVoteFactor);
            q = Math.Min(q, qualityCap);
        }

        var popularityWeight = InterpolateLifecycle(t.Value, a => a.PopularityWeight);
        var qualityWeight = InterpolateLifecycle(t.Value, a => a.QualityWeight);
        var timingWeight = InterpolateLifecycle(t.Value, a => a.TimingWeight);
        var entryFloor = InterpolateLifecycle(t.Value, a => a.EntryFloor);
        var ageCap = InterpolateLifecycle(t.Value, a => a.AgeCap);
        var timingSignal = 100.0 * Math.Exp(-Math.Pow(t.Value / 90.0, 2.0));

        double rawScore;
        if (t < 0 && movie.VoteCount < 30)
        {
            var denominator = popularityWeight + timingWeight;
            rawScore = denominator > 0
                ? ((popularityWeight * p) + (timingWeight * timingSignal)) / denominator
                : p;
        }
        else
        {
            rawScore = (popularityWeight * p) + (qualityWeight * q) + (timingWeight * timingSignal);
        }

        var majorUpcoming = t < 0
            && (reliableBudget >= MajorUpcomingMinimumBudget || candidate.HasCollection);
        var majorUpcomingBoost = 0.0;
        if (majorUpcoming)
        {
            var daysUntilRelease = -t.Value;
            if (daysUntilRelease <= 90)
            {
                majorUpcomingBoost = MajorUpcomingMaximumBoost;
            }
            else if (daysUntilRelease <= 180)
            {
                majorUpcomingBoost = MajorUpcomingMaximumBoost
                    * (1.0 - SmoothStep((daysUntilRelease - 90.0) / 90.0));
            }
        }

        rawScore += majorUpcomingBoost;

        var provenQualityFloor = 0.0;
        if (t >= 0 && movie.VoteCount >= ProvenQualityMinimumVotes && movie.VoteAverage >= 6.8)
        {
            var ageMaturity = Sigmoid((t.Value - 120.0) / 45.0);
            var qualityStrength = Sigmoid((q - 65.0) / 7.0);
            provenQualityFloor = 52.0 + (18.0 * ageMaturity * qualityStrength);
            rawScore = Math.Max(rawScore, provenQualityFloor);
        }

        var matureRatingPenalty = 0.0;
        if (t >= 0)
        {
            var ageFactor = SmoothStep(Clamp((t.Value - 120.0) / 120.0, 0.0, 1.0));
            var ratingDeficit = SmoothStep(Clamp((7.05 - movie.VoteAverage) / 0.35, 0.0, 1.0));
            matureRatingPenalty = MatureRatingMaximumPenalty * ageFactor * ratingDeficit;
            rawScore -= matureRatingPenalty;
        }

        var scoreBeforeLimitedPenalty = Math.Min(rawScore, ageCap);
        var limitedPenalty = limitedOnly
            ? CalculateLimitedPenalty(candidate, movie, t.Value, reliableBudget)
            : 0.0;
        var poolScore = scoreBeforeLimitedPenalty - limitedPenalty;

        if (poolScore < entryFloor)
            reasons.Add($"final score {poolScore:F3} < lifecycle entry floor {entryFloor:F3}");

        var playbackBoost = CalculatePlaybackBoost(t.Value);
        var playbackScore = poolScore + playbackBoost;
        var proofScore = q * Sigmoid((t.Value - 30.0) / 45.0);

        return new TrailerRankingScore
        {
            LifecyclePoolEligible = reasons.Count == 0,
            ExclusionReason = string.Join("; ", reasons),
            DaysFromRelease = t,
            LifecycleLabel = GetLifecycleLabel(t.Value, currentTheatrical),
            CurrentTheatrical = currentTheatrical,
            LimitedOnly = limitedOnly,
            PopularitySignal = p,
            RatingSignal = r,
            RatingConfidence = ratingConfidence,
            ConfidentRatingSignal = rConf,
            VoteSignal = v,
            QualityScore = q,
            UncappedQualityScore = qUncapped,
            QualityCap = qualityCap,
            TimingSignal = timingSignal,
            PopularityWeight = popularityWeight,
            QualityWeight = qualityWeight,
            TimingWeight = timingWeight,
            EntryFloor = entryFloor,
            AgeCap = ageCap,
            MajorUpcoming = majorUpcoming,
            MajorUpcomingBoost = majorUpcomingBoost,
            ProvenQualityFloor = provenQualityFloor,
            MatureRatingPenalty = matureRatingPenalty,
            ScoreBeforeLimitedPenalty = scoreBeforeLimitedPenalty,
            LimitedReleasePenalty = limitedPenalty,
            PoolScore = poolScore,
            PoolRankBand = Math.Floor(poolScore * 2.0) / 2.0,
            ProofScore = proofScore,
            ReleasePlaybackBoost = playbackBoost,
            PlaybackScore = playbackScore,
        };
    }

    internal TrailerRankingManifest BuildManifest(
        IReadOnlyList<TrailerRankingEvaluation> evaluations,
        IReadOnlyList<int> selectedPoolOrder,
        PluginConfiguration config,
        DateTimeOffset generatedAtUtc)
    {
        var byId = evaluations.ToDictionary(e => e.Candidate.Movie.Id);
        var poolRank = selectedPoolOrder
            .Select((id, index) => (id, rank: index + 1))
            .ToDictionary(x => x.id, x => x.rank);

        var selected = selectedPoolOrder
            .Where(byId.ContainsKey)
            .Select(id => byId[id])
            .ToList();

        IEnumerable<TrailerRankingEvaluation> playbackOrdered = config.PlaybackRankingMode switch
        {
            TrailerPlaybackRankingMode.Popularity => selected
                .OrderByDescending(e => e.Candidate.Movie.Popularity)
                .ThenByDescending(e => e.Score.PoolScore)
                .ThenBy(e => poolRank[e.Candidate.Movie.Id])
                .ThenBy(e => e.Candidate.Movie.Id),

            _ when config.ApplyPlaybackReleaseBoost => selected
                .OrderByDescending(e => e.Score.PlaybackScore)
                .ThenByDescending(e => e.Score.PoolScore)
                .ThenBy(e => poolRank[e.Candidate.Movie.Id])
                .ThenBy(e => e.Candidate.Movie.Id),

            _ => selected
                .OrderByDescending(e => e.Score.PoolScore)
                .ThenByDescending(e => e.Candidate.Movie.Popularity)
                .ThenBy(e => poolRank[e.Candidate.Movie.Id])
                .ThenBy(e => e.Candidate.Movie.Id),
        };

        var playbackRank = playbackOrdered
            .Select((evaluation, index) => (id: evaluation.Candidate.Movie.Id, rank: index + 1))
            .ToDictionary(x => x.id, x => x.rank);

        var movies = new Dictionary<string, TrailerRankingManifestEntry>(StringComparer.Ordinal);
        foreach (var evaluation in evaluations)
        {
            var movie = evaluation.Candidate.Movie;
            var score = evaluation.Score;
            poolRank.TryGetValue(movie.Id, out var actualPoolRank);
            playbackRank.TryGetValue(movie.Id, out var actualPlaybackRank);

            movies[movie.Id.ToString(CultureInfo.InvariantCulture)] = new TrailerRankingManifestEntry
            {
                TmdbId = movie.Id,
                Title = movie.Title,
                RawPopularity = Round6(movie.Popularity),
                VoteCount = movie.VoteCount,
                VoteAverage = Round6(movie.VoteAverage),
                LifecycleReleaseDate = evaluation.Candidate.LifecycleReleaseDate,
                DaysFromRelease = score.DaysFromRelease,
                Lifecycle = score.LifecycleLabel,
                LifecyclePoolEligible = score.LifecyclePoolEligible,
                ExclusionReason = score.ExclusionReason,
                PoolScore = Round6(score.PoolScore),
                PoolRankBand = Round6(score.PoolRankBand),
                LimitedReleasePenalty = Round6(score.LimitedReleasePenalty),
                ReleasePlaybackBoost = Round6(score.ReleasePlaybackBoost),
                PlaybackScore = Round6(score.PlaybackScore),
                PoolRank = actualPoolRank == 0 ? null : actualPoolRank,
                PlaybackRank = actualPlaybackRank == 0 ? null : actualPlaybackRank,
                SelectedInPool = actualPoolRank > 0,
            };
        }

        double? cutoff = null;
        if (selectedPoolOrder.Count >= 100
            && byId.TryGetValue(selectedPoolOrder[99], out var cutoffEvaluation))
        {
            cutoff = Round6(cutoffEvaluation.Score.PoolScore);
        }

        return new TrailerRankingManifest
        {
            GeneratedAtUtc = generatedAtUtc,
            Region = string.IsNullOrWhiteSpace(config.TheatricalRegion)
                ? "US"
                : config.TheatricalRegion.Trim().ToUpperInvariant(),
            PoolRankingMode = config.PoolRankingMode,
            PlaybackRankingMode = config.PlaybackRankingMode,
            ApplyPlaybackReleaseBoost = config.ApplyPlaybackReleaseBoost,
            Top100CutoffScore = cutoff,
            Movies = movies,
        };
    }

    private static TrailerRankingScore EmptyScore(
        TrailerRankingCandidate candidate,
        IReadOnlyCollection<string> reasons,
        double p,
        double r,
        double ratingConfidence,
        double rConf,
        double v,
        double q)
    {
        return new TrailerRankingScore
        {
            LifecyclePoolEligible = false,
            ExclusionReason = string.Join("; ", reasons),
            LifecycleLabel = "unknown",
            PopularitySignal = p,
            RatingSignal = r,
            RatingConfidence = ratingConfidence,
            ConfidentRatingSignal = rConf,
            VoteSignal = v,
            QualityScore = q,
            UncappedQualityScore = q,
            QualityCap = 100.0,
        };
    }

    private static (double P, double R, double RatingConfidence, double RConf, double V, double Q) NormalizeSignals(
        TmdbMovieResult movie)
    {
        var popularity = Math.Max(0.0, movie.Popularity);
        var votes = Math.Max(0, movie.VoteCount);
        var rating = Math.Max(0.0, movie.VoteAverage);

        var p = Math.Min(100.0, 100.0 * Math.Log(1.0 + popularity) / Math.Log(501.0));
        var r = Clamp(40.0 * (rating - 6.0), 0.0, 100.0);
        var ratingConfidence = votes / (votes + RatingConfidencePriorVotes);
        var rConf = (ratingConfidence * r) + ((1.0 - ratingConfidence) * 20.0);
        var v = Math.Min(100.0, 100.0 * Math.Log(1.0 + votes) / Math.Log(10001.0));
        var q = (0.55 * rConf) + (0.45 * v);
        return (p, r, ratingConfidence, rConf, v, q);
    }

    private static double CalculateLimitedPenalty(
        TrailerRankingCandidate candidate,
        TmdbMovieResult movie,
        int t,
        long reliableBudget)
    {
        var popularitySupport = SmoothStep(Clamp(
            (movie.Popularity - LimitedPopularitySupportStart)
            / (LimitedPopularitySupportFull - LimitedPopularitySupportStart),
            0.0,
            1.0));

        var budgetSupport = reliableBudget > 0
            ? SmoothStep(Clamp(
                (reliableBudget - LimitedBudgetSupportStart)
                / (double)(LimitedBudgetSupportFull - LimitedBudgetSupportStart),
                0.0,
                1.0))
            : 0.0;

        var franchiseSupport = candidate.HasCollection ? 1.0 : 0.0;
        var audienceSupport = 0.0;
        if (t >= 0)
        {
            var voteSupport = SmoothStep(Clamp((movie.VoteCount - 250.0) / 750.0, 0.0, 1.0));
            var ratingSupport = SmoothStep(Clamp((movie.VoteAverage - 6.5) / 0.5, 0.0, 1.0));
            audienceSupport = voteSupport * ratingSupport;
        }

        var evidenceSupport = Math.Max(
            Math.Max(popularitySupport, budgetSupport),
            Math.Max(franchiseSupport, audienceSupport));

        double ageFactor;
        double minimumPenalty;
        if (t < 0)
        {
            ageFactor = 1.0;
            minimumPenalty = 0.75;
        }
        else if (t <= 30)
        {
            ageFactor = 1.0 - SmoothStep(t / 90.0);
            minimumPenalty = 0.75 - (0.25 * SmoothStep(t / 30.0));
        }
        else if (t <= 90)
        {
            ageFactor = 1.0 - SmoothStep(t / 90.0);
            minimumPenalty = 0.50 * (1.0 - SmoothStep((t - 30.0) / 60.0));
        }
        else
        {
            ageFactor = 0.0;
            minimumPenalty = 0.0;
        }

        var evidencePenalty = LimitedMaxPenalty * (1.0 - evidenceSupport) * ageFactor;
        return Math.Max(minimumPenalty, evidencePenalty);
    }

    internal static double CalculatePlaybackBoost(int daysFromRelease)
    {
        if (daysFromRelease < PlaybackBoostAnchors[0].Day
            || daysFromRelease > PlaybackBoostAnchors[^1].Day)
        {
            return 0.0;
        }

        return InterpolatePoints(daysFromRelease, PlaybackBoostAnchors);
    }

    private static string GetLifecycleLabel(int t, bool currentTheatrical)
    {
        if (t < 0)
            return "upcoming";
        if (currentTheatrical)
            return "in_theatres";
        if (t <= 90)
            return "recently_released";
        return "older_recent";
    }

    private static double InterpolateLifecycle(int day, Func<LifecycleAnchor, double> selector)
    {
        if (day <= LifecycleAnchors[0].Day)
            return selector(LifecycleAnchors[0]);
        if (day >= LifecycleAnchors[^1].Day)
            return selector(LifecycleAnchors[^1]);

        for (var i = 0; i < LifecycleAnchors.Length - 1; i++)
        {
            var a = LifecycleAnchors[i];
            var b = LifecycleAnchors[i + 1];
            if (day < a.Day || day > b.Day)
                continue;

            var x = (day - a.Day) / (double)(b.Day - a.Day);
            var s = SmoothStep(x);
            return selector(a) + ((selector(b) - selector(a)) * s);
        }

        throw new InvalidOperationException("Lifecycle interpolation failed.");
    }

    private static double InterpolatePoints(int day, IReadOnlyList<ScorePoint> points)
    {
        if (day <= points[0].Day)
            return points[0].Score;
        if (day >= points[^1].Day)
            return points[^1].Score;

        for (var i = 0; i < points.Count - 1; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            if (day < a.Day || day > b.Day)
                continue;

            var x = (day - a.Day) / (double)(b.Day - a.Day);
            var s = SmoothStep(x);
            return a.Score + ((b.Score - a.Score) * s);
        }

        throw new InvalidOperationException("Point interpolation failed.");
    }

    private static double Clamp(double value, double minimum, double maximum) =>
        Math.Max(minimum, Math.Min(maximum, value));

    private static double SmoothStep(double x)
    {
        x = Clamp(x, 0.0, 1.0);
        return (3.0 * x * x) - (2.0 * x * x * x);
    }

    private static double Sigmoid(double x)
    {
        if (x >= 0)
        {
            var z = Math.Exp(-x);
            return 1.0 / (1.0 + z);
        }

        var exp = Math.Exp(x);
        return exp / (1.0 + exp);
    }

    private static double Round6(double value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);

    private sealed record LifecycleAnchor(
        int Day,
        double PopularityWeight,
        double QualityWeight,
        double TimingWeight,
        double EntryFloor,
        double AgeCap);

    private sealed record ScorePoint(int Day, double Score);
}
