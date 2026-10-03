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
- regional parental certifications with their country codes

Genre matching reads the sidecar directly, so it does not depend on a Jellyfin Trailers library.

Parental-rating filtering uses Jellyfin's country-aware rating scores, including subratings. Certifications are tried in the movie library's metadata country (or server country), then US, CA, GB, AU, then other countries alphabetically. Within a country, theatrical certifications take priority, followed by limited theatrical, digital, TV, physical and other releases. Missing, NR and unrecognized entries are skipped until a usable rating is found; ratings are never selected by lowest age.

A trailer must satisfy both the current user's parental maximum and the feature's recognized rating (including a custom rating, when set). An unknown feature rating removes only the feature comparison. Unresolved trailers follow the user's **Block unrated trailers** preference. All selection fallbacks obey these limits; if no trailers qualify, the trailer block is skipped. The advertised movie's rating is a proxy, not a separate certification of the trailer itself.

The scheduled download task upgrades regional certifications for all registered trailers with a sidecar TMDB ID, even when the movie is outside the current download sources. Successful empty lookups are cached; failed lookups are retried on a later task run. Bare legacy rating labels without country provenance remain unresolved until refreshed; qualified legacy labels can still be interpreted. Upgrades preserve videos, registered clip IDs, watched history and other sidecar fields. Playback uses local metadata only and never waits for TMDB.

## Requirements

