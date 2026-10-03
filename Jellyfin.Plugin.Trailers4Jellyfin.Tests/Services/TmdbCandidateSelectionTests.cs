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
                    Movie(11, "Too Few Votes", today, 500, 49, 9.0),
                    Movie(12, "Too Low Rated", today, 500, 500, 6.4),
                    Movie(13, "Too Old", today.AddMonths(-13), 500, 500, 9.0));
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

        Assert.Equal(new[] { 2, 3, 4, 1 }, movies.Select(m => m.Id).ToArray());
        Assert.Equal(TmdbMovieSource.Popular | TmdbMovieSource.TopRated, movies.Single(m => m.Id == 4).Sources);
        Assert.DoesNotContain(movies, m => m.Id is 11 or 12 or 13);

        var past = today.AddMonths(-12).ToString("yyyy-MM-dd");
        var future = today.AddMonths(6).ToString("yyyy-MM-dd");

        Assert.Contains(requests, r => r.Contains("/movie/now_playing", StringComparison.Ordinal));
        Assert.Contains(requests, r =>
            r.Contains("primary_release_date.gte=" + today.ToString("yyyy-MM-dd"), StringComparison.Ordinal)
            && r.Contains("primary_release_date.lte=" + future, StringComparison.Ordinal)
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
        "{\"page\":1,\"total_pages\":1,\"results\":[" + string.Join(",", movies) + "]}";

    private static string Movie(int id, string title, DateTime release, double popularity, int votes, double rating) =>
        $"{{\"id\":{id},\"title\":\"{title}\",\"release_date\":\"{release:yyyy-MM-dd}\",\"genre_ids\":[28],\"popularity\":{popularity.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"vote_count\":{votes},\"vote_average\":{rating.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}";

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}
