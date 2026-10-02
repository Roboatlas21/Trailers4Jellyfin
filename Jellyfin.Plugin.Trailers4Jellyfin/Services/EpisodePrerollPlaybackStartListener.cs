using System;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Services
{
    public sealed class EpisodePrerollPlaybackStartListener
        : IEventConsumer<PlaybackStartEventArgs>
    {
        private readonly EpisodePrerollCoordinator _coordinator;
        private readonly ILogger<EpisodePrerollPlaybackStartListener> _logger;

        public EpisodePrerollPlaybackStartListener(
            EpisodePrerollCoordinator coordinator,
            ILogger<EpisodePrerollPlaybackStartListener> logger)
        {
            _coordinator = coordinator;
            _logger = logger;
        }

        public async Task OnEvent(PlaybackStartEventArgs eventArgs)
        {
            try
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null || !config.EnableCinemaMode
                    || string.IsNullOrWhiteSpace(config.EpisodePreRollFolder)
                    || config.EpisodePreRollChancePercent <= 0
                    || eventArgs.Item == null || eventArgs.Users.Count == 0)
                    return;

                var userId = eventArgs.Users[0].Id;
                var item = eventArgs.Item;

                if (CinemaAssetRegistry.IsEpisodePreRoll(item))
                {
                    await _coordinator
                        .RecordPrerollStartedAsync(userId, item.Id, eventArgs.PlaySessionId, config)
                        .ConfigureAwait(false);
                    return;
                }

                if (item is Episode episode)
                {
                    await _coordinator
                        .RecordEpisodeStartedAsync(
                            userId,
                            episode.Id,
                            eventArgs.PlaybackPositionTicks,
                            eventArgs.PlaySessionId,
                            config)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "|Trailers4Jellyfin| Failed to process episode pre-roll PlaybackStart");
            }
        }
    }
}
