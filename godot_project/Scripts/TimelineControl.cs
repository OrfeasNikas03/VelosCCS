// Custom timeline widget: renders tracks with colored clip bars, waveform
// visualization, ruler with time markers, playhead (red when playing, cyan
// otherwise), selection marker, loop region highlight, and snap guides.
// Handles mouse input for seek, select (single/marquee), trim, move clips,
// zoom (Shift+scroll), and pan (scroll).

using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace VelosCCS;

public enum TimelineTool { Select, Razor }

public partial class TimelineControl : Control
{
	[Signal] public delegate void SeekRequestedEventHandler(double time);
	[Signal] public delegate void ClipSelectedEventHandler(int index);
	[Signal] public delegate void SelectionChangedEventHandler(int[] selectedIndices);
	[Signal] public delegate void TrimChangedEventHandler(double start, double end);
	[Signal] public delegate void ClipMovedEventHandler(int index, double newStart, double newEnd, int newTrackIdx);
	[Signal] public delegate void LoopRegionChangedEventHandler(double start, double end, bool enabled);
	[Signal] public delegate void SplitRequestedEventHandler();
	[Signal] public delegate void ContextMenuRequestedEventHandler(int flatIndex, Vector2 globalPosition);
	[Signal] public delegate void DragFinishedEventHandler();
	[Signal] public delegate void TrackReorderedEventHandler(int fromIndex, int toIndex);
	[Signal] public delegate void TrackRenameRequestedEventHandler(int trackIndex, string currentName);
	[Signal] public delegate void AssetDroppedEventHandler(double time, int assetIndex);

	public double Duration { get; private set; } = 60;
	public double PlayheadPos { get; private set; }
	public double SelectionPos { get; private set; }
	public double Zoom { get; set; } = 10;
	public double Scroll { get; set; }
	public TimelineTool CurrentTool
	{
		get => _currentTool;
		set
		{
			_currentTool = value;
			MouseDefaultCursorShape = value == TimelineTool.Razor
				? CursorShape.Cross
				: CursorShape.Arrow;
		}
	}
	private TimelineTool _currentTool = TimelineTool.Select;
	public bool IsPlaying { get; set; }
	public double ProjectDuration { get; private set; } = 60;

	// Loop region
	public double LoopStart { get; set; } = 0;
	public double LoopEnd { get; set; } = 10;
	public bool LoopEnabled { get; set; }

	private const int RulerH = 22;
	private const int TrackH = 28;
	private const int TrackGap = 2;
	private const int TrackHeader = 56;
	private const float HandleW = 8f;

	private enum DragMode { None, TrimStart, TrimEnd, MoveClip, Seek, Marquee, SetLoop }
	private DragMode _currentDrag = DragMode.None;

	private List<ClipData> _clips = new();
	private int _selectedIdx = -1; // primary selection (last clicked, for trim handles / inspector)
	private readonly HashSet<int> _selectedIndices = new();

	private int _dragStartIdx = -1;
	private Vector2 _dragStartPos;
	private Rect2 _marqueeRect;

	private double _snapThreshold = 8.0;
	private double _loopDragAnchor;
	private float _snapIndicatorX = -1f;

	private int _trackCount;
	private float _vScroll;

	private int _dragTrackIdx = -1;
	private int _dragTrackOrigIdx = -1;
	private float _dragTrackStartY;
	private bool _trackHeaderClicked;
	private double _lastTrackHeaderClickTime;

	public int[] GetSelectedIndices() => _selectedIndices.ToArray();
	public int GetPrimarySelected() => _selectedIdx;

	public override void _Ready()
	{
		Log.Print("[Timeline] _Ready");
		ClipContents = true;
	}

	public override void _Notification(int what)
	{
		if (what == NotificationResized)
		{
			Log.Print($"[Timeline] Resized to {Size}");
			float maxV = Math.Max(0, TotalHeight - Size.Y + 10);
			if (_vScroll > maxV) _vScroll = maxV;
		}
	}

	private int TrackAreaTop => RulerH + 4;
	private int TrackAreaHeight => _trackCount * (TrackH + TrackGap) - TrackGap;
	private int TotalHeight => TrackAreaTop + TrackAreaHeight + 4;

	private double T2Px(double t) => TrackHeader + (t - Scroll) * Zoom;
	private double Px2T(float px) => (px - TrackHeader) / Zoom + Scroll;

	public double GetVisibleDuration() => (Size.X - TrackHeader) / Zoom;

	public void StepRelative(float percent)
	{
		Log.Print($"[Timeline] StepRelative percent={percent}");
		double step = GetVisibleDuration() * percent;
		EmitSignal(SignalName.SeekRequested, Mathf.Clamp(SelectionPos + step, 0, Duration));
	}

