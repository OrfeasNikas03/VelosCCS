using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VelosCCS;

public struct ExporterLayer
{
    public string Type;
    public string? Path;
    public string? Text;
    public double X, Y, W, H;
    public int FontSize;
    public string? FontPath;
    public Color FontColor, OutlineColor;
    public int OutlineWidth;
    public double Start, End;
    public bool NormalizeAudio;
    public double Volume;
    public double Rotation;
    public List<Keyframe>? KeyframesX, KeyframesY, KeyframesOpacity, KeyframesScale, KeyframesFontSize;
    public List<Keyframe>? KeyframesRotation;
    public List<TextKeyframe>? KeyframesText;
}

public static class Exporter
{
    private static string? _cachedEncoder;
    private static List<string>? _cachedEncoderArgs;

    public static string EncoderLabel
    {
        get
        {
            if (_cachedEncoder == null)
                GetEncoderArgs(out _, out _);
            if (_cachedEncoder == "libx264") return "Software (libx264)";
            foreach (var (name, _, label) in HwEncoders)
                if (name == _cachedEncoder) return label;
            return _cachedEncoder ?? "unknown";
        }
    }

    private static readonly (string name, string pattern, string label)[] HwEncoders =
    {
        ("h264_nvenc",   @"h264_nvenc",   "NVIDIA NVENC"),
        ("hevc_nvenc",   @"hevc_nvenc",   "NVIDIA NVENC (HEVC)"),
        ("h264_vaapi",   @"h264_vaapi",   "VAAPI (Intel/AMD)"),
        ("h264_amf",     @"h264_amf",     "AMD AMF"),
        ("h264_qsv",     @"h264_qsv",     "Intel QuickSync"),
        ("h264_videotoolbox", @"h264_videotoolbox", "VideoToolbox (macOS)"),
    };

    private static readonly Dictionary<string, string[]> EncPresets = new()
    {
        ["h264_nvenc"]       = new[] { "-preset", "p7", "-tune", "hq", "-rc", "vbr", "-b:v", "15M", "-maxrate", "20M", "-bufsize", "25M" },
        ["hevc_nvenc"]       = new[] { "-preset", "p7", "-tune", "hq", "-rc", "vbr", "-b:v", "15M", "-maxrate", "20M", "-bufsize", "25M" },
        ["h264_vaapi"]       = new[] { "-b:v", "15M", "-maxrate", "20M", "-bufsize", "25M" },
        ["h264_amf"]         = new[] { "-b:v", "15M", "-maxrate", "20M", "-bufsize", "25M", "-quality", "speed" },
        ["h264_qsv"]         = new[] { "-b:v", "15M", "-maxrate", "20M", "-bufsize", "25M" },
        ["h264_videotoolbox"] = new[] { "-b:v", "15M", "-maxrate", "20M", "-bufsize", "25M" },
    };

    private static readonly string[] SwEncoderArgs = { "-c:v", "libx264", "-preset", "medium", "-b:v", "15M" };

    private static string FindFfmpeg()
    {
        string executableDir = OS.GetExecutablePath().GetBaseDir();
        string[] candidates = { "ffmpeg.exe", "ffmpeg" };
        foreach (var name in candidates)
        {
            string sidecar = executableDir.PathJoin(name);
            if (File.Exists(sidecar)) return sidecar;
            string envPath = executableDir.PathJoin("python_env/" + name);
            if (File.Exists(envPath)) return envPath;
        }
        return "ffmpeg";
    }

    private static void GetEncoderArgs(out string encoderName, out List<string> args)
    {
        if (_cachedEncoderArgs != null)
        {
            encoderName = _cachedEncoder!;
            args = _cachedEncoderArgs;
            return;
        }

        string ffmpeg = FindFfmpeg();
        try
        {
            var psi = new ProcessStartInfo(ffmpeg, "-encoders")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                string output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
                proc.WaitForExit(10000);
                foreach (var (name, pattern, label) in HwEncoders)
                {
                    if (Regex.IsMatch(output, pattern, RegexOptions.IgnoreCase))
                    {
                        // Validate the encoder actually works (ghost encoders fail on wrong GPU)
                        if (!TryTestEncoder(ffmpeg, name))
                        {
                            GD.Print($"[Exporter] Encoder {name} listed but not usable, skipping");
                            continue;
                        }
                        GD.Print($"[Exporter] HW encoder: {label} ({name})");
                        _cachedEncoder = name;
                        _cachedEncoderArgs = new List<string> { "-c:v", name };
                        if (EncPresets.TryGetValue(name, out var preset))
                            _cachedEncoderArgs.AddRange(preset);
                        encoderName = name;
                        args = _cachedEncoderArgs;
                        return;
                    }
                }
            }
        }
        catch (Exception e)
        {
            GD.PrintErr($"[Exporter] Encoder probe failed: {e.Message}");
        }

