// Overlay on the source video monitor. Two modes:
//   Layout  — draggable/resizable regions (Content, Camera, UI) with bracket corners
//   Editing — per-clip position/size handles for text/image/GIF overlays.
// Also manages Label/TextureRect nodes that mirror active clip content for live preview.

using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace VelosCCS;

public class OverlayRegion
{
	public string Name { get; set; } = "";
	public Rect2 Rect { get; set; }
	public Color Color { get; set; }
	public bool Visible { get; set; } = true;
}

public enum OverlayMode { Layout, Editing }

public partial class VideoOverlay : Control
{
	[Signal] public delegate void LayoutChangedEventHandler(string regionName);
	[Signal] public delegate void CameraPipChangedEventHandler(Vector2 pos, Vector2 size);
	[Signal] public delegate void UiPipChangedEventHandler(Vector2 pos, Vector2 size);

	private static readonly Color HandleColor = new(1, 0.84f, 0);
	private const float MinSize = 0.05f;
	private const float MaxSize = 1.0f;

	public List<OverlayRegion> Regions { get; } = new();

	private int _dragRegion = -1;
	private enum DragHandle { None, Move, TL, TR, BL, BR }
	private DragHandle _dragHandle = DragHandle.None;
	private Vector2 _dragClickOffset;
	private Rect2 _dragOrigRect;

	// Layer management
	private Control _layersContainer = null!;
	private List<TrackData> _tracks = new();
	private readonly Dictionary<(int, int), Control> _layerNodes = new();
	private TrackClipData? _activeClip;
	private double _currentTime;
	private OverlayMode _mode = OverlayMode.Layout;

	// Layer drag
	private bool _isDraggingLayer;
	private Vector2 _layerDragStart;
	private Vector2 _layerDragOrigPos;
	private Vector2 _layerDragOrigSize;
	private int _layerDragCorner = -1;
	private const float LayerHandleSize = 10f;
	private const float LayerHandleGrab = 14f;

	// PiP edit mode (Camera/UI track clips in Edit step)
	private enum PipEditMode { None, Camera, Ui }
	private PipEditMode _pipMode = PipEditMode.None;
	private Vector2 _pipPos, _pipSize;
	private bool _isDraggingPip;
	private Vector2 _pipDragStart, _pipDragOrigPos, _pipDragOrigSize;
	private int _pipDragCorner = -1;

	public VideoOverlay()
	{
		// Streamladder Content: Left 3.68%, Top 12.49%, Width 49.22%, Height 87.5%
		Regions.Add(new OverlayRegion {
			Name = "Content", Rect = new Rect2(0.036788f, 0.124949f, 0.492216f, 0.875051f),
			Color = new Color(0, 1, 0.53f, 0.8f),
		});
		// Streamladder Camera: Left 58.14%, Top 67.57%, Width 22.80%, Height 32.43%
		Regions.Add(new OverlayRegion {
			Name = "Camera", Rect = new Rect2(0.581453f, 0.675695f, 0.228027f, 0.324305f),
			Color = Color.FromHtml("#D0570C"),
		});
		// UI region (used in Game UI mode)
		Regions.Add(new OverlayRegion {
			Name = "UI", Rect = new Rect2(0.399904f, 0.884377f, 0.200193f, 0.115623f),
			Color = Color.FromHtml("#f78166"),
			Visible = false,
		});

		MouseFilter = MouseFilterEnum.Stop;

		_layersContainer = new Control { MouseFilter = MouseFilterEnum.Ignore };
		AddChild(_layersContainer);
		_layersContainer.SetAnchorsPreset(LayoutPreset.FullRect);

		// Timer for layer visibility updates during playback
		var updateTimer = new Timer { WaitTime = 0.05, Autostart = true };
		updateTimer.Timeout += () =>
		{
			if (_mode == OverlayMode.Editing)
				UpdateLayerVisibility();
		};
		AddChild(updateTimer);
	}

	public void SetMode(OverlayMode mode)
	{
		Log.Print($"[Overlay] SetMode {mode}");
		_mode = mode;
		QueueRedraw();
		if (mode == OverlayMode.Editing)
			UpdateLayerVisibility();
		else
			_layersContainer.Visible = false;
	}

