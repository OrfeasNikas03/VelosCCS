// Import pipeline: local file import, YouTube download, video info fetching,
// waveform loading, and project bin management.

using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
// HttpClient is fully qualified as System.Net.Http.HttpClient to avoid clash with Godot.HttpClient
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace VelosCCS;

public partial class MainWindow
{
	private static string T() => DateTime.Now.ToString("HH:mm:ss.fff");

	private string _lastClipboardUrl = "";

	// Poll clipboard for YouTube/Twitch links, auto-fetch metadata
	private void PollClipboard()
	{
		string text = DisplayServer.ClipboardGet().Trim();
		if (text != _lastClipboardUrl && (text.StartsWith("http") || text.Contains("youtube.com") || text.Contains("twitch.tv") || text.Contains("youtu.be")))
		{
			_lastClipboardUrl = text;
			_urlInput.Text = text;
			OnDownloadPressed();
		}
	}

	// Validate file extension, create MediaAsset, fetch video info async,
	// add to project bin. If this is the first video, load it immediately.
	private async void ImportFileToBin(string path)
	{
		string lower = path.ToLower();
		bool isVideo = lower.EndsWith(".mp4") || lower.EndsWith(".mov") || lower.EndsWith(".avi") || lower.EndsWith(".mkv") || lower.EndsWith(".webm");
		bool isAudio = lower.EndsWith(".mp3") || lower.EndsWith(".wav") || lower.EndsWith(".ogg") || lower.EndsWith(".flac");
		bool isImage = lower.EndsWith(".png") || lower.EndsWith(".jpg") || lower.EndsWith(".jpeg") || lower.EndsWith(".gif") || lower.EndsWith(".webp") || lower.EndsWith(".bmp");

		if (!isVideo && !isAudio && !isImage)
		{
			SetStatus("Unsupported file format", Colors.Red);
			return;
		}

		string name = System.IO.Path.GetFileNameWithoutExtension(path);
		var assetType = isImage ? AssetType.Image : (isVideo ? AssetType.Video : AssetType.Audio);
		var asset = new MediaAsset(name, path, assetType)
		{
			Duration = isImage ? 5.0 : 0,
		};

		if (isAudio)
		{
			_projectBin.Add(asset);
			RefreshBinUI();
			AddAudioClipToTimeline(name, path);
			SetStatus($"Imported audio: {name}", Color.FromHtml("#D0570C"));
			return;
		}

		if (isImage)
		{
			_projectBin.Add(asset);
			RefreshBinUI();
			AddImageClipToTimeline(path, _timeline?.SelectionPos ?? 0);
			SetStatus($"Imported image: {name}", Color.FromHtml("#D0570C"));
			return;
		}

		if (isVideo && _videoPlayer.Stream == null)
		{
			// First video: fetch info before loading so duration is correct
			await FetchVideoInfoForAsset(asset);
			LoadVideoAsset(asset);
			SwitchToState(ViewState.Layout);
		}
		else if (isVideo)
		{
			// Subsequent videos: fire-and-forget, info only needed for bin display
			_ = FetchVideoInfoForAsset(asset);
		}

		_projectBin.Add(asset);
		RefreshBinUI();
		SetStatus($"Imported: {name}", Color.FromHtml("#D0570C"));
	}

	// Query backend for video metadata (duration), update asset and bin UI
	private async Task FetchVideoInfoForAsset(MediaAsset asset)
	{
		Log.Print($"[DL] FetchVideoInfoForAsset: {asset.Path}");
		try
		{
			var info = await _backendService.GetVideoInfo(asset.Path);
			asset.Duration = info.Duration;
			RefreshBinUI();
		}
		catch
		{
			asset.Duration = 60;
		}
	}

