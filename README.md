# SpotySync

SpotySync is a portable Windows companion for keeping Spotify playlists, MusicBee static playlists, and M3U/M3U8 playlists aligned. It also backs up newly Liked Spotify tracks with the supplied custom `spot2yt` downloader and can download pasted Spotify, YouTube, and other supported media links.

The portable release keeps its configuration, OAuth token, caches, logs, sync baselines, and M3U backups beside `SpotySync.exe`. The app login uses Spotify Authorization Code with PKCE. The same compact setup screen also accepts the Spotify app secret needed only by the bundled custom downloader and stores it in that tool's private local configuration.

## Download and get started

1. Open the [**Releases**](../../releases/latest) page and download `SpotySync-<version>-win-x64.zip`. It is ready to run and includes every tool it needs: spotDL, yt-dlp, FFmpeg, Deno and the YouTube Premium token helper. You don't need to install .NET or anything else.
2. Extract the whole zip to a normal folder (not Program Files) and run `SpotySync.exe`.
3. **Sign in to Spotify:** the setup assistant opens on first launch. Sign in to the Spotify Developer Dashboard, and the assistant creates your Spotify app and connects. Each user needs their own Spotify app, which is free.
4. **Optional, for YouTube Premium (~256 kbps) audio:** be signed in to YouTube in your default browser. Firefox works best. The first download sets up the Premium token helper automatically (once, about a minute, needs internet).
5. If you use MusicBee, click **Install MusicBee plugin** in Settings and restart MusicBee.

Without Premium, songs download at YouTube's standard ~128 kbps Opus. To use a browser other than your default, copy `Tools\browser_options.example.txt` to `Tools\browser_options.txt` and set that browser to `true`.

## What's new in 1.5.0

- **New look.** Side navigation, a status bar and 49 built-in themes:
  - SpotySync
  - **Duotone** two-colour themes such as Crimson & Cobalt, Synth Sunset, Neon Tokyo and Aurora
  - Studio classics
  - true-black QD-OLED

  There's also a random theme, a custom accent colour, three font styles, rounded or sharp corners, and switches for the background glow and animations. Everything applies instantly.
- **Modular dashboard.** Six modules: Quick actions, live Download activity graph, Sync preview, Soundtrack Saver, Console and Library tools. Drag a module by its title (a preview follows the cursor and a bar shows where it will land) and resize it from its bottom-right corner (snaps to columns). Each module's ⋯ menu also sets width and height, moves it or hides it.
- **One-click Spotify setup.** The setup assistant opens the Spotify Developer Dashboard inside SpotySync, fills in the app form, reads the Client ID and secret, saves them and connects. You only sign in, tick Spotify's terms and press Save.
- **Searchable settings** (Ctrl+F). Each setting now has a description, matches are counted per category, and unsaved changes are shown in a bar. Several options that used to be INI-only are now in the UI: API retries, MusicBee bridge port, backup marker overlap, the temporary download playlist, and “continue to sync after backup”.
- **Streamlined console.** Lines are grouped per run with a summary banner and coloured by category. Progress lines such as “page 1, page 2…” update in place. You can filter by warnings or errors, search, and hide downloader detail, which is still written to the full log files.
- **Library tools** (read-only), on the new Library page:
  - songs that are in no playlist, either locally or on Spotify
  - songs in no local playlist
  - Liked Songs you haven't downloaded, with a one-click download
  - Likes that aren't in any Spotify playlist
  - duplicate files
  
  Results can be saved as an M3U8 playlist or exported to CSV.