	public void SyncLayers(List<TrackData> tracks)
	{
		Log.Print($"[Overlay] SyncLayers tracks={tracks.Count}");
		_tracks = tracks;
		foreach (var kv in _layerNodes)
			kv.Value.QueueFree();
		_layerNodes.Clear();

		for (int ti = 0; ti < _tracks.Count; ti++)
		{
			var track = _tracks[ti];
			if (track.Type != TrackType.Video) continue;
			for (int ci = 0; ci < track.Clips.Count; ci++)
			{
				var clip = track.Clips[ci];
				if (clip.ClipType == ClipType.SourceVideo) continue;
				var node = CreateLayerNode(clip);
				if (node != null)
				{
					_layerNodes[(ti, ci)] = node;
					_layersContainer.AddChild(node);
				}
			}
		}

		if (_mode == OverlayMode.Editing)
			UpdateLayerVisibility();
	}

	public void SelectLayer(int t, int c, TrackClipData? clip)
	{
		Log.Print($"[Overlay] SelectLayer track={t} clip={c}");
		_activeClip = clip;
		_pipMode = PipEditMode.None;
		QueueRedraw();
	}

	public void SetPipEditing(string trackName, Vector2 pos, Vector2 size)
	{
		Log.Print($"[Overlay] SetPipEditing track={trackName} pos={pos} size={size}");
		_activeClip = null;
		_pipMode = trackName is "Camera" or "Basic Facecam" ? PipEditMode.Camera : PipEditMode.Ui;
		_pipPos = pos;
		_pipSize = size;
		QueueRedraw();
	}

	public void ClearPipEditing()
	{
		Log.Print("[Overlay] ClearPipEditing");
		_pipMode = PipEditMode.None;
		QueueRedraw();
	}

