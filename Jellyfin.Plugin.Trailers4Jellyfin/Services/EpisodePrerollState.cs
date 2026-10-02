using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Services
{
    public sealed class EpisodePrerollStateFile
    {
        public const int CurrentVersion = 1;
        public int Version { get; set; } = CurrentVersion;
        public Dictionary<Guid, EpisodePrerollUserState> Users { get; set; } = new();
    }

    public sealed class EpisodePrerollUserState
    {
        public int EpisodesSincePreroll { get; set; } = int.MaxValue;
        public List<DateTimeOffset> PrerollStartsUtc { get; set; } = new();

        public void Normalize(DateTimeOffset nowUtc)
        {
            EpisodesSincePreroll = Math.Max(EpisodesSincePreroll, 0);
            PrerollStartsUtc ??= new List<DateTimeOffset>();
            // Retain the largest configurable window so increasing it keeps recent history.
            var cutoff = nowUtc - TimeSpan.FromDays(7);
            PrerollStartsUtc.RemoveAll(timestamp => timestamp <= cutoff);
        }

        public EpisodePrerollUserState Clone()
        {
            return new EpisodePrerollUserState
            {
                EpisodesSincePreroll = EpisodesSincePreroll,
                PrerollStartsUtc = new List<DateTimeOffset>(PrerollStartsUtc ?? new List<DateTimeOffset>()),
            };
        }
    }
}
