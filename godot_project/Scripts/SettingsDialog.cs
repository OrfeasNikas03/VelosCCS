using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace VelosCCS;

public partial class SettingsDialog : AcceptDialog
{
    private LineEdit _outputDir = null!;
    private LineEdit _clipOutputDir = null!;
    private CheckBox _normalizeAudio = null!;
    private OptionButton _captionLanguage = null!;
    public string OutputDir => _outputDir.Text.Replace('/', System.IO.Path.DirectorySeparatorChar);
    public string ClipOutputDir => _clipOutputDir.Text.Replace('/', System.IO.Path.DirectorySeparatorChar);
    public bool NormalizeAudio => _normalizeAudio.ButtonPressed;
    public string CaptionLanguage => (string)_captionLanguage.GetItemMetadata(_captionLanguage.Selected);

    public string CurrentOutputDir { get; set; } = "";
    public string CurrentClipOutputDir { get; set; } = "";
    public bool CurrentNormalizeAudio { get; set; } = true;
    public string CurrentCaptionLanguage { get; set; } = "en";

    public SettingsDialog()
    {
        Title = "Settings";
        MinSize = new Vector2I(420, 250);
        Exclusive = true;
        OkButtonText = "Save";
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

        // Output directory
        var dirLabel = new Label { Text = "Output Directory" };
        vbox.AddChild(dirLabel);
        var dirRow = new HBoxContainer();
        _outputDir = new LineEdit
        {
            Text = !string.IsNullOrEmpty(CurrentOutputDir)
                ? CurrentOutputDir.Replace('/', System.IO.Path.DirectorySeparatorChar)
                : System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile) + System.IO.Path.DirectorySeparatorChar + "VelosCCS" + System.IO.Path.DirectorySeparatorChar + "exports",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        dirRow.AddChild(_outputDir);
        var browseBtn = new Button { Text = "Browse" };
        browseBtn.Pressed += () =>
        {
            var fd = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.OpenDir,
                Access = FileDialog.AccessEnum.Filesystem,
                UseNativeDialog = true,
                CurrentDir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            };
            fd.DirSelected += path => { _outputDir.Text = path.Replace('/', System.IO.Path.DirectorySeparatorChar); };
            AddChild(fd);
            fd.PopupCentered();
        };
        dirRow.AddChild(browseBtn);
        vbox.AddChild(dirRow);

        // AI Clip output directory
        var clipDirLabel = new Label { Text = "AI Clip Output Directory" };
        vbox.AddChild(clipDirLabel);
        var clipDirRow = new HBoxContainer();
        _clipOutputDir = new LineEdit
        {
            Text = !string.IsNullOrEmpty(CurrentClipOutputDir)
                ? CurrentClipOutputDir.Replace('/', System.IO.Path.DirectorySeparatorChar)
                : ProjectSettings.GlobalizePath("user://clips/"),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        clipDirRow.AddChild(_clipOutputDir);
        var clipBrowseBtn = new Button { Text = "Browse" };
        clipBrowseBtn.Pressed += () =>
        {
            var fd = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.OpenDir,
                Access = FileDialog.AccessEnum.Filesystem,
                UseNativeDialog = true,
                CurrentDir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            };
            fd.DirSelected += path => { _clipOutputDir.Text = path.Replace('/', System.IO.Path.DirectorySeparatorChar); };
            AddChild(fd);
            fd.PopupCentered();
        };
        clipDirRow.AddChild(clipBrowseBtn);
        vbox.AddChild(clipDirRow);

