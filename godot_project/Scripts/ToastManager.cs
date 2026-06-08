using Godot;
using System.Collections.Generic;

namespace VelosCCS;

public partial class ToastManager : Control
{
    private static readonly Dictionary<Node, int> _activeToasts = new();

    public static void Show(Node parent, string message)
    {
        ShowColored(parent, message, Color.FromHtml("#D0570C"));
    }

    public static void Show(Node parent, string message, Color color)
    {
        ShowColored(parent, message, color);
    }

    public static void Info(Node parent, string message)
    {
        ShowColored(parent, message, Color.FromHtml("#D0570C"));
    }

    public static void Success(Node parent, string message)
    {
        ShowColored(parent, message, Color.FromHtml("#3fb950"));
    }

    public static void Error(Node parent, string message)
    {
        ShowColored(parent, message, Color.FromHtml("#f85149"));
    }

    public static void Warning(Node parent, string message)
    {
        ShowColored(parent, message, Color.FromHtml("#d29922"));
    }

    private static void ShowColored(Node parent, string message, Color accent)
    {
        var panel = new PanelContainer();
        panel.ThemeTypeVariation = "StepPill";

        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 8);

        var dot = new ColorRect
        {
            Color = accent,
            CustomMinimumSize = new Vector2(8, 8),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        hbox.AddChild(dot);

        var label = new Label { Text = message, Modulate = new Color(0.9f, 0.9f, 0.95f) };
        hbox.AddChild(label);

        panel.AddChild(hbox);
        parent.AddChild(panel);

        // Stack toasts vertically
        if (!_activeToasts.ContainsKey(parent))
            _activeToasts[parent] = 0;
        int offset = _activeToasts[parent];
        _activeToasts[parent] = offset + 1;

        int baseY = 70;
        int spacing = 48;
        panel.Position = new Vector2(16, baseY + offset * spacing);
        panel.Modulate = new Color(1, 1, 1, 0);

        var tween = panel.CreateTween().SetParallel(true);
        tween.TweenProperty(panel, "modulate", new Color(1, 1, 1, 1), 0.2f);
        tween.TweenProperty(panel, "position:y", baseY + offset * spacing, 0.25f)
             .From(baseY + offset * spacing + 12)
             .SetTrans(Tween.TransitionType.Back)
             .SetEase(Tween.EaseType.Out);

        tween.TweenInterval(2.0);
        tween.TweenProperty(panel, "modulate:a", 0.0f, 0.4f);
        tween.Finished += () =>
        {
            _activeToasts[parent]--;
            if (_activeToasts[parent] <= 0)
                _activeToasts.Remove(parent);
            panel.QueueFree();
        };
    }
}
