using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Jellyfin.Plugin.Trailers4Jellyfin.Services;
using Xunit;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Tests.Services;

public sealed partial class TrailerSelectionTests
{
    private Video Clip(string name, int? tmdbId, params string[] genres) =>
        Trailer(name, JsonSerializer.Serialize(new { tmdbId, genres }));

    private Movie LibraryMovie(int id, bool watched, User? user = null)
    {
        var movie = new Movie { Id = Guid.NewGuid(), ProviderIds = new Dictionary<string, string> { ["Tmdb"] = id.ToString() } };
        _library.Movies.Add(movie);
        if (watched) _history.Watched.Add(((user ?? _user).Id, movie.Id));
        return movie;
    }

    private void Watched(Video trailer, int? daysAgo)
    {
        _history.Watched.Add((_user.Id, trailer.Id));
        _history.Dates[(_user.Id, trailer.Id)] = daysAgo.HasValue ? new DateTime(2026, 10, 1).AddDays(-daysAgo.Value) : null;
    }

    [Fact]
    public void RelatedGroup_IsUsedOnlyWhenNoDirectMatchRemains()
    {
        var related = Clip("related", null, "Thriller");
        var unrelated = Clip("other", null, "Action");
        var feature = new Movie { Genres = new[] { "Horror" } };
        Assert.Equal(related.Id, Assert.Single(_provider.SelectTrailers(feature, new[] { unrelated, related }, _config, _user)).Id);
        var direct = Clip("direct", null, "Horror");
        Assert.Equal(direct.Id, Assert.Single(_provider.SelectTrailers(feature, new[] { related, direct }, _config, _user)).Id);
    }

    [Fact]
    public void GeneralFallback_IsUsedWhenNoDirectOrRelatedMatchesExist()
    {
        var items = new[] { Clip("one", null, "Comedy"), Clip("two", null, "Family") };
        Assert.Equal(2, _provider.SelectTrailers(new Movie { Genres = new[] { "Horror" } }, items, _config, _user).Count);
    }

    [Fact]
    public void RelatedMapping_IsOneHopDirectional_AndAnimationUsesOtherGenres()
    {
        Assert.DoesNotContain("Action", TrailerGenres.GetRelated(new[] { "Horror" }));
        Assert.Contains("Animation", TrailerGenres.GetRelated(new[] { "Family" }));
        Assert.Empty(TrailerGenres.GetRelated(new[] { "Animation", "Music", "Documentary", "TV Movie" }));
        Assert.Equal(TrailerGenres.GetRelated(new[] { "Horror" }).Order(), TrailerGenres.GetRelated(new[] { "Horror", "Animation" }).Order());
    }

    [Fact]
    public void RelatedScores_CombineGenresButCountEachOnlyOnce()
    {
        _config.NumberOfTrailers = 1;
        var best = Clip("best", null, "Thriller", "Mystery");
        var duplicates = Clip("duplicates", null, "Thriller", "thriller", "Thriller");
        var feature = new Movie { Genres = new[] { "Horror", "Crime" } };
        Assert.Equal(best.Id, Assert.Single(_provider.SelectTrailers(feature, new[] { duplicates, best }, _config, _user)).Id);
    }

    [Fact]
    public void DirectScores_DoNotCountRepeatedOrCaseVariantGenres()
    {
        _config.NumberOfTrailers = 1;
        var best = Clip("best", null, "Horror", "Comedy");
        var duplicates = Clip("duplicates", null, "Horror", "horror", "HORROR");
        Assert.Equal(best.Id, Assert.Single(_provider.SelectTrailers(new Movie { Genres = new[] { "Horror", "Comedy", "Horror" } }, new[] { duplicates, best }, _config, _user)).Id);
    }

    [Fact]
    public void UnwatchedLowerScore_BeatsWatchedHigherScoreWithinGroup()
    {
        _config.PreferUnwatchedTrailers = true;
        var watched = Clip("watched", null, "Horror", "Comedy");
        var unseen = Clip("unseen", null, "Horror");
        Watched(watched, 10);
        var result = _provider.SelectTrailers(new Movie { Genres = new[] { "Horror", "Comedy" } }, new[] { watched, unseen }, _config, _user);
        Assert.Equal(new[] { unseen.Id, watched.Id }, result.Select(t => t.Id));
    }

    [Fact]
    public void MultipleSlots_UseUnwatchedScoresThenOldestDates_IgnoringWatchedScores()
    {
        _config.PreferUnwatchedTrailers = true;
        _config.NumberOfTrailers = 10;
        var high = Clip("high", null, "Horror", "Comedy");
        var low = Clip("low", null, "Horror");
        var older = Clip("older", null, "Horror");
        var recent = Clip("recent", null, "Horror", "Comedy");
        var missing = Clip("missing", null, "Horror");
        Watched(older, 20); Watched(recent, 2); Watched(missing, null);
        var result = _provider.SelectTrailers(new Movie { Genres = new[] { "Horror", "Comedy" } }, new[] { recent, low, older, missing, high, high }, _config, _user);
        Assert.Equal(new[] { high.Id, low.Id, missing.Id, older.Id, recent.Id }, result.Select(t => t.Id));
        Assert.Equal(3, _history.Watched.Count);
    }

