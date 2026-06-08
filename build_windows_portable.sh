#!/usr/bin/env bash
set -euo pipefail

# Build the Windows portable distribution of Velos Content Creation Suite
# No Python dependency — all backend logic is compiled into ClipTool.exe
# Prerequisites: godot-mono 4.6+, dotnet 8 SDK, curl, unzip

PROJECT_DIR="$(cd "$(dirname "$0")" && pwd)"
GODOT_PROJECT="$PROJECT_DIR/godot_project"
DIST_DIR="$PROJECT_DIR/dist"

echo "=== Building VelosCCS Windows Portable (No Python) ==="
echo "Project dir: $PROJECT_DIR"
echo "Dist dir:    $DIST_DIR"

# Clean
rm -rf "$DIST_DIR"
mkdir -p "$DIST_DIR"

# Step 1: Build C# project
echo ""
echo "--- Step 1/4: Building C# project ---"
dotnet build "$GODOT_PROJECT" -c Release 2>&1

# Step 2: Export Windows Godot binary
echo ""
echo "--- Step 2/4: Exporting Windows binary ---"
TEMPLATE_DIR="$HOME/.local/share/godot/export_templates"
TEMPLATE_VER="4.6.2.stable"
if [ ! -L "$TEMPLATE_DIR/$TEMPLATE_VER.mono" ]; then
  if [ -d "$TEMPLATE_DIR/$TEMPLATE_VER" ]; then
    ln -sf "$TEMPLATE_DIR/$TEMPLATE_VER" "$TEMPLATE_DIR/$TEMPLATE_VER.mono"
  else
    echo "ERROR: Export templates not found at $TEMPLATE_DIR/$TEMPLATE_VER"
    echo "Download them from https://github.com/godotengine/godot/releases/download/$TEMPLATE_VER/Godot_v${TEMPLATE_VER}_export_templates.tpz"
    exit 1
  fi
fi
godot-mono --headless --path "$GODOT_PROJECT" --export-release "Windows Desktop" 2>&1

# Step 3: Download FFmpeg for Windows (sidecar next to ClipTool.exe)
echo ""
echo "--- Step 3/4: Downloading FFmpeg for Windows ---"
FFMPEG_URL="https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
FFMPEG_ZIP="$DIST_DIR/ffmpeg.zip"
curl -sL "$FFMPEG_URL" -o "$FFMPEG_ZIP"
unzip -q -o "$FFMPEG_ZIP" -d "$DIST_DIR/ffmpeg-tmp"
find "$DIST_DIR/ffmpeg-tmp" -name "ffmpeg.exe" -exec cp {} "$DIST_DIR/" \;
find "$DIST_DIR/ffmpeg-tmp" -name "ffprobe.exe" -exec cp {} "$DIST_DIR/" \;
rm -rf "$DIST_DIR/ffmpeg-tmp" "$FFMPEG_ZIP"

# Step 4: Download yt-dlp for Windows
echo ""
echo "--- Step 4/4: Downloading yt-dlp for Windows ---"
curl -sL "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe" -o "$DIST_DIR/yt-dlp.exe"

echo ""
echo "=== Build Complete ==="
echo ""
echo "Distribution bundle: $DIST_DIR"
echo "  - ClipTool.exe          (Godot Windows binary)"
echo "  - ClipTool.pck          (game data)"
echo "  - *.dll                 (managed assemblies)"
echo "  - ffmpeg.exe            (video processing)"
echo "  - ffprobe.exe           (video probing)"
echo "  - yt-dlp.exe            (YouTube download)"
echo ""
echo "Total size: $(du -sh "$DIST_DIR" | cut -f1)"
echo ""
echo "To run on Windows: double-click ClipTool.exe"
echo "ffmpeg.exe, ffprobe.exe, and yt-dlp.exe must be next to ClipTool.exe"
