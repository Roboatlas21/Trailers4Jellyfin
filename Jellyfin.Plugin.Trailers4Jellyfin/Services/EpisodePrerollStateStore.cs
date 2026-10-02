using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Services
{
    public sealed class EpisodePrerollStateStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };

        private readonly SemaphoreSlim _fileGate = new(1, 1);
        private readonly ILogger<EpisodePrerollStateStore> _logger;
        private readonly string _path;

        public EpisodePrerollStateStore(ILogger<EpisodePrerollStateStore> logger)
            : this(
                Path.Combine(Plugin.Instance.DataFolderPath, "episode-preroll-history.json"),
                logger)
        {
        }

        internal EpisodePrerollStateStore(
            string path,
            ILogger<EpisodePrerollStateStore> logger)
        {
            _path = path;
            _logger = logger;
        }

        public async Task<EpisodePrerollUserState> LoadUserAsync(
            Guid userId,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default)
        {
            await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
                var state = document.Users.TryGetValue(userId, out var existing) && existing != null
                    ? existing.Clone()
                    : new EpisodePrerollUserState();
                state.Normalize(nowUtc);
                return state;
            }
            finally
            {
                _fileGate.Release();
            }
        }

        public async Task SaveUserAsync(
            Guid userId,
            EpisodePrerollUserState state,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default)
        {
            await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
                var savedState = state.Clone();
                savedState.Normalize(nowUtc);
                document.Users[userId] = savedState;
                await WriteDocumentAsync(document, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _fileGate.Release();
            }
        }

        private async Task<EpisodePrerollStateFile> LoadDocumentAsync(
            CancellationToken cancellationToken)
        {
            if (!File.Exists(_path))
                return new EpisodePrerollStateFile();

            try
            {
                await using var stream = new FileStream(
                    _path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.Asynchronous);

                var document = await JsonSerializer.DeserializeAsync<EpisodePrerollStateFile>(
                    stream,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);

                if (document == null || document.Version != EpisodePrerollStateFile.CurrentVersion)
                {
                    _logger.LogWarning(
                        "|Trailers4Jellyfin| Unsupported episode pre-roll history version; resetting state");
                    return new EpisodePrerollStateFile();
                }

                document.Users ??= new();
                return document;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(
                    ex,
                    "|Trailers4Jellyfin| Invalid episode pre-roll history; resetting state");
                return new EpisodePrerollStateFile();
            }
        }

        private async Task WriteDocumentAsync(
            EpisodePrerollStateFile document,
            CancellationToken cancellationToken)
        {
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var tempPath = _path + ".tmp";

            try
            {
                await using (var stream = new FileStream(
                    tempPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        document,
                        JsonOptions,
                        cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(tempPath, _path, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }
    }
}
