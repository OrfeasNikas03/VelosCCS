using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace VelosCCS;

public partial class AISetupDialog : Window
{
    [Signal]
    public delegate void ProceedEventHandler(string model, string language);

    private Label _statusLabel = null!;
    private ItemList _modelList = null!;
    private OptionButton _languageDropdown = null!;
    private Button _actionBtn = null!;
    private Button _cancelBtn = null!;
    private ProgressBar _dlProgress = null!;
    private VBoxContainer _dlGroup = null!;

    private static readonly Dictionary<string, string> Languages = new()
    {
        ["English"] = "en", ["Greek"] = "el", ["Spanish"] = "es", ["French"] = "fr",
        ["German"] = "de", ["Italian"] = "it", ["Portuguese"] = "pt", ["Russian"] = "ru",
        ["Japanese"] = "ja", ["Korean"] = "ko", ["Chinese"] = "zh", ["Arabic"] = "ar",
        ["Hindi"] = "hi", ["Dutch"] = "nl", ["Turkish"] = "tr", ["Polish"] = "pl",
        ["Afrikaans"] = "af", ["Amharic"] = "am", ["Assamese"] = "as", ["Azerbaijani"] = "az",
        ["Bashkir"] = "ba", ["Belarusian"] = "be", ["Bulgarian"] = "bg", ["Bengali"] = "bn",
        ["Tibetan"] = "bo", ["Breton"] = "br", ["Bosnian"] = "bs", ["Catalan"] = "ca",
        ["Czech"] = "cs", ["Welsh"] = "cy", ["Danish"] = "da", ["Estonian"] = "et",
        ["Basque"] = "eu", ["Persian"] = "fa", ["Finnish"] = "fi", ["Faroese"] = "fo",
        ["Galician"] = "gl", ["Gujarati"] = "gu", ["Hausa"] = "ha", ["Hawaiian"] = "haw",
        ["Hebrew"] = "he", ["Croatian"] = "hr", ["Haitian Creole"] = "ht", ["Hungarian"] = "hu",
        ["Armenian"] = "hy", ["Indonesian"] = "id", ["Icelandic"] = "is", ["Javanese"] = "jw",
        ["Georgian"] = "ka", ["Kazakh"] = "kk", ["Khmer"] = "km", ["Kannada"] = "kn",
        ["Latin"] = "la", ["Luxembourgish"] = "lb", ["Lingala"] = "ln", ["Lao"] = "lo",
        ["Lithuanian"] = "lt", ["Latvian"] = "lv", ["Malagasy"] = "mg", ["Maori"] = "mi",
        ["Macedonian"] = "mk", ["Malayalam"] = "ml", ["Mongolian"] = "mn", ["Marathi"] = "mr",
        ["Malay"] = "ms", ["Maltese"] = "mt", ["Burmese"] = "my", ["Nepali"] = "ne",
        ["Norwegian Nynorsk"] = "nn", ["Norwegian"] = "no", ["Occitan"] = "oc", ["Punjabi"] = "pa",
        ["Pashto"] = "ps", ["Romanian"] = "ro", ["Sanskrit"] = "sa", ["Sindhi"] = "sd",
        ["Sinhala"] = "si", ["Slovak"] = "sk", ["Slovenian"] = "sl", ["Shona"] = "sn",
        ["Somali"] = "so", ["Albanian"] = "sq", ["Serbian"] = "sr", ["Sundanese"] = "su",
        ["Swedish"] = "sv", ["Swahili"] = "sw", ["Tamil"] = "ta", ["Telugu"] = "te",
        ["Tajik"] = "tg", ["Thai"] = "th", ["Turkmen"] = "tk", ["Tagalog"] = "tl",
        ["Tatar"] = "tt", ["Ukrainian"] = "uk", ["Urdu"] = "ur", ["Uzbek"] = "uz",
        ["Vietnamese"] = "vi", ["Yiddish"] = "yi", ["Yoruba"] = "yo", ["Cantonese"] = "yue",
    };

    public override void _Ready()
    {
        Title = "AI Clip Finder";
        Size = new Vector2I(500, 520);
        InitialPosition = WindowInitialPosition.CenterPrimaryScreen;
        Exclusive = true;
        Transient = true;
        Theme = AppTheme.Create();

        var bg = new PanelContainer();
        bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(bg);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("margin_left", 20);
        vbox.AddThemeConstantOverride("margin_right", 20);
        vbox.AddThemeConstantOverride("margin_top", 20);
        bg.AddChild(vbox);

        _statusLabel = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.Word,
            CustomMinimumSize = new Vector2(0, 40),
        };
        vbox.AddChild(_statusLabel);

