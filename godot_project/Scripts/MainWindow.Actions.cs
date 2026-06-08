// Action handlers for MainWindow: clip selection, split, delete, text/SFX/sticker
// insertion, caption generation, export, auto-frame, and track update pipeline.

using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace VelosCCS;

public partial class MainWindow
{
	// Map a flat clip index (used by TimelineControl) back to track/clip tuple
	private void OnClipSelected(int index)
	{
		int idx = index;
		for (int t = 0; t < _tracks.Count; t++)
		{
			if (idx < _tracks[t].Clips.Count)
			{
				_selTrackIdx = t;
				_selClipIdx = idx;
				var clip = _tracks[t].Clips[idx];
				var trackName = _tracks[t].Name;

				// Camera/UI track clips show PiP handles on source monitor
				if (trackName is "Camera" or "Basic Facecam")
				{
					var cam = _outputPreview.GetCameraTarget();
					_overlay.SetPipEditing(trackName, new Vector2(cam[0], cam[1]), new Vector2(cam[2], cam[3]));
					_outputPreview.SelectDisplayLayer(clip);
					RebuildInspector();
					return;
				}
				if (trackName == "UI Content")
				{
					var ui = _outputPreview.GetUiTarget();
					_overlay.SetPipEditing(trackName, new Vector2(ui[0], ui[1]), new Vector2(ui[2], ui[3]));
					_outputPreview.SelectDisplayLayer(clip);
					RebuildInspector();
					return;
				}

				_overlay.SelectLayer(t, idx, clip);
				_outputPreview.SelectDisplayLayer(clip);
				RebuildInspector();
				return;
			}
			idx -= _tracks[t].Clips.Count;
		}
	}

	private void ApplyLayoutPreset(string name)
	{
		// Remove empty layout tracks from other presets before adding the new one
		string[] otherTracks = name switch
		{
			"Basic" => new[] { "Camera", "UI Content" },
			"Circle Facecam" => new[] { "Basic Facecam", "UI Content" },
			"Game UI" => new[] { "Basic Facecam", "Camera" },
			_ => Array.Empty<string>(),
		};
		_tracks.RemoveAll(t => otherTracks.Contains(t.Name));

		// Ensure the right tracks exist for each layout preset
		if (name == "Basic" && !_tracks.Any(t => t.Name == "Basic Facecam"))
		{
			var camTrack = new TrackData { Name = "Basic Facecam", Type = TrackType.Video, ZIndex = 1 };
			var sourceClip = _tracks[0].Clips.FirstOrDefault();
			if (sourceClip != null) camTrack.Clips.Add(sourceClip.Clone());
			_tracks.Add(camTrack);
			UpdateTracks();
		}
		else if (name == "Circle Facecam" && !_tracks.Any(t => t.Name == "Camera"))
		{
			var camTrack = new TrackData { Name = "Camera", Type = TrackType.Video, ZIndex = 1 };
			var sourceClip = _tracks[0].Clips.FirstOrDefault();
			if (sourceClip != null) camTrack.Clips.Add(sourceClip.Clone());
			_tracks.Add(camTrack);
			UpdateTracks();
		}
		else if (name == "Game UI" && !_tracks.Any(t => t.Name == "UI Content"))
		{
			var uiTrack = new TrackData { Name = "UI Content", Type = TrackType.Video, ZIndex = 1 };
			var sourceClip = _tracks[0].Clips.FirstOrDefault();
			if (sourceClip != null) uiTrack.Clips.Add(sourceClip.Clone());
			_tracks.Add(uiTrack);
			UpdateTracks();
		}

		int idx = name switch
		{
			"Basic" => 0,
			"Circle Facecam" => 1,
			"Game UI" => 2,
			_ => 0,
		};
		_outputPreview.SetLayoutMode(idx);
		if (idx == 2)
		{
			// Game UI mode: use user's existing overlay region positions,
			// only set the output layout positions. Bounding box (content sub-rect)
			// only applied for non-16:9 outputs (portrait, square, etc.).
			_overlay.SetRegionVisible("UI", true);
			_outputPreview.SetCameraOutput(new Vector2(0, 0), new Vector2(1, 0.316406f));
			if (ExportAspectRatio == "16:9")
				_outputPreview.SetContentOutput(new Vector4(0, 0, 1, 1));
			else
				_outputPreview.SetContentOutput(new Vector4(0, 0.353495f, 1, 0.609417f));
			_outputPreview.SetUiOverlay(new Vector4(0.286509f, 0.305542f, 0.426982f, 0.078031f), new Vector4(0, 0, 1, 1));
		}
		else if (idx == 1)
		{
			// Circle Facecam mode: set output positions, use default crop regions
			_overlay.SetRegionVisible("UI", false);
			_outputPreview.SetContentOutput(new Vector4(0, 0, 1, 1));
			_outputPreview.SetCameraOutput(new Vector2(0.05f, 0.05f), new Vector2(0.4f, 0.25f));
			_outputPreview.SetUiOverlay(new Vector4(0, 0, 0, 0), new Vector4(0, 0, 1, 1));
		}
		else
		{
			// Basic mode: restore default Streamladder crop regions
			_overlay.SetRegionVisible("UI", false);
			_outputPreview.SetContentOutput(new Vector4(0, 0, 1, 1));
			_outputPreview.SetCameraOutput(new Vector2(0.05f, 0.05f), new Vector2(0.4f, 0.25f));
			_outputPreview.SetUiOverlay(new Vector4(0, 0, 0, 0), new Vector4(0, 0, 1, 1));
			var contentRegion = _overlay.GetRegion("Content");
			if (contentRegion != null) contentRegion.Rect = new Rect2(0.036788f, 0.124949f, 0.492216f, 0.875051f);
			var cameraRegion = _overlay.GetRegion("Camera");
			if (cameraRegion != null) cameraRegion.Rect = new Rect2(0.581453f, 0.675695f, 0.228027f, 0.324305f);
			_overlay.QueueRedraw();
		}
	}

