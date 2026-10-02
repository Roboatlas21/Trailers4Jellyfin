using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Services;

internal static class TrailerMetadataRefresh
{
    internal static async Task RefreshAsync(
        string trailerPath,
        Func<int, CancellationToken, Task<List<TrailerCertification>?>> fetch,
        CancellationToken ct)
    {
        var path = Path.ChangeExtension(trailerPath, ".json");
        if (!File.Exists(path)) return;
        var metadata = JsonSerializer.Deserialize<TrailerMetadata>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
        if (metadata?.TmdbId is not > 0 || metadata.Certifications != null) return;
        var certifications = await fetch(metadata.TmdbId.Value, ct).ConfigureAwait(false);
        if (certifications == null) return; // Retry a failed lookup on the next scheduled run.
        metadata.Certifications = certifications;
        await WriteAsync(path, JsonSerializer.Serialize(metadata), ct).ConfigureAwait(false);
    }

    internal static async Task WriteAsync(string path, string json, CancellationToken ct)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, json, ct).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
