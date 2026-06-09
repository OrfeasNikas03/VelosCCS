using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Whisper.net;

namespace VelosCCS;

public class Transcriber : IDisposable
{
    private WhisperFactory? _factory;
    private bool _modelDownloaded;
    private static string? _workerPath;
    private static bool _vulkanExhausted;

    public Transcriber()
    {
        System.Environment.SetEnvironmentVariable("WHISPER_RUNTIME", "vulkan");
    }

	private static string FindWorkerBinary()
	{
		if (_workerPath != null) return _workerPath;

		string baseDir = AppDomain.CurrentDomain.BaseDirectory;
		string worker = OperatingSystem.IsWindows() ? "WhisperWorker.exe" : "WhisperWorker";

		// Candidate 0: project root via res:// (works in editor)
		string c0 = ProjectSettings.GlobalizePath("res://WhisperWorker_published/" + worker);
		if (System.IO.File.Exists(c0)) { _workerPath = c0; Log.Print($"[Transcriber] Found {c0}"); return _workerPath; }

		// Candidate 1: alongside the executable (exported app)
		string c1 = System.IO.Path.Combine(baseDir, "WhisperWorker_published", worker);
        if (System.IO.File.Exists(c1)) { _workerPath = c1; Log.Print($"[Transcriber] Found {c1}"); return _workerPath; }

        // Candidate 2: same directory as executable (flat layout)
        string c2 = System.IO.Path.Combine(baseDir, worker);
        if (System.IO.File.Exists(c2)) { _workerPath = c2; Log.Print($"[Transcriber] Found {c2}"); return _workerPath; }

        // Candidate 3: one level up from baseDir (when running from data_* subdir)
        string c3 = System.IO.Path.Combine(baseDir, "..", "WhisperWorker_published", worker);
        if (System.IO.File.Exists(c3)) { _workerPath = c3; Log.Print($"[Transcriber] Found {c3}"); return _workerPath; }

        _workerPath = "";
        Log.Print("[Transcriber] WhisperWorker not found — falling back to in-process whisper");
        return "";
    }

