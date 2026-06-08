using Godot;
using System;
using System.IO;
using System.Threading.Tasks;

namespace VelosCCS;

public partial class WhisperSetupDialog : Window
{
    private Transcriber _transcriber = null!;
    private Label _statusLabel = null!;
    private ProgressBar _progressBar = null!;
    private Button _downloadBtn = null!;
    private Button _skipBtn = null!;
    private VBoxContainer _dlGroup = null!;

	private static string SkipMarker =>
		Path.Combine(
			System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
			".config", "velosccs", ".whisper_skip");

	public static bool ShouldShow
	{
		get
		{
			string modelPath = Path.Combine(
				System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
				".cache", "velosccs", "whisper",
				$"ggml-{AppConfig.WhisperModel}.bin");
			return !File.Exists(modelPath) && !File.Exists(SkipMarker);
		}
	}

    public WhisperSetupDialog()
    {
        GD.Print("[WhisperSetupDialog] Constructor");
    }

    public override void _EnterTree()
    {
        GD.Print("[WhisperSetupDialog] _EnterTree");
    }

    public override void _Ready()
    {
        GD.Print("[WhisperSetupDialog] _Ready start");
        try
        {
            Title = "Welcome to Velos Content Creation Suite";
            Size = new Vector2I(600, 420);
            InitialPosition = WindowInitialPosition.CenterPrimaryScreen;
            Theme = AppTheme.Create();
            GD.Print("[WhisperSetupDialog] Window properties set");

            _transcriber = new Transcriber();
            GD.Print("[WhisperSetupDialog] Transcriber created");

            var bg = new PanelContainer();
            bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(bg);

            var margin = new MarginContainer();
            margin.AddThemeConstantOverride("margin_left", 20);
            margin.AddThemeConstantOverride("margin_right", 20);
            margin.AddThemeConstantOverride("margin_top", 20);
            margin.AddThemeConstantOverride("margin_bottom", 8);
            bg.AddChild(margin);

            var vbox = new VBoxContainer();
            margin.AddChild(vbox);

            var title = new Label
            {
                Text = "Generate Captions with AI",
                ThemeTypeVariation = "Header",
                CustomMinimumSize = new Vector2(0, 32),
            };
            vbox.AddChild(title);

            var body = new Label
            {
                Text = "Velos Content Creation Suite can automatically transcribe your video's audio into " +
                       "searchable, editable captions using a speech recognition model that " +
                       "runs entirely on your computer.\n\n" +
                       "Nothing is sent to the cloud — your video never leaves your PC.",
                AutowrapMode = TextServer.AutowrapMode.Word,
                CustomMinimumSize = new Vector2(0, 80),
            };
            vbox.AddChild(body);

            var captionHint = new Label
            {
                Text = "Captions appear as text clips on your timeline and can be edited," +
                       " recolored, and repositioned like any other text layer.",
                AutowrapMode = TextServer.AutowrapMode.Word,
                Modulate = new Color(0.7f, 0.7f, 0.7f),
                CustomMinimumSize = new Vector2(0, 50),
            };
            vbox.AddChild(captionHint);

            var modelInfo = new Label
            {
                Text = $"Model: {AppConfig.WhisperModel} (~{ModelSizeMb} MB download)",
                AutowrapMode = TextServer.AutowrapMode.Word,
                Modulate = new Color(0.6f, 0.6f, 0.6f),
                CustomMinimumSize = new Vector2(0, 24),
            };
            vbox.AddChild(modelInfo);

            _statusLabel = new Label
            {
                AutowrapMode = TextServer.AutowrapMode.Word,
                CustomMinimumSize = new Vector2(0, 24),
            };
            _statusLabel.Hide();
            vbox.AddChild(_statusLabel);

            _dlGroup = new VBoxContainer { Visible = false };
            _progressBar = new ProgressBar
            {
                MinValue = 0, MaxValue = 100, Value = 0,
                ShowPercentage = true,
                CustomMinimumSize = new Vector2(0, 20),
            };
            _dlGroup.AddChild(_progressBar);
            vbox.AddChild(_dlGroup);

            vbox.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });

