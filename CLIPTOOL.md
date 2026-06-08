# Velos Content Creation Suite — AI-Assisted Stream Clipping & Editing Suite

## Scope

Velos Content Creation Suite is a Godot 4 Mono desktop application for creating short-form vertical
video clips (TikTok/Shorts/Reels) from gaming/streaming footage. It provides:

- Video import (local files + YouTube/Twitch download via yt-dlp)
- Auto-transcription via Whisper (WhisperWorker subprocess — Vulkan-accelerated)
- AI-powered clip finding (Whisper → llama-cli LLM → highlight detection)
- Thumbnail previews on import
- Timeline editing with multi-track support (video/audio/text/image/GIF)
- Keyframe animation for text, position, scale, opacity
- Layout presets (Basic, Circle Facecam, Game UI) with blur
- Burn-in export via FFmpeg with captions, overlays, PiP
- Full undo/redo
- AI-driven testing via TCP TestServer

## Architecture

```
┌──────────────────────────────────────────────────────┐
│                   Godot 4 Mono App                   │
│  ┌────────────────────────────────────────────────┐  │
│  │  MainWindow (partial class across 6 files)      │  │
│  │  - MainWindow.cs       : lifecycle, UI, state   │  │
│  │  - MainWindow.Actions.cs : edit operations      │  │
│  │  - MainWindow.Import.cs : import + AI clips     │  │
│  │  - MainWindow.Inspector.cs : property panel     │  │
│  │  - MainWindow.Playback.cs : video playback      │  │
│  │  - MainWindow.Browser.cs : sticker/GIF browser  │  │
│  ├─────────────────┬──────────────────────────────┤  │
│  │ TimelineControl  │ VideoOverlay                  │  │
│  │ (timeline widget)│ (source monitor regions)      │  │
│  ├─────────────────┼──────────────────────────────┤  │
│  │ OutputPreview    │ TestServer (TCP :18765)       │  │
│  │ (result monitor) │ (AI-driven control)           │  │
│  ├─────────────────┴──────────────────────────────┤  │
│  │ External Services (subprocess/cli)               │  │
│  │  WhisperWorker — standalone .NET 8 subprocess for        │  │
│  │                Vulkan-accelerated transcription           │  │
│  │  yt-dlp       — YouTube/Twitch download                  │  │
│  │  ffprobe      — video metadata                           │  │
│  │  ffmpeg       — audio decode, video export               │  │
│  │  llama-cli    — LLM for highlight detection (direct subprocess) │
│  └────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────┘
```

## File Map — Godot C# Scripts (Scripts/)