	public void StepFrame(int direction, float fps = 30f)
	{
		Log.Print($"[Timeline] StepFrame dir={direction} fps={fps}");
		double frameTime = 1.0 / fps;
		EmitSignal(SignalName.SeekRequested, Mathf.Clamp(SelectionPos + (direction * frameTime), 0, Duration));
	}

	public void AutoZoomToClip(double start, double duration)
	{
		Log.Print($"[Timeline] AutoZoomToClip start={start} duration={duration}");
		double availW = Size.X - TrackHeader - 40;
		Zoom = availW / duration;
		Scroll = start - (20 / Zoom);
		SelectionPos = start;
		QueueRedraw();
	}

	public void SetSelection(double time)
	{
		Log.Print($"[Timeline] SetSelection time={time}");
		SelectionPos = Mathf.Clamp(time, 0, Duration);
		QueueRedraw();
	}

	public void SetDuration(double d) { Log.Print($"[Timeline] SetDuration {d}"); Duration = Math.Max(d, 1); QueueRedraw(); }
	public void SetPlayhead(double t) { Log.Print($"[Timeline] SetPlayhead {t}"); PlayheadPos = t; QueueRedraw(); }

	public void SyncSelectionToPlayhead()
	{
		Log.Print($"[Timeline] SyncSelectionToPlayhead pos={PlayheadPos}");
		SelectionPos = PlayheadPos;
		QueueRedraw();
	}

	public void SetSelectedClip(int flatIndex)
	{
		Log.Print($"[Timeline] SetSelectedClip index={flatIndex}");
		_selectedIndices.Clear();
		_selectedIndices.Add(flatIndex);
		_selectedIdx = flatIndex;
		EmitSignal(SignalName.ClipSelected, flatIndex);
		EmitSignal(SignalName.SelectionChanged, _selectedIndices.ToArray());
		QueueRedraw();
	}

	public void SetClips(List<ClipData> clips, int selected)
	{
		Log.Print($"[Timeline] SetClips count={clips.Count} selected={selected}");
		_clips = clips;
		_selectedIdx = selected;
		_selectedIndices.Clear();
		if (selected >= 0) _selectedIndices.Add(selected);
		_trackCount = clips.Count > 0 ? clips.Max(c => c.TrackIndex) + 1 : 1;
		CustomMinimumSize = new Vector2(0, Math.Min(TotalHeight, 300));
		float maxV = Math.Max(0, TotalHeight - Size.Y + 10);
		if (_vScroll > maxV) _vScroll = maxV;
		// Re-clamp scroll after clip changes to prevent blank/out-of-bounds view
		double maxScroll = Math.Max(0, ProjectDuration - GetVisibleDuration());
		Scroll = Mathf.Clamp(Scroll, 0, maxScroll);
		QueueRedraw();
	}

	// Update duration based on tracks: longest clip end + 5s buffer
	public void UpdateProjectDuration(List<TrackData> tracks, double videoDuration)
	{
		Log.Print($"[Timeline] UpdateProjectDuration videoDuration={videoDuration}");
		double maxEnd = videoDuration;
		foreach (var t in tracks)
		{
			foreach (var c in t.Clips)
			{
				if (c.End > maxEnd) maxEnd = c.End;
			}
		}
		ProjectDuration = maxEnd + 60.0;
		SetDuration(ProjectDuration);
		// Re-clamp scroll now that project duration may have changed
		double maxScroll = Math.Max(0, ProjectDuration - GetVisibleDuration());
		Scroll = Mathf.Clamp(Scroll, 0, maxScroll);
		QueueRedraw();
	}

	// Collect all snap points: clip starts/ends, selection/playhead, ruler ticks
	private List<double> GetSnapPoints(double excludeStart, double excludeEnd)
	{
		var points = new List<double>();
		for (int i = 0; i < _clips.Count; i++)
		{
			var c = _clips[i];
			if (Math.Abs(c.Start - excludeStart) < 0.001 && Math.Abs(c.End - excludeEnd) < 0.001)
				continue;
			points.Add(c.Start);
			points.Add(c.End);
		}
		points.Add(SelectionPos);
		if (IsPlaying) points.Add(PlayheadPos);

		double visDur = GetVisibleDuration();
		double rulerStep = visDur <= 10 ? 1 : visDur <= 60 ? 5 : 10;
		double firstTime = Math.Max(0, Math.Floor(Scroll / rulerStep) * rulerStep);
		for (double t = firstTime; t <= Scroll + visDur; t += rulerStep)
			points.Add(t);

		return points;
	}

	private double SnapTime(double time, List<double> snapPoints, out bool didSnap)
	{
		double threshold = _snapThreshold / Zoom;

		// 1. Priority Snapping: Snap to Playhead or Selection Marker first
		double[] priorities = { PlayheadPos, SelectionPos };
		foreach (var p in priorities)
		{
			if (Math.Abs(time - p) < threshold)
			{
				didSnap = true;
				return p;
			}
		}

		// 2. Secondary Snapping: Clip edges and ruler ticks
		double bestDelta = double.MaxValue;
		foreach (var pt in snapPoints)
		{
			double delta = Math.Abs(time - pt);
			if (delta < threshold && delta < Math.Abs(bestDelta))
				bestDelta = pt - time;
		}
		didSnap = Math.Abs(bestDelta) < threshold;
		return didSnap ? time + bestDelta : time;
	}

