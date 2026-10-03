using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Trailers4Jellyfin.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Tests.Services;

public sealed class TmdbBudgetTests
{
    [Theory]
    [InlineData(0, 120, true)]
    [InlineData(7, 120, true)]
    [InlineData(999, 120, true)]
    [InlineData(1000, 120, false)]
    [InlineData(350000, 120, false)]
    [InlineData(9999999, 120, false)]
    [InlineData(10000000, 120, true)]
    [InlineData(25000000, 120, true)]
    [InlineData(25000000, 0, true)]
    [InlineData(25000000, 44, false)]
    [InlineData(25000000, 45, true)]
    public void DetailsFilter_UsesBudgetSanityFloorAndMinimumRuntime(long budget, int runtime, bool expected)
    {
        var details = new TmdbMovieDetails(budget, runtime);
        Assert.Equal(expected, TmdbService.MeetsMovieDetailsRequirements(details, 10_000_000, 1_000, 45));
    }

    [Fact]
    public void MissingDetails_AreAllowed()
    {
        Assert.True(TmdbService.MeetsMovieDetailsRequirements(null, 10_000_000, 1_000, 45));
        Assert.True(TmdbService.MeetsMovieDetailsRequirements(new TmdbMovieDetails(null, null), 10_000_000, 1_000, 45));
    }

    [Fact]
    public async Task DetailsLookup_ParsesBudgetAndRuntime()
    {
        using var handler = new StubHandler((request, _) =>
        {
            Assert.Equal("/3/movie/1340102", request.RequestUri!.AbsolutePath);
            Assert.Equal("?api_key=test-key", request.RequestUri.Query);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"budget\":7500000,\"runtime\":102}")
            });
        });

        using var service = new TmdbService(NullLogger<TmdbService>.Instance, new HttpClient(handler));
        var details = await service.GetMovieDetailsAsync("1340102", "test-key", TestContext.Current.CancellationToken);

        Assert.NotNull(details);
        Assert.Equal(7_500_000, details!.Budget);
        Assert.Equal(102, details.Runtime);
    }

    [Fact]
    public async Task LookupFailure_AllowsUnknownDetails()
    {
        using var handler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var service = new TmdbService(NullLogger<TmdbService>.Instance, new HttpClient(handler));

        Assert.Null(await service.GetMovieDetailsAsync("1", "test-key", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TaskCancellation_IsNotTreatedAsUnknownDetails()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var handler = new StubHandler((_, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var service = new TmdbService(NullLogger<TmdbService>.Instance, new HttpClient(handler));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetMovieDetailsAsync("1", "test-key", cts.Token));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}
