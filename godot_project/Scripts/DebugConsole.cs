using Godot;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace VelosCCS;

public static class DebugConsole
{
    private static bool _isOpen;

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    public static void Toggle()
    {
        if (_isOpen)
            Hide();
        else
            Show();
    }

    public static void Show()
    {
        if (_isOpen) return;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            AllocConsole();
            var writer = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(writer);
            Console.SetError(writer);
            GD.Print("[DebugConsole] Console window opened");
            Log.Print("Debug console opened");
        }
        else
        {
            GD.Print("[DebugConsole] Stdout logging active (run from terminal)");
        }
        _isOpen = true;
    }

    public static void Hide()
    {
        if (!_isOpen) return;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var hWnd = GetConsoleWindow();
            if (hWnd != IntPtr.Zero)
                ShowWindow(hWnd, SW_HIDE);
            FreeConsole();
            GD.Print("[DebugConsole] Console window closed");
        }
        _isOpen = false;
    }
}
