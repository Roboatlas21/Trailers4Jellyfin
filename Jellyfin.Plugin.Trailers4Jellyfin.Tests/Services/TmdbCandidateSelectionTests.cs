using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Trailers4Jellyfin.Configuration;
using Jellyfin.Plugin.Trailers4Jellyfin.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Tests.Services;

public sealed class TmdbCandidateSelectionTests
{
    [Theory]
    [InlineData(50, 4)]
    [InlineData(100, 4)]
    [InlineData(150, 6)]
    public void DiscoveryPageDepth_ScalesWithTrailerPoolTarget(int maxTotalTrailers, int expectedPages)
    {
        Assert.Equal(
            expectedPages,
            TmdbService.ResolveDiscoveryPageDepth(
                configuredPages: 4,
                maxTotalTrailers));
    }

    [Theory]
    [InlineData(50, 4)]
    [InlineData(100, 4)]
    [InlineData(150, 6)]
    public async Task CandidateDiscovery_UsesAdaptivePageDepthForTrailerPoolTarget(
        int maxTotalTrailers,
        int expectedPages)
    {
        var today = DateTime.UtcNow.Date;
        var requestedPages = new List<int>();

        using var handler = new StubHandler((request, _) =>
        {
            var uri = request.RequestUri!;
            Assert.EndsWith("/discover/movie", uri.AbsolutePath, StringComparison.Ordinal);

            var queryParts = Uri.UnescapeDataString(uri.Query)
                .TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries);
            var page = int.Parse(
                queryParts.Single(part => part.StartsWith("page=", StringComparison.Ordinal))[5..],
                System.Globalization.CultureInfo.InvariantCulture);

            requestedPages.Add(page);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    Page(
                        page,
                        totalPages: 10,
                        Movie(
                            10_000 + page,
                            $"Popular {page}",
                            today.AddDays(-1),
                            100 - page,
                            1000,
                            8.0)))
            });
        });

        using var service = new TmdbService(
            NullLogger<TmdbService>.Instance,
            new HttpClient(handler));

        var config = new PluginConfiguration
        {
            TmdbApiKey = "test-key",
            MaxPagesPerSource = 4,
            MaxTotalTrailers = maxTotalTrailers,
            SourceNowPlaying = false,
            SourceUpcoming = false,
            SourcePopular = true,
            SourceTopRated = false,
        };

        var movies = await service.GetCandidateMoviesAsync(
            config,
            TestContext.Current.CancellationToken);

        Assert.Equal(Enumerable.Range(1, expectedPages), requestedPages);
        Assert.Equal(expectedPages, movies.Count);
    }

    [Fact]
    public async Task SourceQueriesApplyFiltersBeforePaginationAndMergeByEffectivePopularity()
    {
        var today = DateTime.UtcNow.Date;
        var requests = new List<string>();

        using var handler = new StubHandler((request, _) =>
        {
            var uri = request.RequestUri!;
            var query = Uri.UnescapeDataString(uri.Query);
            requests.Add(uri.AbsolutePath + query);

            string json;
            if (uri.AbsolutePath.EndsWith("/movie/now_playing", StringComparison.Ordinal))
            {
                json = Page(
                    Movie(1, "Theatre", today, 80, 50, 6.5),
                    Movie(11, "Too Few Votes", today, 500, 29, 9.0),
                    Movie(12, "Too Low Rated", today, 500, 500, 6.4),
                    Movie(13, "Old Primary Date", today.AddMonths(-13), 10, 500, 9.0));
            }
            else if (query.Contains("vote_count.gte=0", StringComparison.Ordinal))
            {
                json = Page(Movie(2, "Coming", today.AddMonths(2), 40, 0, 0));
            }
            else if (query.Contains("vote_count.gte=100", StringComparison.Ordinal))
            {
                json = Page(
                    Movie(3, "Popular", today.AddMonths(-1), 100, 100, 6.0),
                    Movie(4, "Overlap", today.AddMonths(-1), 90, 1000, 8.5));
            }
            else if (query.Contains("vote_count.gte=500", StringComparison.Ordinal))
            {
                json = Page(Movie(4, "Overlap", today.AddMonths(-1), 90, 1000, 8.5));
            }
            else
            {
                throw new InvalidOperationException("Unexpected TMDB request: " + uri);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            });
        });

        using var service = new TmdbService(NullLogger<TmdbService>.Instance, new HttpClient(handler));
        var config = new PluginConfiguration
        {
            TmdbApiKey = "test-key",
            MaxPagesPerSource = 1,
        };

        var movies = await service.GetCandidateMoviesAsync(config, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { 2, 3, 4, 1, 13 }, movies.Select(m => m.Id).ToArray());
        Assert.Equal(TmdbMovieSource.Popular | TmdbMovieSource.TopRated, movies.Single(m => m.Id == 4).Sources);
        Assert.DoesNotContain(movies, m => m.Id is 11 or 12);
        Assert.Contains(movies, m => m.Id == 13 && m.Sources == TmdbMovieSource.InTheatres);

        var past = today.AddMonths(-12).ToString("yyyy-MM-dd");
        var future = today.AddMonths(6).ToString("yyyy-MM-dd");

        Assert.Contains(requests, r =>
            r.Contains("/movie/now_playing", StringComparison.Ordinal)
            && r.Contains("region=US", StringComparison.Ordinal));
        Assert.Contains(requests, r =>
            r.Contains("region=US", StringComparison.Ordinal)
            && r.Contains("with_release_type=2|3", StringComparison.Ordinal)
            && r.Contains("release_date.gte=" + today.ToString("yyyy-MM-dd"), StringComparison.Ordinal)
            && r.Contains("release_date.lte=" + future, StringComparison.Ordinal)
            && r.Contains("vote_count.gte=0", StringComparison.Ordinal)
            && r.Contains("sort_by=popularity.desc", StringComparison.Ordinal));
        Assert.Contains(requests, r =>
            r.Contains("primary_release_date.gte=" + past, StringComparison.Ordinal)
            && r.Contains("primary_release_date.lte=" + today.ToString("yyyy-MM-dd"), StringComparison.Ordinal)
            && r.Contains("vote_count.gte=100", StringComparison.Ordinal)
            && r.Contains("vote_average.gte=6.0", StringComparison.Ordinal)
            && r.Contains("sort_by=popularity.desc", StringComparison.Ordinal));
        Assert.Contains(requests, r =>
            r.Contains("vote_count.gte=500", StringComparison.Ordinal)
            && r.Contains("sort_by=vote_average.desc", StringComparison.Ordinal));
    }

    private static string Page(params string[] movies) =>
        Page(1, 1, movies);

    private static string Page(int page, int totalPages, params string[] movies) =>
        $"{{\"page\":{page},\"total_pages\":{totalPages},\"results\":[" + string.Join(",", movies) + "]}";

    private static string Movie(int id, string title, DateTime release, double popularity, int votes, double rating) =>
        $"{{\"id\":{id},\"title\":\"{title}\",\"release_date\":\"{release:yyyy-MM-dd}\",\"genre_ids\":[28],\"popularity\":{popularity.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"vote_count\":{votes},\"vote_average\":{rating.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}";

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}