- Jellyfin 12.1 and the .NET 10 SDK for building
- A free [TMDB API key](https://www.themoviedb.org/settings/api)
- *(Optional)* [yt-dlp](https://github.com/yt-dlp/yt-dlp) + [ffmpeg](https://ffmpeg.org/) for higher-quality downloads

## Installation

### Via Jellyfin Plugin Catalogue

1. In the Jellyfin dashboard go to **Admin → Plugins → Repositories**.
2. Add `https://raw.githubusercontent.com/Roboatlas21/Trailers4Jellyfin/main/manifest.json`.
3. Install or update **Trailers4Jellyfin** to **2.0.18.0** or newer from the catalogue.
4. Restart Jellyfin.

This fork uses the same plugin ID and configuration file, so an update preserves existing settings. External yt-dlp wrappers are also preserved. There is no need to uninstall the existing plugin first.

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

Use **Browse** beside any folder field to select a folder on the Jellyfin server, then press **Save**. In Docker, the picker shows paths inside the Jellyfin container; the host media folders must be mapped into it. Manual path entry is also supported.

### Cinema Mode

| Setting | Description |
|---|---|
| **Enable Cinema Mode** | Registers Trailers4Jellyfin as a Jellyfin intro provider |
| **Trailer Pre-Roll Folder** | Optional folder; one random video plays before trailers |
| **Trailers per movie** | Number of downloaded trailers to play; 0 disables the trailer block |
| **Match trailers to movie genre** | Direct matches, then related matches, then general fallback; default on |
| **Skip trailers for movies this user has already watched** | Excludes a trailer if any matching TMDB movie copy is watched by this user; default on |
| **Skip trailers for the movie being played** | Excludes all trailers advertising the current TMDB movie; default on |
| **Feature Pre-Roll Folder** | Optional folder; one random video plays after trailers and before the movie |

Each clip type has its own **Prefer unwatched** checkbox: trailer pre-rolls, trailers, feature pre-rolls, and episode pre-rolls. All four default to on. Existing saved values, including explicit off choices, are preserved; missing values use the new default. When enabled, the plugin prefers clips Jellyfin has not marked watched for the current user's ID; each user's history is independent, and no visible media library is required.

Pre-rolls are chosen randomly from the unwatched clips, falling back to the full pool once all are watched. Their selection behavior and episode frequency limits are unchanged. Selection does not mark a clip watched or reset watched history; Jellyfin's playback tracking determines that status.

### Downloaded trailer selection

1. Apply parental-rating restrictions, then both optional movie exclusions. Match movies only by reliable TMDB IDs; absent or unmatched movies remain eligible. One watched matching library copy excludes the trailer for that user. These are firm selection exclusions, never reversed by a fallback and never deleting files. The separate **Skip movies already in library** download option is unchanged.
2. Choose the first nonempty group: direct genre matches, related genre matches, then all remaining eligible trailers. A watched direct match still wins over an unwatched related or unrelated trailer. With genre matching off (or no usable movie genres), use the general group.
3. Score direct matches by distinct genres shared with the feature. Score related matches by distinct genres shared with the combined related-genre list. General matches have equal score. Genre comparisons ignore case; duplicates never increase scores.
4. With **Prefer unwatched trailers** on, take unwatched trailers in descending score order, then watched trailers by oldest last-played time, ignoring their scores. A watched trailer with no date is oldest. With the preference off, ignore trailer history and use descending score for every slot. Randomize equal scores/dates.
5. Fill only from the chosen group, without duplicate clip IDs. If it contains fewer clips than requested, play fewer; never move to another group just to fill the count.

The trailer's own Jellyfin watched flag and last-played date belong to the current user. Missing watched records are unwatched, and queuing does not record a watch. Full movie history is used only by the watched-movie exclusion.

Related genres are directional and expanded once, with equal weight. Combine the rows for all movie genres and remove duplicates; never follow a related genre into its own row.

| Movie genre | Related trailer genres |
|---|---|
| Action | Adventure, Thriller |
| Adventure | Action, Fantasy, Science Fiction |
| Animation | Use the movie's other genres |
| Comedy | Romance, Drama |
| Crime | Thriller, Mystery |
| Documentary | Use the movie's other genres |
| Drama | Romance, History |
| Family | Animation, Adventure, Fantasy |
| Fantasy | Adventure, Science Fiction |
| History | War, Drama, Documentary |
| Horror | Thriller, Mystery |
| Music | Use the movie's other genres |
| Mystery | Thriller, Crime |
| Romance | Comedy, Drama |
| Science Fiction | Adventure, Fantasy, Action |
| TV Movie | Use the movie's other genres |
| Thriller | Mystery, Crime, Action |
| War | History, Action, Drama |
| Western | Adventure, Action, Drama |

Animation, Documentary, Music and TV Movie add no related genres themselves. They still count as direct matches; their other movie genres supply related matches. Animation does not automatically imply Family.

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

### TMDB discovery and ranking

All four discovery sources default to enabled:

| Source | Default query/filter |
|---|---|
| **In Theatres** | TMDB `/movie/now_playing`, primary release within the past 12 months, at least 50 votes, rating at least 6.5 |
| **Coming Soon** | Discover by primary release date from today through the next 6 months, at least 0 votes, source-sorted by popularity |
| **Popular** | Discover by primary release date within the past 12 months, at least 100 votes, rating at least 6.0, sorted by popularity |
| **Top Rated** | Discover by primary release date within the past 12 months, at least 500 votes, sorted by vote average |

The past and Coming Soon windows are independently configurable. Discover filters are sent to TMDB before pagination; In Theatres keeps TMDB's dedicated endpoint and applies its vote/rating/date checks locally.

Results are merged and deduplicated by TMDB movie ID. The final pool is ranked by current TMDB popularity, except movies that qualify through Coming Soon receive a configurable ranking-only multiplier (default **3×**). Source membership is otherwise not a quota.

### Download settings

| Setting | Default | Description |
|---|---:|---|
| **TMDB API Key** | — | TMDB v3 API key or read-access token |
| **Download Folder** | — | Where downloaded trailers and metadata sidecars are stored |
| **Max trailers per run** | 20 | Maximum successful new downloads in one scheduled run |
| **Pages per source** | 3 | TMDB pages fetched from each enabled source |
| **Minimum movie budget** | $10,000,000 | Reliable known budgets below this amount are excluded |
| **Budget metadata sanity floor** | $1,000 | Positive budgets below this value are treated as unreliable/unknown and allowed |
| **Minimum movie runtime** | 45 minutes | Known shorter runtimes are excluded; missing/zero runtime is allowed |
| **Preferred video quality** | 720p | Higher qualities can use yt-dlp + ffmpeg |
| **Skip movies already in my Jellyfin library** | Off | Optional TMDB-ID library exclusion |
| **Skip trailers already downloaded** | On | Reuse an existing file/sidecar instead of re-downloading it |
| **yt-dlp path** | — | Optional explicit yt-dlp executable path |
| **ffmpeg path** | — | Optional ffmpeg path used by yt-dlp |
| **YouTube cookies file** | — | Optional cookies.txt path |

Budget and runtime are read from TMDB movie details. Missing details are allowed rather than causing a false rejection.

## Trailer rotation

**Max trailers to keep** defaults to **100** and represents the desired current ranked pool, not a FIFO/oldest-file queue.

Each scheduled run:

1. Fetches and ranks the current TMDB candidate set.
2. Walks the ranking through budget/runtime checks and trailer availability.
3. Keeps scanning below the nominal top 100 whenever a movie has no suitable TMDB/YouTube trailer, until the desired pool contains 100 trailer-capable movies or the candidate list is exhausted.
4. Existing local trailers count as trailer-capable immediately; missing movies must currently expose a suitable trailer before they can reserve a pool slot.
5. Keeps already-downloaded trailers whose TMDB IDs are still in that desired pool.
6. Downloads up to the per-run limit of missing desired trailers.
7. If the folder is over the configured pool size, retires old out-of-target, duplicate or unidentified legacy files first.

A transient yt-dlp/download failure does not immediately blacklist a movie: if TMDB still advertises a suitable trailer, that movie remains in the desired set and is retried on a later run. Movies with no suitable TMDB trailer do not reserve slots.

Sidecars contain the stable TMDB movie ID, so title changes do not break reconciliation. Legacy files without an ID are matched by their existing title/year filename when they are encountered as a desired candidate and have their sidecar upgraded; otherwise they age out gradually as ranked replacements arrive. This avoids deleting a full legacy pool before the 20-per-run replacement limit can refill it.

The optional **Delete watched trailers** behavior remains available and runs before ranked reconciliation.

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
