// Dynamic inspector panel: builds property editors for Layout (aspect ratio,
// templates, blur, auto-frame) and Edit (position, size, opacity, trim, fade,
// volume, text properties) modes.

using Godot;
using System;
using System.Linq;

namespace VelosCCS;

public partial class MainWindow
{
	// Clear and rebuild the Inspector tab depending on current state
	private void RebuildInspector()
	{
		foreach (var child in _inspectorList.GetChildren())
			child.QueueFree();

		if (_currentState == ViewState.Layout)
		{
			BuildLayoutInspector();
		}
		else if (_currentState == ViewState.Edit)
		{
			if (_selTrackIdx < 0 || _selClipIdx < 0)
			{
				_inspectorList.AddChild(new Label { Text = "Select a clip on the timeline", Modulate = new Color(0.5f, 0.5f, 0.5f) });
				return;
			}
			var clip = _tracks[_selTrackIdx].Clips[_selClipIdx];
			BuildClipInspector(_inspectorList, clip);
		}
	}

	// Layout mode inspector: aspect ratio buttons, layout templates, social overlay,
	// background blur slider/toggle, auto-frame button, and Continue to Edit button.
	private void BuildLayoutInspector()
	{
		_inspectorList.AddChild(new Label { Text = "ASPECT RATIO", Modulate = Color.FromHtml("#D0570C") });
		var ratioGrid = new GridContainer { Columns = 2 };
		_inspectorList.AddChild(ratioGrid);

		string[] ratios = { "9:16", "16:9", "1:1", "4:5" };
		foreach (var r in ratios)
		{
			var btn = new Button { Text = r, CustomMinimumSize = new Vector2(0, 40) };
			bool isSelected = ExportAspectRatio == r;
			if (isSelected) btn.Modulate = new Color(0.345f, 0.651f, 1.0f); // blue highlight
			btn.Pressed += () =>
			{
				Log.Print($"[UI] Button: Aspect {r}");
				GD.Print($"[Inspector] Aspect ratio clicked: {r}");
				_outputPreview.SetAspectRatio(r);
				ExportAspectRatio = r;
				UpdateLayoutRegionVisibility();
				UpdateGameUiContentOutput();
				if (r != "16:9")
				{
					ApplyLayoutPreset("Basic");
					_currentLayoutPreset = "Basic";
				}
				else
				{
					string[] layoutTracks = { "Basic Facecam", "Camera", "UI Content" };
					_tracks.RemoveAll(t => layoutTracks.Contains(t.Name));
					_currentLayoutPreset = "";
					UpdateTracks();
				}
				RebuildInspector(); // re-highlight buttons
			};
			ratioGrid.AddChild(btn);
		}

		_inspectorList.AddChild(new HSeparator());
		_inspectorList.AddChild(new Label { Text = "TEMPLATE", Modulate = Color.FromHtml("#D0570C") });
		var templates = new[] {
			("Basic", "Cam in corner"),
			("Circle Facecam", "Circle mask"),
			("Game UI", "Vertical stack"),
		};
		foreach (var p in templates)
		{
			var btn = new Button { Text = p.Item1, TooltipText = p.Item2, CustomMinimumSize = new Vector2(0, 44) };
			bool isSelected = _currentLayoutPreset == p.Item1;
			if (isSelected) btn.Modulate = new Color(0.345f, 0.651f, 1.0f);
			btn.Pressed += () => { Log.Print($"[UI] Button: Template {p.Item1}"); ApplyLayoutPreset(p.Item1); _currentLayoutPreset = p.Item1; RebuildInspector(); };
			_inspectorList.AddChild(btn);
		}

		_inspectorList.AddChild(new HSeparator());
		_inspectorList.AddChild(new Label { Text = "SOCIAL OVERLAY", Modulate = Color.FromHtml("#D0570C") });
		var overlayOptions = new OptionButton();
		overlayOptions.AddItem("None", 0);
		overlayOptions.AddItem("TikTok", 1);
		overlayOptions.AddItem("YouTube Shorts", 2);
		overlayOptions.AddItem("Instagram Reels", 3);
		overlayOptions.ItemSelected += (idx) =>
		{
			if (idx == 0) _outputPreview.SetSocialOverlay("None");
			else if (idx == 1) _outputPreview.SetSocialOverlay("tiktok");
			else if (idx == 2) _outputPreview.SetSocialOverlay("shorts");
			else if (idx == 3) _outputPreview.SetSocialOverlay("reels");
		};
		_inspectorList.AddChild(overlayOptions);

		_inspectorList.AddChild(new HSeparator());
		_inspectorList.AddChild(new Label { Text = "BLUR", Modulate = Color.FromHtml("#D0570C") });
		var blurSlider = new HSlider { MinValue = 0, MaxValue = 10, Step = 0.5f, Value = 2.5f };
		blurSlider.ValueChanged += (v) => _outputPreview.SetBlur((float)v);
		_inspectorList.AddChild(blurSlider);
		var blurToggle = new CheckBox { Text = "Blur Background", ButtonPressed = _outputPreview.BlurBg };
		blurToggle.Toggled += (on) => _outputPreview.SetBlurBg(on);
		_inspectorList.AddChild(blurToggle);

		_inspectorList.AddChild(new HSeparator());
		var autoFrameBtn = new Button { Text = "Auto-frame (Face Detect)", CustomMinimumSize = new Vector2(0, 44) };
		autoFrameBtn.Pressed += () => { Log.Print("[UI] Button: Auto-frame"); OnAutoFrame(); };
		_inspectorList.AddChild(autoFrameBtn);

	}

