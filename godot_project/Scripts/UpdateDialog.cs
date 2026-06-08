using Godot;
using System;
using System.IO;
using System.Threading.Tasks;

namespace VelosCCS;

public partial class UpdateDialog : Window
{
    private readonly UpdateInfo _info;
    private readonly string _currentVersion;
    private Label _versionLabel = null!;
    private RichTextLabel _changelog = null!;
    private ProgressBar _progressBar = null!;
    private Label _statusLabel = null!;
    private Button _updateBtn = null!;
    private Button _laterBtn = null!;
    private bool _downloading;

    public UpdateDialog(UpdateInfo info, string currentVersion)
    {
        _info = info;
        _currentVersion = currentVersion;
        Title = "Update Available";
        MinSize = new Vector2I(520, 420);
        Exclusive = true;
    }

    public override void _Ready()
    {
        Theme = AppTheme.Create();
        BuildUI();
        this.BounceIn();
    }

    private void BuildUI()
    {
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 10);
        AddChild(vbox);

        _versionLabel = new Label
        {
            Text = $"VelosCCS v{_currentVersion} \u2192 v{_info.LatestVersion}",
            HorizontalAlignment = HorizontalAlignment.Center,
            CustomMinimumSize = new Vector2(0, 30),
        };
        vbox.AddChild(_versionLabel);

        vbox.AddChild(new HSeparator());

        vbox.AddChild(new Label { Text = "What's new:" });

        _changelog = new RichTextLabel
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 200),
            BbcodeEnabled = true,
            ScrollActive = true,
            Text = SanitizeChangelog(_info.Changelog),
        };
        vbox.AddChild(_changelog);

        vbox.AddChild(new HSeparator());

        _progressBar = new ProgressBar
        {
            Visible = false,
            MaxValue = 100,
            ShowPercentage = true,
            CustomMinimumSize = new Vector2(0, 24),
        };
        vbox.AddChild(_progressBar);

        _statusLabel = new Label
        {
            Visible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        vbox.AddChild(_statusLabel);

        vbox.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.Expand });

        var btnRow = new HBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.End,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        btnRow.AddThemeConstantOverride("separation", 8);

        _laterBtn = new Button { Text = "Later" };
        _laterBtn.Pressed += OnLater;
        btnRow.AddChild(_laterBtn);

        _updateBtn = new Button { Text = "Update Now" };
        _updateBtn.Pressed += OnUpdateNow;
        btnRow.AddChild(_updateBtn);

        vbox.AddChild(btnRow);
    }

    private async void OnUpdateNow()
    {
        if (_downloading) return;
        _downloading = true;
        _updateBtn.Disabled = true;
        _laterBtn.Disabled = true;
        _changelog.Visible = false;
        _statusLabel.Visible = true;
        _statusLabel.Text = "Preparing download...";
        _progressBar.Visible = true;

        string tempDir = Path.Combine(Path.GetTempPath(), "VelosCCS_Update");
        Directory.CreateDirectory(tempDir);
        string installerPath = Path.Combine(tempDir, $"VelosCCS_Setup_v{_info.LatestVersion}.exe");

        foreach (var f in Directory.GetFiles(tempDir, "VelosCCS_Setup_*.exe"))
        {
            try { File.Delete(f); } catch { }
        }

        try
        {
            var progress = new Progress<double>(pct =>
            {
                _progressBar.SetDeferred("value", pct);
                _statusLabel.SetDeferred(Label.PropertyName.Text, $"Downloading... {pct:F0}%");
            });

            await UpdateChecker.DownloadInstallerAsync(_info.DownloadUrl, installerPath, progress);

            _statusLabel.Text = "Download complete. Installing...";
            _progressBar.Value = 100;
            await Task.Delay(500);

            AppConfig.LastUpdateCheck = DateTime.UtcNow;
            AppConfig.LastUpdateVersion = _info.LatestVersion;
            AppConfig.SaveSettings();

            UpdateChecker.ApplyUpdate(installerPath);
        }
        catch (Exception e)
        {
            _statusLabel.Text = $"Download failed: {e.Message}";
            _downloading = false;
            _updateBtn.Disabled = false;
            _laterBtn.Disabled = false;
            _changelog.Visible = true;
            _progressBar.Visible = false;
            _statusLabel.Visible = true;
            GD.PrintErr($"[UpdateDialog] {e}");
        }
    }

    private void OnLater()
    {
        AppConfig.SkipUpdateVersion = _info.LatestVersion;
        AppConfig.SaveSettings();
        Hide();
    }

    private static string SanitizeChangelog(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return "No changelog available.";
        raw = raw.Replace("&", "&amp;");
        raw = raw.Replace("<", "&lt;");
        raw = raw.Replace(">", "&gt;");
        return raw;
    }
}
