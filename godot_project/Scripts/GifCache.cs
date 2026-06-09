using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Godot;

namespace VelosCCS;

public static class GifCache
{
    private static readonly Dictionary<string, GifFrameData> _cache = new();

    private struct CacheEntry
    {
        public GifFrameData Data;
        public DateTime LastWriteTime;
    }

    private static readonly Dictionary<string, CacheEntry> _cacheWithTime = new();

    public static GifFrameData? GetOrCreate(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return null;

        var lastWrite = File.GetLastWriteTimeUtc(filePath);

        if (_cacheWithTime.TryGetValue(filePath, out var entry))
        {
            if (entry.LastWriteTime == lastWrite)
                return entry.Data;
        }

        var data = ExtractFrames(filePath);
        if (data != null)
        {
            _cacheWithTime[filePath] = new CacheEntry { Data = data, LastWriteTime = lastWrite };
        }
        return data;
    }

    public static void Invalidate(string filePath)
    {
        _cacheWithTime.Remove(filePath);
    }

    public static void Clear()
    {
        _cacheWithTime.Clear();
    }

    private static GifFrameData? ExtractFrames(string filePath)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "VelosCCS_Gif_" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            string outputPattern = Path.Combine(tempDir, "frame_%04d.png");
            var psi = new ProcessStartInfo("ffmpeg", $"-i \"{filePath}\" -vsync 0 \"{outputPattern}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                Log.Error("[GifCache] Failed to start ffmpeg");
                return null;
            }
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(20000);
            if (proc.ExitCode != 0)
            {
                Log.Error($"[GifCache] ffmpeg exit {proc.ExitCode}: {stderr.Trim()}");
                return null;
            }

            float[]? delays = ParseGifDelays(filePath);

            var framePaths = new List<string>();
            for (int i = 1; ; i++)
            {
                string framePath = Path.Combine(tempDir, $"frame_{i:D4}.png");
                if (!File.Exists(framePath)) break;
                framePaths.Add(framePath);
            }

            if (framePaths.Count == 0) return null;

            int maxFrames = Math.Min(framePaths.Count, 256);
            var textures = new Texture2D[maxFrames];
            var frameDelays = new float[maxFrames];

            for (int i = 0; i < maxFrames; i++)
            {
                var img = Image.LoadFromFile(framePaths[i]);
                if (img == null || img.IsEmpty())
                {
                    Log.Warn($"[GifCache] Frame {i} failed to load from {framePaths[i]}");
                    continue;
                }
                textures[i] = ImageTexture.CreateFromImage(img);

                if (delays != null && i < delays.Length)
                    frameDelays[i] = delays[i] > 0f ? delays[i] : 0.1f;
                else
                    frameDelays[i] = 0.1f;
            }

            return new GifFrameData { Textures = textures, Delays = frameDelays };
        }
        catch (Exception ex)
        {
            Log.Error($"[GifCache] Failed to extract frames: {ex.Message}");
            return null;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); }
            catch { }
        }
    }

    private static float[]? ParseGifDelays(string filePath)
    {
        try
        {
            var bytes = File.ReadAllBytes(filePath);
            if (bytes.Length < 6) return null;

            var delays = new List<float>();
            int pos = 6;
            float defaultDelay = 0.1f;

            if (pos + 7 > bytes.Length) return null;
            bool hasGCT = (bytes[pos + 4] & 0x80) != 0;
            int gctSize = hasGCT ? 3 * (1 << ((bytes[pos + 4] & 0x07) + 1)) : 0;
            pos += 7 + gctSize;

            while (pos < bytes.Length - 1)
            {
                byte b = bytes[pos];

                if (b == 0x3B) break;

                if (b == 0x21)
                {
                    pos++;
                    if (pos >= bytes.Length) break;
                    byte label = bytes[pos];
                    pos++;
                    if (pos >= bytes.Length) break;

                    if (label == 0xF9)
                    {
                        if (pos + 5 <= bytes.Length && bytes[pos] == 4)
                        {
                            int delayCS = bytes[pos + 2] | (bytes[pos + 3] << 8);
                            defaultDelay = delayCS / 100.0f;
                            if (defaultDelay <= 0f) defaultDelay = 0.1f;
                            pos += 6;
                        }
                        else
                        {
                            while (pos < bytes.Length)
                            {
                                int subSize = bytes[pos]; pos++;
                                if (subSize == 0) break;
                                pos += subSize;
                            }
                        }
                    }
                    else
                    {
                        while (pos < bytes.Length)
                        {
                            int subSize = bytes[pos]; pos++;
                            if (subSize == 0) break;
                            pos += subSize;
                        }
                    }
                }
                else if (b == 0x2C)
                {
                    delays.Add(defaultDelay);
                    if (pos + 9 > bytes.Length) break;
                    bool hasLCT = (bytes[pos + 8] & 0x80) != 0;
                    int lctSize = hasLCT ? 3 * (1 << ((bytes[pos + 8] & 0x07) + 1)) : 0;
                    pos += 9 + lctSize;
                    if (pos >= bytes.Length) break;
                    pos++;
                    while (pos < bytes.Length)
                    {
                        int subSize = bytes[pos]; pos++;
                        if (subSize == 0) break;
                        pos += subSize;
                    }
                }
                else if (b == 0x00)
                {
                    pos++;
                }
                else
                {
                    pos++;
                }
            }

            return delays.ToArray();
        }
        catch (Exception ex)
        {
            Log.Warn($"[GifCache] Failed to parse GIF delays: {ex.Message}");
            return null;
        }
    }
}
