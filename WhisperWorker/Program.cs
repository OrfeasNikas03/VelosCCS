using System.Runtime.InteropServices;
using System.Text.Json;
using Whisper.net;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: WhisperWorker <model.bin> <audio.wav> [threads]");
    Environment.Exit(1);
}

string modelPath = args[0];
string wavPath = args[1];
int threads = args.Length > 2 ? int.Parse(args[2]) : 4;

if (!File.Exists(modelPath))
{
    Console.Error.WriteLine($"Model not found: {modelPath}");
    Environment.Exit(1);
}
if (!File.Exists(wavPath))
{
    Console.Error.WriteLine($"Audio not found: {wavPath}");
    Environment.Exit(1);
}

// ── Diagnostics ──
string baseDir = AppDomain.CurrentDomain.BaseDirectory;
string whisperRuntime = Environment.GetEnvironmentVariable("WHISPER_RUNTIME") ?? "(not set)";

bool isWindows = OperatingSystem.IsWindows();
string rid = isWindows ? "win-x64" : "linux-x64";
string libExt = isWindows ? ".dll" : ".so";
string libName = isWindows ? "ggml-vulkan-whisper.dll" : "libwhisper.so";
string vulkanLib = Path.Combine(baseDir, "runtimes", "vulkan", rid, libName);
string cpuLib = Path.Combine(baseDir, "runtimes", rid, libName);

Console.Error.WriteLine($"[diag] Basedir={baseDir}");
Console.Error.WriteLine($"[diag] VULKAN lib exists: {File.Exists(vulkanLib)}  ({vulkanLib})");
Console.Error.WriteLine($"[diag] CPU  lib exists: {File.Exists(cpuLib)}  ({cpuLib})");

// Probe which runtime library dlopen/LoadLibrary would resolve
string probeName = isWindows ? "ggml-vulkan-whisper.dll" : "libwhisper.so";
try
{
    IntPtr handle = NativeLibrary.Load(probeName);
    NativeLibrary.Free(handle);
    Console.Error.WriteLine($"[diag] Load({probeName}) via system search: SUCCESS");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[diag] Load({probeName}) via system search: FAILED — {ex.GetType().Name}");
}

// Try probing the Vulkan path directly (absolute path)
try
{
    IntPtr handle = NativeLibrary.Load(vulkanLib);
    NativeLibrary.Free(handle);
    Console.Error.WriteLine($"[diag] Load(Vulkan path) SUCCESS — {vulkanLib}");
}
catch
{
    Console.Error.WriteLine($"[diag] Load(Vulkan path) FAILED — {vulkanLib}");
}

// Try probing CPU path directly
try
{
    IntPtr handle = NativeLibrary.Load(cpuLib);
    NativeLibrary.Free(handle);
    Console.Error.WriteLine($"[diag] Load(CPU path) SUCCESS — {cpuLib}");
}
catch
{
    Console.Error.WriteLine($"[diag] Load(CPU path) FAILED — {cpuLib}");
}

// Keep whatever the parent process set (cpu for stability, cuda if explicitly opted in)
Console.Error.WriteLine($"[diag] WHISPER_RUNTIME={whisperRuntime} (using: {whisperRuntime})");

var sw = System.Diagnostics.Stopwatch.StartNew();

try
{
    using var factory = WhisperFactory.FromPath(modelPath);
    Console.Error.WriteLine($"[diag] WhisperFactory created in {sw.Elapsed.TotalSeconds:F1}s");

    string whisperLanguage = Environment.GetEnvironmentVariable("WHISPER_LANGUAGE") ?? "en";
    using var processor = factory.CreateBuilder()
        .WithLanguage(whisperLanguage)
        .WithThreads(threads)
        .Build();

    byte[] wavBytes = File.ReadAllBytes(wavPath);
    using var stream = new MemoryStream(wavBytes);

    var segments = new List<Dictionary<string, object>>();
    int segCount = 0;
    await foreach (var result in processor.ProcessAsync(stream))
    {
        if (string.IsNullOrWhiteSpace(result.Text)) continue;
        segCount++;
        segments.Add(new Dictionary<string, object>
        {
            ["start"] = result.Start.TotalSeconds,
            ["end"] = result.End.TotalSeconds,
            ["text"] = result.Text.Trim(),
        });
    }

    sw.Stop();
    Console.Error.WriteLine($"[diag] Transcribed {segCount} segments in {sw.Elapsed.TotalSeconds:F1}s total");

    var output = new Dictionary<string, object> { ["segments"] = segments };
    Console.WriteLine(JsonSerializer.Serialize(output));
    Environment.Exit(0);
}
catch (Exception ex)
{
    sw.Stop();
    Console.Error.WriteLine($"[diag] FAILED after {sw.Elapsed.TotalSeconds:F1}s: {ex}");
    Environment.Exit(1);
}