| File | Responsibility |
|---|---|
| `MainWindow.cs` | App lifecycle, UI construction (top bar, import view with thumbnail preview + AI Clip Finder button, editor split with left dock + monitors + timeline), state machine (Import→Layout→Edit), public accessors for TestServer, undo/redo stack, backend event handling, keyboard shortcut help overlay, _Process frame-updates video textures |
| `MainWindow.Actions.cs` | All action handlers: OnClipSelected, SplitAtPlayhead, DeleteSelected, OnAddTextClip, OnGenerateCaptions, OnExportPressed, OnAutoFrame, OpenStickerBrowser, OpenSFXBrowser, OpenFontBrowser, AddImageClipToTimeline, AddAudioClipToTimeline, UpdateTracks, ApplyLayoutPreset |
| `MainWindow.Import.cs` | Import pipeline: ImportFileToBin, LoadVideoAsset (creates Source Video + Camera + Source Audio tracks), OnDownloadPressed (YouTube/Twitch via yt-dlp, skips ffprobe for URLs), ShowThumbnailPreview (fetches + displays thumbnail), OnAIFindClips (opens AISetupDialog, then runs RunAIClipFinder: download audio → Whisper transcription → llama-cli highlight detection → auto-download clips), OnSelectClips (manual ClipPickerWindow), ProcessDownloads |
| `MainWindow.Inspector.cs` | Dynamic inspector: BuildLayoutInspector (aspect ratio, templates, blur, auto-frame), BuildClipInspector (position, size, opacity, trim, fade, volume), BuildTextInspector (text, font, color, outline, keyframes), AddGridField helper, RefreshClipViews |
| `MainWindow.Playback.cs` | Video playback: OnTimerTimeout (playhead sync, loop, SFX sync), SeekVideo (unpauses → _Process frame-grabs texture → CallDeferred pause), SetPlayback, StepTimeline, StopAllSfx, FormatTime |
| `MainWindow.Browser.cs` | Media browser window for stickers/GIFs/SFX/fonts: scans directories, shows thumbnails, import via file dialog |
| `TimelineControl.cs` | Custom Control: multi-track rendering, waveform visualization, ruler, playhead (cyan/red), selection, loop region, snap guides, marquee selection, keyframe diamonds (3→5), DragFinished signal for undo debounce, vScroll (Ctrl+wheel), arrow key navigation. |
| `VideoOverlay.cs` | Overlay on source monitor: Layout mode (draggable Content/Camera/UI crop regions) and Editing mode (per-clip position/size handles) |
| `OutputPreview.cs` | Result monitor: ShaderMaterial compositing (source → blur bg → content → camera PiP → UI PiP → overlay), RefreshVideoTexture() for direct texture push |
| `TrackData.cs` | Data models: TrackType (Video/Audio), ClipType (SourceVideo/Text/Image/Gif/Audio), TrackData, TrackClipData (with AnimatableProperty, Keyframe, TextKeyframe), ClipData |
| `MediaAsset.cs` | Asset model: Name, Path, AssetType, Duration, Thumbnail, CaptionText, StartTime, EndTime |
| `AISetupDialog.cs` | Modal Window for model selection: shows model picker with download progress bar, percentage tracking, and downloaded checkmarks. Emits Proceed signal on completion. Uses BounceIn/BounceOutThenFree for entrance/close animation. |
| `ProgressWindow.cs` | Modal progress dialog with step label, percentage bar, ETA slot, and timestamped log lines for the AI clip finder pipeline. Entrance via BounceIn. |
| `ClipPickerWindow.cs` | Popup for manual YouTube clip download: add/remove time range spinboxes, shows thumbnail, emits DownloadRequested. BounceIn entrance, BounceOutThenHide on download. |
| `AppTheme.cs` | Static dark theme factory: CheckBox styling, window title color, spacing constants |
| `WindowExtensions.cs` | Window animation helpers: BounceIn (bouncy Back.Out slide-up), BounceOutThenFree (slide-down + close), BounceOutThenHide (slide-down + hide) |
| `ToastManager.cs` | Toast notification system: Info/Success/Error/Warning methods, vertical stacking, slide-in animation |
| `SFXManager.cs` | Sound effect download/cache/preview |
| `FontManager.cs` | Google Fonts download/cache/load |
| `SettingsDialog.cs` | Output directory + normalize audio toggle |
| `TestServer.cs` | TCP command server (:18765) for AI testing |
| `StreamManager.cs` | yt-dlp wrappers: GetInfo (with thumbnail URL from JSON), DownloadAudio, DownloadSection (--download-sections with --force-keyframes-at-cuts) |
| `BackendService.cs` | Orchestrator: ffprobe video info, yt-dlp info, transcription, highlight detection, download, frame extraction, waveform |
| `Transcriber.cs` | Whisper transcription with subprocess isolation: launches `WhisperWorker` standalone process (Vulkan-accelerated), VRAM polling before each launch (WaitForFreeVramAsync), OOM fallback launches worker with WHISPER_RUNTIME=cpu + CPU runtime path. Manages Vulkan runtime path, LD_LIBRARY_PATH, WHISPER_RUNTIME env, model download from HuggingFace, ffmpeg audio decode (single -ss fix), IProgress<double> + Action<string> callbacks. TranscribeAsync (manual captions) delegates to TranscribeChunkAsync for consistent worker-based path. |
| `Transcription.cs` | Data classes: Word, Segment, Transcript (with AsText, GetSegmentAt, AllWords) |
| `LLMDetector.cs` | llama-cli-based highlight detection: calls `llama-cli -f <prompt_file>` directly with concurrent pipe reads and 5-min timeout. Wraps prompt in Llama 3.2 Instruct chat template (`<|begin_of_text|><|start_header_id|>user<|end_header_id|>...<|eot|><|start_header_id|>assistant<|end_header_id|>`). Uses `--single-turn` flag to exit cleanly after one generation (no interactive mode). Parses JSON response (with regex fallback), validates clips (non-overlapping, min duration), auto-samples segments to fit model context. |
| `Detector.cs` | Rule-based HighlightDetector: sliding window scoring with excitement words, question words, exclamation marks |
| `Exporter.cs` | FFmpeg export: serializes tracks, generates drawtext/textmod filters with font scaling, opacity, text keyframe splitting, per-segment enables |
| `Captioner.cs` | ASS/SRT subtitle generation |
| `OllamaManager.cs` | [Removed — replaced by LlamaManager + direct llama-cli subprocess] |
| `LlamaManager.cs` | Static llama-cli lifecycle: FindCliBinary (locates llama-cli executable), model path/config management |
| `Config.cs` | App configuration constants |
| `Reframer.cs` | OpenCV face-tracking crop |
| `Program.cs (WhisperWorker/)` | Standalone .NET 8 console subprocess for Vulkan-accelerated whisper transcription. Reads model path, WAV path, and thread count from CLI args. Loads libwhisper.so via Whisper.net, selects Vulkan runtime (or CPU fallback via WHISPER_RUNTIME env), transcribes 15-min windows, outputs JSON to stdout. |

