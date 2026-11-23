SpotySync for Linux (tested target: Bazzite / Fedora, x64)
==========================================================

Install
-------
1. Unpack the archive anywhere, for example in Downloads:
       tar -xzf SpotySync-*-linux-x64.tar.gz
2. Run the installer from the unpacked folder:
       bash SpotySync-*-linux-x64/install.sh
   It copies SpotySync to ~/.local/share/spotysync and adds it to your app menu.
   Nothing is installed system-wide, so this works on Bazzite's read-only system.

Bring over your Windows setup (optional, saves signing in and re-reading Spotify)
--------------------------------------------------------------------------------
Copy your Windows SpotySync folder (e.g. SpotySync-1.5.10-win-x64, with its Data folder) to the Linux PC,
then install with:
       bash SpotySync-*-linux-x64/install.sh --import /path/to/SpotySync-1.5.10-win-x64
This brings over SpotySync.ini, your Spotify login, the Spotify caches and the "Not on YouTube Music" list.
Windows folders (E:\..., C:\...) are replaced by ~/Music; set your real music folder in Settings.

Strawberry
----------
Install Strawberry from Bazaar (Flatpak: org.strawberrymusicplayer.strawberry).
- Settings -> Collection: add your music folder.
- Synced playlists are .m3u8 files in <music folder>/Playlists. Drag one into Strawberry or use
  Playlist -> Load playlist. Strawberry keeps its own copy, so load it again after a sync changes it.
- If Strawberry is the Flatpak, give it access to your music folder if it lives outside your home folder
  (Flatseal, or: flatpak override --user --filesystem=/path/to/music org.strawberrymusicplayer.strawberry).

YouTube Premium audio (~256 kbps)
---------------------------------
Sign in to YouTube in Firefox (Bazzite's Flatpak Firefox is found automatically). SpotySync uses that
sign-in for the higher-quality streams. Pick another browser in Settings if you use one.

Spotify sign-in
---------------
Only needed if you didn't import your Windows login. Linux doesn't let normal apps use port 80, so add
    http://127.0.0.1:8888/callback
as a Redirect URI on your Spotify app (developer.spotify.com/dashboard) and put the same address in
SpotySync's Settings.