    [Fact]
    public void AllWatched_ReplaysOldestRegardlessOfScore()
    {
        _config.PreferUnwatchedTrailers = true;
        _config.NumberOfTrailers = 1;
        var older = Clip("older", null, "Horror");
        var recent = Clip("recent", null, "Horror", "Comedy");
        Watched(older, 20); Watched(recent, 2);
        Assert.Equal(older.Id, Assert.Single(_provider.SelectTrailers(new Movie { Genres = new[] { "Horror", "Comedy" } }, new[] { recent, older }, _config, _user)).Id);
    }

    [Fact]
    public void PreferenceOff_UsesScoresWithoutReadingTrailerHistory()
    {
        _config.PreferUnwatchedTrailers = false;
        var high = Clip("high", null, "Horror", "Comedy");
        var low = Clip("low", null, "Horror");
        Watched(high, 1);
        var result = _provider.SelectTrailers(new Movie { Genres = new[] { "Horror", "Comedy" } }, new[] { low, high }, _config, _user);
        Assert.Equal(new[] { high.Id, low.Id }, result.Select(t => t.Id));
        Assert.Equal(0, _history.Lookups);
    }

    [Fact]
    public void GenreMatchingOff_IgnoresGenreScoreAndUsesGeneralHistoryOrder()
    {
        _config.EnableGenreMatching = false;
        _config.PreferUnwatchedTrailers = true;
        var match = Clip("match", null, "Horror");
        var other = Clip("other", null, "Comedy");
        Watched(match, 20);
        var result = _provider.SelectTrailers(new Movie { Genres = new[] { "Horror" } }, new[] { match, other }, _config, _user);
        Assert.Equal(new[] { other.Id, match.Id }, result.Select(t => t.Id));
    }

    [Fact]
    public void CurrentMovieExclusion_RemovesAllTrailerVersions_BeforeGenreFallback()
    {
        var feature = LibraryMovie(42, false);
        feature.Genres = new[] { "Horror" };
        var first = Clip("first", 42, "Horror");
        var second = Clip("second", 42, "Horror");
        var other = Clip("other", 99, "Thriller");
        Assert.Equal(other.Id, Assert.Single(_provider.SelectTrailers(feature, new[] { first, other, second }, _config, _user)).Id);
        _config.SkipCurrentMovieTrailers = false;
        var result = _provider.SelectTrailers(feature, new[] { first, other, second }, _config, _user);
        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(other, result);
    }

    [Fact]
    public void WatchedMovieExclusion_UsesCurrentUserAndAnyMatchingCopy_IndependentlyOfTrailerPreference()
    {
        _config.PreferUnwatchedTrailers = false;
        LibraryMovie(42, false);
        LibraryMovie(42, true);
        var trailer = Clip("trailer", 42, "Horror");
        var movie = new Movie { Genres = new[] { "Horror" } };
        Assert.Empty(_provider.SelectTrailers(movie, new[] { trailer }, _config, _user));
        var anotherUser = new User("other", "auth", "reset");
        Assert.Single(_provider.SelectTrailers(movie, new[] { trailer }, _config, anotherUser));
        _config.SkipWatchedMovieTrailers = false;
        Assert.Single(_provider.SelectTrailers(movie, new[] { trailer }, _config, _user));
    }

    [Fact]
    public void UnknownOrAbsentMovieIdentity_RemainsEligible()
    {
        LibraryMovie(42, true);
        var items = new[] { Clip("unknown", null), Clip("absent", 99) };
        Assert.Equal(2, _provider.SelectTrailers(new Movie(), items, _config, _user).Count);
    }

    [Theory]
    [InlineData("\"unknown\"")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("{}")]
    public void InvalidTmdbIdentity_DoesNotExcludeOtherwiseEligibleTrailer(string identity)
    {
        var trailer = Trailer("invalid-id", "{\"tmdbId\":" + identity + ",\"genres\":[\"Horror\"]}");
        Assert.Single(_provider.SelectTrailers(LibraryMovie(42, true), new[] { trailer }, _config, _user));
    }

    [Fact]
    public void NumericStringTmdbId_MatchesCurrentMovie()
    {
        var trailer = Trailer("string-id", "{\"tmdbId\":\"0042\"}");
        Assert.Empty(_provider.SelectTrailers(LibraryMovie(42, false), new[] { trailer }, _config, _user));
    }

    [Fact]
    public void RatingExclusions_AreAppliedBeforeChoosingGenreGroup()
    {
        var restricted = Trailer("restricted", "{\"genres\":[\"Horror\"],\"officialRating\":\"R\"}");
        var allowed = Trailer("allowed", "{\"genres\":[\"Thriller\"],\"officialRating\":\"PG\"}");
        _user.MaxParentalRatingScore = 10;
        Assert.Equal(allowed.Id, Assert.Single(_provider.SelectTrailers(new Movie { Genres = new[] { "Horror" } }, new[] { restricted, allowed }, _config, _user)).Id);
        Assert.Empty(_provider.SelectTrailers(new Movie(), new[] { restricted }, _config, _user));
    }
}