## File Map — Python Backend

The Python backend has been fully replaced by C# equivalents (Whisper.net, direct yt-dlp/ffmpeg subprocess calls). The only remaining Python component is the optional test client (`test_client.py`).

## UI Flow

```
[APP LAUNCH]
     │
     ▼
┌──────────┐   Paste URL       ┌─────────────────┐   Select model  ┌──────────┐
│  IMPORT   │ ──────────────►  │ THUMBNAIL PREVIEW│ ──────────────►│ LAYOUT   │
│           │   Fetch & Clip   │                  │   AI Find Clips│          │
│ Big button│                  │ Thumbnail + info │                 │ Aspect   │
│ URL input │                  │ [Select Clips]   │  [Download audio│ ratio    │
│           │                  │ [AI Find Clips]  │   → Transcribe  │ regions  │
└──────────┘                  └─────────────────┘   → llama-cli LLM  └──────────┘
                                                      → Download      Pick template
                                                      clips]          Set blur
                                                                    Auto-frame
                                                                         │
                                                                         ▼
                                                                  ┌──────────┐
                                                                  │   EDIT   │
                                                                  │          │
                                                                  │ Timeline │
                                                                  │ + overlay│
                                                                  │ + preview│
                                                                  └──────────┘
                                                                  Add text clips
                                                                  Generate captions
                                                                  Add stickers/GIFs
                                                                  Add SFX
                                                                  Trim / split / delete
                                                                  Export (FFmpeg burn)
```

## Key Workflows

### 1. Import
- **Local file**: Click big import button → FileDialog → creates Source Video + Camera + Source Audio tracks, enters Layout
- **YouTube/Twitch URL**: Paste link → "Fetch & Clip" → yt-dlp fetches info (duration, title, uploader, thumbnail) → thumbnail + metadata shown inline → two options:
  - **"Select Clips"** → manual time-range picker → download fragments
  - **"AI Find Clips"** → AISetupDialog for model selection → auto-downloads audio → Whisper transcription → llama-cli LLM identifies highlight moments → downloads 30-60s clips around those moments → adds to Media Bin
- **Duration detection**: HTTP/HTTPS URLs bypass ffprobe (streaming HLS/DASH gives bogus results) and go directly to yt-dlp for accurate duration