        vbox.AddChild(new HSeparator());
        var langLabel = new Label { Text = "Video Language", Modulate = Color.FromHtml("#D0570C") };
        vbox.AddChild(langLabel);
        _languageDropdown = new OptionButton();
        int langIdx = 0;
        int selLang = 0;
        foreach (var kvp in Languages)
        {
            _languageDropdown.AddItem(kvp.Key);
            _languageDropdown.SetItemMetadata(langIdx, kvp.Value);
            if (kvp.Value == AppConfig.CaptionLanguage) selLang = langIdx;
            langIdx++;
        }
        _languageDropdown.Selected = selLang;
        vbox.AddChild(_languageDropdown);

        vbox.AddChild(new HSeparator());
        var modelLabel = new Label { Text = "LLM Model", Modulate = Color.FromHtml("#D0570C") };
        vbox.AddChild(modelLabel);

        _modelList = new ItemList
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SelectMode = ItemList.SelectModeEnum.Single,
            AllowRmbSelect = false,
        };
        vbox.AddChild(_modelList);

        _dlGroup = new VBoxContainer { Visible = false };
        _dlProgress = new ProgressBar
        {
            MinValue = 0, MaxValue = 100, Value = 0,
            ShowPercentage = true,
            CustomMinimumSize = new Vector2(0, 20),
        };
        _dlGroup.AddChild(_dlProgress);
        vbox.AddChild(_dlGroup);

        var btnRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        btnRow.AddThemeConstantOverride("separation", 8);
        _cancelBtn = new Button { Text = "Cancel", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _cancelBtn.Pressed += () => this.BounceOutThenFree();
        btnRow.AddChild(_cancelBtn);

        _actionBtn = new Button
        {
            Text = "Start",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Disabled = true,
        };
        _actionBtn.Pressed += OnAction;
        btnRow.AddChild(_actionBtn);
        vbox.AddChild(btnRow);

        PopulateModels();
        this.BounceIn();
    }

    private void PopulateModels()
    {
        _modelList.Clear();
        var options = LlamaManager.ModelOptions;
        for (int i = 0; i < options.Length; i++)
        {
            var m = options[i];
            string dlStatus = LlamaManager.IsModelDownloaded(m.name) ? " [downloaded]" : "";
            string label = $"{m.name}{dlStatus}\n  {m.desc}\n  RAM: {m.ram}  |  GPU: {m.vram}";
            _modelList.AddItem(label);
        }

        _modelList.Select(0);
        _actionBtn.Text = "Find Clips";
        _actionBtn.Disabled = false;
        _modelList.ItemSelected += (_) => { _actionBtn.Disabled = false; };
    }

    private async void OnAction()
    {
        int sel = _modelList.GetSelectedItems().Length > 0 ? _modelList.GetSelectedItems()[0] : 0;
        string model = LlamaManager.ModelOptions[sel].name;
        string language = (string)_languageDropdown.GetItemMetadata(_languageDropdown.Selected);
        Log.Print($"AISetupDialog: selected model={model}, language={language}");

        if (!LlamaManager.IsModelDownloaded(model))
        {
            _statusLabel.Text = $"Downloading {model}...";
            _dlGroup.Visible = true;
            _actionBtn.Disabled = true;
            _dlProgress.Value = 0;

            bool downloaded = await LlamaManager.EnsureModelDownloadedAsync(model, msg =>
            {
                _statusLabel.Text = msg;
                if (msg.Contains('%'))
                {
                    int pctStart = msg.LastIndexOf(' ') + 1;
                    string pctStr = msg.Substring(pctStart).TrimEnd('%');
                    if (int.TryParse(pctStr, out int pct))
                        _dlProgress.Value = pct;
                }
            });

            if (downloaded)
            {
                _dlProgress.Value = 100;
                _statusLabel.Text = $"{model} ready!";
            }
            else
            {
                _statusLabel.Text = $"Failed to download {model}. Check your connection.";
                _actionBtn.Disabled = false;
                _actionBtn.Text = "Retry";
                return;
            }
        }

        LlamaManager.SetDetectedModel(model);

        Exclusive = false;
        EmitSignal(SignalName.Proceed, model, language);
        this.BounceOutThenFree();
    }
}