	// Split all selected clips at the timeline selection position.
	// Each clip is split into two: left half keeps original start, right half
	// starts at split time. Both get the cloned properties.
	private void SplitAtPlayhead()
	{
		if (_timeline == null) return;
		SnapshotState();
		double time = _timeline.SelectionPos;
		int[] selIndices = _timeline.GetSelectedIndices();
		if (selIndices.Length == 0)
		{
			// Auto-select the clip under the playhead
			var flat = FindClipAtTime(time);
			if (flat < 0) return;
			selIndices = new[] { flat };
		}

		// Sort descending so splits don't shift flat indices of remaining selections
		var sorted = selIndices.OrderByDescending(i => i).ToList();
		bool anySplit = false;
		foreach (int flatIdx in sorted)
		{
			int idx = flatIdx;
			for (int t = 0; t < _tracks.Count; t++)
			{
				if (idx < _tracks[t].Clips.Count)
				{
					var clip = _tracks[t].Clips[idx];
					if (time > clip.Start && time < clip.End)
					{
						var nextHalf = clip.Clone();
						nextHalf.Start = time;
						clip.End = time;
						_tracks[t].Clips.Insert(idx + 1, nextHalf);
						anySplit = true;
					}
					break;
				}
				idx -= _tracks[t].Clips.Count;
			}
		}
		if (anySplit)
		{
			UpdateTracks();
			SwitchToState(ViewState.Edit);
			ToastManager.Show(this, "CLIP SPLIT", Color.FromHtml("#D0570C"));
		}
	}

	// Remove all selected clips from their tracks.
	// Empty tracks are cleaned up by UpdateTracks.
	private void DeleteSelected()
	{
		SnapshotState();
		int[] selIndices = _timeline.GetSelectedIndices();
		if (selIndices.Length == 0) return;

		var toDelete = new List<(int track, int clip)>();
		foreach (int flatIdx in selIndices)
		{
			int idx = flatIdx;
			for (int t = 0; t < _tracks.Count; t++)
			{
				if (idx < _tracks[t].Clips.Count)
				{
					toDelete.Add((t, idx));
					break;
				}
				idx -= _tracks[t].Clips.Count;
			}
		}
		toDelete = toDelete.OrderByDescending(x => x.track).ThenByDescending(x => x.clip).ToList();
		foreach (var (t, ci) in toDelete)
			_tracks[t].Clips.RemoveAt(ci);

		_selTrackIdx = -1;
		_selClipIdx = -1;
		_overlay.SelectLayer(-1, -1, null);
		_outputPreview.SelectDisplayLayer(null);
		UpdateTracks();
		ToastManager.Show(this, "CLIP DELETED", Color.FromHtml("#f78166"));
	}

	private void CopySelected()
	{
		int[] selIndices = _timeline.GetSelectedIndices();
		if (selIndices.Length == 0) return;

		_clipboard = new List<TrackClipData>();
		foreach (int flatIdx in selIndices)
		{
			int idx = flatIdx;
			for (int t = 0; t < _tracks.Count; t++)
			{
				if (idx < _tracks[t].Clips.Count)
				{
					_clipboard.Add(_tracks[t].Clips[idx].Clone());
					break;
				}
				idx -= _tracks[t].Clips.Count;
			}
		}
		SetStatus($"Copied {_clipboard.Count} clip(s)");
		ToastManager.Show(this, "COPIED", Color.FromHtml("#D0570C"));
	}

	private void CutSelected()
	{
		CopySelected();
		DeleteSelected();
		if (_clipboard?.Count > 0)
		{
			SetStatus($"Cut {_clipboard.Count} clip(s)");
			ToastManager.Show(this, "CUT", Color.FromHtml("#f78166"));
		}
	}

	private void DuplicateSelectedClips()
	{
		CopySelected();
		if (_clipboard == null || _clipboard.Count == 0) return;
		// Offset slightly so duplicate is visible next to original
		double offset = _clipboard.Max(c => c.End - c.Start) * 0.1;
		PasteWithOffset(offset);
		SetStatus($"Duplicated {_clipboard.Count} clip(s)");
		ToastManager.Show(this, "DUPLICATED", Color.FromHtml("#D0570C"));
	}

	private void Paste()
	{
		PasteWithOffset(0);
	}

	private void PasteWithOffset(double extraOffset)
	{
		if (_clipboard == null || _clipboard.Count == 0) return;
		SnapshotState();
		double pastePos = _timeline.SelectionPos;
		double offset = extraOffset;

		foreach (var src in _clipboard)
		{
			var clip = src.Clone();
			double dur = clip.End - clip.Start;
			clip.Start = pastePos + offset;
			clip.End = clip.Start + dur;

			var targetTrack = src.ClipType == ClipType.Audio
				? _tracks.FirstOrDefault(t => t.Type == TrackType.Audio)
				: _tracks.FirstOrDefault(t => t.Type == TrackType.Video);
			targetTrack ??= _tracks.FirstOrDefault();
			if (targetTrack == null) continue;
			targetTrack.Clips.Add(clip);
			offset += dur + 0.5 + extraOffset;
		}
		UpdateTracks();
		SetStatus($"Pasted {_clipboard.Count} clip(s)");
		ToastManager.Show(this, "PASTED", Color.FromHtml("#D0570C"));
	}

	// Find and remove all timeline clips that reference the given media asset:
	//   - file-based assets match by FilePath
	//   - transcribed caption entries match by CaptionText content
	private void RemoveAssetFromTimeline(MediaAsset asset)
	{
		foreach (var track in _tracks)
		{
			if (!string.IsNullOrEmpty(asset.Path))
			{
				track.Clips.RemoveAll(c => c.FilePath == asset.Path);
			}
			else if (asset.Type == AssetType.Text && !string.IsNullOrEmpty(asset.CaptionText))
			{
				track.Clips.RemoveAll(c => c.ClipType == ClipType.Text && c.Text == asset.CaptionText);
			}
		}
		// If the deleted asset is the main source video, also remove its audio track clips
		if (asset.Type == AssetType.Video && _videoPath == asset.Path)
		{
			_videoPath = null;
			foreach (var track in _tracks)
			{
				if (track.Name == "Source Audio")
					track.Clips.Clear();
			}
		}
		// Clean up empty tracks (including unused layout tracks)
		_tracks.RemoveAll(t => t.Clips.Count == 0);
		UpdateTracks();
	}

