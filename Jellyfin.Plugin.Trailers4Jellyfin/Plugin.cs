using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Trailers4Jellyfin.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Trailers4Jellyfin
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        private const int CurrentRankingSettingsVersion = 1;

        public override string Name => "Trailers4Jellyfin";

        public override Guid Id => Guid.Parse("7635bf62-6b22-4c4c-9bc7-5d55f0c2bff0");

        public static Plugin Instance { get; private set; } = null!;

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            ApplyRankingSettingsMigration();
        }

        private void ApplyRankingSettingsMigration()
        {
            var config = Configuration;
            var changed = false;

            if (config.RankingSettingsVersion < 1)
            {
                ApplyRankingSettingsVersion1(config);
                config.RankingSettingsVersion = 1;
                changed = true;
            }

            if (changed)
                SaveConfiguration(config);
        }

        private static void ApplyRankingSettingsVersion1(PluginConfiguration config)
        {
            // Version 1 intentionally forces every default changed by the ranking overhaul,
            // even when an older installation had customized one of these values.
            config.PoolRankingMode = TrailerPoolRankingMode.LifecycleScore;
            config.PlaybackRankingMode = TrailerPlaybackRankingMode.Score;
            config.ApplyPlaybackReleaseBoost = true;
            config.TheatricalRegion = "US";

            config.InTheatresMinimumVotes = 30;
            config.MaxPagesPerSource = 4;
            config.MinimumMovieBudget = 500_000;
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            yield return new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.config.html"
            };
        }
    }
}