- **Soundtrack Saver.** Download a Spotify, YouTube or other playlist or album into its own folder, for example `E:\Libraries\Music\Blockheads\`, instead of `Songs`. It then:
  - initializes the files in MusicBee
  - moves back any file MusicBee's auto-organise relocates
  - adds the folder to MusicBee's **Folders tab → filtered folders**. MusicBee has to be closed for that step: SpotySync offers to close and reopen it, or queues the change until MusicBee is closed. It always writes `MusicBee3Settings.ini.spotysync-backup` first.
- Keyboard: Ctrl+1–5 switch pages, Ctrl+F searches, F5 builds the preview, Ctrl+Enter applies it, and Ctrl+S saves settings.

## What it fixes

- Library matching uses one cached index built from Spotify Liked Songs and managed playlist metadata. It does not search Spotify once per local track, and local-to-Spotify additions are still restricted to Liked Songs by default.
- Spotify catalog relinks are recognized as the same recording when title, primary artist, and duration agree, preventing duplicate additions when Spotify gives Liked Songs and a playlist different IDs for the same song.
- Track-level previews use readable artist/title metadata, including protected Spotify-only entries, instead of exposing raw Spotify IDs.
- Protected details also identify MusicBee-only/unmatched files. From a reviewed playlist, **Never sync...** can permanently suppress one or more intentional exceptions without deleting either copy.
- Spotify playlist snapshots are cached. Unchanged playlists are not downloaded from the API again.
- HTTP 429 responses honor Spotify's `Retry-After` value and are retried with a limit. A quota-exceeded response stops with a useful error instead of retrying forever.
- Spotify's current `/playlists/{id}/items` endpoints and `/me/playlists` creation endpoint are used.
- The first playlist sync is always additive and records a three-way baseline.
- Optional later removals require an existing baseline, an unchanged preview snapshot, an explicit setting, a per-playlist cap, and a final confirmation.
- Tracks that exist on only one platform are protected: unmatched local files are not deleted, and Spotify tracks without a confident local file match are not removed.
- MusicBee integration is a small local-only bridge. Spotify login and API code live in the portable application, making plugin updates much simpler.
- MusicBee imports are verified after a refresh and retry. An optional one-second muted playback pass uses the Now Playing queue—without opening playlist tabs—to initialize lyrics/volume processing, then restores the prior track, position, playback state, mute state, and scrobbling state.
- The import handoff also reconciles a file that MusicBee renames between the final disk scan and the plugin request, so a two-track batch cannot silently collapse to one.
- Before choosing backup candidates, verified MusicBee files are merged with the disk scan. If an empty default folder disagrees with MusicBee, SpotySync adopts the real common library root instead of treating every Like as missing.
- If MusicBee's folder monitor renames or auto-organizes a new file during verification, SpotySync discards the vanished path and refreshes the folder once instead of aborting the batch. A later run also completes MusicBee initialization for already-downloaded recent Likes from an unfinished backup without downloading them again.
- Cancelling a download terminates the child downloader before cleanup. Temporary Spotify playlists are removed with a fresh cleanup token, recorded until deletion succeeds, and legacy leftovers are discovered on the next backup.
- If the custom downloader cannot find a YouTube Music source, SpotySync automatically searches YouTube, matching album chapters, and SoundCloud; scores title/artist/duration; downloads the strongest match; and applies the Spotify tags. It never records or decrypts Spotify's protected audio stream.
- Public-source recovery downloads and converts in a private staging directory, applies final title/artist tags, then publishes one complete file to the watched MusicBee folder. MusicBee cannot move intermediate streams out from under ffmpeg, and yt-dlp fragment progress is condensed.

## Quick setup

1. Extract the entire portable ZIP. Do not run it from inside the ZIP.
2. Run `SpotySync.exe` once. It creates `SpotySync.ini` beside itself and opens Settings.
3. In the [Spotify Developer Dashboard](https://developer.spotify.com/dashboard), create an app and register this redirect URI exactly: `http://127.0.0.1/callback`.
4. Paste the app's **Client ID** and **Client secret** into SpotySync Settings. Save, then choose **Connect Spotify**. SpotySync configures the bundled downloader automatically; there is no credential text file to edit by hand.
5. Confirm the music folder. This machine detects `E:\Libraries\Music\Songs`; elsewhere a new installation defaults to the current Windows user's Music folder. An existing portable configuration is never replaced unless an empty location clearly disagrees with the verified MusicBee library.
6. For MusicBee mode, close MusicBee, choose **Install MusicBee plugin**, then start MusicBee again. The top status chip should say `MusicBee: ready`.
7. On Dashboard, choose **Build sync preview**. Inspect every proposed change before selecting **Apply reviewed plan**.

Dashboard is deliberately split into two areas. **Backup songs** offers the automatic recent-Likes backup and a manual Spotify/YouTube link downloader. **Playlist sync** contains only Preview and Apply.

Spotify Development Mode may require the app owner to have Premium and limits which accounts can use a personal app. Spotify's current Development Mode also exposes playlist contents only for playlists the user owns or collaborates on. SpotySync therefore defaults to owned/collaborative playlists.

## Safe synchronization model

Playlist names are matched case-insensitively after punctuation and whitespace normalization. MusicBee folder prefixes are treated as organization rather than part of the playlist name, so `Old Playlists\1st Playlist` matches Spotify's `1st Playlist`; the full MusicBee location is still shown in the preview. If multiple MusicBee folders contain the same leaf name, SpotySync blocks that playlist instead of guessing. Smart/Auto and Radio playlists in MusicBee are read-only and skipped.

On the first successful run, SpotySync unions both sides and stores a baseline in `Data\sync-state.json`; it will not remove tracks. When removals are later enabled:

- A track present in the baseline but now missing from one side is treated as intentionally removed and is removed from the other side.
- A track not present in the baseline but newly added to either side is copied to the other side.
- A local file without a confident Liked Songs match is preserved as local-only.
- A Spotify track without a confident local file match is preserved as Spotify-only.
- A saved rule in `Data\sync-exclusions.txt` leaves that recording alone in that playlist. Delete its line from the text file to undo the rule.
- Any playlist over `MaxRemovalsPerPlaylist` is blocked.
- A changed Spotify snapshot or local playlist digest invalidates the preview.
- M3U playlists and MusicBee playlists get timestamped M3U8 backups before removals.

Each deletion baseline belongs to one local backend and one direction. Switching between MusicBee, M3U, Both, TwoWay, SpotifyToLocal, or LocalToSpotify cannot reuse a deletion baseline from a different mode; the newly selected mode begins add-only.

In `TwoWay`, deletion intent is inferred from the baseline. If a previously synchronized matched track is later removed from Spotify but remains in the local playlist, it is removed locally. If it is removed locally but remains on Spotify, it is removed from Spotify. A newly one-sided matched track is copied to the other side. Local-only/unmatched files and Spotify-only tracks without a local match stay protected, and local-to-Spotify additions remain restricted to Liked Songs by default.

Leave `EnableRemovals=False` until you have completed at least one clean add-only run and reviewed the match report. Deleting `Data\sync-state.json` deliberately resets all deletion baselines; the next run is add-only again.

## Backing up recent Liked Songs

**Back up now** looks at the five most recently modified audio files, uses the oldest timestamp in that group as its rolling cutoff, then reads Likes added since that date. Date modified is used because it normally survives copying or moving a music library more reliably than Windows creation time. The count is configurable with `RecentDownloadAnchorCount`. MusicBee metadata seeds the library check, so a large indexed library does not need to be reprobed. Only missing tracks are submitted to the downloader. The durable backup marker and first-run lookback remain safe fallbacks when fewer than the configured number of files exist.

Missing recent Likes are submitted newest-to-oldest. The custom downloader preserves that playlist order when it finalizes file timestamps, so a date-sorted MusicBee library displays the completed batch in the same order as Spotify.

The marker advances only after every requested track is confidently found in the music folder. Successful files from a partial run are kept, imported, and silently initialized, so the next run retries only what is still missing. On the other hand, a misleading downloader exit/error cannot block completion when every requested recording is independently verified on disk. Title-only files such as `Bartender.mp3` can be corroborated by their duration.

The Activity log prints the exact local folder being scanned. If MusicBee has files but the configured folder is empty, SpotySync detects their common library root, saves that corrected folder, and uses it for duplicate checks and download verification. It also recognizes the `Output folder:` reported by the custom downloader as a final safeguard.

The supplied custom `spot2yt.exe` is the default Spotify downloader. SpotySync checks the bundled `yt-dlp.exe` for updates before a download at most once every seven days; this is enabled by default and configurable in Settings. An update failure is logged but does not block the download.

Enter the Spotify app's Client ID and Client secret in the normal Settings card. SpotySync uses PKCE for its own login and automatically writes the private configuration expected by spot2yt; the internal file never needs to be opened manually and must not be shared. The public official `spotdl.exe` remains packaged as an optional alternative, but SpotySync does not select it by default.

**Download Spotify / YouTube links...** accepts one or more pasted links. Spotify URLs and URIs are sent to the configured downloader—`spot2yt.exe` by default—while YouTube and other media URLs are downloaded directly with the bundled `yt-dlp.exe`. If spot2yt reports a missing YouTube Music source, the enabled-by-default regular-YouTube fallback scores direct candidates and, when needed, exact titled chapters inside a matching artist/album upload. Starting either kind of download switches to Activity immediately.

SpotySync answers the custom downloader's internal prompts only when they actually occur and exits after the submitted GUI job. Unused link prompts are hidden, and its early `All done` message is labeled as the audio stage rather than the end of MusicBee processing.

Recovery also checks SoundCloud for independent releases. An error on one link does not cancel the remaining batch; Activity colors warnings/errors and prints a final summary.

With **Import downloaded tracks into MusicBee** enabled, the bridge refreshes MusicBee, verifies every imported path, and retries missing imports once. **Silently initialize new downloads in MusicBee for 1 second** temporarily mutes playback and disables scrobbling while each newly imported track is played; it then immediately restores the previous track, position, play/pause state, mute setting, and scrobbling setting. This triggers MusicBee's normal lyric and volume processing without waiting for lyric providers. MusicBee does not expose its final visible "No lyrics found" state reliably through the supported plugin API, so SpotySync never guesses or writes a no-lyrics flag; missing lyrics remain available for manual tagging. File-modified timestamps are preserved so library ordering is not disturbed.

## M3U-only and combined modes

