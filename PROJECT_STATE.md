# SpotySync — Project State

_Last updated: 2026-11-20_

## Overview
Portable Windows app (C# WinForms) that keeps Spotify playlists, MusicBee static playlists and
M3U/M3U8 playlists in sync using a safe, baseline-based three-way model, and backs up newly
Liked Spotify tracks via the bundled custom `spot2yt` downloader (with yt-dlp fallback). Includes
a small local-only MusicBee bridge plugin. Full user documentation is in `README.md`.

## Goals / Objectives
- [x] Safe add-only first sync, guarded removals, per-playlist "Never sync" exclusions
- [x] Recent-Likes backup with MusicBee import/initialization
- [ ] **1.5.0**: new themed/modular UI, searchable settings, streamlined console, Library tools,
      Soundtrack Saver. Currently iterating as test builds (`1.5.0-T1`, `-T2`, …) until the user
      approves; then set `<Version>1.5.0</Version>`.

## Current State
- Current build: **1.5.0** (user dropped the -T suffix on 2026-11-12); zip removed from `dist/` to save space (rebuild with `build-release.ps1`),
  also extracted in a separate test folder with the user's data carried over.
  Keep the folder small: `dist/` holds only the newest zip; the build script deletes bin/obj/.build-cache.
  Never leave extracted package folders in `dist/` (the T13 session did — 1.5 GB).
- White flash on appearance changes (maximized, worse when clicking fast): caught with the live recorder
  (`SpotySync.Ui.Smoke.exe --watch <sec> <dir>`): the WHOLE window went solid white for one frame. Cause: the
  cross-fade was a separate layered top-level window (`OverlayWindow`), which Windows sometimes showed blank.
  Fix: `CrossFade.cs` now fades INSIDE the window with a click-through child `FadeLayer` (GDI AlphaBlend of an
  old screen snapshot over a DrawToBitmap render of the new look); fast repeats retarget the running layer.
  Awaiting the user's confirmation. (`OverlayWindow` is still used by the dashboard drag ghost.)
  Performance (user: "so slow and laggy"): fade is all GDI on screen-format DDBs (screen BitBlt for old, WM_PRINT
  of the host's children for new), repaints capped at ~75/s, and a click while a fade runs finishes it and applies
  instantly. Measured maximized: theme click 74 ms with fade / 24 ms without; worst stall during fade ~69 ms.
  Durations: theme 0.2 s, page 0.12 s.
- Fast-scroll text trails/duplicates: `SmoothScroller.Apply` calls `target.Update()` right after each scroll
  step so the exposed strip is painted before the next pixel blit (a full RedrawWindow per frame was too slow).
- Settings no longer scrolls into empty space after "Data & maintenance" (bottom padding fixed at 24 px).
- Screen capture for tests must use BitBlt with CAPTUREBLT (`ScreenGrab.cs`); `CopyFromScreen` omits layered
  windows, which is why the first probes never saw the flash.
- T1 feedback fixed in T2: laggy window drag/hover, Settings rail + scrolling broken, sidebar text
  overlap, single-colour themes, filler modules (At a glance / System check), no module resize or drag
  ghost, DragDrop error popup (it came from the smoke test running MTA → test now runs STA and never
  shows dialogs).

### T2 rendering rules (keep these — they fixed the lag)
- No `WS_EX_COMPOSITED` and no `Color.Transparent` painting chains. `Ui.Restyle` gives every container a
  solid colour inherited from its parent; custom controls (`ISelfBackground`) paint their background via
  `Ui.PaintBack` (solid parent colour, or a parent `IBackdrop` such as `PageHeader`'s cached glow bitmap).
- Custom layout panels with `AutoScroll` must call `base.OnLayout` (that is what creates the scrollbars).
- `WheelRouter` (IMessageFilter) sends the mouse wheel to the control under the cursor.
- Dashboard layout string `[Appearance] DashboardCards` = `[-]id:span:height,…`.
- Setup assistant (`SetupAssistant.cs`) uses WebView2 (`Microsoft.Web.WebView2` 1.0.2903.40, profile in
  `Data\WebView2`), falls back to clipboard detection, and never ticks Spotify's terms for the user.
- Version lives in `src/SpotySync.App/SpotySync.App.csproj`; `build-release.ps1` now reads it from there.
- Build: `./build-release.ps1` (builds, runs `tests/SpotySync.Core.Tests` and `tests/SpotySync.Ui.Smoke`,
  publishes single-file exe, zips into `dist/`). Tool binaries are reused from `SpotDL/`, loose files in
  `dist/`, `C:\Program Files\SpotySync\Tools` (current source on this PC), or the latest zip in `dist/`.
  The script now only deletes old `SpotySync-*` packages from `dist/`, nothing else.
- UI smoke test writes screenshots of every page/theme/dialog to `.build-cache/ui-captures` (or a
  folder passed as the first argument) — use these to review UI changes.

### 1.5.0 UI architecture (src/SpotySync.App)
- `Theme.cs` — palette engine ported from TMSF/T320 Pool Planner (`ThemeCatalog`: SpotySync group,
  17 "Studio classics", 13 QD-OLED, random pool). `Theme.Apply(...)` + `Theme.Changed` event.
- `Ui.cs` — custom-painted controls (RoundedButton, Card, NavItem, StatusPill, KpiTile, ToggleSwitch,
  FieldBox/NumberBox, Chip, CardGrid, VStack, SwatchGrid, GlowHost) + `Ui.Restyle` theme walker.
- `ConsoleView.cs` — classified/collapsing log, filters, per-run summaries (RTF rendering).
- `SettingsView.cs` — searchable SettingRow/SettingGroup page with category rail.
- `MainForm*.cs` partials: shell, Dashboard (modular cards), Preview, Library, Soundtrack, Settings.
- Pages: 0 Dashboard, 1 Sync preview, 2 Library, 3 Console, 4 Settings.
- Appearance prefs persist in `SpotySync.ini` `[Appearance]`; `SoundtrackFolder` in `[Paths]`.

### New Core pieces
- `LibraryInsights.cs` — read-only report from `PreparedSync` (unplaylisted, no-local-playlist,
  liked-missing, liked-not-in-playlist, duplicates) + M3U/CSV writers.
- `MusicBeeSettingsFile.cs` — adds folders to MusicBee `NavigatorFilteredFolders` (Folders tab
  filter) only while MusicBee is closed; backup `MusicBee3Settings.ini.spotysync-backup`; pending
  queue in `Data/musicbee-pending-folders.txt`.
- `SpotySyncService.SaveSoundtrackAsync` — downloads (into Songs, as spot2yt dictates), moves new
  files into `<SoundtrackFolder>\<Name>\`, MusicBee import+prime, then moves back files MusicBee relocates.

## Next Steps
1. Maximized-only white flicker: get exact repro steps from the user (which action, which monitor, does it
   happen with Animations off?), then reproduce with the probe (`SPOTYSYNC_FLICKER_MAX=1`).
2. Further fixes ship as 1.5.1+ (version in csproj); replace the build in `#UserTesting`
   (carry over SpotySync.ini / Data / Logs / Tools credentials).
3. During testing the user's live MusicBee bridge reported "plugin update needed" — they may need
   Settings › Install MusicBee plugin, then restart MusicBee.
3. Unverified: whether MusicBee auto-organise re-moves soundtrack files after SpotySync moves them
   back (the guard re-imports once, then warns). Auto-organise rules aren't in MusicBee3Settings.ini.

## Key Decisions & Notes
- Spotify auth: Authorization Code with PKCE; redirect URI `http://127.0.0.1/callback`.
- Spotify Development Mode only exposes owned/collaborative playlists → that is the default scope.
- Never records or decrypts Spotify's protected audio; downloads come from public sources.
- Deleting `Data\sync-state.json` resets deletion baselines (next run is add-only).
- User's music library on this PC: `E:\Libraries\Music\Songs`; soundtrack folders live beside it
  (`E:\Libraries\Music\Blockheads`, `Minecraft`, `RDR2`, `RoR2`, `Wanderstop`).
- MusicBee settings on this PC: `%AppData%\MusicBee\MusicBee3Settings.ini` (installed, not portable).
  Folders tab uses "filtered folders" (`NavigatorShowFilteredFolders=true`). MusicBee rewrites the ini on exit.
- 2026-11-12 — Dashboard drag-to-reorder only enabled on STA threads (headless test host crashed with
  "DragDrop registration did not succeed").
- 2026-11-12 — Iterations were `1.5.0-T1`…`-T13`; the user then asked for plain `1.5.0`.
- Flicker probe (`tests/SpotySync.Ui.Smoke/FlickerProbe.cs`): `SPOTYSYNC_FLICKER_PROBE=1` shows the window on
  screen over a white backdrop and records thin screen strips (~70 fps; GDI capture is vsync-bound) while
  switching pages/themes/windows. `SPOTYSYNC_FLICKER_MAX=1` maximizes; also `_THEME=<id>`, `_NOANIM=1`,
  `_TOPMOST=0`. Minimise/restore hits are expected. The smoke test's settings persist in its output folder.

## Session Log
- 2026-10-25 — 1.4.8 docs/build script updated (last recorded work).
- 2026-11-12 — Workspace reorganization; this state file created.
- 2026-11-12 — 1.5.0-T2: rendering rewrite (opaque painting, no composited mode), settings scroll fix,
  wheel routing, 12 duotone themes, resizable/draggable module dashboard, live download graph, WebView2
  Spotify setup assistant. T2 placed in #UserTesting, T1 removed.
- 2026-11-12 — 1.5.0-T1: full UI redesign (themes, modular dashboard, searchable settings, console),
  Library tools, Soundtrack Saver, MusicBee Folders-tab registration, new tests, safer build script.
- 2026-11-12 — 1.5.0-T3: one-click setup also installs/updates the MusicBee plugin (hash compare with the
  packaged DLL; closes MusicBee gracefully, copies to %AppData%\MusicBee\Plugins, reopens it). T2 removed
  from #UserTesting. User's installed plugin (Oct 25 build) was outdated → bridge said "plugin update needed".
- 2026-11-12 — 1.5.0-T4: setup uses the user's normal browser + clipboard detection (WebView2 removed),
  auto-continues when both keys are copied; no gradient buttons/logo (Accent2 only for labels, glow,
  graph); fixed ghost-button artifacts (Button never gets a background pass → RoundedButton paints
  PaintBack in OnPaint); wrapping toolbars, compact header pills, proportional status bar, dense
  dashboard packing; drag/resize now tracked by a polling timer (overlay windows stole mouse capture,
  so resizes previewed but never applied) and covered by a simulated-gesture smoke test; rail click
  clears an active settings search. Smoke test also captures every page at 1340x870 and 1100x720.
- 2026-11-12 — 1.5.0-T5: high-refresh smoothness. `Motion.cs` adds `FrameClock` (DwmFlush vsync thread →
  BeginInvoke on a window handle; WinForms timers cap at ~64 Hz) and `SmoothScroller` (eased wheel
  scrolling + animated Settings rail jumps); module drag/resize tracked per display frame. Note: frame
  callbacks MUST be marshalled via a Control handle (a SynchronizationContext capture ran them on the
  thread pool and crashed). T4 replaced in #UserTesting.
- 2026-11-12 — 1.5.0-T6/T7: setup detects earlier SpotySync installs (`ExistingSetup.cs`; e.g.
  C:\Program Files\SpotySync = the user's working Spotify app + login) → "Use my existing setup" imports
  keys, folders/options and Data (login, caches, baselines) read-only from the old folder. The user's
  SpotDL app (996003…) lacked the redirect URI → "redirect_uri: Not matching configuration"; a 20 s
  stall hint now explains it. Install plugin / MusicBee pill now update AND restart MusicBee (old DLL
  stays loaded until restart). Module gestures now read the physical button (GetAsyncKeyState) and
  also end on the card's MouseUp — Control.MouseButtons stayed "down" after the overlay took the
  release (T5 bug). Build preview / errors / library analysis don't switch pages while the Console
  module is visible on the dashboard. T5 was still open, so it was left in #UserTesting.
- Gotchas: never edit source files with PowerShell Get-Content/Set-Content (PS 5.1 reads UTF-8 as ANSI →
  mojibake; happened to SetupAssistant.cs in T5). Pre-existing: TrackMatcher.cs line 16 regex has
  mojibake for en/em dashes (from Oct 15) — en-dash "remaster" suffixes aren't stripped; not yet fixed.
- 2026-11-12 — 1.5.0-T8: REAL-mouse test (`tests/SpotySync.Ui.Smoke/RealInput.cs`, opt-in via
  SPOTYSYNC_REAL_INPUT=1 — it moves the user's cursor ~2 s; warn first) found the module-resize bug:
  sizes were saved but CardGrid was never re-laid out (ResumeLayout(true) does nothing when only the
  Spans/Heights dictionaries change) → ApplyDashboardLayout now forces PerformLayout. Real resize + title
  drag verified. Spotify login now tries redirect forms in turn with a listener on each (configured,
  /callback any port, / any port, :9900/, :8888/callback) and remembers the one accepted (Spotify only
  reports a mismatch after sign-in, so it can't be probed). Startup asks once to reuse a logged-in earlier
  setup; One-click setup adopts it directly. MusicBee pill: "no bridge answer" is now "closed"/"plugin not
  responding", not "update needed"; re-checked every 8 s. Streamlined: no page subtitles, Tools/Safety pills
  only when relevant, no status-bar Apply, no module "Open" buttons, Console toolbar only "Downloader
  detail", Settings › Layout = show/hide switches only. Rail highlights the last section at the bottom.
- 2026-11-12 — 1.5.0-T9: Never-sync editor (`ExclusionReviewDialog`) shows a playlist's saved rules
  (ticked, untick = remove via `SyncExclusionStore.Remove`) plus this preview's changes; Settings ›
  "Manage ignored tracks" edits all rules. Rules always live in this copy's Data\sync-exclusions.txt;
  importing an earlier setup MERGES its rules. Simple console (default on, `[Appearance] ConsoleSimple`):
  `LogEntry.Milestone/Simple` from `LogClassifier.Simplify`; yt-dlp now runs with `--newline` and the
  Downloader logs `progress | <name> | <pct>` (10 % steps) → "Downloading X 40%"; spot2yt gives no
  per-song progress, so `DownloadMonitor.FileArrived` logs "Downloaded: Artist - Title  n/N" per file.
  Default dashboard = the user's layout (`DefaultDashLayout` in MainForm.Dashboard.cs). Download-activity
  graph softer and history length scales with module width. T8 was open → left in #UserTesting.
- 2026-11-12 — 1.5.0-T10: MusicBee import now retries twice (3 s) — the T8 "MusicBee API operation failed"
  was MusicBee's folder monitor dropping spot2yt's temp `.webm` at the same second (MusicBee ErrorLog.dat
  "dead file link removed"). Unavailable songs are one plain warning (no duplicate MainForm WARNING).
  Console keeps the user's selection while lines arrive; Ctrl+C/Ctrl+A in Console + Console module.
  Settings sections no longer cut off (VStack.HeightFor now subtracts child margins like OnLayout;
  smoke test checks every section fits at 1600/1340/1100). Animations (`Animate.cs`): `Tween` for eased
  hover (buttons, nav, chips) and switch knobs, `CrossFade` (screen capture fades out) for theme/appearance
  changes, page switches and sidebar collapse; CardGrid glides modules to new spots (a new layout mid-glide
  re-targets; layout during a glide frame is ignored via `inGlideStep`); progress bar on the frame clock.
- 2026-11-12 — 1.5.0-T11/T12: white flash fixed — overlay windows (cross-fade, drag ghost/marker) now derive
  from `OverlayWindow` (dark BackColor + DWMWA_CLOAK until the first WM_PAINT). Console module: word-wrap +
  compact lines (readable at 1 column). Module hover highlight (Card.HoverHighlight, accent border via Tween).
  Soundtrack Saver root cause: MusicBee library settings (E:\Libraries\Music\MusicBee\MusicBeeLibrarySettings.ini)
  had MusicOrganiseEnabled + KeepOrganised with MusicFromFolder=E:\ → MusicBee moved every imported song into
  Songs and deleted the emptied soundtrack folder (move-back then failed: folder gone). Now: before saving,
  SpotySync asks once to narrow MusicFromFolder to the Songs folder (`MusicBeeLibrarySettings.cs`, backup
  .spotysync-backup, MusicBee closed/reopened, Folders-tab entry added in the same restart); move-back recreates
  the folder; `SoundtrackManifest` (Data\soundtracks\<name>.txt) enables "Repair" from the soundtrack list
  (moves only files written at/after the save time, incl. MusicBee's "_1" collision names). The user's first
  Straftat run (T10) ended up in Songs; the user then deleted those 33 files (Recycle Bin, 21:13).
- 2026-11-12 — 1.5.0-T13 (dist/SpotySync-1.5.0-T13-win-x64.zip; publish had to run from the user terminal - agent shell cannot reach nuget.org on this PC; no #UserTesting folder exists here): Sync preview grid scrolls pixel-smooth
  (`SmoothScroll.cs`: `SmoothGrid` at full height inside `GridScrollHost` + fixed `GridHeader`; DataGridView alone can
  only jump whole rows); details pane and dashboard module use `SmoothRichText` (EM_SETSCROLLPOS); PlanList eases too.
  Expand details / Show playlist list glide the divider (`AnimateSplitter`). CrossFade flicker fixed: overlay is
  painted + uncloaked synchronously (`OverlayWindow.RevealNow`, DwmFlush) BEFORE the change, and everything is painted
  (RedrawWindow UPDATENOW|ALLCHILDREN) before the fade. Stale playlist on navigation fixed: details follow
  `CurrentCellChanged` and `SelectPlanRowCore` (SelectionChanged fired before the current row moved). Dashboard Sync
  preview module: clicking a playlist shows its planned changes in the module ("All playlists" / "Open in Sync preview").
  Preview no longer vanishes: `SaveSettingsFields` (run by every action) only discards it when `SyncFingerprint()`
  changes; after Apply the plan stays, marked "Applied" (`planAppliedAt`, can't be applied twice); Run everything keeps
  the old preview until the new one is built. Removed the "Continue to playlist sync after backup" toggle (it only
  affected Run everything; off = same as the Backup button). `AutoSyncAfterBackup` stays in the ini but is unused.
  Smoke test gained `ValidatePreviewInteractions`.
- 2026-11-12 — Merged into one folder: removed `SpotySync_T12` (snapshot; nothing unique vs T13 source) and shrank
  SpotySync 2.4 GB → ~2 MB (extracted T12/T13 packages + zips in dist, .build-cache, bin/obj). Version → `1.5.0`.
  Hunted the maximized-only white flicker with a new screen-recording probe: no bright frames in normal or
  maximized windows, the user's theme, animations on/off, topmost on/off (the user watched; saw none either).
  Fixed a real bug the probe runs exposed: the Settings rail highlight got stuck if you scrolled within 0.9 s of
  clicking a section (always with Animations off); `SettingsView.railRecheck` re-checks after the hold.
- 2026-11-12 (later) — Caught the appearance-change white flash with a live recorder while the user spam-clicked
  (13 full-window white frames in 2.5 min). Replaced the overlay-window cross-fade with an in-window `FadeLayer`;
  fixed fast-scroll text trails in `SmoothScroller`. Rebuilt 1.5.0 (41 tests + smoke pass) and swapped the exe into
  `#UserTesting\SpotySync-1.5.0-win-x64` (user data untouched); dist zip removed again to keep the folder small.
- 2026-11-12 (later) — User confirmed the white flash is gone but it felt very slow. Profiled on screen
  (`SPOTYSYNC_TIMING=1` probe mode): fade rewritten to GDI DDBs, frame cap, instant switch on rapid clicks; scroll
  fix made cheap; Settings end padding trimmed. Rebuilt 1.5.0 (all tests pass), exe swapped into #UserTesting.
- 2026-11-12 (later) — User feedback round: sidebar Appearance button → `SettingsView.ShowGroup("appearance")` (no
  search); theme gallery (`SwatchGrid`) paints from a cached bitmap (scroll stutter past previews); sync preview +
  Library persist in `Data\last-preview.json` (`MainForm.Persist.cs`; restored preview is view-only, Apply needs a
  rebuild; deleted when a settings change invalidates it); Soundtrack card fits its description to the module and
  hides it when short; launch white flash fixed (main window DWM-cloaked until first paint in OnShown); Library uses
  new virtual `InsightTable` (pixel-smooth, fast); "Don't download" exclusions in `Data\download-exclusions.txt`
  (`DownloadExclusionStore`, Core); double-click opens tracks via `spotify:track:` when the app is installed;
  tabs now Liked not downloaded / Local in no playlist (=NoLocalPlaylist) / Liked in no playlist / Duplicates.
  Smoke test gained `ValidatePreviewSnapshot`. Build swapped into #UserTesting when the user closes the app.
- 2026-11-12 (later) — Themes: +12 Duotone and a new "Inspired by your games" group (21 two-tone palettes from games
  installed on this PC: Elden Ring, RDR2, Stardew, Cult of the Lamb, Noita, Gungeon, Risk of Rain, Subnautica,
  Factorio, Inscryption, Into the Breach, Mouthwashing, Baba Is You, Skyrim, God of War, Minecraft, Tiny Glade, ABZU,
  CoD Zombies, Astroneer, Nuclear Throne; names hint at the game). User: enough single-colour themes — add no more.
- 2026-11-13 — Library "Liked, not downloaded" now has a checkbox column (tick = download, untick = exclude;
  rows stay in place, click a selected row's box to flip all selected). `InsightTable` caches each painted row as a
  bitmap (scrolling only copies images). Build queued to swap into #UserTesting when the app closes.
- 2026-11-13 — Sync preview details are now rows (`MainForm.PreviewRows.cs`, reuses `InsightTable`): changes ticked
  (untick = add never-sync rule + RebuildPlan), excluded unticked (tick = remove rule), protected rows in purple with
  the platform, in-sync tracks grey at the bottom. "Never sync…" button hidden (Settings › Manage ignored tracks
  remains). `details` RichTextBox kept off-screen for Copy details + smoke checks. No dotted focus box in the
  playlist grid (`SmoothGrid.ShowFocusCues`). Themes: removed 24 the user disliked; group renamed "Game worlds";
  new `ThemeDef.Fx` backdrop effects (stars, grid, scan, rays, waves, rain, embers) baked into the cached
  `PageHeader` backdrop; 6 new fx themes; Element 115 redesigned (cyan + violet, stars).
- 2026-11-13 — Dashboard Sync preview module's playlist view uses the same checkbox rows (`BuildDetailRows(table,
  summary)` / `FillDetailRows(table, summary, item)`; per-table playlist in `detailPlaylistOf`). Old rich-text
  `dashPlanDetail` no longer shown. Build queued to swap into #UserTesting when the app closes.
- 2026-11-13 — Sync preview grid ordered like the module (blocked, then most changes, then name).
- 2026-11-18 — New PC (no spot2yt): installed .NET 8 SDK; built projects individually (`.slnx` needs SDK 9.0.200+).
  App `Tools` filled with public tools (spotDL 4.6.0, yt-dlp, ffmpeg/ffprobe 9.0.2, Deno); `Downloader=Tools\spotdl.exe`.
  Audio quality: spotDL now runs with `--format opus --bitrate disable` (stream copy of YouTube Music's best Opus,
  ~135 kbps, instead of 128 kbps MP3 re-encode); yt-dlp uses `-f bestaudio/best -x --audio-format best` (no MP3
  V0 transcode). Fallback tagging writes stream-level tags too (Ogg/Opus ignored the global ones) and retries with
  `-map 0:a` when Ogg can't hold the cover-art stream. spotDL 4.5.2 pin → 4.6.0 (4.5.2 got YouTube HTTP 403).
- 2026-11-18 (later) — YouTube Premium audio: `Tools\browser_options.txt` (FIREFOX=true) is now used FIRST (not as a retry) for
  spotDL (`--threads 1 --yt-dlp-args "--cookies-from-browser firefox --sleep-interval 4 --max-sleep-interval 10"`)
  and yt-dlp (same sleep); retry without cookies on failure. Premium formats (774 Opus ~268k, 141 AAC ~258k) need a GVS
  PO token: bgutil-ytdlp-pot-provider 2.0.2 script mode via Deno (`%USERPROFILE%\bgutil-ytdlp-pot-provider\server`,
  plugin zip in `%APPDATA%\yt-dlp\plugins`). Verified through `Downloader.RunManualAsync`: both paths → ~270 kbps Opus.

## 2026-11-18 — large downloads, playlist names
- Manual/missing-Likes downloads now run in batches of 50 links (SpotySyncService.DownloadLinksAsync); each batch is imported into MusicBee before the next. Fixes "The filename or extension is too long" when 5,000+ links were passed on one spotDL command line.
- Spotify share links are stripped of ?si=/&pt= query strings; spotDL gets one automatic retry when it exits non-zero before writing any audio (intermittent `KeyError: 'ownerV2'`). A Spotify 403 on GET /v1/tracks is only warned about once per run.
- spotDL runs with PYTHONIOENCODING=utf-8 / PYTHONUTF8=1 (cp1252 console crashed on symbol-heavy titles).
- MusicBee plugin: playlist names containing : < > / \ ? * " | are created with look-alike characters (MusicBee refused them as file names). After rebuilding the plugin, copy mb_MusicBeeSyncToService.dll into the app's "MusicBee Plugin" folder manually (build-release.ps1 can't run on this PC).

## 2026-11-19 — Premium audio not reaching app downloads
- Every song from the in-app runs on 2026-11-18 (~233 files) measured ~130 kbps (YouTube format 251), while identical spotDL commands run by hand at the same time got format 774 (~265 kbps). Cause not yet confirmed; this PC has no page file and commit was ~98% full (SpotySync crashed with an SEHException, spotDL logged MemoryError in a subprocess reader).
- Added diagnostics in Downloader: a "Premium audio check" (yt-dlp -F with browser cookies on a known song, once per 30 min, logs cookie/PO-token/warning lines) before each spotDL run, and a per-batch "Quality check" that measures audio-only bitrate (sum of audio packet sizes / duration) of new files and lists any under 200 kbps.
- Added Library › "Upgrade quality" (SpotySyncService.UpgradeLowQualityAsync): runs the Premium check first and stops if it fails; finds files with a Spotify ID (spotDL's WOAS tag) whose audio-only bitrate is < 200 kbps; re-downloads them in batches of 50 with spotDL `--overwrite force`; keeps the new copy at the original path only when it is >= 200 kbps; then refreshes those files in MusicBee.
- ROOT CAUSE of ~128 kbps app downloads: the bgutil script-deno PO-token provider gives Deno 5 s (`deno --version`) and 15 s (`generate_once.ts --version`) to start. On a cold start these time out; the 5 s one fails silently (debug-only log), the provider is marked unavailable for the whole yt-dlp/spotDL process, YouTube withholds formats 774/141, and every song falls back to 251. Fix: Downloader.WarmUpPoTokenHelperAsync runs the same two Deno commands with 2–3 min limits before the Premium check and before every spotDL batch; the Premium check retries once after a warm-up.
- Quality check now classifies each <200 kbps song by probing its YouTube URL (comment tag) with cookies: "no 256k version on YouTube" vs "missed an available 256k version" (first 15 per batch).
- 2026-11-19 later: warm-up alone did not fix the app (its check finished in ~7 s with no "[pot" line, i.e. the bgutil provider was never consulted), although the same check passes from console, windowed and Explorer-environment test parents. Now yt-dlp/spotDL get explicit `--js-runtimes deno:<Tools/deno.exe>` and `--extractor-args youtubepot-bgutilscript:server_home=<~/bgutil-ytdlp-pot-provider/server>` (forward slashes because spotDL shlex-splits --yt-dlp-args), and a failed Premium check re-runs yt-dlp with -v and logs "Premium debug |" lines (plugin dirs, PO token providers, jsc, deno).
- ACTUAL ROOT CAUSE (confirmed): the bgutil plugin zip had been written to %APPDATA%\yt-dlp\plugins from inside an MSIX-packaged (sandboxed) app, so Windows virtualized the write into that app's private %LOCALAPPDATA%\Packages\...\LocalCache folder. Tools launched from that app saw it; SpotySync launched normally did not ("Plugin directories: none" → no PO token → format 251). The same applies to anything else installed under AppData from a packaged app. Shipping the plugin beside the tools avoids the problem. Fix: plugin now ships in Tools\yt-dlp-plugins\bgutil-ytdlp-pot-provider.zip (searched next to yt-dlp.exe and spotdl.exe); build-release.ps1 downloads/pins it (sha256 a26b980b…e27ef). Verified outside the sandbox (process created via WMI): yt-dlp lists 774, spotDL download measured 252 kbps.

## 2026-11-20 — 1.5.1: download activity overhaul, dashboard grid, live library
- The user's working copy on this PC is `dist\SpotySync-1.5.0-win-x64` (login, Data, Logs). build-release.ps1 used to
  delete every `SpotySync-*` folder in dist and the same-version package folder at start: both now skip/refuse a folder
  holding SpotySync.ini / Data / Logs. Version → 1.5.1. Agent shells here cannot reach nuget.org (WinError 10013 —
  Portmaster firewall prompts); run the build from the user's terminal.
- `DownloadActivity.cs` replaces DownloadMonitor/ActivityGraph (old ones removed from Activity.cs). Speed is measured
  from growing temp files: spotDL writes yt-dlp `.part` files into `%USERPROFILE%\.spotdl\temp`; yt-dlp direct writes
  `.part` in the music folder. The old monitor watched the Soundtrack root (= C:\Users\jjarm on this PC → whole
  profile) and counted every write (2 GB for 3 songs, spikes). Shown speed = 10 s average, graph = 15 s average +
  faint per-second bars; songs done (new audio files) / failed (LookupError, AudioProviderError, ERROR: [youtube]) /
  skipped vs queue ("Download queue: N song(s)." logged by DownloadLinksAsync; also "N to download", "Quality
  upgrade: N song"); current song = next in spotDL's "Processing query" order (spotDL resolves all queries first,
  then downloads in order with --threads 1); size estimates from Spotify durations × observed bytes/ms (≈160 kbps
  until songs finish); ETA from time per song. Ranges 1m/5m/15m/1h/All (`[Appearance] ActivityRange`), hover
  read-out, song markers, pause banner. Full screen: `ActivityWindow` (F11 / Esc / Space = pause), from the module
  header button or double-click on the graph. Console page: "Show graph" splits the page (`ConsoleSplit`).
- Pause: `SpotySync.Core.DownloadProcesses` — Downloader starts spotDL/yt-dlp/ffmpeg/search via
  `DownloadProcesses.Start` (tracked); Pause = NtSuspendProcess on each process tree (Toolhelp snapshot); new
  processes and the next batch wait (`WaitIfPausedAsync`); RunBusyAsync always `Reset()`s at the end. Status-bar
  Pause/Resume button. Core test suspends/resumes a real ping process.
- Dashboard grid (`CardGrid` in Ui.cs) now positional: each card has a column + vertical order (its centre); cards
  stack per column in order. Layout string `[-][~]id:span:height:col:order` (reading order; "~" = minimized; old
  strings without col/order are packed once). Dragging moves the real card (no overlay ghost), others glide live, a
  dashed placeholder shows the drop spot; a dragged card goes above another once its top edge is above that card's
  middle. Edge auto-scroll. Card header buttons (`Card.HeaderActions`): minimize/restore on every module (also
  double-click the title), full screen on Download activity, open-Console on Console.
- Dashboard Console module = `LogFeed` (every entry incl. downloader detail, time + tag, incremental, follows only
  when scrolled to the bottom). ConsoleView gained `Reset` event + `AddToolbarControl`.
- Status bar: Spotify track links get "Title — Artists  ·  " in front (track index from liked cache + preview).
- Library: ROOT CAUSE of "library never updates": `Data\last-preview.json` (restored at launch) froze the library,
  and Analyse reused it. Now `LibraryScanner.ReconcileAsync` (new audio in the music folder probed with ffprobe,
  missing files dropped only if their drive exists) runs at startup, after every run that wrote songs, on Analyse,
  and live via a FileSystemWatcher (2.5 s debounce, 6 s during runs); preview plan rebuilt on a worker thread unless
  applied. `MainForm.LiveLibrary` (off in the smoke test). Library rows: checkbox = selection (nothing ticked by
  default, every tab), Select all ⇄ Deselect (n), Download/Save/Copy/Export use ticked rows; excluded rows have no box.
  `InsightTable.ReplaceItems` keeps scroll/selection on live refreshes.
- Tests: 45 core tests; smoke `ValidateLiveFeatures` (drag middle card under left one, minimize, full console,
  status names, library select, console split, simulated download run, full-screen window).
- 2026-11-20 — 1.5.2: "Show graph" crashed (CrossFade added its FadeLayer to a SplitContainer, whose Controls are read-only); CrossFade now climbs to a parent that accepts children and falls back to an instant change. Closing the window during a run crashed in RunBusyAsync's finally (EndRun on a disposed console); the end-of-run work moved to FinishRunAsync and is skipped once the form is disposed. The user runs from dist\SpotySync-1.5.1-win-x64 (in use, kept by the build).
- 2026-11-20 — Standing rule: after every code change, run build-release.ps1 (from the user's terminal on this PC). Besides the dist zip it now fills dist\SpotySync-<version>-win-x64 (was releases\ briefly) with the ready-to-run app (program files replaced; SpotySync.ini/Data/Logs kept). releases/ is git-ignored.
