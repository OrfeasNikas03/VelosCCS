// Video playback logic: play/pause, seeking, loop, frame stepping, and
// real-time SFX audio playback synchronized to the timeline playhead.

using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace VelosCCS;

public partial class MainWindow
{
	// Periodic timer callback (every 250ms): sync playhead, detect playback stall,
	// handle loop, update overlay/preview/position label, and sync SFX.
	private void OnTimerTimeout()
	{
		if (_videoPlayer.Stream == null) return;

		double streamPos = _videoPlayer.StreamPosition;

		if (_isPlaying)
		{
			double delta = streamPos - _lastStreamPos;
			if (delta > 0 && delta < 10.0)
				_timelinePlayheadPos += delta;
		}
		_lastStreamPos = streamPos;

		double timelineEnd = GetTimelineEnd();

		if (_loopPlayback && _timeline.LoopEnabled && _timelinePlayheadPos >= _timeline.LoopEnd)
		{
			SeekVideo(_timeline.LoopStart);
			return;
		}

		if (_timelinePlayheadPos >= timelineEnd)
		{
			// GD.Print($"[Playback] END-OF-TIMELINE: pos={_timelinePlayheadPos:F3} end={timelineEnd:F3} isPlaying={_isPlaying}");
			_timelinePlayheadPos = timelineEnd;
			if (_isPlaying)
			{
				_videoPlayer.Paused = true;
				_isPlaying = false;
				SetPlayButtonText("Play");
				StopAllSfx();
			}
		}
		else if (_isPlaying && !_videoPlayer.Paused && Math.Abs(streamPos - _lastPlayheadPos) < 0.001)
		{
			// GD.Print($"[Playback] STALL: pos={streamPos:F3} last={_lastPlayheadPos:F3} — nudging +50ms");
			_timelinePlayheadPos += 0.05;
			_videoPlayer.StreamPosition = streamPos + 0.05;
		}

		ApplyEmptySpaceDisplay(_timelinePlayheadPos);

		_timeline.IsPlaying = _isPlaying;
		_timeline.SetPlayhead(_timelinePlayheadPos);
		_overlay.SetCurrentTime(_timelinePlayheadPos);
		_outputPreview.SetDisplayTime(_timelinePlayheadPos);
		_positionLabel.Text = $"{FormatTime(_timelinePlayheadPos)} / {FormatTime(timelineEnd)}";
		_lastPlayheadPos = streamPos;
		UpdateSfxPlayback(_timelinePlayheadPos);
	}

	private double GetTimelineEnd()
	{
		double end = 0;
		foreach (var t in _tracks)
			foreach (var c in t.Clips)
				if (c.End > end) end = c.End;
		return end > 0 ? end : _videoDuration;
	}

	// Check if playhead is inside any clip; show black + mute when in gaps.
	// Also switches the video player stream when entering a different video file.
	private void ApplyEmptySpaceDisplay(double currentPos)
	{
		var activeVideoClip = _tracks
			.Where(t => t.Type == TrackType.Video)
			.SelectMany(t => t.Clips)
			.FirstOrDefault(c => currentPos >= c.Start && currentPos < c.End);

		var activeAudioClip = _tracks
			.Where(t => t.Type == TrackType.Audio)
			.SelectMany(t => t.Clips)
			.FirstOrDefault(c => currentPos >= c.Start && currentPos < c.End);

		bool showVideo = activeVideoClip != null;
		// GD.Print($"[Playback] Tick: pos={currentPos:F3} activeVideo={(activeVideoClip != null)} start={activeVideoClip?.Start:F3} end={activeVideoClip?.End:F3} path={System.IO.Path.GetFileName(activeVideoClip?.FilePath ?? "")}");
		_sourceDisplay.Modulate = showVideo ? Colors.White : Colors.Black;
		_outputPreview.Modulate = showVideo ? Colors.White : Colors.Black;

		// Switch video player stream when entering a different clip
		// Handles both cross-file (SwitchVideoFile) and same-file (player finished) cases.
		if (activeVideoClip != null && !string.IsNullOrEmpty(activeVideoClip.FilePath))
		{
			if (activeVideoClip.FilePath != _lastLoadedVideoPath)
			{
				SwitchVideoFile(activeVideoClip.FilePath, currentPos - activeVideoClip.Start);
			}
			else if (_isPlaying && !_videoPlayer.IsPlaying())
			{
				// Same file, video player finished — seek to clip-relative position and restart
				double seekPos = currentPos - activeVideoClip.Start;
				double fileDur = _videoDuration;
				if (seekPos < fileDur)
				{
					_videoPlayer.StreamPosition = seekPos;
					_videoPlayer.Play();
					_lastStreamPos = _videoPlayer.StreamPosition;
				}
			}
		}

		// Smooth volume transitions to avoid crackling
		float targetDb;
		if (activeAudioClip != null)
		{
			float vol = activeAudioClip.Volume.StaticValue;
			targetDb = Mathf.LinearToDb(Mathf.Clamp(vol, 0.001f, 2f));
		}
		else
		{
			targetDb = -80;
		}
		float currentDb = _videoPlayer.VolumeDb;
		if (Math.Abs(currentDb - targetDb) > 1f)
			_videoPlayer.VolumeDb = Mathf.Lerp(currentDb, targetDb, 0.3f);
		else
			_videoPlayer.VolumeDb = targetDb;
	}

