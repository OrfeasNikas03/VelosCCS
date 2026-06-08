// Sound effect downloader and cache manager. Fetches MP3 files from remote
// URLs (myinstants), caches to user://sfx/, provides preview playback via
// AudioStreamPlayer and install check.

using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace VelosCCS;

public partial class SFXManager : Node
{
    private const string SFXDir = "user://sfx/";
    private readonly System.Net.Http.HttpClient _httpClient = new();

    public readonly Dictionary<string, string> AvailableSFX = new()
    {
        { "Vine Thud", "https://www.myinstants.com/media/sounds/vine-boom.mp3" },
        { "Bruh", "https://www.myinstants.com/media/sounds/movie_1.mp3" },
        { "Air Horn", "https://www.myinstants.com/media/sounds/air-horn-club-sample_1.mp3" },
        { "Discord Join", "https://www.myinstants.com/media/sounds/discord-join.mp3" },
        { "Keyboard Typing", "https://www.myinstants.com/media/sounds/mechanical-keyboard-typing-sound-effect.mp3" },
        { "Success Bell", "https://www.myinstants.com/media/sounds/ding-sound-effect_2.mp3" },
    };

    public override void _Ready()
    {
        if (!DirAccess.DirExistsAbsolute(SFXDir))
            DirAccess.MakeDirRecursiveAbsolute(SFXDir);
    }

    public async Task<string?> DownloadSFX(string name)
    {
        if (!AvailableSFX.TryGetValue(name, out var url)) return null;

        string localPath = $"{SFXDir}{name.Replace(" ", "_").ToLower()}.mp3";
        if (FileAccess.FileExists(localPath)) return localPath;

        try
        {
            byte[] data = await _httpClient.GetByteArrayAsync(url);
            using var file = FileAccess.Open(localPath, FileAccess.ModeFlags.Write);
            file.StoreBuffer(data);
            return localPath;
        }
        catch (Exception e)
        {
            GD.PrintErr($"[SFXManager] Download failed: {e.Message}");
            return null;
        }
    }

    public bool IsInstalled(string name) =>
        FileAccess.FileExists($"{SFXDir}{name.Replace(" ", "_").ToLower()}.mp3");
}