	// Rebuild flat clip list from _tracks, sort tracks (video first, then audio),
	// remove empty tracks, reassign selection, and push everything to the
	// TimelineControl, VideoOverlay, OutputPreview, and Inspector.
	private void UpdateTracks()
	{
		TrackClipData? selClip = null;
		if (_selTrackIdx >= 0 && _selTrackIdx < _tracks.Count && _selClipIdx >= 0 && _selClipIdx < _tracks[_selTrackIdx].Clips.Count)
			selClip = _tracks[_selTrackIdx].Clips[_selClipIdx];

		// Tracks keep their creation/insertion order — no automatic resorting

		// Keep user-created tracks even when empty
		// _tracks.RemoveAll(t => t.Clips.Count == 0 && t.Name is not ("Camera" or "Source Video" or "Source Audio"));

		_selTrackIdx = -1;
		_selClipIdx = -1;
		if (selClip != null)
		{
			for (int t = 0; t < _tracks.Count; t++)
			{
				int ci = _tracks[t].Clips.IndexOf(selClip);
				if (ci >= 0) { _selTrackIdx = t; _selClipIdx = ci; break; }
			}
		}

		var flatClips = new List<ClipData>();
		int flatSel = -1;
		int fi = 0;
		for (int t = 0; t < _tracks.Count; t++)
		{
			string name = _tracks[t].Name;
			for (int ci = 0; ci < _tracks[t].Clips.Count; ci++)
			{
				var c = _tracks[t].Clips[ci];
				string displayName = c.ClipType switch
				{
					ClipType.Text => c.Text?.Trim().Length > 0 ? c.Text.Trim().Substring(0, Math.Min(c.Text.Trim().Length, 20)) : "Text",
					ClipType.Image or ClipType.Gif => System.IO.Path.GetFileNameWithoutExtension(c.FilePath),
					_ => System.IO.Path.GetFileNameWithoutExtension(c.FilePath) ?? "Clip",
				};
				if (string.IsNullOrEmpty(displayName)) displayName = "Clip";
				var kfTimes = new List<double>();
				foreach (var prop in new[] { c.PosX, c.PosY, c.Scale, c.Opacity, c.Volume, c.FontSizeAnim })
					if (prop.IsAnimated)
						foreach (var k in prop.Keyframes)
							kfTimes.Add(c.Start + k.Time);
				// Include text keyframe times
				foreach (var tk in c.TextKeyframes)
					kfTimes.Add(c.Start + tk.Time);
				if (kfTimes.Count > 0)
				{
					kfTimes = kfTimes.OrderBy(t => t).Distinct().ToList();
					kfTimes.RemoveAll(t => t < c.Start || t > c.End);
				}
				flatClips.Add(new ClipData((float)c.Start, (float)c.End, c.WaveformPeaks, t, name, c.ClipType, displayName, kfTimes.Count > 0 ? kfTimes : null));
				if (selClip != null && c == selClip) flatSel = fi;
				fi++;
			}
		}
		_timeline.UpdateProjectDuration(_tracks, _videoDuration);
		_timeline.SetClips(flatClips, flatSel);
		_overlay.SyncLayers(_tracks);
		_outputPreview.SyncDisplayLayers(_tracks);
		RebuildInspector();
	}

	// Add a text clip: scans existing Video tracks for a 5s gap; if none found, creates a new track.
	private async void OnAddTextClip()
	{
		double targetStart = _timeline.SelectionPos;
		double targetEnd = targetStart + 5.0;

		SnapshotState();

		TrackData? targetTrack = null;
		foreach (var track in _tracks.Where(t => t.Type == TrackType.Video))
		{
			bool isBlocked = track.Clips.Any(c => targetStart < c.End && targetEnd > c.Start);
			if (!isBlocked)
			{
				targetTrack = track;
				break;
			}
		}

		if (targetTrack == null)
		{
			int n = 1 + _tracks.Count(t => t.Name.Contains("Text") || t.Name.Contains("Video"));
			targetTrack = new TrackData { Name = $"Text Layers {n}", Type = TrackType.Video };
			_tracks.Add(targetTrack);
		}

		var newTextClip = new TrackClipData
		{
			ClipType = ClipType.Text,
			Text = "New Text",
			Start = targetStart,
			End = targetEnd,
			FontSize = 48,
			Position = new Vector2(0.3f, 0.3f),
			Size = new Vector2(0.4f, 0.2f),
		};

		targetTrack.Clips.Add(newTextClip);

		UpdateTracks();
		SetStatus($"Added text layer on \"{targetTrack.Name}\"", Color.FromHtml("#D0570C"));

		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

		int flatIdx = GetFlatIndex(targetTrack, targetTrack.Clips.Count - 1);
		OnClipSelected(flatIdx);

		_overlay.QueueRedraw();
	}

	// Compute flat index for a clip within a specific track (used to communicate
	// selections between track-aware code and the flat-indexed TimelineControl)
	private int GetFlatIndex(TrackData targetTrack, int clipIdxInTrack)
	{
		int count = 0;
		foreach (var track in _tracks)
		{
			if (track == targetTrack) return count + clipIdxInTrack;
			count += track.Clips.Count;
		}
		return -1;
	}

	// Find the flat index of the clip containing the given time position
	private int FindClipAtTime(double time)
	{
		int flat = 0;
		foreach (var track in _tracks)
		{
			foreach (var clip in track.Clips)
			{
				if (time > clip.Start && time < clip.End)
					return flat;
				flat++;
			}
		}
		return -1;
	}

