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
		return new StreamInfo
		{
			Url = url,
			Title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "Untitled" : "Untitled",
			Duration = root.TryGetProperty("duration", out var d) ? d.GetDouble() : 0,
			WebpageUrl = root.TryGetProperty("webpage_url", out var w) ? w.GetString() ?? url : url,
			Uploader = root.TryGetProperty("uploader", out var u) ? u.GetString() ?? "" : "",
			Thumbnail = root.TryGetProperty("thumbnail", out var th) ? th.GetString() ?? "" : "",
		};
    }

    public string DownloadAudio(string url, string outputPath)
    {
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

        return path;
    }

    public string DownloadAudioWithProgress(string url, string outputPath, DownloadProgressCallback? onProgress)
    {
        string ext = "opus";
        string path = Path.ChangeExtension(outputPath, ext);
        if (File.Exists(path))
        {
            onProgress?.Invoke("100", "cached", "0s");
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

        return path;
    }

    public string DownloadSection(string url, double start, double duration, string outputPath)
    {
        string ytDlp = FindYtDlp();
        string fmtStart = FormatTime(start);
        string fmtEnd = FormatTime(start + duration);

        var psi = new ProcessStartInfo(ytDlp,
            $"--download-sections \"*{fmtStart}-{fmtEnd}\" " +
            $"-f \"bestvideo[height<=720][ext=mp4]+bestaudio[ext=m4a]/best[height<=720]\" " +
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
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"yt-dlp section download failed (exit {proc.ExitCode}): {stderr.Trim()}");

        return outputPath;
    }

    public delegate void DownloadProgressCallback(string percent, string speed, string eta);

    public async Task<string> DownloadSectionWithProgressAsync(string url, double start, double duration, string outputPath, DownloadProgressCallback? onProgress)
    {
        string ytDlp = FindYtDlp();
        string fmtStart = FormatTime(start);
        string fmtEnd = FormatTime(start + duration);
        var stderrBuf = new System.Text.StringBuilder();
        GD.Print($"[StreamManager] DownloadSection start: {url} [{fmtStart}-{fmtEnd}] -> {outputPath}");

        var psi = new ProcessStartInfo(ytDlp,
            $"--download-sections \"*{fmtStart}-{fmtEnd}\" " +
            $"-f \"bestvideo[height<=720][ext=mp4]+bestaudio[ext=m4a]/best[height<=720]\" " +
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
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"yt-dlp section download failed (exit {proc.ExitCode}): {stderrBuf.ToString().Trim()}");

        return outputPath;
    }

    public static string FormatTime(double t)
    {
        int h = (int)(t / 3600);
        int m = (int)((t % 3600) / 60);
        int s = (int)(t % 60);
        return $"{h:D2}:{m:D2}:{s:D2}";
    }
}
