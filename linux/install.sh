#!/usr/bin/env bash
# Installs SpotySync for Linux into your home folder (nothing system-wide, so it works on Bazzite and
# other immutable systems) and adds it to the app menu.
#
#   bash install.sh                       install or update
#   bash install.sh --import /path/to/SpotySync-1.5.10-win-x64
#                                         also bring over settings, Spotify login and caches from the Windows version
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
name="$(basename "$here")"
root="${XDG_DATA_HOME:-$HOME/.local/share}/spotysync"
target="$root/$name"
import=""
if [[ "${1:-}" == "--import" ]]; then import="${2:-}"; fi

mkdir -p "$root"
if [[ "$here" != "$target" ]]; then
  echo "Installing to $target"
  mkdir -p "$target"
  # program files are replaced; SpotySync.ini, Data and Logs from an earlier run of this version stay
  cp -a "$here/." "$target/"
fi

chmod +x "$target/SpotySync" "$target"/Tools/{spotdl,yt-dlp,ffmpeg,ffprobe,deno} 2>/dev/null || true

if [[ -n "$import" ]]; then
  if [[ ! -d "$import/Data" ]]; then echo "No Data folder in $import - nothing imported."; else
    echo "Importing settings, Spotify login and caches from $import"
    [[ -f "$import/SpotySync.ini" && ! -f "$target/SpotySync.ini" ]] && cp "$import/SpotySync.ini" "$target/"
    mkdir -p "$target/Data"
    cp -n "$import"/Data/*.json "$import"/Data/*.txt "$target/Data/" 2>/dev/null || true
    rm -f "$target/Data/local-library-cache.json"   # Windows file paths; rebuilt from your Linux music folder
  fi
fi

apps="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
mkdir -p "$apps"
cat > "$apps/spotysync.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=SpotySync
Comment=Spotify Likes and playlists as music files, synced with Strawberry
Exec="$target/SpotySync"
Path=$target
Icon=$target/SpotySync.png
Terminal=false
Categories=AudioVideo;Audio;Music;
StartupWMClass=SpotySync
EOF
update-desktop-database "$apps" >/dev/null 2>&1 || true

echo
echo "SpotySync is installed. Open it from your app menu, or run:"
echo "  \"$target/SpotySync\""
