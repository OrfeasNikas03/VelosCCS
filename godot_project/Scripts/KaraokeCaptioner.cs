using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VelosCCS;

public class KaraokeStyle
{
    public string FontName { get; set; } = "Arial";
    public int FontSize { get; set; } = 38;
    public string PrimaryColor { get; set; } = "#888888";
    public string HighlightColor { get; set; } = "#FFD700";
    public string OutlineColor { get; set; } = "#000000";
    public double OutlineWidth { get; set; } = 1.5;
    public string Position { get; set; } = "bottom";
    public bool Bold { get; set; }
    public int MaxCharsPerLine { get; set; } = 28;
    public double BackgroundOpacity { get; set; } = 0.4;

    public static KaraokeStyle Default => new();

    public static readonly Dictionary<string, KaraokeStyle> Presets = new()
    {
        ["Clean"] = new() { FontName = "Arial", FontSize = 38, PrimaryColor = "#CCCCCC", HighlightColor = "#FFFFFF", OutlineColor = "#000000", OutlineWidth = 1.5, BackgroundOpacity = 0.3 },
        ["Bold"] = new() { FontName = "Impact", FontSize = 44, PrimaryColor = "#AAAAAA", HighlightColor = "#FF4500", OutlineColor = "#000000", OutlineWidth = 2.0, BackgroundOpacity = 0.5 },
        ["Gaming"] = new() { FontName = "Montserrat", FontSize = 40, PrimaryColor = "#8888FF", HighlightColor = "#00FF00", OutlineColor = "#000000", OutlineWidth = 1.8, BackgroundOpacity = 0.4 },
        ["Minimal"] = new() { FontName = "Helvetica", FontSize = 34, PrimaryColor = "#FFFFFF", HighlightColor = "#FFFF00", OutlineColor = "#222222", OutlineWidth = 1.0, BackgroundOpacity = 0.0 },
        ["Podcast"] = new() { FontName = "Georgia", FontSize = 36, PrimaryColor = "#DDDDDD", HighlightColor = "#00BFFF", OutlineColor = "#333333", OutlineWidth = 1.2, BackgroundOpacity = 0.25 },
        ["Neon"] = new() { FontName = "Arial Black", FontSize = 42, PrimaryColor = "#00FFFF", HighlightColor = "#FFFFFF", OutlineColor = "#FF00FF", OutlineWidth = 2.5, BackgroundOpacity = 0.6, Bold = true },
        ["Classic"] = new() { FontName = "Times New Roman", FontSize = 36, PrimaryColor = "#F5F5DC", HighlightColor = "#FFD700", OutlineColor = "#8B4513", OutlineWidth = 1.8, BackgroundOpacity = 0.3 },
        ["Modern"] = new() { FontName = "Segoe UI", FontSize = 40, PrimaryColor = "#E0E0E0", HighlightColor = "#4FC3F7", OutlineColor = "#1A1A2E", OutlineWidth = 1.2, BackgroundOpacity = 0.5 },
    };
}

public class KaraokeCaptioner
{
    private readonly KaraokeStyle _style;

    public KaraokeCaptioner(KaraokeStyle? style = null, string? preset = null)
    {
        if (preset != null && KaraokeStyle.Presets.TryGetValue(preset, out var p))
            _style = p;
        else if (style != null)
            _style = style;
        else
            _style = KaraokeStyle.Default;
    }

