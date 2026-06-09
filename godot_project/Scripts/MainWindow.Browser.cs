using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace VelosCCS;

partial class MainWindow
{
    private const int BrowserBatchSize = 24;
    private const int BrowserColumns = 3;
    private static readonly System.Net.Http.HttpClient _browserHttp = new();
    static MainWindow()
    {
        _browserHttp.DefaultRequestHeaders.UserAgent.ParseAdd("VelosCCS/1.0");
    }

    private struct BrowserItem
    {
        public string Name;
        public string? Path;
        public string Category;
        public string? RemoteUrl;
        public string? DisplayFontPath;
        public string? OnlineUrl;
        public bool IsOnline;
    }

    // ── Google Fonts search ──────────────────────────────────────────────

    private static async Task<List<string>> SearchGoogleFonts(string query)
    {
        var list = new List<string>();
        try
        {
            string json = await _browserHttp.GetStringAsync("https://fonts.google.com/metadata/fonts");
            var doc = JsonDocument.Parse(json);
            var families = doc.RootElement.GetProperty("familyMetadataList");
            string q = query.ToLowerInvariant();
            foreach (var f in families.EnumerateArray())
            {
                string family = f.GetProperty("family").GetString() ?? "";
                if (!family.ToLowerInvariant().Contains(q)) continue;
                list.Add(family);
            }
        }
        catch { }
        return list;
    }

    private static async Task<string?> DownloadGoogleFont(string family)
    {
        string dir = ProjectSettings.GlobalizePath("user://fonts/");
        if (!DirAccess.DirExistsAbsolute(dir))
            DirAccess.MakeDirAbsolute(dir);

        string safeName = family.Replace(" ", "").Replace("-", "") + ".ttf";
        string local = Path.Combine(dir, safeName);
        if (File.Exists(local)) return local;

        try
        {
            // Fetch download URL via Google Fonts CSS API
            string cssUrl = $"https://fonts.googleapis.com/css2?family={Uri.EscapeDataString(family)}&display=swap";
            string css = await _browserHttp.GetStringAsync(cssUrl);

            // Extract the first url(...) from the CSS response
            int urlStart = css.IndexOf("url(", StringComparison.Ordinal);
            if (urlStart < 0) return null;
            urlStart += "url(".Length;
            int urlEnd = css.IndexOf(")", urlStart);
            if (urlEnd < 0) return null;
            string downloadUrl = css.Substring(urlStart, urlEnd - urlStart);
            // Strip quotes if present
            downloadUrl = downloadUrl.Trim('\'', '"');

            byte[] data = await _browserHttp.GetByteArrayAsync(downloadUrl);
            if (data == null || data.Length == 0) return null;
            File.WriteAllBytes(local, data);
            return local;
        }
        catch { return null; }
    }

    // ── Wikimedia Commons image search ──────────────────────────────────

    private static async Task<List<(string Name, string ImageUrl)>> SearchWikimediaImages(string query)
    {
        var list = new List<(string Name, string ImageUrl)>();
        try
        {
            string searchUrl = $"https://commons.wikimedia.org/w/api.php?action=query&list=search&srsearch={Uri.EscapeDataString(query)}&format=json&srlimit=30&srnamespace=6";
            string searchJson = await _browserHttp.GetStringAsync(searchUrl);
            var searchDoc = JsonDocument.Parse(searchJson);
            var hits = searchDoc.RootElement.GetProperty("query").GetProperty("search");
            var titles = new List<string>();
            foreach (var h in hits.EnumerateArray())
                titles.Add(h.GetProperty("title").GetString() ?? "");
            if (titles.Count == 0) return list;

            // Fetch image URLs in one batch
            string batch = string.Join("|", titles.Take(20).Select(t => Uri.EscapeDataString(t)));
            string infoUrl = $"https://commons.wikimedia.org/w/api.php?action=query&titles={batch}&prop=imageinfo&iiprop=url&format=json";
            string infoJson = await _browserHttp.GetStringAsync(infoUrl);
            var infoDoc = JsonDocument.Parse(infoJson);
            var pages = infoDoc.RootElement.GetProperty("query").GetProperty("pages");
            foreach (var p in pages.EnumerateObject())
            {
                if (!p.Value.TryGetProperty("imageinfo", out var ii)) continue;
                if (ii.ValueKind != JsonValueKind.Array || ii.GetArrayLength() == 0) continue;
                string url = ii[0].GetProperty("url").GetString() ?? "";
                string title = p.Value.GetProperty("title").GetString() ?? "";
                string name = title.Replace("File:", "").Replace("_", " ");
                if (name.Length > 50) name = name.Substring(0, 50) + "...";
                list.Add((name, url));
            }
        }
        catch { }
        return list;
    }

