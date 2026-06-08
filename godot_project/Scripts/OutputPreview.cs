// Result (master) monitor: renders the final composited output via a
// ShaderMaterial that layers blurred background, fitted content, camera PiP,
// UI PiP, and social overlay (TikTok/Shorts/Reels). Supports Basic, Circle
// Facecam, and Game UI layout modes. Also mirrors track overlay nodes for
// live text/image/GIF preview.

using Godot;
using System;
using System.Collections.Generic;

namespace VelosCCS;

public partial class OutputPreview : VBoxContainer
{
	private TextureRect _display = null!;
	private ShaderMaterial _shaderMat = null!;
	private Control _cameraOverlay = null!;
	private TextureRect _socialOverlay = null!;
	private AspectRatioContainer _container = null!;

	private VideoOverlay? _sourceOverlay;
	private int _outW = 1920, _outH = 1080;  // Output resolution for font scaling

	private Vector2 _camOutputPos = new(0.05f, 0.05f);
	private Vector2 _camOutputSize = new(0.4f, 0.25f);
	private Vector2 _uiOutputPos = new(0.02f, 0.7f);
	private Vector2 _uiOutputSize = new(0.3f, 0.12f);
	private Vector4 _contentOutput = new(0, 0, 1, 1);
	private int _layoutMode;
	private bool _showCameraOverlay;
	private bool _pipInteractive = true;
	private string _socialOverlayName = "None";

	private enum DragMode { None, Move, Resize }
	private DragMode _dragMode = DragMode.None;
	private Vector2 _dragStart, _dragOrigPos, _dragOrigSize;
	private int _resizeCorner = -1;
	private bool _dragIsUi;

	private const float HandleSize = 10f;
	private const float HandleGrab = 14f;

	// Text editing in preview
	private LineEdit _textEditor = null!;
	private (int ti, int ci)? _editingClip;
	public Action<(int ti, int ci, string text)>? TextEdited;

	// Image rotation controls
	private HBoxContainer _rotationBar = null!;
	private Label _rotationLabel = null!;
	public Action<(int ti, int ci, float rotation)>? RotationChanged;

	// Display-only layers (result monitor mirror)
	private Control _displayOverlay = null!;
	private List<TrackData> _displayTracks = new();
	private readonly Dictionary<(int, int), Control> _displayLayerNodes = new();
	private TrackClipData? _displayActiveClip;
	private double _displayTime;

