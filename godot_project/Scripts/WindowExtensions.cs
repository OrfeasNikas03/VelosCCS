using Godot;

namespace VelosCCS;

public static class WindowExtensions
{
    public static void BounceIn(this Window win)
    {
        Log.Print($"[UI] {win.GetType().Name} BounceIn");
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(win)) return;
            var orig = win.Position;
            win.Position = new Vector2I(orig.X, orig.Y + 30);
            var tween = win.CreateTween();
            tween.TweenProperty(win, "position", orig, 0.35f)
                 .SetTrans(Tween.TransitionType.Back)
                 .SetEase(Tween.EaseType.Out);
        }).CallDeferred();
    }

    public static void BounceOutThenFree(this Window win)
    {
        if (!GodotObject.IsInstanceValid(win)) return;
        Log.Print($"[UI] {win.GetType().Name} BounceOutThenFree");
        var tween = win.CreateTween();
        tween.TweenProperty(win, "position",
            new Vector2I(win.Position.X, win.Position.Y + 30), 0.2f)
             .SetTrans(Tween.TransitionType.Back)
             .SetEase(Tween.EaseType.In);
        tween.Finished += () =>
        {
            win.LogSizes(win.GetType().Name);
            win.QueueFree();
        };
    }

    public static void BounceOutThenHide(this Window win)
    {
        if (!GodotObject.IsInstanceValid(win)) return;
        Log.Print($"[UI] {win.GetType().Name} BounceOutThenHide");
        var tween = win.CreateTween();
        tween.TweenProperty(win, "position",
            new Vector2I(win.Position.X, win.Position.Y + 30), 0.2f)
             .SetTrans(Tween.TransitionType.Back)
             .SetEase(Tween.EaseType.In);
        tween.Finished += () =>
        {
            if (GodotObject.IsInstanceValid(win))
            {
                win.LogSizes(win.GetType().Name);
                win.Hide();
            }
        };
    }
}
