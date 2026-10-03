using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Trailers4Jellyfin.Services;
using Xunit;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Tests.Services;

public sealed class TrailerPopularityMetadataTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "t4j-popularity-" + Guid.NewGuid());

    public TrailerPopularityMetadataTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task UpdatePopularity_AddsValueAndPreservesExistingMetadata()
    {
        var trailer = Path.Combine(_directory, "Movie (2026).mp4");
        var sidecar = Path.ChangeExtension(trailer, ".json");
        await File.WriteAllTextAsync(
            sidecar,
            "{\"tmdbId\":42,\"title\":\"Movie\",\"year\":2026,\"genres\":[\"Horror\"],\"customField\":\"keep\"}",
            TestContext.Current.CancellationToken);

        await TrailerMetadataRefresh.UpdatePopularityAsync(
            trailer,
            123.456,
            TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(sidecar, TestContext.Current.CancellationToken));
        Assert.Equal(123.456, doc.RootElement.GetProperty("popularity").GetDouble());
        Assert.Equal("Movie", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal("keep", doc.RootElement.GetProperty("customField").GetString());
    }

    [Fact]
    public async Task UpdatePopularity_MissingSidecarIsNoOp()
    {
        await TrailerMetadataRefresh.UpdatePopularityAsync(
            Path.Combine(_directory, "missing.mp4"),
            100,
            TestContext.Current.CancellationToken);
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