	// Setup: create AspectRatioContainer, TextureRect, ShaderMaterial, camera/UI PiP
	// overlay controls, social overlay TextureRect, display layer mirror, texture poll timer
	public void Setup(VideoStreamPlayer sourcePlayer)
	{
		_container = new AspectRatioContainer { Ratio = 16f / 9f, SizeFlagsVertical = SizeFlags.ExpandFill, ClipContents = true };
		AddChild(_container);

		_display = new TextureRect { ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale };
		_container.AddChild(_display);
		_display.SetAnchorsPreset(LayoutPreset.FullRect);

		_shaderMat = new ShaderMaterial();
		_shaderMat.Shader = new Shader { Code = @"
            shader_type canvas_item;
            uniform vec4 gameplay_coords;
            uniform vec4 camera_coords;
            uniform vec4 camera_output;
            uniform vec4 content_output;
            uniform vec4 ui_output;
            uniform vec4 ui_coords;
            uniform float blur_amount = 2.5;
            uniform float target_aspect = 0.5625;
            uniform float blur_bg = 1.0;
            uniform int layout_mode = 0;

            void fragment() {
                vec2 uv = UV;

                // Layer 0: Background (blur or black)
                vec4 final_color;
                if (blur_bg > 0.5) {
                    vec2 bg_uv = clamp(gameplay_coords.xy + uv * gameplay_coords.zw, vec2(0.0), vec2(1.0));
                    vec4 blur_sum = vec4(0.0);
                    float offset = blur_amount * 0.002;
                    blur_sum += texture(TEXTURE, bg_uv + vec2(-offset, -offset));
                    blur_sum += texture(TEXTURE, bg_uv + vec2(offset, -offset));
                    blur_sum += texture(TEXTURE, bg_uv + vec2(-offset, offset));
                    blur_sum += texture(TEXTURE, bg_uv + vec2(offset, offset));
                    blur_sum += texture(TEXTURE, bg_uv) * 2.0;
                    final_color = (blur_sum / 6.0) * 0.4;
                } else {
                    final_color = vec4(0.0, 0.0, 0.0, 1.0);
                }

                // Determine content rect per layout mode
                vec4 c_out = (layout_mode == 2) ? content_output : vec4(0.0, 0.0, 1.0, 1.0);

                // Layer 1: Content (layout_mode 0/1: fitted to canvas, mode 2: stretched to content_output)
                vec2 c_rel = (uv - c_out.xy) / max(vec2(0.01), c_out.zw);
                bool in_content = c_rel.x >= 0.0 && c_rel.x <= 1.0 && c_rel.y >= 0.0 && c_rel.y <= 1.0;
                if (in_content) {
                    if (layout_mode == 2) {
                        vec2 src_uv = clamp(gameplay_coords.xy + c_rel * gameplay_coords.zw, vec2(0.0), vec2(1.0));
                        final_color = texture(TEXTURE, src_uv);
                    } else {
                        float sw = max(0.01, gameplay_coords.z);
                        float sh = max(0.01, gameplay_coords.w);
                        float s_aspect = sw / sh;
                        float t_aspect = max(0.01, target_aspect);
                        float game_w, game_h, game_left, game_top;
                        if (s_aspect >= t_aspect) {
                            game_w = 1.0; game_h = t_aspect / s_aspect;
                            game_left = 0.0; game_top = 0.5 - game_h / 2.0;
                        } else {
                            game_w = s_aspect / t_aspect; game_h = 1.0;
                            game_left = 0.5 - game_w / 2.0; game_top = 0.0;
                        }
                        bool in_fit = c_rel.x >= game_left && c_rel.x <= game_left + game_w && c_rel.y >= game_top && c_rel.y <= game_top + game_h;
                        if (in_fit) {
                            vec2 g_uv = vec2((c_rel.x - game_left) / game_w, (c_rel.y - game_top) / game_h);
                            vec2 src_uv = clamp(gameplay_coords.xy + g_uv * gameplay_coords.zw, vec2(0.0), vec2(1.0));
                            final_color = texture(TEXTURE, src_uv);
                        }
                    }
                }

                // Layer 2: Camera (rectangle or circle; stretched to rect in Game UI mode)
                vec2 cam_rel = (uv - camera_output.xy) / max(vec2(0.01), camera_output.zw);
                bool in_camera = cam_rel.x >= 0.0 && cam_rel.x <= 1.0 && cam_rel.y >= 0.0 && cam_rel.y <= 1.0;
                if (in_camera) {
                    bool show_cam = true;
                    if (layout_mode == 1) {
                        vec2 cam_center = camera_output.xy + camera_output.zw * 0.5;
                        vec2 rel = (uv - cam_center) / (camera_output.zw * 0.5);
                        if (length(rel) > 1.0) show_cam = false;
                    }
                    if (show_cam) {
                        vec2 cam_uv = clamp(camera_coords.xy + cam_rel * camera_coords.zw, vec2(0.0), vec2(1.0));
                        final_color = texture(TEXTURE, cam_uv);
                    }
                }

                // Layer 3: UI PiP (topmost)
                vec2 ui_rel = (uv - ui_output.xy) / max(vec2(0.01), ui_output.zw);
                if (ui_rel.x >= 0.0 && ui_rel.x <= 1.0 && ui_rel.y >= 0.0 && ui_rel.y <= 1.0) {
                    vec2 ui_uv = clamp(ui_coords.xy + ui_rel * ui_coords.zw, vec2(0.0), vec2(1.0));
                    final_color = texture(TEXTURE, ui_uv);
                }

                COLOR = final_color;
            }"
		};
		// Shader not applied initially — 16:9 Normal mode shows raw source

		_cameraOverlay = new Control { MouseFilter = MouseFilterEnum.Pass };
		_container.AddChild(_cameraOverlay);
		_cameraOverlay.SetAnchorsPreset(LayoutPreset.FullRect);
		_cameraOverlay.Draw += OnDrawOverlay;
		_cameraOverlay.GuiInput += OnInteractionInput;

		_socialOverlay = new TextureRect { ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore, Visible = false, ZIndex = 5 };
		if (FileAccess.FileExists("res://Assets/tiktok_overlay.png"))
			_socialOverlay.Texture = GD.Load<Texture2D>("res://Assets/tiktok_overlay.png");
		_container.AddChild(_socialOverlay);
		_socialOverlay.SetAnchorsPreset(LayoutPreset.FullRect);

		_displayOverlay = new Control { MouseFilter = MouseFilterEnum.Ignore };
		_container.AddChild(_displayOverlay);
		_displayOverlay.SetAnchorsPreset(LayoutPreset.FullRect);

		_textEditor = new LineEdit
		{
			PlaceholderText = "Edit text...",
			Visible = false,
			ZIndex = 10,
		};
		_textEditor.TextSubmitted += text =>
		{
			if (_editingClip != null)
			{
				TextEdited?.Invoke((_editingClip.Value.ti, _editingClip.Value.ci, text));
				_editingClip = null;
			}
			_textEditor.Visible = false;
		};
		_textEditor.FocusExited += () =>
		{
			if (_editingClip != null && _textEditor.Text.Length > 0)
			{
				TextEdited?.Invoke((_editingClip.Value.ti, _editingClip.Value.ci, _textEditor.Text));
			}
			_editingClip = null;
			_textEditor.Visible = false;
		};
		_container.AddChild(_textEditor);

		_rotationBar = new HBoxContainer
		{
			Visible = false,
			ZIndex = 10,
			Modulate = new Color(1, 1, 1, 0.85f),
		};
		var rotLeft = new Button { Text = "↺", CustomMinimumSize = new Vector2(32, 28), TooltipText = "Rotate left" };
		rotLeft.Pressed += () =>
		{
			if (_displayActiveClip != null)
			{
				float newRot = (_displayActiveClip.Rotation.StaticValue - 15f) % 360f;
				if (newRot < 0) newRot += 360f;
				_rotationLabel.Text = $"{(int)newRot}°";
				FindActiveClipAndNotify(newRot);
			}
		};
		_rotationBar.AddChild(rotLeft);
		_rotationLabel = new Label { Text = "0°", CustomMinimumSize = new Vector2(40, 28), HorizontalAlignment = HorizontalAlignment.Center };
		_rotationBar.AddChild(_rotationLabel);
		var rotRight = new Button { Text = "↻", CustomMinimumSize = new Vector2(32, 28), TooltipText = "Rotate right" };
		rotRight.Pressed += () =>
		{
			if (_displayActiveClip != null)
			{
				float newRot = (_displayActiveClip.Rotation.StaticValue + 15f) % 360f;
				_rotationLabel.Text = $"{(int)newRot}°";
				FindActiveClipAndNotify(newRot);
			}
		};
		_rotationBar.AddChild(rotRight);
		var rotReset = new Button { Text = "↺ Reset", CustomMinimumSize = new Vector2(60, 28), TooltipText = "Reset rotation" };
		rotReset.Pressed += () =>
		{
			if (_displayActiveClip != null)
			{
				_rotationLabel.Text = "0°";
				FindActiveClipAndNotify(0f);
			}
		};
		_rotationBar.AddChild(rotReset);
		_container.AddChild(_rotationBar);

		var timer = new Timer { WaitTime = 0.05, Autostart = true };
		timer.Timeout += () =>
		{
			_display.Texture = sourcePlayer.GetVideoTexture();
			UpdateDisplayLayers();
		};
		AddChild(timer);

		_shaderMat.SetShaderParameter("gameplay_coords", new Vector4(0, 0, 1, 1));
		_shaderMat.SetShaderParameter("camera_output", new Vector4(_camOutputPos.X, _camOutputPos.Y, _camOutputSize.X, _camOutputSize.Y));
		_shaderMat.SetShaderParameter("target_aspect", 16f / 9f);
		_shaderMat.SetShaderParameter("layout_mode", 0);
		_shaderMat.SetShaderParameter("ui_output", new Vector4(0, 0, 0, 0));
		_shaderMat.SetShaderParameter("ui_coords", new Vector4(0, 0, 1, 1));

	}