	// Edit mode inspector for a selected clip: position, size, opacity, trim,
	// fade in/out, volume, and text properties (if applicable)
	private void BuildClipInspector(VBoxContainer parent, TrackClipData clip)
	{
		parent.AddChild(new Label { Text = "CLIP PROPERTIES", Modulate = Color.FromHtml("#D0570C") });

		var grid = new GridContainer { Columns = 2 };
		grid.AddThemeConstantOverride("h_separation", 8);
		grid.AddThemeConstantOverride("v_separation", 4);
		parent.AddChild(grid);

		AddGridField(grid, "X", clip.PosX.StaticValue, 0f, 1f, 0.01f, v =>
		{
			clip.PosX.StaticValue = v;
			clip.Position = new Vector2(v, clip.Position.Y);
			RefreshClipViews();
		}, clip.PosX, clip.Start);
		AddGridField(grid, "Y", clip.PosY.StaticValue, 0f, 1f, 0.01f, v =>
		{
			clip.PosY.StaticValue = v;
			clip.Position = new Vector2(clip.Position.X, v);
			RefreshClipViews();
		}, clip.PosY, clip.Start);
		AddGridField(grid, "Scale", clip.Scale.StaticValue, 0.1f, 3f, 0.05f, v =>
		{
			clip.Scale.StaticValue = v;
			RefreshClipViews();
		}, clip.Scale, clip.Start);
		AddGridField(grid, "Opacity", clip.Opacity.StaticValue, 0f, 1f, 0.01f, v =>
		{
			clip.Opacity.StaticValue = v;
			RefreshClipViews();
		}, clip.Opacity, clip.Start);
		AddGridField(grid, "Rotation", clip.Rotation.StaticValue, 0f, 360f, 1f, v =>
		{
			clip.Rotation.StaticValue = v;
			RefreshClipViews();
		}, clip.Rotation, clip.Start);

		parent.AddChild(new HSeparator());
		parent.AddChild(new Label { Text = "TRIM", Modulate = Color.FromHtml("#D0570C") });

		var trimGrid = new GridContainer { Columns = 2 };
		trimGrid.AddThemeConstantOverride("h_separation", 8);
		trimGrid.AddThemeConstantOverride("v_separation", 4);
		parent.AddChild(trimGrid);

		AddGridField(trimGrid, "Start", (float)clip.Start, 0f, (float)_videoDuration, 0.1f, v =>
		{
			clip.Start = v;
			_timeline.QueueRedraw();
		});
		AddGridField(trimGrid, "End", (float)clip.End, 0f, (float)_videoDuration, 0.1f, v =>
		{
			clip.End = v;
			_timeline.QueueRedraw();
		});

		parent.AddChild(new HSeparator());
		parent.AddChild(new Label { Text = "FADE", Modulate = Color.FromHtml("#D0570C") });

		var fadeGrid = new GridContainer { Columns = 2 };
		fadeGrid.AddThemeConstantOverride("h_separation", 8);
		fadeGrid.AddThemeConstantOverride("v_separation", 4);
		parent.AddChild(fadeGrid);

		AddGridField(fadeGrid, "Fade In", (float)clip.FadeIn, 0f, 10f, 0.1f, v =>
		{
			clip.FadeIn = v;
			RefreshClipViews();
		});
		AddGridField(fadeGrid, "Fade Out", (float)clip.FadeOut, 0f, 10f, 0.1f, v =>
		{
			clip.FadeOut = v;
			RefreshClipViews();
		});

		if (clip.ClipType is ClipType.Audio or ClipType.SourceVideo)
		{
			parent.AddChild(new HSeparator());
			parent.AddChild(new Label { Text = "AUDIO", Modulate = Color.FromHtml("#D0570C") });

			var audioGrid = new GridContainer { Columns = 2 };
			audioGrid.AddThemeConstantOverride("h_separation", 8);
			audioGrid.AddThemeConstantOverride("v_separation", 4);
			parent.AddChild(audioGrid);

			AddGridField(audioGrid, "Volume", clip.Volume.StaticValue, 0f, 2f, 0.01f, v =>
			{
				clip.Volume.StaticValue = v;
			});
		}

		if (clip.ClipType == ClipType.Text)
		{
			parent.AddChild(new HSeparator());
			BuildTextInspector(parent, clip);
		}
	}

