# ClipTool Test Steps

## New Features in v0.4.0

### 1. Language Dropdown in AI Clip Finder

**Test Steps:**
1. Launch ClipTool, paste a Twitch VOD URL, click "AI Find Clips"
2. **Verify**: A dialog appears titled "AI Clip Finder" with:
   - "Video Language" dropdown showing "English" (default)
   - 99 languages available (scroll through)
   - "LLM Model" list below the language selector
3. Select a non-English language (e.g., "Greek" for Greek VODs)
4. Select an LLM model and click "Find Clips"
5. **Verify**: The transcription uses the selected language
6. **Verify**: `session.log` contains `language=el` (or chosen code) in the AI Clip Finder log line

**Expected:** Language choice persists for the session but defaults to Settings value next time.

---

### 2. External Debug Console

**Test Steps:**
1. Click the "Console" toolbar button (or press `Ctrl+Shift+C`)
2. **On Windows**: A cmd window should appear showing real-time logs
3. **On Linux**: Logs appear in the terminal where ClipTool was launched
4. Click "Console" again (or `Ctrl+Shift+C`)
5. **On Windows**: The cmd window should close
6. **Verify**: The `session.log` file in `~/.config/cliptool/` captures all logs
7. Do some operations (import, seek, export) and verify logs appear in real-time

**Expected:** Console toggles on/off cleanly without crashing. All `Log.Print()` calls appear in both the console and `session.log`.

---

### 3. Extensive Logging

**Test Steps:**
1. Launch ClipTool
2. **Verify** startup log: `session.log` contains version, platform, Godot version
3. Import a video
4. **Verify**: `LoadVideoAsset`, `StreamManager.GetInfo` appear in logs
5. Switch between Import/Layout/Edit states
6. **Verify**: `SwitchToState` log entries for each transition
7. Export a video
8. **Verify**: `ExportAsync` log entry with input/output/layers
9. Check `~/.config/cliptool/session.log` for all entries

**Expected:** Every major operation is logged with context.

---

### 4. HW Encoder Detection (AMD fix)

**Test Steps:**
1. Run ClipTool on a system with AMD GPU (or no NVIDIA GPU)
2. Initiate an export
3. **Verify**: `Exporter.Encoder test for h264_nvenc failed` appears if NVENC not available
4. **Verify**: Falls through to working encoder (amf, qsv, or libx264)
5. **Verify**: Export completes successfully with correct encoder

**Expected:** Export succeeds on any GPU configuration.

---

### 5. Import Audio Tracks

**Test Steps:**
1. Import a video (first video) — creates Source Video + Source Audio tracks
2. **Verify**: Both tracks present in timeline
3. Import another video from the bin (click and add to timeline)
4. **Verify**: Both video clip AND audio clip are added to timeline
5. Play back — both clips should have audio

**Expected:** Every video clip on the timeline has an associated audio clip.

---

### 6. Image Rotation Bounding Box

**Test Steps:**
1. Add an image to the timeline
2. Select it in the preview (blue bounding box appears)
3. Rotate the image via the rotation bar (↺/↻)
4. **Verify**: The blue bounding box rotates WITH the image
5. Double-click a text clip after rotation
6. **Verify**: Text editor appears at the correct (rotated) position

**Expected:** Bounding box and hitbox follow rotation.

---

### 7. Multi-Clip Video Playback

**Test Steps:**
1. Import a video via AI Clip Finder (multiple clips detected)
2. Drag a second clip from the bin onto the timeline
3. Play from before the first clip
4. **Verify**: Playback switches video file when playhead enters the second clip
5. **Verify**: Audio switches too

**Expected:** Multiple video files play seamlessly on the same timeline.

---

### 8. Sound Browser Volume Slider

**Test Steps:**
1. Open the Sound Browser
2. **Verify**: A "Volume:" slider is visible below the search box
3. Move the slider
4. **Verify**: Percentage label updates (0%, 50%, 100%)
5. Click "Preview" on a sound
6. **Verify**: Preview volume changes with the slider

**Expected:** Volume slider controls preview loudness without affecting main mix.

---

## Regression Tests

### Import Flow
- [ ] Paste a Twitch VOD URL → thumbnail loads
- [ ] "Select Clips" opens clip picker
- [ ] "AI Find Clips" opens language + model dialog
- [ ] Cancel button closes all dialogs
- [ ] Import an MP4 file from filesystem
- [ ] Image import (PNG, JPG, GIF)
- [ ] Audio import (MP3, WAV, OGG)

### Timeline
- [ ] Drag clips to reorder
- [ ] Ctrl+click for multi-select
- [ ] Drag multi-selected clips
- [ ] Trim start/end handles
- [ ] Loop region set/unset
- [ ] Playback (play/pause, seek)
- [ ] Keyboard shortcuts (Space, arrows, Ctrl+Z, etc.)

### Export
- [ ] Export at 16:9
- [ ] Export at 9:16
- [ ] Export with text/watermark overlay
- [ ] Export with image overlay
- [ ] Export with blur background
- [ ] Export with captions
- [ ] Cancel mid-export

### UI
- [ ] Settings dialog: change output dir, language, normalize
- [ ] Settings persist across app restarts
- [ ] Inspector shows correct clip properties
- [ ] Font browser opens and shows local fonts
- [ ] Sound browser shows local sounds on open
- [ ] Image browser shows local images on open
- [ ] Console toggle (Ctrl+Shift+C)
- [ ] In-app ConsoleDialog still works
