using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VelosCCS;

// ─── Serializable project data ──────────────────────────────────────────────

public class ProjectData
{
    public string FormatVersion { get; set; } = "1.0";
    public string? VideoPath { get; set; }
    public double VideoDuration { get; set; }
    public string ExportAspectRatio { get; set; } = "16:9";
    public string ExportOutputDir { get; set; } = "";
    public bool ExportNormalizeAudio { get; set; } = true;

    // Layout state
    public LayoutState Layout { get; set; } = new();

    // Media bin
    public List<SerializableAsset> Assets { get; set; } = new();

    // Tracks
    public List<SerializableTrack> Tracks { get; set; } = new();
}

public class LayoutState
{
    public int LayoutMode { get; set; }
    public bool BlurBg { get; set; }
    public bool ShowCameraOverlay { get; set; }
    public string SocialOverlay { get; set; } = "None";
    public float[] CameraOutput { get; set; } = new[] { 0.05f, 0.05f, 0.4f, 0.25f };
    public float[] UiOutput { get; set; } = new[] { 0.02f, 0.7f, 0.3f, 0.12f };
    public List<SerializableRegion> Regions { get; set; } = new();
}

public class SerializableRegion
{
    public string Name { get; set; } = "";
    public float[] Rect { get; set; } = new[] { 0f, 0f, 1f, 1f };
    public float[] Color { get; set; } = new[] { 1f, 1f, 1f, 1f };
    public bool Visible { get; set; } = true;
}

public class SerializableAsset
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Type { get; set; } = "Video";
    public double Duration { get; set; }
    public double StartTime { get; set; }
    public double EndTime { get; set; }
    public string? CaptionText { get; set; }
}

public class SerializableTrack
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "Video";
    public bool Muted { get; set; }
    public int ZIndex { get; set; }
    public List<SerializableClip> Clips { get; set; } = new();
}

public class SerializableClip
{
    public string ClipType { get; set; } = "SourceVideo";
    public double Start { get; set; }
    public double End { get; set; }
    public string SourceRegion { get; set; } = "gameplay";
    public string FilePath { get; set; } = "";
    public double RelStart { get; set; }
    public string Text { get; set; } = "";
    public int FontSize { get; set; } = 48;
    public string FontPath { get; set; } = "";
    public float[] FontColor { get; set; } = new[] { 1f, 1f, 1f, 1f };
    public float[] OutlineColor { get; set; } = new[] { 0f, 0f, 0f, 1f };
    public int OutlineWidth { get; set; } = 4;
    public float[] Position { get; set; } = new[] { 0.5f, 0.5f };
    public float[] Size { get; set; } = new[] { 1f, 1f };
    public float[] Color { get; set; } = new[] { 0.24f, 0.52f, 1f, 1f };
    public double FadeIn { get; set; }
    public double FadeOut { get; set; }
    public List<float> WaveformPeaks { get; set; } = new();
    public SerializableAnimProp Volume { get; set; } = new();
    public SerializableAnimProp PosX { get; set; } = new();
    public SerializableAnimProp PosY { get; set; } = new();
    public SerializableAnimProp Scale { get; set; } = new();
    public SerializableAnimProp Opacity { get; set; } = new();
    public SerializableAnimProp FontSizeAnim { get; set; } = new();
    public SerializableAnimProp Rotation { get; set; } = new();
    public List<SerializableTextKeyframe> TextKeyframes { get; set; } = new();
}

public class SerializableAnimProp
{
    public float StaticValue { get; set; } = 1f;
    public bool IsAnimated { get; set; }
    public List<SerializableKeyframe> Keyframes { get; set; } = new();
}

public class SerializableKeyframe
{
    public double Time { get; set; }
    public float Value { get; set; }
}

public class SerializableTextKeyframe
{
    public double Time { get; set; }
    public string Text { get; set; } = "";
    public string FontPath { get; set; } = "";
}

// ─── JSON converters for Godot types ────────────────────────────────────────