	// Text-specific inspector: edit text content, font size, font selection,
	// foreground color, outline color/width
	private void BuildTextInspector(VBoxContainer parent, TrackClipData clip)
	{
		parent.AddChild(new Label { Text = "TEXT", Modulate = Color.FromHtml("#D0570C") });

		var tb = new LineEdit { Text = clip.Text, PlaceholderText = "Type here..." };
		tb.TextChanged += (t) => { clip.Text = t; RefreshClipViews(); };
		parent.AddChild(tb);

		// Text keyframe button
		var txKfHbox = new HBoxContainer();
		var txKfBtn = new Button
		{
			Text = "◇ Text Keyframe",
			CustomMinimumSize = new Vector2(0, 30),
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
		};
		txKfBtn.Pressed += () =>
		{
			Log.Print("[UI] Button: Text keyframe");
			SnapshotState();
			double lt = _timeline.SelectionPos - clip.Start;
			int idx = clip.TextKeyframes.FindIndex(k => Math.Abs(k.Time - lt) < 0.01);
			if (idx >= 0)
			{
				clip.TextKeyframes.RemoveAt(idx);
			}
			else
			{
				clip.TextKeyframes.Add(new TextKeyframe { Time = lt, Text = clip.Text, FontPath = clip.FontPath });
			}
			clip.TextKeyframes = clip.TextKeyframes.OrderBy(k => k.Time).ToList();
			UpdateTracks();
		};
		txKfHbox.AddChild(txKfBtn);
		parent.AddChild(txKfHbox);

		var textGrid = new GridContainer { Columns = 2 };
		textGrid.AddThemeConstantOverride("h_separation", 8);
		textGrid.AddThemeConstantOverride("v_separation", 4);
		parent.AddChild(textGrid);

		AddGridField(textGrid, "Font Size", clip.FontSize, 8, 200, 1, v =>
		{
			clip.FontSize = (int)v;
			clip.FontSizeAnim.StaticValue = v;
			RefreshClipViews();
		}, clip.FontSizeAnim, clip.Start);

		textGrid.AddChild(new Label { Text = "Font" });
		var fontBtn = new Button
		{
			Text = string.IsNullOrEmpty(clip.FontPath) ? "Select Font..." : System.IO.Path.GetFileNameWithoutExtension(clip.FontPath),
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			CustomMinimumSize = new Vector2(0, 30),
		};
		fontBtn.Pressed += () => { Log.Print("[UI] Button: Font select"); OpenFontBrowserWindow(); };
		textGrid.AddChild(fontBtn);

		textGrid.AddChild(new Label { Text = "Color" });
		var fgPicker = new ColorPickerButton { Color = clip.FontColor, CustomMinimumSize = new Vector2(0, 30) };
		fgPicker.ColorChanged += (c) => { clip.FontColor = c; RefreshClipViews(); };
		textGrid.AddChild(fgPicker);

		textGrid.AddChild(new Label { Text = "Outline" });
		var olPicker = new ColorPickerButton { Color = clip.OutlineColor, CustomMinimumSize = new Vector2(0, 30) };
		olPicker.ColorChanged += (c) => { clip.OutlineColor = c; RefreshClipViews(); };
		textGrid.AddChild(olPicker);

		AddGridField(textGrid, "Outline W", clip.OutlineWidth, 0, 20, 1, v =>
		{
			clip.OutlineWidth = (int)v;
			RefreshClipViews();
		});
	}