	// _Draw: render ruler, tracks, clip bars, type icons, display names, waveforms,
	// trim handles, loop highlight, selection marker, playhead, snap indicator, marquee
	public override void _Draw()
	{
		var font = ThemeDB.FallbackFont;
		if (font == null) return;

		var w = Size.X;
		var h = Size.Y;

		DrawRect(new Rect2(0, 0, w, h), new Color(0.12f, 0.12f, 0.12f));
		DrawRect(new Rect2(0, 0, w, RulerH), new Color(0.1f, 0.1f, 0.1f));

		// Loop region highlight
		if (LoopEnabled)
		{
			var lx1 = (float)T2Px(LoopStart);
			var lx2 = (float)T2Px(LoopEnd);
			if (lx2 > TrackHeader && lx1 < w)
				DrawRect(new Rect2(lx1, 0, lx2 - lx1, h), new Color(0.3f, 0.6f, 1.0f, 0.12f));
		}

		// Ruler
		double visDur = GetVisibleDuration();
		double rulerStep = visDur <= 10 ? 1 : visDur <= 30 ? 5 : visDur <= 120 ? 10 : visDur <= 600 ? 30 : visDur <= 3600 ? 60 : 300;
		double firstTime = Math.Max(0, Math.Floor(Scroll / rulerStep) * rulerStep);
		for (double t = firstTime; t <= Math.Min(Scroll + visDur, ProjectDuration); t += rulerStep)
		{
			var x = (float)T2Px(t);
			if (x < TrackHeader || x > w) continue;
			DrawLine(new Vector2(x, 0), new Vector2(x, 5), new Color(0.6f, 0.6f, 0.6f));
			DrawString(font, new Vector2(x + 2, RulerH - 5), FormatSec(t),
					   HorizontalAlignment.Left, -1, 8, new Color(0.7f, 0.7f, 0.7f));
		}

		// Group clips by track
		var trackClips = new Dictionary<int, List<(int flatIdx, ClipData clip)>>();
		for (int i = 0; i < _clips.Count; i++)
		{
			var ti = _clips[i].TrackIndex;
			if (!trackClips.ContainsKey(ti)) trackClips[ti] = new();
			trackClips[ti].Add((i, _clips[i]));
		}

		// Detect overlapping clips on the same track
		var overlapping = new HashSet<int>();
		foreach (var kv in trackClips)
		{
			var items = kv.Value.OrderBy(x => x.clip.Start).ToList();
			for (int i = 1; i < items.Count; i++)
			{
				if (items[i].clip.Start < items[i - 1].clip.End)
				{
					overlapping.Add(items[i].flatIdx);
					overlapping.Add(items[i - 1].flatIdx);
				}
			}
		}

		foreach (var kv in trackClips.OrderBy(k => k.Key))
		{
			int ti = kv.Key;
			var items = kv.Value;
			int laneY = TrackAreaTop + ti * (TrackH + TrackGap) - (int)_vScroll;

			// Track label
			var labelText = items[0].clip.TrackName;
			if (string.IsNullOrEmpty(labelText)) labelText = $"Track {ti + 1}";
			var labelBg = new Rect2(0, laneY, TrackHeader, TrackH);
			DrawRect(labelBg, new Color(0.15f, 0.15f, 0.15f));

			// Track type badge
			string badge = labelText switch
			{
				string n when n.StartsWith("Video") => "V",
				string n when n.StartsWith("Audio") => "A",
				string n when n.StartsWith("Stickers") => "S",
				string n when n.StartsWith("Captions") => "C",
				_ => "T",
			};
			var badgeCol = labelText switch
			{
				string n when n.StartsWith("Video") => new Color(0.2f, 0.5f, 0.8f),
				string n when n.StartsWith("Audio") => new Color(0.8f, 0.5f, 0.2f),
				string n when n.StartsWith("Stickers") => new Color(0.1f, 0.7f, 0.3f),
				string n when n.StartsWith("Captions") => new Color(0.6f, 0.4f, 0.1f),
				_ => new Color(0.4f, 0.4f, 0.4f),
			};
			DrawRect(new Rect2(4, laneY + 5, 18, 18), badgeCol);
			DrawString(font, new Vector2(9, laneY + 18), badge, HorizontalAlignment.Left, -1, 10, Colors.White);
			DrawString(font, new Vector2(28, laneY + TrackH / 2 + 4), labelText, HorizontalAlignment.Left, -1, 9, new Color(0.6f, 0.6f, 0.6f));

			// Draw clips in this track
			foreach (var (flatIdx, c) in items)
			{
				float rawX1 = (float)T2Px(c.Start);
				float rawX2 = (float)T2Px(c.End);
				if (rawX2 < TrackHeader || rawX1 > w) continue;
				float x1 = Mathf.Max(rawX1, TrackHeader);
				float x2 = Mathf.Min(rawX2, w);
				bool isSel = _selectedIndices.Contains(flatIdx);

				// Clip color by type
				var col = c.Type switch
				{
					ClipType.Text => isSel ? new Color(0.6f, 0.4f, 0.1f, 0.85f) : new Color(0.6f, 0.4f, 0.1f, 0.4f),
					ClipType.Image or ClipType.Gif => isSel ? new Color(0.1f, 0.7f, 0.3f, 0.85f) : new Color(0.1f, 0.7f, 0.3f, 0.4f),
					_ => isSel ? new Color(0.2f, 0.5f, 0.8f, 0.85f) : new Color(0.2f, 0.5f, 0.8f, 0.4f),
				};
				DrawRect(new Rect2(x1, laneY + 1, x2 - x1, TrackH - 2), col);
				if (isSel)
					DrawRect(new Rect2(x1, laneY + 1, x2 - x1, TrackH - 2), Colors.Cyan, false, 2);

				// Overlap warning: red tint + diagonal stripes
				if (overlapping.Contains(flatIdx))
				{
					var r = new Rect2(x1, laneY + 1, x2 - x1, TrackH - 2);
					DrawRect(r, new Color(1f, 0.2f, 0.2f, 0.15f));
					DrawRect(r, new Color(1f, 0.2f, 0.2f, 0.5f), false, 1);
				}

				// Type icon
				if (x2 - x1 > 18)
				{
					string icon = c.Type switch
					{
						ClipType.Text => "T",
						ClipType.Image => "■",
						ClipType.Gif => "▶",
						ClipType.Audio => "♪",
						_ => "",
					};
					if (!string.IsNullOrEmpty(icon))
						DrawString(font, new Vector2(x1 + 4, laneY + TrackH - 7), icon, HorizontalAlignment.Left, -1, 10, Colors.White);
				}

				// Display name
				if (x2 - x1 > 30 && !string.IsNullOrEmpty(c.DisplayName))
				{
					const float textOffsetX = 22f;
					float nameMaxW = x2 - x1 - textOffsetX - 4;
					if (nameMaxW > 10)
					{
						var name = c.DisplayName;
						DrawString(font, new Vector2(x1 + textOffsetX, laneY + 12), name, HorizontalAlignment.Left, (int)nameMaxW, 8, new Color(0.9f, 0.9f, 0.9f));
					}
				}

				// Waveform (draw each peak individually for full detail at all zoom levels)
				if (c.WaveformPeaks is { Count: > 0 })
				{
					var centerY = laneY + TrackH / 2f;
					var maxAmp = (TrackH - 6) / 2f;
					double clipDur = c.End - c.Start;
					int n = c.WaveformPeaks.Count;
					for (int pi = 0; pi < n; pi++)
					{
						double t = (double)pi / n;
						float pxx = (float)T2Px(c.Start + t * clipDur);
						if (pxx < TrackHeader) continue;
						if (pxx > rawX2 || pxx > w) break;
						float peak = c.WaveformPeaks[pi];
						float display = MathF.Pow(peak, 0.5f);
						float halfH = display * maxAmp;
						DrawLine(new Vector2(pxx, centerY - halfH), new Vector2(pxx, centerY + halfH), new Color(1, 1, 1, 0.5f), 1f);
					}
				}
				// Keyframe diamonds
				if (c.KeyframeTimes is { Count: > 0 })
				{
					foreach (double kt in c.KeyframeTimes)
					{
						float kx = (float)T2Px(kt);
						if (kx < x1 || kx > x2) continue;
						float ky = laneY + TrackH / 2f;
						float d = 5f;
						var gold = new Color(1, 0.8f, 0.2f);
						DrawLine(new Vector2(kx, ky - d), new Vector2(kx + d, ky), gold, 2.5f);
						DrawLine(new Vector2(kx + d, ky), new Vector2(kx, ky + d), gold, 2.5f);
						DrawLine(new Vector2(kx, ky + d), new Vector2(kx - d, ky), gold, 2.5f);
						DrawLine(new Vector2(kx - d, ky), new Vector2(kx, ky - d), gold, 2.5f);
						// Fill diamond
						DrawCircle(new Vector2(kx, ky), 2f, gold);
					}
				}

			}

			// Trim handles for primary selected clip in this track
			if (_selectedIdx >= 0 && _selectedIdx < _clips.Count)
			{
				var selC = _clips[_selectedIdx];
				if (selC.TrackIndex == ti)
				{
					var sx = (float)T2Px(selC.Start);
					var ex = (float)T2Px(selC.End);
					DrawRect(new Rect2(sx - HandleW / 2, laneY + 1, HandleW, TrackH - 2), new Color(1, 1, 0, 0.9f));
					DrawRect(new Rect2(ex - HandleW / 2, laneY + 1, HandleW, TrackH - 2), new Color(1, 1, 0, 0.9f));
				}
			}

			// Lane separator
			DrawLine(new Vector2(TrackHeader, laneY + TrackH), new Vector2(w, laneY + TrackH), new Color(0.08f, 0.08f, 0.08f));
		}

		// Selection marker (Vegas Edit Point) - Bright White with circle flag
		var selX = (float)T2Px(SelectionPos);
		if (selX >= TrackHeader && selX <= w)
		{
			DrawCircle(new Vector2(selX, RulerH), 4f, Colors.White);
			DrawLine(new Vector2(selX, RulerH), new Vector2(selX, h), Colors.White, 1.5f);
		}

		// Playhead (red during playback, always visible for loop)
		var px = (float)T2Px(PlayheadPos);
		if (px >= TrackHeader && px <= w)
			DrawLine(new Vector2(px, 0), new Vector2(px, h), IsPlaying ? Colors.Red : Color.FromHtml("#D0570C"), 2f);

		// Snap indicator (yellow vertical line during drag)
		if (_snapIndicatorX >= 0)
			DrawLine(new Vector2(_snapIndicatorX, 0), new Vector2(_snapIndicatorX, h), new Color(1, 1, 0, 0.7f), 1f);

		// Marquee selection box
		if (_currentDrag == DragMode.Marquee)
			DrawRect(_marqueeRect, new Color(1, 1, 1, 0.15f), false, 1);
	}