	// Load video into VideoStreamPlayer, create Source Video / Camera / Source
	// Audio tracks, copy temp file to user://, start playback, fetch waveform.
	private void LoadVideoAsset(MediaAsset asset)
	{
		Log.Print($"[DL] LoadVideoAsset: {asset.Path}");
		_videoPath = asset.Path;
		_tracks = new List<TrackData>
		{
			new() { Name = "Source Video", Type = TrackType.Video, ZIndex = 0 },
		};

		var userPath = "user://temp_video.mp4";
		System.IO.File.Copy(asset.Path, ProjectSettings.GlobalizePath(userPath), true);
		_videoPlayer.Stream = ResourceLoader.Load<VideoStream>(userPath);
		if (_videoPlayer.Stream != null) { _videoPlayer.Play(); _isPlaying = true; }

		_videoDuration = asset.Duration > 0 ? asset.Duration : 60;

		var srcClip = new TrackClipData { Start = 0, End = _videoDuration, ClipType = ClipType.SourceVideo, FilePath = asset.Path };
		_tracks[0].Clips.Add(srcClip);

		var srcAudio = new TrackData { Name = "Source Audio", Type = TrackType.Audio, ZIndex = 2 };
		srcAudio.Clips.Add(new TrackClipData { Start = 0, End = _videoDuration, ClipType = ClipType.Audio, FilePath = asset.Path });
		_tracks.Add(srcAudio);

		_ = LoadWaveform(asset);

		UpdateTracks();
		SetStatus($"Loaded: {_videoDuration:F1}s video");
		_srcInfoLabel.Text = $"{System.IO.Path.GetFileName(asset.Path)}  ({_videoDuration:F1}s)";

		_overlay.Visible = true;
	}

	// Fetch waveform peaks from backend and attach to audio clips only
	private async Task LoadWaveform(MediaAsset asset)
	{
		Log.Print("[DL] LoadWaveform");
		try
		{
			var wf = await _backendService.GetWaveform(asset.Path);
			var peaks = wf?.Peaks ?? new List<float>();
			asset.WaveformPeaks = peaks;

			foreach (var track in _tracks)
			{
				if (track.Type == TrackType.Audio)
				{
					foreach (var clip in track.Clips)
					{
						if (clip.ClipType == ClipType.Audio)
							clip.WaveformPeaks = peaks;
					}
				}
				else if (track.Type == TrackType.Video)
				{
					foreach (var clip in track.Clips)
						clip.WaveformPeaks = new List<float>();
				}
			}

			UpdateTracks();
		}
		catch (Exception e) { GD.PrintErr("Waveform fail: " + e.Message); }
	}

