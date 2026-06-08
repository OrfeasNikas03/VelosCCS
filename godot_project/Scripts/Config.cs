using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace VelosCCS;

public static class AppConfig
{
    public const string AppName = "Velos Content Creation Suite";
    public const string AppVersion = "4.0.5";

    public static string WhisperModel => System.Environment.GetEnvironmentVariable("CLIPTOOL_WHISPER_MODEL") ?? "base";
    public static string WhisperDevice => System.Environment.GetEnvironmentVariable("CLIPTOOL_WHISPER_DEVICE") ?? "cpu";
    public static string WhisperCompute => System.Environment.GetEnvironmentVariable("CLIPTOOL_WHISPER_COMPUTE") ?? "int8";
    public static int WhisperThreads => int.TryParse(System.Environment.GetEnvironmentVariable("CLIPTOOL_WHISPER_THREADS"), out var t) ? t : 4;
    public static string OutputDir => Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "VelosCCS", "exports");

    public static string TempDir => Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "VelosCCS", "temp");
    public static string ConfigDir => Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".config", "velosccs");
    private static string ConfigPath => Path.Combine(ConfigDir, "settings.json");
    public static string LogPath => Path.Combine(ConfigDir, "session.log");

    public const int OutputWidth = 1080;
    public const int OutputHeight = 1920;

    public static readonly Dictionary<string, (int w, int h)> AspectRatios = new()
    {
        ["9:16"] = (1080, 1920),
        ["16:9"] = (1920, 1080),
        ["1:1"] = (1080, 1080),
        ["4:5"] = (1080, 1350),
        ["2:3"] = (1080, 1620),
    };

    public const string DefaultAspect = "9:16";

    public const string UpdateRepoUrl = "https://api.github.com/repos/OrfeasNikas03/VelosCCS/releases/latest";  // SET THIS: "https://api.github.com/repos/OrfeasNikas03/VelosCCS/releases/latest"
    public static string UpdateRepoToken => System.Environment.GetEnvironmentVariable("VELOSCCS_UPDATE_TOKEN") ?? UpdateRepoTokenFallback;
    public const string UpdateRepoTokenFallback = "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";  // SET THIS to a GitHub PAT (Contents:read) before release builds
    public static DateTime? LastUpdateCheck { get; set; } = null;
    public static string LastUpdateVersion { get; set; } = "";
    public static string SkipUpdateVersion { get; set; } = "";

    public static string CaptionLanguage { get; set; } = "en";
    public static string ExportOutputDir { get; set; } = "";
    public static string ClipOutputDir { get; set; } = "";

    public static void LoadSettings()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            if (!File.Exists(ConfigPath)) return;
            string json = File.ReadAllText(ConfigPath);
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (data == null) return;
            if (data.TryGetValue("caption_language", out var lang)) CaptionLanguage = lang;
            if (data.TryGetValue("export_output_dir", out var dir)) ExportOutputDir = dir;
            if (data.TryGetValue("clip_output_dir", out var clipDir)) ClipOutputDir = clipDir;
            if (data.TryGetValue("last_update_check", out var lc) && DateTime.TryParse(lc, out var dt)) LastUpdateCheck = dt;
            if (data.TryGetValue("last_update_version", out var uv)) LastUpdateVersion = uv;
            if (data.TryGetValue("skip_update_version", out var sv)) SkipUpdateVersion = sv;
        }
        catch (Exception e)
        {
            GD.PrintErr($"[AppConfig] Failed to load settings: {e.Message}");
        }
    }

    public static void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var data = new Dictionary<string, string>
            {
                ["caption_language"] = CaptionLanguage,
                ["export_output_dir"] = ExportOutputDir,
                ["clip_output_dir"] = ClipOutputDir,
                ["last_update_check"] = LastUpdateCheck?.ToString("o") ?? "",
                ["last_update_version"] = LastUpdateVersion,
                ["skip_update_version"] = SkipUpdateVersion,
            };
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(data));
        }
        catch (Exception e)
        {
            GD.PrintErr($"[AppConfig] Failed to save settings: {e.Message}");
        }
    }
}
