using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Trailers4Jellyfin.Services;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using Xunit;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Tests.Services;

public sealed class TrailerRatingPolicyTests
{
    internal static TrailerRatingPolicy CreatePolicy()
        => new(DispatchProxy.Create<ILocalizationManager, RatingProxy>(),
            DispatchProxy.Create<ILibraryManager, SelectionLibraryProxy>(),
            DispatchProxy.Create<IServerConfigurationManager, ServerConfigurationProxy>());

    [Fact]
    public void CountryControlsAmbiguousRating_WithoutChoosingLowestRating()
    {
        var metadata = new TrailerMetadata { Certifications = new() { new("US", "R"), new("CA", "R") } };
        Assert.Equal(18, CreatePolicy().ResolveRating(metadata, "CA")!.Score);
        Assert.Equal(17, CreatePolicy().ResolveRating(metadata, "US")!.Score);
    }

    [Fact]
    public void UnratedAndUnrecognizedEntries_DoNotStopCountryFallback()
    {
        var metadata = new TrailerMetadata { Certifications = new() { new("CA", "NR"), new("US", "unknown"), new("GB", "15"), new("AU", "PG") } };
        Assert.Equal(15, CreatePolicy().ResolveRating(metadata, "CA")!.Score);
    }

    [Fact]
    public void RecognizedEntryWithinCountry_WinsBeforeNextCountry()
    {
        var metadata = new TrailerMetadata { Certifications = new() { new("CA", "NR"), new("CA", "R"), new("US", "PG") } };
        Assert.Equal(18, CreatePolicy().ResolveRating(metadata, "CA")!.Score);
    }

    [Fact]
    public void RemainingCountries_AreDeterministicAndNotSortedByRating()
    {
        var metadata = new TrailerMetadata { Certifications = new() { new("FR", "12"), new("DE", "16") } };
        Assert.Equal(16, CreatePolicy().ResolveRating(metadata, "NZ")!.Score);
    }

    [Fact]
    public void BareLegacyRatingsStayUnknown_QualifiedRatingsRemainUsable()
    {
        Assert.Null(CreatePolicy().ResolveRating(new() { OfficialRating = "R" }, "CA"));
        Assert.Equal(17, CreatePolicy().ResolveRating(new() { OfficialRating = "US:R" }, "CA")!.Score);
    }

    [Fact]
    public void UserAndFeatureLimits_AreBothEnforcedIncludingSubscores()
    {
        var policy = CreatePolicy();
        var user = new User("viewer", "auth", "reset") { MaxParentalRatingScore = 13, MaxParentalRatingSubScore = 0 };
        var metadata = new TrailerMetadata { Certifications = new() { new("US", "PG-13") } };
        Assert.False(policy.IsAllowed(metadata, "US", null, user));
        user.MaxParentalRatingSubScore = 1;
        Assert.True(policy.IsAllowed(metadata, "US", null, user));
        Assert.False(policy.IsAllowed(metadata, "US", new(13, 0), user));
        Assert.False(policy.IsAllowed(metadata, "US", new(10, null), user));
        Assert.True(policy.IsAllowed(metadata, "US", new(13, 1), user));
        user.MaxParentalRatingScore = 12;
        Assert.False(policy.IsAllowed(metadata, "US", new(18, null), user));
    }

    [Fact]
    public void UnresolvedTrailer_UsesTrailerUnratedPreference_EvenWithoutAgeLimit()
    {
        var user = new User("viewer", "auth", "reset");
        Assert.True(CreatePolicy().IsAllowed(new(), "US", null, user));
        user.Preferences.Add(new(Jellyfin.Database.Implementations.Enums.PreferenceKind.BlockUnratedItems, "Trailer"));
        Assert.False(CreatePolicy().IsAllowed(new(), "US", null, user));
    }

    [Fact]
    public void MetadataCountry_UsesLibraryThenServer_AndFeatureCustomRating()
    {
        var library = DispatchProxy.Create<ILibraryManager, SelectionLibraryProxy>();
        var proxy = (SelectionLibraryProxy)(object)library;
        var policy = new TrailerRatingPolicy(DispatchProxy.Create<ILocalizationManager, RatingProxy>(), library,
            DispatchProxy.Create<IServerConfigurationManager, ServerConfigurationProxy>());
        var movie = new Movie { OfficialRating = "US:PG", CustomRating = "US:R" };
        Assert.Equal("US", policy.GetMetadataCountry(movie));
        proxy.Country = "CA";
        Assert.Equal("CA", policy.GetMetadataCountry(movie));
        Assert.Equal(17, policy.GetFeatureRating(movie, "CA")!.Score);
    }

