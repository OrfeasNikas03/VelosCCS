using Godot;
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace VelosCCS;

public class StreamInfo
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public double Duration { get; set; }
    public string WebpageUrl { get; set; } = "";
    public string Uploader { get; set; } = "";
    public string Thumbnail { get; set; } = "";
}

public class StreamManager
{
    private string FindYtDlp()
    {
        string executableDir = OS.GetExecutablePath().GetBaseDir();
        string[] candidates = { "yt-dlp.exe", "yt-dlp" };
        foreach (var name in candidates)
        {
            string sidecar = executableDir.PathJoin(name);
            if (File.Exists(sidecar)) return sidecar;
            string envPath = executableDir.PathJoin("python_env/" + name);
            if (File.Exists(envPath)) return envPath;
        }
        return "yt-dlp";
    }

    public StreamInfo GetInfo(string url)
    {
        string ytDlp = FindYtDlp();
        Log.Print($"StreamManager.GetInfo: {url}");
        var psi = new ProcessStartInfo(ytDlp, $"-j --no-download {url}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

		using var proc = Process.Start(psi);
		if (proc == null) throw new InvalidOperationException("Failed to start yt-dlp");

		string output = proc.StandardOutput.ReadToEnd();
		string error = proc.StandardError.ReadToEnd().Trim();
		proc.WaitForExit();
		if (proc.ExitCode != 0)
		    throw new InvalidOperationException($"yt-dlp info failed (exit {proc.ExitCode}): {error}");

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
		var info = new StreamInfo
		{
			Url = url,
			Title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "Untitled" : "Untitled",
			Duration = root.TryGetProperty("duration", out var d) ? d.GetDouble() : 0,
			WebpageUrl = root.TryGetProperty("webpage_url", out var w) ? w.GetString() ?? url : url,
			Uploader = root.TryGetProperty("uploader", out var u) ? u.GetString() ?? "" : "",
			Thumbnail = root.TryGetProperty("thumbnail", out var th) ? th.GetString() ?? "" : "",
		};
		Log.Print($"[DL] GetInfo done: title={info.Title}, duration={info.Duration:F0}s");
		return info;
    }

    public string DownloadAudio(string url, string outputPath)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Log.Print($"[DL] DownloadAudio start: {url}");
        string ext = "opus";
        string path = Path.ChangeExtension(outputPath, ext);
        if (File.Exists(path)) { Log.Print($"StreamManager.DownloadAudio: cached at {path}"); return path; }

        string ytDlp = FindYtDlp();
        var psi = new ProcessStartInfo(ytDlp,
            $"-f bestaudio -o \"{path}\" --no-playlist {url}")
        {
            RedirectStandardOutput = false,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi);
        if (proc == null) throw new InvalidOperationException("Failed to start yt-dlp");
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"yt-dlp audio download failed (exit {proc.ExitCode}): {stderr.Trim()}");