	// Double-click handler for media bin items: add to timeline at selection pos
	private void AddAssetToTimeline(MediaAsset asset)
	{
		SnapshotState();
		if (asset.Type == AssetType.Video)
		{
			var vidTrack = _tracks.FirstOrDefault(t => t.Type == TrackType.Video && t.Name == "Source Video");
			if (vidTrack == null)
			{
				vidTrack = new TrackData { Name = "Source Video", Type = TrackType.Video };
				_tracks.Add(vidTrack);
			}

			double pos = _timeline?.SelectionPos ?? 0;
			double dur = asset.Duration > 0 ? asset.Duration : 10;
			var clip = new TrackClipData
			{
				ClipType = ClipType.SourceVideo,
				Start = pos,
				End = pos + dur,
				FilePath = asset.Path,
				WaveformPeaks = asset.WaveformPeaks ?? new(),
			};
			vidTrack.Clips.Add(clip);

			// Also add an audio clip for this video
			var audioTrack = _tracks.FirstOrDefault(t => t.Type == TrackType.Audio && t.Name == "Source Audio");
			if (audioTrack == null)
			{
				audioTrack = new TrackData { Name = "Source Audio", Type = TrackType.Audio, ZIndex = 2 };
				_tracks.Add(audioTrack);
			}
			audioTrack.Clips.Add(new TrackClipData
			{
				ClipType = ClipType.Audio,
				Start = pos,
				End = pos + dur,
				FilePath = asset.Path,
			});

			// Clone into existing layout tracks so new clip inherits size/position
			string[] layoutTrackNames = { "Basic Facecam", "Camera", "UI Content" };
			foreach (string name in layoutTrackNames)
			{
				var layoutTrack = _tracks.FirstOrDefault(t => t.Name == name);
				if (layoutTrack == null) continue;
				var existing = layoutTrack.Clips.FirstOrDefault();
				if (existing == null) continue;
				var layoutClip = clip.Clone();
				layoutClip.Position = existing.Position;
				layoutClip.Size = existing.Size;
				layoutClip.Scale.StaticValue = existing.Scale.StaticValue;
				layoutClip.Opacity.StaticValue = existing.Opacity.StaticValue;
				layoutClip.Rotation.StaticValue = existing.Rotation.StaticValue;
				layoutTrack.Clips.Add(layoutClip);
			}

			UpdateTracks();
			SetStatus($"Added {asset.Name} to timeline", Color.FromHtml("#D0570C"));
		}
		else if (asset.Type == AssetType.Audio)
		{
			AddAudioClipToTimeline(asset.Name, asset.Path);
		}
		else if (asset.Type == AssetType.Image)
		{
			AddImageClipToTimeline(asset.Path, _timeline.SelectionPos);
		}
		else if (asset.Type == AssetType.Text)
		{
			// If this is a transcribed caption with timestamps, re-add at the original
			// audio-synced position instead of at the cursor (plain OnAddTextClip).
			if (asset.StartTime > 0 || asset.EndTime > 0)
			{
				var captionsTrack = _tracks.FirstOrDefault(t => t.Name == "Captions");
				if (captionsTrack == null)
				{
					captionsTrack = new TrackData { Name = "Captions", Type = TrackType.Video };
					_tracks.Add(captionsTrack);
				}
				var clip = new TrackClipData
				{
					ClipType = ClipType.Text,
					Text = asset.CaptionText ?? asset.Name,
					Start = asset.StartTime,
					End = asset.EndTime,
					FontSize = 48,
					FontColor = Colors.White,
					OutlineColor = Colors.Black,
					OutlineWidth = 4,
					Position = new Vector2(0.1f, 0.85f),
					Size = new Vector2(0.8f, 0.12f),
				};
				captionsTrack.Clips.Add(clip);
				UpdateTracks();
				SetStatus($"Added caption: {asset.Name}", Color.FromHtml("#D0570C"));
			}
			else
			{
				OnAddTextClip();
			}
		}
	}

	// Refresh the ItemList in the Media tab from _projectBin contents
	private void RefreshBinUI()
	{
		_binUI.Clear();
		_binFilteredIndices.Clear();

		string filter = _binSearchFilter?.Trim().ToLowerInvariant() ?? "";

		for (int i = 0; i < _projectBin.Count; i++)
		{
			var asset = _projectBin[i];

			// Apply search filter
			if (!string.IsNullOrEmpty(filter))
			{
				bool match = asset.Name.ToLowerInvariant().Contains(filter)
					|| (asset.CaptionText != null && asset.CaptionText.ToLowerInvariant().Contains(filter));
				if (!match) continue;
			}

			_binFilteredIndices.Add(i);

			string icon = asset.Type switch
			{
				AssetType.Video => "🎬 ",
				AssetType.Audio => "🔊 ",
				AssetType.Text => "📝 ",
				_ => "",
			};
			string label = icon + asset.Name;
			if (asset.Duration > 0)
				label += $" ({asset.Duration:F1}s)";
			if (asset.Type == AssetType.Text && !string.IsNullOrEmpty(asset.CaptionText))
				label = icon + asset.CaptionText;
			_binUI.AddItem(label);
		}

		if (_binUI.ItemCount == 0 && !string.IsNullOrEmpty(filter))
			_binUI.AddItem("(no results)");
	}

	// YouTube/URL download: fetch info on background thread, show thumbnail preview,
	// then open ClipPickerWindow on "Select Clips" button
	private static readonly Regex GoogleDriveRegex = new(@"drive\.google\.com|docs\.google\.com", RegexOptions.IgnoreCase);

