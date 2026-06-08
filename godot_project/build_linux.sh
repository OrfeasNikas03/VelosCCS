#!/usr/bin/env bash
set -euo pipefail

# Build the Linux distribution for Velos Content Creation Suite
# Prerequisites: godot, dotnet 8 SDK, curl, unzip

PROJECT_DIR="$(cd "$(dirname "$0")" && pwd)"
LINUX_EXPORT_DIR="$PROJECT_DIR/../app_exports/linux"

echo "=== Building VelosCCS Linux Distribution ==="
echo "Project dir: $PROJECT_DIR"
echo "Export dir:   $LINUX_EXPORT_DIR"

# Step 1: Publish WhisperWorker for Linux
echo ""
echo "--- Step 1/4: Publishing WhisperWorker for linux-x64 ---"
rm -rf "$PROJECT_DIR/WhisperWorker_published_linux"
dotnet publish "$PROJECT_DIR/../WhisperWorker/WhisperWorker.csproj" \
  -c Release -r linux-x64 --self-contained true \
  -o "$PROJECT_DIR/WhisperWorker_published_linux" 2>&1
# Strip unused native runtimes (windows, macos) to save ~59MB
rm -rf "$PROJECT_DIR/WhisperWorker_published_linux/runtimes/win-"* \
       "$PROJECT_DIR/WhisperWorker_published_linux/runtimes/osx-"* \
       "$PROJECT_DIR/WhisperWorker_published_linux/runtimes/macos-"* 2>/dev/null || true
echo "  WhisperWorker published (unused runtimes stripped)"

# Step 2: Download llama-cli for Linux
echo ""
echo "--- Step 2/4: Downloading llama-cli (llama.cpp) for Linux ---"
# Auto-detect the latest llama.cpp release tag
LLAMA_VERSION=$(curl -sL "https://api.github.com/repos/ggml-org/llama.cpp/releases/latest" | grep '"tag_name"' | head -1 | sed 's/.*"tag_name": "\(.*\)".*/\1/')
if [ -z "$LLAMA_VERSION" ]; then
  echo "  WARNING: Could not detect latest version, falling back to b4600"
  LLAMA_VERSION="b4600"
fi
echo "  Detected latest llama.cpp: $LLAMA_VERSION"

rm -rf "$PROJECT_DIR/LlamaWorker_published"
mkdir -p "$PROJECT_DIR/LlamaWorker_published"
curl -sL "https://github.com/ggml-org/llama.cpp/releases/download/$LLAMA_VERSION/llama-$LLAMA_VERSION-bin-ubuntu-x64.zip" -o /tmp/llama-linux.zip
rm -rf /tmp/llama-linux-tmp
unzip -q -o /tmp/llama-linux.zip -d /tmp/llama-linux-tmp
# Copy core llama-cli files
for f in llama-cli llama-cli-impl.so llama.so llama-common.so ggml.so ggml-base.so ggml-cpu-x64.so ggml-vulkan.so ggml-rpc.so; do
  cp "/tmp/llama-linux-tmp/$f" "$PROJECT_DIR/LlamaWorker_published/" 2>/dev/null || echo "  WARNING: $f not found in release"
done
# Copy CPU dispatch .so files for broad hardware support
for f in ggml-cpu-haswell ggml-cpu-zen4 ggml-cpu-skylakex ggml-cpu-icelake ggml-cpu-sapphirerapids ggml-cpu-alderlake ggml-cpu-cannonlake ggml-cpu-cascadelake ggml-cpu-cooperlake ggml-cpu-ivybridge ggml-cpu-piledriver ggml-cpu-sandybridge ggml-cpu-sse42; do
  cp "/tmp/llama-linux-tmp/${f}.so" "$PROJECT_DIR/LlamaWorker_published/" 2>/dev/null || true
done
rm -rf /tmp/llama-linux-tmp /tmp/llama-linux.zip

# Make llama-cli executable
chmod +x "$PROJECT_DIR/LlamaWorker_published/llama-cli" 2>/dev/null || true
echo "  llama-cli + native .so files downloaded ($(ls "$PROJECT_DIR/LlamaWorker_published/"llama-cli "$PROJECT_DIR/LlamaWorker_published/"*.so 2>/dev/null | wc -l) files)"

# Step 3: Export Godot project for Linux
echo ""
echo "--- Step 3/4: Exporting Godot Linux binary ---"
"$PROJECT_DIR/godot" --headless --path "$PROJECT_DIR" --export-release "Linux/X11" 2>&1
echo "  Linux export complete"

# Step 4: Bundle LlamaWorker + WhisperWorker alongside the exported binary
echo ""
echo "--- Step 4/4: Bundling workers ---"
mkdir -p "$LINUX_EXPORT_DIR/LlamaWorker_published"
cp -r "$PROJECT_DIR/LlamaWorker_published/"* "$LINUX_EXPORT_DIR/LlamaWorker_published/"
mkdir -p "$LINUX_EXPORT_DIR/WhisperWorker_published"
cp -r "$PROJECT_DIR/WhisperWorker_published_linux/"* "$LINUX_EXPORT_DIR/WhisperWorker_published/"
echo "  Workers bundled into $LINUX_EXPORT_DIR"

# Package as tarball for distribution
echo ""
echo "--- Packaging ---"
TARBALL="$PROJECT_DIR/../ClipTool_Linux.tar.gz"
tar czf "$TARBALL" -C "$(dirname "$LINUX_EXPORT_DIR")" "$(basename "$LINUX_EXPORT_DIR")"
echo "  Packaged: $TARBALL"

echo ""
echo "=== Build Complete ==="
echo ""
ls -lh "$LINUX_EXPORT_DIR/" 2>/dev/null || echo "No Linux export found!"
ls -lh "$TARBALL" 2>/dev/null
