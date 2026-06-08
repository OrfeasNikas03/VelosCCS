using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace VelosCCS;

public class AudioWaveform
{
    public List<float> Peaks { get; }
    public double Duration { get; }

    public AudioWaveform(List<float> peaks, double duration)
    {
        Peaks = peaks;
        Duration = duration;
    }

    public List<float> GetPeaksInRange(double start, double end, int numSamples)
    {
        if (Duration <= 0 || Peaks.Count == 0)
            return Enumerable.Repeat(0f, numSamples).ToList();

        int startIdx = (int)((start / Duration) * Peaks.Count);
        int endIdx = (int)((end / Duration) * Peaks.Count);
        startIdx = Math.Max(0, Math.Min(startIdx, Peaks.Count - 1));
        endIdx = Math.Max(startIdx + 1, Math.Min(endIdx, Peaks.Count));

        var chunk = Peaks.GetRange(startIdx, endIdx - startIdx);
        if (chunk.Count == 0)
            return Enumerable.Repeat(0f, numSamples).ToList();

        var resampled = new List<float>(numSamples);
        for (int i = 0; i < numSamples; i++)
        {
            int s = i * chunk.Count / numSamples;
            int e = (i + 1) * chunk.Count / numSamples;
            if (s < e)
                resampled.Add(chunk.GetRange(s, e - s).Max());
            else
                resampled.Add(chunk[^1]);
        }
        return resampled;
    }

    public static AudioWaveform? Extract(string videoPath, int targetPeaks = 0)
    {
        double? dur = GetDuration(videoPath);
        if (dur == null || dur <= 0)
            return null;

        var psi = new ProcessStartInfo("ffmpeg", $"-i \"{videoPath}\" -ac 1 -ar 8000 -f s16le -hide_banner -loglevel error pipe:1")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        byte[] raw;
        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            using var ms = new MemoryStream();
            proc.StandardOutput.BaseStream.CopyTo(ms);
            proc.WaitForExit(30000);
            raw = ms.ToArray();
        }
        catch
        {
            return null;
        }

        if (raw.Length < 2)
            return null;

        int sampleCount = raw.Length / 2;
        var samples = new short[sampleCount];
        for (int i = 0; i < sampleCount; i++)
            samples[i] = BitConverter.ToInt16(raw, i * 2);

        int numPeaks = targetPeaks > 0 ? targetPeaks : Math.Max(2000, Math.Min(sampleCount, (int)(dur * 200)));
        int samplesPerPeak = Math.Max(1, sampleCount / numPeaks);
        var peaks = new List<float>(numPeaks);
        for (int i = 0; i < numPeaks; i++)
        {
            int chunkStart = i * samplesPerPeak;
            int chunkEnd = Math.Min((i + 1) * samplesPerPeak, sampleCount);
            double sumSq = 0;
            int cnt = 0;
            for (int j = chunkStart; j < chunkEnd && j < sampleCount; j++)
            {
                float s = samples[j] / 32768f;
                sumSq += s * s;
                cnt++;
            }
            peaks.Add(MathF.Sqrt((float)(sumSq / cnt)));
        }

        return new AudioWaveform(peaks, dur.Value);
    }

    private static double? GetDuration(string path)
    {
        var psi = new ProcessStartInfo("ffprobe", $"-v error -show_entries format=duration -of csv=p=0 \"{path}\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            string output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(10000);
            return double.Parse(output);
        }
        catch
        {
            return null;
        }
    }
}
