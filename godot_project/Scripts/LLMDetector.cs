using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace VelosCCS;

public class LLMHighlightDetector
{
    public async Task<List<(double start, double end)>> FindHighlightsAsync(
        List<Segment> segments,
        int maxClips = 5,
        double minDuration = 15.0,
        double maxDuration = 60.0,
        Action<string>? progressCallback = null)
    {
        if (segments == null || segments.Count == 0)
            return new();

        double baseTime = segments[0].Start;
        double windowDuration = segments[^1].End - baseTime;
        string transcriptText = SegmentsToText(segments, baseTime);

        double minDur = Math.Min(minDuration, windowDuration * 0.5);
        double maxDur = Math.Min(maxDuration, windowDuration);

        string prompt = GetViralPrompt(transcriptText, windowDuration, maxClips, minDur, maxDur);

        progressCallback?.Invoke("Sending transcript to AI...");

        List<(double start, double end)> clips;
        try
        {
            clips = await QueryLlamaWorkerAsync(prompt, maxClips, progressCallback);
        }
        catch (Exception e)
        {
            Log.Print($"[LLMDetect] LlamaWorker failed: {e.Message} — trying Ollama fallback...");
            progressCallback?.Invoke("LlamaWorker failed, trying Ollama...");
            clips = await TryQueryOllamaFallbackAsync(prompt, maxClips, progressCallback);
        }

        Log.Print($"[LLMDetect] baseTime={baseTime:F1}s windowDur={windowDuration:F1}s segs={segments.Count} chars={prompt.Length}");
        if (clips.Count > 0)
            Log.Print($"[LLMDetect] LLM returned {clips.Count} raw: [{string.Join(", ", clips.Select(c => $"({c.start:F1},{c.end:F1})"))}]");

        var valid = new List<(double start, double end)>();
        foreach (var (relStart, relEnd) in clips)
        {
            double s = baseTime + Math.Max(0, relStart);
            double e = baseTime + Math.Max(s - baseTime + 1, relEnd);
            if (e > baseTime + windowDuration) e = baseTime + windowDuration;
            double dur = e - s;
            if (dur < minDuration * 0.5)
            {
                Log.Print($"[LLMDetect]  reject ({s:F1},{e:F1}) dur={dur:F1}s < {minDuration * 0.5:F0}s minimum");
                continue;
            }
            if (valid.Any(v => !(e <= v.start || s >= v.end)))
            {
                Log.Print($"[LLMDetect]  reject ({s:F1},{e:F1}) overlaps existing");
                continue;
            }
            valid.Add((s, e));
            Log.Print($"[LLMDetect]  accept ({s:F1},{e:F1}) dur={dur:F1}s");
        }

        if (valid.Count == 0)
            Log.Print($"[LLMDetect] FindHighlights: no valid clips after filtering ({clips.Count} from LLM)");

        return valid;
    }

    private string GetViralPrompt(string transcriptSegment, double totalDuration, int maxClips, double minDur, double maxDur)
    {
        string body = $@"Find exactly {maxClips} clips from this transcript. 
RULES:
- Each clip must be {minDur}-{maxDur} seconds long. MINIMUM {minDur}s. NEVER shorter.
- Clips start at a funny/exciting moment and last {minDur}-{maxDur}s.
- Return EXACTLY {maxClips} clips even if not perfect.

TRANSCRIPT (t=seconds from chunk start):
{transcriptSegment}

Return JSON array: [{{""start"": float, ""end"": float}}]
No markdown, no explanation. Only valid JSON. Example for {maxClips}x {minDur}s clips:
[{{""start"": {minDur:F0}.0, ""end"": {maxDur:F0}.0}}, {{""start"": 300.0, ""end"": 340.0}}]";

        // Llama 3.2 Instruct chat template
        return $"<|begin_of_text|><|start_header_id|>user<|end_header_id|>\n\n{body}<|eot|><|start_header_id|>assistant<|end_header_id|>\n\n";
    }