	private void UpdateShaderUniforms()
	{
		if (_sourceOverlay == null || _shaderMat == null) return;
		var g = _sourceOverlay.GetRegion("Content")?.Rect ?? new Rect2(0, 0, 1, 1);
		var c = _sourceOverlay.GetRegion("Camera")?.Rect ?? new Rect2(0, 0, 1, 1);
		var u = _sourceOverlay.GetRegion("UI")?.Rect ?? new Rect2(0, 0, 1, 1);
		_shaderMat.SetShaderParameter("gameplay_coords", new Vector4(g.Position.X, g.Position.Y, g.Size.X, g.Size.Y));
		_shaderMat.SetShaderParameter("camera_coords", new Vector4(c.Position.X, c.Position.Y, c.Size.X, c.Size.Y));
		_shaderMat.SetShaderParameter("camera_output", new Vector4(_camOutputPos.X, _camOutputPos.Y, _camOutputSize.X, _camOutputSize.Y));
		_shaderMat.SetShaderParameter("content_output", _contentOutput);
		_shaderMat.SetShaderParameter("ui_output", new Vector4(_uiOutputPos.X, _uiOutputPos.Y, _uiOutputSize.X, _uiOutputSize.Y));
		_shaderMat.SetShaderParameter("ui_coords", new Vector4(u.Position.X, u.Position.Y, u.Size.X, u.Size.Y));
		_cameraOverlay.QueueRedraw();
	}

