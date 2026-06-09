using Godot;
using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using HttpClient = System.Net.Http.HttpClient;

namespace VelosCCS;

public record UpdateInfo(string LatestVersion, string DownloadUrl, string Changelog, string PublishedAt);

public static class UpdateChecker
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
    };

    public static async Task<UpdateInfo?> CheckAsync(string currentVersion)
    {
        Log.Print("[Update] CheckForUpdates started");
        if (string.IsNullOrEmpty(AppConfig.UpdateRepoUrl))
        {
            Log.Warn("[Update] UpdateRepoUrl is not set — skipping check");
            return null;
        }

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, AppConfig.UpdateRepoUrl);
            request.Headers.UserAgent.ParseAdd("VelosCCS");
            if (!string.IsNullOrEmpty(AppConfig.UpdateRepoToken))
                request.Headers.Add("Authorization", $"Bearer {AppConfig.UpdateRepoToken}");

            using var response = await _http.SendAsync(request);
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tagName = root.GetProperty("tag_name").GetString() ?? "";
            string versionStr = tagName.StartsWith("v") ? tagName[1..] : tagName;
            string changelog = root.GetProperty("body").GetString() ?? "";
            string publishedAt = root.GetProperty("published_at").GetString() ?? "";

            string? downloadUrl = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string name = asset.GetProperty("name").GetString() ?? "";
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        long assetId = asset.GetProperty("id").GetInt64();
                        string baseApiUrl = AppConfig.UpdateRepoUrl.Substring(0, AppConfig.UpdateRepoUrl.LastIndexOf("/releases/", StringComparison.Ordinal));
                        downloadUrl = $"{baseApiUrl}/assets/{assetId}";
                        break;
                    }
                }
            }

            if (downloadUrl == null)
            {
                Log.Warn("[Update] No installer asset found in release");
                return null;
            }

            if (!Version.TryParse(versionStr, out var latest) ||
                !Version.TryParse(currentVersion, out var current))
            {
                Log.Warn("[Update] Could not parse versions");
                return null;
            }

            if (latest <= current)
            {
                Log.Print($"[Update] Current v{currentVersion} is up to date (latest: {versionStr})");
                return null;
            }

            if (versionStr == AppConfig.SkipUpdateVersion)
            {
                Log.Print($"[Update] v{versionStr} was skipped by user");
                return null;
            }

            return new UpdateInfo(versionStr, downloadUrl, changelog, publishedAt);
        }
        catch (Exception e)
        {
            Log.Error($"[Update] Check failed: {e.Message}");
            return null;
        }
        finally
        {
            Log.Print("[Update] CheckForUpdates completed");
        }
    }

    public static async Task<string> DownloadInstallerAsync(string url, string destPath, IProgress<double>? progress = null)
    {
        Log.Print("[Update] DownloadUpdate started");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("VelosCCS");
        request.Headers.Accept.ParseAdd("application/octet-stream");
        if (!string.IsNullOrEmpty(AppConfig.UpdateRepoToken))
            request.Headers.Add("Authorization", $"Bearer {AppConfig.UpdateRepoToken}");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        long totalBytes = response.Content.Headers.ContentLength ?? -1;
        await using var contentStream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = new FileStream(destPath, FileMode.Create, System.IO.FileAccess.Write, FileShare.None, 8192, true);

        var buffer = new byte[81920];
        long bytesRead = 0;
        int bytesJustRead;

        while ((bytesJustRead = await contentStream.ReadAsync(buffer)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesJustRead));
            bytesRead += bytesJustRead;
            if (totalBytes > 0)
                progress?.Report((double)bytesRead / totalBytes * 100.0);
        }

        Log.Print("[Update] DownloadUpdate completed");
        return destPath;
    }

    public static void ApplyUpdate(string installerPath)
    {
        Log.Print("[Update] LaunchInstaller started");
        if (OS.GetName() != "Windows")
        {
            Log.Print("[Update] Auto-update only supported on Windows");
            return;
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                UseShellExecute = true,
            };
            System.Diagnostics.Process.Start(psi);
            GetTree().Quit();
        }
        catch (Exception e)
        {
            Log.Error($"[Update] Failed to launch installer: {e.Message}");
        }
    }

    public static bool ShouldCheck()
    {
        if (AppConfig.LastUpdateCheck == null)
            return true;
        return (DateTime.UtcNow - AppConfig.LastUpdateCheck.Value).TotalHours > 24;
    }

    private static SceneTree GetTree()
    {
        return (SceneTree)Engine.GetMainLoop();
    }
}
