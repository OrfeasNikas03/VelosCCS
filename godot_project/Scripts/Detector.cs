using System;
using System.Collections.Generic;

namespace VelosCCS;

public class HighlightDetector
{
    private static readonly string[] ExcitementWords =
    {
        "wow", "oh my god", "omg", "no way", "what", "crazy",
        "unbelievable", "amazing", "insane", "let's go", "lets go",
        "holy", "damn", "wtf", "bruh", "bro",
    };

    private static readonly string[] QuestionWords =
    {
        "why", "how", "what", "who", "where", "when", "?",
    };

    public List<(double start, double end)> FindHighlights(
        List<Segment> segments,
        int maxClips = 5,
        double minDuration = 15.0,
        double maxDuration = 60.0)
    {
        if (segments == null || segments.Count == 0)
            return new();

        double windowSize = 30.0;
        double stride = 10.0;
        double totalDuration = segments[^1].End - segments[0].Start;

        var candidates = new List<(double start, double end, double score)>();
        double t = segments[0].Start;
        while (t + windowSize <= segments[^1].End + stride)
        {
            double windowEnd = t + windowSize;
            string text = string.Join(" ",
                segments.FindAll(s => s.Start < windowEnd && s.End > t)
                         .ConvertAll(s => s.Text ?? ""));
            double score = TextScore(text);
            candidates.Add((t, Math.Min(windowEnd, segments[^1].End), score));
            t += stride;
        }

        candidates.Sort((a, b) => b.score.CompareTo(a.score));

        var clips = new List<(double start, double end)>();
        var covered = new List<(double start, double end)>();

        foreach (var (start, end, _) in candidates)
        {
            double dur = end - start;
            if (dur < minDuration || dur > maxDuration)
                continue;
            if (IsOverlapping(start, end, covered))
                continue;

            double clipEnd = dur > maxDuration ? start + maxDuration : end;
            clips.Add((start, clipEnd));
            covered.Add((start, clipEnd));

            if (clips.Count >= maxClips)
                break;
        }

        if (clips.Count == 0)
            clips.Add((segments[0].Start, Math.Min(segments[^1].End, maxDuration)));

        return clips;
    }

    private static double TextScore(string text)
    {
        string lower = text.ToLowerInvariant();
        double score = 0;

        foreach (var word in ExcitementWords)
        {
            int count = 0, idx = lower.IndexOf(word, StringComparison.Ordinal);
            while (idx != -1)
            {
                count++;
                idx = lower.IndexOf(word, idx + 1, StringComparison.Ordinal);
            }
            score += count * 2.0;
        }

        foreach (var word in QuestionWords)
        {
            int count = 0, idx = lower.IndexOf(word, StringComparison.Ordinal);
            while (idx != -1)
            {
                count++;
                idx = lower.IndexOf(word, idx + 1, StringComparison.Ordinal);
            }
            score += count * 1.0;
        }

        int exclCount = 0;
        int exclIdx = text.IndexOf('!');
        while (exclIdx != -1)
        {
            exclCount++;
            exclIdx = text.IndexOf('!', exclIdx + 1);
        }
        score += exclCount * 1.5;

        return score;
    }

    private static bool IsOverlapping(double start, double end, List<(double s, double e)> ranges)
    {
        foreach (var (s, e) in ranges)
            if (!(end <= s || start >= e))
                return true;
        return false;
    }
}