	public void RefreshActiveLayer()
	{
		if (_activeClip == null) { Log.Print("[Overlay] RefreshActiveLayer: no active clip"); return; }
		Log.Print("[Overlay] RefreshActiveLayer");

		foreach (var (key, node) in _layerNodes)
		{
			var clip = _tracks[key.Item1].Clips[key.Item2];
			if (clip != _activeClip) continue;

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
						Log.Error($"[Overlay] Font load failed: {e.Message}");
					}
				}
			}
			break;
		}
		UpdateLayerVisibility();
		QueueRedraw();
	}

	private static Control? CreateLayerNode(TrackClipData clip)
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
						Log.Error($"[Overlay] Failed to load font: {clip.FontPath} - {ex.Message}");
					}
				}

				label.LabelSettings = ls;
				return label;
			case ClipType.Image:
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
			case ClipType.Gif:
				if (string.IsNullOrEmpty(clip.FilePath) || !System.IO.File.Exists(clip.FilePath)) return null;
				var gifData = GifCache.GetOrCreate(clip.FilePath);
				if (gifData?.Textures == null || gifData.Textures.Length == 0) return null;
				var gifRect = new GifTextureRect
				{
					ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
					StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
					MouseFilter = MouseFilterEnum.Ignore,
				};
				gifRect.Play(gifData);
				return gifRect;
			default:
				return null;
		}
	}

	private void UpdateLayerVisibility()
	{
		_layersContainer.Visible = (_mode == OverlayMode.Editing);
		var ds = Size;
		if (ds.X <= 5 || ds.Y <= 5) return;
		float fontScale = ds.Y / 720f;

		foreach (var (key, node) in _layerNodes)
		{
			var (ti, ci) = key;
			if (ti >= _tracks.Count || ci >= _tracks[ti].Clips.Count) continue;
			var clip = _tracks[ti].Clips[ci];
			bool inTime = _currentTime >= clip.Start && _currentTime <= clip.End;
			bool visible = !_tracks[ti].Muted && inTime;
			node.Visible = visible;
			if (visible)
			{
				node.SetAnchorsPreset(LayoutPreset.TopLeft);
				node.PivotOffset = Vector2.Zero;

				double localT = _currentTime - clip.Start;
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
	}

	public void SetCurrentTime(double t)
	{
		_currentTime = t;
	}

	public OverlayRegion? GetRegion(string name) => Regions.FirstOrDefault(r => r.Name == name);

	public void AddRegion(string name, Rect2 rect, Color color)
	{
		Log.Print($"[Overlay] AddRegion {name}");
		Regions.Add(new OverlayRegion { Name = name, Rect = rect, Color = color });
		QueueRedraw();
	}

	public void RemoveRegion(string name)
	{
		Log.Print($"[Overlay] RemoveRegion {name}");
		Regions.RemoveAll(r => r.Name == name);
		QueueRedraw();
	}

	public void ClearLayers()
	{
		Log.Print("[Overlay] ClearLayers");
		foreach (var kv in _layerNodes)
			kv.Value.QueueFree();
		_layerNodes.Clear();
		_layersContainer.Visible = false;
	}

	public void SetRegionVisible(string name, bool visible)
	{
		Log.Print($"[Overlay] SetRegionVisible {name}={visible}");
		var region = GetRegion(name);
		if (region != null)
		{
			region.Visible = visible;
			QueueRedraw();
		}
		else
		{
			Log.Warn($"[Overlay] SetRegionVisible: region '{name}' not found");
		}
	}

	public override void _Draw()
	{
		if (!Visible) return;
		var size = Size;
		if (size.X <= 0 || size.Y <= 0) return;

		if (_mode == OverlayMode.Layout)
		{
			foreach (var reg in Regions)
			{
				if (!reg.Visible) continue;
				DrawBox(reg);
			}
		}
		else if (_mode == OverlayMode.Editing && _pipMode != PipEditMode.None)
		{
			var color = _pipMode == PipEditMode.Camera ? new Color(1, 0.84f, 0, 0.8f) : new Color(1, 0.4f, 0.7f, 0.8f);
			var handleColor = new Color(color.R, color.G, color.B, 0.9f);
			var lr = new Rect2(_pipPos * size, _pipSize * size);
			DrawRect(lr, color, false, 2);
			float half = LayerHandleSize / 2f;
			foreach (var p in GetPipCornersPx())
				DrawRect(new Rect2(p.X - half, p.Y - half, LayerHandleSize, LayerHandleSize), handleColor);
		}
		else if (_mode == OverlayMode.Editing && _activeClip != null)
		{
			bool isOverlay = _activeClip.ClipType == ClipType.Text ||
							 _activeClip.ClipType == ClipType.Image ||
							 _activeClip.ClipType == ClipType.Gif;
			if (!isOverlay) return;
			var pos = _activeClip.Position * size;
			var sz = _activeClip.Size * size;
			var center = pos + sz * 0.5f;
			float rotDeg = _activeClip.Rotation.StaticValue;
			if (Math.Abs(rotDeg) > 0.5f)
			{
				DrawSetTransform(center, Mathf.DegToRad(rotDeg), Vector2.One);
				DrawRect(new Rect2(-sz * 0.5f, sz), new Color(0.34f, 0.65f, 1, 0.8f), false, 2);
				float half = LayerHandleSize / 2f;
				foreach (var p in GetRotatedCorners(-sz * 0.5f, sz))
					DrawRect(new Rect2(p.X - half, p.Y - half, LayerHandleSize, LayerHandleSize), new Color(0.34f, 0.65f, 1, 0.9f));
				DrawSetTransform(Vector2.Zero, 0, Vector2.One);
			}
			else
			{
				var lr = new Rect2(pos, sz);
				DrawRect(lr, new Color(0.34f, 0.65f, 1, 0.8f), false, 2);
				float half = LayerHandleSize / 2f;
				foreach (var p in GetLayerCornersPx())
					DrawRect(new Rect2(p.X - half, p.Y - half, LayerHandleSize, LayerHandleSize), new Color(0.34f, 0.65f, 1, 0.9f));
			}
		}
	}

	private Vector2[] GetLayerCornersPx()
	{
		var ds = Size;
		var pos = _activeClip!.Position * ds;
		var sz = _activeClip.Size * ds;
		return new[] { pos, new Vector2(pos.X + sz.X, pos.Y), new Vector2(pos.X, pos.Y + sz.Y), pos + sz };
	}

	private Vector2[] GetRotatedCorners(Vector2 origin, Vector2 size)
	{
		return new[]
		{
			origin,
			new Vector2(origin.X + size.X, origin.Y),
			new Vector2(origin.X, origin.Y + size.Y),
			origin + size,
		};
	}

	private Vector2[] GetPipCornersPx()
	{
		var ds = Size;
		var pos = _pipPos * ds;
		var sz = _pipSize * ds;
		return new[] { pos, new Vector2(pos.X + sz.X, pos.Y), new Vector2(pos.X, pos.Y + sz.Y), pos + sz };
	}

	private void DrawBox(OverlayRegion reg)
	{
		var r = new Rect2(reg.Rect.Position * Size, reg.Rect.Size * Size);
		var color = reg.Color;

		// Bracket corners (tech look)
		float l = 20f;
		DrawLine(r.Position, r.Position + new Vector2(l, 0), color, 2);
		DrawLine(r.Position, r.Position + new Vector2(0, l), color, 2);
		DrawLine(r.End, r.End - new Vector2(l, 0), color, 2);
		DrawLine(r.End, r.End - new Vector2(0, l), color, 2);

		// Technical label (semi-transparent dark bar)
		var font = ThemeDB.FallbackFont ?? Theme.GetDefaultFont();
		var labelText = reg.Name.ToUpper();
		var textSize = font.GetStringSize(labelText, HorizontalAlignment.Left, -1, 10);
		var labelBg = new Rect2(r.Position.X, r.Position.Y - 24, textSize.X + 15, 20);
		if (labelBg.Position.Y < 0) labelBg.Position = new Vector2(labelBg.Position.X, 0);
		DrawRect(labelBg, new Color(0, 0, 0, 0.7f), true);
		DrawRect(labelBg, color, false, 1);
		DrawString(font, labelBg.Position + new Vector2(7, 14), labelText, HorizontalAlignment.Left, -1, 10, color);

		// Handles
		float hs = 10f;
		float half = hs / 2;
		foreach (var p in GetCornerPoints(r))
			DrawRect(new Rect2(p.X - half, p.Y - half, hs, hs), HandleColor);
	}

	private Vector2[] GetCornerPoints(Rect2 r) => new[] {
		r.Position,
		new Vector2(r.End.X, r.Position.Y),
		new Vector2(r.Position.X, r.End.Y),
		r.End
	};

	public override void _GuiInput(InputEvent @event)
	{
		if (_mode == OverlayMode.Layout)
		{
			HandleLayoutInput(@event);
		}
		else if (_mode == OverlayMode.Editing)
		{
			HandleEditingInput(@event);
		}
	}

	private void HandleLayoutInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
		{
			if (mb.Pressed)
			{
				var hit = HitTest(mb.Position);
				if (hit.region >= 0)
				{
					_dragRegion = hit.region;
					_dragHandle = hit.handle;
					_dragOrigRect = Regions[_dragRegion].Rect;

					var posNorm = mb.Position / Size;
					_dragClickOffset = posNorm - _dragOrigRect.Position;

					AcceptEvent();
				}
			}
			else if (_dragRegion >= 0)
			{
				EmitSignal(SignalName.LayoutChanged, Regions[_dragRegion].Name);
				_dragRegion = -1;
				_dragHandle = DragHandle.None;
			}
		}
		else if (@event is InputEventMouseMotion mm)
		{
			if (_dragRegion >= 0)
			{
				ProcessDrag(mm.Position);
				AcceptEvent();
			}
			else
			{
				var hit = HitTest(mm.Position);
				if (hit.handle == DragHandle.Move) MouseDefaultCursorShape = CursorShape.Drag;
				else if (hit.handle != DragHandle.None) MouseDefaultCursorShape = CursorShape.Fdiagsize;
				else MouseDefaultCursorShape = CursorShape.Arrow;
			}
		}
	}

	private void HandleEditingInput(InputEvent @event)
	{
		if (_pipMode != PipEditMode.None)
		{
			HandlePipDrag(@event);
			return;
		}
		if (_activeClip == null) return;

		if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
		{
			if (mb.Pressed)
			{
				var mpos = mb.Position;
				var ds = Size;
				if (ds.X <= 0) return;

				// Check corner handles
				var corners = new[]
				{
					_activeClip.Position * ds,
					new Vector2((_activeClip.Position.X + _activeClip.Size.X) * ds.X, _activeClip.Position.Y * ds.Y),
					new Vector2(_activeClip.Position.X * ds.X, (_activeClip.Position.Y + _activeClip.Size.Y) * ds.Y),
					(_activeClip.Position + _activeClip.Size) * ds,
				};
				for (int i = 0; i < corners.Length; i++)
				{
					if (corners[i].DistanceTo(mpos) < LayerHandleGrab)
					{
						_isDraggingLayer = true;
						_layerDragCorner = i;
						_layerDragOrigPos = _activeClip.Position;
						_layerDragOrigSize = _activeClip.Size;
						_layerDragStart = mpos;
						AcceptEvent();
						return;
					}
				}

				// Check body
				var clipRect = new Rect2(_activeClip.Position * ds, _activeClip.Size * ds);
				if (clipRect.HasPoint(mpos))
				{
					_isDraggingLayer = true;
					_layerDragCorner = -1;
					_layerDragOrigPos = _activeClip.Position;
					_layerDragStart = mpos;
					AcceptEvent();
				}
			}
			else if (_isDraggingLayer)
			{
				_isDraggingLayer = false;
				_layerDragCorner = -1;
			}
		}
		else if (@event is InputEventMouseMotion mm && _isDraggingLayer)
		{
			var delta = (mm.Position - _layerDragStart) / Size;
			var ds = Size;

			if (_layerDragCorner < 0)
			{
				// Move
				_activeClip.Position = (_layerDragOrigPos + delta).Clamp(Vector2.Zero, Vector2.One - _activeClip.Size);
				_activeClip.PosX.StaticValue = _activeClip.Position.X;
				_activeClip.PosY.StaticValue = _activeClip.Position.Y;
			}
			else
			{
				// Resize
				Vector2 newSize = _layerDragOrigSize;
				Vector2 newPos = _layerDragOrigPos;

				switch (_layerDragCorner)
				{
					case 0: newPos += delta; newSize -= delta; break;
					case 1: newPos.Y += delta.Y; newSize.X += delta.X; newSize.Y -= delta.Y; break;
					case 2: newPos.X += delta.X; newSize.X -= delta.X; newSize.Y += delta.Y; break;
					case 3: newSize += delta; break;
				}

				float min = 0.05f;
				newSize = newSize.Clamp(new Vector2(min, min), Vector2.One);
				newPos = newPos.Clamp(Vector2.Zero, Vector2.One - newSize);

				_activeClip.Size = newSize;
				_activeClip.Position = newPos;
			}

			UpdateLayerVisibility();
			QueueRedraw();
			AcceptEvent();
		}
	}

	private void HandlePipDrag(InputEvent @event)
	{
		if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
		{
			if (mb.Pressed)
			{
				var mpos = mb.Position;
				var ds = Size;
				if (ds.X <= 0) return;

				var corners = GetPipCornersPx();
				for (int i = 0; i < corners.Length; i++)
				{
					if (corners[i].DistanceTo(mpos) < LayerHandleGrab)
					{
						_isDraggingPip = true;
						_pipDragCorner = i;
						_pipDragOrigPos = _pipPos;
						_pipDragOrigSize = _pipSize;
						_pipDragStart = mpos;
						AcceptEvent();
						return;
					}
				}

				var pipRect = new Rect2(_pipPos * ds, _pipSize * ds);
				if (pipRect.HasPoint(mpos))
				{
					_isDraggingPip = true;
					_pipDragCorner = -1;
					_pipDragOrigPos = _pipPos;
					_pipDragStart = mpos;
					AcceptEvent();
				}
			}
			else if (_isDraggingPip)
			{
				_isDraggingPip = false;
				_pipDragCorner = -1;
			}
		}
		else if (@event is InputEventMouseMotion mm && _isDraggingPip)
		{
			var delta = (mm.Position - _pipDragStart) / Size;
			var ds = Size;

			if (_pipDragCorner < 0)
			{
				_pipPos = (_pipDragOrigPos + delta).Clamp(Vector2.Zero, Vector2.One - _pipSize);
			}
			else
			{
				Vector2 newSize = _pipDragOrigSize;
				Vector2 newPos = _pipDragOrigPos;

				switch (_pipDragCorner)
				{
					case 0: newPos += delta; newSize -= delta; break;
					case 1: newPos.Y += delta.Y; newSize.X += delta.X; newSize.Y -= delta.Y; break;
					case 2: newPos.X += delta.X; newSize.X -= delta.X; newSize.Y += delta.Y; break;
					case 3: newSize += delta; break;
				}

				float min = 0.05f;
				newSize = newSize.Clamp(new Vector2(min, min), Vector2.One);
				newPos = newPos.Clamp(Vector2.Zero, Vector2.One - newSize);

				_pipPos = newPos;
				_pipSize = newSize;
			}

			if (_pipMode == PipEditMode.Camera)
				EmitSignal(SignalName.CameraPipChanged, _pipPos, _pipSize);
			else
				EmitSignal(SignalName.UiPipChanged, _pipPos, _pipSize);

			QueueRedraw();
			AcceptEvent();
		}
	}

	private void ProcessDrag(Vector2 mousePos)
	{
		var posNorm = mousePos / Size;
		var reg = Regions[_dragRegion];
		var r = _dragOrigRect;

		if (_dragHandle == DragHandle.Move)
		{
			var newPos = posNorm - _dragClickOffset;
			reg.Rect = new Rect2(newPos.Clamp(Vector2.Zero, Vector2.One - r.Size), r.Size);
		}
		else
		{
			var x = r.Position.X;
			var y = r.Position.Y;
			var w = r.Size.X;
			var h = r.Size.Y;

			switch (_dragHandle)
			{
				case DragHandle.TL:
					w += (x - posNorm.X); h += (y - posNorm.Y);
					x = posNorm.X; y = posNorm.Y;
					break;
				case DragHandle.TR:
					w = posNorm.X - x; h += (y - posNorm.Y);
					y = posNorm.Y;
					break;
				case DragHandle.BL:
					w += (x - posNorm.X); x = posNorm.X;
					h = posNorm.Y - y;
					break;
				case DragHandle.BR:
					w = posNorm.X - x; h = posNorm.Y - y;
					break;
			}

			w = Mathf.Clamp(w, MinSize, MaxSize);
			h = Mathf.Clamp(h, MinSize, MaxSize);
			x = Mathf.Clamp(x, 0, 1 - w);
			y = Mathf.Clamp(y, 0, 1 - h);

			reg.Rect = new Rect2(x, y, w, h);
		}
		QueueRedraw();
	}

	private (int region, DragHandle handle) HitTest(Vector2 pos)
	{
		float handleGrabRadius = 15f;

		for (int i = Regions.Count - 1; i >= 0; i--)
		{
			var reg = Regions[i];
			if (!reg.Visible) continue;

			var r = new Rect2(reg.Rect.Position * Size, reg.Rect.Size * Size);
			var corners = GetCornerPoints(r);

			if (pos.DistanceTo(corners[0]) < handleGrabRadius) return (i, DragHandle.TL);
			if (pos.DistanceTo(corners[1]) < handleGrabRadius) return (i, DragHandle.TR);
			if (pos.DistanceTo(corners[2]) < handleGrabRadius) return (i, DragHandle.BL);
			if (pos.DistanceTo(corners[3]) < handleGrabRadius) return (i, DragHandle.BR);

			if (r.HasPoint(pos)) return (i, DragHandle.Move);
		}
		return (-1, DragHandle.None);
	}
}
