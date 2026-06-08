using Godot;
using System;
using System.Collections.Generic;
using System.IO;

namespace VelosCCS;

public static class LogBuffer
{
    private static readonly List<string> _logs = new();
    private static readonly int _maxLines = 500;
    private static StreamWriter? _fileWriter;
    private static bool _initialized;

    public static event Action<string>? OnLog;

    public static void Init()
    {
        if (_initialized) return;
        _initialized = true;

        // Rotate previous session log
        try
        {
            var logPath = AppConfig.LogPath;
            var dir = Path.GetDirectoryName(logPath);
            if (dir != null) Directory.CreateDirectory(dir);
            if (File.Exists(logPath))
            {
                var bak = logPath + ".bak";
                if (File.Exists(bak)) File.Delete(bak);
                File.Move(logPath, bak);
            }
            _fileWriter = new StreamWriter(logPath, append: false) { AutoFlush = true };
        }
        catch { }

        // Load previous session log from .bak
        try
        {
            var bakPath = AppConfig.LogPath + ".bak";
            if (File.Exists(bakPath))
            {
                foreach (var line in File.ReadAllLines(bakPath))
                    AddLine(line);
            }
        }
        catch { }
    }

    public static void AddLine(string line)
    {
        lock (_logs)
        {
            _logs.Add(line);
            if (_logs.Count > _maxLines)
                _logs.RemoveAt(0);
        }
        try
        {
            _fileWriter?.WriteLine(line);
        }
        catch { }
        OnLog?.Invoke(line);
    }

    public static int LineCount { get { lock (_logs) return _logs.Count; } }

    public static IReadOnlyList<string> GetLogs()
    {
        lock (_logs)
            return _logs.ToArray();
    }

    public static void Shutdown()
    {
        try
        {
            _fileWriter?.Dispose();
            _fileWriter = null;
        }
        catch { }
    }
}