        GD.Print("[Exporter] No HW encoder found, using software libx264");
        _cachedEncoder = "libx264";
        _cachedEncoderArgs = new List<string>(SwEncoderArgs);
        encoderName = _cachedEncoder;
        args = _cachedEncoderArgs;
    }

    private static bool TryTestEncoder(string ffmpeg, string encoderName)
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpeg,
                $"-f lavfi -i testsrc2=duration=1:size=128x128 -c:v {encoderName} -f null - -t 1 -loglevel error")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10000);
            bool ok = proc.ExitCode == 0;
            if (!ok) GD.Print($"[Exporter] Encoder test for {encoderName} failed (exit {proc.ExitCode}): {stderr.Trim()}");
            return ok;
        }
        catch
        {
            return false;
        }
    }

    public static async Task ExportAsync(
        string inputPath, string outputPath,
        double start, double duration,
        string? assPath = null,
        int cropX = 0, int cropY = 0, int cropW = 0, int cropH = 0,
        int outWidth = 1080, int outHeight = 1920,
        bool normalizeAudio = false,
        double blurIntensity = 0,
        float[]? gameCrop = null,
        float[]? camCrop = null, float[]? camTarget = null,
        int layoutMode = 0,
        float[]? uiCrop = null, float[]? uiTarget = null,
        float[]? gameCropNorm = null,
        float[]? layoutCrop = null,
        List<ExporterLayer>? layers = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        Log.Print($"ExportAsync: {inputPath} -> {outputPath}, {duration:F1}s, start={start}, out={outWidth}x{outHeight}, layers={layers?.Count ?? 0}");

        bool isStreamladder = gameCrop != null && camCrop != null && camTarget != null;
        bool isLetterbox = gameCrop != null && camCrop == null;
        bool useLayout = layoutCrop != null && blurIntensity > 0;

        string mode;

        if (isStreamladder)
        {
            mode = "streamladder";
            GD.Print($"[Exporter] Mode: streamladder (layout_mode={layoutMode})");
        }
        else if (isLetterbox)
        {
            mode = "letterbox";
            GD.Print($"[Exporter] Mode: letterbox (blur_intensity={blurIntensity})");
        }
        else if (useLayout)
        {
            mode = "complex";
            GD.Print($"[Exporter] Mode: complex (layout_crop={string.Join(",", layoutCrop!)})");
        }
        else
        {
            mode = "simple";
            GD.Print($"[Exporter] Mode: simple (crop={cropW}x{cropH}, offset={cropX},{cropY})");
        }

        List<string> cmd;
        if (isStreamladder)
            cmd = BuildStreamladderCmd(inputPath, outputPath, start, duration,
                assPath, outWidth, outHeight,
                gameCrop!, camCrop!, camTarget!,
                normalizeAudio, blurIntensity, gameCropNorm, layoutMode,
                uiCrop, uiTarget, layers);
        else if (isLetterbox)
            cmd = BuildLetterboxCmd(inputPath, outputPath, start, duration,
                assPath, outWidth, outHeight,
                gameCrop!, normalizeAudio, blurIntensity, gameCropNorm, layers);
        else if (useLayout)
            cmd = BuildComplexCmd(inputPath, outputPath, start, duration,
                assPath, cropX, cropY, cropW, cropH,
                outWidth, outHeight, blurIntensity, normalizeAudio,
                layoutCrop!, layers);
        else
            cmd = BuildSimpleCmd(inputPath, outputPath, start, duration,
                assPath, cropX, cropY, cropW, cropH,
                outWidth, outHeight, normalizeAudio, layers);

        var fcIdx = cmd.FindIndex(c => c.Contains("filter_complex"));
        string fcStr = fcIdx >= 0 && fcIdx + 1 < cmd.Count ? cmd[fcIdx + 1] : "(none)";
        GD.Print($"[Exporter] filter_complex: {fcStr}");
        GD.Print($"[Exporter] Running FFmpeg (mode={mode})...");

        await RunFfmpegAsync(cmd, progress, ct);
        GD.Print($"[Exporter] FFmpeg completed OK for {outputPath}");
    }

    private static async Task RunFfmpegAsync(List<string> cmd,
        IProgress<double>? progress, CancellationToken ct)
    {
        string ffmpeg = FindFfmpeg();
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in cmd)
            psi.ArgumentList.Add(arg);

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        // Read stderr asynchronously for progress reporting and error capture
        double duration = 0;
        var errorLines = new System.Collections.Concurrent.ConcurrentBag<string>();
        var stderrTask = Task.Run(() =>
        {
            string? line;
            while ((line = proc.StandardError.ReadLine()) != null)
            {
                if (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Error", StringComparison.OrdinalIgnoreCase))
                    errorLines.Add(line);

                if (duration == 0)
                {
                    var durMatch = Regex.Match(line, @"Duration: (\d+):(\d+):(\d+\.\d+)");
                    if (durMatch.Success)
                    {
                        duration = int.Parse(durMatch.Groups[1].Value) * 3600
                                 + int.Parse(durMatch.Groups[2].Value) * 60
                                 + double.Parse(durMatch.Groups[3].Value);
                    }
                }

                if (progress != null && duration > 0)
                {
                    var timeMatch = Regex.Match(line, @"time=(\d+):(\d+):(\d+\.\d+)");
                    if (timeMatch.Success)
                    {
                        double current = int.Parse(timeMatch.Groups[1].Value) * 3600
                                       + int.Parse(timeMatch.Groups[2].Value) * 60
                                       + double.Parse(timeMatch.Groups[3].Value);
                        progress.Report(Math.Min(current / duration, 1.0));
                    }
                }
            }
        }, ct);

        await Task.WhenAny(proc.WaitForExitAsync(), Task.Delay(-1, ct));
        ct.ThrowIfCancellationRequested();

        await stderrTask;

        if (proc.ExitCode != 0)
        {
            string errDetail = errorLines.Count > 0
                ? "\n  FFmpeg errors:\n  " + string.Join("\n  ", errorLines.Take(10))
                : "";
            throw new InvalidOperationException($"FFmpeg exited with code {proc.ExitCode}{errDetail}");
        }
    }

    private static List<string> BuildSimpleCmd(
        string inputPath, string outputPath,
        double start, double duration,
        string? assPath, int cropX, int cropY, int cropW, int cropH,
        int outWidth, int outHeight, bool normalizeAudio,
        List<ExporterLayer>? layers)
    {
        layers ??= new();
        bool hasLayers = layers.Any(l => l.Type is "text" or "image" or "gif" or "audio");

        if (!hasLayers)
        {
            var filterParts = new List<string>();
            if (cropW > 0)
                filterParts.Add($"crop={cropW}:{cropH}:{cropX}:{cropY},scale={outWidth}:{outHeight}:flags=lanczos");
            else
                filterParts.Add($"scale={outWidth}:{outHeight}:force_original_aspect_ratio=increase:flags=lanczos,crop={outWidth}:{outHeight}");
            if (!string.IsNullOrEmpty(assPath))
                filterParts.Add($"ass={assPath}");

            GetEncoderArgs(out _, out var encArgs);
            var cmd = new List<string>
            {
                "-ss", start.ToString("F3"), "-i", inputPath,
                "-t", duration.ToString("F3"),
                "-vf", string.Join(",", filterParts),
            };
            cmd.AddRange(encArgs);
            cmd.AddRange(new[] { "-c:a", "aac", "-b:a", "128k" });
            if (normalizeAudio)
                cmd.AddRange(new[] { "-af", "loudnorm=I=-14:LRA=1:TP=-1" });
            cmd.AddRange(new[] { "-y", outputPath });
            return cmd;
        }

        // filter_complex path
        var filters = new List<string>();
        if (cropW > 0)
            filters.Add($"[0:v]crop={cropW}:{cropH}:{cropX}:{cropY},scale={outWidth}:{outHeight}:flags=lanczos[base]");
        else
            filters.Add($"[0:v]scale={outWidth}:{outHeight}:force_original_aspect_ratio=increase:flags=lanczos,crop={outWidth}:{outHeight}[base]");

        var cmd2 = new List<string> { "-ss", start.ToString("F3"), "-i", inputPath };
        var (lastV, mapA) = AddLayerInputsAndFilters(cmd2, filters, layers, "base", duration, outWidth, outHeight, 1);

        if (!string.IsNullOrEmpty(assPath))
        {
            filters.Add($"[{lastV}]ass='{assPath}'[out]");
            lastV = "out";
        }
        else
        {
            filters.Add($"[{lastV}]copy[out]");
        }

        // If no audio layers but normalize requested, apply loudnorm to source
        bool hasAudioLayers = layers.Any(l => l.Type == "audio");
        if (!hasAudioLayers && normalizeAudio && mapA == "0:a")
        {
            filters.Add("[0:a]loudnorm=I=-14:LRA=1:TP=-1[outa]");
            mapA = "[outa]";
        }

        GetEncoderArgs(out _, out var encArgs2);
        cmd2.AddRange(new[] { "-t", duration.ToString("F3") });
        cmd2.AddRange(new[] { "-filter_complex", string.Join(";", filters), "-map", "[out]", "-map", mapA });
        cmd2.AddRange(encArgs2);
        cmd2.AddRange(new[] { "-c:a", "aac", "-b:a", "128k", "-y", outputPath });
        return cmd2;
    }

    private static List<string> BuildComplexCmd(
        string inputPath, string outputPath,
        double start, double duration,
        string? assPath, int cropX, int cropY, int cropW, int cropH,
        int outWidth, int outHeight, double blurIntensity,
        bool normalizeAudio,
        float[] layoutCrop, List<ExporterLayer>? layers)
    {
        layers ??= new();
        var cmd = new List<string> { "-ss", start.ToString("F3"), "-i", inputPath };
        var filters = new List<string>();

        float lx = layoutCrop[0], ly = layoutCrop[1], lw = layoutCrop[2], lh = layoutCrop[3];
        int lr = Math.Max(1, (int)(blurIntensity * 8));
        int lp = Math.Max(1, (int)blurIntensity);

        filters.Add("[0:v]split=2[main1][main2]");
        filters.Add($"[main1]scale={outWidth}:{outHeight}:force_original_aspect_ratio=increase:flags=lanczos[full1]");
        filters.Add($"[main2]scale={outWidth}:{outHeight}:force_original_aspect_ratio=increase,boxblur={lr}:{lp}[blurred]");
        filters.Add($"[full1]crop={outWidth * lw:F0}:{outHeight * lh:F0}:{outWidth * lx:F0}:{outHeight * ly:F0}[cropped]");
        filters.Add("[blurred][cropped]overlay=(W-w)/2:(H-h)/2[base_v]");

        var (lastV, mapA) = AddLayerInputsAndFilters(cmd, filters, layers, "base_v", duration, outWidth, outHeight, 1);

        if (!string.IsNullOrEmpty(assPath))
        {
            filters.Add($"[{lastV}]ass='{assPath}'[out]");
            lastV = "out";
        }
        else
        {
            filters.Add($"[{lastV}]copy[out]");
            lastV = "out";
        }

        bool hasAudioLayers = layers.Any(l => l.Type == "audio");
        if (!hasAudioLayers && normalizeAudio && mapA == "0:a")
        {
            filters.Add("[0:a]loudnorm=I=-14:LRA=1:TP=-1[outa]");
            mapA = "[outa]";
        }

        GetEncoderArgs(out _, out var encArgs);
        cmd.AddRange(new[] { "-t", duration.ToString("F3") });
        cmd.AddRange(new[] { "-filter_complex", string.Join(";", filters), "-map", "[out]", "-map", mapA });
        cmd.AddRange(encArgs);
        cmd.AddRange(new[] { "-c:a", "aac", "-b:a", "128k", "-y", outputPath });
        return cmd;
    }

    private static List<string> BuildLetterboxCmd(
        string inputPath, string outputPath,
        double start, double duration,
        string? assPath, int outW, int outH,
        float[] gameCrop, bool normalizeAudio, double blurIntensity,
        float[]? gameCropNorm, List<ExporterLayer>? layers)
    {
        layers ??= new();
        // Convert normalized crop values (0-1 from overlay) to input video pixels
        var (inW, inH) = ProbeVideoDimensions(inputPath);
        float mulX = inW > 0 ? inW : 1, mulY = inH > 0 ? inH : 1;
        float gx = gameCrop[0] * mulX, gy = gameCrop[1] * mulY, gw = gameCrop[2] * mulX, gh = gameCrop[3] * mulY;

        // UV-space fitting (matches OutputPreview shader)
        int fitW = outW, fitH = outH, fitX = 0, fitY = 0;
        if (gameCropNorm != null && gameCropNorm.Length >= 4)
        {
            float ngw = gameCropNorm[2], ngh = gameCropNorm[3];
            double sAspect = ngw / Math.Max(ngh, 0.001f);
            double tAspect = (double)outW / outH;
            if (sAspect >= tAspect)
            {
                fitW = outW;
                fitH = (int)(outW / sAspect);
                fitX = 0;
                fitY = (outH - fitH) / 2;
            }
            else
            {
                fitH = outH;
                fitW = (int)(outH * sAspect);
                fitX = (outW - fitW) / 2;
                fitY = 0;
            }
        }

        var cmd = new List<string> { "-ss", start.ToString("F3"), "-i", inputPath };

        // Image layers are added first, then audio (so stream indices are correct)
        var imageLayers = layers.Where(l => l.Type is "image" or "gif").ToList();
        var audioLayers = layers.Where(l => l.Type == "audio").ToList();

        foreach (var l in imageLayers)
        {
            if (l.Type == "gif")
                cmd.AddRange(new[] { "-ignore_loop", "0", "-i", l.Path! });
            else
                cmd.AddRange(new[] { "-loop", "1", "-i", l.Path! });
        }
        foreach (var l in audioLayers)
            cmd.AddRange(new[] { "-i", l.Path! });

        cmd.AddRange(new[] { "-t", duration.ToString("F3") });

        var filters = new List<string>();
        if (blurIntensity > 0)
        {
            int lr = Math.Max(1, (int)(blurIntensity * 8));
            int lp = Math.Max(1, (int)blurIntensity);
            filters.Add("[0:v]split=2[fg_src][bg_src]");
            filters.Add($"[bg_src]crop={gw}:{gh}:{gx}:{gy},scale={outW}:{outH}:force_original_aspect_ratio=increase,crop={outW}:{outH},boxblur={lr}:{lp}[blurred_bg]");
            filters.Add($"[fg_src]crop={gw}:{gh}:{gx}:{gy},scale={fitW}:{fitH}[fitted_fg]");
            filters.Add($"[blurred_bg][fitted_fg]overlay={fitX}:{fitY}[base_v]");
        }
        else
        {
            filters.Add($"[0:v]crop={gw}:{gh}:{gx}:{gy},scale={fitW}:{fitH},pad={outW}:{outH}:(ow-iw)/2:(oh-ih)/2:black[base_v]");
        }
        string lastV = "base_v";

        // Text layers
        var textLayers = layers.Where(l => l.Type == "text").ToList();
        for (int i = 0; i < textLayers.Count; i++)
            AddTextLayer(filters, textLayers[i], i, ref lastV, duration, outW, outH);

        // Image overlay layers (stream indices: 1..imageLayers.Count)
        for (int i = 0; i < imageLayers.Count; i++)
            AddImageLayer(filters, imageLayers[i], i, 1 + i, ref lastV, duration, outW, outH);

        // Captions
        if (!string.IsNullOrEmpty(assPath))
        {
            filters.Add($"[{lastV}]ass='{assPath}'[out]");
            lastV = "out";
        }
        else
        {
            filters.Add($"[{lastV}]copy[out]");
            lastV = "out";
        }

        // Audio chain
        string mapA = "0:a";
        int audioStreamStart = 1 + imageLayers.Count;
        if (audioLayers.Count > 0)
            AddAudioChain(filters, audioLayers, audioStreamStart, normalizeAudio, out mapA);
        else if (normalizeAudio)
        {
            filters.Add("[0:a]loudnorm=I=-14:LRA=1:TP=-1[outa]");
            mapA = "[outa]";
        }

        GetEncoderArgs(out _, out var encArgs);
        cmd.AddRange(new[] { "-filter_complex", string.Join(";", filters), "-map", "[out]", "-map", mapA });
        cmd.AddRange(encArgs);
        cmd.AddRange(new[] { "-c:a", "aac", "-b:a", "128k", "-y", outputPath });
        return cmd;
    }

    private static List<string> BuildStreamladderCmd(
        string inputPath, string outputPath,
        double start, double duration,
        string? assPath, int outW, int outH,
        float[] gameCrop, float[] camCrop, float[] camTarget,
        bool normalizeAudio, double blurIntensity,
        float[]? gameCropNorm, int layoutMode,
        float[]? uiCrop, float[]? uiTarget,
        List<ExporterLayer>? layers)
    {
        layers ??= new();

        // Convert normalized crop values (0-1 from overlay) to input video pixels
        var (inW, inH) = ProbeVideoDimensions(inputPath);
        float mulX = inW > 0 ? inW : 1, mulY = inH > 0 ? inH : 1;
        float gx = gameCrop[0] * mulX, gy = gameCrop[1] * mulY, gw = gameCrop[2] * mulX, gh = gameCrop[3] * mulY;
        float cx = camCrop[0] * mulX, cy = camCrop[1] * mulY, cw = camCrop[2] * mulX, ch = camCrop[3] * mulY;
        float tx = camTarget[0], ty = camTarget[1], tw = camTarget[2], th = camTarget[3];

        int camPx = (int)(tx * outW), camPy = (int)(ty * outH);
        int camPw = (int)(tw * outW), camPh = (int)(th * outH);

		int ffBlur = Math.Max(1, (int)(blurIntensity * 10));

		// UI PiP
        int uiOx = 0, uiOy = 0, uiOw = 0, uiOh = 0;
        int uPx = 0, uPy = 0, uPw = 0, uPh = 0;
        if (layoutMode == 2 && uiCrop != null && uiTarget != null)
        {
            uPx = (int)(uiCrop[0] * mulX); uPy = (int)(uiCrop[1] * mulY);
            uPw = (int)(uiCrop[2] * mulX); uPh = (int)(uiCrop[3] * mulY);
            uiOx = (int)(uiTarget[0] * outW); uiOy = (int)(uiTarget[1] * outH);
            uiOw = (int)(uiTarget[2] * outW); uiOh = (int)(uiTarget[3] * outH);
        }

        var imageLayers = layers.Where(l => l.Type is "image" or "gif").ToList();
        var textLayers = layers.Where(l => l.Type == "text").ToList();
        var audioLayers = layers.Where(l => l.Type == "audio").ToList();

        var cmd = new List<string> { "-ss", start.ToString("F3"), "-i", inputPath };
        foreach (var l in imageLayers)
        {
            if (l.Type == "gif")
                cmd.AddRange(new[] { "-ignore_loop", "0", "-i", l.Path! });
            else
                cmd.AddRange(new[] { "-loop", "1", "-i", l.Path! });
        }
        foreach (var l in audioLayers)
            cmd.AddRange(new[] { "-i", l.Path! });
        cmd.AddRange(new[] { "-t", duration.ToString("F3") });

        // Circle mask geometry
        double camCx = camPw / 2.0, camCy = camPh / 2.0;
        double camR = Math.Min(camPw, camPh) / 2.0;

        string camStream;
        if (layoutMode == 1)
            camStream = $"[0:v]crop={cw}:{ch}:{cx}:{cy},scale={camPw}:{camPh}:flags=lanczos,format=rgba,geq=a='if(lte(sqrt((X-{camCx})^2+(Y-{camCy})^2),{camR}),255,0)'[cam_v]";
        else
            camStream = $"[0:v]crop={cw}:{ch}:{cx}:{cy},scale={camPw}:{camPh}:flags=lanczos[cam_v]";

		var filters = new List<string>
		{
			$"[0:v]crop={gw}:{gh}:{gx}:{gy},scale={outW}:{outH}:force_original_aspect_ratio=increase,crop={outW}:{outH},boxblur={ffBlur}:5[bg_v]",
			$"[0:v]crop={gw}:{gh}:{gx}:{gy},scale={outW}:{outH}:force_original_aspect_ratio=decrease[fg_fit]",
			camStream,
			$"[bg_v][fg_fit]overlay=(W-w)/2:(H-h)/2[base_v]",
			$"[base_v][cam_v]overlay={camPx}:{camPy}[v_composite]",
		};

        string lastV = "v_composite";

        // UI PiP (layout_mode 2)
        if (layoutMode == 2 && uiOw > 0 && uiOh > 0)
        {
            filters.Add($"[0:v]crop={uPw}:{uPh}:{uPx}:{uPy},scale={uiOw}:{uiOh}:flags=lanczos[ui_v]");
            filters.Add($"[v_composite][ui_v]overlay={uiOx}:{uiOy}[v_with_ui]");
            lastV = "v_with_ui";
        }

        // Text layers
        for (int i = 0; i < textLayers.Count; i++)
            AddTextLayer(filters, textLayers[i], i, ref lastV, duration, outW, outH);

        // Image/GIF overlay (stream indices: 1..imageLayers.Count)
        for (int i = 0; i < imageLayers.Count; i++)
            AddImageLayer(filters, imageLayers[i], i, 1 + i, ref lastV, duration, outW, outH);

        // Safety pad
        filters.Add($"[{lastV}]pad={outW}:{outH}:(ow-iw)/2:(oh-ih)/2:black[final_v]");
        lastV = "final_v";

        // Captions
        if (!string.IsNullOrEmpty(assPath))
        {
            filters.Add($"[{lastV}]ass='{assPath}'[out]");
            lastV = "out";
        }
        else
        {
            filters.Add($"[{lastV}]copy[out]");
            lastV = "out";
        }

        // Audio
        string mapA = "0:a";
        int audioStreamStart = 1 + imageLayers.Count;
        if (audioLayers.Count > 0)
            AddAudioChain(filters, audioLayers, audioStreamStart, normalizeAudio, out mapA);
        else if (normalizeAudio)
        {
            filters.Add("[0:a]loudnorm=I=-14:LRA=1:TP=-1[outa]");
            mapA = "[outa]";
        }

        GetEncoderArgs(out _, out var encArgs);
        cmd.AddRange(new[] { "-filter_complex", string.Join(";", filters), "-map", "[out]", "-map", mapA });
        cmd.AddRange(encArgs);
        cmd.AddRange(new[] { "-c:a", "aac", "-b:a", "128k", "-y", outputPath });
        return cmd;
    }

    private static void AddImageLayer(List<string> filters, ExporterLayer layer,
        int imgIdx, int streamIdx, ref string lastV,
        double duration, int outW, int outH)
    {
        string nextV = $"v_img_{imgIdx}";
        string xExpr = GenerateFfmpegExpression(layer.KeyframesX, layer.X, duration, timeOffset: layer.Start);
        string yExpr = GenerateFfmpegExpression(layer.KeyframesY, layer.Y, duration, timeOffset: layer.Start);
        string rotExpr = GenerateFfmpegExpression(layer.KeyframesRotation, layer.Rotation, duration, timeOffset: layer.Start);
        int targetW = Math.Max(1, (int)(outW * layer.W));
        double rs = Math.Max(0, layer.Start);
        double re = Math.Min(layer.End, duration);
        bool hasRotation = layer.Rotation != 0 || layer.KeyframesRotation != null;
        string scaleFilter = $"scale={targetW}:-1:flags=lanczos";
        if (hasRotation)
            scaleFilter += $",rotate={rotExpr}*PI/180:fillcolor=0x00000000";
        filters.Add($"[{streamIdx}:v]{scaleFilter}[ovl{imgIdx}]");
        filters.Add($"[{lastV}][ovl{imgIdx}]overlay=x='(W-w)*{xExpr}':y='(H-h)*{yExpr}':enable='between(t,{rs},{re})'[{nextV}]");
        lastV = nextV;
    }

    private static void AddTextLayer(List<string> filters, ExporterLayer layer,
        int txtIdx, ref string lastV,
        double duration, int outW, int outH)
    {
        string nextV = $"v_txt_{txtIdx}";
        string xExpr = GenerateFfmpegExpression(layer.KeyframesX, layer.X, duration, timeOffset: layer.Start);
        string yExpr = GenerateFfmpegExpression(layer.KeyframesY, layer.Y, duration, timeOffset: layer.Start);
        string alphaExpr = GenerateFfmpegExpression(layer.KeyframesOpacity, 1.0, duration, timeOffset: layer.Start);
        string fsExpr = GenerateFfmpegExpression(layer.KeyframesFontSize, layer.FontSize, duration, timeOffset: layer.Start);
        string scaleExpr = GenerateFfmpegExpression(layer.KeyframesScale, 1.0, duration, timeOffset: layer.Start);
        string rotExpr = GenerateFfmpegExpression(layer.KeyframesRotation, layer.Rotation, duration, timeOffset: layer.Start);
        bool hasRotation = layer.Rotation != 0 || layer.KeyframesRotation != null;

        double rs = Math.Max(0, layer.Start);
        double re = Math.Min(layer.End, duration);

        string bx = $"W*{xExpr} + W*({layer.W}*{scaleExpr})/2 - tw/2";
        string by = $"H*{yExpr} + H*({layer.H}*{scaleExpr})/2 - th/2";

        int fcr = (int)(layer.FontColor.R * 255);
        int fcg = (int)(layer.FontColor.G * 255);
        int fcb = (int)(layer.FontColor.B * 255);
        int ocr = (int)(layer.OutlineColor.R * 255);
        int ocg = (int)(layer.OutlineColor.G * 255);
        int ocb = (int)(layer.OutlineColor.B * 255);
        string fcHex = $"0x{fcr:x2}{fcg:x2}{fcb:x2}";
        string ocHex = $"0x{ocr:x2}{ocg:x2}{ocb:x2}";

        // Build time segments from text keyframes
        var segments = new List<(double segStart, double segEnd, string text, string? fontPath)>();
        if (layer.KeyframesText is { Count: > 0 })
        {
            var sorted = layer.KeyframesText.OrderBy(k => k.Time).ToList();
            double prevAbs = rs;
            string prevText = layer.Text ?? "";
            string? prevFont = layer.FontPath;
            foreach (var kf in sorted)
            {
                double kfAbs = layer.Start + kf.Time;
                if (kfAbs > prevAbs)
                    segments.Add((prevAbs, kfAbs, prevText, prevFont));
                prevAbs = kfAbs;
                prevText = kf.Text ?? prevText;
                prevFont = kf.FontPath ?? prevFont;
            }
            segments.Add((prevAbs, re, prevText, prevFont));
        }
        else
        {
            segments.Add((rs, re, layer.Text ?? "", layer.FontPath));
        }

        if (hasRotation)
        {
            // Rotation path: render text on a transparent canvas, rotate, overlay
            string canvasLabel = $"txt_{txtIdx}_canvas";
            filters.Add($"color=c=black@0:s={outW}x{outH}:d={duration},format=rgba[{canvasLabel}_init]");

            string prev = $"{canvasLabel}_init";
            int segIdx = 0;
            foreach (var (segStart, segEnd, segText, segFont) in segments)
            {
                double clampedStart = Math.Max(segStart, rs);
                double clampedEnd = Math.Min(segEnd, re);
                if (clampedStart >= clampedEnd) { segIdx++; continue; }

                string cleanText = EscapeFfmpegText(segText);
                string cur = segIdx == segments.Count - 1 ? canvasLabel : $"{canvasLabel}_seg{segIdx}";

                string fontArg = "";
                string? fp = segFont ?? layer.FontPath;
                if (!string.IsNullOrEmpty(fp) && File.Exists(fp))
                {
                    string escaped = fp.Replace(":", "\\:");
                    fontArg = $":fontfile='{escaped}'";
                }
                else if (File.Exists("/usr/share/fonts/noto/NotoSans-Regular.ttf"))
                    fontArg = ":fontfile='/usr/share/fonts/noto/NotoSans-Regular.ttf'";
                else if (File.Exists("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"))
                    fontArg = ":fontfile='/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf'";

                filters.Add(
                    $"[{prev}]drawtext=text='{cleanText}':fontsize='{fsExpr}':" +
                    $"fontcolor={fcHex}:borderw={layer.OutlineWidth}:bordercolor={ocHex}:" +
                    $"alpha='{alphaExpr}':" +
                    $"text_align=C:x='{bx}':y='{by}'{fontArg}:" +
                    $"enable='between(t,{clampedStart},{clampedEnd})'[{cur}]");
                prev = cur;
                segIdx++;
            }

            string rotated = $"{canvasLabel}_rot";
            filters.Add($"[{canvasLabel}]rotate={rotExpr}*PI/180:ow=iw:oh=ih:fillcolor=0x00000000[{rotated}]");
            filters.Add($"[{lastV}][{rotated}]overlay=0:0:enable='between(t,{rs},{re})'[{nextV}]");
        }
        else
        {
            // No rotation: draw text directly on the video frame (existing approach)
            int segIdx = 0;
            foreach (var (segStart, segEnd, segText, segFont) in segments)
            {
                double clampedStart = Math.Max(segStart, rs);
                double clampedEnd = Math.Min(segEnd, re);
                if (clampedStart >= clampedEnd) { segIdx++; continue; }

                string cleanText = EscapeFfmpegText(segText);
                string inputLabel = segIdx == 0 ? lastV : $"v_txt_{txtIdx}_seg{segIdx - 1}";
                string outputLabel = segIdx == segments.Count - 1 ? nextV : $"v_txt_{txtIdx}_seg{segIdx}";

                string fontArg = "";
                string? fp = segFont ?? layer.FontPath;
                if (!string.IsNullOrEmpty(fp) && File.Exists(fp))
                {
                    string escaped = fp.Replace(":", "\\:");
                    fontArg = $":fontfile='{escaped}'";
                }
                else if (File.Exists("/usr/share/fonts/noto/NotoSans-Regular.ttf"))
                    fontArg = ":fontfile='/usr/share/fonts/noto/NotoSans-Regular.ttf'";
                else if (File.Exists("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"))
                    fontArg = ":fontfile='/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf'";

                filters.Add(
                    $"[{inputLabel}]drawtext=text='{cleanText}':fontsize='{fsExpr}':" +
                    $"fontcolor={fcHex}:borderw={layer.OutlineWidth}:bordercolor={ocHex}:" +
                    $"alpha='{alphaExpr}':" +
                    $"text_align=C:x='{bx}':y='{by}'{fontArg}:" +
                    $"enable='between(t,{clampedStart},{clampedEnd})'[{outputLabel}]");

                segIdx++;
            }
        }
        lastV = nextV;
    }

    private static void AddAudioChain(List<string> filters,
        List<ExporterLayer> audioLayers, int streamStart,
        bool normalizeAudio, out string mapA)
    {
        var audioFilters = new List<string> { "[0:a]volume=1.0[a_main]" };
        var audioInputs = new List<string> { "[a_main]" };
        for (int i = 0; i < audioLayers.Count; i++)
        {
            var l = audioLayers[i];
            int delayMs = (int)(l.Start * 1000);
            int aidx = streamStart + i;
            audioFilters.Add($"[{aidx}:a]volume={l.Volume},adelay={delayMs}|{delayMs}[a{i}]");
            audioInputs.Add($"[a{i}]");
        }
        string amixStr = string.Join("", audioInputs);
        audioFilters.Add($"{amixStr}amix=inputs={audioInputs.Count}:duration=first[a_mixed]");
        if (normalizeAudio)
            audioFilters.Add("[a_mixed]loudnorm=I=-14:LRA=1:TP=-1[outa]");
        else
            audioFilters.Add("[a_mixed]copy[outa]");
        filters.AddRange(audioFilters);
        mapA = "[outa]";
    }

    private static (int w, int h) ProbeVideoDimensions(string path)
    {
        try
        {
            var psi = new ProcessStartInfo(FindFfmpeg().Replace("ffmpeg", "ffprobe"),
                $"-v error -select_streams v:0 -show_entries stream=width,height -of csv=s=,:p=0 \"{path}\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return (0, 0);
            string output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5000);
            var parts = output.Split(',');
            if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
                return (w, h);
        }
        catch { }
        return (0, 0);
    }

    private static (string lastV, string mapA) AddLayerInputsAndFilters(
        List<string> cmd, List<string> filters,
        List<ExporterLayer> layers, string lastV,
        double duration, int outW, int outH, int inputCount)
    {
        var imageLayers = layers.Where(l => l.Type is "image" or "gif").ToList();
        var textLayers = layers.Where(l => l.Type == "text").ToList();
        var audioLayers = layers.Where(l => l.Type == "audio").ToList();

        // Image inputs (stream indices: inputCount, inputCount+1, ...)
        for (int i = 0; i < imageLayers.Count; i++)
        {
            var l = imageLayers[i];
            if (l.Type == "gif")
                cmd.AddRange(new[] { "-ignore_loop", "0", "-i", l.Path! });
            else
                cmd.AddRange(new[] { "-loop", "1", "-i", l.Path! });

            int streamIdx = inputCount + i;
            string nextV = $"v_img_{i}";
            string xExpr = GenerateFfmpegExpression(l.KeyframesX, l.X, duration, timeOffset: l.Start);
            string yExpr = GenerateFfmpegExpression(l.KeyframesY, l.Y, duration, timeOffset: l.Start);
            string rotExpr = GenerateFfmpegExpression(l.KeyframesRotation, l.Rotation, duration, timeOffset: l.Start);
            int targetW = Math.Max(1, (int)(outW * l.W));
            double rs = Math.Max(0, l.Start);
            double re = Math.Min(l.End, duration);
            string scaleFilter = $"scale={targetW}:-1:flags=lanczos";
            bool hasRotation = l.Rotation != 0 || l.KeyframesRotation != null;
            if (hasRotation)
                scaleFilter += $",rotate={rotExpr}*PI/180:fillcolor=0x00000000";
            filters.Add($"[{streamIdx}:v]{scaleFilter}[ovl_img{i}]");
            filters.Add($"[{lastV}][ovl_img{i}]overlay=x='(W-w)*{xExpr}':y='(H-h)*{yExpr}':enable='between(t,{rs},{re})'[{nextV}]");
            lastV = nextV;
        }

        // Text layers
        for (int i = 0; i < textLayers.Count; i++)
            AddTextLayer(filters, textLayers[i], i, ref lastV, duration, outW, outH);

        // Audio chain
        string mapA = "0:a";
        int audioStreamStart = inputCount + imageLayers.Count;
        if (audioLayers.Count > 0)
        {
            // Add audio inputs to cmd
            foreach (var l in audioLayers)
                cmd.AddRange(new[] { "-i", l.Path! });
            AddAudioChain(filters, audioLayers, audioStreamStart, false, out mapA);
        }

        return (lastV, mapA);
    }

    private static string EscapeFfmpegText(string text)
    {
        return text
            .Replace("\\", "\\\\")
            .Replace("'", "'\\''")
            .Replace(":", "\\:")
            .Replace(",", "\\,");
    }

    private static string GenerateFfmpegExpression(List<Keyframe>? keyframes,
        double defaultVal, double duration, double timeOffset = 0)
    {
        if (keyframes == null || keyframes.Count == 0)
            return defaultVal.ToString("F6");

        var sorted = keyframes.OrderBy(k => k.Time).ToList();
        if (sorted.Count == 1)
            return sorted[0].Value.ToString("F6");

        double t0 = sorted[0].Time + timeOffset;
        string expr = sorted[^1].Value.ToString("F6");
        for (int i = sorted.Count - 2; i >= 0; i--)
        {
            double t1 = sorted[i].Time + timeOffset, v1 = sorted[i].Value;
            double t2 = sorted[i + 1].Time + timeOffset, v2 = sorted[i + 1].Value;
            double slope = t2 - t1 != 0 ? (v2 - v1) / (t2 - t1) : 0;
            string segExpr = $"{v1:F6} + (t-{t1:F6})*{slope:F6}";
            expr = $"if(between(t,{t1:F6},{t2:F6}), {segExpr}, {expr})";
        }
        expr = $"if(lt(t,{t0:F6}), {sorted[0].Value:F6}, {expr})";
        return $"({expr})";
    }
}