### 2. AI Clip Finder Pipeline
1. **AISetupDialog**: Shows model picker with download progress bar, percentage tracking, and downloaded checkmarks. Handles model selection and download.
2. **ProgressWindow**: Modal dialog showing current step, percentage bar, and timestamped log lines with ETA slot.
3. **Download audio**: yt-dlp bestaudio (temporary, deleted after)
4. **Transcribe**: WhisperWorker subprocess with Vulkan acceleration. VRAM polling (`nvidia-smi` free memory) waits for ≥500MB free before each window launch. On GPU OOM, falls back to CPU worker with CPU runtime path. ffmpeg audio decode uses single `-ss` to avoid double-skip bug.
5. **Analyze**: Send transcript to llama-cli directly (`-f <prompt_file>`), wraps in Llama 3.2 Instruct chat template, uses `--single-turn` flag. JSON clip detection.
6. **Download clips**: 5 clips max, 30-60s each, via yt-dlp --download-sections with --force-keyframes-at-cuts. Async download with `WaitForExitAsync` + `ConfigureAwait(false)` to keep Godot UI responsive.
7. **Cleanup**: Temp audio file deleted

### WhisperWorker — Subprocess Architecture

Whisper transcription runs in a **standalone .NET 8 console process** to isolate GPU crashes.

```
Godot App ─── Process.Start ───→ WhisperWorker (subprocess)
                                       │
                                       ├── Loads libwhisper.so (Vulkan via ggml-vulkan)
                                       ├── Transcribes WAV → JSON to stdout
                                       ├── Exits with code 0 on success
                                       └── Exits with SIGSEGV (139) on GPU crash
                                            → Godot survives, falls back to CPU or skips window
```

**Vulkan Runtime:**
- Uses `WHISPER_RUNTIME=vulkan` to load `libwhisper.so` from `runtimes/vulkan/linux-x64/`
- Built with `GGML_VULKAN=ON` for GTX 1660 (6 GB VRAM)
- On GPU OOM, launches worker with `WHISPER_RUNTIME=cpu` + `runtimes/linux-x64/` path

**Subprocess isolation benefits:**
- GPU driver crash (SIGSEGV) kills only the child process, not Godot
- Pipeline continues to next window with empty transcript
- No system freeze or display lockup

**Model:** whisper `base` (150 MB), built from whisper.cpp commit `27101c0`.

### LLM Highlight Detection

- **Model:** Llama 3.2 3B (Q4_K_M, ~2.5 GB VRAM) via direct llama-cli subprocess
- **Invocation:** `llama-cli -f <prompt_file>` with concurrent stdout/stderr pipe reads and 5-min timeout
- **`--single-turn` flag:** Prevents llama-cli from entering interactive mode on instruct models; process exits cleanly after one generation
- **Chat template:** Wraps prompt in `<|begin_of_text|><|start_header_id|>user<|end_header_id|>...<|eot|><|start_header_id|>assistant<|end_header_id|>` — prevents model from echoing transcript text
- **Model load:** ~6–11s per window (3B Q4_K_M) on Vulkan; generation at ~8.9 t/s
- **Prompt:** Asks for TikTok/Reels-worthy moments with explicit 30-60s duration rules
- **Segment sampling:** Windows with >150 segments are sampled evenly to keep prompt under context window
- **JSON parsing:** Three-tier — full JSON document → regex repair + parse → regex extraction of `start`/`end` pairs from raw text
- **Validation:** Non-overlapping, minimum duration `minDuration * 0.5` (default 15s), max `maxClips` per window

### 3. Layout
- Choose aspect ratio (9:16 default)
- Pick template (Basic / Circle Facecam / Game UI)
- Drag Content/Camera/UI crop regions
- Apply background blur (auto-toggles for portrait/square vs 16:9)
- Auto-frame with face detection
- 16:9 Basic mode hides layout overlays (no compositing needed)

### 4. Text & Captions
- Manual text: "Text" button → adds clip at selection position
- Auto captions: "Generate Captions" → Whisper transcription → per-segment text clips on "Captions" track
- Inspector: text content, font size, font family (Google Fonts), color, outline, position, opacity, fade, keyframes
- Export: font size scaled by output_h/display_h, text centered via tw/2 + th/2

### 5. Editing
- Select/move/trim clips on timeline (or Inspector fields)
- Split with Razor tool or S key
- Delete selected (Delete key)
- Undo/Redo (Ctrl+Z / Ctrl+Shift+Z)
- Play/pause (Space), frame step (Shift+Left/Right), 5% jump (Left/Right)
- Keyframe diamonds shown for both position/opacity keyframes AND text keyframes
- Undo snapshots debounced during continuous drag operations

