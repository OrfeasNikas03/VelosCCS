// Popup window for selecting YouTube clip ranges to download.
// Add/remove start-end spin boxes, emits DownloadRequested with fragment list.

using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
// HttpClient fully qualified as System.Net.Http.HttpClient to avoid clash with Godot.HttpClient

namespace VelosCCS;

public partial class ClipPickerWindow : Window
{
    [Signal]
    public delegate void DownloadRequestedEventHandler(Godot.Collections.Array<Godot.Collections.Dictionary> fragments);

    private VBoxContainer _list = null!;
    private double _totalDuration;

    public override void _Ready()
    {
        Log.Print("[UI] ClipPickerWindow opened");
        Theme = AppTheme.Create();
        this.BounceIn();
    }

    public void Setup(string title, double duration, string thumbnailUrl = "")
    {
        Title = "SELECT CLIPS TO DOWNLOAD";
        Size = new Vector2I(600, 650);
        InitialPosition = WindowInitialPosition.CenterPrimaryScreen;
        Exclusive = true;
        Transient = true;
        _totalDuration = duration;

        var bg = new PanelContainer();
        bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(bg);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("margin_left", 20);
        vbox.AddThemeConstantOverride("margin_right", 20);
        vbox.AddThemeConstantOverride("margin_top", 20);
        bg.AddChild(vbox);

        // Thumbnail if available
        if (!string.IsNullOrEmpty(thumbnailUrl))
        {
            var thumbRect = new TextureRect
            {
                CustomMinimumSize = new Vector2(560, 200),
                StretchMode = TextureRect.StretchModeEnum.KeepAspect,
                ExpandMode = TextureRect.ExpandModeEnum.FitWidth,
            };
            _ = LoadThumbnailAsync(thumbnailUrl, thumbRect);
            vbox.AddChild(thumbRect);
        }

        vbox.AddChild(new Label { Text = $"{title}\nDuration: {Math.Round(duration / 60.0, 1)} min", AutowrapMode = TextServer.AutowrapMode.Word });
        vbox.AddChild(new HSeparator());

        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(_list);
        vbox.AddChild(scroll);

        AddRange();

        var btnRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var addBtn = new Button { Text = "+ Add Another Range", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        addBtn.Pressed += () => { Log.Print("[UI] ClipPicker: Add range"); AddRange(); };

        var dlBtn = new Button
        {
            Text = "START DOWNLOAD",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Modulate = Color.FromHtml("#D0570C"),
        };
        dlBtn.Pressed += () => { Log.Print("[UI] ClipPicker: Download pressed"); OnStartDownload(); };

        btnRow.AddChild(addBtn);
        btnRow.AddChild(dlBtn);
        vbox.AddChild(btnRow);
    }

    private void AddRange()
    {
        var row = new HBoxContainer();
        var startSpin = new SpinBox { MinValue = 0, MaxValue = _totalDuration, Suffix = "s", CustomMinimumSize = new Vector2(100, 0), Value = 0 };
        var endSpin = new SpinBox { MinValue = 0, MaxValue = _totalDuration, Suffix = "s", CustomMinimumSize = new Vector2(100, 0), Value = Mathf.Min(30, (float)_totalDuration) };

        var delBtn = new Button { Text = "X", Flat = true };
        delBtn.Pressed += () => row.QueueFree();

        row.AddChild(new Label { Text = "Start:" });
        row.AddChild(startSpin);
        row.AddChild(new Label { Text = "End:" });
        row.AddChild(endSpin);
        row.AddChild(delBtn);
        _list.AddChild(row);
    }

    private void OnStartDownload()
    {
        var fragments = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        foreach (var child in _list.GetChildren())
        {
            if (child is HBoxContainer row)
            {
                double start = ((SpinBox)row.GetChild(1)).Value;
                double end = ((SpinBox)row.GetChild(3)).Value;
                fragments.Add(new Godot.Collections.Dictionary { { "start", start }, { "end", end } });
            }
        }
        EmitSignal(SignalName.DownloadRequested, fragments);
        this.BounceOutThenHide();
    }

    private async Task LoadThumbnailAsync(string url, TextureRect target)
    {
        try
        {
            var http = new System.Net.Http.HttpClient();
            var bytes = await http.GetByteArrayAsync(url);
            http.Dispose();
            if (bytes.Length > 0)
            {
                var img = new Godot.Image();
                if (img.LoadPngFromBuffer(bytes) == Godot.Error.Ok ||
                    img.LoadJpgFromBuffer(bytes) == Godot.Error.Ok ||
                    img.LoadWebpFromBuffer(bytes) == Godot.Error.Ok)
                {
                    target.Texture = Godot.ImageTexture.CreateFromImage(img);
                }
            }
        }
        catch (Exception e)
        {
            GD.PrintErr("Picker thumbnail fail: " + e.Message);
        }
    }
}