    private string GetModelPath()
    {
        string cacheDir = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            ".cache", "velosccs", "whisper");
        return System.IO.Path.Combine(cacheDir, $"ggml-{AppConfig.WhisperModel}.bin");
    }

    public async Task EnsureModelDownloadedAsync(Action<string>? progressCallback = null)
    {
        if (_modelDownloaded) return;
        string modelFile = GetModelPath();

        if (System.IO.File.Exists(modelFile))
        {
            _modelDownloaded = true;
            return;
        }

        progressCallback?.Invoke($"Downloading {AppConfig.WhisperModel} model...");
        string cacheDir = System.IO.Path.GetDirectoryName(modelFile)!;
        System.IO.Directory.CreateDirectory(cacheDir);
        string repo = $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{AppConfig.WhisperModel}.bin";
        using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        using var response = await client.GetAsync(repo);
        response.EnsureSuccessStatusCode();
        using var fs = System.IO.File.Create(modelFile);
        await response.Content.CopyToAsync(fs);
        progressCallback?.Invoke("Model download complete.");
        _modelDownloaded = true;
    }

    public async Task LoadModelAsync(Action<string>? progressCallback = null)
    {
        Log.Print("[Transcriber] LoadModelAsync started");
        if (_factory != null) return;
        await EnsureModelDownloadedAsync(progressCallback);

        progressCallback?.Invoke("Loading transcription model...");
        _factory = WhisperFactory.FromPath(GetModelPath());
        Log.Print("[Transcriber] LoadModelAsync completed");
    }

    public void UnloadModel()
    {
        _factory?.Dispose();
        _factory = null;
    }

    public async Task<Transcript> TranscribeChunkAsync(
        string audioPath, double startTime, double endTime,
        string? language = null,
        Action<string>? progressCallback = null,
        IProgress<double>? progress = null)
    {
        Log.Print("[Transcriber] Transcribe started");
        string lang = language ?? AppConfig.CaptionLanguage;
        string worker = FindWorkerBinary();
        Log.Print("[Transcriber] Transcribe completed");
        if (string.IsNullOrEmpty(worker))
            return await TranscribeInProcessAsync(audioPath, startTime, endTime, lang, progressCallback, progress);

        string runtime = _vulkanExhausted ? "cpu" : "vulkan";
        return await TranscribeViaWorkerAsync(worker, audioPath, startTime, endTime, runtime, lang, progressCallback);
    }

    private async Task<Transcript> TranscribeViaWorkerAsync(
        string workerPath, string audioPath, double startTime, double endTime,
        string runtime, string language,
        Action<string>? progressCallback = null)
    {
        await EnsureModelDownloadedAsync(progressCallback);

        // Wait for VRAM to be available before starting a Vulkan worker
        if (runtime == "vulkan")
            await WaitForFreeVramAsync();

        double chunkDuration = endTime - startTime;
        string label = FormatTime(startTime);
        progressCallback?.Invoke($"Transcribing {label}...");

        string modelPath = GetModelPath();
        string wavPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"velosccs_w_{startTime:F0}_{endTime:F0}.wav");

        try
        {
            ExtractWavToFile(audioPath, startTime, chunkDuration, wavPath);

            int threads = AppConfig.WhisperThreads;
            var psi = new ProcessStartInfo(workerPath, $"\"{modelPath}\" \"{wavPath}\" {threads}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.EnvironmentVariables["WHISPER_RUNTIME"] = runtime;
            psi.EnvironmentVariables["WHISPER_LANGUAGE"] = language;
            string runtimeDir = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(workerPath)!,
                "runtimes",
                runtime == "cpu" ? "linux-x64" : System.IO.Path.Combine(runtime, "linux-x64"));
            string existingLdPath = System.Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? "";
            psi.EnvironmentVariables["LD_LIBRARY_PATH"] = runtimeDir +
                (existingLdPath.Length > 0 ? ":" + existingLdPath : "");

            using var proc = Process.Start(psi);
            if (proc == null)
                throw new InvalidOperationException("Failed to start WhisperWorker");

            string stdout = await proc.StandardOutput.ReadToEndAsync();
            string stderr = await proc.StandardError.ReadToEndAsync();

            // Log worker diagnostics from stderr
            if (!string.IsNullOrWhiteSpace(stderr))
                foreach (string line in stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    Log.Print($"[Worker] {line.Trim()}");

            bool exited = proc.WaitForExit((int)TimeSpan.FromMinutes(10).TotalMilliseconds);
            if (!exited)
            {
                proc.Kill();
                throw new TimeoutException("WhisperWorker timed out");
            }

            var segments = new List<Segment>();

            if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
            {
                segments = ParseWorkerSegments(stdout, startTime);
            }
            else if (proc.ExitCode != 0)
            {
                Log.Error($"[Transcriber] WhisperWorker exit {proc.ExitCode} at {label}: {stderr}");
                if (proc.ExitCode != 1)
                    progressCallback?.Invoke($"Whisper crashed — no transcript for {label}");

                // Detect Vulkan OOM and fall back to CPU for remaining windows
                if (runtime == "vulkan" && (stderr.Contains("OutOfDeviceMemory") || stderr.Contains("out of memory")))
                {
                    _vulkanExhausted = true;
                    Log.Print("[Transcriber] Vulkan OOM detected — falling back to CPU whisper for remaining windows");
                    progressCallback?.Invoke("GPU out of memory — switching to CPU whisper");
                }
            }

            return new Transcript
            {
                Segments = segments,
                Language = language,
                Duration = chunkDuration,
            };
        }
        finally
        {
            try { System.IO.File.Delete(wavPath); } catch { }
        }
    }

    private static List<Segment> ParseWorkerSegments(string json, double baseTime)
    {
        var segments = new List<Segment>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("segments", out var arr))
                return segments;

            foreach (var item in arr.EnumerateArray())
            {
                double start = baseTime + item.GetProperty("start").GetDouble();
                double end = baseTime + item.GetProperty("end").GetDouble();
                string text = item.GetProperty("text").GetString() ?? "";

                var rawWords = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                int wordCount = Math.Max(rawWords.Length, 1);
                double wordDur = (end - start) / wordCount;
                var words = rawWords.Select((w, i) => new Word
                {
                    Text = w.Trim().TrimEnd('.', '!', '?', ',', ';', ':'),
                    Start = start + i * wordDur,
                    End = start + (i + 1) * wordDur,
                    Probability = 0,
                }).ToList();

                segments.Add(new Segment
                {
                    Start = start,
                    End = end,
                    Text = text,
                    Words = words,
                });
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[Transcriber] Failed to parse worker JSON: {ex.Message}");
        }
        return segments;
    }

    private async Task<Transcript> TranscribeInProcessAsync(
        string audioPath, double startTime, double endTime,
        string language,
        Action<string>? progressCallback = null,
        IProgress<double>? progress = null)
    {
        await LoadModelAsync(progressCallback);

        double chunkDuration = endTime - startTime;
        progressCallback?.Invoke($"Transcribing {FormatTime(startTime)} - {FormatTime(endTime)}...");

        byte[] wavData = DecodeAudioChunkToWav(audioPath, startTime, chunkDuration);

        var segments = new List<Segment>();

        int whisperThreads = AppConfig.WhisperThreads;
        using var processor = _factory!
            .CreateBuilder()
            .WithLanguage(language)
            .WithThreads(whisperThreads)
            .Build();

        await foreach (var result in processor.ProcessAsync(new MemoryStream(wavData)))
        {
            if (string.IsNullOrWhiteSpace(result.Text)) continue;
            progress?.Report(Math.Min(result.End.TotalSeconds / chunkDuration, 1.0));

            string text = result.Text.Trim();
            double segStart = startTime + result.Start.TotalSeconds;
            double segEnd = startTime + result.End.TotalSeconds;
            double segDur = segEnd - segStart;

            var rawWords = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int wordCount = Math.Max(rawWords.Length, 1);
            double wordDur = segDur / wordCount;
            var words = rawWords.Select((w, i) => new Word
            {
                Text = w.Trim().TrimEnd('.', '!', '?', ',', ';', ':'),
                Start = segStart + i * wordDur,
                End = segStart + (i + 1) * wordDur,
                Probability = 0,
            }).ToList();

            segments.Add(new Segment
            {
                Start = segStart,
                End = segEnd,
                Text = text,
                Words = words,
            });
        }

        progress?.Report(1.0);

        return new Transcript
        {
            Segments = segments,
            Language = language,
            Duration = chunkDuration,
        };
    }

    public async Task<Transcript> TranscribeAsync(
        string audioPath,
        string? language = null,
        Action<string>? progressCallback = null,
        IProgress<double>? progress = null)
    {
        double totalDuration = GetDuration(audioPath);
        if (totalDuration <= 0) totalDuration = 1;
        return await TranscribeChunkAsync(audioPath, 0, totalDuration, language, progressCallback, progress);
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _factory = null;
    }

    private static void ExtractWavToFile(string audioPath, double startTime, double duration, string outPath)
    {
        Log.Print($"[Transcriber] ExtractWavToFile: {audioPath} start={startTime:F3} dur={duration:F3} -> {outPath}");
        var psi = new ProcessStartInfo("ffmpeg",
            $"-ss {startTime:F3} -i \"{audioPath}\" -t {duration:F3} -f wav -acodec pcm_s16le -ac 1 -ar 16000 -y -hide_banner -loglevel error \"{outPath}\"")
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                Log.Error("[Transcriber] ExtractWavToFile: failed to start ffmpeg");
                throw new InvalidOperationException("Failed to start ffmpeg");
            }
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(120000);
            if (proc.ExitCode != 0)
            {
                Log.Error($"[Transcriber] ExtractWavToFile: ffmpeg exit {proc.ExitCode}: {stderr.Trim()}");
                throw new InvalidOperationException("ffmpeg chunk extraction failed");
            }
        }
        catch (Exception e) when (e is not InvalidOperationException)
        {
            Log.Error($"[Transcriber] ExtractWavToFile exception: {e.Message}");
            throw;
        }
    }

    private static byte[] DecodeAudioChunkToWav(string audioPath, double startTime, double duration)
    {
        Log.Print($"[Transcriber] DecodeAudioChunkToWav: {audioPath} start={startTime:F3} dur={duration:F3}");
        var psi = new ProcessStartInfo("ffmpeg",
            $"-ss {startTime:F3} -i \"{audioPath}\" -t {duration:F3} -f wav -acodec pcm_s16le -ac 1 -ar 16000 -y -hide_banner -loglevel error pipe:1")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                Log.Error("[Transcriber] DecodeAudioChunkToWav: failed to start ffmpeg");
                throw new InvalidOperationException("Failed to start ffmpeg");
            }

            using var ms = new MemoryStream();
            proc.StandardOutput.BaseStream.CopyTo(ms);
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(120000);

            if (proc.ExitCode != 0)
            {
                Log.Error($"[Transcriber] DecodeAudioChunkToWav: ffmpeg exit {proc.ExitCode}: {stderr.Trim()}");
                throw new InvalidOperationException("ffmpeg audio chunk decode failed");
            }

            return ms.ToArray();
        }
        catch (Exception e) when (e is not InvalidOperationException)
        {
            Log.Error($"[Transcriber] DecodeAudioChunkToWav exception: {e.Message}");
            throw;
        }
    }

    private static string FormatTime(double t)
    {
        int h = (int)(t / 3600);
        int m = (int)((t % 3600) / 60);
        int s = (int)(t % 60);
        return $"{h:D2}:{m:D2}:{s:D2}";
    }

    private static byte[] DecodeAudioToWav(string audioPath)
    {
        Log.Print($"[Transcriber] DecodeAudioToWav: {audioPath}");
        var psi = new ProcessStartInfo("ffmpeg",
            $"-i \"{audioPath}\" -f wav -acodec pcm_s16le -ac 1 -ar 16000 -y -hide_banner -loglevel error pipe:1")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                Log.Error("[Transcriber] DecodeAudioToWav: failed to start ffmpeg");
                throw new InvalidOperationException("Failed to start ffmpeg");
            }

            using var ms = new MemoryStream();
            proc.StandardOutput.BaseStream.CopyTo(ms);
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(120000);

            if (proc.ExitCode != 0)
            {
                Log.Error($"[Transcriber] DecodeAudioToWav: ffmpeg exit {proc.ExitCode}: {stderr.Trim()}");
                throw new InvalidOperationException("ffmpeg audio decoding failed");
            }

            return ms.ToArray();
        }
        catch (Exception e) when (e is not InvalidOperationException)
        {
            Log.Error($"[Transcriber] DecodeAudioToWav exception: {e.Message}");
            throw;
        }
    }

    private static double GetDuration(string path)
    {
        Log.Print($"[Transcriber] GetDuration: {path}");
        var psi = new ProcessStartInfo("ffprobe",
            $"-v error -show_entries format=duration -of csv=p=0 \"{path}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                Log.Warn("[Transcriber] GetDuration: failed to start ffprobe");
                return 0;
            }
            string output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(10000);
            if (double.TryParse(output, out var d))
                return d;
            Log.Warn($"[Transcriber] GetDuration: could not parse ffprobe output: \"{output}\"");
            return 0;
        }
        catch (Exception e)
        {
            Log.Error($"[Transcriber] GetDuration exception: {e.Message}");
            return 0;
        }
    }

    private static async Task WaitForFreeVramAsync(long minFreeMb = 500, int maxWaitSec = 30)
    {
        if (!File.Exists("/usr/bin/nvidia-smi"))
        {
            await Task.Delay(3000);
            return;
        }

        long freeMb = 0;
        for (int i = 0; i < maxWaitSec; i++)
        {
            try
            {
                var psi = new ProcessStartInfo("nvidia-smi",
                    "--query-gpu=memory.free --format=csv,noheader,nounits")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                if (proc == null) break;
                string output = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                proc.WaitForExit(3000);
                if (long.TryParse(output.Trim(), out freeMb) && freeMb >= minFreeMb)
                    return;
            }
            catch { }

            await Task.Delay(1000);
        }

        Log.Print($"[Transcriber] VRAM wait exhausted ({freeMb} MB free, needed {minFreeMb}) — proceeding anyway");
    }
}
