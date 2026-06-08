using Godot;
using System;
using System.IO;

namespace VelosCCS;

public static class Log
{
    private static bool _hooked;

    public static void HookConsole()
    {
        if (_hooked) return;
        _hooked = true;

        var writer = new LogTextWriter();
        Console.SetOut(writer);
        Console.SetError(writer);

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            Error($"Unhandled exception: {ex?.Message}\n{ex?.StackTrace}");
        };
    }

    public static void Info(string msg)
    {
        GD.Print(msg);
        LogBuffer.AddLine($"[INFO] {msg}");
        WriteConsoleDirect(msg);
    }

    public static void Error(string msg)
    {
        GD.PrintErr(msg);
        LogBuffer.AddLine($"[ERROR] {msg}");
        WriteConsoleDirect($"[ERROR] {msg}");
    }

    public static void Print(string msg)
    {
        GD.Print(msg);
        LogBuffer.AddLine($"[INFO] {msg}");
        WriteConsoleDirect(msg);
    }

    public static void Warn(string msg)
    {
        GD.Print(msg);
        LogBuffer.AddLine($"[WARN] {msg}");
        WriteConsoleDirect($"[WARN] {msg}");
    }

    private static void WriteConsoleDirect(string msg)
    {
        try { Console.WriteLine(msg); }
        catch { /* console not available */ }
    }
}

internal class LogTextWriter : StringWriter
{
    public override void WriteLine(string? value)
    {
        if (!string.IsNullOrEmpty(value))
            LogBuffer.AddLine($"[CONSOLE] {value}");
    }

    public override void Write(string? value)
    {
        if (!string.IsNullOrEmpty(value))
            LogBuffer.AddLine($"[CONSOLE] {value}");
    }
}