	private async void OnDownloadPressed()
	{
		Log.Print("[UI] OnDownloadPressed");
		var url = _urlInput.Text.Trim();
		if (string.IsNullOrEmpty(url)) return;

		_urlInput.Editable = false;
		SetStatus("Connecting to stream...", Colors.Yellow);

		if (GoogleDriveRegex.IsMatch(url))
		{
			SetStatus("Google Drive links are not supported. Use YouTube or a direct video URL.", Colors.Red);
			_urlInput.Editable = true;
			return;
		}

		try
		{
			StreamInfo info;
			if (url.StartsWith("http://") || url.StartsWith("https://"))
			{
				// Run yt-dlp info fetch on background thread
				info = await Task.Run(() => _backendService.GetYtInfo(url));
			}
			else
			{
				try
				{
					var vi = await _backendService.GetVideoInfo(url);
					if (vi.Duration > 0)
					{
						info = new StreamInfo { Url = url, Title = "Online Video", Duration = vi.Duration, WebpageUrl = url };
					}
					else
					{
						throw new Exception("Not a valid file");
					}
				}
				catch
				{
					info = await Task.Run(() => _backendService.GetYtInfo(url));
				}
			}

			if (info.Duration <= 0)
			{
				SetStatus("Could not determine video duration", Colors.Red);
				return;
			}

			_lastStreamInfo = info;
			ShowThumbnailPreview(info);

			if (info.Duration > 1200)
				SetStatus($"Found: {info.Title} (Long video — consider selecting a range)", Color.FromHtml("#D0570C"));
			else
				SetStatus($"Ready: {info.Title}", Color.FromHtml("#D0570C"));
			this.LogSizes("OnDownloadPressed");
		}
		catch (Exception e)
		{
			SetStatus($"Link error: {e.Message}", Colors.Red);
		}
		finally
		{
			_urlInput.Editable = true;
		}
	}

	private async void ShowThumbnailPreview(StreamInfo info)
	{
		string title = info.Title;
		string uploader = info.Uploader;
		double duration = info.Duration;
		string durStr = duration >= 3600
			? $"{duration/3600:F0}h{(duration%3600)/60:F0}m{duration%60:F0}s"
			: duration >= 60
				? $"{duration/60:F0}m{duration%60:F0}s"
				: $"{duration:F0}s";

		_importInfo.Text = $"{title}\n{uploader}  •  {durStr}";

		if (!string.IsNullOrEmpty(info.Thumbnail))
		{
			try
			{
				var http = new System.Net.Http.HttpClient();
				var bytes = await http.GetByteArrayAsync(info.Thumbnail);
				http.Dispose();
				if (bytes.Length > 0)
				{
					var img = new Godot.Image();
					if (img.LoadPngFromBuffer(bytes) == Godot.Error.Ok ||
						img.LoadJpgFromBuffer(bytes) == Godot.Error.Ok ||
						img.LoadWebpFromBuffer(bytes) == Godot.Error.Ok)
					{
						_importThumb.Texture = Godot.ImageTexture.CreateFromImage(img);
					}
				}
			}
			catch (Exception e)
			{
				GD.PrintErr("Thumbnail fetch failed: " + e.Message);
			}
		}

		_importPreview.Visible = true;
	}

	private void OnSelectClips()
	{
		Log.Print("[UI] OnSelectClips");
		if (_lastStreamInfo == null || _lastStreamInfo.Duration <= 0) return;

		var picker = new ClipPickerWindow();
		AddChild(picker);
		picker.Setup(_lastStreamInfo.Title, _lastStreamInfo.Duration, _lastStreamInfo.Thumbnail);
		picker.DownloadRequested += (fragments) => ProcessDownloads(_lastStreamInfo.Url, fragments);
		picker.PopupCentered();
		this.LogSizes("OnSelectClips");
	}

	// AI clip finder: show setup dialog, download audio, transcribe, run LLM detection, auto-download clips
	private void OnAIFindClips()
	{
		Log.Print("[UI] OnAIFindClips");
		if (_lastStreamInfo == null || _lastStreamInfo.Duration <= 0) return;

		var dialog = new AISetupDialog();
		AddChild(dialog);
		dialog.Proceed += (model, language, maxHeight) => _ = RunAIClipFinder(model, language, maxHeight);
		dialog.PopupCentered();
	}