	// Helper: adds a labeled SpinBox row to a GridContainer.
	// If animProp and clipStart are provided, shows a keyframe diamond toggle button.
	// Shows a reset button (↺) that restores the initial value.
	private void AddGridField(GridContainer grid, string label, float initial, float min, float max, float step, System.Action<float> onChanged, AnimatableProperty? animProp = null, double clipStart = 0)
	{
		grid.AddChild(new Label { Text = label });
		var hbox = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		var spin = new SpinBox
		{
			MinValue = min,
			MaxValue = max,
			Step = step,
			Value = initial,
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			CustomMinimumSize = new Vector2(0, 30),
		};
		spin.ValueChanged += (v) => onChanged((float)v);
		hbox.AddChild(spin);

		var resetBtn = new Button
		{
			Text = "↺",
			TooltipText = "Reset to default",
			Flat = true,
			CustomMinimumSize = new Vector2(22, 30),
		};
		string capturedLabel = label;
		resetBtn.Pressed += () =>
		{
			Log.Print($"[UI] Button: Reset {capturedLabel}");
			spin.Value = initial;
			onChanged(initial);
		};
		hbox.AddChild(resetBtn);

		if (animProp != null)
		{
			var kfBtn = new Button
			{
				Text = "◇",
				CustomMinimumSize = new Vector2(30, 30),
			};
			kfBtn.Pressed += () =>
			{
				Log.Print($"[UI] Button: Keyframe toggle {capturedLabel}");
				double lt = _timeline.SelectionPos - clipStart;
				bool hasKf = animProp.IsAnimated && animProp.Keyframes.Any(k => Math.Abs(k.Time - lt) < 0.01);
				if (hasKf)
				{
					animProp.Keyframes.RemoveAll(k => Math.Abs(k.Time - lt) < 0.01);
					if (animProp.Keyframes.Count == 0) animProp.IsAnimated = false;
					kfBtn.Text = "◇";
				}
				else
				{
					animProp.IsAnimated = true;
					animProp.Keyframes.RemoveAll(k => Math.Abs(k.Time - lt) < 0.01);
					animProp.Keyframes.Add(new Keyframe { Time = lt, Value = (float)spin.Value });
					kfBtn.Text = "◆";
				}
				UpdateTracks();
			};
			hbox.AddChild(kfBtn);
		}

		grid.AddChild(hbox);
	}

	// Sync the overlay, preview, and timeline after inspector edits
	private void RefreshClipViews()
	{
		_overlay.RefreshActiveLayer();
		_outputPreview.RefreshDisplayLayer();
		_timeline.QueueRedraw();
	}
}
