using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace VelosCCS;

public class VideoInfo
{
    public int Width { get; set; }
    public int Height { get; set; }
    public double Duration { get; set; }
}

public class BackendService
{
    public Transcriber Transcriber { get; } = new();
    public Downloader Downloader { get; } = new();

    public async Task<VideoInfo> GetVideoInfo(string path)
    {
        try
        {
            var psi = new ProcessStartInfo("ffprobe",
                $"-v quiet -print_format json -show_format -show_streams \"{path}\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null) return new VideoInfo { Width = 1920, Height = 1080, Duration = 60 };

            string output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            int width = 1920, height = 1080;
            double duration = 60;

            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var s in streams.EnumerateArray())
                {
                    if (s.TryGetProperty("codec_type", out var ct) && ct.GetString() == "video")
                    {
                        if (s.TryGetProperty("width", out var w)) width = w.GetInt32();
                        if (s.TryGetProperty("height", out var h)) height = h.GetInt32();
                        break;
                    }
                }
            }
            if (root.TryGetProperty("format", out var fmt) &&
                fmt.TryGetProperty("duration", out var d))
            {
                if (d.ValueKind == System.Text.Json.JsonValueKind.Number)
                    duration = d.GetDouble();
                else if (d.ValueKind == System.Text.Json.JsonValueKind.String &&
                         double.TryParse(d.GetString(), System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out var dur))
                    duration = dur;
            }

            return new VideoInfo { Width = width, Height = height, Duration = duration };
        }
        catch
        {
            return new VideoInfo { Width = 1920, Height = 1080, Duration = 60 };
        }
    }

    public async Task<AudioWaveform?> GetWaveform(string path)
    {
        return await Task.Run(() => AudioWaveform.Extract(path));
    }

    public StreamInfo GetYtInfo(string url)
    {
        return Downloader.GetInfo(url);
    }

    public string DownloadVideo(string url, string outputDir)
    {
        return Downloader.Download(url, outputDir);
    }

    public string DownloadSection(string url, double start, double duration, string outputPath)
    {
        var sm = new StreamManager();
        return sm.DownloadSection(url, start, duration, outputPath);
    }

    public (int x, int y, int w, int h) Reframe(string path, double start = 0, double duration = 30,
        string method = "center")
    {
        return new Reframer(method).GetCropRect(path, start, duration);
    }

    public async Task<Transcript> TranscribeAsync(string path,
        string? language = null, IProgress<double>? progress = null)
    {
        return await Transcriber.TranscribeAsync(path, language, progressCallback: null, progress: progress);
    }

    public async Task<Transcript> TranscribeChunkAsync(string path,
        double startTime, double endTime,
        string? language = null,
        Action<string>? progressCallback = null,
        IProgress<double>? progress = null)
    {
        return await Transcriber.TranscribeChunkAsync(path, startTime, endTime, language, progressCallback, progress);
    }

    public List<(double start, double end)> DetectHighlights(List<Segment> segments)
    {
        return new HighlightDetector().FindHighlights(segments);
    }

    public byte[] ExtractFrame(string path, double time, int width = 0, int height = 0)
    {
        var psi = new ProcessStartInfo("ffmpeg",
            $"-ss {time:F3} -i \"{path}\" -vframes 1 -f image2pipe -vcodec png -")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi);
        if (proc == null) return Array.Empty<byte>();

        using var ms = new MemoryStream();
        proc.StandardOutput.BaseStream.CopyTo(ms);
        proc.WaitForExit(10000);
        return ms.ToArray();
    }

    public void Dispose()
    {
        Transcriber.Dispose();
    }
}
