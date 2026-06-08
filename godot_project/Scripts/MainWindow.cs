// Main editor window
//for Velos Content Creation Suite. Orchestrates UI construction, state machine
// (Import → Layout → Edit), track management, undo/redo, and Python backend IPC.
// Split across 5 partial files: MainWindow.*.cs

using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace VelosCCS;

public partial class MainWindow : Control
{
	private enum ViewState { Import, Layout, Edit }
	private ViewState _currentState = ViewState.Import;

	private BackendService _backendService = new();
	private FontManager _fontManager = new();
	private SFXManager _sfxManager = new();
	private AudioStreamPlayer _sfxPreviewPlayer = new();
	private string? _videoPath;
	private double _videoDuration;

	private VideoStreamPlayer _videoPlayer = null!;
	private VideoOverlay _overlay = null!;
	private OutputPreview _outputPreview = null!;
	private TimelineControl _timeline = null!;
	private VBoxContainer _inspectorList = null!;
	private ItemList _binUI = null!;
	private HBoxContainer _stepIndicator = null!;
	private Control _importView = null!;
	private Control _bodyHBox = null!;
	private AspectRatioContainer _srcAspect = null!;
	private TextureRect _sourceDisplay = null!;
	private VBoxContainer _timelineContainer = null!;
	private Control _previewWrapper = null!;
	private Control _timelineWrapper = null!;
	private HBoxContainer _editToolbar = null!;
	private VBoxContainer _sourceVbox = null!;
	private Label _statusLabel = null!;
	private LineEdit _urlInput = null!;
	private Button _playBtn = null!;
	private Button _layoutPlayBtn = null!;
	private Label _positionLabel = null!;
	private FileDialog _fileDialog = null!;
	private FileDialog _saveDialog = null!;
	private FileDialog _openProjectDialog = null!;
	private AcceptDialog _confirmDialog = null!;
	private VBoxContainer _rootVbox = null!;
	private ProgressBar _progressBar = null!;
	private PanelContainer _slidePanel = null!;
	private Control _slideWrapper = null!;
	private VBoxContainer _slideContent = null!;
	private VBoxContainer _sideVbox = null!;
	private Button _exportBtn = null!;
	private Button _continueBtn = null!;
	private StyleBoxFlat _exportRed = null!;
	private Button _toggleBtn = null!;
	private bool _slideOpen;
	private HBoxContainer _slideTabs = null!;
	private Button _slideMediaTab = null!;
	private Button _slideInspTab = null!;
	private VBoxContainer _mediaPanel = null!;
	private ScrollContainer _inspectorPanel = null!;
	private HBoxContainer _editorContent = null!;
	private Label _srcInfoLabel = null!;

	private TextureRect _importThumb = null!;
	private Label _importInfo = null!;
	private Control _importPreview = null!;
	private Button _selectClipsBtn = null!;
	private Button _aiFindBtn = null!;
	private StreamInfo _lastStreamInfo = null!;
	private int _pendingBinDeleteIdx = -1;
	private bool _pendingDeleteClips;
	private string _binSearchFilter = "";
	private Tween? _statusTween;

	private List<TrackData> _tracks = new();
	private readonly List<MediaAsset> _projectBin = new();
	private readonly List<int> _binFilteredIndices = new();
	private int _selTrackIdx = -1;
	private int _selClipIdx = -1;
	private bool _isPlaying;
	private bool _binDragPressed;
	private Vector2 _binDragStartPos;
	private int _binDragItemIndex = -1;
	private bool _isDraggingClip;
	private Timer? _seekPauseTimer;
	private double _lastPlayheadPos;
	private double _timelinePlayheadPos;
	private double _lastStreamPos;
	private bool _loopPlayback;
	private readonly Dictionary<TrackClipData, AudioStreamPlayer> _activeSfxPlayers = new();

	private readonly Stack<List<TrackData>> _undoStack = new();
	private readonly Stack<List<TrackData>> _redoStack = new();
	private List<TrackClipData>? _clipboard;
	private VBoxContainer _resVbox = null!;

	// Export settings (mirrors SettingsDialog defaults)
	public string ExportAspectRatio { get; set; } = "16:9";
	public string ExportOutputDir { get; set; } = "";
	public bool ExportNormalizeAudio { get; set; } = true;
	public string ExportCaptionLanguage { get; set; } = "en";

	private string _currentLayoutPreset = "";

	public override void _Notification(int what)
	{
		if (what == NotificationWMCloseRequest || what == NotificationWMGoBackRequest)
			AppConfig.SaveSettings();
	}