	// Load a different video file into the video player and seek to given position
	private string? _lastLoadedVideoPath;
	private void SwitchVideoFile(string filePath, double seekPos)
	{
		Log.Print($"[UI] SwitchVideoFile: {filePath}");
		bool wasPlaying = _isPlaying && !_videoPlayer.Paused;
		_videoPlayer.Paused = true;

		var stream = ResourceLoader.Load<VideoStream>(filePath);
		if (stream != null)
		{
			_videoPlayer.Stream = stream;
			_videoPlayer.StreamPosition = Math.Max(0, seekPos);

			// Re-fetch duration for this clip
			var clip = _tracks
				.Where(t => t.Type == TrackType.Video)
				.SelectMany(t => t.Clips)
				.FirstOrDefault(c => c.FilePath == filePath);
			double oldDur = _videoDuration;
			if (clip != null)
			{
				var asset = _projectBin.FirstOrDefault(a => a.Path == filePath);
				if (asset != null)
					_videoDuration = asset.Duration;
			}
			GD.Print($"[Playback] SwitchVideoFile: {System.IO.Path.GetFileName(filePath)} seek={seekPos:F3} oldDur={oldDur:F3} newDur={_videoDuration:F3} clipStart={clip?.Start:F3} clipEnd={clip?.End:F3}");

			if (wasPlaying)
			{
				_videoPlayer.Paused = false;
				if (!_videoPlayer.IsPlaying()) _videoPlayer.Play();
			}
			_lastStreamPos = _videoPlayer.StreamPosition;
			_lastLoadedVideoPath = filePath;
		}
	}

	private static string FormatTime(double seconds)
	{
		var ts = TimeSpan.FromSeconds(seconds);
		return ts.Hours > 0
			? $"{ts.Hours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
			: $"{ts.Minutes}:{ts.Seconds:D2}";
	}

	// Manage active SFX AudioStreamPlayers: start playback for clips that enter
	// the current time range, stop and clean up for clips that exit.
	private void UpdateSfxPlayback(double currentPos)
	{
		var overlapping = new HashSet<TrackClipData>();
		foreach (var track in _tracks)
		{
			if (track.Name == "Source Audio") continue; // played via VideoPlayer
			foreach (var clip in track.Clips)
			{
				if (clip.ClipType != ClipType.Audio) continue;
				if (string.IsNullOrEmpty(clip.FilePath)) continue;
				if (currentPos >= clip.Start && currentPos < clip.End)
					overlapping.Add(clip);
			}
		}

		var toRemove = new List<TrackClipData>();
		foreach (var kv in _activeSfxPlayers)
		{
			if (!overlapping.Contains(kv.Key))
			{
				kv.Value.Stop();
				RemoveChild(kv.Value);
				kv.Value.QueueFree();
				toRemove.Add(kv.Key);
			}
		}
		foreach (var key in toRemove)
			_activeSfxPlayers.Remove(key);

		foreach (var clip in overlapping)
		{
			if (_activeSfxPlayers.ContainsKey(clip)) continue;

			try
			{
				string ext = System.IO.Path.GetExtension(clip.FilePath).ToLowerInvariant();
				AudioStream? stream = null;

				if (ext == ".mp3")
				{
					byte[] data = FileAccess.GetFileAsBytes(clip.FilePath);
					if (data != null && data.Length > 0)
						stream = AudioStreamMP3.LoadFromBuffer(data);
				}
				else
				{
					stream = ResourceLoader.Load<AudioStream>(clip.FilePath);
				}

				if (stream == null) continue;

				var player = new AudioStreamPlayer();
				player.Stream = stream;
				player.VolumeDb = Mathf.LinearToDb(Mathf.Clamp(clip.Volume.StaticValue, 0.001f, 2f));
				AddChild(player);

				double offset = currentPos - clip.Start;
				player.Play(Mathf.Max(0, (float)offset));

				_activeSfxPlayers[clip] = player;
			}
			catch (Exception e)
			{
				GD.PrintErr($"[SFX] Failed to play {clip.FilePath}: {e.Message}");
			}
		}
	}