Set `LocalBackend=M3U` to operate without MusicBee. SpotySync scans the configured music folder, uses `Tools\ffprobe.exe` for metadata, caches the result, and writes UTF-8 `.m3u8` files. Relative paths are enabled by default for portability.

Set `LocalBackend=Both` to use MusicBee as the authoritative local playlist source and mirror the resulting playlists into M3U8 files as well.

The M3U folder defaults to the portable `Playlists` folder beside the EXE. M3U-only preview reads playlists from that folder—not MusicBee—and Apply updates those files. Changing a backend or direction in Settings is saved before Preview, so the preview cannot silently use the previous selection.

Available directions are `TwoWay`, `SpotifyToLocal`, and `LocalToSpotify`. Include/exclude wildcard filters accept `*` and `?`; separate multiple patterns with semicolons.

**Full Likes refresh (days)** controls how often SpotySync rereads every page of Liked Songs. Between full refreshes it reads only the newest pages until it reaches a cached track, which avoids hammering Spotify for a library with thousands of Likes. **No-history fallback (days)** is used only when there are too few local audio timestamps for the rolling newest-file anchor and no durable successful-backup marker exists. Both settings remain active; **Force full refresh** bypasses the caches for the current operation only.

## Files and portability

- `SpotySync.ini` — readable settings and Spotify Client ID.
- `Data\spotify-token.json` — OAuth access/refresh token. Treat it like a password.
- `Data\sync-state.json` — successful three-way baselines and the durable backup marker.
- `Data\liked-cache.json` — cached Liked Songs index used for local matching.
- `Data\spotify-playlist-cache.json` — cached playlist contents keyed by snapshot.
- `Data\sync-exclusions.txt` — editable per-playlist tracks that SpotySync must leave alone; delete a line to re-enable that track.
- `Data\Backups` — M3U safety copies.
- `Logs` — readable activity and crash logs.
- `%LOCALAPPDATA%\MusicBeeSyncToService` — only the bridge's random local authentication token, MusicBee backups, and the last portable EXE location.

To move the application, close SpotySync, copy the complete folder, and run the EXE from its new location. Do not share a copy containing `Data\spotify-token.json`. The clean release ZIP contains no credentials or tokens.

## Command line

The same EXE supports automation:

```text
SpotySync.exe --backup
SpotySync.exe --sync --dry-run
SpotySync.exe --sync
SpotySync.exe --all
SpotySync.exe --all --refresh
```

`--sync` without `--dry-run` applies the generated plan non-interactively, so establish and review a baseline in the GUI first. Activity is written to `Logs` even when launched by Task Scheduler.

## Troubleshooting

- **MusicBee offline:** reinstall the packaged plugin, fully exit MusicBee, and reopen it. If MusicBee shows a modal dialog, close it before retrying.
- **Spotify redirect mismatch:** register `http://127.0.0.1/callback` exactly. Do not use `localhost`.
- **Spotify 403 on followed playlists:** use owned or collaborative playlists; current Development Mode restricts other playlist contents.
- **Spotify 429:** leave the cache enabled and avoid Force full refresh. SpotySync waits for ordinary rate limits automatically.
- **No download marker advance:** inspect the Activity tab and `Logs`. SpotySync intentionally leaves the marker unchanged after a partial/failed download.
- **Downloader credentials needed:** enter the Client ID and Client secret in Settings and save. SpotySync configures the custom downloader automatically.
- **Bad track match:** SpotySync recognizes reversed title/artist tags, `&` versus `and`, remaster/edition suffixes, and edition-length drift. An embedded Spotify track URL/URI in the MusicBee Comment field remains the strongest exact match.
- **Intentional edition/name mismatch:** use **Never sync...** on the reviewed playlist. The saved rule suppresses additions, removals, and protected notices for that track only in that playlist.
- **Duplicate MusicBee name:** two folder locations containing the same leaf playlist name are ambiguous; rename one before syncing. SpotySync blocks the pair and makes no changes.

## Building

Requirements: Windows, .NET SDK 8 or newer, and .NET Framework 4.7.2 targeting support. Run:

```powershell
.\build-release.ps1
```

The script:
- builds the app and plugin
- runs the policy/M3U/config tests and an off-screen UI layout smoke test
- downloads and checksum-verifies the pinned spotDL and bgutil token-helper files (plugin and server source)
- writes a clean portable ZIP under `dist`

It needs ffmpeg.exe, ffprobe.exe, deno.exe and yt-dlp.exe from one of these places:
- loose in `dist`
- a previous release ZIP in `dist`
- the locally built app's `Tools` folder

The large tool binaries are not stored in git because GitHub rejects files over 100 MB. They ship in the release ZIP instead. Your local `bin` folders, which hold your settings and Spotify login, are never touched or packaged.
