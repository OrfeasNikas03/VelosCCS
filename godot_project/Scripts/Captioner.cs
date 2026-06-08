using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VelosCCS;

public class CaptionStyle
{
    public string FontName { get; set; } = "Arial";
    public int FontSize { get; set; } = 36;
    public string PrimaryColor { get; set; } = "#FFFFFF";
    public string OutlineColor { get; set; } = "#000000";
    public double OutlineWidth { get; set; } = 1.5;
    public string Position { get; set; } = "bottom";
    public bool Bold { get; set; }
}

public class Captioner
{
    private readonly CaptionStyle _style;

    public Captioner(CaptionStyle? style = null)
    {
        _style = style ?? new CaptionStyle();
    }

    public void CreateAss(List<Segment> segments, string outputPath,
        int videoWidth = 1080, int videoHeight = 1920)
    {
        string primary = HexToAss(_style.PrimaryColor);
        string outline = HexToAss(_style.OutlineColor);

        var sb = new StringBuilder();
        sb.AppendLine("[Script Info]");
        sb.AppendLine("ScriptType: v4.00+");
        sb.AppendLine($"PlayResX: {videoWidth}");
        sb.AppendLine($"PlayResY: {videoHeight}");
        sb.AppendLine("ScaledBorderAndShadow: yes");
        sb.AppendLine();
        sb.AppendLine("[V4+ Styles]");
        sb.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, " +
            "SecondaryColour, OutlineColour, BackColour, Bold, Italic, " +
            "Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, " +
            "BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, " +
            "MarginV, Encoding");
        sb.AppendLine($"Style: Default,{_style.FontName},{_style.FontSize}," +
            $"{primary},{primary},{outline},{outline}," +
            $"{(_style.Bold ? "-1" : "0")},0,0,0,100,100,0,0,1," +
            $"{_style.OutlineWidth:F1},0,2,30,30,40,1");
        sb.AppendLine();
        sb.AppendLine("[Events]");
        sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, " +
            "MarginV, Effect, Text");

        foreach (var seg in segments)
        {
            string start = ToAssTime(seg.Start);
            string end = ToAssTime(seg.End);
            string text = seg.Text
                .Replace("\n", "\\N")
                .Replace("{", "\\{")
                .Replace("}", "\\}");
            sb.AppendLine($"Dialogue: 0,{start},{end},Default,,0,0,0,,{text}");
        }

        File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
    }

    public void CreateSrt(List<Segment> segments, string outputPath)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            sb.AppendLine($"{i + 1}");
            sb.AppendLine($"{ToSrtTime(seg.Start)} --> {ToSrtTime(seg.End)}");
            sb.AppendLine(seg.Text);
            sb.AppendLine();
        }
        File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
    }

    private static string HexToAss(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6)
            return $"&H00{hex[4..6]}{hex[2..4]}{hex[..2]}";
        return "&H00FFFFFF";
    }

    private static string ToAssTime(double seconds)
    {
        int h = (int)(seconds / 3600);
        int m = (int)((seconds % 3600) / 60);
        int s = (int)(seconds % 60);
        int cs = (int)((seconds - Math.Floor(seconds)) * 100);
        return $"{h}:{m:D2}:{s:D2}.{cs:D2}";
    }

    private static string ToSrtTime(double seconds)
    {
        int h = (int)(seconds / 3600);
        int m = (int)((seconds % 3600) / 60);
        int s = (int)(seconds % 60);
        int ms = (int)((seconds - Math.Floor(seconds)) * 1000);
        return $"{h:D2}:{m:D2}:{s:D2},{ms:D3}";
    }
}
