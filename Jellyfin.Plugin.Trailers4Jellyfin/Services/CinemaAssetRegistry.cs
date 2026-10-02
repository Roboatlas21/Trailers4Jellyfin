using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Services
{
    /// <summary>
    /// Registers downloaded trailers as private, unparented Jellyfin items.
    /// This follows the Local Intros Extended architecture: the plugin owns
    /// stable ItemIds without putting helper media in a normal user library.
    /// </summary>
    public sealed class CinemaAssetRegistry
    {
        private const string DownloadedTrailerProviderKey = "trailers4jellyfin.trailer";

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

        public IReadOnlyList<Video> SyncDownloadedTrailers(string folder)
        {
            lock (_syncLock)
            {
                return SyncFolder(folder);
            }
        }

        private IReadOnlyList<Video> SyncFolder(string? folder)
        {
            var registered = GetRegistered();

            if (string.IsNullOrWhiteSpace(folder))
            {
                foreach (var item in registered)
                    Delete(item);
                return Array.Empty<Video>();
            }

            if (!Directory.Exists(folder))
            {
                _logger.LogWarning(
                    "|Trailers4Jellyfin| Configured downloaded trailer folder does not exist: {Folder}",
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
                        { DownloadedTrailerProviderKey, file },
                    },
                };

                _libraryManager.CreateItem(item, null);
                active.Add(item);
                _logger.LogInformation(
                    "|Trailers4Jellyfin| Registered private downloaded trailer: {Path}",
                    file);
            }

            foreach (var item in registered)
            {
                var path = item.Path;
                if (string.IsNullOrWhiteSpace(path)
                    || !files.Contains(Path.GetFullPath(path)))
                {
                    Delete(item);
                }
            }

            return active;
        }

        private List<Video> GetRegistered()
        {
            return _libraryManager
                .GetItemsResult(new InternalItemsQuery
                {
                    HasAnyProviderId = new Dictionary<string, string>
                    {
                        { DownloadedTrailerProviderKey, string.Empty },
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

        private void Delete(Video item)
        {
            var path = item.Path;
            try
            {
                item.Path = null;
                _libraryManager.DeleteItem(
                    item,
                    new DeleteOptions
                    {
                        DeleteFileLocation = false,
                        DeleteFromExternalProvider = false,
                    });
                _logger.LogInformation(
                    "|Trailers4Jellyfin| Removed private downloaded trailer registration: {Path}",
                    path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "|Trailers4Jellyfin| Could not remove private downloaded trailer registration: {Path}",
                    path);
            }
            finally
            {
                item.Path = path;
            }
        }
    }
}
