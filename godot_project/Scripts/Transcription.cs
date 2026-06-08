using System;
using System.Collections.Generic;
using System.Linq;

namespace VelosCCS;

public class Word
{
    public string Text { get; set; } = "";
    public double Start { get; set; }
    public double End { get; set; }
    public double Probability { get; set; }
}

public class Segment
{
    public double Start { get; set; }
    public double End { get; set; }
    public string Text { get; set; } = "";
    public List<Word> Words { get; set; } = new();
}

public class Transcript
{
    public List<Segment> Segments { get; set; } = new();
    public string Language { get; set; } = "";
    public double Duration { get; set; }

    public string AsText()
    {
        return string.Join("\n", Segments.Select(s => s.Text));
    }

    public Segment? GetSegmentAt(double time)
    {
        return Segments.FirstOrDefault(s => s.Start <= time && time <= s.End);
    }

    public List<Word> AllWords()
    {
        return Segments.SelectMany(s => s.Words).ToList();
    }
}