    [Fact]
    public void TmdbParsing_PreservesCountriesAndReleasePriorityAndSkipsEmptyLabels()
    {
        using var doc = JsonDocument.Parse("""
            {"results":[{"iso_3166_1":"CA","release_dates":[{"type":4,"certification":"PG"},{"type":3,"certification":"NR"},{"type":2,"certification":"R"},{"type":5,"certification":""}]},{"iso_3166_1":"US","release_dates":[{"type":3,"certification":"R"}]}]}
            """);
        Assert.Equal(new[] { new TrailerCertification("CA", "NR"), new("CA", "R"), new("CA", "PG"), new("US", "R") }, TmdbService.ParseCertifications(doc.RootElement));
    }

    [Fact]
    public async Task MetadataUpgrade_PreservesVideoAndOtherFields_AndRetriesFailedLookups()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            var video = Path.Combine(directory, "movie.mp4");
            var sidecar = Path.ChangeExtension(video, ".json");
            File.WriteAllText(video, "unchanged video");
            var original = """{"tmdbId":42,"title":"Title","genres":["Action"],"officialRating":"R","extra":"keep"}""";
            File.WriteAllText(sidecar, original);
            await TrailerMetadataRefresh.RefreshAsync(video, (_, _) => Task.FromResult<List<TrailerCertification>?>(null), CancellationToken.None);
            Assert.Equal(original, File.ReadAllText(sidecar));
            await TrailerMetadataRefresh.RefreshAsync(video, (_, _) => Task.FromResult<List<TrailerCertification>?>(new() { new("CA", "R") }), CancellationToken.None);
            using var metadata = JsonDocument.Parse(File.ReadAllText(sidecar));
            Assert.Equal("Title", metadata.RootElement.GetProperty("title").GetString());
            Assert.Equal("keep", metadata.RootElement.GetProperty("extra").GetString());
            Assert.Equal("CA", metadata.RootElement.GetProperty("certifications")[0].GetProperty("country").GetString());
            Assert.Equal("unchanged video", File.ReadAllText(video));
            await TrailerMetadataRefresh.RefreshAsync(video, (_, _) => throw new Exception("Already cached"), CancellationToken.None);
            File.WriteAllText(sidecar, original);
            await TrailerMetadataRefresh.RefreshAsync(video, (_, _) => Task.FromResult<List<TrailerCertification>?>(new()), CancellationToken.None);
            await TrailerMetadataRefresh.RefreshAsync(video, (_, _) => throw new Exception("Empty lookup must also be cached"), CancellationToken.None);
        }
        finally { Directory.Delete(directory, true); }
    }
}

public class RatingProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name != nameof(ILocalizationManager.GetRatingScore)) throw new NotSupportedException(method.Name);
        var rating = (string)args![0]!;
        var country = (string?)args[1] ?? "US";
        if (rating.StartsWith("CA-", StringComparison.Ordinal)) rating = "CA:" + rating[3..];
        if (!rating.Contains(':')) rating = country + ":" + rating;
        return rating.ToUpperInvariant() switch
        {
            "US:G" => new ParentalRatingScore(0, null),
            "US:PG" or "CA:PG" or "AU:PG" => new(10, null),
            "US:PG-13" => new(13, 1),
            "US:R" => new(17, null),
            "CA:R" => new(18, null),
            "GB:15" => new(15, null),
            "DE:16" => new(16, null),
            "FR:12" => new(12, null),
            _ => null,
        };
    }
}

public class SelectionLibraryProxy : DispatchProxy
{
    public string Country { get; set; } = string.Empty;
    public List<MediaBrowser.Controller.Entities.BaseItem> Movies { get; } = new();
    public int MovieQueries { get; private set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == nameof(ILibraryManager.GetLibraryOptions)) return new LibraryOptions { MetadataCountryCode = Country };
        if (method.Name == nameof(ILibraryManager.GetItemList)) { MovieQueries++; return Movies.ToArray(); }
        throw new NotSupportedException(method.Name);
    }
}

public class ServerConfigurationProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args)
        => method!.Name == "get_Configuration" ? new ServerConfiguration { MetadataCountryCode = "US" } : throw new NotSupportedException(method.Name);
}