    private static string SegmentsToText(List<Segment> segments, double baseTime = 0)
    {
        const int maxSegments = 150;
        var sampled = segments;
        if (segments.Count > maxSegments)
        {
            int step = (int)Math.Ceiling((double)segments.Count / maxSegments);
            sampled = segments.Where((_, i) => i % step == 0).ToList();
            Log.Print($"[LLMDetect] SegmentsToText: {segments.Count} → sampled {sampled.Count} (step={step})");
        }
        var lines = new List<string>();
        foreach (var s in sampled)
        {
            double t = s.Start - baseTime;
            lines.Add($"t={t:F1} {s.Text}");
        }
        return string.Join("\n", lines);
    }

    private async Task<List<(double start, double end)>> QueryLlamaWorkerAsync(
        string prompt, int maxClips, Action<string>? progressCallback = null)
    {
        string cliPath = LlamaManager.FindCliBinary();
        if (string.IsNullOrEmpty(cliPath))
        {
            Log.Error("[LLMDetect] llama-cli binary not found");
            throw new InvalidOperationException("llama-cli not found — run setup first");
        }

        string modelPath = LlamaManager.GetModelPath();
        if (!File.Exists(modelPath))
        {
            progressCallback?.Invoke("Downloading model...");
            bool downloaded = await LlamaManager.EnsureModelDownloadedAsync(progressCallback: progressCallback);
            if (!downloaded)
                throw new InvalidOperationException("Failed to download model");
        }

        string workerDir = Path.GetDirectoryName(cliPath)!;

        var sw = Stopwatch.StartNew();

        // Write prompt to temp file to avoid shell escaping issues
        string tmpPrompt = System.IO.Path.GetTempFileName();
        try
        {
            await System.IO.File.WriteAllTextAsync(tmpPrompt, prompt);

            Log.Print($"[LLMDetect] Starting llama-cli directly: model={Path.GetFileName(modelPath)} prompt_len={prompt.Length}");

            int cpuThreads = Math.Clamp(System.Environment.ProcessorCount, 1, 16);
            string cliArgs = $"-ngl -1 -m \"{modelPath}\" -f \"{tmpPrompt}\" --no-display-prompt --single-turn -n 1024 --temp 0.1 -t {cpuThreads}";
            Log.Print($"[LLMDetect] args: {cliArgs}");

            var psi = new ProcessStartInfo(cliPath, cliArgs)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            if (!OperatingSystem.IsWindows())
            {
                string existingLdPath = System.Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? "";
                string ldPath = workerDir +
                    (existingLdPath.Length > 0 ? ":" + existingLdPath : "");
                psi.EnvironmentVariables["LD_LIBRARY_PATH"] = ldPath;
                Log.Print($"[LLMDetect] LD_LIBRARY_PATH={ldPath}");
            }

            using var proc = Process.Start(psi);
            if (proc == null)
                throw new InvalidOperationException("Failed to start llama-cli");

            // Read stdout and stderr concurrently to avoid pipe deadlock
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            var timeoutTask = Task.Delay(TimeSpan.FromMinutes(5));

            var completed = await Task.WhenAny(stdoutTask, timeoutTask);
            if (completed == timeoutTask)
            {
                proc.Kill();
                throw new TimeoutException("llama-cli timed out after 5 minutes");
            }

            string stdout = await stdoutTask;
            string stderr = await stderrTask;

            // Clean up temp file
            try { System.IO.File.Delete(tmpPrompt); } catch { }

            if (!string.IsNullOrWhiteSpace(stderr))
                foreach (string line in stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    Log.Print($"[llama-cli] {line.Trim()}");

            proc.WaitForExit(5000);
            sw.Stop();
            Log.Print($"[LLMDetect] llama-cli exited {proc.ExitCode} in {sw.Elapsed.TotalSeconds:F1}s (stdout={stdout.Length} chars)");

            // Check for model corruption and re-download + retry once
            if (proc.ExitCode != 0 && (stderr.Contains("corrupted") || stderr.Contains("not within the file bounds")))
            {
                Log.Print($"[LLMDetect] Model corrupted at {modelPath}, re-downloading...");
                progressCallback?.Invoke("Model corrupted, re-downloading...");
                try { System.IO.File.Delete(modelPath); } catch { }
                bool downloaded = await LlamaManager.EnsureModelDownloadedAsync(progressCallback: progressCallback);
                if (!downloaded)
                    throw new InvalidOperationException("Failed to re-download model");
                Log.Print($"[LLMDetect] Retrying with re-downloaded model...");
                return await QueryLlamaWorkerAsync(prompt, maxClips, progressCallback);
            }

            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                Log.Error($"[LLMDetect] llama-cli failed (exit={proc.ExitCode}): {stdout?[..Math.Min(stdout.Length, 500)]}");
                throw new InvalidOperationException($"llama-cli failed with exit code {proc.ExitCode}");
            }

            // Parse output — llama-cli outputs raw text, trim prompt prefix if any
            string text = stdout.Trim();
            // Remove the leading prompt echo (llama-cli prints "> " prefix per line)
            var lines = text.Split('\n')
                .Where(l => !l.TrimStart().StartsWith(">"))
                .Select(l => l.TrimStart('>').Trim())
                .Where(l => l.Length > 0)
                .ToList();
            text = string.Join("\n", lines);
            if (string.IsNullOrWhiteSpace(text))
                text = stdout.Trim();

        Log.Print($"[LLMDetect] response ({sw.Elapsed.TotalSeconds:F1}s, {text.Length} chars): {text[..Math.Min(text.Length, 500)]}");
        if (text.Length > 500)
            Log.Print($"[LLMDetect] ... (truncated, total {text.Length} chars)");

        return ParseResponse(text, maxClips);
        }
        catch
        {
            // Ensure temp file cleanup on any exception
            try { System.IO.File.Delete(tmpPrompt); } catch { }
            throw;
        }
    }