public class Vector2Converter : JsonConverter<Vector2>
{
    public override Vector2 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var arr = JsonSerializer.Deserialize<float[]>(ref reader, options);
        return arr != null && arr.Length >= 2 ? new Vector2(arr[0], arr[1]) : Vector2.Zero;
    }

    public override void Write(Utf8JsonWriter writer, Vector2 value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new[] { value.X, value.Y }, options);
    }
}

public class ColorConverter : JsonConverter<Color>
{
    public override Color Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var arr = JsonSerializer.Deserialize<float[]>(ref reader, options);
        return arr != null && arr.Length >= 4 ? new Color(arr[0], arr[1], arr[2], arr[3]) : Colors.White;
    }

    public override void Write(Utf8JsonWriter writer, Color value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new[] { value.R, value.G, value.B, value.A }, options);
    }
}

public class Rect2Converter : JsonConverter<Rect2>
{
    public override Rect2 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var arr = JsonSerializer.Deserialize<float[]>(ref reader, options);
        return arr != null && arr.Length >= 4 ? new Rect2(arr[0], arr[1], arr[2], arr[3]) : new Rect2();
    }

    public override void Write(Utf8JsonWriter writer, Rect2 value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new[] { value.Position.X, value.Position.Y, value.Size.X, value.Size.Y }, options);
    }
}

public class Vector4Converter : JsonConverter<Vector4>
{
    public override Vector4 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var arr = JsonSerializer.Deserialize<float[]>(ref reader, options);
        return arr != null && arr.Length >= 4 ? new Vector4(arr[0], arr[1], arr[2], arr[3]) : Vector4.Zero;
    }

    public override void Write(Utf8JsonWriter writer, Vector4 value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new[] { value.X, value.Y, value.Z, value.W }, options);
    }
}

// ─── Serialization helpers ──────────────────────────────────────────────────