    public void CreateAss(
        List<Segment> segments,
        string outputPath,
        int videoWidth = 1080,
        int videoHeight = 1920,
        Action<string>? progressCallback = null)
    {
        progressCallback?.Invoke("Creating karaoke captions...");

        string colorPrimary = HexToAss(_style.PrimaryColor);
        string colorHighlight = HexToAss(_style.HighlightColor);
        string colorOutline = HexToAss(_style.OutlineColor);

        int marginV = _style.Position == "top"
            ? (int)(videoHeight * 0.05)
            : _style.FontSize + 80;

        int marginH = (int)(videoWidth * 0.04);

        string assHeader =
            "[Script Info]\n" +
            "ScriptType: v4.00+\n" +
            $"PlayResX: {videoWidth}\n" +
            $"PlayResY: {videoHeight}\n" +
            "ScaledBorderAndShadow: yes\n" +
            "WrapStyle: 2\n" +
            "\n" +
            "[V4+ Styles]\n" +
            "Format: Name, Fontname, Fontsize, PrimaryColour, " +
            "SecondaryColour, OutlineColour, BackColour, Bold, Italic, " +
            "Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, " +
            "BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, " +
            "MarginV, Encoding\n" +
            $"Style: Karaoke,{_style.FontName},{_style.FontSize}," +
            $"{colorPrimary},{colorHighlight}," +
            $"{colorOutline},{colorOutline}," +
            $"{(_style.Bold ? "-1" : "0")},0,0,0,100,100,0,0,1," +
            $"{_style.OutlineWidth:F1},0,2," +
            $"{marginH},{marginH},{marginV},1\n" +
            "\n" +
            "[Events]\n" +
            "Format: Layer, Start, End, Style, Name, MarginL, MarginR, " +
            "MarginV, Effect, Text\n";

        var lines = new List<string> { assHeader };

        foreach (var seg in segments)
        {
            if (seg.Words == null || seg.Words.Count == 0)
            {
                lines.Add(MakePlainEvent(seg.Start, seg.End, seg.Text));
                continue;
            }

            var wrappedLines = WrapWords(seg.Words, seg.Text, _style.MaxCharsPerLine);
            var karaokeLines = new List<string>();
            foreach (var (lineWords, _) in wrappedLines)
            {
                karaokeLines.Add(BuildKaraokeLine(lineWords));
            }

            string fullText = string.Join("\\N", karaokeLines);
            lines.Add(
                $"Dialogue: 0,{ToAssTime(seg.Start)}," +
                $"{ToAssTime(seg.End)},Karaoke,,0,0,0,,{fullText}\n"
            );
        }

        File.WriteAllText(outputPath, string.Concat(lines), System.Text.Encoding.UTF8);
    }

    private static string BuildKaraokeLine(List<Word> words)
    {
        var parts = new List<string>();
        foreach (var w in words)
        {
            int durationCs = Math.Max(1, (int)((w.End - w.Start) * 100));
            string escaped = w.Text.Replace("{", "\\{").Replace("}", "\\}");
            parts.Add($"{{\\k{durationCs}}}{escaped} ");
        }
        return string.Concat(parts).TrimEnd();
    }

    private static List<(List<Word> words, string text)> WrapWords(
        List<Word> words, string fullText, int maxChars)
    {
        var result = new List<(List<Word>, string)>();
        var currentWords = new List<Word>();
        int currentChars = 0;

        foreach (var w in words)
        {
            int wordLen = w.Text.Length + 1;
            if (currentChars + wordLen > maxChars && currentWords.Count > 0)
            {
                string lineText = string.Join(" ", currentWords.Select(wrd => wrd.Text));
                result.Add((new List<Word>(currentWords), lineText));
                currentWords = new List<Word> { w };
                currentChars = wordLen;
            }
            else
            {
                currentWords.Add(w);
                currentChars += wordLen;
            }
        }

        if (currentWords.Count > 0)
        {
            string lineText = string.Join(" ", currentWords.Select(wrd => wrd.Text));
            result.Add((currentWords, lineText));
        }

        return result;
    }

    private static string MakePlainEvent(double start, double end, string text)
    {
        string escaped = text
            .Replace("\n", "\\N")
            .Replace("{", "\\{")
            .Replace("}", "\\}");
        return $"Dialogue: 0,{ToAssTime(start)},{ToAssTime(end)},Karaoke,,0,0,0,,{escaped}\n";
    }

    private static string HexToAss(string hexColor)
    {
        hexColor = hexColor.TrimStart('#');
        if (hexColor.Length == 6)
        {
            string r = hexColor[..2], g = hexColor[2..4], b = hexColor[4..6];
            return $"&H00{b}{g}{r}";
        }
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
}