	public void SetOverlay(VideoOverlay overlay)
	{
		_sourceOverlay = overlay;
		if (_sourceOverlay != null)
			_sourceOverlay.LayoutChanged += (string _) => UpdateShaderUniforms();
	}

	public void SetSocialOverlay(string platformName)
	{
		_socialOverlayName = platformName;
		if (platformName == "None")
		{
			_socialOverlay.Visible = false;
			return;
		}
		string path = $"res://Assets/{platformName.ToLower()}_overlay.png";
		if (FileAccess.FileExists(path))
		{
			_socialOverlay.Texture = GD.Load<Texture2D>(path);
			_socialOverlay.Visible = true;
		}
		else
		{
			_socialOverlay.Visible = false;
		}
	}
	public string GetSocialOverlayName() => _socialOverlayName;
	public bool GetShowCameraOverlay() => _showCameraOverlay;
	public new int LayoutMode => _layoutMode;
	public void SetBlur(float v) { if (_shaderMat != null) _shaderMat.SetShaderParameter("blur_amount", v); }
	public void SetBlurBg(bool on) { _blurBg = on; if (_shaderMat != null) _shaderMat.SetShaderParameter("blur_bg", on ? 1f : 0f); }
	public bool BlurBg => _blurBg;
	private bool _blurBg;
	public void SetLayoutMode(int mode)
	{
		_layoutMode = mode;
		if (_shaderMat != null) _shaderMat.SetShaderParameter("layout_mode", mode);
		_cameraOverlay.QueueRedraw();
	}
	public void SetCameraOutput(Vector2 pos, Vector2 size) { _camOutputPos = pos; _camOutputSize = size; if (_shaderMat != null) { _shaderMat.SetShaderParameter("camera_output", new Vector4(pos.X, pos.Y, size.X, size.Y)); } _cameraOverlay.QueueRedraw(); }
	public void SetPipInteractive(bool on) { _pipInteractive = on; _cameraOverlay.QueueRedraw(); }
	public void SetUiOutput(Vector2 pos, Vector2 size) { _uiOutputPos = pos; _uiOutputSize = size; if (_shaderMat != null) { _shaderMat.SetShaderParameter("ui_output", new Vector4(pos.X, pos.Y, size.X, size.Y)); } _cameraOverlay.QueueRedraw(); }
	public void SetUiOverlay(Vector4 output, Vector4 coords)
	{
		_uiOutputPos = new Vector2(output.X, output.Y);
		_uiOutputSize = new Vector2(output.Z, output.W);
		if (_shaderMat != null)
		{
			_shaderMat.SetShaderParameter("ui_output", output);
			_shaderMat.SetShaderParameter("ui_coords", coords);
		}
		_cameraOverlay.QueueRedraw();
	}
	public void SetContentOutput(Vector4 output)
	{
		_contentOutput = output;
		if (_shaderMat != null) _shaderMat.SetShaderParameter("content_output", output);
		_cameraOverlay.QueueRedraw();
	}
	public void SetSourceCrop(string regionName, Rect2 rect)
	{
		if (_sourceOverlay == null) return;
		var region = _sourceOverlay.GetRegion(regionName);
		if (region == null) return;
		region.Rect = rect;
		_sourceOverlay.QueueRedraw();
		UpdateShaderUniforms();
	}
	public void UpdateCrop(float x, float y, float w, float h) { }

	public float[] GetCameraTarget() => new[] { _camOutputPos.X, _camOutputPos.Y, _camOutputSize.X, _camOutputSize.Y };
	public float[] GetUiTarget() => new[] { _uiOutputPos.X, _uiOutputPos.Y, _uiOutputSize.X, _uiOutputSize.Y };