	// Popup window listing available fonts with preview (rendered in their own
	// typeface). Download + select sets the clip.FontPath and refreshes the overlay.
	private void OpenFontBrowser(TrackClipData clip, Button uiTriggerBtn)
	{
		var dialog = new Window
		{
			Title = "FONT LIBRARY",
			Size = new Vector2I(450, 550),
			Exclusive = true,
			Transient = true,
			InitialPosition = Window.WindowInitialPosition.CenterPrimaryScreen,
		};
		var bg = new PanelContainer();
		bg.SetAnchorsPreset(LayoutPreset.FullRect);
		dialog.AddChild(bg);

		var vbox = new VBoxContainer();
		vbox.AddThemeConstantOverride("margin_left", 15);
		vbox.AddThemeConstantOverride("margin_right", 15);
		vbox.AddThemeConstantOverride("margin_top", 15);
		bg.AddChild(vbox);

		vbox.AddChild(new Label { Text = "STREAMER FAVORITES", HorizontalAlignment = HorizontalAlignment.Center });

		var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
		var grid = new GridContainer { Columns = 1, SizeFlagsHorizontal = SizeFlags.ExpandFill };
		scroll.AddChild(grid);
		vbox.AddChild(scroll);

		foreach (var kv in _fontManager.AvailableFonts)
		{
			var card = new PanelContainer { CustomMinimumSize = new Vector2(0, 70) };
			grid.AddChild(card);

			var h = new HBoxContainer();
			card.AddChild(h);

			string fontName = kv.Key;

			var nameLabel = new Label
			{
				Text = fontName,
				SizeFlagsHorizontal = SizeFlags.ExpandFill,
				HorizontalAlignment = HorizontalAlignment.Center,
			};

			if (_fontManager.IsFontInstalled(fontName))
			{
				try
				{
					var fontFile = _fontManager.LoadFont(fontName);
					if (fontFile != null)
					{
						nameLabel.AddThemeFontOverride("font", fontFile);
						nameLabel.AddThemeFontSizeOverride("font_size", 24);
					}
				}
				catch { }
			}

			h.AddChild(nameLabel);

			bool installed = _fontManager.IsFontInstalled(fontName);
			var dlBtn = new Button
			{
				Text = installed ? "SELECT" : "PREVIEW & USE",
				CustomMinimumSize = new Vector2(100, 0),
				Disabled = false,
			};

			string capturedName = fontName;
			dlBtn.Pressed += async () =>
			{
				dlBtn.Text = "Loading...";
				string? path = await _fontManager.DownloadFont(capturedName);
				if (path != null)
				{
					clip.FontPath = path;
					uiTriggerBtn.Text = capturedName;
					_overlay.RefreshActiveLayer();
					_outputPreview.RefreshDisplayLayer();
					dialog.QueueFree();
				}
				else
				{
					dlBtn.Text = "RETRY";
				}
			};
			h.AddChild(dlBtn);
		}

		var closeBtn = new Button { Text = "Close", SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
		closeBtn.Pressed += () => dialog.BounceOutThenFree();
		vbox.AddChild(closeBtn);

		AddChild(dialog);
		dialog.Popup();
		dialog.CloseRequested += () => dialog.BounceOutThenFree();
		dialog.BounceIn();
	}

	// Popup window listing available sound effects with preview and add-to-timeline
	private void OpenSFXBrowser()
	{
		var dialog = new Window
		{
			Title = "SOUND EFFECTS LIBRARY",
			Size = new Vector2I(500, 600),
			InitialPosition = Window.WindowInitialPosition.CenterPrimaryScreen,
			Transient = true,
			Exclusive = true,
		};
		var bg = new PanelContainer();
		bg.SetAnchorsPreset(LayoutPreset.FullRect);
		dialog.AddChild(bg);

		var vbox = new VBoxContainer();
		vbox.AddThemeConstantOverride("margin_left", 15);
		vbox.AddThemeConstantOverride("margin_right", 15);
		vbox.AddThemeConstantOverride("margin_top", 15);
		bg.AddChild(vbox);

		var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
		var grid = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		scroll.AddChild(grid);
		vbox.AddChild(scroll);

		foreach (var sfx in _sfxManager.AvailableSFX)
		{
			var row = new PanelContainer();
			grid.AddChild(row);
			var h = new HBoxContainer();
			row.AddChild(h);

			h.AddChild(new Label { Text = sfx.Key, SizeFlagsHorizontal = SizeFlags.ExpandFill });

			var previewBtn = new Button { Text = "Preview" };
			string capturedSfxName = sfx.Key;
			previewBtn.Pressed += async () =>
			{
				string? path = await _sfxManager.DownloadSFX(capturedSfxName);
				if (path != null)
				{
					_sfxPreviewPlayer.Stream = AudioStreamMP3.LoadFromBuffer(Godot.FileAccess.GetFileAsBytes(path));
					_sfxPreviewPlayer.Play();
				}
			};
			h.AddChild(previewBtn);

			var useBtn = new Button { Text = "ADD TO TIMELINE", Modulate = Color.FromHtml("#D0570C") };
			useBtn.Pressed += async () =>
			{
				string? path = await _sfxManager.DownloadSFX(capturedSfxName);
				if (path != null)
				{
					AddAudioClipToTimeline(capturedSfxName, path);
					_projectBin.Add(new MediaAsset(capturedSfxName, path, AssetType.Audio));
					RefreshBinUI();
					dialog.QueueFree();
				}
			};
			h.AddChild(useBtn);
		}

		var closeBtn = new Button { Text = "Close", SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
		closeBtn.Pressed += () => dialog.BounceOutThenFree();
		vbox.AddChild(closeBtn);

		AddChild(dialog);
		dialog.Popup();
		dialog.CloseRequested += () => dialog.BounceOutThenFree();
		dialog.BounceIn();
	}

	// Create an audio clip on a new SFX track, detecting MP3 duration if possible.
	private void AddAudioClipToTimeline(string name, string path)
	{
		SnapshotState();
		int n = 1 + _tracks.Count(t => t.Name.StartsWith("SFX"));
		var sfxTrack = new TrackData { Name = $"SFX {n}", Type = TrackType.Audio };
		_tracks.Add(sfxTrack);

		var newClip = new TrackClipData
		{
			ClipType = ClipType.Audio,
			Text = name,
			FilePath = path,
			Start = _timeline.SelectionPos,
			End = _timeline.SelectionPos + 2.0,
			Color = Color.FromHtml("#f78166"),
			Volume = new AnimatableProperty { StaticValue = 1.0f },
		};

		try
		{
			string lower = path.ToLowerInvariant();
			AudioStream? stream = null;
			if (lower.EndsWith(".mp3"))
			{
				byte[] data = Godot.FileAccess.GetFileAsBytes(path);
				if (data != null && data.Length > 0)
					stream = AudioStreamMP3.LoadFromBuffer(data);
			}
			else
			{
				stream = ResourceLoader.Load<AudioStream>(path);
			}
			if (stream != null)
			{
				double dur = stream.GetLength();
				if (dur > 0)
					newClip.End = newClip.Start + dur;
			}
		}
		catch (Exception e)
		{
			GD.PrintErr($"[MainWindow] Could not detect audio length: {e.Message}");
		}

		sfxTrack.Clips.Add(newClip);
		UpdateTracks();
		SetStatus($"Added SFX: {name}", Color.FromHtml("#D0570C"));
	}

	// Popup window: scans Assets/ and user://stickers/ for image files, displays
	// as a thumbnail grid. Clicking adds the image/GIF to the timeline on the
	// "Stickers" track. Import button lets users add new files.
	private void OpenStickerBrowser()
	{
		var dialog = new Window
		{
			Title = "EMOTES & STICKERS",
			Size = new Vector2I(550, 650),
			InitialPosition = Window.WindowInitialPosition.CenterPrimaryScreen,
			Transient = true,
			Exclusive = true,
		};
		// Ensure cleanup
		dialog.CloseRequested += () => { if (IsInstanceValid(dialog)) dialog.BounceOutThenFree(); };

		var bg = new PanelContainer();
		bg.SetAnchorsPreset(LayoutPreset.FullRect);
		dialog.AddChild(bg);

		var vbox = new VBoxContainer();
		vbox.AddThemeConstantOverride("margin_left", 15);
		vbox.AddThemeConstantOverride("margin_right", 15);
		vbox.AddThemeConstantOverride("margin_top", 15);
		vbox.AddThemeConstantOverride("separation", 10);
		bg.AddChild(vbox);

		vbox.AddChild(new Label { Text = "EMOTES & STICKERS", Modulate = Color.FromHtml("#D0570C") });

		// Scan for image files
		var paths = new List<string>();
		string[] dirs = {
			ProjectSettings.GlobalizePath("res://Assets/"),
			ProjectSettings.GlobalizePath("user://stickers/"),
		};
		foreach (var d in dirs)
		{
			if (!DirAccess.DirExistsAbsolute(d)) continue;
			var dir = DirAccess.Open(d);
			if (dir == null) continue;
			dir.ListDirBegin();
			while (true)
			{
				string? fn = dir.GetNext();
				if (string.IsNullOrEmpty(fn)) break;
				if (fn == "." || fn == "..") continue;
				string ext = System.IO.Path.GetExtension(fn).ToLowerInvariant();
				if (ext is ".png" or ".gif" or ".jpg" or ".jpeg" or ".webp")
					paths.Add(System.IO.Path.Combine(d, fn));
			}
			dir.ListDirEnd();
		}

		// Import button
		var importBtn = new Button { Text = "Import Image/GIF", SizeFlagsHorizontal = SizeFlags.ExpandFill };
		vbox.AddChild(importBtn);

		var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
		var grid = new GridContainer { Columns = 3, SizeFlagsHorizontal = SizeFlags.ExpandFill };
		grid.AddThemeConstantOverride("h_separation", 8);
		grid.AddThemeConstantOverride("v_separation", 8);
		scroll.AddChild(grid);
		vbox.AddChild(scroll);

		// Import button wiring
		importBtn.Pressed += () =>
		{
			var fd = new FileDialog
			{
				Title = "Import Sticker",
				FileMode = FileDialog.FileModeEnum.OpenFile,
				Access = FileDialog.AccessEnum.Filesystem,
				UseNativeDialog = true,
				CurrentDir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
			};
			fd.AddFilter("*.png,*.gif,*.jpg,*.jpeg,*.webp ; Images");
			var fdCleanup = () => { if (IsInstanceValid(fd)) fd.QueueFree(); };
			fd.CloseRequested += fdCleanup;
			dialog.AddChild(fd);
			fd.FileSelected += (filePath) =>
			{
				string stickerDir = ProjectSettings.GlobalizePath("user://stickers/");
				if (!DirAccess.DirExistsAbsolute(stickerDir))
					DirAccess.MakeDirAbsolute(stickerDir);
				string dest = System.IO.Path.Combine(stickerDir, System.IO.Path.GetFileName(filePath));
				try { DirAccess.CopyAbsolute(filePath, dest); } catch { }
				dialog.BounceOutThenFree();
				fdCleanup();
				OpenStickerBrowser();
			};
			fd.PopupCentered();
		};

		// Populate grid with sticker cards
		foreach (var path in paths)
		{
			// Use a Button to get reliable click handling
			var card = new Button
			{
				CustomMinimumSize = new Vector2(0, 110),
				SizeFlagsHorizontal = SizeFlags.ExpandFill,
				Flat = true,
				TooltipText = System.IO.Path.GetFileName(path),
			};
			grid.AddChild(card);

			var cardV = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
			card.AddChild(cardV);

			// Try to load thumbnail
			try
			{
				var img = new Image();
				if (img.Load(path) == Error.Ok && !img.IsEmpty())
				{
					img.Resize(64, 64, Image.Interpolation.Lanczos);
					var tex = ImageTexture.CreateFromImage(img);
					var tr = new TextureRect
					{
						Texture = tex,
						ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
						StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
						CustomMinimumSize = new Vector2(64, 64),
						SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
					};
					cardV.AddChild(tr);
				}
			}
			catch (Exception e)
			{
				GD.PrintErr($"Sticker thumbnail failed: {path} - {e.Message}");
			}

			cardV.AddChild(new Label
			{
				Text = System.IO.Path.GetFileNameWithoutExtension(path),
				HorizontalAlignment = HorizontalAlignment.Center,
				AutowrapMode = TextServer.AutowrapMode.Word,
				CustomMinimumSize = new Vector2(0, 16),
				Modulate = new Color(0.8f, 0.8f, 0.8f),
			});

			var captured = path;
			card.Pressed += () =>
			{
				if (IsInstanceValid(dialog)) dialog.BounceOutThenFree();
				AddImageClipToTimeline(captured);
			};

		}

		if (paths.Count == 0)
		{
			grid.AddChild(new Label { Text = "No stickers found.\nClick 'Import' to add one.", Modulate = new Color(0.5f, 0.5f, 0.5f) });
		}

		// Close button
		var closeBtn = new Button { Text = "Close", SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
		closeBtn.Pressed += () => { if (IsInstanceValid(dialog)) dialog.BounceOutThenFree(); };
		vbox.AddChild(closeBtn);

		GetTree().Root.AddChild(dialog);
		dialog.Popup();
		dialog.BounceIn();
	}

	// Insert an image/GIF clip on the "Stickers" track at given position
	private void AddImageClipToTimeline(string path, double? position = null)
	{
		SnapshotState();
		double pos = position ?? _timeline?.SelectionPos ?? 0;
		var track = _tracks.FirstOrDefault(t => t.Name == "Stickers");
		if (track == null)
		{
			track = new TrackData { Name = "Stickers", Type = TrackType.Video };
			_tracks.Add(track);
		}

		string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
		var clip = new TrackClipData
		{
			ClipType = ext == ".gif" ? ClipType.Gif : ClipType.Image,
			Start = pos,
			End = pos + 5.0,
			FilePath = path,
			Position = new Vector2(0.5f, 0.5f),
			Size = new Vector2(0.3f, 0.3f),
		};
		track.Clips.Add(clip);
		UpdateTracks();
		SetStatus($"Added sticker: {System.IO.Path.GetFileName(path)}", Color.FromHtml("#D0570C"));
	}

	// Call backend reframe (face detection) to suggest crop rectangle for Content region
	private async void OnAutoFrame()
	{
		if (string.IsNullOrEmpty(_videoPath)) { SetStatus("Load a video first", Colors.Red); return; }

		SetStatus("Detecting faces...", Colors.Yellow);

		try
		{
			double dur = _videoDuration > 0 ? _videoDuration : 30;
			var (cropX, cropY, cropW, cropH) = _backendService.Reframe(_videoPath!, 0, dur, "face");
			// Convert to normalized coordinates
			var vi = await _backendService.GetVideoInfo(_videoPath!);
			float x = vi.Width > 0 ? (float)cropX / vi.Width : 0;
			float y = vi.Height > 0 ? (float)cropY / vi.Height : 0;
			float w = vi.Width > 0 ? (float)cropW / vi.Width : 1;
			float h = vi.Height > 0 ? (float)cropH / vi.Height : 1;

			var region = _overlay.GetRegion("Content");
			if (region != null)
				region.Rect = new Rect2(x, y, w, h);

			_overlay.EmitSignal("LayoutChanged", "Content");
			SetStatus("Auto-frame applied", Color.FromHtml("#D0570C"));
		}
		catch (Exception e)
		{
			SetStatus($"Auto-frame failed: {e.Message}", Colors.Red);
		}
	}

	// Transcribe video audio via backend, create per-segment text clips on a
	// "Captions" track, add entries to media bin, and switch to Edit mode.
	private async void OnGenerateCaptions()
	{
		if (string.IsNullOrEmpty(_videoPath)) { SetStatus("Load a video first", Colors.Red); return; }

		SnapshotState();
		SetStatus("Transcribing audio...", Colors.Yellow);

		try
		{
			var transcript = await _backendService.TranscribeAsync(_videoPath!, ExportCaptionLanguage);

			if (transcript.Segments.Count == 0) { SetStatus("No speech detected", Colors.Orange); return; }

			// Collect segments into a list so we can iterate twice
			var segList = new List<(double start, double end, string text)>();
			foreach (var seg in transcript.Segments)
			{
				segList.Add((seg.Start, seg.End, seg.Text));
			}

			var textTrack = _tracks.FirstOrDefault(t => t.Name == "Captions");
			if (textTrack == null)
			{
				textTrack = new TrackData { Name = "Captions", Type = TrackType.Video };
				_tracks.Add(textTrack);
			}

			foreach (var (start, end, text) in segList)
			{
				var clip = new TrackClipData
				{
					ClipType = ClipType.Text,
					Text = text.Trim(),
					Start = start,
					End = end,
					FontSize = 48,
					FontColor = Colors.White,
					OutlineColor = Colors.Black,
					OutlineWidth = 4,
					Position = new Vector2(0.5f, 0.85f),
					Size = new Vector2(0.8f, 0.12f),
				};
				textTrack.Clips.Add(clip);
			}

			UpdateTracks();

			// Add caption entries to media bin with timestamps and full text
			int segIdx = 0;
			foreach (var (start, end, text) in segList)
			{
				string trimmed = text.Trim();
				string preview = trimmed.Length > 40 ? trimmed[..40] + "..." : trimmed;
				_projectBin.Add(new MediaAsset($"C{++segIdx}: {preview}", "", AssetType.Text)
				{
					StartTime = start,
					EndTime = end,
					CaptionText = trimmed,
				});
			}
			RefreshBinUI();

			SetStatus($"Generated {segList.Count} captions", Color.FromHtml("#D0570C"));

			if (_currentState == ViewState.Layout)
				SwitchToState(ViewState.Edit);
		}
		catch (Exception e)
		{
			SetStatus($"Caption failed: {e.Message}", Colors.Red);
		}
	}

	// Serialize all tracks/clips as JSON layer descriptors, call backend
	// export to produce final video via FFmpeg.
	private async void OnExportPressed()
	{
		if (_videoPath == null) return;
		GD.Print($"[Export] ===== START EXPORT =====");
		GD.Print($"[Export] Path: {_videoPath}");
		GD.Print($"[Export] Tracks: {_tracks.Count}, clips per track: {string.Join(", ", _tracks.Select(t => $"{t.Name}={t.Clips.Count}"))}");
		GD.Print($"[Export] Settings: aspect={ExportAspectRatio}, normalize={ExportNormalizeAudio}");
		GD.Print($"[Export] LayoutMode: {_outputPreview.LayoutMode}");
		var sw = System.Diagnostics.Stopwatch.StartNew();
		var (outW, outH) = AppConfig.AspectRatios.GetValueOrDefault(ExportAspectRatio, (1080, 1920));
		try
		{
			// Compute full timeline range: min start to max end across all clips
			double rangeStart = double.MaxValue;
			double rangeEnd = 0;
			var exportLayers = new List<ExporterLayer>();
			foreach (var track in _tracks)
			{
				GD.Print($"[Export] Track: {track.Name}, Type={track.Type}, Clips={track.Clips.Count}");
				foreach (var clip in track.Clips)
				{
					GD.Print($"[Export]   Clip: type={clip.ClipType}, start={clip.Start:F2}, end={clip.End:F2}, text='{clip.Text}', font_size={clip.FontSize}, pos=({clip.Position.X:F3},{clip.Position.Y:F3}), size=({clip.Size.X:F3},{clip.Size.Y:F3}), font_path={clip.FontPath}");
					if (clip.Start < rangeStart) rangeStart = clip.Start;
					if (clip.End > rangeEnd) rangeEnd = clip.End;

					if (clip.ClipType == ClipType.Text)
					{
						string? fontGlobalPath = null;
						if (!string.IsNullOrEmpty(clip.FontPath))
							fontGlobalPath = ProjectSettings.GlobalizePath(clip.FontPath);

						// Scale font size from 720p reference to output resolution
						float baseFontSize = clip.FontSizeAnim.IsAnimated
							? clip.FontSizeAnim.GetValueAt(0)
							: clip.FontSize;
						float exportFontSize = (baseFontSize / 720f) * outH;
						int exportOutlineWidth = (int)Math.Max(0, (clip.OutlineWidth / 720f) * outH);

						exportLayers.Add(new ExporterLayer
						{
							Type = "text",
							Text = clip.Text,
							X = clip.Position.X,
							Y = clip.Position.Y,
							W = clip.Size.X,
							H = clip.Size.Y,
							FontSize = (int)Math.Max(1, exportFontSize),
							FontPath = fontGlobalPath,
							FontColor = clip.FontColor,
							OutlineColor = clip.OutlineColor,
							OutlineWidth = exportOutlineWidth,
							Start = clip.Start,
							End = clip.End,
							Volume = 1.0,
							Rotation = clip.Rotation.StaticValue,
							KeyframesX = clip.PosX.IsAnimated ? clip.PosX.Keyframes : null,
							KeyframesY = clip.PosY.IsAnimated ? clip.PosY.Keyframes : null,
							KeyframesScale = clip.Scale.IsAnimated ? clip.Scale.Keyframes : null,
							KeyframesOpacity = clip.Opacity.IsAnimated ? clip.Opacity.Keyframes : null,
							KeyframesFontSize = clip.FontSizeAnim.IsAnimated ? clip.FontSizeAnim.Keyframes : null,
							KeyframesRotation = clip.Rotation.IsAnimated ? clip.Rotation.Keyframes : null,
							KeyframesText = clip.TextKeyframes.Count > 0 ? clip.TextKeyframes : null,
						});
					}
					else if (clip.ClipType is ClipType.Image or ClipType.Gif)
					{
						string? imgGlobalPath = null;
						if (!string.IsNullOrEmpty(clip.FilePath))
							imgGlobalPath = ProjectSettings.GlobalizePath(clip.FilePath);

						string layerType = clip.ClipType == ClipType.Gif ? "gif" : "image";
						exportLayers.Add(new ExporterLayer
						{
							Type = layerType,
							Path = imgGlobalPath,
							X = clip.Position.X,
							Y = clip.Position.Y,
							W = clip.Size.X,
							H = clip.Size.Y,
							Start = clip.Start,
							End = clip.End,
							Volume = 1.0,
							Rotation = clip.Rotation.StaticValue,
							KeyframesX = clip.PosX.IsAnimated ? clip.PosX.Keyframes : null,
							KeyframesY = clip.PosY.IsAnimated ? clip.PosY.Keyframes : null,
							KeyframesScale = clip.Scale.IsAnimated ? clip.Scale.Keyframes : null,
							KeyframesOpacity = clip.Opacity.IsAnimated ? clip.Opacity.Keyframes : null,
							KeyframesRotation = clip.Rotation.IsAnimated ? clip.Rotation.Keyframes : null,
						});
					}
					else if (clip.ClipType == ClipType.Audio && track.Name.StartsWith("SFX"))
					{
						string? audioGlobalPath = null;
						if (!string.IsNullOrEmpty(clip.FilePath))
							audioGlobalPath = ProjectSettings.GlobalizePath(clip.FilePath);

						exportLayers.Add(new ExporterLayer
						{
							Type = "audio",
							Path = audioGlobalPath,
							Start = clip.Start,
							End = clip.End,
							Volume = clip.Volume.StaticValue,
						});
					}
				}
			}

			if (rangeStart == double.MaxValue) rangeStart = 0;
			double exportDuration = rangeEnd - rangeStart;
			GD.Print($"[Export] Range: [{rangeStart:F2}, {rangeEnd:F2}] ({exportDuration:F2}s), layers={exportLayers.Count}");

			// Gather layout regions from the VideoOverlay
			float[]? gameCrop = null, camCrop = null, camTarget = null;
			float[]? uiCrop = null, uiTarget = null;
			int layoutMode = _outputPreview.LayoutMode;
			bool isNormal16x9 = ExportAspectRatio == "16:9";
			GD.Print($"[Export] Layout mode: {layoutMode}, isNormal16x9={isNormal16x9}");
			if (!isNormal16x9 || layoutMode > 0)
			{
				var contentRect = _overlay.GetRegion("Content")?.Rect;
				if (contentRect.HasValue)
				{
					gameCrop = new[] { contentRect.Value.Position.X, contentRect.Value.Position.Y, contentRect.Value.Size.X, contentRect.Value.Size.Y };
					GD.Print($"[Export] Content region: pos=({contentRect.Value.Position.X:F3},{contentRect.Value.Position.Y:F3}), size=({contentRect.Value.Size.X:F3},{contentRect.Value.Size.Y:F3})");
				}
				var cameraRect = _overlay.GetRegion("Camera")?.Rect;
				if (cameraRect.HasValue)
				{
					camCrop = new[] { cameraRect.Value.Position.X, cameraRect.Value.Position.Y, cameraRect.Value.Size.X, cameraRect.Value.Size.Y };
					GD.Print($"[Export] Camera region: pos=({cameraRect.Value.Position.X:F3},{cameraRect.Value.Position.Y:F3}), size=({cameraRect.Value.Size.X:F3},{cameraRect.Value.Size.Y:F3})");
				}
				camTarget = _outputPreview.GetCameraTarget();
				GD.Print($"[Export] Camera target: [{string.Join(", ", camTarget.Select(f => f.ToString("F3")))}]");
				if (layoutMode == 2)
				{
					var uiRect = _overlay.GetRegion("UI")?.Rect;
					if (uiRect.HasValue)
					{
						uiCrop = new[] { uiRect.Value.Position.X, uiRect.Value.Position.Y, uiRect.Value.Size.X, uiRect.Value.Size.Y };
						GD.Print($"[Export] UI region: pos=({uiRect.Value.Position.X:F3},{uiRect.Value.Position.Y:F3}), size=({uiRect.Value.Size.X:F3},{uiRect.Value.Size.Y:F3})");
					}
					uiTarget = _outputPreview.GetUiTarget();
					GD.Print($"[Export] UI target: [{string.Join(", ", uiTarget.Select(f => f.ToString("F3")))}]");
				}
			}

			// Build export directory
			string outputDir = ExportOutputDir;
			if (string.IsNullOrEmpty(outputDir))
				outputDir = AppConfig.ExportOutputDir;
			if (string.IsNullOrEmpty(outputDir))
				outputDir = AppConfig.OutputDir;
			System.IO.Directory.CreateDirectory(outputDir);

			// Generate ASS captions if transcript is available
			string? assPath = null;

			string outputPath = System.IO.Path.Combine(outputDir, "clip_001.mp4");
			_progressBar.Modulate = new Color(0.2f, 0.5f, 0.8f);
			var progress = new Progress<double>(pct =>
			{
				Callable.From(() =>
				{
					int pctInt = (int)(pct * 100);
					_progressBar.Visible = pct < 1.0;
					_progressBar.Value = pctInt;
					if (pct < 1.0)
					{
						SetStatus($"Exporting... {pctInt}%", Color.FromHtml("#D0570C"));
					}
				}).CallDeferred();
			});

			string encoderLabel = Exporter.EncoderLabel;
			SetStatus($"Exporting with {encoderLabel}...", Color.FromHtml("#D0570C"));
			GD.Print($"[Export] Running C# Exporter: encoder={encoderLabel}, path={_videoPath}, out={outW}x{outH}, layoutMode={layoutMode}");
			await Exporter.ExportAsync(
				inputPath: _videoPath,
				outputPath: outputPath,
				start: rangeStart,
				duration: exportDuration,
				assPath: assPath,
				outWidth: outW,
				outHeight: outH,
				normalizeAudio: ExportNormalizeAudio,
				blurIntensity: _outputPreview.BlurBg ? 2.5 : 0,
				gameCrop: gameCrop,
				camCrop: camCrop,
				camTarget: camTarget,
				layoutMode: layoutMode,
				uiCrop: uiCrop,
				uiTarget: uiTarget,
				layers: exportLayers,
				progress: progress
			);

			sw.Stop();
			GD.Print($"[Export] Success: exported in {sw.Elapsed.TotalSeconds:F1}s");
			_progressBar.Modulate = new Color(0.25f, 0.7f, 0.35f);
			_progressBar.Value = 100;
			SetStatus("✓ Export Complete", Color.FromHtml("#3fb950"));
			_ = HideExportUI(3.0f);
		}
		catch (Exception e)
		{
			sw.Stop();
			GD.PrintErr($"[Export] FAILED after {sw.Elapsed.TotalSeconds:F1}s: {e.Message}\n{e.StackTrace}");
			_progressBar.Modulate = new Color(0.8f, 0.25f, 0.25f);
			_statusLabel.Text = "✗ Export failed: " + e.Message;
			_statusLabel.Modulate = new Color(0.95f, 0.35f, 0.35f);
		}
	}

	private async Task HideExportUI(float delay)
	{
		await Task.Delay((int)(delay * 1000));
		_progressBar.Visible = false;
		_progressBar.Modulate = new Color(0.2f, 0.5f, 0.8f);
		_progressBar.Value = 0;
		_statusLabel.Text = " Ready";
		_statusLabel.Modulate = new Color(1, 1, 1, 1);
		await Task.Delay(10000);
		_statusLabel.Text = "";
	}

	private void AddKeyframeAtPlayhead()
	{
		if (_selTrackIdx < 0 || _selClipIdx < 0) return;
		var clip = _tracks[_selTrackIdx].Clips[_selClipIdx];
		double localT = _timeline.SelectionPos - clip.Start;
		if (localT < 0 || localT > clip.End - clip.Start) return;
		SnapshotState();
		foreach (var prop in new[] { clip.PosX, clip.PosY, clip.Scale, clip.Opacity, clip.FontSizeAnim })
		{
			if (!prop.IsAnimated || !prop.Keyframes.Any(k => Math.Abs(k.Time - localT) < 0.01))
			{
				prop.IsAnimated = true;
				prop.Keyframes.RemoveAll(k => Math.Abs(k.Time - localT) < 0.01);
				prop.Keyframes.Add(new Keyframe { Time = localT, Value = prop.StaticValue });
			}
		}
		UpdateTracks();
		SetStatus("Keyframe added");
	}

	private void RemoveKeyframeAtPlayhead()
	{
		if (_selTrackIdx < 0 || _selClipIdx < 0) return;
		var clip = _tracks[_selTrackIdx].Clips[_selClipIdx];
		double localT = _timeline.SelectionPos - clip.Start;
		if (localT < 0 || localT > clip.End - clip.Start) return;
		SnapshotState();
		int removed = 0;
		foreach (var prop in new[] { clip.PosX, clip.PosY, clip.Scale, clip.Opacity, clip.FontSizeAnim })
		{
			removed += prop.Keyframes.RemoveAll(k => Math.Abs(k.Time - localT) < 0.01);
			if (prop.Keyframes.Count == 0) prop.IsAnimated = false;
		}
		if (removed > 0)
		{
			UpdateTracks();
			SetStatus($"Removed {removed} keyframe(s)");
		}
	}

	private void OnTrackReordered(int fromIndex, int toIndex)
	{
		if (fromIndex < 0 || fromIndex >= _tracks.Count || toIndex < 0 || toIndex >= _tracks.Count) return;
		SnapshotState();
		var track = _tracks[fromIndex];
		_tracks.RemoveAt(fromIndex);
		_tracks.Insert(toIndex, track);
		UpdateTracks();
		SetStatus($"Track moved: {track.Name}", Color.FromHtml("#D0570C"));
	}

	private void OnTrackRenameRequested(int trackIndex, string currentName)
	{
		if (trackIndex < 0 || trackIndex >= _tracks.Count) return;

		var dialog = new AcceptDialog
		{
			Title = "Rename Track",
			DialogText = "Enter new name:",
			OkButtonText = "Rename",
			Exclusive = true,
		};
		var input = new LineEdit { Text = currentName, SizeFlagsHorizontal = SizeFlags.ExpandFill };
		dialog.AddChild(input);
		AddChild(dialog);

		dialog.Confirmed += () =>
		{
			string newName = input.Text.Trim();
			if (!string.IsNullOrEmpty(newName))
			{
				SnapshotState();
				_tracks[trackIndex].Name = newName;
				UpdateTracks();
				SetStatus($"Track renamed: {newName}", Color.FromHtml("#D0570C"));
			}
		};

		// Open the dialog after adding the input
		dialog.PopupCentered(new Vector2I(300, 0));
		input.GrabFocus();
		input.CaretColumn = input.Text.Length;
		input.TextSubmitted += (text) =>
		{
			if (!string.IsNullOrEmpty(text.Trim()))
			{
				SnapshotState();
				_tracks[trackIndex].Name = text.Trim();
				UpdateTracks();
				SetStatus($"Track renamed: {text.Trim()}", Color.FromHtml("#D0570C"));
			}
			dialog.QueueFree();
		};
	}

	private async void RunBackgroundUpdateCheck()
	{
		if (!UpdateChecker.ShouldCheck()) return;

		var info = await UpdateChecker.CheckAsync(AppConfig.AppVersion);
		AppConfig.LastUpdateCheck = DateTime.UtcNow;

		if (info != null)
		{
			AppConfig.LastUpdateVersion = info.LatestVersion;
			AppConfig.SaveSettings();
			ToastManager.Info(this, $"Update v{info.LatestVersion} available \u2014 check Settings");
		}
	}
}