	// Input handler: zoom (shift+scroll), pan (scroll), left mouse for seek/select/
	// trim/move/marquee, ruler click for loop region, drag updates for all modes.
	public override void _GuiInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton mb)
		{
			// Shift + Scroll for pivot zoom
			if (mb.ShiftPressed && (mb.ButtonIndex == MouseButton.WheelUp || mb.ButtonIndex == MouseButton.WheelDown))
			{
				double zoomFactor = 1.2;
				double pivotTime = Px2T(mb.Position.X);

				if (mb.ButtonIndex == MouseButton.WheelUp) Zoom *= zoomFactor;
				if (mb.ButtonIndex == MouseButton.WheelDown) Zoom /= zoomFactor;

				Zoom = Mathf.Clamp(Zoom, 1.0, 5000.0);

				Scroll = pivotTime - (mb.Position.X - TrackHeader) / Zoom;
				double maxScroll = Math.Max(0, ProjectDuration - GetVisibleDuration());
				Scroll = Mathf.Clamp(Scroll, 0, maxScroll);

				QueueRedraw();
				AcceptEvent();
				return;
			}

			// Vertical scroll with Ctrl+wheel
			if (mb.CtrlPressed && (mb.ButtonIndex == MouseButton.WheelUp || mb.ButtonIndex == MouseButton.WheelDown))
			{
				float step = TrackH + TrackGap;
				_vScroll += mb.ButtonIndex == MouseButton.WheelDown ? step : -step;
				float maxV = Math.Max(0, TotalHeight - Size.Y + 10);
				_vScroll = Math.Clamp(_vScroll, 0, maxV);
				QueueRedraw();
				AcceptEvent();
				return;
			}

			// Regular scroll for horizontal panning
			if (mb.ButtonIndex == MouseButton.WheelUp || mb.ButtonIndex == MouseButton.WheelDown)
			{
				float pixelStep = 80f;
				double timeStep = pixelStep / Zoom;

				if (mb.ButtonIndex == MouseButton.WheelUp)
					Scroll -= timeStep;
				else
					Scroll += timeStep;

				Scroll = Mathf.Clamp(Scroll, 0, Math.Max(0, ProjectDuration - GetVisibleDuration()));

				QueueRedraw();
				AcceptEvent();
				return;
			}

			if (mb.ButtonIndex == MouseButton.Right && mb.Pressed)
			{
				int hitIdx = GetClipAtPos(mb.Position);
				if (hitIdx >= 0)
				{
					// Select the right-clicked clip
					_selectedIndices.Clear();
					_selectedIndices.Add(hitIdx);
					_selectedIdx = hitIdx;
					EmitSignal(SignalName.ClipSelected, hitIdx);
					EmitSignal(SignalName.SelectionChanged, _selectedIndices.ToArray());
					QueueRedraw();

					EmitSignal(SignalName.ContextMenuRequested, hitIdx, GetScreenPosition() + mb.Position);
					AcceptEvent();
					return;
				}
			}

			if (mb.ButtonIndex == MouseButton.Left)
			{
				if (mb.Pressed)
				{
					_dragStartPos = mb.Position;
					_snapIndicatorX = -1f;

					double clickedTime = Px2T(mb.Position.X);
					clickedTime = Mathf.Clamp(clickedTime, 0, Duration);

					// Shift+click seeks; shift+drag creates loop region
					if (mb.ShiftPressed)
					{
						SelectionPos = clickedTime;
						EmitSignal(SignalName.SeekRequested, SelectionPos);
						_loopDragAnchor = clickedTime;
						_currentDrag = DragMode.SetLoop;
						LoopStart = clickedTime;
						LoopEnd = clickedTime;
						LoopEnabled = true;
						QueueRedraw();
						AcceptEvent();
						return;
					}

					// Razor tool: split clip at click position
					if (CurrentTool == TimelineTool.Razor)
					{
						int hitIdx = GetClipAtPos(mb.Position);
						if (hitIdx >= 0)
						{
							SelectionPos = clickedTime;
							EmitSignal(SignalName.SeekRequested, SelectionPos);
							EmitSignal(SignalName.SplitRequested);
						}
						QueueRedraw();
						AcceptEvent();
						return;
					}

					// Track header: drag to reorder, double-click to rename
					int trackHit = HitTestTrack(mb.Position.Y);
					if (mb.Position.X < TrackHeader && trackHit >= 0)
					{
						double now = Time.GetTicksMsec() / 1000.0;
						if (now - _lastTrackHeaderClickTime < 0.4)
						{
							// Double-click: rename
							_lastTrackHeaderClickTime = 0;
							EmitSignal(SignalName.TrackRenameRequested, trackHit, GetTrackNameAt(trackHit));
							QueueRedraw();
							AcceptEvent();
							return;
						}
						_lastTrackHeaderClickTime = now;

						_dragTrackOrigIdx = trackHit;
						_dragTrackIdx = trackHit;
						_dragTrackStartY = mb.Position.Y;
						_trackHeaderClicked = true;
						_currentDrag = DragMode.MoveClip; // Reuse MoveClip mode internally
						QueueRedraw();
						AcceptEvent();
						return;
					}

					// Select tool
					int hitIdx2 = GetClipAtPos(mb.Position);
					if (hitIdx2 >= 0)
					{
						// Ctrl+click: toggle into multi-selection
						if (mb.CtrlPressed)
						{
							if (_selectedIndices.Contains(hitIdx2))
								_selectedIndices.Remove(hitIdx2);
							else
								_selectedIndices.Add(hitIdx2);
							_selectedIdx = _selectedIndices.Count > 0 ? _selectedIndices.Last() : hitIdx2;
							EmitSignal(SignalName.SelectionChanged, _selectedIndices.ToArray());
						}
						else
						{
							if (!_selectedIndices.Contains(hitIdx2))
								_selectedIndices.Clear();
							_selectedIndices.Add(hitIdx2);
							_selectedIdx = hitIdx2;
							EmitSignal(SignalName.ClipSelected, hitIdx2);
							EmitSignal(SignalName.SelectionChanged, _selectedIndices.ToArray());
						}

						// Ensure _selectedIdx is still valid before using it
						if (_selectedIdx < 0 || _selectedIdx >= _clips.Count) { _selectedIdx = hitIdx2; }
						// Check trim handles on primary selected clip
						var c = _clips[_selectedIdx];
						if (c.TrackIndex == _clips[hitIdx2].TrackIndex)
						{
							var sx = (float)T2Px(c.Start);
							var ex = (float)T2Px(c.End);
							if (Math.Abs(mb.Position.X - sx) < HandleW + 4) { _currentDrag = DragMode.TrimStart; _dragStartIdx = _selectedIdx; QueueRedraw(); AcceptEvent(); return; }
							if (Math.Abs(mb.Position.X - ex) < HandleW + 4) { _currentDrag = DragMode.TrimEnd; _dragStartIdx = _selectedIdx; QueueRedraw(); AcceptEvent(); return; }
						}

						_currentDrag = DragMode.MoveClip;
						_dragStartIdx = hitIdx2;
						QueueRedraw();
						AcceptEvent();
						return;
					}
					else
					{
						// Clicked in ruler area → seek
						if (mb.Position.Y < RulerH)
						{
							SelectionPos = clickedTime;
							EmitSignal(SignalName.SeekRequested, SelectionPos);
							QueueRedraw();
							AcceptEvent();
							return;
						}
						// Clicked empty space in a track → marquee only
						if (HitTestTrack(mb.Position.Y) >= 0)
						{
							_selectedIndices.Clear();
							_selectedIdx = -1;
							EmitSignal(SignalName.SelectionChanged, Array.Empty<int>());
							_currentDrag = DragMode.Marquee;
							_marqueeRect = new Rect2(mb.Position, Vector2.Zero);
						}
						QueueRedraw();
						AcceptEvent();
						return;
					}
				}
				else
				{
					// Mouse release
					if (_currentDrag == DragMode.SetLoop)
					{
						if (Math.Abs(LoopEnd - LoopStart) < 0.1) LoopEnabled = false;
						EmitSignal(SignalName.LoopRegionChanged, LoopStart, LoopEnd, LoopEnabled);
					}
					else if (_currentDrag is DragMode.TrimStart or DragMode.TrimEnd && _selectedIdx >= 0)
					{
						var clip = _clips[_selectedIdx];
						EmitSignal(SignalName.TrimChanged, clip.Start, clip.End);
					}
					else if (_trackHeaderClicked)
					{
						_trackHeaderClicked = false;
						if (_dragTrackOrigIdx >= 0 && _dragTrackIdx >= 0 && _dragTrackOrigIdx != _dragTrackIdx)
							EmitSignal(SignalName.TrackReordered, _dragTrackOrigIdx, _dragTrackIdx);
						_dragTrackIdx = -1;
						_dragTrackOrigIdx = -1;
					}
					else if (_currentDrag == DragMode.MoveClip && _selectedIndices.Count > 0)
					{
						foreach (int idx in _selectedIndices.ToArray())
						{
							var clip = _clips[idx];
							EmitSignal(SignalName.ClipMoved, idx, clip.Start, clip.End, clip.TrackIndex);
						}
					}
					_currentDrag = DragMode.None;
					_snapIndicatorX = -1f;
					_dragStartIdx = -1;
					EmitSignal(SignalName.DragFinished);
					QueueRedraw();
				}
			}
			return;
		}

		if (@event is InputEventMouseMotion mm && _currentDrag != DragMode.None)
		{
			if (_currentDrag == DragMode.SetLoop)
			{
				double cur = Mathf.Clamp(Px2T(mm.Position.X), 0, Duration);
				LoopStart = Math.Min(_loopDragAnchor, cur);
				LoopEnd = Math.Max(_loopDragAnchor, cur);
				QueueRedraw();
				return;
			}

			var t = Math.Clamp(Px2T(mm.Position.X), 0, 36000);

			if (_currentDrag == DragMode.TrimStart && _selectedIdx >= 0)
			{
				var clip = _clips[_selectedIdx];
				var snapPoints = GetSnapPoints(clip.Start, clip.End);
				double newStart = SnapTime(t, snapPoints, out bool snapped);
				newStart = Math.Min(newStart, clip.End - 0.1);
				clip.Start = (float)newStart;
				_clips[_selectedIdx] = clip;
				_snapIndicatorX = snapped ? (float)T2Px(newStart) : -1f;
				EmitSignal(SignalName.TrimChanged, clip.Start, clip.End);
				QueueRedraw();
			}
			else if (_currentDrag == DragMode.TrimEnd && _selectedIdx >= 0)
			{
				var clip = _clips[_selectedIdx];
				var snapPoints = GetSnapPoints(clip.Start, clip.End);
				double newEnd = SnapTime(t, snapPoints, out bool snapped);
				newEnd = Math.Clamp(newEnd, clip.Start + 0.1, 36000);
				clip.End = (float)newEnd;
				_clips[_selectedIdx] = clip;
				_snapIndicatorX = snapped ? (float)T2Px(newEnd) : -1f;
				EmitSignal(SignalName.TrimChanged, clip.Start, clip.End);
				QueueRedraw();
			}
			else if (_currentDrag == DragMode.MoveClip && _selectedIndices.Count > 0)
			{
				double deltaT = mm.Relative.X / Zoom;
				int newTrack = HitTestTrack(mm.Position.Y);
				int? originTrack = _dragStartIdx >= 0 && _dragStartIdx < _clips.Count
					? _clips[_dragStartIdx].TrackIndex : null;

				foreach (int idx in _selectedIndices.ToArray())
				{
					var clip = _clips[idx];
					double newStart = clip.Start + deltaT;
					double newEnd = clip.End + deltaT;
					if (newStart < 0) { newStart = 0; newEnd = clip.End - clip.Start; }
					if (newEnd > 36000) { newEnd = 36000; newStart = 36000 - (clip.End - clip.Start); }
					// Dynamically expand timeline if dragging past current end
					if (newEnd > Duration) {
						SetDuration(newEnd + 10);
						ProjectDuration = newEnd + 10;
					}
					clip.Start = (float)newStart;
					clip.End = (float)newEnd;
					// Only change track for clips from the same origin track
					if (newTrack >= 0 && originTrack.HasValue && clip.TrackIndex == originTrack.Value)
						clip.TrackIndex = newTrack;
					_clips[idx] = clip;
				}

				// Snap only the primary clip
				if (_selectedIdx >= 0)
				{
					var pc = _clips[_selectedIdx];
					var snapPoints = GetSnapPoints(pc.Start, pc.End);
					double snappedStart = SnapTime(pc.Start, snapPoints, out bool snapped);
					if (snapped)
					{
						double snapDelta = snappedStart - pc.Start;
						foreach (int idx in _selectedIndices.ToArray())
						{
							var clip = _clips[idx];
							clip.Start = (float)(clip.Start + snapDelta);
							clip.End = (float)(clip.End + snapDelta);
							_clips[idx] = clip;
						}
						_snapIndicatorX = (float)T2Px(snappedStart);
					}
					else { _snapIndicatorX = -1f; }
				}

				QueueRedraw();
			}
			else if (_currentDrag == DragMode.Marquee)
			{
				_marqueeRect = new Rect2(_dragStartPos, mm.Position - _dragStartPos).Abs();
				UpdateMarqueeSelection();
				QueueRedraw();
			}
			else if (_trackHeaderClicked && _dragTrackOrigIdx >= 0)
			{
				int newTrack = HitTestTrack(mm.Position.Y);
				if (newTrack >= 0 && newTrack != _dragTrackIdx)
				{
					_dragTrackIdx = newTrack;
					QueueRedraw();
				}
			}
			else if (_currentDrag == DragMode.Seek) { SeekToMouse(mm.Position.X); }
		}
	}

	public void ClearSnapping() { Log.Print("[Timeline] ClearSnapping"); _snapIndicatorX = -1f; QueueRedraw(); }

	public void ClearClips()
	{
		Log.Print("[Timeline] ClearClips");
		_clips.Clear();
		_selectedIndices.Clear();
		_selectedIdx = -1;
		_trackCount = 0;
		QueueRedraw();
	}

	public void SelectAllClips()
	{
		Log.Print("[Timeline] SelectAllClips");
		_selectedIndices.Clear();
		_selectedIdx = -1;
		for (int i = 0; i < _clips.Count; i++)
		{
			_selectedIndices.Add(i);
			if (_selectedIdx < 0) _selectedIdx = i;
		}
		EmitSignal(SignalName.SelectionChanged, _selectedIndices.ToArray());
		QueueRedraw();
	}

	// Select clips intersecting the marquee rectangle
	private void UpdateMarqueeSelection()
	{
		_selectedIndices.Clear();
		_selectedIdx = -1;
		for (int i = 0; i < _clips.Count; i++)
		{
			var c = _clips[i];
			var rect = new Rect2(
				(float)T2Px(c.Start),
				TrackAreaTop + c.TrackIndex * (TrackH + TrackGap) - _vScroll,
				(float)((c.End - c.Start) * Zoom),
				TrackH);
			if (_marqueeRect.Intersects(rect))
			{
				_selectedIndices.Add(i);
				if (_selectedIdx < 0) _selectedIdx = i;
			}
		}
		EmitSignal(SignalName.SelectionChanged, _selectedIndices.ToArray());
	}

	private int GetClipAtPos(Vector2 pos)
	{
		for (int i = _clips.Count - 1; i >= 0; i--)
		{
			var c = _clips[i];
			var x1 = T2Px(c.Start);
			var x2 = T2Px(c.End);
			var y1 = TrackAreaTop + c.TrackIndex * (TrackH + TrackGap) - _vScroll;
			if (pos.X >= x1 && pos.X <= x2 && pos.Y >= y1 && pos.Y <= y1 + TrackH)
				return i;
		}
		return -1;
	}

	private int HitTestTrack(float mouseY)
	{
		if (mouseY < TrackAreaTop) return -1;
		int ti = (int)((mouseY - TrackAreaTop + _vScroll) / (TrackH + TrackGap));
		if (ti < 0 || ti >= _trackCount) return -1;
		float laneY = TrackAreaTop + ti * (TrackH + TrackGap) - _vScroll;
		if (mouseY > laneY + TrackH) return -1;
		return ti;
	}

	private string GetTrackNameAt(int trackIdx)
	{
		var clip = _clips.FirstOrDefault(c => c.TrackIndex == trackIdx);
		return clip.TrackName;
	}

	private void SeekToMouse(float mouseX)
	{
		EmitSignal(SignalName.SeekRequested, Math.Clamp(Px2T(mouseX), 0, Duration));
	}

	private static string FormatSec(double s)
	{
		var ts = TimeSpan.FromSeconds(s);
		return ts.Hours > 0
			? $"{ts.Hours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
			: $"{ts.Minutes}:{ts.Seconds:D2}";
	}

	public override bool _CanDropData(Vector2 atPosition, Variant data)
	{
		if (atPosition.Y < RulerH) return false;
		return data.Obj is Godot.Collections.Dictionary dict && dict.ContainsKey("asset_index");
	}

	public override void _DropData(Vector2 atPosition, Variant data)
	{
		Log.Print($"[Timeline] _DropData pos={atPosition}");
		if (data.Obj is Godot.Collections.Dictionary dict && dict.TryGetValue("asset_index", out var idxVal))
		{
			double time = Math.Clamp(Px2T(atPosition.X), 0, Duration);
			EmitSignal(SignalName.AssetDropped, time, (int)idxVal);
		}
	}
}
