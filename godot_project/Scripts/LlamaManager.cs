using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace VelosCCS;

public static class LlamaManager
{
    private static string ConfigDir =>
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            ".config", "velosccs");
    private static string ConfigFile => Path.Combine(ConfigDir, "preferences.json");
    private static string ModelDir =>
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            ".cache", "velosccs", "llm");

    public static string GetModelPath(string? modelName = null)
    {
        modelName ??= GetDetectedModel();
        return Path.Combine(ModelDir, modelName);
    }

    public static string GetDetectedModel()
    {
        try
        {
            string text = File.ReadAllText(ConfigFile);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("llm_model", out var m))
                return m.GetString() ?? DefaultModel;
        }
        catch { }
        return DefaultModel;
    }

    public static void SetDetectedModel(string model)
    {
        Dictionary<string, object> prefs = new();
        try
        {
            string text = File.ReadAllText(ConfigFile);
            prefs = JsonSerializer.Deserialize<Dictionary<string, object>>(text) ?? new();
        }
        catch { }
        prefs["llm_model"] = model;
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigFile, JsonSerializer.Serialize(prefs, new JsonSerializerOptions { WriteIndented = true }));
    }

    private const long MinModelBytes = 50 * 1024 * 1024; // 50 MB — GGUF models are always >= this

    public static bool IsModelDownloaded(string? modelName = null)
    {
        string path = GetModelPath(modelName);
        return File.Exists(path) && new FileInfo(path).Length >= MinModelBytes;
    }

    public static async Task<bool> EnsureModelDownloadedAsync(string? modelName = null, Action<string>? progressCallback = null)
    {
        string name = modelName ?? GetDetectedModel();
        string modelFile = GetModelPath(name);
        if (IsModelDownloaded(name))
            return true;

        Directory.CreateDirectory(ModelDir);
        // Remove any partial download
        try { if (File.Exists(modelFile)) File.Delete(modelFile); } catch { }
        string url = ModelUrl(name);
        progressCallback?.Invoke($"Downloading {name}...");
        GD.Print($"[LlamaManager] downloading {name} from {url}");

        try
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            using var response = await client.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            long totalBytes = response.Content.Headers.ContentLength ?? -1;
            using var fs = File.Create(modelFile);
            using var stream = await response.Content.ReadAsStreamAsync();
            byte[] buf = new byte[81920];
            long bytesRead = 0;
            int read;
            while ((read = await stream.ReadAsync(buf)) > 0)
            {
                await fs.WriteAsync(buf, 0, read);
                bytesRead += read;
                if (totalBytes > 0)
                {
                    int pct = (int)(bytesRead * 100 / totalBytes);
                    progressCallback?.Invoke($"Downloading {name}... {pct}%");
                }
            }
            GD.Print($"[LlamaManager] {name} downloaded ({bytesRead / 1048576} MB at {modelFile})");
            progressCallback?.Invoke($"{name} downloaded");
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[LlamaManager] failed to download {name}: {ex.Message}");
            progressCallback?.Invoke($"Download failed: {ex.Message}");
            try { File.Delete(modelFile); } catch { }
            return false;
        }
    }

    private static string ModelUrl(string name)
    {
        var urls = new Dictionary<string, string>
        {
            ["llama-3.2-3b-instruct.Q4_K_M.gguf"] =
                "https://huggingface.co/bartowski/Llama-3.2-3B-Instruct-GGUF/resolve/main/Llama-3.2-3B-Instruct-Q4_K_M.gguf",
            ["llama-3.2-3b-instruct.Q8_0.gguf"] =
                "https://huggingface.co/bartowski/Llama-3.2-3B-Instruct-GGUF/resolve/main/Llama-3.2-3B-Instruct-Q8_0.gguf",
            ["Phi-3.1-mini-4k-instruct-Q4_K_M.gguf"] =
                "https://huggingface.co/bartowski/Phi-3.1-mini-4k-instruct-GGUF/resolve/main/Phi-3.1-mini-4k-instruct-Q4_K_M.gguf",
            ["Llama-3.2-1B-Instruct-Q4_K_M.gguf"] =
                "https://huggingface.co/bartowski/Llama-3.2-1B-Instruct-GGUF/resolve/main/Llama-3.2-1B-Instruct-Q4_K_M.gguf",
            ["tinyllama-1.1b-chat-v1.0.Q4_K_M.gguf"] =
                "https://huggingface.co/TheBloke/TinyLlama-1.1B-Chat-v1.0-GGUF/resolve/main/tinyllama-1.1b-chat-v1.0.Q4_K_M.gguf",
        };
        return urls.TryGetValue(name, out var u) ? u : urls[DefaultModel];
    }

    public const string DefaultModel = "llama-3.2-3b-instruct.Q4_K_M.gguf";

    public static readonly (string name, string desc, string ram, string vram)[] ModelOptions =
    {
        ("llama-3.2-3b-instruct.Q4_K_M.gguf", "Default – good quality, fits 4GB+ GPU (~2GB)",  "~2 GB RAM", "~2 GB VRAM"),
        ("llama-3.2-3b-instruct.Q8_0.gguf",   "Higher quality – needs 6GB+ GPU (~3.3GB)",      "~3.3 GB RAM", "~3.3 GB VRAM"),
        ("Phi-3.1-mini-4k-instruct-Q4_K_M.gguf", "Tiny – fits any GPU, fast (~1.5GB)",          "~1.5 GB RAM", "~1.5 GB VRAM"),
        ("Llama-3.2-1B-Instruct-Q4_K_M.gguf", "Smallest – runs anywhere (~0.7GB)",              "~0.7 GB RAM", "~0.7 GB VRAM"),
        ("tinyllama-1.1b-chat-v1.0.Q4_K_M.gguf", "Ultra-light – fast on low-end GPU (~0.7GB)", "~0.7 GB RAM", "~0.7 GB VRAM"),
    };

    private static string? _cliPath;

	public static string FindCliBinary()
	{
		if (_cliPath != null) return _cliPath;

		string baseDir = AppDomain.CurrentDomain.BaseDirectory;
		string home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);

		string cli = OperatingSystem.IsWindows() ? "llama-cli.exe" : "llama-cli";

		// Candidate 0: project root via res:// (works in editor)
		string c0 = ProjectSettings.GlobalizePath("res://LlamaWorker_published/" + cli);
		if (File.Exists(c0)) { _cliPath = c0; GD.Print($"[LlamaManager] Found {c0}"); return _cliPath; }

		// Candidate 1: alongside the executable (used by exported/packaged app)
		string c1 = Path.Combine(baseDir, "LlamaWorker_published", cli);
        if (File.Exists(c1)) { _cliPath = c1; GD.Print($"[LlamaManager] Found {c1}"); return _cliPath; }

        // Candidate 2: one level up from baseDir/LlamaWorker_published (when running from data_* subdir)
        string c2 = Path.Combine(baseDir, "..", "LlamaWorker_published", cli);
        if (File.Exists(c2)) { _cliPath = c2; GD.Print($"[LlamaManager] Found {c2}"); return _cliPath; }

        // Candidate 3: Linux user install path
        if (!OperatingSystem.IsWindows())
        {
            string c3 = Path.Combine(home, ".local", "share", "velosccs", "LlamaWorker_published", cli);
            if (File.Exists(c3)) { _cliPath = c3; GD.Print($"[LlamaManager] Found {c3}"); return _cliPath; }
        }

        _cliPath = "";
        GD.Print("[LlamaManager] llama-cli not found (searched: " + string.Join(", ", c1, c2) + ")");
        return "";
    }
}
