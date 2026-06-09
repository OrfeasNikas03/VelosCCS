using Godot;

namespace VelosCCS;

public static class ControlExtensions
{
    public static void LogSizes(this Control ctrl, string context)
    {
        var size = ctrl.Size;
        var min = ctrl.CustomMinimumSize;
        var pos = ctrl.Position;
        var parentSize = ctrl.GetParentControl()?.Size ?? Vector2.Zero;
        Log.Print($"[UI] {context}: pos=({pos.X},{pos.Y}) size=({size.X}x{size.Y}) min=({min.X}x{min.Y}) parent=({parentSize.X}x{parentSize.Y})");
    }

    public static void LogSizes(this Window win, string context)
    {
        var size = win.Size;
        var pos = win.Position;
        Log.Print($"[UI] {context}: pos=({pos.X},{pos.Y}) size=({size.X}x{size.Y})");
    }

    private static Control? GetParentControl(this Control ctrl)
    {
        var p = ctrl.GetParent();
        while (p != null && p is not Control)
            p = p.GetParent();
        return p as Control;
    }
}
