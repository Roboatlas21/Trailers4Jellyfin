using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Trailers4Jellyfin.Services;
using Xunit;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Tests.Services;

public sealed class TrailerDownloadPublicationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "t4j-download-" + Guid.NewGuid());
    public TrailerDownloadPublicationTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task Video_RemainsHiddenUntilMetadataIsReady()
    {
        var output = Path.Combine(_directory, "Film.mp4");
        string? staged = null;
        var success = await TrailerDownloadService.DownloadAndPublishAsync(output,
            path =>
            {
                staged = path;
                Assert.True(TrailerDownloadService.IsPartialDownloadArtifact(path));
                File.WriteAllText(path, "complete video");
                Assert.False(File.Exists(output));
                return Task.FromResult(true);
            },
            () =>
            {
                Assert.False(File.Exists(output));
                File.WriteAllText(Path.ChangeExtension(output, ".json"), "{}");
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        Assert.True(success);
        Assert.Equal("complete video", File.ReadAllText(output));
        Assert.False(File.Exists(staged));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledDownload_PreservesPreviousVideo(bool cancel)
    {
        var output = Path.Combine(_directory, "Film.mp4");
        File.WriteAllText(output, "previous video");
        string? staged = null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var metadataCalled = false;
        var task = TrailerDownloadService.DownloadAndPublishAsync(output,
            path =>
            {
                staged = path;
                File.WriteAllText(path, "partial");
                if (cancel) cts.Cancel();
                return Task.FromResult(cancel);
            },
            () => { metadataCalled = true; return Task.CompletedTask; }, cts.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        else Assert.False(await task);
        Assert.False(metadataCalled);
        Assert.Equal("previous video", File.ReadAllText(output));
        Assert.False(File.Exists(staged));
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
