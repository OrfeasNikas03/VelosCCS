using Godot;
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace VelosCCS;

public class Downloader
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

    public string Download(string url, string outputDir)
    {
        Directory.CreateDirectory(outputDir);

        string ytDlp = FindYtDlp();
        string args = $"--format \"bestvideo[height<=1080]+bestaudio/best[height<=1080]\" " +
                      $"--format-sort \"vcodec:avc1,res,codec\" " +
                      $"--merge-output-format mp4 " +
                      $"--output \"{outputDir}/%(title)s.%(ext)s\" " +
                      $"--no-quiet -- {url}";

        var psi = new ProcessStartInfo(ytDlp, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi);
        if (proc == null)
            throw new InvalidOperationException($"Failed to start {ytDlp}");

        string output = proc.StandardOutput.ReadToEnd();
        string error = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"yt-dlp failed (exit {proc.ExitCode}): {error}");

        // Find the output file(s) in the directory
        var videoExts = new[] { ".mp4", ".mkv", ".webm", ".mov" };
        foreach (var ext in videoExts)
        {
            var files = Directory.GetFiles(outputDir, $"*{ext}");
            if (files.Length > 0)
                return files[0];
        }

        throw new InvalidOperationException("yt-dlp completed but output file not found");
    }

    public StreamInfo GetInfo(string url)
    {
        string ytDlp = FindYtDlp();
        string args = $"--dump-json -- {url}";

        var psi = new ProcessStartInfo(ytDlp, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi);
        if (proc == null)
            throw new InvalidOperationException($"Failed to start {ytDlp}");

        string output = proc.StandardOutput.ReadToEnd().Trim();
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
        };
    }
}