	private void OnDrawOverlay()
	{
		if (!_pipInteractive) return;
		var ds = _display.Size;
		if (ds.X <= 0) return;

		float half = HandleSize / 2f;

		// UI PiP (topmost — draw first so it renders beneath camera overlay)
		if (_display.Texture != null && _layoutMode == 2)
		{
			var uiPx = _uiOutputPos * ds;
			var uiSz = _uiOutputSize * ds;
			_cameraOverlay.DrawRect(new Rect2(uiPx, uiSz), new Color(1, 0.4f, 0.7f, 0.8f), false, 2);
			foreach (var p in GetUiCornersPx())
				_cameraOverlay.DrawRect(new Rect2(p.X - half, p.Y - half, HandleSize, HandleSize), new Color(1, 0.4f, 0.7f, 0.9f));
		}

		// Camera PiP
		if (_display.Texture != null && _showCameraOverlay)
		{
			var camPx = _camOutputPos * ds;
			var camSz = _camOutputSize * ds;
			_cameraOverlay.DrawRect(new Rect2(camPx, camSz), new Color(1, 0.84f, 0, 0.8f), false, 2);
			foreach (var p in GetCamCornersPx())
				_cameraOverlay.DrawRect(new Rect2(p.X - half, p.Y - half, HandleSize, HandleSize), new Color(1, 0.84f, 0, 0.9f));
		}
	}

	private Vector2[] GetCamCornersPx()
	{
		var ds = _display.Size;
		var pos = _camOutputPos * ds;
		var sz = _camOutputSize * ds;
		return new[] { pos, new Vector2(pos.X + sz.X, pos.Y), new Vector2(pos.X, pos.Y + sz.Y), pos + sz };
	}

	private Vector2[] GetUiCornersPx()
	{
		var ds = _display.Size;
		var pos = _uiOutputPos * ds;
		var sz = _uiOutputSize * ds;
		return new[] { pos, new Vector2(pos.X + sz.X, pos.Y), new Vector2(pos.X, pos.Y + sz.Y), pos + sz };
	}

