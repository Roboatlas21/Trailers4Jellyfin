using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.Trailers4Jellyfin.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Tests.Services;

public sealed class CinemaAssetRegistryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "t4j-assets-" + Guid.NewGuid());
    private readonly CinemaAssetRegistry _registry;
    private readonly LibraryProxy _library;

    public CinemaAssetRegistryTests()
    {
        Directory.CreateDirectory(_directory);
        var manager = DispatchProxy.Create<ILibraryManager, LibraryProxy>();
        _library = (LibraryProxy)(object)manager;
        _registry = new CinemaAssetRegistry(manager, NullLogger<CinemaAssetRegistry>.Instance);
    }

    [Fact]
    public void CaseOnlyRename_ReturnsPlayablePath()
    {
        var original = Path.Combine(_directory, "Commercial.mp4");
        var renamed = Path.Combine(_directory, "commercial.mp4");
        File.WriteAllText(original, "video");
        _registry.SyncEpisodePreRolls(_directory);
        File.Move(original, renamed);
        Assert.True(File.Exists(Assert.Single(_registry.SyncEpisodePreRolls(_directory)).Path));
    }

    [Fact]
    public void CaseSensitiveFiles_RemainDistinct()
    {
        if (OperatingSystem.IsWindows()) return;
        File.WriteAllText(Path.Combine(_directory, "Commercial.mp4"), "one");
        File.WriteAllText(Path.Combine(_directory, "commercial.mp4"), "two");
        Assert.Equal(2, _registry.SyncEpisodePreRolls(_directory).Count);
    }

    [Fact]
    public void Rescan_ReusesIds_AndUnregisteringPreservesFiles()
    {
        var path = Path.Combine(_directory, "Commercial.mp4");
        File.WriteAllText(path, "video");
        var first = Assert.Single(_registry.SyncEpisodePreRolls(_directory));
        Assert.Equal(first.Id, Assert.Single(_registry.SyncEpisodePreRolls(_directory)).Id);
        Assert.Empty(_registry.SyncEpisodePreRolls(string.Empty));
        Assert.True(File.Exists(path));
        Assert.Single(_library.DeletedPaths);
        Assert.Null(_library.DeletedPaths[0]);
    }

    [Fact]
    public void StagingFiles_AreExcluded()
    {
        foreach (var name in new[] { "Film.temp.mp4", "Film.temp.f137.mp4", "Film.mp4.part", "Film.temp.json.tmp" })
            File.WriteAllText(Path.Combine(_directory, name), "partial");
        Assert.Empty(_registry.SyncDownloadedTrailers(_directory));
    }

    public void Dispose() => Directory.Delete(_directory, true);
}

public class LibraryProxy : DispatchProxy
{
    private readonly List<BaseItem> _items = new();
    public List<string?> DeletedPaths { get; } = new();

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == "GetItemsResult")
        {
            var query = (InternalItemsQuery)args![0]!;
            return new QueryResult<BaseItem> { Items = _items.Where(i => query.HasAnyProviderId!.Keys.Any(key => i.ProviderIds?.ContainsKey(key) == true)).ToArray() };
        }
        if (method.Name == "CreateItem") { _items.Add((BaseItem)args![0]!); return null; }
        if (method.Name == "DeleteItem")
        {
            var item = (BaseItem)args![0]!;
            DeletedPaths.Add(item.Path);
            _items.Remove(item);
            return null;
        }
        throw new NotSupportedException(method.Name);
    }
}