public static class ProjectSerializer
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        IncludeFields = true,
        Converters =
        {
            new Vector2Converter(),
            new ColorConverter(),
            new Rect2Converter(),
            new Vector4Converter(),
        },
    };

    public static ProjectData Serialize(MainWindow main)
    {
        var data = new ProjectData
        {
            VideoPath = main.GetVideoPath(),
            VideoDuration = main.GetVideoDuration(),
            ExportAspectRatio = main.ExportAspectRatio,
            ExportOutputDir = main.ExportOutputDir,
            ExportNormalizeAudio = main.ExportNormalizeAudio,
        };

        // Layout state
        var preview = main.GetOutputPreview();
        data.Layout.LayoutMode = preview.LayoutMode;
        data.Layout.BlurBg = preview.BlurBg;
        data.Layout.ShowCameraOverlay = preview.GetShowCameraOverlay();
        data.Layout.SocialOverlay = preview.GetSocialOverlayName();

        var cam = preview.GetCameraTarget();
        data.Layout.CameraOutput = cam;
        var ui = preview.GetUiTarget();
        data.Layout.UiOutput = ui;

        foreach (var r in main.GetOverlayRegions())
        {
            data.Layout.Regions.Add(new SerializableRegion
            {
                Name = r.Name,
                Rect = new[] { r.Rect.Position.X, r.Rect.Position.Y, r.Rect.Size.X, r.Rect.Size.Y },
                Color = new[] { r.Color.R, r.Color.G, r.Color.B, r.Color.A },
                Visible = r.Visible,
            });
        }

        // Media bin
        foreach (var asset in main.GetProjectBin())
        {
            data.Assets.Add(new SerializableAsset
            {
                Name = asset.Name,
                Path = asset.Path,
                Type = asset.Type.ToString(),
                Duration = asset.Duration,
                StartTime = asset.StartTime,
                EndTime = asset.EndTime,
                CaptionText = asset.CaptionText,
            });
        }

        // Tracks
        foreach (var track in main.GetTracks())
        {
            var st = new SerializableTrack
            {
                Name = track.Name,
                Type = track.Type.ToString(),
                Muted = track.Muted,
                ZIndex = track.ZIndex,
            };
            foreach (var clip in track.Clips)
                st.Clips.Add(SerializeClip(clip));
            data.Tracks.Add(st);
        }

        return data;
    }

    private static SerializableClip SerializeClip(TrackClipData c)
    {
        return new SerializableClip
        {
            ClipType = c.ClipType.ToString(),
            Start = c.Start,
            End = c.End,
            SourceRegion = c.SourceRegion,
            FilePath = c.FilePath,
            RelStart = c.RelStart,
            Text = c.Text,
            FontSize = c.FontSize,
            FontPath = c.FontPath,
            FontColor = new[] { c.FontColor.R, c.FontColor.G, c.FontColor.B, c.FontColor.A },
            OutlineColor = new[] { c.OutlineColor.R, c.OutlineColor.G, c.OutlineColor.B, c.OutlineColor.A },
            OutlineWidth = c.OutlineWidth,
            Position = new[] { c.Position.X, c.Position.Y },
            Size = new[] { c.Size.X, c.Size.Y },
            Color = new[] { c.Color.R, c.Color.G, c.Color.B, c.Color.A },
            FadeIn = c.FadeIn,
            FadeOut = c.FadeOut,
            WaveformPeaks = new List<float>(c.WaveformPeaks),
            Volume = SerializeAnimProp(c.Volume),
            PosX = SerializeAnimProp(c.PosX),
            PosY = SerializeAnimProp(c.PosY),
            Scale = SerializeAnimProp(c.Scale),
            Opacity = SerializeAnimProp(c.Opacity),
            FontSizeAnim = SerializeAnimProp(c.FontSizeAnim),
            Rotation = SerializeAnimProp(c.Rotation),
            TextKeyframes = c.TextKeyframes.ConvertAll(tk => new SerializableTextKeyframe
            {
                Time = tk.Time,
                Text = tk.Text,
                FontPath = tk.FontPath,
            }),
        };
    }

    private static SerializableAnimProp SerializeAnimProp(AnimatableProperty p)
    {
        return new SerializableAnimProp
        {
            StaticValue = p.StaticValue,
            IsAnimated = p.IsAnimated,
            Keyframes = p.Keyframes.ConvertAll(k => new SerializableKeyframe { Time = k.Time, Value = k.Value }),
        };
    }

    // ─── Deserialization ──────────────────────────────────────────────────

    public static string ToJson(ProjectData data) =>
        JsonSerializer.Serialize(data, JsonOptions);

    public static ProjectData? FromJson(string json) =>
        JsonSerializer.Deserialize<ProjectData>(json, JsonOptions);

    // Reconstruct runtime objects from project data into the main window
    public static void DeserializeInto(ProjectData data, MainWindow main)
    {
        main.ClearAllState();

        // Video source
        if (data.VideoPath != null)
            main.SetVideoPath(data.VideoPath, data.VideoDuration);

        // Export settings
        main.ExportAspectRatio = data.ExportAspectRatio;
        main.ExportOutputDir = data.ExportOutputDir;
        main.ExportNormalizeAudio = data.ExportNormalizeAudio;

        // Media bin
        foreach (var sa in data.Assets)
        {
            var type = sa.Type switch
            {
                "Video" => AssetType.Video,
                "Audio" => AssetType.Audio,
                "Font" => AssetType.Font,
                "Text" => AssetType.Text,
                _ => AssetType.Video,
            };
            var asset = new MediaAsset(sa.Name, sa.Path, type, sa.Duration)
            {
                StartTime = sa.StartTime,
                EndTime = sa.EndTime,
                CaptionText = sa.CaptionText,
            };
            main.AddAssetToBin(asset);
        }

        // Tracks
        foreach (var st in data.Tracks)
        {
            var trackType = st.Type == "Audio" ? TrackType.Audio : TrackType.Video;
            var track = new TrackData
            {
                Name = st.Name,
                Type = trackType,
                Muted = st.Muted,
                ZIndex = st.ZIndex,
            };
            foreach (var sc in st.Clips)
                track.Clips.Add(DeserializeClip(sc));
            main.AddTrackDirect(track);
        }

        // Layout state
        var preview = main.GetOutputPreview();
        preview.SetAspectRatio(data.ExportAspectRatio);
        preview.SetLayoutMode(data.Layout.LayoutMode);
        preview.SetBlurBg(data.Layout.BlurBg);
        preview.SetCameraOutput(
            new Vector2(data.Layout.CameraOutput[0], data.Layout.CameraOutput[1]),
            new Vector2(data.Layout.CameraOutput[2], data.Layout.CameraOutput[3]));
        preview.SetUiOutput(
            new Vector2(data.Layout.UiOutput[0], data.Layout.UiOutput[1]),
            new Vector2(data.Layout.UiOutput[2], data.Layout.UiOutput[3]));
        preview.SetSocialOverlay(data.Layout.SocialOverlay);

        // Overlay regions
        main.ClearOverlayRegions();
        foreach (var sr in data.Layout.Regions)
        {
            main.AddOverlayRegion(new OverlayRegion
            {
                Name = sr.Name,
                Rect = new Rect2(sr.Rect[0], sr.Rect[1], sr.Rect[2], sr.Rect[3]),
                Color = new Color(sr.Color[0], sr.Color[1], sr.Color[2], sr.Color[3]),
                Visible = sr.Visible,
            });
        }

        // Update views
        main.UpdateAfterLoad();
    }

    private static TrackClipData DeserializeClip(SerializableClip sc)
    {
        var clipType = sc.ClipType switch
        {
            "SourceVideo" => ClipType.SourceVideo,
            "Text" => ClipType.Text,
            "Image" => ClipType.Image,
            "Gif" => ClipType.Gif,
            "Audio" => ClipType.Audio,
            _ => ClipType.SourceVideo,
        };
        return new TrackClipData
        {
            ClipType = clipType,
            Start = sc.Start,
            End = sc.End,
            SourceRegion = sc.SourceRegion,
            FilePath = sc.FilePath,
            RelStart = sc.RelStart,
            Text = sc.Text,
            FontSize = sc.FontSize,
            FontPath = sc.FontPath,
            FontColor = sc.FontColor.Length >= 4 ? new Color(sc.FontColor[0], sc.FontColor[1], sc.FontColor[2], sc.FontColor[3]) : Colors.White,
            OutlineColor = sc.OutlineColor.Length >= 4 ? new Color(sc.OutlineColor[0], sc.OutlineColor[1], sc.OutlineColor[2], sc.OutlineColor[3]) : Colors.Black,
            OutlineWidth = sc.OutlineWidth,
            Position = sc.Position.Length >= 2 ? new Vector2(sc.Position[0], sc.Position[1]) : new Vector2(0.5f, 0.5f),
            Size = sc.Size.Length >= 2 ? new Vector2(sc.Size[0], sc.Size[1]) : new Vector2(1f, 1f),
            Color = sc.Color.Length >= 4 ? new Color(sc.Color[0], sc.Color[1], sc.Color[2], sc.Color[3]) : Colors.DodgerBlue,
            FadeIn = sc.FadeIn,
            FadeOut = sc.FadeOut,
            WaveformPeaks = new List<float>(sc.WaveformPeaks),
            Volume = DeserializeAnimProp(sc.Volume),
            PosX = DeserializeAnimProp(sc.PosX),
            PosY = DeserializeAnimProp(sc.PosY),
            Scale = DeserializeAnimProp(sc.Scale),
            Opacity = DeserializeAnimProp(sc.Opacity),
            FontSizeAnim = DeserializeAnimProp(sc.FontSizeAnim),
            Rotation = DeserializeAnimProp(sc.Rotation),
            TextKeyframes = sc.TextKeyframes.ConvertAll(tk => new TextKeyframe
            {
                Time = tk.Time,
                Text = tk.Text,
                FontPath = tk.FontPath,
            }),
        };
    }

    private static AnimatableProperty DeserializeAnimProp(SerializableAnimProp p)
    {
        return new AnimatableProperty
        {
            StaticValue = p.StaticValue,
            IsAnimated = p.IsAnimated,
            Keyframes = p.Keyframes.ConvertAll(k => new Keyframe { Time = k.Time, Value = k.Value }),
        };
    }
}