	private void OnInteractionInput(InputEvent ev)
	{
		if (ev is InputEventMouseButton mb && mb.DoubleClick && mb.ButtonIndex == MouseButton.Left)
		{
			// Double-click: check for text clips at cursor position (works in all states)
			var ds = _displayOverlay.Size;
			if (ds.X > 5 && ds.Y > 5)
			{
				foreach (var (key, node) in _displayLayerNodes)
				{
					var (ti, ci) = key;
					if (ti >= _displayTracks.Count || ci >= _displayTracks[ti].Clips.Count) continue;
					var clip = _displayTracks[ti].Clips[ci];
					if (clip.ClipType != ClipType.Text) continue;
					var rect = new Rect2(node.Position, node.Size);
					float clipRot = clip.Rotation.GetValueAt(0);
					bool hit;
					if (Math.Abs(clipRot) > 0.5f)
					{
						var center = node.Position + node.Size * 0.5f;
						var local = mb.Position - center;
						float rad = Mathf.DegToRad(-clipRot);
						float cos = Mathf.Cos(rad), sin = Mathf.Sin(rad);
						var unrot = new Vector2(local.X * cos - local.Y * sin, local.X * sin + local.Y * cos);
						hit = new Rect2(-node.Size * 0.5f, node.Size).HasPoint(unrot);
					}
					else
					{
						hit = rect.HasPoint(mb.Position);
					}
					if (hit)
					{
						_editingClip = (ti, ci);
						_textEditor.Text = clip.Text;
						_textEditor.Size = node.Size;
						_textEditor.Position = node.Position;
						_textEditor.Visible = true;
						_textEditor.CallDeferred("grab_focus");
						_textEditor.CaretColumn = _textEditor.Text.Length;
						return;
					}
				}
			}
		}
		// Dismiss text editor on any click outside it
		if (ev is InputEventMouseButton mbClick && mbClick.Pressed && _textEditor.Visible)
		{
			var editorRect = new Rect2(_textEditor.Position, _textEditor.Size);
			if (!editorRect.HasPoint(mbClick.Position))
			{
				_editingClip = null;
				_textEditor.Visible = false;
			}
		}

		if (!_pipInteractive) return;
		if (ev is InputEventMouseButton mb2 && mb2.ButtonIndex == MouseButton.Left)
		{
			if (mb2.Pressed)
			{
				var mpos = mb2.Position;
				var posNorm = mpos / _display.Size;

				// UI PiP (topmost layer, Game UI mode only)
				if (_layoutMode == 2)
				{
					var uiCorners = GetUiCornersPx();
					for (int i = 0; i < uiCorners.Length; i++)
					{
						if (uiCorners[i].DistanceTo(mpos) < HandleGrab)
						{
							_dragMode = DragMode.Resize;
							_dragStart = mpos;
							_dragOrigPos = _uiOutputPos;
							_dragOrigSize = _uiOutputSize;
							_resizeCorner = i;
							_dragIsUi = true;
							return;
						}
					}
					var uiRect = new Rect2(_uiOutputPos * _display.Size, _uiOutputSize * _display.Size);
					if (uiRect.HasPoint(mpos))
					{
						_dragMode = DragMode.Move;
						_dragStart = mpos;
						_dragOrigPos = _uiOutputPos;
						_resizeCorner = -1;
						_dragIsUi = true;
						return;
					}
				}

				// Camera PiP
				if (_showCameraOverlay)
				{
					var camCorners = GetCamCornersPx();
					for (int i = 0; i < camCorners.Length; i++)
					{
						if (camCorners[i].DistanceTo(mpos) < HandleGrab)
						{
							_dragMode = DragMode.Resize;
							_dragStart = mpos;
							_dragOrigPos = _camOutputPos;
							_dragOrigSize = _camOutputSize;
							_resizeCorner = i;
							_dragIsUi = false;
							return;
						}
					}
					var camRect = new Rect2(_camOutputPos * _display.Size, _camOutputSize * _display.Size);
					if (camRect.HasPoint(mpos))
					{
						_dragMode = DragMode.Move;
						_dragStart = mpos;
						_dragOrigPos = _camOutputPos;
						_resizeCorner = -1;
						_dragIsUi = false;
					}
				}
			}
			else { _dragMode = DragMode.None; _resizeCorner = -1; }
		}
		else if (ev is InputEventMouseMotion mm && _dragMode != DragMode.None)
		{
			var delta = (mm.Position - _dragStart) / _display.Size;

			if (_dragMode == DragMode.Move)
			{
				if (_dragIsUi)
					_uiOutputPos = (_dragOrigPos + delta).Clamp(Vector2.Zero, Vector2.One - _uiOutputSize);
				else
					_camOutputPos = (_dragOrigPos + delta).Clamp(Vector2.Zero, Vector2.One - _camOutputSize);
			}
			else if (_dragMode == DragMode.Resize && _resizeCorner >= 0)
			{
				float min = 0.05f;
				var p = _dragOrigPos;
				var s = _dragOrigSize;
				var d = delta;

				switch (_resizeCorner)
				{
					case 0: p += d; s -= d; break;
					case 1: p = new Vector2(p.X, p.Y + d.Y); s = new Vector2(s.X + d.X, s.Y - d.Y); break;
					case 2: p = new Vector2(p.X + d.X, p.Y); s = new Vector2(s.X - d.X, s.Y + d.Y); break;
					case 3: s += d; break;
				}

				if (s.X < min) s.X = min;
				if (s.Y < min) s.Y = min;
				p = p.Clamp(Vector2.Zero, Vector2.One - new Vector2(min, min));
				if (p.X + s.X > 1f) s.X = 1f - p.X;
				if (p.Y + s.Y > 1f) s.Y = 1f - p.Y;

				if (_dragIsUi) { _uiOutputPos = p; _uiOutputSize = s; }
				else { _camOutputPos = p; _camOutputSize = s; }
			}

			UpdateShaderUniforms();
		}
	}

	// Sync all overlay layer nodes from track data (mirror of VideoOverlay.SyncLayers)
	public void SyncDisplayLayers(List<TrackData> tracks)
	{
		_displayTracks = tracks;
		foreach (var kv in _displayLayerNodes)
			kv.Value.QueueFree();
		_displayLayerNodes.Clear();

		for (int ti = 0; ti < _displayTracks.Count; ti++)
		{
			var track = _displayTracks[ti];
			if (track.Type != TrackType.Video) continue;
			for (int ci = 0; ci < track.Clips.Count; ci++)
			{
				var clip = track.Clips[ci];
				if (clip.ClipType == ClipType.SourceVideo) continue;
				var node = CreateDisplayNode(clip);
				if (node != null)
				{
					_displayLayerNodes[(ti, ci)] = node;
					_displayOverlay.AddChild(node);
				}
			}
		}
	}

