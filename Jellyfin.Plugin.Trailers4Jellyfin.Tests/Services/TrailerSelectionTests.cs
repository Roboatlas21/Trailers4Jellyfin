using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Trailers4Jellyfin.Configuration;
using Jellyfin.Plugin.Trailers4Jellyfin.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Tests.Services;

public sealed class TrailerSelectionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "t4j-metadata-" + Guid.NewGuid());
    private readonly TrailerIntroProvider _provider = new(null!, null!, null!, NullLogger<TrailerIntroProvider>.Instance);
    private readonly PluginConfiguration _config = new() { NumberOfTrailers = 2 };
    public TrailerSelectionTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{bad json")]
    [InlineData("{\"genres\":[123],\"officialRating\":\"R\"}")]
    public void BadSidecar_OnlyExcludesAffectedTrailer(string json)
    {
        var bad = Trailer("bad", json);
        var good = Trailer("good", "{\"genres\":[\"Comedy\"],\"officialRating\":\"PG\"}");
        var result = _provider.SelectTrailers(new Movie { OfficialRating = "PG", Genres = new[] { "Comedy" } }, new[] { bad, good }, _config);
        Assert.Equal(good.Id, Assert.Single(result).Id);
    }

    [Fact]
    public void RatingsAndGenreScores_AreBothPreserved()
    {
        _config.NumberOfTrailers = 1;
        var adult = Trailer("adult", "{\"genres\":[\"Comedy\",\"Adventure\"],\"officialRating\":\"R\"}");
        var match = Trailer("match", "{\"genres\":[\"Comedy\"],\"officialRating\":\"PG\"}");
        var other = Trailer("other", "{\"genres\":[\"Drama\"],\"officialRating\":\"PG\"}");
        var feature = new Movie { OfficialRating = "CA-PG", Genres = new[] { "Comedy", "Adventure" } };
        Assert.Equal(match.Id, Assert.Single(_provider.SelectTrailers(feature, new[] { adult, other, match }, _config)).Id);
    }

    [Fact]
    public void TooFewGenreMatches_FillsFromRemainingAppropriateTrailers()
    {
        var match = Trailer("match", "{\"genres\":[\"Comedy\"],\"officialRating\":\"PG\"}");
        var unknown = Trailer("unknown", null);
        var feature = new Movie { OfficialRating = "PG", Genres = new[] { "Comedy" } };
        var result = _provider.SelectTrailers(feature, new[] { unknown, match }, _config);
        Assert.Equal(new[] { match.Id, unknown.Id }, result.Select(v => v.Id));
    }

    private Video Trailer(string name, string? json)
    {
        var path = Path.Combine(_directory, name + ".mp4");
        if (json != null) File.WriteAllText(Path.ChangeExtension(path, ".json"), json);
        return new Video { Id = Guid.NewGuid(), Path = path };
    }
    public void Dispose() => Directory.Delete(_directory, true);
}
