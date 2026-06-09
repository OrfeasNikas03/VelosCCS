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
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Log.Print("[DL] BackendService.GetVideoInfo start");
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

            var result = new VideoInfo { Width = width, Height = height, Duration = duration };
            Log.Print($"[DL] BackendService.GetVideoInfo done in {sw.Elapsed.TotalSeconds:F1}s");
            return result;
        }
        catch (Exception e)
        {
            Log.Error($"[DL] BackendService.GetVideoInfo failed: {e.Message}");
            return new VideoInfo { Width = 1920, Height = 1080, Duration = 60 };
        }
    }

    public async Task<AudioWaveform?> GetWaveform(string path)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Log.Print("[DL] BackendService.GetWaveform start");
        try
        {
            var result = await Task.Run(() => AudioWaveform.Extract(path));
            Log.Print($"[DL] BackendService.GetWaveform done in {sw.Elapsed.TotalSeconds:F1}s");
            return result;
        }
        catch (Exception e)
        {
            Log.Error($"[DL] BackendService.GetWaveform failed: {e.Message}");
            throw;
        }
    }

    public StreamInfo GetYtInfo(string url)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Log.Print("[DL] BackendService.GetYtInfo start");
        try
        {
            var result = Downloader.GetInfo(url);
            Log.Print($"[DL] BackendService.GetYtInfo done in {sw.Elapsed.TotalSeconds:F1}s");
            return result;
        }
        catch (Exception e)
        {
            Log.Error($"[DL] BackendService.GetYtInfo failed: {e.Message}");
            throw;
        }
    }

    public string DownloadVideo(string url, string outputDir)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Log.Print("[DL] BackendService.DownloadVideo start");
        try
        {
            var result = Downloader.Download(url, outputDir);
            Log.Print($"[DL] BackendService.DownloadVideo done in {sw.Elapsed.TotalSeconds:F1}s");
            return result;
        }
        catch (Exception e)
        {
            Log.Error($"[DL] BackendService.DownloadVideo failed: {e.Message}");
            throw;
        }
    }

    public string DownloadSection(string url, double start, double duration, string outputPath, int maxHeight = 720)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Log.Print($"[DL] BackendService.DownloadSection start maxHeight={maxHeight}");
        try
        {
            var sm = new StreamManager();
            var result = sm.DownloadSection(url, start, duration, outputPath, maxHeight);
            Log.Print($"[DL] BackendService.DownloadSection done in {sw.Elapsed.TotalSeconds:F1}s");
            return result;
        }
        catch (Exception e)
        {
            Log.Error($"[DL] BackendService.DownloadSection failed: {e.Message}");
            throw;
        }
    }

    public (int x, int y, int w, int h) Reframe(string path, double start = 0, double duration = 30,
        string method = "center")
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Log.Print("[DL] BackendService.Reframe start");
        try
        {
            var result = new Reframer(method).GetCropRect(path, start, duration);
            Log.Print($"[DL] BackendService.Reframe done in {sw.Elapsed.TotalSeconds:F1}s");
            return result;
        }
        catch (Exception e)
        {
            Log.Error($"[DL] BackendService.Reframe failed: {e.Message}");
            throw;
        }
    }

    public async Task<Transcript> TranscribeAsync(string path,
        string? language = null, IProgress<double>? progress = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Log.Print("[DL] BackendService.TranscribeAsync start");
        try
        {
            var result = await Transcriber.TranscribeAsync(path, language, progressCallback: null, progress: progress);
            Log.Print($"[DL] BackendService.TranscribeAsync done in {sw.Elapsed.TotalSeconds:F1}s");
            return result;
        }
        catch (Exception e)
        {
            Log.Error($"[DL] BackendService.TranscribeAsync failed: {e.Message}");
            throw;
        }
    }

    public async Task<Transcript> TranscribeChunkAsync(string path,
        double startTime, double endTime,
        string? language = null,
        Action<string>? progressCallback = null,
        IProgress<double>? progress = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Log.Print("[DL] BackendService.TranscribeChunkAsync start");
        try
        {
            var result = await Transcriber.TranscribeChunkAsync(path, startTime, endTime, language, progressCallback, progress);
            Log.Print($"[DL] BackendService.TranscribeChunkAsync done in {sw.Elapsed.TotalSeconds:F1}s");
            return result;
        }
        catch (Exception e)
        {
            Log.Error($"[DL] BackendService.TranscribeChunkAsync failed: {e.Message}");
            throw;
        }
    }

    public List<(double start, double end)> DetectHighlights(List<Segment> segments)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Log.Print("[DL] BackendService.DetectHighlights start");
        try
        {
            var result = new HighlightDetector().FindHighlights(segments);
            Log.Print($"[DL] BackendService.DetectHighlights done in {sw.Elapsed.TotalSeconds:F1}s");
            return result;
        }
        catch (Exception e)
        {
            Log.Error($"[DL] BackendService.DetectHighlights failed: {e.Message}");
            throw;
        }
    }

    public byte[] ExtractFrame(string path, double time, int width = 0, int height = 0)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Log.Print("[DL] BackendService.ExtractFrame start");
        try
        {
            var psi = new ProcessStartInfo("ffmpeg",
                $"-ss {time:F3} -i \"{path}\" -vframes 1 -f image2pipe -vcodec png -")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                Log.Warn("[DL] BackendService.ExtractFrame: ffmpeg process null");
                return Array.Empty<byte>();
            }

            using var ms = new MemoryStream();
            proc.StandardOutput.BaseStream.CopyTo(ms);
            proc.WaitForExit(10000);
            var result = ms.ToArray();
            Log.Print($"[DL] BackendService.ExtractFrame done in {sw.Elapsed.TotalSeconds:F1}s");
            return result;
        }
        catch (Exception e)
        {
            Log.Error($"[DL] BackendService.ExtractFrame failed: {e.Message}");
            throw;
        }
    }

    public void Dispose()
    {
        Transcriber.Dispose();
    }
}