    // ── Myinstants sound search ─────────────────────────────────────────

    private static async Task<List<(string Name, string SoundUrl)>> SearchMyinstants(string query)
    {
        var list = new List<(string Name, string SoundUrl)>();
        try
        {
            string html = await _browserHttp.GetStringAsync(
                $"https://www.myinstants.com/en/search/?name={Uri.EscapeDataString(query)}");
            int idx = 0;
            while (true)
            {
                idx = html.IndexOf("play('/media/sounds/", idx);
                if (idx < 0) break;
                int urlStart = idx + "play('/media/sounds/".Length;
                int urlEnd = html.IndexOf("'", urlStart);
                if (urlEnd < 0) break;
                string soundFile = html.Substring(urlStart, urlEnd - urlStart);
                if (!soundFile.EndsWith(".mp3")) { idx = urlEnd + 1; continue; }

                // Look backwards for title="Play NAME sound"
                int titleEnd = idx;
                int titleStart = html.LastIndexOf("title=\"Play ", titleEnd, Math.Min(titleEnd, 300));
                string name;
                if (titleStart >= 0)
                {
                    titleStart += "title=\"Play ".Length;
                    int titleEnd2 = html.IndexOf(" sound\"", titleStart);
                    name = titleEnd2 > titleStart && titleEnd2 - titleStart < 100
                        ? html.Substring(titleStart, titleEnd2 - titleStart)
                        : Path.GetFileNameWithoutExtension(soundFile);
                }
                else
                {
                    name = Path.GetFileNameWithoutExtension(soundFile);
                }

                if (!list.Any(x => x.Name == name))
                    list.Add((name, $"https://www.myinstants.com/media/sounds/{soundFile}"));
                idx = urlEnd + 1;
            }
        }
        catch { }
        return list;
    }

    // ── Online thumbnail downloader ─────────────────────────────────────

    private static async Task<ImageTexture?> DownloadThumbnail(string url)
    {
        try
        {
            byte[] data = await _browserHttp.GetByteArrayAsync(url);
            if (data == null || data.Length == 0) return null;
            var img = new Image();
            Error err = Error.FileNotFound;
            string lower = url.ToLowerInvariant();
            if (lower.Contains(".png")) err = img.LoadPngFromBuffer(data);
            else if (lower.Contains(".jpg") || lower.Contains(".jpeg")) err = img.LoadJpgFromBuffer(data);
            else if (lower.Contains(".webp")) err = img.LoadWebpFromBuffer(data);
            if (err != Error.Ok || img.IsEmpty()) return null;
            img.Resize(64, 64, Image.Interpolation.Lanczos);
            return ImageTexture.CreateFromImage(img);
        }
        catch { return null; }
    }

    // ── Font Browser Window ─────────────────────────────────────────────

