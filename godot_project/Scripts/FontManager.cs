// Google Font downloader and cache manager. Downloads TTF from GitHub raw
// URLs, caches to font_cache/ and user://fonts/, provides LoadDynamicFont
// and path resolution for use in clip text rendering.

using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace VelosCCS;

public partial class FontManager : Node
{
    private const string UserFontDir = "user://fonts/";
    private const string CacheDir = "res://font_cache/";
    private readonly System.Net.Http.HttpClient _httpClient = new();

    public Dictionary<string, string> AvailableFonts { get; } = new()
    {
        { "Bangers", "https://github.com/google/fonts/raw/main/ofl/bangers/Bangers-Regular.ttf" },
        { "Luckiest Guy", "https://github.com/google/fonts/raw/main/ofl/luckiestguy/LuckiestGuy-Regular.ttf" },
        { "Anton", "https://github.com/google/fonts/raw/main/ofl/anton/Anton-Regular.ttf" },
        { "Montserrat Bold", "https://github.com/google/fonts/raw/main/ofl/montserrat/Montserrat%5Bwght%5D.ttf" },
        { "Permanent Marker", "https://github.com/google/fonts/raw/main/ofl/permanentmarker/PermanentMarker-Regular.ttf" },
        { "Oswald", "https://github.com/google/fonts/raw/main/ofl/oswald/Oswald%5Bwght%5D.ttf" },
    };

    public override void _Ready()
    {
        Log.Print("[Font] _Ready");
        string userGlobal = ProjectSettings.GlobalizePath(UserFontDir);
        string cacheGlobal = ProjectSettings.GlobalizePath(CacheDir);
        System.IO.Directory.CreateDirectory(userGlobal);
        System.IO.Directory.CreateDirectory(cacheGlobal);
        SyncCache();
    }

    private void SyncCache()
    {
        Log.Print("[Font] SyncCache started");
        string userDir = ProjectSettings.GlobalizePath(UserFontDir);
        string cacheDir = ProjectSettings.GlobalizePath(CacheDir);
        if (!System.IO.Directory.Exists(userDir))
        {
            Log.Print("[Font] SyncCache finished — no user dir");
            return;
        }

        foreach (string f in System.IO.Directory.GetFiles(userDir, "*.ttf"))
        {
            string name = System.IO.Path.GetFileName(f);
            string dest = System.IO.Path.Combine(cacheDir, name);
            if (!System.IO.File.Exists(dest))
            {
                System.IO.File.Copy(f, dest, false);
                Log.Print($"[Font] SyncCache: copied {name}");
            }
        }
        foreach (string f in System.IO.Directory.GetFiles(userDir, "*.otf"))
        {
            string name = System.IO.Path.GetFileName(f);
            string dest = System.IO.Path.Combine(cacheDir, name);
            if (!System.IO.File.Exists(dest))
            {
                System.IO.File.Copy(f, dest, false);
                Log.Print($"[Font] SyncCache: copied {name}");
            }
        }
        Log.Print("[Font] SyncCache finished");
    }

    private static string SafeName(string name) =>
        name.Replace(" ", "").Replace("-", "");

    private static string UserFilePath(string displayName) =>
        System.IO.Path.Combine(
            ProjectSettings.GlobalizePath(UserFontDir),
            SafeName(displayName) + ".ttf");

    private static string CacheFilePath(string displayName) =>
        System.IO.Path.Combine(
            ProjectSettings.GlobalizePath(CacheDir),
            SafeName(displayName) + ".ttf");

    public async Task<string?> DownloadFont(string family)
    {
        Log.Print($"[Font] DownloadFont entry: {family}");
        if (!AvailableFonts.TryGetValue(family, out var url))
        {
            Log.Error($"[Font] DownloadFont: {family} not in available fonts");
            return null;
        }

        string userPath = UserFilePath(family);
        string cachePath = CacheFilePath(family);

        if (System.IO.File.Exists(cachePath))
        {
            Log.Print($"[Font] DownloadFont: {family} already cached at {cachePath}");
            return cachePath;
        }

        try
        {
            var data = await _httpClient.GetByteArrayAsync(url);
            if (data == null || data.Length == 0)
            {
                Log.Error($"[Font] DownloadFont: empty data for {family}");
                return null;
            }

            System.IO.File.WriteAllBytes(userPath, data);
            System.IO.File.Copy(userPath, cachePath, true);

            Log.Print($"[Font] DownloadFont: {family} downloaded to {cachePath}");
            return cachePath;
        }
        catch (Exception e)
        {
            Log.Error($"[Font] DownloadFont failed for {family}: {e.Message}");
            return null;
        }
    }

    public FontFile? LoadFont(string displayName)
    {
        Log.Print($"[Font] LoadFont entry: {displayName}");
        string path = CacheFilePath(displayName);
        if (!System.IO.File.Exists(path))
        {
            Log.Error($"[Font] LoadFont: {displayName} not found at {path}");
            return null;
        }
        try
        {
            var font = new FontFile();
            font.LoadDynamicFont(path);
            Log.Print($"[Font] LoadFont: {displayName} loaded from {path}");
            return font;
        }
        catch (Exception e)
        {
            Log.Error($"[Font] LoadFont failed for {displayName}: {e.Message}");
            return null;
        }
    }

    public FontFile? LoadFontFromPath(string fontPath)
    {
        string globalPath = fontPath;
        if (globalPath.StartsWith("res://"))
            globalPath = ProjectSettings.GlobalizePath(globalPath);
        if (!System.IO.File.Exists(globalPath)) return null;
        try
        {
            var font = new FontFile();
            font.LoadDynamicFont(globalPath);
            return font;
        }
        catch (Exception e)
        {
            GD.PrintErr($"[FontManager] Load failed from {fontPath}: {e.Message}");
            return null;
        }
    }

    public bool IsFontInstalled(string displayName) =>
        System.IO.File.Exists(CacheFilePath(displayName));

    public string GetFontPath(string displayName) =>
        CacheFilePath(displayName);

    public List<(string Name, string LocalPath)> GetInstalledFonts()
    {
        var list = new List<(string, string)>();
        string cacheDir = ProjectSettings.GlobalizePath(CacheDir);
        if (!System.IO.Directory.Exists(cacheDir)) return list;

        foreach (string f in System.IO.Directory.GetFiles(cacheDir, "*.ttf"))
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(f);
            list.Add((name, f));
        }
        return list;
    }
}
