using Godot;
using System;
using System.Text;

namespace VelosCCS;

public partial class ConsoleDialog : Window
{
    private RichTextLabel _logView = null!;
    private bool _autoScroll = true;
    private int _lastLineCount;
    private Timer _pollTimer = null!;

    public ConsoleDialog()
    {
        Title = "Debug Console";
        MinSize = new Vector2I(600, 350);
        Size = new Vector2I(700, 400);
        CloseRequested += () => Hide();
    }

    public override void _Ready()
    {
        Theme = AppTheme.Create();
        var vbox = new VBoxContainer();
        vbox.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(vbox);

        _logView = new RichTextLabel
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            ScrollFollowing = true,
            BbcodeEnabled = true,
            SelectionEnabled = true,
            ContextMenuEnabled = true,
        };
        vbox.AddChild(_logView);

        var toolbar = new HBoxContainer();
        var clearBtn = new Button { Text = "Clear" };
        clearBtn.Pressed += () => _logView.Clear();
        toolbar.AddChild(clearBtn);

        var copyBtn = new Button { Text = "Copy All" };
        copyBtn.Pressed += () =>
        {
            var sb = new StringBuilder();
            foreach (var line in LogBuffer.GetLogs())
                sb.AppendLine(line);
            DisplayServer.ClipboardSet(sb.ToString());
        };
        toolbar.AddChild(copyBtn);

        var refreshBtn = new Button { Text = "Refresh" };
        refreshBtn.Pressed += () => ReloadLogs();
        toolbar.AddChild(refreshBtn);

        var autoScrollToggle = new CheckBox { Text = "Auto-scroll", ButtonPressed = true };
        autoScrollToggle.Toggled += (on) => _autoScroll = on;
        toolbar.AddChild(autoScrollToggle);

        vbox.AddChild(toolbar);

        ReloadLogs();

        LogBuffer.OnLog += OnNewLog;
        TreeExiting += () => LogBuffer.OnLog -= OnNewLog;

        // Poll timer catches logs from GD.Print via file
        _pollTimer = new Timer { WaitTime = 0.5, OneShot = false, Autostart = true };
        _pollTimer.Timeout += PollNewLogs;
        AddChild(_pollTimer);
    }

    private void PollNewLogs()
    {
        var logs = LogBuffer.GetLogs();
        if (logs.Count > _lastLineCount)
        {
            Callable.From(() =>
            {
                for (int i = _lastLineCount; i < logs.Count; i++)
                    AppendLine(logs[i]);
                _lastLineCount = logs.Count;
            }).CallDeferred();
        }
    }

    private void ReloadLogs()
    {
        _logView.Clear();
        var logs = LogBuffer.GetLogs();
        _lastLineCount = logs.Count;
        foreach (var line in logs)
            AppendLine(line);
    }

    private void OnNewLog(string line)
    {
        Callable.From(() => AppendLine(line)).CallDeferred();
    }

    private void AppendLine(string line)
    {
        if (!IsInstanceValid(_logView)) return;
        if (line.StartsWith("[ERROR]"))
            _logView.PushColor(Colors.Red);
        else if (line.StartsWith("[WARN]"))
            _logView.PushColor(Colors.Yellow);
        else
            _logView.PushColor(new Color(0.8f, 0.8f, 0.8f));
        _logView.AddText(line + "\n");
        _logView.Pop();
        if (_autoScroll)
            _logView.ScrollToLine(int.MaxValue);
    }
}
