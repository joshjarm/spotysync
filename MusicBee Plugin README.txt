SPOTYSYNC BRIDGE FOR MUSICBEE
============================

Recommended installation:
  1. Run SpotySync.exe and open Settings.
  2. Fully close MusicBee.
  3. Click Install MusicBee plugin.
  4. Restart MusicBee and confirm that SpotySync says MusicBee: ready.

Manual installation:
  Standard MusicBee: copy mb_MusicBeeSyncToService.dll to
    %APPDATA%\MusicBee\Plugins

  Portable MusicBee: copy it to the Plugins folder beside MusicBee.exe.

This DLL replaces older mb_MusicBeeSyncToService.dll versions. It does not contain
Spotify credentials and never contacts the internet. It exposes an authenticated
bridge on 127.0.0.1:51983 only while MusicBee is running. The portable SpotySync app
uses that bridge to read the library/static playlists, import downloads, and apply a
reviewed playlist plan.

Bridge 4.8 verifies every requested import instead of assuming MusicBee accepted it,
and reconciles files that MusicBee renames between the folder scan and plugin handoff.
When enabled in SpotySync Settings, it also mutes MusicBee, plays each newly imported
track for one second to initialize MusicBee processing, then restores the previous
track, playback position, play/pause state, mute state, and scrobbling state. It uses
the Now Playing queue directly and never creates temporary playlist tabs.
It does not wait for lyric providers or automatically mark tracks as having no lyrics:
MusicBee does not reliably expose the final visible "No lyrics found" state through
its supported plugin API. Missing lyrics remain untouched for manual review. The bridge
does not write placeholder text into Lyrics and preserves each file's modified time.

Before any removal it writes a timestamped M3U8 backup under:
  %LOCALAPPDATA%\MusicBeeSyncToService\PlaylistBackups