        Log.Print($"[DL] DownloadAudio done: {System.IO.Path.GetFileName(path)} in {sw.Elapsed.TotalSeconds:F1}s");
        return path;
    }

    public string DownloadAudioWithProgress(string url, string outputPath, DownloadProgressCallback? onProgress)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Log.Print($"[DL] DownloadAudioWithProgress start: {url}");
        try
        {
            string ext = "opus";
            string path = Path.ChangeExtension(outputPath, ext);
            if (File.Exists(path))
            {
                onProgress?.Invoke("100", "cached", "0s");
                Log.Print($"[DL] DownloadAudioWithProgress: cached {System.IO.Path.GetFileName(path)}");
                return path;
            }

            string ytDlp = FindYtDlp();
            var stderrBuf = new System.Text.StringBuilder();

            var psi = new ProcessStartInfo(ytDlp,
                $"-f bestaudio -o \"{path}\" --newline --no-playlist {url}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (s, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                var match = Regex.Match(e.Data, @"\[download\]\s+(?<pct>[\d\.]+)% of.*?at\s+(?<spd>.*?)\s+ETA\s+(?<eta>.*)");
                if (match.Success)
                {
                    onProgress?.Invoke(match.Groups["pct"].Value, match.Groups["spd"].Value, match.Groups["eta"].Value);
                }
            };
            proc.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    stderrBuf.AppendLine(e.Data);
            };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"yt-dlp audio download failed (exit {proc.ExitCode}): {stderrBuf.ToString().Trim()}");

            Log.Print($"[DL] DownloadAudioWithProgress done: {System.IO.Path.GetFileName(outputPath)} in {sw.Elapsed.TotalSeconds:F1}s");
            return path;
        }
        catch (Exception e)
        {
            Log.Error($"[DL] DownloadAudioWithProgress failed: {e.Message}");
            throw;
        }
    }

    private static readonly string[] FormatFallbacks =
    {
        "bestvideo[height<=720][ext=mp4]+bestaudio[ext=m4a]/best[height<=720]",
        "bestvideo[height<=720]+bestaudio/best[height<=720]",
        "best[height<=720]",
        "best",
    };

    public string DownloadSection(string url, double start, double duration, string outputPath)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Log.Print($"[DL] DownloadSection start: {url} [{FormatTime(start)}-{FormatTime(start+duration)}]");
        string ytDlp = FindYtDlp();
        string fmtStart = FormatTime(start);
        string fmtEnd = FormatTime(start + duration);

        var errors = new System.Collections.Generic.List<string>();
        foreach (var fmt in FormatFallbacks)
        {
            var psi = new ProcessStartInfo(ytDlp,
                $"--download-sections \"*{fmtStart}-{fmtEnd}\" " +
                $"-f \"{fmt}\" " +
                $"-o \"{outputPath}\" --no-playlist --force-keyframes-at-cuts {url}")
            {
                RedirectStandardOutput = false,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null) throw new InvalidOperationException("Failed to start yt-dlp");
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode == 0)
            {
                Log.Print($"[DL] DownloadSection done: {System.IO.Path.GetFileName(outputPath)} in {sw.Elapsed.TotalSeconds:F1}s");
                return outputPath;
            }
            errors.Add($"format '{fmt}' failed: {stderr.Trim()}");
        }
        throw new InvalidOperationException($"yt-dlp section download failed: {string.Join("; ", errors)}");
    }

    public delegate void DownloadProgressCallback(string percent, string speed, string eta);

    public async Task<string> DownloadSectionWithProgressAsync(string url, double start, double duration, string outputPath, DownloadProgressCallback? onProgress)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Log.Print($"[DL] DownloadSectionWithProgress start: {url} [{FormatTime(start)}-{FormatTime(start+duration)}]");
        string ytDlp = FindYtDlp();
        string fmtStart = FormatTime(start);
        string fmtEnd = FormatTime(start + duration);
        GD.Print($"[StreamManager] DownloadSection start: {url} [{fmtStart}-{fmtEnd}] -> {outputPath}");

        var errors = new System.Collections.Generic.List<string>();
        foreach (var fmt in FormatFallbacks)
        {
            var stderrBuf = new System.Text.StringBuilder();
            var psi = new ProcessStartInfo(ytDlp,
                $"--download-sections \"*{fmtStart}-{fmtEnd}\" " +
                $"-f \"{fmt}\" " +
                $"-o \"{outputPath}\" --newline --force-keyframes-at-cuts {url}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (s, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                var match = Regex.Match(e.Data, @"\[download\]\s+(?<pct>[\d\.]+)% of.*?at\s+(?<spd>.*?)\s+ETA\s+(?<eta>.*)");
                if (match.Success)
                {
                    onProgress?.Invoke(match.Groups["pct"].Value, match.Groups["spd"].Value, match.Groups["eta"].Value);
                }
            };
            proc.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    stderrBuf.AppendLine(e.Data);
            };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync().ConfigureAwait(false);
            if (proc.ExitCode == 0)
            {
                Log.Print($"[DL] DownloadSectionWithProgress done: {System.IO.Path.GetFileName(outputPath)} in {sw.Elapsed.TotalSeconds:F1}s");
                return outputPath;
            }
            errors.Add($"format '{fmt}' failed: {stderrBuf.ToString().Trim()}");
        }
        throw new InvalidOperationException($"yt-dlp section download failed: {string.Join("; ", errors)}");
    }

    public static string FormatTime(double t)
    {
        int h = (int)(t / 3600);
        int m = (int)((t % 3600) / 60);
        int s = (int)(t % 60);
        return $"{h:D2}:{m:D2}:{s:D2}";
    }
}
