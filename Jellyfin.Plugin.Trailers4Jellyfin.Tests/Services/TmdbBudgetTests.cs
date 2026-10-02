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
    [InlineData("{\"budget\":350000}", false)]
    [InlineData("{\"budget\":9999999}", false)]
    [InlineData("{\"budget\":10000000}", true)]
    [InlineData("{\"budget\":25000000}", true)]
    [InlineData("{\"budget\":3000000000}", true)]
    [InlineData("{\"budget\":0}", true)]
    [InlineData("{\"budget\":-1}", true)]
    [InlineData("{\"budget\":null}", true)]
    [InlineData("{\"budget\":\"unknown\"}", true)]
    [InlineData("{}", true)]
    [InlineData("invalid JSON", true)]
    public async Task BudgetFilter_RejectsOnlyKnownBudgetsBelowMinimum(string json, bool expected)
    {
        using var handler = new StubHandler((request, _) =>
        {
            Assert.Equal("/3/movie/1340102", request.RequestUri!.AbsolutePath);
            Assert.Equal("?api_key=test-key", request.RequestUri.Query);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        });
        using var service = new TmdbService(NullLogger<TmdbService>.Instance, new HttpClient(handler));
        Assert.Equal(expected, await service.MeetsMinimumBudgetAsync(
            "1340102", "test-key", 10_000_000, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisabledBudgetFilter_MakesNoRequest()
    {
        var requested = false;
        using var handler = new StubHandler((_, _) =>
        {
            requested = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var service = new TmdbService(NullLogger<TmdbService>.Instance, new HttpClient(handler));
        Assert.True(await service.MeetsMinimumBudgetAsync("1", "test-key", 0, TestContext.Current.CancellationToken));
        Assert.False(requested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LookupFailure_AllowsMovie(bool timeout)
    {
        using var handler = new StubHandler((_, _) => timeout
            ? Task.FromException<HttpResponseMessage>(new TaskCanceledException("HTTP timeout"))
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var service = new TmdbService(NullLogger<TmdbService>.Instance, new HttpClient(handler));
        Assert.True(await service.MeetsMinimumBudgetAsync("1", "test-key", 10_000_000, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TaskCancellation_IsNotTreatedAsAnUnknownBudget()
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
            service.MeetsMinimumBudgetAsync("1", "test-key", 10_000_000, cts.Token));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}
