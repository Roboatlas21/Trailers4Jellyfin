using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Trailers4Jellyfin.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Tests.Services;

public sealed class TmdbVideoSelectionTests
{
    [Fact]
    public async Task GetTrailers_ParsesAllYouTubeVideoTypesAndAppliesSelectorOrdering()
    {
        using var handler = new StubHandler((request, _) =>
        {
            Assert.Equal("/3/movie/1514026/videos", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"id\":1514026,\"results\":["
                    + "{\"key\":\"special\",\"name\":\"Official Special Look\",\"site\":\"YouTube\",\"size\":2160,\"type\":\"Featurette\",\"official\":true,\"published_at\":\"2026-09-01T00:00:00Z\",\"iso_639_1\":\"en\"},"
                    + "{\"key\":\"full\",\"name\":\"Official Trailer\",\"site\":\"YouTube\",\"size\":1080,\"type\":\"Trailer\",\"official\":false,\"published_at\":\"2026-08-01T00:00:00Z\",\"iso_639_1\":\"en\"},"
                    + "{\"key\":\"vimeo\",\"name\":\"Trailer\",\"site\":\"Vimeo\",\"size\":2160,\"type\":\"Trailer\",\"official\":true,\"published_at\":\"2026-10-01T00:00:00Z\",\"iso_639_1\":\"en\"}"
                    + "]}")
            });
        });

        using var service = new TmdbService(NullLogger<TmdbService>.Instance, new HttpClient(handler));

        var videos = await service.GetTrailersAsync(
            "1514026",
            "test-key",
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, videos.Count);
        Assert.Equal("full", videos[0].Key);
        Assert.Equal("Trailer", videos[0].Type);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), videos[0].PublishedAt);
        Assert.Equal(2, videos[0].ReturnOrder);
        Assert.Equal("special", videos[1].Key);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}