		// Caption language — all 99 Whisper-supported languages
		var langLabel = new Label { Text = "Caption Language" };
		vbox.AddChild(langLabel);
		_captionLanguage = new OptionButton();
		var langs = new Dictionary<string, string>
		{
			["Afrikaans"] = "af", ["Amharic"] = "am", ["Arabic"] = "ar", ["Assamese"] = "as",
			["Azerbaijani"] = "az", ["Bashkir"] = "ba", ["Belarusian"] = "be", ["Bulgarian"] = "bg",
			["Bengali"] = "bn", ["Tibetan"] = "bo", ["Breton"] = "br", ["Bosnian"] = "bs",
			["Catalan"] = "ca", ["Czech"] = "cs", ["Welsh"] = "cy", ["Danish"] = "da",
			["German"] = "de", ["Greek"] = "el", ["English"] = "en", ["Spanish"] = "es",
			["Estonian"] = "et", ["Basque"] = "eu", ["Persian"] = "fa", ["Finnish"] = "fi",
			["Faroese"] = "fo", ["French"] = "fr", ["Galician"] = "gl", ["Gujarati"] = "gu",
			["Hausa"] = "ha", ["Hawaiian"] = "haw", ["Hebrew"] = "he", ["Hindi"] = "hi",
			["Croatian"] = "hr", ["Haitian Creole"] = "ht", ["Hungarian"] = "hu", ["Armenian"] = "hy",
			["Indonesian"] = "id", ["Icelandic"] = "is", ["Italian"] = "it", ["Japanese"] = "ja",
			["Javanese"] = "jw", ["Georgian"] = "ka", ["Kazakh"] = "kk", ["Khmer"] = "km",
			["Kannada"] = "kn", ["Korean"] = "ko", ["Latin"] = "la", ["Luxembourgish"] = "lb",
			["Lingala"] = "ln", ["Lao"] = "lo", ["Lithuanian"] = "lt", ["Latvian"] = "lv",
			["Malagasy"] = "mg", ["Maori"] = "mi", ["Macedonian"] = "mk", ["Malayalam"] = "ml",
			["Mongolian"] = "mn", ["Marathi"] = "mr", ["Malay"] = "ms", ["Maltese"] = "mt",
			["Burmese"] = "my", ["Nepali"] = "ne", ["Dutch"] = "nl", ["Norwegian Nynorsk"] = "nn",
			["Norwegian"] = "no", ["Occitan"] = "oc", ["Punjabi"] = "pa", ["Polish"] = "pl",
			["Pashto"] = "ps", ["Portuguese"] = "pt", ["Romanian"] = "ro", ["Russian"] = "ru",
			["Sanskrit"] = "sa", ["Sindhi"] = "sd", ["Sinhala"] = "si", ["Slovak"] = "sk",
			["Slovenian"] = "sl", ["Shona"] = "sn", ["Somali"] = "so", ["Albanian"] = "sq",
			["Serbian"] = "sr", ["Sundanese"] = "su", ["Swedish"] = "sv", ["Swahili"] = "sw",
			["Tamil"] = "ta", ["Telugu"] = "te", ["Tajik"] = "tg", ["Thai"] = "th",
			["Turkmen"] = "tk", ["Tagalog"] = "tl", ["Turkish"] = "tr", ["Tatar"] = "tt",
			["Ukrainian"] = "uk", ["Urdu"] = "ur", ["Uzbek"] = "uz", ["Vietnamese"] = "vi",
			["Yiddish"] = "yi", ["Yoruba"] = "yo", ["Chinese"] = "zh", ["Cantonese"] = "yue",
		};
		int selIdx = 0;
		int i = 0;
		foreach (var kvp in langs)
		{
			_captionLanguage.AddItem(kvp.Key);
			_captionLanguage.SetItemMetadata(i, kvp.Value);
			if (kvp.Value == CurrentCaptionLanguage) selIdx = i;
			i++;
		}
		_captionLanguage.Selected = selIdx;
		vbox.AddChild(_captionLanguage);

        // Toggles
        _normalizeAudio = new CheckBox { Text = "Normalize Audio", ButtonPressed = CurrentNormalizeAudio };
        vbox.AddChild(_normalizeAudio);

        vbox.AddChild(new HSeparator());

        var updateLabel = new Label { Text = "Updates" };
        vbox.AddChild(updateLabel);

        var versionRow = new HBoxContainer();
        versionRow.AddChild(new Label
        {
            Text = $"Current version: v{AppConfig.AppVersion}",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        });

        var checkBtn = new Button { Text = "Check for Updates" };
        checkBtn.Pressed += async () =>
        {
            checkBtn.Disabled = true;
            checkBtn.Text = "Checking...";

            var info = await UpdateChecker.CheckAsync(AppConfig.AppVersion);

            if (info == null)
            {
                var upToDate = new AcceptDialog
                {
                    Title = "Up to Date",
                    DialogText = $"VelosCCS v{AppConfig.AppVersion} is the latest version.",
                    OkButtonText = "OK",
                };
                upToDate.Ready += () =>
                {
                    upToDate.Theme = AppTheme.Create();
                    upToDate.PopupCentered();
                };
                AddChild(upToDate);
                checkBtn.Disabled = false;
                checkBtn.Text = "Check for Updates";
                return;
            }

            AppConfig.LastUpdateCheck = DateTime.UtcNow;
            AppConfig.LastUpdateVersion = info.LatestVersion;
            AppConfig.SaveSettings();

            var dlg = new UpdateDialog(info, AppConfig.AppVersion);
            var parent = GetParent();
            if (parent is MainWindow mw)
            {
                Hide();
                mw.AddChild(dlg);
                dlg.PopupCentered();
            }
            else
            {
                AddChild(dlg);
                dlg.PopupCentered();
            }
        };
        versionRow.AddChild(checkBtn);
        vbox.AddChild(versionRow);

        vbox.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.Expand });
    }
}
