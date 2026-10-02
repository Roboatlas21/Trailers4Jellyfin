using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Trailers4Jellyfin.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Services
{
    public sealed record CinemaAssetSet(
        IReadOnlyList<Video> TrailerPreRolls,
        IReadOnlyList<Video> DownloadedTrailers,
        IReadOnlyList<Video> FeaturePreRolls);

    /// <summary>
    /// Registers Cinema Mode files as private Jellyfin items instead of requiring
    /// user-visible helper libraries. This follows the same architecture used by
    /// Local Intros Extended: unparented Video rows are tagged with a provider ID,
    /// reused on later scans, and returned to Jellyfin by ItemId.
    /// </summary>
    public sealed class CinemaAssetRegistry
    {
        private const string DownloadedTrailerProviderKey = "trailers4jellyfin.trailer";
        private const string TrailerPreRollProviderKey = "trailers4jellyfin.trailer-preroll";
        private const string FeaturePreRollProviderKey = "trailers4jellyfin.feature-preroll";

        private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mkv", ".m4v", ".mov", ".webm", ".avi", ".ts", ".m2ts",
        };

        internal static StringComparer PathComparer { get; } = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<CinemaAssetRegistry> _logger;
        private readonly object _syncLock = new();

        public CinemaAssetRegistry(
            ILibraryManager libraryManager,
            ILogger<CinemaAssetRegistry> logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
        }

        public CinemaAssetSet SyncConfiguredAssets(PluginConfiguration config)
        {
            lock (_syncLock)
            {
                return new CinemaAssetSet(
                    SyncFolder(config.TrailerPreRollFolder, TrailerPreRollProviderKey, "trailer pre-roll"),
                    SyncFolder(config.DownloadFolder, DownloadedTrailerProviderKey, "downloaded trailer"),
                    SyncFolder(config.FeaturePreRollFolder, FeaturePreRollProviderKey, "feature pre-roll"));
            }
        }

        public IReadOnlyList<Video> SyncDownloadedTrailers(string folder)
        {
            lock (_syncLock)
            {
                return SyncFolder(folder, DownloadedTrailerProviderKey, "downloaded trailer");
            }
        }

        private IReadOnlyList<Video> SyncFolder(
            string? folder,
            string providerKey,
            string label)
        {
            var registered = GetRegistered(providerKey);

            if (string.IsNullOrWhiteSpace(folder))
            {
                foreach (var item in registered)
                    Delete(item, label);
                return Array.Empty<Video>();
            }

            if (!Directory.Exists(folder))
            {
                _logger.LogWarning(
                    "|Trailers4Jellyfin| Configured {Label} folder does not exist: {Folder}",
                    label,
                    folder);
                return Array.Empty<Video>();
            }

            var files = Directory
                .EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(IsPlayableVideo)
                .Select(Path.GetFullPath)
                .ToHashSet(PathComparer);

            var byPath = registered
                .Where(i => !string.IsNullOrWhiteSpace(i.Path))
                .GroupBy(i => Path.GetFullPath(i.Path!), PathComparer)
                .ToDictionary(g => g.Key, g => g.First(), PathComparer);

            var active = new List<Video>(files.Count);

            foreach (var file in files)
            {
                if (byPath.TryGetValue(file, out var existing))
                {
                    active.Add(existing);
                    continue;
                }

                var item = new Video
                {
                    Id = Guid.NewGuid(),
                    Path = file,
                    Name = Path.GetFileNameWithoutExtension(file),
                    ProviderIds = new Dictionary<string, string>
                    {
                        { providerKey, file },
                    },
                };

                _libraryManager.CreateItem(item, null);
                active.Add(item);
                _logger.LogInformation(
                    "|Trailers4Jellyfin| Registered private {Label}: {Path}",
                    label,
                    file);
            }

            foreach (var item in registered)
            {
                var path = item.Path;
                if (string.IsNullOrWhiteSpace(path)
                    || !files.Contains(Path.GetFullPath(path)))
                {
                    Delete(item, label);
                }
            }

            return active;
        }

        private List<Video> GetRegistered(string providerKey)
        {
            return _libraryManager
                .GetItemsResult(new InternalItemsQuery
                {
                    HasAnyProviderId = new Dictionary<string, string>
                    {
                        { providerKey, string.Empty },
                    },
                })
                .Items
                .OfType<Video>()
                .ToList();
        }

        private static bool IsPlayableVideo(string path)
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith("._", StringComparison.Ordinal)
                || TrailerDownloadService.IsPartialDownloadArtifact(path))
                return false;

            return VideoExtensions.Contains(Path.GetExtension(path));
        }

        private void Delete(Video item, string label)
        {
            var path = item.Path;
            try
            {
                // Jellyfin treats unparented items as internal and may delete their
                // backing path even when DeleteFileLocation is false. Clear Path on
                // the in-memory item first so removing a private registration can
                // never remove the user's actual preroll/trailer file.
                item.Path = null;
                _libraryManager.DeleteItem(
                    item,
                    new DeleteOptions
                    {
                        DeleteFileLocation = false,
                        DeleteFromExternalProvider = false,
                    });
                _logger.LogInformation(
                    "|Trailers4Jellyfin| Removed private {Label} registration: {Path}",
                    label,
                    path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "|Trailers4Jellyfin| Could not remove private {Label} registration: {Path}",
                    label,
                    path);
            }
            finally
            {
                item.Path = path;
            }
        }
    }
}