    private void OpenFontBrowserWindow()
    {
        var window = new Window
        {
            Title = "Font Browser",
            Size = new Vector2I(700, 750),
            InitialPosition = Window.WindowInitialPosition.CenterPrimaryScreen,
            Transient = true, Exclusive = true,
        };
        window.CloseRequested += () => window.BounceOutThenFree();

        var bg = new PanelContainer(); bg.SetAnchorsPreset(LayoutPreset.FullRect); window.AddChild(bg);
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("margin_left", 20); vbox.AddThemeConstantOverride("margin_right", 20);
        vbox.AddThemeConstantOverride("margin_top", 20); vbox.AddThemeConstantOverride("separation", 15);
        bg.AddChild(vbox);

        vbox.AddChild(new Label { Text = "FONTS", Modulate = Color.FromHtml("#D0570C") });

        string searchText = "";
        List<BrowserItem> allItems = new();
        List<BrowserItem> filtered = new();
        bool searching = false;

        var searchBox = new LineEdit { PlaceholderText = "Search fonts...", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        vbox.AddChild(searchBox);

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        var grid = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(grid); vbox.AddChild(scroll);

        var statusLabel = new Label { Modulate = new Color(0.6f, 0.6f, 0.6f) };
        vbox.AddChild(statusLabel);

        var importBtn = new Button { Text = "Import Font", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        vbox.AddChild(importBtn);

        void LoadLocal()
        {
            allItems.Clear();
            foreach (var kvp in _fontManager.AvailableFonts)
            {
                string cachePath = _fontManager.GetFontPath(kvp.Key);
                allItems.Add(new BrowserItem { Name = kvp.Key, Category = "Font", RemoteUrl = kvp.Value,
                    DisplayFontPath = File.Exists(cachePath) ? cachePath : null });
            }
            string userFontDir = ProjectSettings.GlobalizePath("user://fonts/");
            if (DirAccess.DirExistsAbsolute(userFontDir))
            {
                foreach (string f in Directory.GetFiles(userFontDir, "*.ttf").Concat(Directory.GetFiles(userFontDir, "*.otf")))
                {
                    string n = Path.GetFileNameWithoutExtension(f);
                    if (allItems.Any(b => b.Name == n)) continue;
                    allItems.Add(new BrowserItem { Name = n, Path = f, Category = "Font", DisplayFontPath = f });
                }
            }
        }

        async void DoOnlineSearch(string q)
        {
            if (string.IsNullOrWhiteSpace(q) || searching) return;
            searching = true;
            statusLabel.Text = "Searching Google Fonts...";
            try
            {
                var online = await SearchGoogleFonts(q);
                foreach (var family in online)
                {
                    if (allItems.Any(b => b.Name == family)) continue;
                    allItems.Add(new BrowserItem { Name = family, Category = "Font", IsOnline = true });
                }
            }
            catch { }
            searching = false;
            ApplyFilter();
        }

        void ApplyFilter()
        {
            filtered = (string.IsNullOrEmpty(searchText)
                ? allItems
                : allItems.Where(b => b.Name.ToLowerInvariant().Contains(searchText))).ToList();
            RenderGrid();
        }

        void RenderGrid()
        {
            foreach (var c in grid.GetChildren()) { grid.RemoveChild(c); if (c is Node n) n.QueueFree(); }
            statusLabel.Text = $"{filtered.Count} fonts";
            if (filtered.Count > 0)
            {
                var gc = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
                gc.AddThemeConstantOverride("h_separation", 15);
                gc.AddThemeConstantOverride("v_separation", 15);
                for (int i = 0; i < filtered.Count; i++)
                    gc.AddChild(MakeFontCard(filtered[i]));
                grid.AddChild(gc);
            }
            else
                grid.AddChild(new Label { Text = string.IsNullOrEmpty(searchText) ? "No fonts found." : $"No results for '{searchText}'", Modulate = new Color(0.5f, 0.5f, 0.5f), SizeFlagsHorizontal = SizeFlags.ExpandFill });
        }

        Control MakeFontCard(BrowserItem item)
        {
            var card = new PanelContainer { CustomMinimumSize = new Vector2(0, 100), SizeFlagsHorizontal = SizeFlags.ExpandFill };
            var h = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Begin, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            h.AddThemeConstantOverride("separation", 15);
            card.AddChild(h);

            var preview = new Label
            {
                Text = "Aa",
                CustomMinimumSize = new Vector2(60, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            FontFile? loaded = null;
            if (item.DisplayFontPath != null && File.Exists(item.DisplayFontPath))
            {
                try { loaded = new FontFile(); loaded.LoadDynamicFont(item.DisplayFontPath);
                    preview.AddThemeFontOverride("font", loaded); preview.AddThemeFontSizeOverride("font_size", 32); }
                catch { loaded = null; }
            }
            if (loaded == null)
            {
                preview.AddThemeFontSizeOverride("font_size", 24);
                preview.Modulate = new Color(0.5f, 0.5f, 0.5f);
            }
            h.AddChild(preview);

            var nameLabel = new Label { Text = item.Name, SizeFlagsHorizontal = SizeFlags.ExpandFill,
                VerticalAlignment = VerticalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart,
                ClipText = false };
            nameLabel.AddThemeFontSizeOverride("font_size", 14);
            if (loaded != null)
                nameLabel.AddThemeFontOverride("font", loaded);
            h.AddChild(nameLabel);

            var selectBtn = new Button { Text = "Select", SizeFlagsVertical = SizeFlags.ShrinkCenter, FocusMode = FocusModeEnum.None };
            h.AddChild(selectBtn);

            string captured = item.Name;
            selectBtn.Pressed += () => { Log.Print("[UI] Button: Font Select pressed"); _ = PickFont(captured); };
            card.GuiInput += (ev) =>
            {
                if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                    _ = PickFont(captured);
            };
            return card;
        }

        async Task PickFont(string fontName)
        {
            if (_selTrackIdx < 0 || _selClipIdx < 0) { SetStatus("Select a text clip first"); return; }
            var clip = _tracks[_selTrackIdx].Clips[_selClipIdx];
            if (clip.ClipType != ClipType.Text) { SetStatus("Select a text clip first"); return; }

            // Check if already installed locally
            string userFontDir = ProjectSettings.GlobalizePath("user://fonts/");
            foreach (string ext in new[] { ".ttf", ".otf" })
            {
                string fp = Path.Combine(userFontDir, fontName + ext);
                if (File.Exists(fp)) { ApplyFont(clip, fp, fontName); return; }
            }
            // Check FontManager
            if (_fontManager.AvailableFonts.ContainsKey(fontName))
            {
                string? path = await _fontManager.DownloadFont(fontName);
                if (path != null) { ApplyFont(clip, path, fontName); return; }
            }
            // Download from Google Fonts via CSS API
            statusLabel.Text = $"Downloading {fontName}...";
            string? dlPath = await DownloadGoogleFont(fontName);
            if (dlPath != null) { ApplyFont(clip, dlPath, fontName); return; }
            SetStatus($"Could not download font: {fontName}");
        }

        void ApplyFont(TrackClipData clip, string path, string name)
        {
            clip.FontPath = path; _overlay.RefreshActiveLayer(); _outputPreview.RefreshDisplayLayer();
            SetStatus($"Font: {name}"); RebuildInspector();
            if (IsInstanceValid(window)) window.BounceOutThenFree();
        }

        searchBox.TextChanged += (t) =>
        {
            searchText = t.ToLowerInvariant();
            if (searchText.Length >= 2) DoOnlineSearch(searchText);
            LoadLocal();
            ApplyFilter();
        };
        scroll.GetVScrollBar().ValueChanged += (val) => { };

        importBtn.Pressed += () =>
        {
            Log.Print("[UI] Button: Font Import pressed");
            var fd = new FileDialog { Title = "Import Font", FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true, CurrentDir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile) };
            fd.AddFilter("*.ttf,*.otf ; Font Files");
            void FdCleanup() { if (IsInstanceValid(fd)) fd.QueueFree(); }
            fd.CloseRequested += FdCleanup;
            fd.FileSelected += (filePath) =>
            {
                string fontDir = ProjectSettings.GlobalizePath("user://fonts/");
                if (!DirAccess.DirExistsAbsolute(fontDir)) DirAccess.MakeDirAbsolute(fontDir);
                string dest = Path.Combine(fontDir, Path.GetFileName(filePath));
                try { DirAccess.CopyAbsolute(filePath, dest); } catch { }
                SetStatus($"Imported font: {Path.GetFileName(filePath)}");
                FdCleanup(); LoadLocal(); ApplyFilter();
            };
            window.AddChild(fd); fd.PopupCentered();
        };

        var closeBtn = new Button { Text = "Close", SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
        closeBtn.Pressed += () => { Log.Print("[UI] Button: Font Close pressed"); window.BounceOutThenFree(); };
        vbox.AddChild(closeBtn);

        LoadLocal(); ApplyFilter();
        AddChild(window); window.Popup(); window.BounceIn();
    }

    // ── Sound Browser Window ─────────────────────────────────────────────

    private void OpenSoundBrowserWindow()
    {
        var window = new Window
        {
            Title = "Sound Browser",
            Size = new Vector2I(700, 750),
            InitialPosition = Window.WindowInitialPosition.CenterPrimaryScreen,
            Transient = true, Exclusive = true,
            Theme = AppTheme.Create(),
        };
        window.CloseRequested += () => window.BounceOutThenFree();

        var bg = new PanelContainer(); bg.SetAnchorsPreset(LayoutPreset.FullRect); window.AddChild(bg);
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("margin_left", 20); vbox.AddThemeConstantOverride("margin_right", 20);
        vbox.AddThemeConstantOverride("margin_top", 20); vbox.AddThemeConstantOverride("separation", 15);
        bg.AddChild(vbox);

        vbox.AddChild(new Label { Text = "SOUNDS", Modulate = Color.FromHtml("#D0570C") });

        string searchText = "";
        List<BrowserItem> allItems = new();
        List<BrowserItem> filtered = new();
        bool searching = false;

        var searchBox = new LineEdit { PlaceholderText = "Search sounds...", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        vbox.AddChild(searchBox);

        var volumeH = new HBoxContainer();
        volumeH.AddChild(new Label { Text = "Volume:", VerticalAlignment = VerticalAlignment.Center });
        var volumeSlider = new HSlider { MinValue = 0, MaxValue = 1, Value = 0.5f, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var volumeLabel = new Label { Text = "50%", VerticalAlignment = VerticalAlignment.Center, CustomMinimumSize = new Vector2(40, 0) };
        volumeSlider.ValueChanged += (v) => { _sfxPreviewPlayer.VolumeDb = Mathf.LinearToDb(Mathf.Clamp((float)v, 0.001f, 1f)); volumeLabel.Text = $"{(int)(v * 100)}%"; };
        volumeH.AddChild(volumeSlider);
        volumeH.AddChild(volumeLabel);
        vbox.AddChild(volumeH);

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        var grid = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(grid); vbox.AddChild(scroll);

        var statusLabel = new Label { Modulate = new Color(0.6f, 0.6f, 0.6f) };
        vbox.AddChild(statusLabel);

        var importBtn = new Button { Text = "Import Sound", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        vbox.AddChild(importBtn);

        void LoadLocal()
        {
            foreach (var kvp in _sfxManager.AvailableSFX)
                if (!allItems.Any(b => b.Name == kvp.Key))
                    allItems.Add(new BrowserItem { Name = kvp.Key, Category = "Audio", RemoteUrl = kvp.Value });

            string sfxDir = ProjectSettings.GlobalizePath("user://sfx/");
            if (DirAccess.DirExistsAbsolute(sfxDir))
            {
                foreach (string f in Directory.GetFiles(sfxDir, "*.mp3").Concat(Directory.GetFiles(sfxDir, "*.wav"))
                    .Concat(Directory.GetFiles(sfxDir, "*.ogg")).Concat(Directory.GetFiles(sfxDir, "*.flac")))
                {
                    string n = Path.GetFileNameWithoutExtension(f);
                    if (allItems.Any(b => b.Name == n || b.Path == f)) continue;
                    allItems.Add(new BrowserItem { Name = n, Path = f, Category = "Audio" });
                }
            }
        }

        async void DoOnlineSearch(string q)
        {
            if (string.IsNullOrWhiteSpace(q) || searching) return;
            searching = true;
            statusLabel.Text = "Searching Myinstants...";
            try
            {
                var online = await SearchMyinstants(q);
                foreach (var (name, url) in online)
                {
                    if (allItems.Any(b => b.Name == name || b.OnlineUrl == url)) continue;
                    allItems.Add(new BrowserItem { Name = name, Category = "Audio", OnlineUrl = url, IsOnline = true });
                }
            }
            catch { }
            searching = false;
            ApplyFilter();
        }

        void ApplyFilter()
        {
            filtered = (string.IsNullOrEmpty(searchText)
                ? allItems
                : allItems.Where(b => b.Name.ToLowerInvariant().Contains(searchText))).ToList();
            RenderGrid();
        }

        void RenderGrid()
        {
            foreach (var c in grid.GetChildren()) { grid.RemoveChild(c); if (c is Node n) n.QueueFree(); }
            statusLabel.Text = $"{filtered.Count} sounds";
            if (filtered.Count > 0)
            {
                var gc = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
                gc.AddThemeConstantOverride("h_separation", 15);
                gc.AddThemeConstantOverride("v_separation", 15);
                for (int i = 0; i < filtered.Count; i++)
                    gc.AddChild(MakeSoundCard(filtered[i]));
                grid.AddChild(gc);
            }
            else
                grid.AddChild(new Label { Text = string.IsNullOrEmpty(searchText) ? "No sounds found." : $"No results for '{searchText}'", Modulate = new Color(0.5f, 0.5f, 0.5f), SizeFlagsHorizontal = SizeFlags.ExpandFill });
        }

        Control MakeSoundCard(BrowserItem item)
        {
            var card = new PanelContainer { CustomMinimumSize = new Vector2(0, 60), SizeFlagsHorizontal = SizeFlags.ExpandFill };
            var h = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            h.AddThemeConstantOverride("separation", 15);
            card.AddChild(h);

            var icon = new Label { Text = item.IsOnline ? "🌐" : "🔊", CustomMinimumSize = new Vector2(50, 0),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
            h.AddChild(icon);

            var nameLabel = new Label { Text = item.Name, SizeFlagsHorizontal = SizeFlags.ExpandFill,
                VerticalAlignment = VerticalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart,
                ClipText = false };
            nameLabel.AddThemeFontSizeOverride("font_size", 14);
            h.AddChild(nameLabel);

            var btnVbox = new VBoxContainer { SizeFlagsVertical = SizeFlags.ShrinkCenter };
            string cName = item.Name; string? cPath = item.Path; string? cUrl = item.RemoteUrl; string? cOnline = item.OnlineUrl;

            var previewBtn = new Button { Text = "Preview", SizeFlagsHorizontal = SizeFlags.ExpandFill };
            previewBtn.Pressed += async () =>
            {
                Log.Print("[UI] Button: Sound Preview pressed");
                string? p = cPath;
                if (p == null || !File.Exists(p))
                {
                    if (cUrl != null) p = await _sfxManager.DownloadSFX(cName);
                    else if (cOnline != null) p = await DownloadOnlineSound(cName, cOnline);
                }
                if (p != null && File.Exists(p))
                { _sfxPreviewPlayer.Stream = AudioStreamMP3.LoadFromBuffer(Godot.FileAccess.GetFileAsBytes(p)); _sfxPreviewPlayer.Play(); }
            };
            btnVbox.AddChild(previewBtn);

            var addBtn = new Button { Text = "Add", SizeFlagsHorizontal = SizeFlags.ExpandFill };
            addBtn.Pressed += async () =>
            {
                Log.Print("[UI] Button: Sound Add pressed");
                string? p = cPath;
                if (p == null || !File.Exists(p))
                {
                    if (cUrl != null) p = await _sfxManager.DownloadSFX(cName);
                    else if (cOnline != null) p = await DownloadOnlineSound(cName, cOnline);
                }
                if (p != null && File.Exists(p))
                {
                    AddAudioClipToTimeline(cName, p);
                    _projectBin.Add(new MediaAsset(cName, p, AssetType.Audio));
                    RefreshBinUI();
                    if (IsInstanceValid(window)) window.BounceOutThenFree();
                }
            };
            btnVbox.AddChild(addBtn);

            h.AddChild(btnVbox);
            return card;
        }

        async Task<string?> DownloadOnlineSound(string name, string url)
        {
            string sfxDir = ProjectSettings.GlobalizePath("user://sfx/");
            if (!DirAccess.DirExistsAbsolute(sfxDir)) DirAccess.MakeDirAbsolute(sfxDir);
            string local = Path.Combine(sfxDir, $"{name.Replace(" ", "_").ToLowerInvariant()}.mp3");
            if (File.Exists(local)) return local;
            try
            {
                byte[] data = await _browserHttp.GetByteArrayAsync(url);
                File.WriteAllBytes(local, data);
                return local;
            }
            catch { return null; }
        }

        searchBox.TextChanged += (t) =>
        {
            searchText = t.ToLowerInvariant();
            if (searchText.Length >= 2) DoOnlineSearch(searchText);
            LoadLocal();
            ApplyFilter();
        };
        scroll.GetVScrollBar().ValueChanged += (val) => { };

        importBtn.Pressed += () =>
        {
            Log.Print("[UI] Button: Sound Import pressed");
            var fd = new FileDialog { Title = "Import Sound", FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true, CurrentDir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile) };
            fd.AddFilter("*.mp3,*.wav,*.ogg,*.flac ; Audio Files");
            void FdCleanup() { if (IsInstanceValid(fd)) fd.QueueFree(); }
            fd.CloseRequested += FdCleanup;
            fd.FileSelected += (filePath) =>
            {
                string sfxDir = ProjectSettings.GlobalizePath("user://sfx/");
                if (!DirAccess.DirExistsAbsolute(sfxDir)) DirAccess.MakeDirAbsolute(sfxDir);
                string dest = Path.Combine(sfxDir, Path.GetFileName(filePath));
                try { DirAccess.CopyAbsolute(filePath, dest); } catch { }
                SetStatus($"Imported sound: {Path.GetFileName(filePath)}");
                FdCleanup(); LoadLocal(); ApplyFilter();
            };
            window.AddChild(fd); fd.PopupCentered();
        };

        var closeBtn = new Button { Text = "Close", SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
        closeBtn.Pressed += () => { Log.Print("[UI] Button: Sound Close pressed"); window.BounceOutThenFree(); };
        vbox.AddChild(closeBtn);

        LoadLocal(); ApplyFilter();
        AddChild(window); window.Popup(); window.BounceIn();
    }

    // ── Image Browser Window ─────────────────────────────────────────────

    private void OpenImageBrowserWindow()
    {
        var window = new Window
        {
            Title = "Image Browser",
            Size = new Vector2I(700, 750),
            InitialPosition = Window.WindowInitialPosition.CenterPrimaryScreen,
            Transient = true, Exclusive = true,
        };
        window.CloseRequested += () => window.BounceOutThenFree();

        var bg = new PanelContainer(); bg.SetAnchorsPreset(LayoutPreset.FullRect); window.AddChild(bg);
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("margin_left", 20); vbox.AddThemeConstantOverride("margin_right", 20);
        vbox.AddThemeConstantOverride("margin_top", 20); vbox.AddThemeConstantOverride("separation", 15);
        bg.AddChild(vbox);

        vbox.AddChild(new Label { Text = "IMAGES & GIFs", Modulate = Color.FromHtml("#D0570C") });

        string searchText = "";
        List<BrowserItem> allItems = new();
        List<BrowserItem> filtered = new();
        bool searching = false;
        var thumbnails = new Dictionary<string, ImageTexture>();

        var searchBox = new LineEdit { PlaceholderText = "Search images...", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        vbox.AddChild(searchBox);

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        var grid = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(grid); vbox.AddChild(scroll);

        var statusLabel = new Label { Modulate = new Color(0.6f, 0.6f, 0.6f) };
        vbox.AddChild(statusLabel);

        var importBtn = new Button { Text = "Import Image/GIF", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        vbox.AddChild(importBtn);

        void LoadLocal()
        {
            string[] imageDirs = {
                ProjectSettings.GlobalizePath("res://Assets/"),
                ProjectSettings.GlobalizePath("user://stickers/"),
            };
            foreach (string d in imageDirs)
            {
                if (!DirAccess.DirExistsAbsolute(d)) continue;
                var dir = DirAccess.Open(d);
                if (dir == null) continue;
                dir.ListDirBegin();
                while (true)
                {
                    string? fn = dir.GetNext();
                    if (string.IsNullOrEmpty(fn)) break;
                    if (fn == "." || fn == "..") continue;
                    string ext = Path.GetExtension(fn).ToLowerInvariant();
                    if (ext is ".png" or ".gif" or ".jpg" or ".jpeg" or ".webp")
                    {
                        string full = Path.Combine(d, fn);
                        string n = Path.GetFileNameWithoutExtension(fn);
                        if (allItems.Any(b => b.Path == full)) continue;
                        allItems.Add(new BrowserItem { Name = n, Path = full, Category = "Image" });
                    }
                }
                dir.ListDirEnd();
            }
        }

        async void DoOnlineSearch(string q)
        {
            if (string.IsNullOrWhiteSpace(q) || searching) return;
            searching = true;
            statusLabel.Text = "Searching Wikimedia Commons...";
            try
            {
                var online = await SearchWikimediaImages(q);
                // Sort: PNG/GIF (transparent) first, then WEBP, then JPG
                online.Sort((a, b) =>
                {
                    int Score(string url)
                    {
                        string l = url.ToLowerInvariant();
                        if (l.Contains(".png")) return 0;
                        if (l.Contains(".gif")) return 1;
                        if (l.Contains(".webp")) return 2;
                        return 3;
                    }
                    return Score(a.ImageUrl).CompareTo(Score(b.ImageUrl));
                });
                foreach (var (name, url) in online)
                {
                    if (allItems.Any(b => b.OnlineUrl == url)) continue;
                    allItems.Add(new BrowserItem { Name = name, Category = "Image", OnlineUrl = url, IsOnline = true });
                }
                // Kick off thumbnail downloads (first 12 only)
                var toThumb = online.Take(12).ToList();
                foreach (var (_, url) in toThumb)
                {
                    if (thumbnails.ContainsKey(url)) continue;
                    _ = LoadThumbnailAsync(url);
                }
            }
            catch { }
            searching = false;
            ApplyFilter();
        }

        async Task LoadThumbnailAsync(string url)
        {
            var tex = await DownloadThumbnail(url);
            if (tex != null)
            {
                thumbnails[url] = tex;
                ApplyFilter(); // re-render to show thumbnail
            }
        }

        void ApplyFilter()
        {
            filtered = (string.IsNullOrEmpty(searchText)
                ? allItems
                : allItems.Where(b => b.Name.ToLowerInvariant().Contains(searchText))).ToList();
            RenderGrid();
        }

        void RenderGrid()
        {
            foreach (var c in grid.GetChildren()) { grid.RemoveChild(c); if (c is Node n) n.QueueFree(); }
            statusLabel.Text = $"{filtered.Count} images";
            if (filtered.Count > 0)
            {
                var gc = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
                gc.AddThemeConstantOverride("h_separation", 15);
                gc.AddThemeConstantOverride("v_separation", 15);
                for (int i = 0; i < filtered.Count; i++)
                    gc.AddChild(MakeImageCard(filtered[i]));
                grid.AddChild(gc);
            }
            else
                grid.AddChild(new Label { Text = string.IsNullOrEmpty(searchText) ? "No images found." : $"No results for '{searchText}'", Modulate = new Color(0.5f, 0.5f, 0.5f), SizeFlagsHorizontal = SizeFlags.ExpandFill });
        }

        Control MakeImageCard(BrowserItem item)
        {
            var card = new PanelContainer { CustomMinimumSize = new Vector2(0, 80), SizeFlagsHorizontal = SizeFlags.ExpandFill };
            var h = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            h.AddThemeConstantOverride("separation", 15);
            card.AddChild(h);

            Control thumbCtrl;
            if (item.IsOnline)
            {
                if (item.OnlineUrl != null && thumbnails.TryGetValue(item.OnlineUrl, out var thumb))
                {
                    thumbCtrl = new TextureRect { Texture = thumb, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                        StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                        CustomMinimumSize = new Vector2(64, 64), SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
                }
                else
                {
                    thumbCtrl = new Label { Text = "⏳", HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center, CustomMinimumSize = new Vector2(64, 64),
                        SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
                }
            }
            else if (item.Path != null)
            {
                try
                {
                    var img = new Image();
                    if (img.Load(item.Path) == Error.Ok && !img.IsEmpty())
                    {
                        img.Resize(64, 64, Image.Interpolation.Lanczos);
                        var tex = ImageTexture.CreateFromImage(img);
                        thumbCtrl = new TextureRect { Texture = tex, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                            CustomMinimumSize = new Vector2(64, 64), SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
                    }
                    else { thumbCtrl = new Control { CustomMinimumSize = new Vector2(64, 64) }; }
                }
                catch { thumbCtrl = new Control { CustomMinimumSize = new Vector2(64, 64) }; }
            }
            else { thumbCtrl = new Control { CustomMinimumSize = new Vector2(64, 64) }; }

            h.AddChild(thumbCtrl);

            var nameLabel = new Label { Text = item.Name, SizeFlagsHorizontal = SizeFlags.ExpandFill,
                VerticalAlignment = VerticalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart,
                ClipText = false };
            nameLabel.AddThemeFontSizeOverride("font_size", 14);
            h.AddChild(nameLabel);

            string capturedPath = item.Path!; string capturedUrl = item.OnlineUrl!;
            var selectBtn = new Button { Text = "Select", SizeFlagsVertical = SizeFlags.ShrinkCenter, FocusMode = FocusModeEnum.None };
            selectBtn.Pressed += () =>
            {
                Log.Print("[UI] Button: Image Select pressed");
                if (capturedPath != null && File.Exists(capturedPath))
                    AddImageClipToTimeline(capturedPath);
                else if (capturedUrl != null)
                { _ = DownloadAndAddImage(capturedUrl, capturedPath ?? "image"); return; }
                if (IsInstanceValid(window)) window.BounceOutThenFree();
            };
            h.AddChild(selectBtn);
            return card;
        }

        async Task DownloadAndAddImage(string url, string name)
        {
            string stickerDir = ProjectSettings.GlobalizePath("user://stickers/");
            if (!DirAccess.DirExistsAbsolute(stickerDir)) DirAccess.MakeDirAbsolute(stickerDir);
            string ext = url.Contains(".png") ? ".png" : url.Contains(".gif") ? ".gif" : ".jpg";
            string local = Path.Combine(stickerDir, $"online_{Guid.NewGuid():N}{ext}");
            try
            {
                byte[] data = await _browserHttp.GetByteArrayAsync(url);
                File.WriteAllBytes(local, data);
                AddImageClipToTimeline(local);
                if (IsInstanceValid(window)) window.BounceOutThenFree();
            }
            catch { SetStatus("Failed to download image"); }
        }

        searchBox.TextChanged += (t) =>
        {
            searchText = t.ToLowerInvariant();
            if (searchText.Length >= 2) DoOnlineSearch(searchText);
            LoadLocal();
            ApplyFilter();
        };
        scroll.GetVScrollBar().ValueChanged += (val) => { };

        importBtn.Pressed += () =>
        {
            Log.Print("[UI] Button: Image Import pressed");
            var fd = new FileDialog { Title = "Import Image/GIF", FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true, CurrentDir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile) };
            fd.AddFilter("*.png,*.gif,*.jpg,*.jpeg,*.webp ; Images");
            void FdCleanup() { if (IsInstanceValid(fd)) fd.QueueFree(); }
            fd.CloseRequested += FdCleanup;
            fd.FileSelected += (filePath) =>
            {
                string stickerDir = ProjectSettings.GlobalizePath("user://stickers/");
                if (!DirAccess.DirExistsAbsolute(stickerDir)) DirAccess.MakeDirAbsolute(stickerDir);
                string dest = Path.Combine(stickerDir, Path.GetFileName(filePath));
                try { DirAccess.CopyAbsolute(filePath, dest); } catch { }
                SetStatus($"Imported image: {Path.GetFileName(filePath)}");
                FdCleanup(); LoadLocal(); ApplyFilter();
            };
            window.AddChild(fd); fd.PopupCentered();
        };

        var closeBtn = new Button { Text = "Close", SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
        closeBtn.Pressed += () => { Log.Print("[UI] Button: Image Close pressed"); window.BounceOutThenFree(); };
        vbox.AddChild(closeBtn);

        LoadLocal(); ApplyFilter();
        AddChild(window); window.Popup(); window.BounceIn();
    }
}
