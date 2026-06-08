// Data models for tracks, clips, keyframes, and animatable properties.
// TrackData holds a list of clips; TrackClipData holds full clip state
// including text, position, size, opacity, fade, keyframes, and waveform.
// ClipData is a lightweight flat struct used by TimelineControl for rendering.

using Godot;
using System.Collections.Generic;
using System.Linq;

namespace VelosCCS;

public enum TrackType { Video, Audio }
public enum ClipType { SourceVideo, Text, Image, Gif, Audio }

public class Keyframe
{
    public double Time;
    public float Value;
}

public class TextKeyframe
{
    public double Time;
    public string Text = "";
    public string FontPath = "";
}

public class AnimatableProperty
{
    public float StaticValue = 1.0f;
    public bool IsAnimated;
    public List<Keyframe> Keyframes = new();

    public float GetValueAt(double localTime)
    {
        if (!IsAnimated || Keyframes.Count == 0) return StaticValue;
        var sorted = Keyframes.OrderBy(k => k.Time).ToList();
        if (localTime <= sorted[0].Time) return sorted[0].Value;
        if (localTime >= sorted.Last().Time) return sorted.Last().Value;
        for (int i = 0; i < sorted.Count - 1; i++)
        {
            if (localTime >= sorted[i].Time && localTime <= sorted[i + 1].Time)
            {
                float t = (float)((localTime - sorted[i].Time) / (sorted[i + 1].Time - sorted[i].Time));
                return Mathf.Lerp(sorted[i].Value, sorted[i + 1].Value, t);
            }
        }
        return StaticValue;
    }
}

public class TrackData
{
    public string Name { get; set; } = "";
    public TrackType Type { get; set; } = TrackType.Video;
    public bool Muted { get; set; }
    public int ZIndex { get; set; }
    public List<TrackClipData> Clips { get; set; } = new();
}

public class TrackClipData
{
    public ClipType ClipType { get; set; } = ClipType.SourceVideo;
    public double Start { get; set; }
    public double End { get; set; }

    public string SourceRegion { get; set; } = "gameplay";

    public string Text { get; set; } = "";
    public int FontSize { get; set; } = 48;
    public string FontPath { get; set; } = "";
    public Color FontColor { get; set; } = Colors.White;
    public Color OutlineColor { get; set; } = Colors.Black;
    public int OutlineWidth { get; set; } = 4;
    public string FilePath { get; set; } = "";
    public AnimatableProperty Volume = new() { StaticValue = 1.0f };
    public double RelStart;

    public Vector2 Position { get; set; } = new(0.5f, 0.5f);
    public Vector2 Size { get; set; } = new(1.0f, 1.0f);
    public Color Color { get; set; } = Colors.DodgerBlue;

    public AnimatableProperty PosX = new() { StaticValue = 0.5f };
    public AnimatableProperty PosY = new() { StaticValue = 0.5f };
    public AnimatableProperty Scale = new() { StaticValue = 1.0f };
    public AnimatableProperty Opacity = new() { StaticValue = 1.0f };
    public AnimatableProperty FontSizeAnim = new() { StaticValue = 48f };
    public AnimatableProperty Rotation = new() { StaticValue = 0f };

    public List<TextKeyframe> TextKeyframes = new();
    public List<float> WaveformPeaks = new();
    public double FadeIn, FadeOut;
    public ImageTexture? CachedThumbnail;
    public bool ThumbnailRequested;

    public TrackClipData Clone()
    {
        return new TrackClipData
        {
            ClipType = ClipType,
            Start = Start,
            End = End,
            SourceRegion = SourceRegion,
            Text = Text,
            FontSize = FontSize,
            FontPath = FontPath,
            FontColor = FontColor,
            OutlineColor = OutlineColor,
            OutlineWidth = OutlineWidth,
            FilePath = FilePath,
            Position = Position,
            Size = Size,
            Color = Color,
            Volume = new AnimatableProperty { StaticValue = Volume.StaticValue, IsAnimated = Volume.IsAnimated, Keyframes = new List<Keyframe>(Volume.Keyframes) },
            PosX = new AnimatableProperty { StaticValue = PosX.StaticValue, IsAnimated = PosX.IsAnimated, Keyframes = new List<Keyframe>(PosX.Keyframes) },
            PosY = new AnimatableProperty { StaticValue = PosY.StaticValue, IsAnimated = PosY.IsAnimated, Keyframes = new List<Keyframe>(PosY.Keyframes) },
            Scale = new AnimatableProperty { StaticValue = Scale.StaticValue, IsAnimated = Scale.IsAnimated, Keyframes = new List<Keyframe>(Scale.Keyframes) },
            Opacity = new AnimatableProperty { StaticValue = Opacity.StaticValue, IsAnimated = Opacity.IsAnimated, Keyframes = new List<Keyframe>(Opacity.Keyframes) },
            FontSizeAnim = new AnimatableProperty { StaticValue = FontSizeAnim.StaticValue, IsAnimated = FontSizeAnim.IsAnimated, Keyframes = new List<Keyframe>(FontSizeAnim.Keyframes) },
            Rotation = new AnimatableProperty { StaticValue = Rotation.StaticValue, IsAnimated = Rotation.IsAnimated, Keyframes = new List<Keyframe>(Rotation.Keyframes) },
            WaveformPeaks = new List<float>(WaveformPeaks),
            TextKeyframes = new List<TextKeyframe>(TextKeyframes),
            FadeIn = FadeIn,
            FadeOut = FadeOut,
        };
    }

    public string GetTextAt(double localTime)
    {
        if (TextKeyframes.Count == 0) return Text;
        return TextKeyframes.OrderBy(k => k.Time).LastOrDefault(k => k.Time <= localTime)?.Text ?? Text;
    }

    public string GetFontPathAt(double localTime)
    {
        if (TextKeyframes.Count == 0) return FontPath;
        return TextKeyframes.OrderBy(k => k.Time).LastOrDefault(k => k.Time <= localTime)?.FontPath ?? FontPath;
    }

    public float GetFadeAt(double localTime)
    {
        double dur = End - Start;
        if (localTime < FadeIn && FadeIn > 0) return (float)(localTime / FadeIn);
        if (localTime > (dur - FadeOut) && FadeOut > 0) return (float)((dur - localTime) / FadeOut);
        return 1.0f;
    }
}

public struct ClipData
{
    public float Start;
    public float End;
    public List<float>? WaveformPeaks;
    public int TrackIndex;
    public string TrackName;
    public ClipType Type;
    public string DisplayName;
    public List<double>? KeyframeTimes;
    public ClipData(float start, float end, List<float>? peaks = null, int trackIdx = 0, string trackName = "", ClipType type = ClipType.SourceVideo, string displayName = "", List<double>? keyframeTimes = null)
    {
        Start = start; End = end; WaveformPeaks = peaks;
        TrackIndex = trackIdx; TrackName = trackName;
        Type = type; DisplayName = displayName;
        KeyframeTimes = keyframeTimes;
    }
}