        var btnRow = new HBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 40),
        };
        btnRow.AddThemeConstantOverride("separation", 8);

        var btnBg = new StyleBoxFlat
        {
            BgColor = new Color(0.35f, 0.35f, 0.35f),
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ContentMarginLeft = 20, ContentMarginRight = 20,
            ContentMarginTop = 8, ContentMarginBottom = 8,
        };
        var btnHover = (StyleBoxFlat)btnBg.Duplicate();
        btnHover.BgColor = new Color(0.45f, 0.45f, 0.45f);

        _skipBtn = new Button
        {
            Text = "Skip",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _skipBtn.AddThemeStyleboxOverride("normal", btnBg);
        _skipBtn.AddThemeStyleboxOverride("hover", btnHover);
        _skipBtn.AddThemeColorOverride("font_color", Colors.White);
        _skipBtn.Pressed += OnSkip;
        btnRow.AddChild(_skipBtn);

        var dlBg = new StyleBoxFlat
        {
            BgColor = Color.FromHtml("#D0570C"),
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ContentMarginLeft = 20, ContentMarginRight = 20,
            ContentMarginTop = 8, ContentMarginBottom = 8,
        };
        var dlHover = (StyleBoxFlat)dlBg.Duplicate();
        dlHover.BgColor = Color.FromHtml("#79c0ff");

        _downloadBtn = new Button
        {
            Text = "Download Whisper",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _downloadBtn.AddThemeStyleboxOverride("normal", dlBg);
        _downloadBtn.AddThemeStyleboxOverride("hover", dlHover);
        _downloadBtn.AddThemeColorOverride("font_color", Colors.White);
        _downloadBtn.Pressed += OnDownload;
        btnRow.AddChild(_downloadBtn);

        vbox.AddChild(btnRow);
        GD.Print("[WhisperSetupDialog] UI elements created");

        // Force a layout update and log visibility
        GD.Print($"[WhisperSetupDialog] SkipBtn visible={_skipBtn.Visible} text='{_skipBtn.Text}' size={_skipBtn.Size}");
        GD.Print($"[WhisperSetupDialog] DlBtn visible={_downloadBtn.Visible} text='{_downloadBtn.Text}' size={_downloadBtn.Size}");

            GD.Print("[WhisperSetupDialog] _Ready complete, calling BounceIn");
            this.BounceIn();
        }
        catch (Exception e)
        {
            GD.PrintErr($"[WhisperSetupDialog] _Ready exception: {e.Message}\n{e.StackTrace}");
        }
    }

    public override void _ExitTree()
    {
        GD.Print("[WhisperSetupDialog] _ExitTree");
    }

    private static int ModelSizeMb => AppConfig.WhisperModel switch
    {
        "tiny" => 75,
        "base" => 150,
        "small" => 470,
        "medium" => 1500,
        "large-v3" => 3100,
        _ => 150,
    };

    private async void OnDownload()
    {
        GD.Print("[WhisperSetupDialog] OnDownload clicked");
        _downloadBtn.Disabled = true;
        _skipBtn.Disabled = true;
        _statusLabel.Show();
        _statusLabel.Text = "Starting download...";
        _dlGroup.Visible = true;
        _progressBar.Value = 0;

        try
        {
            await _transcriber.EnsureModelDownloadedAsync(msg =>
            {
                CallDeferred(nameof(UpdateProgress), msg);
            });

            _progressBar.Value = 100;
            _statusLabel.Text = "Whisper model ready! You can now generate captions from any video.";
            _downloadBtn.Text = "Done";
            _downloadBtn.Disabled = false;
            _downloadBtn.Pressed -= OnDownload;
            _downloadBtn.Pressed += OnClose;
            GD.Print("[WhisperSetupDialog] Download complete");
        }
        catch (Exception e)
        {
            GD.PrintErr($"[WhisperSetupDialog] Download failed: {e.Message}");
            _statusLabel.Text = $"Download failed: {e.Message}";
            _downloadBtn.Text = "Retry";
            _downloadBtn.Disabled = false;
        }
    }

    private void UpdateProgress(string msg)
    {
        if (msg.Contains('%'))
        {
            int pctStart = msg.LastIndexOf(' ') + 1;
            string pctStr = msg.Substring(pctStart).TrimEnd('%');
            if (int.TryParse(pctStr, out int pct))
                _progressBar.Value = pct;
        }
        _statusLabel.Text = msg;
        GD.Print($"[WhisperSetupDialog] Download progress: {msg}");
    }

    private void OnSkip()
    {
        GD.Print("[WhisperSetupDialog] OnSkip clicked");
        try
        {
            string dir = Path.GetDirectoryName(SkipMarker)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(SkipMarker, "skipped");
            GD.Print("[WhisperSetupDialog] Skip marker written");
        }
        catch (Exception e)
        {
            GD.PrintErr($"[WhisperSetupDialog] Skip marker write failed: {e.Message}");
        }
        OnClose();
    }

    private void OnClose()
    {
        GD.Print("[WhisperSetupDialog] OnClose");
        _transcriber?.Dispose();
        GD.Print("[WhisperSetupDialog] Calling BounceOutThenFree");
        this.BounceOutThenFree();
    }
}