	// Seek video player to given time, update timeline/overlay/preview, stop SFX
	private void SeekVideo(double time)
	{
		Log.Print($"[UI] SeekVideo: {time}");
		if (_videoPlayer.Stream == null) return;

		_timelinePlayheadPos = time;
		_videoPlayer.StreamPosition = time;

		ApplyEmptySpaceDisplay(time);
		_timeline.SetPlayhead(time);
		_overlay.SetCurrentTime(time);
		_outputPreview.SetDisplayTime(time);
		_outputPreview.QueueRedraw();
		StopAllSfx();

		if (_videoPlayer.Paused)
		{
			_videoPlayer.Paused = false;
			if (!_videoPlayer.IsPlaying()) _videoPlayer.Play();
			CallDeferred(nameof(FinishSeek));
		}
		_lastStreamPos = _videoPlayer.StreamPosition;
	}

	private void FinishSeek()
	{
		_videoPlayer.Paused = true;
	}

	// Toggle playback: play from selection position, or stop and optionally
	// move selection to current playhead position (Enter/K shortcut behavior).
	private void SetPlayback(bool shouldPlay, bool moveSelectionToCurrent = false)
	{
		Log.Print($"[UI] SetPlayback: playing={shouldPlay}, pauseMove={moveSelectionToCurrent}");
		if (_videoPlayer.Stream == null) return;

		if (shouldPlay)
		{
			_videoPlayer.StreamPosition = _timeline.SelectionPos;
			_videoPlayer.Paused = false;
			if (!_videoPlayer.IsPlaying()) _videoPlayer.Play();
			_isPlaying = true;
			_timelinePlayheadPos = _timeline.SelectionPos;
			_lastStreamPos = _videoPlayer.StreamPosition;
			SetPlayButtonText("Stop");
		}
		else
		{
			_videoPlayer.Paused = true;
			_isPlaying = false;
			SetPlayButtonText("Play");
			StopAllSfx();

			if (moveSelectionToCurrent)
			{
				_timeline.SetSelection(_videoPlayer.StreamPosition);
				SetStatus("Selection moved to playhead", Color.FromHtml("#D0570C"));
			}
			else
			{
				SeekVideo(_timeline.SelectionPos);
			}
		}
		_timeline.QueueRedraw();
	}

	private void StopAllSfx()
	{
		foreach (var kv in _activeSfxPlayers)
		{
			kv.Value.Stop();
			RemoveChild(kv.Value);
			kv.Value.QueueFree();
		}
		_activeSfxPlayers.Clear();
	}

	private void SetPlayButtonText(string text)
	{
		_playBtn.Text = text;
		_layoutPlayBtn.Text = text;
	}

	// Step selection forward/backward by one frame (1/30s) or 5% of visible duration
	private void StepTimeline(int direction, bool isPercent)
	{
		double amount = isPercent ? (_timeline.GetVisibleDuration() * 0.05) : (1.0 / 30.0);
		double maxPos = Math.Max(_videoDuration, GetTimelineEnd());
		double newTime = Mathf.Clamp(_timeline.SelectionPos + (direction * amount), 0, maxPos);
		_timeline.SetSelection(newTime);
		SeekVideo(newTime);
	}
}