    private static List<(double start, double end)> ParseResponse(string text, int maxClips)
    {
        text = Regex.Replace(text, @"```(?:json)?\s*", "", RegexOptions.Singleline);
        var match = Regex.Match(text, @"\[.*?(\]|\])", RegexOptions.Singleline);
        if (!match.Success)
        {
            var partial = Regex.Match(text, @"\[.*", RegexOptions.Singleline);
            if (!partial.Success)
                return FallbackParseRegex(text, maxClips);
            match = partial;
        }

        string json = match.Value;
        int openCount = json.Count(c => c == '[');
        int closeCount = json.Count(c => c == ']');
        if (closeCount < openCount)
            json += new string(']', openCount - closeCount);

        json = Regex.Replace(json, @"(\w+)\s*=", "\"$1\": ");
        json = json.Replace("'", "\"");

        JsonElement items;
        try
        {
            items = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true }).RootElement;
        }
        catch (Exception ex)
        {
            Log.Print($"[LLMDetect] ParseResponse: JSON parse failed: {ex.Message} — trying regex fallback");
            return FallbackParseRegex(json, maxClips);
        }

        if (items.ValueKind != JsonValueKind.Array)
        {
            Log.Print($"[LLMDetect] ParseResponse: root is not array, got {items.ValueKind}");
            return new();
        }

        var clips = new List<(double, double)>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("start", out var s) || !item.TryGetProperty("end", out var e)) continue;
            if (s.ValueKind == JsonValueKind.Number && e.ValueKind == JsonValueKind.Number)
                clips.Add((s.GetDouble(), e.GetDouble()));
        }

        if (clips.Count == 0)
            Log.Print($"[LLMDetect] ParseResponse: array has {items.GetArrayLength()} items, but none had valid start/end numbers");

        return clips.Take(maxClips).ToList();
    }

    private static List<(double start, double end)> FallbackParseRegex(string text, int maxClips)
    {
        var clips = new List<(double, double)>();
        var re = new Regex(@"""start""\s*[:=]\s*([\d.]+)\s*[,\s]*""end""\s*[:=]\s*([\d.]+)", RegexOptions.IgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in re.Matches(text))
        {
            if (double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double s) &&
                double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double e))
            {
                clips.Add((s, e));
            }
        }
        if (clips.Count > 0)
            Log.Print($"[LLMDetect] FallbackParseRegex: extracted {clips.Count} clips");
        else
            Log.Print($"[LLMDetect] FallbackParseRegex: no clips found. Starts: \"{text[..Math.Min(text.Length, 200)]}\"");
        return clips.Take(maxClips).ToList();
    }

    public static async Task UnloadModelAsync()
    {
        // No-op: LlamaWorker is a subprocess, VRAM freed when process exits
        await Task.CompletedTask;
    }

    // ── Ollama fallback ──

    private static readonly System.Net.Http.HttpClient _ollamaClient = new() { Timeout = TimeSpan.FromSeconds(300) };
    private const string OllamaHost = "http://localhost:11434";

    private static async Task<bool> IsOllamaRunningAsync()
    {
        try
        {
            var response = await _ollamaClient.GetAsync($"{OllamaHost}/api/tags");
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private async Task<List<(double start, double end)>> TryQueryOllamaFallbackAsync(
        string prompt, int maxClips, Action<string>? progressCallback = null)
    {
        if (!await IsOllamaRunningAsync())
        {
            Log.Print("[LLMDetect] Ollama not running — no fallback available");
            progressCallback?.Invoke("AI detection failed: no LLM backend available");
            return new();
        }

        string model = OllamaFallbackModel();
        progressCallback?.Invoke($"Using Ollama ({model})...");
        Log.Print($"[LLMDetect] Ollama fallback with model={model}");

        var payload = new
        {
            model,
            prompt,
            stream = false,
            keep_alive = "0",
            options = new { temperature = 0.1, num_predict = 1024 },
        };
        string jsonPayload = JsonSerializer.Serialize(payload);
        var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        var sw = Stopwatch.StartNew();
        var response = await _ollamaClient.PostAsync($"{OllamaHost}/api/generate", content);
        string responseText = await response.Content.ReadAsStringAsync();
        sw.Stop();

        if (!response.IsSuccessStatusCode)
        {
            Log.Error($"[LLMDetect] Ollama returned {response.StatusCode}: {responseText[..Math.Min(responseText.Length, 500)]}");
            progressCallback?.Invoke("Ollama fallback failed");
            return new();
        }

        using var doc = JsonDocument.Parse(responseText);
        string text = doc.RootElement.TryGetProperty("response", out var r)
            ? r.GetString() ?? "" : "";

        Log.Print($"[LLMDetect] Ollama fallback ({sw.Elapsed.TotalSeconds:F1}s, {text.Length} chars): {text[..Math.Min(text.Length, 500)]}");
        if (text.Length > 500)
            Log.Print($"[LLMDetect] ... (truncated, total {text.Length} chars)");

        return ParseResponse(text, maxClips);
    }

    private static string OllamaFallbackModel()
    {
        // Try to find a suitable model from Ollama's installed list
        try
        {
            var response = _ollamaClient.GetAsync($"{OllamaHost}/api/tags").Result;
            if (!response.IsSuccessStatusCode) return "llama3.2:3b";
            string json = response.Content.ReadAsStringAsync().Result;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("models", out var arr))
            {
                foreach (var m in arr.EnumerateArray())
                {
                    string name = m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (name.Contains("llama3.2") || name.Contains("phi3") || name.Contains("tinyllama"))
                        return name;
                }
                // Return first available model
                foreach (var m in arr.EnumerateArray())
                {
                    string name = m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(name)) return name;
                }
            }
        }
        catch { }
        return "llama3.2:3b";
    }
}
