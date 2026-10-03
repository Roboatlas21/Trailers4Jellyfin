using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Trailers4Jellyfin.ScheduledTasks;
using Jellyfin.Plugin.Trailers4Jellyfin.Services;
using Xunit;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Tests.Services;

public sealed class RankedTrailerPoolSelectorTests
{
    [Fact]
    public async Task ContinuesBelowNominalLimitUntilItFindsTrailerCapableMovies()
    {
        var candidates = Enumerable.Range(1, 6)
            .Select(id => Movie(id))
            .ToList();

        var trailerLookups = new List<int>();

        var selected = await RankedTrailerPoolSelector.SelectAsync(
            candidates,
            desiredLimit: 3,
            (movie, _) => Task.FromResult(movie.Id != 2),
            movie => movie.Id == 1 ? "/trailers/one.mp4" : null,
            (movie, _) =>
            {
                trailerLookups.Add(movie.Id);
                return Task.FromResult<TmdbVideo?>(movie.Id is 3 or 5
                    ? new TmdbVideo($"yt-{movie.Id}", "Trailer", "en", true, 1080)
                    : null);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(new[] { 1, 3, 5 }, selected.Select(x => x.Movie.Id).ToArray());
        Assert.Equal("/trailers/one.mp4", selected[0].ExistingPath);
        Assert.Null(selected[0].Trailer);
        Assert.Equal("yt-3", selected[1].Trailer!.Key);
        Assert.Equal("yt-5", selected[2].Trailer!.Key);

        // Rank 2 fails movie eligibility, rank 4 has no trailer, and rank 5 is scanned
        // to fill the third slot. Rank 6 is never touched once the pool is full.
        Assert.Equal(new[] { 3, 4, 5 }, trailerLookups);
    }

    [Fact]
    public async Task StopsShortWhenCandidateListCannotFillPool()
    {
        var selected = await RankedTrailerPoolSelector.SelectAsync(
            new[] { Movie(1), Movie(2), Movie(3) },
            desiredLimit: 3,
            (_, _) => Task.FromResult(true),
            _ => null,
            (movie, _) => Task.FromResult<TmdbVideo?>(movie.Id == 2
                ? new TmdbVideo("yt-2", "Trailer", "en", true, 1080)
                : null),
            TestContext.Current.CancellationToken);

        Assert.Single(selected);
        Assert.Equal(2, selected[0].Movie.Id);
    }

    private static TmdbMovieResult Movie(int id) =>
        new(
            id,
            $"Movie {id}",
            "2026-01-01",
            new[] { 28 },
            Popularity: 100 - id,
            VoteCount: 1000,
            VoteAverage: 8,
            TmdbMovieSource.Popular);
}