	private async Task RunAIClipFinder(string model, string language, int maxHeight = 720)
	{
		var totalSw = System.Diagnostics.Stopwatch.StartNew();

		Log.Print($"AI Clip Finder: started with model={model}, language={language}, maxHeight={maxHeight}p, url={_lastStreamInfo.Url}");
		double vodDuration = _lastStreamInfo.Duration;
		SystemResources.Log("AI finder: start");
		GD.Print($"AI Clip Finder: VOD duration={vodDuration:F0}s ({(vodDuration/3600):F1}h)");

		var progressWin = new ProgressWindow();
		AddChild(progressWin);
		progressWin.PopupCentered();

		string? audioFile = null;
		string audioDir = ProjectSettings.GlobalizePath("user://temp_audio/");
		string clipsDir = !string.IsNullOrEmpty(AppConfig.ClipOutputDir)
			? AppConfig.ClipOutputDir
			: ProjectSettings.GlobalizePath("user://clips/");
		Directory.CreateDirectory(audioDir);
		Directory.CreateDirectory(clipsDir);

		// Derive a per-video filename so cached audio doesn't collide across videos
		string videoId = "";
		var idMatch = System.Text.RegularExpressions.Regex.Match(_lastStreamInfo.Url,
			@"(?:v=|youtu\.be/|youtube\.com/embed/)([a-zA-Z0-9_-]{11})");
		if (idMatch.Success)
			videoId = "_" + idMatch.Groups[1].Value;
		string audioBase = System.IO.Path.Combine(audioDir, $"vod_audio{videoId}");

		try
		{
			// ── STEP 1: Audio-Only Extraction with live progress ──
			progressWin.SetStep("Downloading audio track from stream...");
			progressWin.SetProgress(0.0);
			SetStatus("Connecting to stream...", Colors.Yellow);
			GD.Print("AI Clip Finder: STEP 1/4 — extracting audio track");

			var sm = new StreamManager();
			GD.Print("AI Clip Finder: starting yt-dlp audio download with --newline progress");
			audioFile = await Task.Run(() => sm.DownloadAudioWithProgress(
				_lastStreamInfo.Url,
				audioBase,
				(pct, spd, eta) =>
				{
					string p = pct, s = spd, e = eta;
					Callable.From(() =>
					{
						double frac = double.Parse(p) / 100.0 * 0.05;
						progressWin.SetStep($"Downloading audio: {p}% at {s}, ETA {e}");
						progressWin.SetProgress(frac);
						SetStatus($"Downloading audio: {p}% ({s}, ETA {e})", Colors.Yellow);
					}).CallDeferred();
				}));

			var audioSize = new System.IO.FileInfo(audioFile).Length;
			string sizeStr = audioSize >= 1_000_000_000
				? $"{audioSize / 1_000_000_000.0:F1} GB"
				: audioSize >= 1_000_000
					? $"{audioSize / 1_000_000.0:F1} MB"
					: $"{audioSize / 1_000.0:F1} KB";
			GD.Print($"AI Clip Finder: audio downloaded to {audioFile} ({sizeStr})");
			SystemResources.Log("after audio download");
			progressWin.SetStep($"Audio ready — {sizeStr}");
			progressWin.SetProgress(0.05);
			progressWin.Log($"Audio downloaded ({sizeStr})");
			SetStatus($"Audio downloaded ({sizeStr})", Color.FromHtml("#D0570C"));

			// ── STEP 2: Windowed Transcription & Analysis (Map-Reduce) ──
			double windowSize = 900; // 15-minute chunks
			int totalWindows = (int)Math.Ceiling(vodDuration / windowSize);
		var allDetectedClips = new List<(double start, double end)>();
		int totalSegs = 0;
		int windowsWithSpeech = 0;
		int windowsWithClips = 0;

		GD.Print($"AI Clip Finder: STEP 2/4 — windowed transcription ({totalWindows} windows of {windowSize / 60:F0} min)");

		for (int w = 0; w < totalWindows; w++)
		{
			double windowStart = w * windowSize;
			double windowEnd = Math.Min(windowStart + windowSize, vodDuration);
			double windowDur = windowEnd - windowStart;

			string label = $"[{StreamManager.FormatTime(windowStart)} - {StreamManager.FormatTime(windowEnd)}]";
			double windowPct = 0.05 + (double)w / totalWindows * 0.70;
			progressWin.SetStep($"Window {w + 1}/{totalWindows} {label}");
			progressWin.SetProgress(windowPct);
			SetStatus($"Transcribing {label}", Colors.Yellow);
			GD.Print($"AI Clip Finder: [{T()}] window {w + 1}/{totalWindows} {label} ({windowDur:F0}s) — transcribing");
			SystemResources.Log($"before transcribe window {w + 1}/{totalWindows}");

			GC.Collect();
			GC.WaitForPendingFinalizers();

			// Isolate each window so one failure doesn't kill the pipeline
			try
			{
			Log.Print($"AI Clip Finder: transcribing window {w + 1}/{totalWindows} in {language}");
			var transcript = await _backendService.Transcriber.TranscribeChunkAsync(
				audioFile, windowStart, windowEnd,
				language: language,
				progressCallback: msg =>
				{
					GD.Print("AI Clip Finder: " + msg);
					progressWin.SetStep($"Window {w + 1}/{totalWindows}: {msg}");
				});

				if (transcript.Segments.Count == 0)
				{
					GD.Print($"AI Clip Finder: [{T()}] window {label} — NO SPEECH");
					progressWin.Log($"Window {w + 1}/{totalWindows}: no speech detected");
					_backendService.Transcriber.UnloadModel();
					continue;
				}
				windowsWithSpeech++;

				totalSegs += transcript.Segments.Count;
				int charCount = 0;
				foreach (var s in transcript.Segments) charCount += s.Text.Length;
				GD.Print($"AI Clip Finder: [{T()}] window {label} — {transcript.Segments.Count} segs, {charCount} chars");
				SystemResources.Log($"after transcribe window {w + 1}/{totalWindows}");
				progressWin.Log($"Window {w + 1}/{totalWindows}: {transcript.Segments.Count} segments");

				_backendService.Transcriber.UnloadModel();
				SystemResources.Log($"after whisper unload window {w + 1}/{totalWindows}");

				// Let GPU driver reclaim VRAM before starting LLM
				GC.Collect();
				GC.WaitForPendingFinalizers();
				await Task.Delay(1500);

				progressWin.SetStep($"AI analyzing window {w + 1}/{totalWindows} ({transcript.Segments.Count} segments)...");
				SetStatus($"AI analyzing {label}...", Colors.Yellow);
				GD.Print($"AI Clip Finder: [{T()}] window {label} — analyzing with LLM");
				SystemResources.Log($"before AI analyze window {w + 1}/{totalWindows}");

				var detector = new LLMHighlightDetector();
				var clips = await detector.FindHighlightsAsync(
					transcript.Segments,
					maxClips: 2,
					minDuration: 30,
					maxDuration: 60,
					progressCallback: msg =>
					{
						GD.Print("AI Clip Finder: LLM — " + msg);
						progressWin.SetStep($"AI analyzing window {w + 1}/{totalWindows}: {msg}");
					});

				if (clips.Count > 0)
				{
					windowsWithClips++;
					GD.Print($"AI Clip Finder: [{T()}] window {label} — {clips.Count} clips:");
					foreach (var (cs, ce) in clips)
						GD.Print($"  → {cs:F1}s - {ce:F1}s (dur {ce - cs:F1}s)");
					progressWin.Log($"Window {w + 1}/{totalWindows}: {clips.Count} clip(s) found");
				}
				else
				{
					GD.Print($"AI Clip Finder: [{T()}] window {label} — LLM returned 0 clips");
				}
				SystemResources.Log($"after AI analyze window {w + 1}/{totalWindows}");

				// Let GPU driver reclaim VRAM before next window
				GC.Collect();
				GC.WaitForPendingFinalizers();
				await Task.Delay(1500);

				allDetectedClips.AddRange(clips);
			}
			catch (Exception ex)
			{
				GD.PrintErr($"AI Clip Finder: [{T()}] window {label} FAILED: {ex.Message}");
				SystemResources.Log($"window {w + 1}/{totalWindows} FAILED");
				_backendService.Transcriber.UnloadModel();
				// Continue to next window instead of aborting the entire pipeline
			}

			GC.Collect();
			GC.WaitForPendingFinalizers();
		}

		GD.Print($"AI Clip Finder: STEP 2 complete — {totalWindows} windows, {windowsWithSpeech} with speech, {windowsWithClips} with clips, {totalSegs} total segments, {allDetectedClips.Count} clips total, elapsed={totalSw.Elapsed.TotalMinutes:F1}m");
		GD.Print($"AI Clip Finder: clip list: [{string.Join(", ", allDetectedClips.Select(c => $"({c.start:F1}s,{c.end:F1}s)"))}]");
		SystemResources.Log("after all windows");
			_backendService.Transcriber.UnloadModel();

			if (allDetectedClips.Count == 0)
			{
				progressWin.BounceOutThenHide();
				SetStatus("AI couldn't find any clip-worthy moments.", Colors.Orange);
				GD.Print("AI Clip Finder: no clips found in any window — aborting");
				return;
			}

			// ── STEP 3: Targeted Video Extraction with progress ──
			progressWin.SetStep($"Downloading {allDetectedClips.Count} viral candidates...");
			progressWin.SetProgress(0.80);
			SetStatus($"Downloading {allDetectedClips.Count} clips from stream...", Colors.Cyan);
			SwitchToState(ViewState.Layout);
			GD.Print($"AI Clip Finder: STEP 3/4 — downloading {allDetectedClips.Count} clips");

			long totalDownloadedBytes = 0;
			int c = 0;
			foreach (var (cStart, cEnd) in allDetectedClips)
			{
				c++;
				double dur = cEnd - cStart;
				string outPath = System.IO.Path.Combine(clipsDir, $"vod_clip_{c}.mp4");
				GD.Print($"AI Clip Finder: downloading clip {c}/{allDetectedClips.Count}: start={cStart:F1}s dur={dur:F1}s -> {outPath}");

				// Use async progress-tracking download
				int clipIdx = c;
				await sm.DownloadSectionWithProgressAsync(
					_lastStreamInfo.Url, cStart, dur, outPath,
					(pct, spd, eta) =>
					{
						Callable.From(() =>
						{
							double baseProgress = 0.80 + (double)(clipIdx - 1) / allDetectedClips.Count * 0.18;
							double clipFraction = double.Parse(pct) / 100.0 * 0.18 / allDetectedClips.Count;
							progressWin.SetStep($"Downloading clip {clipIdx}/{allDetectedClips.Count}: {pct}% at {spd}, ETA {eta}");
							progressWin.SetProgress(baseProgress + clipFraction);
							SetStatus($"Downloading clip {clipIdx}/{allDetectedClips.Count}: {pct}% ({spd})", Colors.Yellow);
						}).CallDeferred();
					}, maxHeight: maxHeight);

				long fileBytes = new System.IO.FileInfo(outPath).Length;
				totalDownloadedBytes += fileBytes;
				string clipSize = fileBytes >= 1_000_000
					? $"{fileBytes / 1_000_000.0:F1} MB"
					: $"{fileBytes / 1_000.0:F1} KB";
				GD.Print($"AI Clip Finder: clip {c} downloaded — {clipSize}");
				SystemResources.Log($"after clip {c} download");

				_projectBin.Add(new MediaAsset($"Viral Clip {c}", outPath, AssetType.Video, dur));

				double progressFraction = 0.80 + (double)c / allDetectedClips.Count * 0.18;
				progressWin.SetStep($"Clip {c}/{allDetectedClips.Count} ready ({clipSize})");
				progressWin.SetProgress(progressFraction);
				SetStatus($"Clip {c}/{allDetectedClips.Count}: {clipSize}", Color.FromHtml("#D0570C"));
			}

			RefreshBinUI();

			if (_projectBin.Count > 0 && _videoPlayer.Stream == null)
				LoadVideoAsset(_projectBin[0]);

			// ── Final Summary ──
			string totalSize = totalDownloadedBytes >= 1_000_000_000
				? $"{totalDownloadedBytes / 1_000_000_000.0:F1} GB"
				: totalDownloadedBytes >= 1_000_000
					? $"{totalDownloadedBytes / 1_000_000.0:F1} MB"
					: $"{totalDownloadedBytes / 1_000.0:F1} KB";
			progressWin.BounceOutThenHide();
			ToastManager.Success(this, $"Found {c} clips ({totalSize})");
			SetStatus($"AI complete — {c} clips extracted ({totalSize})", Color.FromHtml("#3fb950"));
			SystemResources.Log("AI finder: done");
			GD.Print($"AI Clip Finder: DONE — {c} clips, {totalSize} total, {allDetectedClips.Count} windows processed");
		}
		catch (Exception e)
		{
			progressWin.BounceOutThenHide();
			SystemResources.Log("AI finder: FAILED");
			ToastManager.Error(this, $"AI analysis failed: {e.Message}");
			SetStatus($"VOD analysis failed: {e.Message}", Colors.Red);
			GD.PrintErr("AI Clip Finder: FATAL — " + e);
		}
		finally
		{
			if (audioFile != null && System.IO.File.Exists(audioFile))
			{
				long cleanupSize = new System.IO.FileInfo(audioFile).Length;
				try
				{
					System.IO.File.Delete(audioFile);
					GD.Print($"AI Clip Finder: cleaned up temp audio ({cleanupSize / 1_000_000.0:F1} MB freed)");
				}
				catch (Exception ex) { GD.PrintErr("AI Clip Finder: failed to delete temp audio: " + ex.Message); }
			}
			// Free LLM model from VRAM (no-op: subprocess releases on exit)
			await LLMHighlightDetector.UnloadModelAsync();
			_aiFindBtn.Disabled = false;
			_progressBar.Visible = false;
			ToastManager.Info(this, "AI analysis complete — clips ready for editing");
			SystemResources.Log("AI finder: resources released");
			GD.Print("AI Clip Finder: resources released");
		}
	}