### 6. Export
- "EXPORT" button serializes all tracks → calls FFmpeg
- Three layout paths: Streamladder (dual-crop PiP), Letterbox (single-crop), Simple (no layout)
- Text keyframes split into per-segment drawtext filters with enable='between(t,...)'
- Scale keyframes consumed in width/height expressions
- fontsize_expr → fontsize; opacity via alpha parameter

## Data Model

```
Project
 ├── Tracks (List<TrackData>)
 │    ├── Name: string
 │    ├── Type: Video / Audio
 │    ├── Muted: bool
 │    ├── ZIndex: int
 │    └── Clips (List<TrackClipData>)
 │         ├── ClipType: SourceVideo / Text / Image / Gif / Audio
 │         ├── Start / End: double (seconds)
 │         ├── Position / Size: Vector2 (normalized 0-1)
 │         ├── Text / FontSize / FontPath / FontColor / OutlineColor / OutlineWidth
 │         ├── Color (DodgerBlue default)
 │         ├── Volume: AnimatableProperty
 │         ├── PosX / PosY / Scale / Opacity: AnimatableProperty (static or keyframed)
 │         ├── TextKeyframes: List<TextKeyframe> (time + text)
 │         ├── KeyframesScale: List<Keyframe> (scale animation)
 │         ├── WaveformPeaks: List<float>
 │         └── FadeIn / FadeOut: double
 ├── ProjectBin (List<MediaAsset>)
 │    ├── Name / Path / Type / Duration
 │    └── Thumbnail / WaveformPeaks / CaptionText / StartTime / EndTime
 ├── UndoStack / RedoStack: Stack<List<TrackData>> (deep clone)
 └── SelectionPos: double
```

## Animatable Properties

PosX, PosY, Scale, Opacity, Volume are AnimatableProperty:
- **Static**: single float, GetValueAt() returns constant
- **Animated**: Keyframes (time + value) with linear interpolation
- **Text keyframes**: TextKeyframes list with time + text

## TestServer (AI Testing)

TCP server on `127.0.0.1:18765`. JSON-line commands: ping, quit, reset, screenshot, import_file, get_tracks, set_selection, set_timeline_pos, call, click_button, get_property, get_ui_state, list_buttons.

Python client: `test_client.py` with `AppClient` class.

## Keyboard Shortcuts

| Key | Action |
|---|---|
| Space | Play / Pause |
| Enter / K | Pause & move selection |
| V / R | Select / Razor tool |
| S | Split clip at playhead |
| Left / Right | Jump 5% of view |
| Shift+Left / Right | Step frame |
| Ctrl+Z / Ctrl+Shift+Z | Undo / Redo |
| Delete | Delete selected |
| Ctrl+I / Ctrl+T / Ctrl+G | Import / Text / Captions |
| Ctrl+E | Export |
| Shift+Scroll | Zoom timeline |
| Ctrl+Scroll | Scroll tracks vertically |
| Scroll | Pan timeline horizontally |
| ? / Ctrl+/ | Help overlay |

## Build & Export

```
cd godot_project
dotnet build

# Also build WhisperWorker subprocess
dotnet publish ../WhisperWorker/WhisperWorker.csproj -c Release -o WhisperWorker_published

# Copy Vulkan & CPU runtimes to Worker dir:
cp -r .godot/mono/temp/bin/Debug/runtimes/vulkan WhisperWorker_published/runtimes/
cp -r .godot/mono/temp/bin/Debug/runtimes/linux-x64 WhisperWorker_published/runtimes/

godot-mono --headless --path .
```

**Export (release, embedded PCK):**
```
godot-mono --headless --export-release "Linux/X11" app_exports/linux/ClipTool.x86_64
godot-mono --headless --export-release "Windows Desktop" app_exports/windows/ClipTool.exe
```

Requires: Godot 4 Mono, .NET SDK, yt-dlp, FFmpeg, llama-cli (optional, for AI clip finding).