	public override void _Ready()
	{
		AppConfig.LoadSettings();
		LogBuffer.Init();
		Log.HookConsole();
		Log.Print($"Velos Content Creation Suite v{AppConfig.AppVersion} started on {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
		Log.Print($"Platform: {OS.GetName()} {OS.GetDistributionName()} — Godot {Engine.GetVersionInfo()["string"]}");

		SetAnchorsPreset(LayoutPreset.FullRect);
		AnchorRight = 1;
		AnchorBottom = 1;
		OffsetRight = 0;
		OffsetBottom = 0;

		Theme = AppTheme.Create();

		GetTree().Root.FilesDropped += (files) =>
		{
			foreach (string file in files)
				ImportFileToBin(file);
		};

		AddChild(_fontManager);
		AddChild(_sfxManager);
		AddChild(_sfxPreviewPlayer);
		BuildUI();
		SwitchToState(ViewState.Import);

		CallDeferred(nameof(ForceLayoutUpdate));
		CallDeferred(nameof(RunBackgroundUpdateCheck));
		// Start test server for AI-driven testing
		AddChild(new TestServer());

		// First-launch welcome dialog: offer to download Whisper for captions
		bool shouldShow = WhisperSetupDialog.ShouldShow;
		GD.Print($"[MainWindow] WhisperSetupDialog.ShouldShow={shouldShow}");
		if (shouldShow)
		{
			GD.Print("[MainWindow] Scheduling ShowWhisperSetup via CallDeferred");
			CallDeferred(nameof(ShowWhisperSetup));
		}
	}

	private void ShowWhisperSetup()
	{
		GD.Print("[ShowWhisperSetup] Creating WhisperSetupDialog");
		var dlg = new WhisperSetupDialog();
		GD.Print("[ShowWhisperSetup] AddChild");
		AddChild(dlg);
		GD.Print("[ShowWhisperSetup] PopupCentered");
		dlg.PopupCentered();
		GD.Print("[ShowWhisperSetup] Done");
	}

	private void ForceLayoutUpdate()
	{
		if (_rootVbox != null)
			_rootVbox.Size = GetViewportRect().Size;
	}

	public override void _Process(double delta)
	{
		if (_outputPreview != null && _videoPlayer != null && _videoPlayer.Stream != null)
		{
			var tex = _videoPlayer.GetVideoTexture();
			if (tex != null)
			{
				_sourceDisplay.Texture = tex;
				_outputPreview.RefreshVideoTexture(_videoPlayer);
			}
		}

		// Poll clipboard for link auto-import (only while in import view)
		if (_currentState == ViewState.Import)
			PollClipboard();
	}

	// ── Public accessors for TestServer ──
	public void ResetProject()
	{
		_tracks.Clear();
		_undoStack.Clear();
		_redoStack.Clear();
		_videoPath = null;
		_selTrackIdx = -1;
		_selClipIdx = -1;
		_videoDuration = 0;
		_projectBin.Clear();
		RefreshBinUI();
		// Reset video player state so next import triggers LoadVideoAsset
		if (_videoPlayer != null) _videoPlayer.Stream = null;
		_isPlaying = false;
		_timelinePlayheadPos = 0;
		_lastStreamPos = 0;
		if (_playBtn != null) SetPlayButtonText("Play");
		StopAllSfx();
		UpdateTracks();
		SwitchToState(ViewState.Import);
		SetStatus("Reset", Color.FromHtml("#D0570C"));
	}
	public void ImportFileInternal(string path) { ImportFileToBin(path); }
	public string? GetVideoPath() => _videoPath;
	public double GetVideoDuration() => _videoDuration;
	public int GetTrackCount() => _tracks.Count;
	public string GetCurrentState() => _currentState.ToString();
	public int GetSelTrackIdx() => _selTrackIdx;
	public int GetSelClipIdx() => _selClipIdx;
	public bool GetIsPlaying() => _isPlaying;
	public OutputPreview GetOutputPreview() => _outputPreview;
	public List<OverlayRegion> GetOverlayRegions() => _overlay.Regions;
	public List<MediaAsset> GetProjectBin() => _projectBin;
	public List<TrackData> GetTracks() => _tracks;
	public void CallAction(string name)
	{
		switch (name)
		{
			case "Undo": Undo(); break;
			case "Redo": Redo(); break;
			case "SplitAtPlayhead": SplitAtPlayhead(); break;
			case "DeleteSelected": DeleteSelected(); break;
			case "OnAddTextClip": OnAddTextClip(); break;
			case "OnGenerateCaptions": OnGenerateCaptions(); break;
			case "OnExportPressed": OnExportPressed(); break;
			case "OnAutoFrame": OnAutoFrame(); break;
			case "OpenStickerBrowser": OpenStickerBrowser(); break;
		}
	}
	public object? GetTracksData()
	{
		return _tracks.Select(t => new
		{
			name = t.Name,
			type = t.Type.ToString(),
			muted = t.Muted,
			clipCount = t.Clips.Count,
			clips = t.Clips.Select(c => new
			{
				type = c.ClipType.ToString(),
				start = c.Start,
				end = c.End,
				text = c.ClipType == ClipType.Text ? c.Text : null,
				filePath = c.ClipType is ClipType.Image or ClipType.Gif or ClipType.Audio ? c.FilePath : null,
				position = new[] { c.Position.X, c.Position.Y },
				size = new[] { c.Size.X, c.Size.Y },
				scale = c.Scale.StaticValue,
				opacity = c.Opacity.StaticValue,
				rotation = c.Rotation.StaticValue,
				volume = c.Volume.StaticValue,
				fontSize = c.FontSize,
				fontPath = c.FontPath ?? "",
				fontColor = new[] { c.FontColor.R, c.FontColor.G, c.FontColor.B, c.FontColor.A },
				outlineWidth = c.OutlineWidth,
				fadeIn = c.FadeIn,
				fadeOut = c.FadeOut,
			}).ToList(),
		}).ToList();
	}

	public object? GetClipData(int trackIdx, int clipIdx)
	{
		if (trackIdx < 0 || trackIdx >= _tracks.Count) return null;
		var t = _tracks[trackIdx];
		if (clipIdx < 0 || clipIdx >= t.Clips.Count) return null;
		var c = t.Clips[clipIdx];
		return new
		{
			trackName = t.Name,
			trackType = t.Type.ToString(),
			type = c.ClipType.ToString(),
			start = c.Start,
			end = c.End,
			text = c.ClipType == ClipType.Text ? c.Text : null,
			filePath = c.ClipType is ClipType.Image or ClipType.Gif or ClipType.Audio ? c.FilePath : null,
			position = new[] { c.Position.X, c.Position.Y },
			size = new[] { c.Size.X, c.Size.Y },
			scale = c.Scale.StaticValue,
			opacity = c.Opacity.StaticValue,
			rotation = c.Rotation.StaticValue,
			rotationAnimated = c.Rotation.IsAnimated || c.Rotation.Keyframes.Count > 0,
			volume = c.Volume.StaticValue,
			fontSize = c.FontSize,
			fontPath = c.FontPath ?? "",
			fontColor = new[] { c.FontColor.R, c.FontColor.G, c.FontColor.B, c.FontColor.A },
			outlineColor = new[] { c.OutlineColor.R, c.OutlineColor.G, c.OutlineColor.B, c.OutlineColor.A },
			outlineWidth = c.OutlineWidth,
			fadeIn = c.FadeIn,
			fadeOut = c.FadeOut,
			keyframeCount = c.TextKeyframes.Count,
			textKeyframes = c.TextKeyframes.Select(k => new
			{
				time = k.Time,
				text = k.Text,
			}).ToList(),
		};
	}

	public void SetClipProperty(string prop, double value)
	{
		if (_selTrackIdx < 0 || _selClipIdx < 0 ||
			_selTrackIdx >= _tracks.Count || _selClipIdx >= _tracks[_selTrackIdx].Clips.Count)
			return;
		var clip = _tracks[_selTrackIdx].Clips[_selClipIdx];
		switch (prop)
		{
			case "rotation": clip.Rotation.StaticValue = (float)value; break;
			case "opacity": clip.Opacity.StaticValue = (float)value; break;
			case "scale": clip.Scale.StaticValue = (float)value; break;
			case "volume": clip.Volume.StaticValue = (float)value; break;
			case "start": clip.Start = value; break;
			case "end": clip.End = value; break;
			case "fadeIn": clip.FadeIn = value; break;
			case "fadeOut": clip.FadeOut = value; break;
			case "positionX": clip.Position = new Vector2((float)value, clip.Position.Y); break;
			case "positionY": clip.Position = new Vector2(clip.Position.X, (float)value); break;
			case "sizeX": clip.Size = new Vector2(Math.Max(0.05f, (float)value), clip.Size.Y); break;
			case "sizeY": clip.Size = new Vector2(clip.Size.X, Math.Max(0.05f, (float)value)); break;
		}
		_overlay?.RefreshActiveLayer();
		_outputPreview?.RefreshDisplayLayer();
		_timeline?.QueueRedraw();
	}



	private void ExecuteBinDelete()
	{
		if (_pendingBinDeleteIdx < 0 || _pendingBinDeleteIdx >= _projectBin.Count) return;
		SnapshotState();
		var asset = _projectBin[_pendingBinDeleteIdx];
		_projectBin.RemoveAt(_pendingBinDeleteIdx);
		RemoveAssetFromTimeline(asset);
		RefreshBinUI();
		SetStatus("Asset removed from bin");
		_pendingBinDeleteIdx = -1;
	}

	private void ExecuteClipDelete()
	{
		_pendingDeleteClips = false;
		DeleteSelected();
	}

	public void SetVideoPath(string path, double duration)
	{
		_videoPath = path;
		_videoDuration = duration;
	}

	public void ClearAllState()
	{
		_tracks.Clear();
		_projectBin.Clear();
		_undoStack.Clear();
		_redoStack.Clear();
		_selTrackIdx = -1;
		_selClipIdx = -1;
		_videoPath = null;
		_videoDuration = 0;
		if (_videoPlayer != null) _videoPlayer.Stream = null;
		_isPlaying = false;
		_timelinePlayheadPos = 0;
		_lastStreamPos = 0;
		_timeline.ClearClips();
		RefreshBinUI();
	}

	public void AddAssetToBin(MediaAsset asset)
	{
		_projectBin.Add(asset);
	}

	public void AddTrackDirect(TrackData track)
	{
		_tracks.Add(track);
	}

	public void ClearOverlayRegions()
	{
		_overlay.Regions.Clear();
	}

	public void AddOverlayRegion(OverlayRegion region)
	{
		_overlay.Regions.Add(region);
	}

	public void UpdateAfterLoad()
	{
		RefreshBinUI();
		_overlay.QueueRedraw();
		_outputPreview.QueueRedraw();
		UpdateLayoutRegionVisibility();
		ReloadVideoSource();
		if (_projectBin.Count > 0)
			SwitchToState(ViewState.Edit);
		else
			UpdateTracks();
		SetStatus("Project loaded", Color.FromHtml("#D0570C"));
	}

	private void ReloadVideoSource()
	{
		if (string.IsNullOrEmpty(_videoPath) || !System.IO.File.Exists(_videoPath))
		{
			_srcInfoLabel.Text = "";
			return;
		}

		try
		{
			var userPath = "user://temp_video.mp4";
			System.IO.File.Copy(_videoPath, ProjectSettings.GlobalizePath(userPath), true);
			_videoPlayer.Stream = ResourceLoader.Load<VideoStream>(userPath);
			if (_videoPlayer.Stream != null)
			{
				_videoPlayer.Play();
				_isPlaying = true;
				if (_playBtn != null) SetPlayButtonText("Pause");
			}
			_srcInfoLabel.Text = $"{System.IO.Path.GetFileName(_videoPath)}  ({_videoDuration:F1}s)";

			// Load waveform for audio clips
			var videoAsset = _projectBin.FirstOrDefault(a => a.Type == AssetType.Video);
			if (videoAsset != null)
				_ = LoadWaveform(videoAsset);
		}
		catch (Exception ex)
		{
			GD.PrintErr($"Failed to reload video source: {ex.Message}");
			_srcInfoLabel.Text = "Video file not found: " + System.IO.Path.GetFileName(_videoPath);
		}
	}

	// ─── SAVE / LOAD ──────────────────────────────────────────────────────

	public async Task SaveProjectAsync(string filePath)
	{
		try
		{
			var data = ProjectSerializer.Serialize(this);
			string json = ProjectSerializer.ToJson(data);
			using var file = Godot.FileAccess.Open(filePath, Godot.FileAccess.ModeFlags.Write);
			if (file == null)
			{
				SetStatus("Failed to save project: could not open file for writing", Color.FromHtml("#f78166"));
				return;
			}
			file.StoreString(json);
			file.Close();
			SetStatus($"Project saved: {filePath.GetFile()}", Color.FromHtml("#D0570C"));
			ToastManager.Show(this, "PROJECT SAVED", Color.FromHtml("#D0570C"));
		}
		catch (Exception ex)
		{
			GD.PrintErr($"Save failed: {ex}");
			SetStatus("Save failed: " + ex.Message, Color.FromHtml("#f78166"));
		}
		await Task.CompletedTask;
	}

	public async Task LoadProjectAsync(string filePath)
	{
		try
		{
			using var file = Godot.FileAccess.Open(filePath, Godot.FileAccess.ModeFlags.Read);
			if (file == null)
			{
				SetStatus("Failed to open project: could not read file", Color.FromHtml("#f78166"));
				return;
			}
			string json = file.GetAsText();
			file.Close();

			var data = ProjectSerializer.FromJson(json);
			if (data == null)
			{
				SetStatus("Failed to parse project file", Color.FromHtml("#f78166"));
				return;
			}

			ProjectSerializer.DeserializeInto(data, this);
			SetStatus($"Project loaded: {filePath.GetFile()}", Color.FromHtml("#D0570C"));
			ToastManager.Show(this, "PROJECT LOADED", Color.FromHtml("#D0570C"));
		}
		catch (Exception ex)
		{
			GD.PrintErr($"Load failed: {ex}");
			SetStatus("Load failed: " + ex.Message, Color.FromHtml("#f78166"));
		}
		await Task.CompletedTask;
	}

	// Events from save/open dialogs
	private void OnSaveFileSelected(string path)
	{
		// Ensure .velosccs extension

		if (!path.EndsWith(".velosccs", StringComparison.OrdinalIgnoreCase))

			path += ".velosccs";
		_ = SaveProjectAsync(path);
	}

	private void OnOpenFileSelected(string path)
	{
		_ = LoadProjectAsync(path);
	}

	public void SetSelection(int track, int clip)
	{
		_selTrackIdx = track;
		_selClipIdx = clip;
		RebuildInspector();
		// Also update timeline selection so SplitAtPlayhead etc. work
		if (_timeline != null && track >= 0 && track < _tracks.Count && clip >= 0 && clip < _tracks[track].Clips.Count)
		{
			int flatIdx = 0;
			for (int t = 0; t < track; t++)
				flatIdx += _tracks[t].Clips.Count;
			flatIdx += clip;
			_timeline.SetSelectedClip(flatIdx);
		}
	}
	public void SetTimelinePos(double pos)
	{
		if (_timeline != null)
			_timeline.SetSelection(pos);
	}

	private void BuildUI()
	{
		var bg = new ColorRect { Color = Color.FromHtml("#191A25") };
		bg.SetAnchorsPreset(LayoutPreset.FullRect);
		AddChild(bg);
		_rootVbox = new VBoxContainer();
		AddChild(_rootVbox);
		_rootVbox.SetAnchorsPreset(LayoutPreset.FullRect);
		_rootVbox.AddThemeConstantOverride("separation", 0);

		// ─── TOP BAR (Steps only: Import / Layout / Edit) ───
		var topBar = new PanelContainer { CustomMinimumSize = new Vector2(0, 59) };
		topBar.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = Color.FromHtml("#11121C") });
		_rootVbox.AddChild(topBar);
		var topH = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, SizeFlagsHorizontal = SizeFlags.ExpandFill };
		topBar.AddChild(topH);

		_stepIndicator = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
		_stepIndicator.AddThemeConstantOverride("separation", 12);
		topH.AddChild(_stepIndicator);

		// ─── IMPORT VIEW ───
		_importView = new CenterContainer { SizeFlagsVertical = SizeFlags.ExpandFill };

		var impV = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
		_importView.AddChild(impV);

		var bigBtn = new PanelContainer
		{
			CustomMinimumSize = new Vector2(500, 260),
			MouseFilter = MouseFilterEnum.Pass,
		};
		bigBtn.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = Color.FromHtml("#303030"), CornerRadiusTopLeft = 12, CornerRadiusTopRight = 12, CornerRadiusBottomLeft = 12, CornerRadiusBottomRight = 12 });
		var bigBtnVbox = new VBoxContainer
		{
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			SizeFlagsVertical = SizeFlags.ExpandFill,
		};
		var bigBtnLabel = new Label
		{
			Text = "DROP VIDEO FILE HERE",
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			SizeFlagsVertical = SizeFlags.ExpandFill,
			Modulate = Color.FromHtml("#D9D9D9"),
		};
		bigBtnVbox.AddChild(bigBtnLabel);
		bigBtn.AddChild(bigBtnVbox);

		// Make the whole panel clickable to open file dialog
		var bigBtnClick = new Button
		{
			Text = "Select File",
			SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
		};
		bigBtnClick.AddThemeStyleboxOverride("normal", new StyleBoxFlat { BgColor = Color.FromHtml("#555555"), CornerRadiusTopLeft = 10, CornerRadiusTopRight = 10, CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10, ContentMarginLeft = 20, ContentMarginRight = 20, ContentMarginTop = 10, ContentMarginBottom = 10 });
		bigBtnClick.Pressed += () => _fileDialog.PopupCentered();
		bigBtnVbox.AddChild(bigBtnClick);
		bigBtn.GuiInput += (ev) =>
		{
			if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
				_fileDialog.PopupCentered();
		};
		impV.AddChild(bigBtn);

		impV.AddChild(new HSeparator());

		var orLabel = new Label
		{
			Text = "Or paste a YouTube / Twitch link",
			HorizontalAlignment = HorizontalAlignment.Center,
			Modulate = Color.FromHtml("#D9D9D9"),
		};
		impV.AddChild(orLabel);

		_urlInput = new LineEdit
		{
			PlaceholderText = "https://youtube.com/watch?v=...",
			CustomMinimumSize = new Vector2(0, 36),
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
		};
		_urlInput.TextSubmitted += (_) => OnDownloadPressed();
		var urlBtn = new Button { Text = "Fetch" };
		urlBtn.AddThemeStyleboxOverride("normal", new StyleBoxFlat { BgColor = Color.FromHtml("#555555"), CornerRadiusTopLeft = 10, CornerRadiusTopRight = 10, CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10, ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 8, ContentMarginBottom = 8 });
		urlBtn.AddThemeColorOverride("font_color", Color.FromHtml("#D0570C"));
		urlBtn.Pressed += OnDownloadPressed;
		var urlClearBtn = new Button { Text = "X", Flat = true, CustomMinimumSize = new Vector2(36, 36), TooltipText = "Clear" };
		urlClearBtn.Pressed += () => { _urlInput.Text = ""; _importPreview.Visible = false; };
		var urlRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
		urlRow.AddChild(_urlInput);
		urlRow.AddChild(urlBtn);
		urlRow.AddChild(urlClearBtn);
		impV.AddChild(urlRow);

		// ─── IMPORT PREVIEW (hidden until URL info fetched) ───
		_importPreview = new HBoxContainer { Visible = false };
		_importThumb = new TextureRect
		{
			CustomMinimumSize = new Vector2(320, 180),
			StretchMode = TextureRect.StretchModeEnum.KeepAspect,
			ExpandMode = TextureRect.ExpandModeEnum.FitWidth,
		};
		_importPreview.AddChild(_importThumb);

		var infoVbox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		_importInfo = new Label { AutowrapMode = TextServer.AutowrapMode.Word };
		infoVbox.AddChild(_importInfo);
		_selectClipsBtn = new Button { Text = "Select Clips to Download" };
		_selectClipsBtn.AddThemeStyleboxOverride("normal", new StyleBoxFlat { BgColor = Color.FromHtml("#555555"), CornerRadiusTopLeft = 10, CornerRadiusTopRight = 10, CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10, ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 8, ContentMarginBottom = 8 });
		_selectClipsBtn.AddThemeColorOverride("font_color", Color.FromHtml("#D0570C"));
		_selectClipsBtn.Pressed += OnSelectClips;
		infoVbox.AddChild(_selectClipsBtn);
		_aiFindBtn = new Button { Text = "AI Find Clips" };
		_aiFindBtn.AddThemeStyleboxOverride("normal", new StyleBoxFlat { BgColor = Color.FromHtml("#555555"), CornerRadiusTopLeft = 10, CornerRadiusTopRight = 10, CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10, ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 8, ContentMarginBottom = 8 });
		_aiFindBtn.AddThemeColorOverride("font_color", Color.FromHtml("#BF2618"));
		_aiFindBtn.Pressed += OnAIFindClips;
		infoVbox.AddChild(_aiFindBtn);
		_importPreview.AddChild(infoVbox);
		impV.AddChild(_importPreview);

		// ─── BODY: Sidebar (always visible) + Content Stack (toggles Import vs Editor) ───
		_bodyHBox = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
		_rootVbox.AddChild(_bodyHBox);

		// SIDEBAR
		var sideBar = new PanelContainer { CustomMinimumSize = new Vector2(134, 0), MouseFilter = MouseFilterEnum.Pass };
		sideBar.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = Color.FromHtml("#11121C") });
		_bodyHBox.AddChild(sideBar);
		_sideVbox = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
		_sideVbox.AddThemeConstantOverride("margin_left", 8);
		_sideVbox.AddThemeConstantOverride("margin_right", 8);
		_sideVbox.AddThemeConstantOverride("margin_top", 20);
		_sideVbox.AddThemeConstantOverride("margin_bottom", 16);
		_sideVbox.AddThemeConstantOverride("separation", 32);
		sideBar.AddChild(_sideVbox);

		var logoCircle = new PanelContainer { CustomMinimumSize = new Vector2(80, 80), MouseFilter = MouseFilterEnum.Ignore, SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
		logoCircle.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = Color.FromHtml("#D9D9D9"), CornerRadiusTopLeft = 40, CornerRadiusTopRight = 40, CornerRadiusBottomLeft = 40, CornerRadiusBottomRight = 40 });
		var logoVbox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ShrinkCenter, SizeFlagsVertical = SizeFlags.ShrinkCenter };
		logoVbox.AddThemeConstantOverride("separation", 6);
		logoCircle.AddChild(logoVbox);
		var logoSeg1 = new ColorRect { Color = Color.FromHtml("#D0570C"), CustomMinimumSize = new Vector2(24, 6), SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
		var logoSeg2 = new ColorRect { Color = Color.FromHtml("#D0570C"), CustomMinimumSize = new Vector2(36, 6), SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
		var logoSeg3 = new ColorRect { Color = Color.FromHtml("#D0570C"), CustomMinimumSize = new Vector2(16, 6), SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
		logoVbox.AddChild(logoSeg1);
		logoVbox.AddChild(logoSeg2);
		logoVbox.AddChild(logoSeg3);
		_sideVbox.AddChild(logoCircle);
		_sideVbox.AddChild(new Control { CustomMinimumSize = new Vector2(0, 30) });

		BuildSidebarButton("Settings", () =>
		{
			var dlg = new SettingsDialog
			{
				CurrentOutputDir = ExportOutputDir,
				CurrentNormalizeAudio = ExportNormalizeAudio,
				CurrentCaptionLanguage = ExportCaptionLanguage,
			};
			dlg.Confirmed += () =>
			{
				ExportNormalizeAudio = dlg.NormalizeAudio;
				ExportOutputDir = dlg.OutputDir;
				ExportCaptionLanguage = dlg.CaptionLanguage;
				AppConfig.CaptionLanguage = dlg.CaptionLanguage;
				AppConfig.ExportOutputDir = dlg.OutputDir;
				AppConfig.SaveSettings();
			};
			AddChild(dlg);
			dlg.PopupCentered();
		});
		BuildSidebarButton("Console", () => DebugConsole.Toggle());
		BuildSidebarButton("Save", () => _saveDialog.PopupCentered());
		BuildSidebarButton("Open", () => _openProjectDialog.PopupCentered());
		BuildSidebarButton("Legal", () =>
		{
			var dlg = new AcceptDialog { Title = "Legal & Credits", MinSize = new Vector2I(520, 400), Exclusive = true, OkButtonText = "Close" };
			var vbox = new VBoxContainer();
			vbox.AddThemeConstantOverride("separation", 10);
			dlg.AddChild(vbox);
			vbox.AddChild(new Label { Text = "Velos Content Creation Suite uses the following open-source software:", Modulate = new Color(0.8f, 0.8f, 0.8f) });
			var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
			var creditVbox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
			var credits = new Label
			{
				Text = "",
				AutowrapMode = TextServer.AutowrapMode.Word,
				SizeFlagsHorizontal = SizeFlags.ExpandFill,
			};
			creditVbox.AddChild(credits);
			scroll.AddChild(creditVbox);
			vbox.AddChild(scroll);
			vbox.AddChild(new Label { Text = "See LICENSE-THIRD-PARTY.txt for full license texts.", Modulate = new Color(0.5f, 0.5f, 0.5f) });
			AddChild(dlg);
			dlg.PopupCentered();
			credits.Text = string.Join("\n",
				"• FFmpeg — GNU General Public License v3.0",
				"  https://ffmpeg.org",
				"",
				"• llama.cpp — MIT License",
				"  Copyright (c) 2023 Georgi Gerganov",
				"  https://github.com/ggerganov/llama.cpp",
				"",
				"• yt-dlp — The Unlicense",
				"  https://github.com/yt-dlp/yt-dlp",
				"",
				"• Whisper.net — MIT License",
				"  Copyright (c) 2023 Alex Andreev",
				"  https://github.com/sandrohanea/whisper.net",
				"",
				"• Godot Engine — MIT License",
				"  Copyright (c) 2014-present Godot Engine contributors",
				"  https://godotengine.org"
			);
			credits.Modulate = new Color(0.7f, 0.7f, 0.7f);
		});

		_toggleBtn = new Button
		{
			Text = "Panel \u25B6",
			CustomMinimumSize = new Vector2(0, 46),
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			TooltipText = "Toggle side panel",
		};
		var toggleNorm = new StyleBoxFlat { BgColor = Color.FromHtml("#D0570C"), CornerRadiusTopLeft = 15, CornerRadiusTopRight = 15, CornerRadiusBottomLeft = 15, CornerRadiusBottomRight = 15, ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 8, ContentMarginBottom = 8 };
		var toggleHover = new StyleBoxFlat { BgColor = Color.FromHtml("#e0661a"), CornerRadiusTopLeft = 15, CornerRadiusTopRight = 15, CornerRadiusBottomLeft = 15, CornerRadiusBottomRight = 15, ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 8, ContentMarginBottom = 8 };
		_toggleBtn.AddThemeStyleboxOverride("normal", toggleNorm);
		_toggleBtn.AddThemeStyleboxOverride("hover", toggleHover);
		_toggleBtn.AddThemeStyleboxOverride("pressed", toggleHover);
		_toggleBtn.AddThemeStyleboxOverride("disabled", toggleNorm);
		_toggleBtn.AddThemeColorOverride("font_color", Color.FromHtml("#FFFFFF"));
		_toggleBtn.Pressed += ToggleSlidePanel;
		_sideVbox.AddChild(_toggleBtn);

		_sideVbox.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });

		_exportBtn = new Button
		{
			Text = "Export",
			CustomMinimumSize = new Vector2(0, 46),
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			Visible = false,
			TooltipText = "Export video (Ctrl+E)",
		};
		var exportGray = new StyleBoxFlat { BgColor = Color.FromHtml("#D9D9D9"), CornerRadiusTopLeft = 15, CornerRadiusTopRight = 15, CornerRadiusBottomLeft = 15, CornerRadiusBottomRight = 15, ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 8, ContentMarginBottom = 8 };
		_exportRed = new StyleBoxFlat { BgColor = Color.FromHtml("#BF2618"), CornerRadiusTopLeft = 15, CornerRadiusTopRight = 15, CornerRadiusBottomLeft = 15, CornerRadiusBottomRight = 15, ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 8, ContentMarginBottom = 8 };
		_exportBtn.AddThemeStyleboxOverride("normal", exportGray);
		_exportBtn.AddThemeStyleboxOverride("hover", _exportRed);
		_exportBtn.AddThemeStyleboxOverride("pressed", _exportRed);
		_exportBtn.AddThemeStyleboxOverride("disabled", exportGray);
		_exportBtn.AddThemeColorOverride("font_color", Color.FromHtml("#11121C"));
		_exportBtn.Pressed += OnExportPressed;
		_sideVbox.AddChild(_exportBtn);

		_continueBtn = new Button
		{
			Text = "Continue to Editor",
			CustomMinimumSize = new Vector2(0, 46),
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			Visible = false,
			TooltipText = "Switch to Edit mode",
		};
		_continueBtn.AddThemeStyleboxOverride("normal", _exportRed);
		_continueBtn.AddThemeStyleboxOverride("hover", _exportRed);
		_continueBtn.AddThemeStyleboxOverride("pressed", _exportRed);
		_continueBtn.AddThemeColorOverride("font_color", Color.FromHtml("#FFFFFF"));
		_continueBtn.Pressed += () => SwitchToState(ViewState.Edit);
		_sideVbox.AddChild(_continueBtn);

		// CONTENT STACK: toggles between Import view and Editor content
		var contentStack = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
		contentStack.AddThemeConstantOverride("separation", 0);
		_bodyHBox.AddChild(contentStack);
		contentStack.AddChild(_importView);
		_editorContent = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, Visible = false };
		contentStack.AddChild(_editorContent);

		// SLIDE-OUT PANEL: wrapper clips content at animated width; inner panel always full-size
		_slideWrapper = new Control { CustomMinimumSize = new Vector2(0, 0), SizeFlagsHorizontal = SizeFlags.ShrinkCenter, ClipContents = true };
		_editorContent.AddChild(_slideWrapper);
		_slidePanel = new PanelContainer();
		_slidePanel.SetAnchorsPreset(LayoutPreset.FullRect);
		_slidePanel.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = Color.FromHtml("#11121C") });
		_slideWrapper.AddChild(_slidePanel);
		_slideContent = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
		_slideContent.AddThemeConstantOverride("margin_left", 12);
		_slideContent.AddThemeConstantOverride("margin_right", 12);
		_slideContent.AddThemeConstantOverride("margin_top", 12);
		_slideContent.AddThemeConstantOverride("separation", 6);
		_slidePanel.AddChild(_slideContent);

		// Tab header for Edit mode
		_slideTabs = new HBoxContainer();
		_slideTabs.AddThemeConstantOverride("separation", 6);
		_slideContent.AddChild(_slideTabs);
		_slideMediaTab = new Button { Text = "Media", ToggleMode = true, ButtonPressed = true, SizeFlagsHorizontal = SizeFlags.ExpandFill, Flat = true };
		_slideInspTab = new Button { Text = "Inspector", ToggleMode = true, SizeFlagsHorizontal = SizeFlags.ExpandFill, Flat = true };
		var tabGroup = new ButtonGroup();
		_slideMediaTab.ButtonGroup = tabGroup;
		_slideInspTab.ButtonGroup = tabGroup;
		_slideMediaTab.Toggled += (on) => { if (on) { _mediaPanel.Visible = true; _inspectorPanel.Visible = false; } };
		_slideInspTab.Toggled += (on) => { if (on) { _mediaPanel.Visible = false; _inspectorPanel.Visible = true; } };
		// Style tabs as rectangular pills
		var tabNorm = new StyleBoxFlat { BgColor = new Color(0.2f, 0.2f, 0.2f), CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6 };
		var tabPressed = new StyleBoxFlat { BgColor = Color.FromHtml("#D0570C"), CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6 };
		_slideMediaTab.AddThemeStyleboxOverride("normal", tabNorm);
		_slideMediaTab.AddThemeStyleboxOverride("pressed", tabPressed);
		_slideInspTab.AddThemeStyleboxOverride("normal", tabNorm);
		_slideInspTab.AddThemeStyleboxOverride("pressed", tabPressed);
		_slideTabs.AddChild(_slideMediaTab);
		_slideTabs.AddChild(_slideInspTab);

		// Stacked content
		_mediaPanel = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, Visible = true };
		_mediaPanel.AddThemeConstantOverride("separation", 6);
		_inspectorPanel = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, Visible = false };
		_inspectorList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		_inspectorList.AddThemeConstantOverride("margin_left", 16);
		_inspectorList.AddThemeConstantOverride("margin_right", 16);
		_inspectorList.AddThemeConstantOverride("margin_top", 12);
		_inspectorList.AddThemeConstantOverride("separation", 8);
		_inspectorPanel.AddChild(_inspectorList);
		_slideContent.AddChild(_mediaPanel);
		_slideContent.AddChild(_inspectorPanel);

		BuildMediaBinTab(_mediaPanel);

		// RIGHT AREA: Monitors + Timeline
		var rightVbox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
		_editorContent.AddChild(rightVbox);

		// PREVIEW WRAPPER: slides in/out, fills remaining space
		_previewWrapper = new Control { SizeFlagsVertical = SizeFlags.ExpandFill, ClipContents = true };
		rightVbox.AddChild(_previewWrapper);

		// UPPER: Monitors side by side (HSplitContainer = user-resizable divider)
		var previewH = new HSplitContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		previewH.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = Color.FromHtml("#191A25") });
		previewH.SetAnchorsPreset(LayoutPreset.FullRect);
		_previewWrapper.AddChild(previewH);

		// Edit Monitor (left)
		_sourceVbox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = Vector2.Zero };
		_sourceVbox.AddChild(new Label { Text = "EDIT", HorizontalAlignment = HorizontalAlignment.Center, CustomMinimumSize = new Vector2(0, 0) });

		var srcBg = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
		srcBg.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = Color.FromHtml("#191A25") });
		_sourceVbox.AddChild(srcBg);

		_srcAspect = new AspectRatioContainer { Ratio = 16f / 9f, SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = Vector2.Zero, MouseFilter = MouseFilterEnum.Ignore };
		srcBg.AddChild(_srcAspect);

		var srcInfo = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Center, Modulate = new Color(0.5f, 0.5f, 0.5f), CustomMinimumSize = new Vector2(0, 0) };
		_sourceVbox.AddChild(srcInfo);
		_srcInfoLabel = srcInfo;

		_layoutPlayBtn = new Button
		{
			Text = "Play",
			Visible = false,
			SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
		};
		_layoutPlayBtn.Pressed += () => SetPlayback(!_isPlaying, false);
		_sourceVbox.AddChild(_layoutPlayBtn);

		_videoPlayer = new VideoStreamPlayer { Expand = true, MouseFilter = MouseFilterEnum.Ignore };
		_srcAspect.AddChild(_videoPlayer);

		_sourceDisplay = new TextureRect
		{
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
			StretchMode = TextureRect.StretchModeEnum.Scale,
			MouseFilter = MouseFilterEnum.Ignore,
		};
		_srcAspect.AddChild(_sourceDisplay);
		_sourceDisplay.SetAnchorsPreset(LayoutPreset.FullRect);

		_overlay = new VideoOverlay { Visible = false };
		_overlay.LayoutChanged += (string _) => _outputPreview.QueueRedraw();
		_overlay.CameraPipChanged += (pos, size) => _outputPreview.SetCameraOutput(pos, size);
		_overlay.UiPipChanged += (pos, size) => _outputPreview.SetUiOutput(pos, size);
		_srcAspect.AddChild(_overlay);

		var srcTimer = new Timer { WaitTime = 0.05, Autostart = true };
		srcTimer.Timeout += () => _sourceDisplay.Texture = _videoPlayer.GetVideoTexture();
		_srcAspect.AddChild(srcTimer);

		previewH.AddChild(_sourceVbox);

		// Master Monitor (right)
		_resVbox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = Vector2.Zero };
		var resVbox = _resVbox;
		resVbox.AddChild(new Label { Text = "RESULT", HorizontalAlignment = HorizontalAlignment.Center, CustomMinimumSize = new Vector2(0, 0) });

		var resBg = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
		resBg.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = Color.FromHtml("#191A25") });
		resVbox.AddChild(resBg);

		_outputPreview = new OutputPreview();
		_outputPreview.Setup(_videoPlayer);
		_outputPreview.SetOverlay(_overlay);
		_outputPreview.TextEdited += ((int ti, int ci, string text) args) =>
		{
			if (args.ti >= 0 && args.ti < _tracks.Count && args.ci >= 0 && args.ci < _tracks[args.ti].Clips.Count)
			{
				var clip = _tracks[args.ti].Clips[args.ci];
				clip.Text = args.text;
				RefreshClipViews();
			}
		};
		_outputPreview.RotationChanged += ((int ti, int ci, float rotation) args) =>
		{
			if (args.ti >= 0 && args.ti < _tracks.Count && args.ci >= 0 && args.ci < _tracks[args.ti].Clips.Count)
			{
				var clip = _tracks[args.ti].Clips[args.ci];
				clip.Rotation.StaticValue = args.rotation;
				RefreshClipViews();
			}
		};
		_outputPreview.SizeFlagsVertical = SizeFlags.ExpandFill;
		resBg.AddChild(_outputPreview);
		previewH.AddChild(resVbox);

		// LOWER: Timeline area (wrapped for slide-in)
		_timelineContainer = new VBoxContainer { Visible = false };
		_timelineWrapper = new Control { ClipContents = true };
		_timelineWrapper.SizeFlagsVertical = SizeFlags.ShrinkBegin;
		_timelineWrapper.CustomMinimumSize = new Vector2(0, 0);
		rightVbox.AddChild(_timelineWrapper);
		_timelineContainer.SetAnchorsPreset(LayoutPreset.FullRect);
		_timelineContainer.AddThemeConstantOverride("margin_bottom", 8);
		_timelineWrapper.AddChild(_timelineContainer);

		BuildTimelineToolbar(_timelineContainer);

		_timeline = new TimelineControl { SizeFlagsVertical = SizeFlags.ExpandFill };
		_timeline.SeekRequested += SeekVideo;
		_timeline.ClipSelected += OnClipSelected;
		_timeline.SplitRequested += SplitAtPlayhead;
		_timeline.ContextMenuRequested += ShowClipContextMenu;
		_timeline.DragFinished += () => _isDraggingClip = false;
		_timeline.TrackReordered += OnTrackReordered;
		_timeline.TrackRenameRequested += OnTrackRenameRequested;
		_timeline.TrimChanged += (s, e) =>
		{
			if (!_isDraggingClip) { SnapshotState(); _isDraggingClip = true; }
			if (_selTrackIdx >= 0 && _selClipIdx >= 0)
			{
				var clip = _tracks[_selTrackIdx].Clips[_selClipIdx];
				clip.Start = s;
				clip.End = e;
			}
		};
		_timeline.ClipMoved += (flatIdx, newStart, newEnd, newTrackIdx) =>
		{
			SnapshotState();
			_isDraggingClip = true;
			int idx = flatIdx;
			for (int t = 0; t < _tracks.Count; t++)
			{
				if (idx < _tracks[t].Clips.Count)
				{
					var clip = _tracks[t].Clips[idx];

					// Validate track type: prevent video/audio type mismatch
					if (newTrackIdx >= 0 && newTrackIdx < _tracks.Count)
					{
						var targetTrack = _tracks[newTrackIdx];
						bool isAudioClip = clip.ClipType == ClipType.Audio;
						bool isAudioTrack = targetTrack.Type == TrackType.Audio;
						if (isAudioClip != isAudioTrack)
							newTrackIdx = t;
					}

					clip.Start = newStart;
					clip.End = newEnd;
					if (newTrackIdx >= 0 && newTrackIdx < _tracks.Count && newTrackIdx != t)
					{
						_tracks[t].Clips.RemoveAt(idx);
						_tracks[newTrackIdx].Clips.Add(clip);
						UpdateTracks();
					}
					return;
				}
				idx -= _tracks[t].Clips.Count;
			}
		};
		_timelineContainer.AddChild(_timeline);

		// ─── DRAG-DROP HANDLER ───
		_timeline.AssetDropped += (time, assetIdx) =>
		{
			if (assetIdx < 0 || assetIdx >= _projectBin.Count) return;
			var asset = _projectBin[assetIdx];
			SnapshotState();
			_timeline.SetSelection(time);
			AddAssetToTimeline(asset);
		};

		// ─── FILE DIALOG ───
		var homeDir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
		_fileDialog = new FileDialog { FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true, CurrentDir = homeDir };
		_fileDialog.AddFilter("*.mp4,*.mov,*.avi,*.mkv,*.webm ; Video Files");
		_fileDialog.AddFilter("*.mp3,*.wav,*.ogg,*.flac ; Audio Files");
		_fileDialog.FileSelected += ImportFileToBin;
		AddChild(_fileDialog);

		// ─── CONFIRMATION DIALOG ───
		_confirmDialog = new AcceptDialog();
		_confirmDialog.Title = "Confirm";
		_confirmDialog.OkButtonText = "Delete";
		AddChild(_confirmDialog);

		// ─── SAVE / OPEN PROJECT DIALOGS ───
		_saveDialog = new FileDialog
		{
			Title = "Save Project As",
			FileMode = FileDialog.FileModeEnum.SaveFile,
			Access = FileDialog.AccessEnum.Filesystem,
			UseNativeDialog = true,
			CurrentDir = homeDir,
		};
		_saveDialog.AddFilter("*.velosccs ; VelosCCS Project");
		_saveDialog.FileSelected += OnSaveFileSelected;
		AddChild(_saveDialog);

		_openProjectDialog = new FileDialog
		{
			Title = "Open Project",
			FileMode = FileDialog.FileModeEnum.OpenFile,
			Access = FileDialog.AccessEnum.Filesystem,
			UseNativeDialog = true,
			CurrentDir = homeDir,
		};
		_openProjectDialog.AddFilter("*.velosccs ; VelosCCS Project");
		_openProjectDialog.FileSelected += OnOpenFileSelected;
		AddChild(_openProjectDialog);

		// ─── STATUS BAR ───
		var statusBar = new VBoxContainer();
		statusBar.AddThemeConstantOverride("separation", 0);
		_rootVbox.AddChild(statusBar);

		_statusLabel = new Label { Text = " Ready", CustomMinimumSize = new Vector2(0, 24) };
		statusBar.AddChild(_statusLabel);

		_progressBar = new ProgressBar
		{
			Visible = false,
			MaxValue = 100,
			Value = 0,
			CustomMinimumSize = new Vector2(0, 6),
			ShowPercentage = false,
			Modulate = new Color(0.2f, 0.5f, 0.8f),
		};
		statusBar.AddChild(_progressBar);

		// ─── PLAYHEAD SYNC TIMER ───
		var timer = new Timer { WaitTime = 0.25, Autostart = true };
		timer.Timeout += OnTimerTimeout;
		AddChild(timer);
	}

	private void TransitionToView(Control activeView)
	{
		Control[] allViews = { _importView, _editorContent };

		foreach (var v in allViews)
		{
			if (v == activeView)
			{
				v.Visible = true;
				if (v.Modulate.A < 0.5f)
				{
					v.Modulate = new Color(1, 1, 1, 0);
					var tween = CreateTween();
					tween.TweenProperty(v, "modulate", new Color(1, 1, 1, 1), 0.3f);
				}

				if (v == _editorContent)
					_rootVbox.QueueSort();
			}
			else
			{
				if (v.Visible && v.Modulate.A > 0.5f)
				{
					var tween = CreateTween();
					tween.TweenProperty(v, "modulate", new Color(1, 1, 1, 0), 0.15f);
					tween.TweenProperty(v, "visible", false, 0);
				}
				else
				{
					v.Visible = false;
				}
			}
		}
	}

	private void SwitchToState(ViewState state)
	{
		Log.Print($"SwitchToState: {_currentState} -> {state}");
		var prevState = _currentState;
		_currentState = state;
		if (state == ViewState.Import) TransitionToView(_importView);
		else TransitionToView(_editorContent);
		_rootVbox.QueueSort();

		// Preview slide in/out (Import ↔ Layout/Edit)
		if (prevState == ViewState.Import && state != ViewState.Import)
		{
			_previewWrapper.SizeFlagsVertical = SizeFlags.ShrinkBegin;
			_previewWrapper.CustomMinimumSize = new Vector2(0, 0);
			CallDeferred(nameof(AnimatePreviewIn));
		}
		else if (state == ViewState.Import && prevState != ViewState.Import)
		{
			float currentH = _previewWrapper.Size.Y;
			_previewWrapper.SizeFlagsVertical = SizeFlags.ShrinkBegin;
			_previewWrapper.CustomMinimumSize = new Vector2(0, currentH);
			var t = CreateTween();
			t.TweenProperty(_previewWrapper, "custom_minimum_size", new Vector2(0, 0), 0.2f)
			 .SetEase(Tween.EaseType.In).SetTrans(Tween.TransitionType.Cubic);
		}

		// Timeline slide in/out + toolbar fade (Edit only)
		bool showTimeline = (state == ViewState.Edit);
		bool hideTimeline = !showTimeline && _timelineContainer.Visible;
		Log.Print($"[Slide]   timeline: show={showTimeline} hide={hideTimeline} visible={_timelineContainer.Visible} modulate={_timelineContainer.Modulate.A:F2}");
		if (showTimeline)
		{
			_timelineContainer.Visible = true;
			_timelineContainer.Modulate = new Color(1, 1, 1, 0);
			_timelineWrapper.CustomMinimumSize = new Vector2(0, 0);
			_timelineWrapper.SizeFlagsVertical = SizeFlags.ShrinkBegin;
			var t = CreateTween();
			t.SetParallel();
			t.TweenProperty(_timelineWrapper, "custom_minimum_size", new Vector2(0, 250), 0.25f)
			 .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
			t.TweenProperty(_timelineContainer, "modulate", new Color(1, 1, 1, 1), 0.2f);
			Log.Print("[Timeline] Toolbar fade in (modulate 0→1, delay 0.1s)");
			_editToolbar.Modulate = new Color(1, 1, 1, 0);
			t.TweenProperty(_editToolbar, "modulate", new Color(1, 1, 1, 1), 0.2f)
			 .SetDelay(0.1f);
			t.Finished += () => Log.Print($"[TimelineLayout] wrapper={_timelineWrapper.Size} container={_timelineContainer.Size} timeline={_timeline.Size} parent={_timelineWrapper.GetParent<Control>().Size}");
		}
		else if (hideTimeline)
		{
			_timelineWrapper.SizeFlagsVertical = SizeFlags.ShrinkBegin;
			var t = CreateTween();
			t.SetParallel();
			t.TweenProperty(_timelineWrapper, "custom_minimum_size", new Vector2(0, 0), 0.2f)
			 .SetEase(Tween.EaseType.In).SetTrans(Tween.TransitionType.Cubic);
			t.TweenProperty(_timelineContainer, "modulate", new Color(1, 1, 1, 0), 0.15f);
			t.TweenProperty(_timelineContainer, "visible", false, 0);
		}

		// Fade overlay in/out
		bool showOverlay = (state == ViewState.Layout || state == ViewState.Edit);
		bool hideOverlay = !showOverlay && _overlay.Visible;
		if (showOverlay || hideOverlay)
		{
			var t = CreateTween();
			if (showOverlay)
			{
				_overlay.Visible = true;
				_overlay.Modulate = new Color(1, 1, 1, 0);
				t.TweenProperty(_overlay, "modulate", new Color(1, 1, 1, 1), 0.2f);
			}
			else
			{
				t.TweenProperty(_overlay, "modulate", new Color(1, 1, 1, 0), 0.15f);
				t.TweenProperty(_overlay, "visible", false, 0);
			}
		}
		if (state == ViewState.Layout)
		{
			UpdateLayoutRegionVisibility();
			_srcAspect.Ratio = 16f / 9f;
			_sourceDisplay.Material = null;
			_overlay.SetMode(OverlayMode.Layout);
			_outputPreview.SetPipInteractive(true);
			_overlay.ClearLayers();
			_layoutPlayBtn.Visible = true;
		}
		else if (state == ViewState.Edit)
		{
			_layoutPlayBtn.Visible = false;
			_srcAspect.Ratio = _outputPreview.CurrentRatio;
			_sourceDisplay.Material = _outputPreview.DisplayMaterial;
			_overlay.SetMode(OverlayMode.Editing);
			_outputPreview.SetPipInteractive(false);
			CallDeferred(nameof(UpdateTracks));
		}
		else
		{
			_layoutPlayBtn.Visible = false;
		}
		// Sidebar always visible; slide panel starts closed
		_slideOpen = false;
		_slideWrapper.CustomMinimumSize = new Vector2(0, 0);
		_toggleBtn.Visible = (state != ViewState.Import);
		_slideTabs.Visible = (state == ViewState.Edit);
		if (state == ViewState.Layout)
		{
			_mediaPanel.Visible = false;
			_inspectorPanel.Visible = true;
			_toggleBtn.Text = "Layout \u25B6";
		}
		else if (state == ViewState.Edit)
		{
			_mediaPanel.Visible = true;
			_inspectorPanel.Visible = false;
			_slideMediaTab.ButtonPressed = true;
			_toggleBtn.Text = "Media \u25B6";
		}
		_exportBtn.Visible = (state == ViewState.Edit);
		_continueBtn.Visible = (state == ViewState.Layout);
		if (state == ViewState.Edit)
		{
			_exportBtn.AddThemeStyleboxOverride("normal", _exportRed);
			_exportBtn.AddThemeColorOverride("font_color", Color.FromHtml("#FFFFFF"));
		}
		else
		{
			_exportBtn.AddThemeStyleboxOverride("normal", new StyleBoxFlat { BgColor = Color.FromHtml("#D9D9D9"), CornerRadiusTopLeft = 15, CornerRadiusTopRight = 15, CornerRadiusBottomLeft = 15, CornerRadiusBottomRight = 15, ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 8, ContentMarginBottom = 8 });
			_exportBtn.AddThemeColorOverride("font_color", Color.FromHtml("#11121C"));
		}
		RefreshStepIndicator();
		RebuildInspector();
	}

	private void AnimatePreviewIn()
	{
		var parent = _previewWrapper.GetParent<Control>();
		float h = parent.Size.Y;
		float targetH = _currentState == ViewState.Edit ? h - 250f : h;

		if (targetH <= 0f) return;

		var t = CreateTween();
		t.TweenProperty(_previewWrapper, "custom_minimum_size", new Vector2(0, targetH), 0.3f)
		 .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
		t.Finished += () =>
		{
			_previewWrapper.SizeFlagsVertical = SizeFlags.ExpandFill;
		};
	}

	private void RefreshStepIndicator()
	{
		foreach (Node n in _stepIndicator.GetChildren())
			n.QueueFree();

		ViewState[] states = { ViewState.Import, ViewState.Layout, ViewState.Edit };
		string[] labels = { "IMPORT", "LAYOUT", "EDIT" };
		var white = Color.FromHtml("#FFFFFF");
		var gray = Color.FromHtml("#D9D9D9");
		var bgWhite = new StyleBoxFlat { BgColor = Colors.White, CornerRadiusTopLeft = 10, CornerRadiusTopRight = 10, CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10 };
		var bgGray = new StyleBoxFlat { BgColor = gray, CornerRadiusTopLeft = 10, CornerRadiusTopRight = 10, CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10 };
		for (int i = 0; i < labels.Length; i++)
		{
			var idx = i;
			bool active = (int)_currentState == i;
			var btn = new Button
			{
				Text = labels[i],
				CustomMinimumSize = new Vector2(100, 40),
				Flat = true,
			};
			btn.AddThemeStyleboxOverride("normal", active ? bgWhite : bgGray);
			btn.AddThemeColorOverride("font_color", active ? Color.FromHtml("#D0570C") : Color.FromHtml("#555555"));
			btn.Pressed += () =>
			{
				if (idx == 0) SwitchToState(ViewState.Import);
				else if (idx == 1 && _projectBin.Count > 0) SwitchToState(ViewState.Layout);
				else if (idx == 2 && _projectBin.Count > 0) SwitchToState(ViewState.Edit);
			};
			_stepIndicator.AddChild(btn);
		}
	}

	private void BuildSidebarButton(string text, Action callback)
	{
		var btn = new Button
		{
			Text = text,
			CustomMinimumSize = new Vector2(0, 46),
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			TooltipText = text,
		};
		var norm = new StyleBoxFlat { BgColor = Color.FromHtml("#D0570C"), CornerRadiusTopLeft = 15, CornerRadiusTopRight = 15, CornerRadiusBottomLeft = 15, CornerRadiusBottomRight = 15, ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 8, ContentMarginBottom = 8 };
		var hover = new StyleBoxFlat { BgColor = Color.FromHtml("#e0661a"), CornerRadiusTopLeft = 15, CornerRadiusTopRight = 15, CornerRadiusBottomLeft = 15, CornerRadiusBottomRight = 15, ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 8, ContentMarginBottom = 8 };
		btn.AddThemeStyleboxOverride("normal", norm);
		btn.AddThemeStyleboxOverride("hover", hover);
		btn.AddThemeStyleboxOverride("pressed", hover);
		btn.AddThemeStyleboxOverride("disabled", norm);
		btn.AddThemeColorOverride("font_color", Color.FromHtml("#FFFFFF"));
		btn.Pressed += callback;
		_sideVbox.AddChild(btn);
	}

	private void ToggleSlidePanel()
	{
		_slideOpen = !_slideOpen;
		if (_slideOpen)
		{
			var t = CreateTween();
			t.TweenProperty(_slideWrapper, "custom_minimum_size", new Vector2(240, 0), 0.25f).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
			_toggleBtn.Text = (_currentState == ViewState.Layout ? "Layout" : "Media") + " \u25C2";
		}
		else
		{
			var t = CreateTween();
			t.TweenProperty(_slideWrapper, "custom_minimum_size", new Vector2(0, 0), 0.2f).SetEase(Tween.EaseType.In).SetTrans(Tween.TransitionType.Cubic);
			_toggleBtn.Text = (_currentState == ViewState.Layout ? "Layout" : "Media") + " \u25B6";
		}
	}

	private void BuildTimelineToolbar(VBoxContainer parent)
	{
		_editToolbar = new HBoxContainer();
		_editToolbar.AddThemeConstantOverride("separation", 10);
		parent.AddChild(_editToolbar);

		Button MakeToolBtn(string text, string tooltip)
		{
			var b = new Button { Text = text, TooltipText = tooltip, FocusMode = FocusModeEnum.None };
			return b;
		}

		_playBtn = MakeToolBtn("Play", "Play / Pause (Space)");
		_playBtn.Pressed += () => SetPlayback(!_isPlaying, false);

		var pauseMoveBtn = MakeToolBtn("Pause & Move", "Stop, move playhead here (Enter / K)");
		pauseMoveBtn.Pressed += () => SetPlayback(false, true);

		_editToolbar.AddChild(_playBtn);
		_editToolbar.AddChild(pauseMoveBtn);
		_editToolbar.AddChild(new VSeparator());

		var toolGroup = new ButtonGroup();
		var selectBtn = MakeToolBtn("Select", "Select tool (V)");
		selectBtn.ToggleMode = true; selectBtn.ButtonGroup = toolGroup; selectBtn.ButtonPressed = true;
		selectBtn.Toggled += (on) => { if (on) _timeline.CurrentTool = TimelineTool.Select; };
		var razorBtn = MakeToolBtn("Razor", "Split tool (R)");
		razorBtn.ToggleMode = true; razorBtn.ButtonGroup = toolGroup;
		razorBtn.Toggled += (on) => { if (on) _timeline.CurrentTool = TimelineTool.Razor; };

		_editToolbar.AddChild(selectBtn);
		_editToolbar.AddChild(razorBtn);
		_editToolbar.AddChild(new VSeparator());

		var prevFrame = MakeToolBtn("<<", "Previous frame (Shift+Left)");
		prevFrame.Pressed += () => StepTimeline(-1, false);
		var nextFrame = MakeToolBtn(">>", "Next frame (Shift+Right)");
		nextFrame.Pressed += () => StepTimeline(1, false);
		_editToolbar.AddChild(prevFrame);
		_editToolbar.AddChild(nextFrame);
		_editToolbar.AddChild(new VSeparator());

		var stepBack = MakeToolBtn("-5%", "Jump back 5% (Left)");
		stepBack.Pressed += () => StepTimeline(-1, true);
		var stepFwd = MakeToolBtn("+5%", "Jump forward 5% (Right)");
		stepFwd.Pressed += () => StepTimeline(1, true);
		_editToolbar.AddChild(stepBack);
		_editToolbar.AddChild(stepFwd);
		_editToolbar.AddChild(new VSeparator());

		_positionLabel = new Label { Text = "0:00 / 0:00", HorizontalAlignment = HorizontalAlignment.Right, CustomMinimumSize = new Vector2(140, 0) };
		_editToolbar.AddChild(_positionLabel);
		_editToolbar.AddChild(new VSeparator());

		var loopBtn = MakeToolBtn("Loop", "Loop Region On/Off");
		loopBtn.ToggleMode = true;
		loopBtn.Toggled += (on) => { _timeline.LoopEnabled = on; _loopPlayback = on; };
		_editToolbar.AddChild(loopBtn);
		_editToolbar.AddChild(new VSeparator());

		var eyeBtn = MakeToolBtn("👁", "Toggle Result Preview");
		eyeBtn.ToggleMode = true; eyeBtn.ButtonPressed = true;
		eyeBtn.Toggled += (on) => _resVbox.Visible = on;
		_editToolbar.AddChild(eyeBtn);
		_editToolbar.AddChild(new VSeparator());

		var addVBtn = MakeToolBtn("+V", "Add Video Track");
		addVBtn.Pressed += () => AddTrack(TrackType.Video);
		_editToolbar.AddChild(addVBtn);
		var addABtn = MakeToolBtn("+A", "Add Audio Track");
		addABtn.Pressed += () => AddTrack(TrackType.Audio);
		_editToolbar.AddChild(addABtn);

		}


	private void BuildMediaBinTab(VBoxContainer parent)
	{
		parent.AddChild(new Label { Text = "PROJECT MEDIA", Modulate = Color.FromHtml("#D0570C") });

		// Search filter
		var searchBox = new LineEdit
		{
			PlaceholderText = "Search media...",
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
		};
		searchBox.TextChanged += _ => { _binSearchFilter = searchBox.Text; RefreshBinUI(); };
		parent.AddChild(searchBox);

		_binUI = new ItemList
		{
			SizeFlagsVertical = SizeFlags.ExpandFill,
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			SelectMode = ItemList.SelectModeEnum.Single,
		};
		_binUI.ItemActivated += (idx) =>
		{
			int i = (int)idx;
			if (i >= 0 && i < _binFilteredIndices.Count)
			{
				AddAssetToTimeline(_projectBin[_binFilteredIndices[i]]);
				_binUI.DeselectAll();
			}
		};
		// ItemList delete + drag handlers
		_binUI.GuiInput += (ev) =>
		{
			if (ev is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
			{
				if (mb.Pressed)
				{
					var sel = _binUI.GetSelectedItems();
					_binDragPressed = sel.Length > 0;
					_binDragStartPos = mb.Position;
					_binDragItemIndex = sel.Length > 0 ? _binFilteredIndices[sel[0]] : -1;
				}
				else
				{
					_binDragPressed = false;
				}
			}
			else if (ev is InputEventMouseMotion mm && _binDragPressed && _binDragItemIndex >= 0)
			{
				if (mm.Position.DistanceTo(_binDragStartPos) > 10)
				{
					_binDragPressed = false;
					var data = new Godot.Collections.Dictionary { ["asset_index"] = _binDragItemIndex };
					var preview = new Label { Text = _projectBin[_binDragItemIndex].Name };
					_binUI.ForceDrag(data, preview);
					AcceptEvent();
					return;
				}
			}

			if (ev is InputEventKey k && k.Pressed && k.Keycode == Key.Delete)
			{
				if (_currentState == ViewState.Edit && _timeline.GetSelectedIndices().Length > 0)
					return;
				var sel = _binUI.GetSelectedItems();
				if (sel.Length > 0)
				{
					int idx = sel[0];
					if (idx >= 0 && idx < _binFilteredIndices.Count)
					{
						int realIdx = _binFilteredIndices[idx];
						if (realIdx >= 0 && realIdx < _projectBin.Count)
						{
							SnapshotState();
							var asset = _projectBin[realIdx];
							_projectBin.RemoveAt(realIdx);
							RemoveAssetFromTimeline(asset);
							RefreshBinUI();
							GetViewport().SetInputAsHandled();
						}
					}
				}
			}
		};
		parent.AddChild(_binUI);

		parent.AddChild(new HSeparator());

		var btnRow = new HBoxContainer();
		btnRow.AddThemeConstantOverride("separation", 6);
		var importBtn = new Button { Text = "Import", SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "Import video/audio file (Ctrl+I)" };
		importBtn.Pressed += () => _fileDialog.PopupCentered();
		btnRow.AddChild(importBtn);
		var textBtn = new Button { Text = "Text", SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "Add a text clip (Ctrl+T)" };
		textBtn.Pressed += OnAddTextClip;
		btnRow.AddChild(textBtn);
		var sfxBtn = new Button { Text = "SFX", SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "Add sound effect" };
		sfxBtn.Pressed += OpenSoundBrowserWindow;
		btnRow.AddChild(sfxBtn);
		parent.AddChild(btnRow);

		parent.AddChild(new HSeparator());

		var capsBtn = new Button { Text = "Generate Captions", SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "Auto-transcribe with Whisper (Ctrl+G)" };
		capsBtn.Pressed += OnGenerateCaptions;
		parent.AddChild(capsBtn);

		var stickerBtn = new Button { Text = "Add Image/GIF", SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "Open sticker/emote browser" };
		stickerBtn.Pressed += OpenImageBrowserWindow;
		parent.AddChild(stickerBtn);
	}


	// Deep-clone the current track state for undo. Copies every TrackData and
	// TrackClipData to avoid reference sharing between undo stack and live state.
	private void SnapshotState()
	{
		var copy = _tracks.Select(t => new TrackData
		{
			Name = t.Name,
			Type = t.Type,
			Muted = t.Muted,
			ZIndex = t.ZIndex,
			Clips = t.Clips.Select(c => c.Clone()).ToList(),
		}).ToList();
		_undoStack.Push(copy);
		_redoStack.Clear();  // New action invalidates redo history
	}

	private void ShowClipContextMenu(int flatIdx, Vector2 globalPos)
	{
		var menu = new PopupMenu();
		menu.Position = (Vector2I)globalPos;
		menu.AddItem("Copy", 0);
		menu.AddItem("Cut", 1);
		menu.AddItem("Split", 2);
		menu.AddItem("Delete", 3);
		menu.AddSeparator();
		menu.AddItem("Add Keyframe", 4);
		menu.AddItem("Remove Keyframe", 5);
		menu.AddSeparator();
		menu.AddItem("Paste", 6);
		menu.IdPressed += (id) =>
		{
			switch (id)
			{
				case 0: CopySelected(); break;
				case 1: CutSelected(); break;
				case 2: SplitAtPlayhead(); break;
				case 3: DeleteSelected(); break;
				case 4: AddKeyframeAtPlayhead(); break;
				case 5: RemoveKeyframeAtPlayhead(); break;
				case 6: Paste(); break;
			}
		};
		menu.PopupHide += menu.QueueFree;
		AddChild(menu);
		menu.Popup();
	}

	// Pop from undo stack → push current state to redo stack → restore
	private void Undo()
	{
		if (_undoStack.Count == 0) return;
		var current = _tracks.Select(t => new TrackData
		{
			Name = t.Name, Type = t.Type, Muted = t.Muted, ZIndex = t.ZIndex,
			Clips = t.Clips.Select(c => c.Clone()).ToList(),
		}).ToList();
		_redoStack.Push(current);
		_tracks = _undoStack.Pop();
		UpdateTracks();
		SetStatus($"Undo ({_undoStack.Count} left)", Color.FromHtml("#D0570C"));
	}

	// Pop from redo stack → push current state to undo stack → restore
	private void Redo()
	{
		if (_redoStack.Count == 0) return;
		var current = _tracks.Select(t => new TrackData
		{
			Name = t.Name, Type = t.Type, Muted = t.Muted, ZIndex = t.ZIndex,
			Clips = t.Clips.Select(c => c.Clone()).ToList(),
		}).ToList();
		_undoStack.Push(current);
		_tracks = _redoStack.Pop();
		UpdateTracks();
		SetStatus($"Redo ({_redoStack.Count} left)", Color.FromHtml("#D0570C"));
	}

	private void AddTrack(TrackType type)
	{
		SnapshotState();
		int n = type == TrackType.Video
			? 1 + _tracks.Count(t => t.Type == TrackType.Video)
			: 1 + _tracks.Count(t => t.Type == TrackType.Audio);
		var track = new TrackData
		{
			Name = $"{(type == TrackType.Video ? "Video" : "Audio")} {n}",
			Type = type,
		};
		if (type == TrackType.Video)
		{
			int audioIdx = _tracks.FindIndex(t => t.Type == TrackType.Audio);
			if (audioIdx >= 0)
				_tracks.Insert(audioIdx, track);
			else
				_tracks.Add(track);
		}
		else
		{
			_tracks.Add(track);
		}
		UpdateTracks();
		SetStatus($"Added: {track.Name}", Color.FromHtml("#D0570C"));
	}

	private void UpdateLayoutRegionVisibility()
	{
		bool is16_9 = ExportAspectRatio == "16:9";
		foreach (var reg in _overlay.Regions)
		{
			if (reg.Name == "UI")
				reg.Visible = !is16_9 && _outputPreview.LayoutMode == 2;
			else
				reg.Visible = !is16_9;
		}
		_overlay.QueueRedraw();
	}

	// When aspect ratio changes while in Game UI mode, toggle the content
	// bounding box (sub-rectangle crop) — only needed for non-16:9 outputs.
	private void UpdateGameUiContentOutput()
	{
		if (_outputPreview.LayoutMode != 2) return;
		if (ExportAspectRatio == "16:9")
			_outputPreview.SetContentOutput(new Vector4(0, 0, 1, 1));
		else
			_outputPreview.SetContentOutput(new Vector4(0, 0.353495f, 1, 0.609417f));
	}

	private void SetStatus(string msg, Color? color = null)
	{
		_statusLabel.Text = $" {msg}";
		if (color.HasValue) _statusLabel.Modulate = color.Value;

		_statusTween?.Kill();
		_statusTween = CreateTween();
		_statusTween.TweenInterval(10);
		_statusTween.TweenProperty(_statusLabel, "modulate", new Color(0.5f, 0.5f, 0.5f), 0.5f);
	}

	// Modal popup listing all keyboard shortcuts with key/description pairs
	private void ShowShortcutHelp()
	{
		var win = new Window
		{
			Title = "Keyboard Shortcuts",
			Size = new Vector2I(420, 480),
			InitialPosition = Window.WindowInitialPosition.CenterPrimaryScreen,
			Transient = true,
			Exclusive = true,
		};
		var bg = new PanelContainer();
		bg.SetAnchorsPreset(LayoutPreset.FullRect);
		win.AddChild(bg);
		var vbox = new VBoxContainer();
		vbox.AddThemeConstantOverride("margin_left", 15);
		vbox.AddThemeConstantOverride("margin_right", 15);
		vbox.AddThemeConstantOverride("margin_top", 15);
		vbox.AddThemeConstantOverride("separation", 6);
		bg.AddChild(vbox);
		vbox.AddChild(new Label { Text = "KEYBOARD SHORTCUTS", Modulate = Color.FromHtml("#D0570C") });

		var shortcuts = new (string key, string desc)[]
		{
			("Space", "Play / Pause"),
			("Enter / K", "Pause & move selection here"),
			("V", "Select tool"),
			("R", "Razor (split) tool"),
			("S", "Split clip at playhead"),
			("Shift+Left / Right", "Step frame"),
			("Left / Right", "Jump 5% of view"),
			("Ctrl+Z", "Undo"),
			("Ctrl+Shift+Z / Ctrl+Y", "Redo"),
			("Delete", "Delete selected clip"),
			("Ctrl+I", "Import file"),
			("Ctrl+T", "Add text clip"),
			("Ctrl+G", "Generate captions"),
			("Ctrl+E", "Export video"),
			("Shift+Scroll", "Zoom timeline"),
			("Scroll", "Pan timeline"),
			("? / Ctrl+/", "This help"),
		};
		var grid = new GridContainer { Columns = 2 };
		grid.AddThemeConstantOverride("h_separation", 12);
		grid.AddThemeConstantOverride("v_separation", 4);
		foreach (var (key, desc) in shortcuts)
		{
			grid.AddChild(new Label { Text = key, Modulate = Color.FromHtml("#f78166") });
			grid.AddChild(new Label { Text = desc });
		}
		vbox.AddChild(grid);

		var closeBtn = new Button { Text = "Close", SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
		closeBtn.Pressed += () => win.BounceOutThenFree();
		vbox.AddChild(closeBtn);

		AddChild(win);
		win.Popup();
		win.CloseRequested += () => win.BounceOutThenFree();
		win.BounceIn();
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventKey k && k.Pressed)
		{
			// Left/Right arrows: 5% jump, Shift+arrows: frame step
			if (k.Keycode == Key.Left || k.Keycode == Key.Right)
			{
				int dir = k.Keycode == Key.Right ? 1 : -1;
				StepTimeline(dir, !k.ShiftPressed);
				GetViewport().SetInputAsHandled();
				return;
			}
			// Up/Down arrows: prevent UI focus navigation
			if (k.Keycode == Key.Up || k.Keycode == Key.Down)
			{
				GetViewport().SetInputAsHandled();
				return;
			}
			if (k.Keycode == Key.Z && k.CtrlPressed && !k.ShiftPressed)
			{
				Undo();
				GetViewport().SetInputAsHandled();
				return;
			}
			if ((k.Keycode == Key.Y && k.CtrlPressed) || (k.Keycode == Key.Z && k.CtrlPressed && k.ShiftPressed))
			{
				Redo();
				GetViewport().SetInputAsHandled();
				return;
			}
			if (k.Keycode == Key.C && k.CtrlPressed && !k.ShiftPressed)
			{
				CopySelected();
				GetViewport().SetInputAsHandled();
				return;
			}
			if (k.Keycode == Key.X && k.CtrlPressed && !k.ShiftPressed)
			{
				CutSelected();
				GetViewport().SetInputAsHandled();
				return;
			}
			if (k.Keycode == Key.V && k.CtrlPressed && !k.ShiftPressed)
			{
				Paste();
				GetViewport().SetInputAsHandled();
				return;
			}

			// Delete: delete selected timeline clip or bin item
			if (k.Keycode == Key.Delete)
			{
				if (_currentState == ViewState.Edit && _timeline.GetSelectedIndices().Length > 0)
				{
					_pendingDeleteClips = true;
					_confirmDialog.DialogText = $"Delete {_timeline.GetSelectedIndices().Length} selected clip(s)?";
					_confirmDialog.PopupCentered(new Vector2I(350, 0));
					if (!_confirmDialog.IsConnected(AcceptDialog.SignalName.Confirmed, Callable.From(ExecuteClipDelete)))
						_confirmDialog.Confirmed += ExecuteClipDelete;
					GetViewport().SetInputAsHandled();
				}
				else
				{
					var binSel = _binUI.GetSelectedItems();
					if (binSel.Length > 0)
					{
						int idx = binSel[0];
						if (idx >= 0 && idx < _binFilteredIndices.Count)
						{
							int realIdx = _binFilteredIndices[idx];
							if (realIdx >= 0 && realIdx < _projectBin.Count)
							{
								var asset = _projectBin[realIdx];
								_pendingBinDeleteIdx = realIdx;
								_confirmDialog.DialogText = $"Remove \"{asset.Name}\" from the project bin?\nThis will also remove all its clips from the timeline.";
								_confirmDialog.PopupCentered(new Vector2I(400, 0));
								if (!_confirmDialog.IsConnected(AcceptDialog.SignalName.Confirmed, Callable.From(ExecuteBinDelete)))
									_confirmDialog.Confirmed += ExecuteBinDelete;
								GetViewport().SetInputAsHandled();
							}
						}
					}
				}
				return;
			}

			// Space: toggle play/pause
			if (k.Keycode == Key.Space)
			{
				SetPlayback(!_isPlaying, false);
				GetViewport().SetInputAsHandled();
				return;
			}

			// Enter / K: pause and move playhead
			if (k.Keycode == Key.Enter || k.Keycode == Key.K)
			{
				SetPlayback(false, true);
				GetViewport().SetInputAsHandled();
				return;
			}

			// V: Select tool
			if (k.Keycode == Key.V && !k.CtrlPressed)
			{
				_timeline.CurrentTool = TimelineTool.Select;
				GetViewport().SetInputAsHandled();
				return;
			}

			// R: Razor/split tool
			if (k.Keycode == Key.R && !k.CtrlPressed)
			{
				_timeline.CurrentTool = TimelineTool.Razor;
				GetViewport().SetInputAsHandled();
				return;
			}

			// S: Split at playhead
			if (k.Keycode == Key.S && !k.CtrlPressed && !k.ShiftPressed)
			{
				SplitAtPlayhead();
				GetViewport().SetInputAsHandled();
				return;
			}

			// ?: Show shortcut help
			if (k.Keycode == Key.Slash && k.ShiftPressed)
			{
				ShowShortcutHelp();
				GetViewport().SetInputAsHandled();
				return;
			}

			// Ctrl+E: Export
			if (k.Keycode == Key.E && k.CtrlPressed)
			{
				OnExportPressed();
				GetViewport().SetInputAsHandled();
				return;
			}

			// Ctrl+T: Add text clip
			if (k.Keycode == Key.T && k.CtrlPressed)
			{
				OnAddTextClip();
				GetViewport().SetInputAsHandled();
				return;
			}

			// Ctrl+G: Generate captions
			if (k.Keycode == Key.G && k.CtrlPressed && !k.ShiftPressed)
			{
				OnGenerateCaptions();
				GetViewport().SetInputAsHandled();
				return;
			}

			// Ctrl+D: duplicate selected clip
			if (k.Keycode == Key.D && k.CtrlPressed)
			{
				DuplicateSelectedClips();
				GetViewport().SetInputAsHandled();
				return;
			}

			// Ctrl+A: select all clips
			if (k.Keycode == Key.A && k.CtrlPressed && _currentState == ViewState.Edit)
			{
				_timeline.SelectAllClips();
				GetViewport().SetInputAsHandled();
				return;
			}

			// Ctrl+= / Ctrl+-: zoom in/out
			if (k.Keycode == Key.Equal && k.CtrlPressed)
			{
				_timeline.Zoom = Mathf.Clamp(_timeline.Zoom * 1.3, 1.0, 5000.0);
				_timeline.QueueRedraw();
				GetViewport().SetInputAsHandled();
				return;
			}
			if (k.Keycode == Key.Minus && k.CtrlPressed)
			{
				_timeline.Zoom = Mathf.Clamp(_timeline.Zoom / 1.3, 1.0, 5000.0);
				_timeline.QueueRedraw();
				GetViewport().SetInputAsHandled();
				return;
			}

			// Ctrl+1/2/3: switch steps
			if (k.CtrlPressed && k.Keycode >= Key.Key1 && k.Keycode <= Key.Key3)
			{
				int step = (int)(k.Keycode - Key.Key1);
				if (step == 0) SwitchToState(ViewState.Import);
				else if (step == 1) SwitchToState(ViewState.Layout);
				else if (step == 2) SwitchToState(ViewState.Edit);
				GetViewport().SetInputAsHandled();
				return;
			}

			// Ctrl+Shift+C: toggle external debug console
			if (k.Keycode == Key.C && k.CtrlPressed && k.ShiftPressed)
			{
				DebugConsole.Toggle();
				GetViewport().SetInputAsHandled();
				return;
			}

			// Ctrl+I: import media
			if (k.Keycode == Key.I && k.CtrlPressed)
			{
				_fileDialog.PopupCentered();
				GetViewport().SetInputAsHandled();
				return;
			}

			// Ctrl+O: open project
			if (k.Keycode == Key.O && k.CtrlPressed)
			{
				_openProjectDialog.PopupCentered();
				GetViewport().SetInputAsHandled();
				return;
			}

			// Ctrl+S: save project
			if (k.Keycode == Key.S && k.CtrlPressed)
			{
				_saveDialog.PopupCentered();
				GetViewport().SetInputAsHandled();
				return;
			}

			// Ctrl+Shift+F: open SFX browser
			if (k.Keycode == Key.F && k.CtrlPressed && k.ShiftPressed)
			{
				OpenSoundBrowserWindow();
				GetViewport().SetInputAsHandled();
				return;
			}

			// Ctrl+Shift+G: open Image/GIF browser
			if (k.Keycode == Key.G && k.CtrlPressed && k.ShiftPressed)
			{
				OpenImageBrowserWindow();
				GetViewport().SetInputAsHandled();
				return;
			}

			// Ctrl+,: open Settings
			if (k.Keycode == Key.Comma && k.CtrlPressed)
			{
				var dlg = new SettingsDialog
				{
					CurrentOutputDir = ExportOutputDir,
					CurrentNormalizeAudio = ExportNormalizeAudio,
					CurrentCaptionLanguage = ExportCaptionLanguage,
				};
				dlg.Confirmed += () =>
				{
					ExportNormalizeAudio = dlg.NormalizeAudio;
					ExportOutputDir = dlg.OutputDir;
					ExportCaptionLanguage = dlg.CaptionLanguage;
					AppConfig.CaptionLanguage = dlg.CaptionLanguage;
					AppConfig.ExportOutputDir = dlg.OutputDir;
					AppConfig.SaveSettings();
				};
				AddChild(dlg);
				dlg.PopupCentered();
				GetViewport().SetInputAsHandled();
				return;
			}
		}
	}
}