	// Call backend to download YouTube fragments, add resulting clips to project bin
	private async void ProcessDownloads(string url, Godot.Collections.Array<Godot.Collections.Dictionary> fragments)
	{
		Log.Print($"[DL] ProcessDownloads: {url}, {fragments.Count} fragments");
		SwitchToState(ViewState.Layout);
		SetStatus("Downloading fragments...", Colors.Cyan);

		try
		{
			string outputDir = !string.IsNullOrEmpty(AppConfig.ClipOutputDir)
				? AppConfig.ClipOutputDir
				: ProjectSettings.GlobalizePath("user://clips/");
			Directory.CreateDirectory(outputDir);
			int i = 0;
			foreach (var frag in fragments)
			{
				i++;
				double start = frag["start"].As<double>();
				double end = frag["end"].As<double>();
				double dur = end - start;
				string outPath = System.IO.Path.Combine(outputDir, $"download_clip_{i}.mp4");
				_backendService.DownloadSection(url, start, dur, outPath);
				_projectBin.Add(new MediaAsset($"Clip {i}", outPath, AssetType.Video, dur));
			}
			RefreshBinUI();

			// Auto-load first downloaded clip
			if (_projectBin.Count > 0 && _videoPlayer.Stream == null)
				LoadVideoAsset(_projectBin[0]);

			SetStatus($"{_projectBin.Count} clip(s) ready in Media Bin", Color.FromHtml("#D0570C"));
		}
		catch (Exception e)
		{
			SetStatus($"Download failed: {e.Message}", Colors.Red);
		}
	}
}
