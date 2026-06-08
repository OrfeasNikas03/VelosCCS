using Godot;
using System;

namespace VelosCCS;

public partial class ProgressWindow : Window
{
    private Label _stepLabel = null!;
    private ProgressBar _progressBar = null!;
    private RichTextLabel _miniLog = null!;
    private int _logLines;

    public override void _Ready()
    {
        Title = "AI Clip Finder";
        Size = new Vector2I(420, 240);
        InitialPosition = WindowInitialPosition.CenterPrimaryScreen;
        Exclusive = true;
        Transient = true;
        Unresizable = false;
        Theme = AppTheme.Create();

        var bg = new PanelContainer();
        bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(bg);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("margin_left", 20);
        vbox.AddThemeConstantOverride("margin_right", 20);
        vbox.AddThemeConstantOverride("margin_top", 20);
        bg.AddChild(vbox);

        _stepLabel = new Label
        {
            Text = "Starting...",
            AutowrapMode = TextServer.AutowrapMode.Word,
            CustomMinimumSize = new Vector2(0, 40),
        };
        vbox.AddChild(_stepLabel);

        _progressBar = new ProgressBar
        {
            MinValue = 0,
            MaxValue = 100,
            Value = 0,
            ShowPercentage = true,
            CustomMinimumSize = new Vector2(0, 24),
        };
        vbox.AddChild(_progressBar);

        _miniLog = new RichTextLabel
        {
            CustomMinimumSize = new Vector2(0, 80),
            ScrollFollowing = true,
            BbcodeEnabled = true,
            Text = "[color=#8b949e]Waiting for engine...[/color]",
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        var sb = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.2f) };
        _miniLog.AddThemeStyleboxOverride("normal", sb);
        vbox.AddChild(_miniLog);

        this.BounceIn();
    }

    public void SetStep(string text)
    {
        _stepLabel.Text = text;
    }

    public void SetProgress(double fraction)
    {
        _progressBar.Value = Math.Clamp(fraction * 100, 0, 100);
    }

    public void SetProgressRange(double value, double max)
    {
        _progressBar.MaxValue = max;
        _progressBar.Value = Math.Clamp(value, 0, max);
    }

    public void Log(string msg)
    {
        _logLines++;
        _miniLog.AppendText($"\n[color=#D0570C]>[/color] {msg}");
        if (_logLines > 30)
        {
            string current = _miniLog.Text;
            int idx = current.IndexOf('\n', current.IndexOf('\n') + 1);
            if (idx > 0)
            {
                _miniLog.Text = current[..idx].TrimStart();
                _logLines--;
            }
        }
    }
}
