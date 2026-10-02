# Trailers4Jellyfin

A Jellyfin plugin that automatically downloads movie trailers from TMDB/YouTube and plays them through Jellyfin Cinema Mode.

This branch also manages optional trailer pre-roll and feature pre-roll folders without requiring any of those folders to be exposed as normal Jellyfin libraries.

## Cinema Mode order

When Cinema Mode is enabled, the plugin returns intros in this order:

```text
Trailer Pre-Roll
→ Downloaded Trailer(s)
→ Feature Pre-Roll
→ Movie
```

Each configured pre-roll folder contributes one random video. Leave either folder blank to skip that stage. Set **Trailers per movie** to 0 if you only want the pre-roll stages.

## Private Cinema Mode assets

Trailers, trailer pre-rolls, and feature pre-rolls are registered as private, unparented Jellyfin video items. This follows the same architecture used by Local Intros Extended: the plugin creates internal Jellyfin items with its own provider IDs and returns those ItemIds through `IIntroProvider`.

Because the items do not belong to a normal media library:

- You do **not** need a visible `Trailers` library.
- You do **not** need visible trailer/feature pre-roll libraries.
- Users do not need library access to those helper assets.
- The assets do not create normal library navigation or Recently Added rows.
- Jellyfin still gets stable ItemIds for playback and watched-state tracking.

The plugin automatically reconciles its internal registrations with the configured folders during Cinema Mode requests and trailer download runs. Files removed from disk have their private registrations cleaned up.

## Trailer metadata and filtering

Downloaded trailers keep a JSON sidecar next to each video. The sidecar stores:

- TMDB movie ID
- title
- year
- genres
- parental rating/certification

Genre matching reads the sidecar directly, so it does not depend on a Jellyfin Trailers library.

Parental-rating filtering also reads the stored certification. Unknown/missing ratings keep the existing permissive behavior and are allowed. Existing legacy sidecars are backfilled when the scheduled task encounters the corresponding downloaded trailer again.

## Requirements

- Jellyfin 12.1 and the .NET 10 SDK for building
- A free [TMDB API key](https://www.themoviedb.org/settings/api)
- *(Optional)* [yt-dlp](https://github.com/yt-dlp/yt-dlp) + [ffmpeg](https://ffmpeg.org/) for higher-quality downloads

## Installation

### Via Jellyfin Plugin Catalogue

1. In the Jellyfin dashboard go to **Admin → Plugins → Repositories**.
2. Add the repository URL from the upstream project.
3. Install **Trailers4Jellyfin** from the catalogue.
4. Restart Jellyfin.

For testing this development branch, build/install it manually instead of using the upstream catalogue package.

### Manual build

```sh
git clone https://github.com/Roboatlas21/Trailers4Jellyfin
cd Trailers4Jellyfin
git checkout feat/internal-cinema-mode-assets
dotnet publish Jellyfin.Plugin.Trailers4Jellyfin/Jellyfin.Plugin.Trailers4Jellyfin.csproj --configuration Release --output bin
```

Copy the built plugin files into the Jellyfin plugin directory and restart Jellyfin.

## Configuration

Go to **Admin → Plugins → Trailers4Jellyfin**.

### Cinema Mode

| Setting | Description |
|---|---|
| **Enable Cinema Mode** | Registers Trailers4Jellyfin as a Jellyfin intro provider |
| **Trailer Pre-Roll Folder** | Optional folder; one random video plays before trailers |
| **Trailers per movie** | Number of downloaded trailers to play; 0 disables the trailer block |
| **Match trailers to movie genre** | Prefers trailers whose stored TMDB genres match the feature |
| **Feature Pre-Roll Folder** | Optional folder; one random video plays after trailers and before the movie |

Example:

```text
/data/trailer-prerolls/
    Coming Attractions.mp4

/data/trailers/
    Movie A (2026).mp4
    Movie A (2026).json
    Movie B (2026).mp4
    Movie B (2026).json

/data/feature-prerolls/
    RocketCloud Pictures.mp4
```

None of those folders need to be added to Jellyfin as media libraries.

### Episode commercials

Set **Episode Pre-Roll Folder** to a folder of commercials and enable Cinema Mode for TV episodes in your client. The folder does not need a Jellyfin library. All frequency controls are plugin settings:

| Setting | Default | Effect |
|---|---|---|
| Chance per eligible episode | 75% | Fresh random roll after the limits below pass; 0 disables commercials |
| Minimum time between commercials | 60 minutes | Cooldown since the last commercial actually started; 0 disables it |
| Episode starts between commercials | 3 | Fresh episode starts since the last commercial; 0 disables spacing |
| Maximum commercials per window | 2 | Rolling limit per user; 0 disables this limit |
| Rolling window | 4 hours | Lookback for the limit, adjustable from 1 to 168 hours |

A new user is immediately eligible. The episode following a commercial counts toward the next spacing interval. Selecting a commercial does not consume the cooldown or quota: those are recorded when playback starts. Counters and recent commercial timestamps survive server restarts.

These are best-effort frequency controls for occasional commercials. Duplicate playback reports receive basic protection, but simultaneous playback on multiple devices can exceed a limit. Episodes with a saved Jellyfin resume position are skipped; the client should also skip intros when resuming. The random roll is made for each eligible intro request, so repeated viewings are not permanently assigned the same outcome.

### Download settings

| Setting | Description |
|---|---|
| **TMDB API Key** | TMDB v3 API key or read-access token |
| **Download Folder** | Where downloaded trailers and metadata sidecars are stored |
| **Max trailers per run** | Maximum new trailers to download in one task run |
| **Preferred video quality** | 480p/720p built-in or higher quality with yt-dlp |
| **Skip movies already in my Jellyfin library** | Avoid downloading trailers for movies you already own |
| **Skip trailers already downloaded** | Reuse existing trailer files |
| **yt-dlp path** | Optional explicit yt-dlp executable path |
| **ffmpeg path** | Optional ffmpeg path used by yt-dlp |
| **YouTube cookies file** | Optional cookies.txt path |

## Trailer rotation

The scheduled task can:

- cap the total number of downloaded trailers
- remove the oldest trailers when above the cap
- delete trailers marked watched by any user

Watched-state checks use the plugin's private Jellyfin trailer items, so they continue to work without a visible Trailers library.

## Running the task

Go to **Admin → Scheduled Tasks → Trailers4Jellyfin → Download TMDB Trailers** and run it.

No Jellyfin library scan is required after downloads. The plugin registers downloaded trailers internally.

## Client setup

The server plugin provides the Cinema Mode items, but the client still has to request Cinema Mode intros.

For Moonfin, enable:

```text
Settings → Playback & SyncPlay → Automation & Queue → Cinema Mode
```

A separate CherryFloors Cinema Mode server plugin is not required for this setup.

## Quality notes

| Mode | Max quality | Requirements |
|---|---|---|
| Built-in (YoutubeExplode) | 720p | None |
| yt-dlp | Depends on configured wrapper/options | yt-dlp + ffmpeg |

## Licence

MIT
