using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Trailers4Jellyfin.Configuration;
using Jellyfin.Plugin.Trailers4Jellyfin.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Tests.Services;

public sealed partial class TrailerSelectionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "t4j-metadata-" + Guid.NewGuid());
    private readonly TrailerIntroProvider _provider;
    private readonly UserDataProxy _history;
    private readonly SelectionLibraryProxy _library;
    private readonly User _user = new("viewer", "auth", "reset");
    private readonly PluginConfiguration _config = new() { NumberOfTrailers = 2 };
    public TrailerSelectionTests()
    {
        Directory.CreateDirectory(_directory);
        var manager = DispatchProxy.Create<IUserDataManager, UserDataProxy>();
        _history = (UserDataProxy)(object)manager;
        var library = DispatchProxy.Create<ILibraryManager, SelectionLibraryProxy>();
        _library = (SelectionLibraryProxy)(object)library;
        _provider = new(null!, null!, manager, TrailerRatingPolicyTests.CreatePolicy(), library, NullLogger<TrailerIntroProvider>.Instance);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{bad json")]
    [InlineData("{\"genres\":[123],\"officialRating\":\"R\"}")]
    public void BadSidecar_OnlyExcludesAffectedTrailer(string json)
    {
        var bad = Trailer("bad", json);
        var good = Trailer("good", "{\"genres\":[\"Comedy\"],\"officialRating\":\"PG\"}");
        var result = _provider.SelectTrailers(new Movie { OfficialRating = "PG", Genres = new[] { "Comedy" } }, new[] { bad, good }, _config, _user);
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
        Assert.Equal(match.Id, Assert.Single(_provider.SelectTrailers(feature, new[] { adult, other, match }, _config, _user)).Id);
    }

    [Fact]
    public void TooFewGenreMatches_PlaysFewerInsteadOfFillingFromOtherGroups()
    {
        var match = Trailer("match", "{\"genres\":[\"Comedy\"],\"officialRating\":\"PG\"}");
        var unknown = Trailer("unknown", null);
        var feature = new Movie { OfficialRating = "PG", Genres = new[] { "Comedy" } };
        var result = _provider.SelectTrailers(feature, new[] { unknown, match }, _config, _user);
        Assert.Equal(match.Id, Assert.Single(result).Id);
    }

    [Fact]
    public void PreRollPreference_UsesUserIdAndStoredHistory()
    {
        var first = Trailer("first", null);
        var second = Trailer("second", null);
        var otherUser = new User(_user.Username, "auth", "reset");
        _history.Watched.Add((_user.Id, first.Id));
        _history.Watched.Add((otherUser.Id, second.Id));
        var items = new[] { first, second };

        // Registry items do not need to have user data populated in memory.
        Assert.Equal(second.Id, Assert.Single(_provider.GetPreferredClips(items, _user, true)).Id);
        Assert.Equal(first.Id, Assert.Single(_provider.GetPreferredClips(items, otherUser, true)).Id);
    }

    [Fact]
    public void PreRollPreference_OffSkipsHistoryLookup()
    {
        var items = new[] { Trailer("first", null), Trailer("second", null) };
        _history.Watched.Add((_user.Id, items[0].Id));
        Assert.Equal(items, _provider.GetPreferredClips(items, _user, false));
        Assert.Equal(0, _history.Lookups);
    }

    [Fact]
    public void PreRollPreference_AllWatchedFallsBackWithoutChangingHistory()
    {
        var items = new[] { Trailer("first", null), Trailer("second", null) };
        foreach (var item in items) _history.Watched.Add((_user.Id, item.Id));
        Assert.Equal(items, _provider.GetPreferredClips(items, _user, true));
        Assert.Equal(2, _history.Watched.Count);
        Assert.Empty(_provider.GetPreferredClips(Array.Empty<Video>(), _user, true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TrailerPreference_DoesNotBypassRatingsOrLeaveDirectGroup(bool enabled)
    {
        _config.NumberOfTrailers = 1;
        _config.PreferUnwatchedTrailers = enabled;
        var adult = Trailer("adult", "{\"genres\":[\"Comedy\"],\"officialRating\":\"R\"}");
        var watchedMatch = Trailer("watched", "{\"genres\":[\"Comedy\"],\"officialRating\":\"PG\"}");
        var unwatchedOther = Trailer("unwatched", "{\"genres\":[\"Drama\"],\"officialRating\":\"PG\"}");
        _history.Watched.Add((_user.Id, watchedMatch.Id));
        var feature = new Movie { OfficialRating = "PG", Genres = new[] { "Comedy" } };
        var result = _provider.SelectTrailers(feature, new[] { adult, watchedMatch, unwatchedOther }, _config, _user);
        Assert.Equal(watchedMatch.Id, Assert.Single(result).Id);
    }

    [Fact]
    public void TrailerPreference_OrdersWithinGroupAndRemainsPerUser()
    {
        _config.PreferUnwatchedTrailers = true;
        var match = Trailer("match", "{\"genres\":[\"Comedy\"],\"officialRating\":\"PG\"}");
        var other = Trailer("other", "{\"genres\":[\"Comedy\"]}");
        _history.Watched.Add((_user.Id, match.Id));
        var feature = new Movie { OfficialRating = "PG", Genres = new[] { "Comedy" } };
        var items = new[] { match, other };
        var result = _provider.SelectTrailers(feature, items, _config, _user);
        Assert.Equal(new[] { other.Id, match.Id }, result.Select(item => item.Id));

        var otherUser = new User(_user.Username, "auth", "reset");
        result = _provider.SelectTrailers(feature, items, _config, otherUser);
        Assert.Equal(2, result.Count);
        Assert.Equal(2, result.Select(item => item.Id).Distinct().Count());
    }

    [Fact]
    public void TrailerPreference_AllWatchedStillUsesGenreMatching()
    {
        _config.PreferUnwatchedTrailers = true;
        _config.NumberOfTrailers = 1;
        var match = Trailer("match", "{\"genres\":[\"Comedy\"]}");
        var other = Trailer("other", null);
        foreach (var item in new[] { match, other }) _history.Watched.Add((_user.Id, item.Id));
        var result = _provider.SelectTrailers(new Movie { Genres = new[] { "Comedy" } }, new[] { other, match }, _config, _user);
        Assert.Equal(match.Id, Assert.Single(result).Id);
    }

    private Video Trailer(string name, string? json)
    {
        var path = Path.Combine(_directory, name + ".mp4");
        if (json != null)
        {
            json = json.Replace("\"officialRating\":\"PG\"", "\"certifications\":[{\"country\":\"US\",\"rating\":\"PG\"}]")
                .Replace("\"officialRating\":\"R\"", "\"certifications\":[{\"country\":\"US\",\"rating\":\"R\"}]");
        }
        if (json != null) File.WriteAllText(Path.ChangeExtension(path, ".json"), json);
        return new Video { Id = Guid.NewGuid(), Path = path };
    }
    public void Dispose() => Directory.Delete(_directory, true);
}

public class UserDataProxy : DispatchProxy
{
    public HashSet<(Guid UserId, Guid ItemId)> Watched { get; } = new();
    public Dictionary<(Guid UserId, Guid ItemId), DateTime?> Dates { get; } = new();
    public int Lookups { get; private set; }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name != nameof(IUserDataManager.GetUserDataBatch))
            throw new NotSupportedException(method.Name);
        Lookups++;
        var items = (IReadOnlyList<BaseItem>)args![0]!;
        var user = (User)args[1]!;
        return items.Where(item => Watched.Contains((user.Id, item.Id)))
            .ToDictionary(item => item.Id, item => new UserItemData { Key = item.Id.ToString(), Played = true, LastPlayedDate = Dates.GetValueOrDefault((user.Id, item.Id)) });
    }
}
