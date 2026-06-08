// Data model for imported assets in the project bin (Media tab).
// Stores file path, type, duration, optional thumbnail, and waveform peaks.

using Godot;
using System.Collections.Generic;

namespace VelosCCS;

public enum AssetType { Video, Audio, Font, Text, Image }

public class MediaAsset
{
    public string Name { get; set; }
    public string Path { get; set; }
    public AssetType Type { get; set; }
    public double Duration { get; set; }

    public ImageTexture? Thumbnail { get; set; }
    public List<float>? WaveformPeaks { get; set; }

    // Timestamp range for transcribed caption clips; used to re-place clips at the
    // correct audio-synced position when re-added from the media bin after deletion.
    public double StartTime { get; set; }
    public double EndTime { get; set; }

    // Full text content for transcribed captions (bin Name stores truncated preview).
    public string? CaptionText { get; set; }

    public MediaAsset(string name, string path, AssetType type, double duration = 0)
    {
        Name = name;
        Path = path;
        Type = type;
        Duration = duration;
    }
}