	public void SelectDisplayLayer(TrackClipData? clip)
	{
		_displayActiveClip = clip;
	}

	public void RefreshDisplayLayer()
	{
		if (_displayActiveClip == null) return;

		foreach (var (key, node) in _displayLayerNodes)
		{
			var clip = _displayTracks[key.Item1].Clips[key.Item2];
			if (clip != _displayActiveClip) continue;

			if (node is Label l)
			{
				l.Text = clip.Text;
				if (l.LabelSettings != null)
				{
					l.LabelSettings.FontSize = clip.FontSize;
					l.LabelSettings.FontColor = clip.FontColor;
					l.LabelSettings.OutlineSize = clip.OutlineWidth;
					l.LabelSettings.OutlineColor = clip.OutlineColor;
				}

				if (!string.IsNullOrEmpty(clip.FontPath))
				{
					try
					{
						var ff = new FontFile();
						ff.LoadDynamicFont(clip.FontPath);
						if (l.LabelSettings != null)
							l.LabelSettings.Font = ff;
					}
					catch (Exception e)
					{
						GD.PrintErr($"[OutputPreview] Font load failed: {e.Message}");
					}
				}
			}
			break;
		}
	}

	public void SetDisplayTime(double t)
	{
		_displayTime = t;
	}

	public void RefreshVideoTexture(VideoStreamPlayer src)
	{
		_display.Texture = src.GetVideoTexture();
	}

	private void FindActiveClipAndNotify(float rotation)
	{
		if (_displayActiveClip == null) return;
		foreach (var (key, node) in _displayLayerNodes)
		{
			var (ti, ci) = key;
			if (ti < _displayTracks.Count && ci < _displayTracks[ti].Clips.Count)
			{
				var clip = _displayTracks[ti].Clips[ci];
				if (clip == _displayActiveClip)
				{
					RotationChanged?.Invoke((ti, ci, rotation));
					return;
				}
			}
		}
	}

	private void UpdateDisplayLayers()
	{
		var ds = _displayOverlay.Size;
		if (ds.X <= 5 || ds.Y <= 5) return;
		float fontScale = ds.Y / 720f;

		foreach (var (key, node) in _displayLayerNodes)
		{
			var (ti, ci) = key;
			if (ti >= _displayTracks.Count || ci >= _displayTracks[ti].Clips.Count) continue;
			var clip = _displayTracks[ti].Clips[ci];
			bool inTime = _displayTime >= clip.Start && _displayTime <= clip.End;
			bool visible = !_displayTracks[ti].Muted && inTime;
			node.Visible = visible;
			if (visible)
			{
				node.SetAnchorsPreset(LayoutPreset.TopLeft);
				node.PivotOffset = Vector2.Zero;

				double localT = _displayTime - clip.Start;
				float o = clip.Opacity.GetValueAt(localT);
				float fade = clip.GetFadeAt(localT);

				node.Size = clip.Size * ds;
				node.Position = clip.Position * ds;
				node.PivotOffset = node.Size * 0.5f;
				float rotationDeg = clip.Rotation.GetValueAt(localT);
				node.Rotation = Mathf.DegToRad(rotationDeg);
				node.Modulate = new Color(1, 1, 1, o * fade);

				if (node is Label l && l.LabelSettings != null)
				{
					l.HorizontalAlignment = HorizontalAlignment.Center;
					l.VerticalAlignment = VerticalAlignment.Center;
					l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
					l.ClipText = false;

					int baseSize = (int)clip.FontSizeAnim.GetValueAt(localT);
					l.LabelSettings.FontSize = (int)Math.Max(1, baseSize * fontScale);
					l.LabelSettings.OutlineSize = (int)Math.Max(0, clip.OutlineWidth * fontScale);
					string curText = clip.GetTextAt(localT);
					if (l.Text != curText) l.Text = curText;
					string curFontPath = clip.GetFontPathAt(localT);
					string prevPath = (string)l.GetMeta("font_path", "");
					if (prevPath != curFontPath)
					{
						l.SetMeta("font_path", curFontPath ?? "");
						if (!string.IsNullOrEmpty(curFontPath))
						{
							try
							{
								var ff = new FontFile();
								ff.LoadDynamicFont(curFontPath);
								l.LabelSettings.Font = ff;
							}
							catch { }
						}
						else
						{
							l.LabelSettings.Font = null;
						}
					}
				}
			}
		}

		// Show rotation bar for active image/gif clip
		if (_displayActiveClip != null && _displayActiveClip.ClipType is ClipType.Image or ClipType.Gif)
		{
			_rotationBar.Visible = true;
			var clipPos = _displayActiveClip.Position * ds;
			var clipSize = _displayActiveClip.Size * ds;
			_rotationBar.Position = new Vector2(clipPos.X + clipSize.X - _rotationBar.Size.X, clipPos.Y - 30);
			_rotationLabel.Text = $"{(int)_displayActiveClip.Rotation.StaticValue}°";
		}
		else
		{
			_rotationBar.Visible = false;
		}
	}

