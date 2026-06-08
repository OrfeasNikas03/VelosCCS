#!/usr/bin/env bash
set -euo pipefail

# Build the Windows installer for Velos Content Creation Suite
# Prerequisites: wine (with Inno Setup 6 installed), godot, dotnet 8 SDK, curl, unzip

PROJECT_DIR="$(cd "$(dirname "$0")" && pwd)"
SIDECAR_DIR="$PROJECT_DIR/installer_sidecar"

echo "=== Building VelosCCS Windows Installer ==="
echo "Project dir: $PROJECT_DIR"

# Clean sidecar dir
rm -rf "$SIDECAR_DIR"
mkdir -p "$SIDECAR_DIR"

# Step 1: Download FFmpeg for Windows
echo ""
echo "--- Step 1/7: Downloading FFmpeg for Windows ---"
FFMPEG_URL="https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
FFMPEG_ZIP="/tmp/ffmpeg.zip"
curl -sL "$FFMPEG_URL" -o "$FFMPEG_ZIP"
unzip -q -o "$FFMPEG_ZIP" -d /tmp/ffmpeg-tmp
find /tmp/ffmpeg-tmp -name "ffmpeg.exe" -exec cp {} "$SIDECAR_DIR/" \;
find /tmp/ffmpeg-tmp -name "ffprobe.exe" -exec cp {} "$SIDECAR_DIR/" \;
rm -rf /tmp/ffmpeg-tmp "$FFMPEG_ZIP"
echo "  ffmpeg.exe + ffprobe.exe downloaded"

# Step 2: Download yt-dlp for Windows
echo ""
echo "--- Step 2/7: Downloading yt-dlp for Windows ---"
curl -sL "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe" -o "$SIDECAR_DIR/yt-dlp.exe"
echo "  yt-dlp.exe downloaded"

# Step 3: Publish WhisperWorker for Windows
echo ""
echo "--- Step 3/7: Publishing WhisperWorker for win-x64 ---"
rm -rf "$PROJECT_DIR/WhisperWorker_published"
dotnet publish "$PROJECT_DIR/../WhisperWorker/WhisperWorker.csproj" \
  -c Release -r win-x64 --self-contained true \
  -o "$PROJECT_DIR/WhisperWorker_published" 2>&1
# Strip unused native runtimes (linux, macos, win-arm64, win-x86) to save ~59MB
rm -rf "$PROJECT_DIR/WhisperWorker_published/runtimes/linux-"* \
       "$PROJECT_DIR/WhisperWorker_published/runtimes/osx-"* \
       "$PROJECT_DIR/WhisperWorker_published/runtimes/macos-"* \
       "$PROJECT_DIR/WhisperWorker_published/runtimes/win-arm64" \
       "$PROJECT_DIR/WhisperWorker_published/runtimes/win-x86" 2>/dev/null || true
echo "  WhisperWorker published (unused runtimes stripped)"

# Step 4: Download llama-cli for Windows
echo ""
echo "--- Step 4/7: Downloading llama-cli (llama.cpp) for Windows ---"
# Auto-detect the latest llama.cpp release tag
LLAMA_VERSION=$(curl -sL "https://api.github.com/repos/ggml-org/llama.cpp/releases/latest" | grep '"tag_name"' | head -1 | sed 's/.*"tag_name": "\(.*\)".*/\1/')
if [ -z "$LLAMA_VERSION" ]; then
  echo "  WARNING: Could not detect latest version, falling back to b9413"
  LLAMA_VERSION="b9413"
fi
echo "  Detected latest llama.cpp: $LLAMA_VERSION"
rm -rf "$PROJECT_DIR/LlamaWorker_published"
mkdir -p "$PROJECT_DIR/LlamaWorker_published"
	curl -sL "https://github.com/ggml-org/llama.cpp/releases/download/$LLAMA_VERSION/llama-$LLAMA_VERSION-bin-win-vulkan-x64.zip" -o /tmp/llama-win.zip
rm -rf /tmp/llama-win-tmp
unzip -q -o /tmp/llama-win.zip -d /tmp/llama-win-tmp
# Copy core llama-cli files
	for f in llama-cli.exe llama-cli-impl.dll llama.dll llama-common.dll ggml.dll ggml-base.dll ggml-cpu-x64.dll ggml-vulkan.dll ggml-rpc.dll mtmd.dll libomp140.x86_64.dll; do
  cp "/tmp/llama-win-tmp/$f" "$PROJECT_DIR/LlamaWorker_published/" 2>/dev/null || echo "  WARNING: $f not found in release"
done
# Copy CPU dispatch DLLs for broad hardware support
for f in ggml-cpu-haswell ggml-cpu-zen4 ggml-cpu-skylakex ggml-cpu-icelake ggml-cpu-sapphirerapids ggml-cpu-alderlake ggml-cpu-cannonlake ggml-cpu-cascadelake ggml-cpu-cooperlake ggml-cpu-ivybridge ggml-cpu-piledriver ggml-cpu-sandybridge ggml-cpu-sse42; do
  cp "/tmp/llama-win-tmp/${f}.dll" "$PROJECT_DIR/LlamaWorker_published/" 2>/dev/null || true
done
rm -rf /tmp/llama-win-tmp /tmp/llama-win.zip
echo "  llama-cli.exe + native DLLs downloaded ($(ls "$PROJECT_DIR/LlamaWorker_published/"*.exe "$PROJECT_DIR/LlamaWorker_published/"*.dll 2>/dev/null | wc -l) files)"

# Step 5: Export Godot project for Windows
echo ""
echo "--- Step 5/7: Exporting Godot Windows binary ---"
/usr/bin/godot-mono --headless --path "$PROJECT_DIR" --export-release "Windows Desktop" 2>&1
echo "  Windows export complete"

# Step 6a: Download VC++ redistributable for fresh Windows installs
echo ""
echo "--- Step 6a/7: Downloading VC++ Redistributable ---"
VC_REDIST_URL="https://aka.ms/vs/17/release/vc_redist.x64.exe"
curl -sL "$VC_REDIST_URL" -o "$SIDECAR_DIR/vc_redist.x64.exe"
echo "  vc_redist.x64.exe downloaded"

# Step 6b: Download .NET 8 Desktop Runtime for systems without it
echo ""
echo "--- Step 6b/7: Downloading .NET 8 Desktop Runtime ---"
DOTNET_RUNTIME_URL="https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe"
curl -sL "$DOTNET_RUNTIME_URL" -o "$SIDECAR_DIR/dotnet8-desktop-runtime-x64.exe"
echo "  dotnet8-desktop-runtime-x64.exe downloaded"

# Step 7: Build installer with Inno Setup
echo ""
echo "--- Step 7/7: Building Inno Setup installer ---"
pushd "$PROJECT_DIR"
wine "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" "VelosCCS_Setup.iss" 2>&1
popd

echo ""
echo "=== Build Complete ==="
echo ""
ls -lh "$PROJECT_DIR/installer_build/"*.exe 2>/dev/null || echo "No installer found!"
