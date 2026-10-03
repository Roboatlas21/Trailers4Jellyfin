using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Services;

public sealed class TrailerRankingStore
{
    internal const string FileName = "trailers4jellyfin-ranking.json";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly ILogger<TrailerRankingStore> _logger;
    private readonly object _sync = new();

    private string? _cachedPath;
    private DateTime _cachedWriteTimeUtc;
    private TrailerRankingManifest? _cachedManifest;

    public TrailerRankingStore(ILogger<TrailerRankingStore> logger)
    {
        _logger = logger;
    }

    internal async Task WriteAsync(
        string downloadFolder,
        TrailerRankingManifest manifest,
        CancellationToken ct)
    {
        Directory.CreateDirectory(downloadFolder);
        var path = GetPath(downloadFolder);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            var json = JsonSerializer.Serialize(manifest, JsonOptions);
            await File.WriteAllTextAsync(temporary, json, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);

            lock (_sync)
            {
                _cachedPath = path;
                _cachedWriteTimeUtc = File.GetLastWriteTimeUtc(path);
                _cachedManifest = manifest;
            }
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal bool TryGetEntry(
        string downloadFolder,
        int tmdbId,
        out TrailerRankingManifestEntry? entry)
    {
        entry = null;
        if (tmdbId <= 0 || string.IsNullOrWhiteSpace(downloadFolder))
            return false;

        var manifest = GetManifest(downloadFolder);
        return manifest != null
            && manifest.Movies.TryGetValue(tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture), out entry);
    }

    internal TrailerRankingManifest? GetManifest(string downloadFolder)
    {
        if (string.IsNullOrWhiteSpace(downloadFolder))
            return null;

        var path = GetPath(downloadFolder);
        if (!File.Exists(path))
            return null;

        var writeTimeUtc = File.GetLastWriteTimeUtc(path);
        lock (_sync)
        {
            if (string.Equals(_cachedPath, path, StringComparison.Ordinal)
                && _cachedWriteTimeUtc == writeTimeUtc)
            {
                return _cachedManifest;
            }

            try
            {
                var manifest = JsonSerializer.Deserialize<TrailerRankingManifest>(File.ReadAllText(path), JsonOptions);
                _cachedPath = path;
                _cachedWriteTimeUtc = writeTimeUtc;
                _cachedManifest = manifest;
                return manifest;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Cache the failed timestamp too so a corrupt manifest does not log once per playback.
                _cachedPath = path;
                _cachedWriteTimeUtc = writeTimeUtc;
                _cachedManifest = null;
                _logger.LogWarning(
                    ex,
                    "|Trailers4Jellyfin| Could not read ranking manifest {Path}; playback will fall back to popularity",
                    path);
                return null;
            }
        }
    }

    internal static string GetPath(string downloadFolder) => Path.Combine(downloadFolder, FileName);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