	private static Control? CreateDisplayNode(TrackClipData clip)
	{
		switch (clip.ClipType)
		{
			case ClipType.Text:
				var label = new Label
				{
					Text = clip.Text,
					HorizontalAlignment = HorizontalAlignment.Center,
					VerticalAlignment = VerticalAlignment.Center,
					AutowrapMode = TextServer.AutowrapMode.WordSmart,
					MouseFilter = MouseFilterEnum.Ignore,
				};

				var ls = new LabelSettings
				{
					FontSize = clip.FontSize,
					FontColor = clip.FontColor,
					OutlineSize = clip.OutlineWidth,
					OutlineColor = clip.OutlineColor,
				};

				if (!string.IsNullOrEmpty(clip.FontPath))
				{
					try
					{
						var fontFile = new FontFile();
						fontFile.LoadDynamicFont(clip.FontPath);
						ls.Font = fontFile;
					}
					catch (Exception ex)
					{
						GD.PrintErr($"[OutputPreview] Failed to load font: {clip.FontPath} - {ex.Message}");
					}
				}

				label.LabelSettings = ls;
				return label;
			case ClipType.Image:
			case ClipType.Gif:
				if (string.IsNullOrEmpty(clip.FilePath) || !System.IO.File.Exists(clip.FilePath)) return null;
				var img = Image.LoadFromFile(clip.FilePath);
				if (img == null || img.IsEmpty())
				{
					try
					{
						var bytes = System.IO.File.ReadAllBytes(clip.FilePath);
						var ext = System.IO.Path.GetExtension(clip.FilePath).ToLowerInvariant();
						img = new Image();
						if (ext == ".png") img.LoadPngFromBuffer(bytes);
						else if (ext is ".jpg" or ".jpeg") img.LoadJpgFromBuffer(bytes);
						else if (ext == ".webp") img.LoadWebpFromBuffer(bytes);
						else if (ext == ".bmp") img.LoadBmpFromBuffer(bytes);
						else img.LoadPngFromBuffer(bytes);
					}
					catch { return null; }
				}
				if (img == null || img.IsEmpty()) return null;
				return new TextureRect
				{
					Texture = ImageTexture.CreateFromImage(img),
					ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
					StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
					MouseFilter = MouseFilterEnum.Ignore,
				};
			default:
				return null;
		}
	}

	public float CurrentRatio => _container.Ratio;
	public Material? DisplayMaterial => _display.Material;
	public Vector2 OutputSize => new(_outW, _outH);
	public Vector2 DisplaySize => _displayOverlay.Size;

	// Switch aspect ratio: 16:9 uses raw source (no shader), others apply shader
	// with reframing, camera PiP, blur background, and social overlay compositing.
	public void SetAspectRatio(string ratio)
	{
		var parts = ratio.Split(':');
		if (parts.Length == 2 && float.TryParse(parts[0], out var w) && float.TryParse(parts[1], out var h) && h > 0)
		{
			float aspect = w / h;
			_container.Ratio = aspect;

			// Store output resolution for font scaling
			(_outW, _outH) = ratio switch
			{
				"9:16" => (1080, 1920),
				"16:9" => (1920, 1080),
				"1:1" => (1080, 1080),
				"4:5" => (864, 1080),
				"2:3" => (720, 1080),
				_ => (1080, 1920),
			};

			bool isNormal16_9 = Math.Abs(aspect - 16f / 9f) < 0.01f;
			_showCameraOverlay = !isNormal16_9;

			// Auto-toggle blur: on for portrait/square, off for 16:9
			SetBlurBg(!isNormal16_9);

			if (isNormal16_9)
			{
				_display.Material = null;
			}
			else if (_shaderMat != null)
			{
				_display.Material = _shaderMat;
				_shaderMat.SetShaderParameter("target_aspect", aspect);
			}

			_cameraOverlay.QueueRedraw();
		}
	}
}
