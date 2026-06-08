#!/usr/bin/env bash
set -euo pipefail

# Downloads sidecar dependencies needed by the Inno Setup installer.
# Run this before building the installer.

SIDECAR_DIR="$(cd "$(dirname "$0")" && pwd)/godot_project/installer_sidecar"
mkdir -p "$SIDECAR_DIR"

echo "=== Downloading sidecar dependencies ==="

# FFmpeg for Windows
if [ ! -f "$SIDECAR_DIR/ffmpeg.exe" ] || [ ! -f "$SIDECAR_DIR/ffprobe.exe" ]; then
  echo "Downloading FFmpeg..."
  FFMPEG_URL="https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
  TMP_ZIP=$(mktemp)
  curl -sL "$FFMPEG_URL" -o "$TMP_ZIP"
  unzip -q -o "$TMP_ZIP" -d /tmp/ffmpeg-extract
  find /tmp/ffmpeg-extract -name "ffmpeg.exe" -exec cp {} "$SIDECAR_DIR/" \;
  find /tmp/ffmpeg-extract -name "ffprobe.exe" -exec cp {} "$SIDECAR_DIR/" \;
  rm -rf /tmp/ffmpeg-extract "$TMP_ZIP"
  echo "  ffmpeg.exe + ffprobe.exe downloaded"
fi

# yt-dlp
if [ ! -f "$SIDECAR_DIR/yt-dlp.exe" ]; then
  echo "Downloading yt-dlp..."
  curl -sL "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe" -o "$SIDECAR_DIR/yt-dlp.exe"
  echo "  yt-dlp.exe downloaded"
fi

# VC++ Redistributable
if [ ! -f "$SIDECAR_DIR/vc_redist.x64.exe" ]; then
  echo "Downloading VC++ Redistributable..."
  curl -sL "https://aka.ms/vs/17/release/vc_redist.x64.exe" -o "$SIDECAR_DIR/vc_redist.x64.exe"
  echo "  vc_redist.x64.exe downloaded"
fi

# .NET 8 Desktop Runtime
if [ ! -f "$SIDECAR_DIR/dotnet8-desktop-runtime-x64.exe" ]; then
  echo "Downloading .NET 8 Desktop Runtime..."
  curl -sL "https://dotnet.microsoft.com/en-us/download/dotnet/thank-you/runtime-desktop-8.0.0-windows-x64-installer" -o "$SIDECAR_DIR/dotnet8-desktop-runtime-x64.exe"
  echo "  dotnet8-desktop-runtime-x64.exe downloaded"
fi

echo "=== All sidecar dependencies ready ==="
ls -lh "$SIDECAR_DIR/"
