using Godot;

namespace VelosCCS;

public static class AppTheme
{
    public static Theme Create()
    {
        var theme = new Theme();

        var bgApp = Color.FromHtml("#191A25");
        var bgBar = Color.FromHtml("#11121C");
        var bgDialog = Color.FromHtml("#303030");
        var bgPanel = Color.FromHtml("#161b22");

        var accent = Color.FromHtml("#D0570C");
        var accentSec = Color.FromHtml("#f78166");
        var textMain = Color.FromHtml("#FBFBFB");
        var textDim = Color.FromHtml("#D9D9D9");
        var btnBg = Color.FromHtml("#555555");
        var danger = Color.FromHtml("#BF2618");

        var btnNormal = new StyleBoxFlat
        {
            BgColor = btnBg,
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            BorderWidthBottom = 2,
            BorderColor = new Color(1, 1, 1, 0.1f),
            ContentMarginLeft = 16, ContentMarginRight = 16,
            ContentMarginTop = 10, ContentMarginBottom = 10,
        };
        var btnHover = (StyleBoxFlat)btnNormal.Duplicate();
        btnHover.BgColor = new Color(accent.R, accent.G, accent.B, 0.3f);
        btnHover.BorderColor = accent;
        var btnPressed = (StyleBoxFlat)btnNormal.Duplicate();
        btnPressed.BgColor = new Color(accent.R, accent.G, accent.B, 0.5f);
        btnPressed.BorderWidthBottom = 0;
        btnPressed.BorderWidthTop = 2;

        theme.SetStylebox("normal", "Button", btnNormal);
        theme.SetStylebox("hover", "Button", btnHover);
        theme.SetStylebox("pressed", "Button", btnPressed);
        theme.SetColor("font_color", "Button", textMain);
        theme.SetColor("font_hover_color", "Button", Colors.White);
        theme.SetColor("font_pressed_color", "Button", accent);

        var bigBtn = (StyleBoxFlat)btnNormal.Duplicate();
        bigBtn.BgColor = new Color(accent.R, accent.G, accent.B, 0.1f);
        bigBtn.BorderWidthLeft = bigBtn.BorderWidthRight = bigBtn.BorderWidthTop = bigBtn.BorderWidthBottom = 2;
        bigBtn.BorderColor = accent;
        bigBtn.CornerRadiusTopLeft = 15; bigBtn.CornerRadiusBottomRight = 15;
        theme.SetStylebox("normal", "BigImportButton", bigBtn);

        var panelStyle = new StyleBoxFlat
        {
            BgColor = bgDialog,
            BorderWidthLeft = 1, BorderWidthRight = 1,
            BorderWidthTop = 1, BorderWidthBottom = 1,
            BorderColor = new Color(1, 1, 1, 0.05f),
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            ShadowColor = new Color(0, 0, 0, 0.3f),
            ShadowSize = 4,
            ContentMarginLeft = 12, ContentMarginRight = 12,
        };
        theme.SetStylebox("panel", "Panel", new StyleBoxFlat { BgColor = bgApp });
        theme.SetStylebox("panel", "PanelContainer", panelStyle);

        var lineEditStyle = new StyleBoxFlat
        {
            BgColor = bgApp,
            BorderWidthLeft = 2,
            BorderColor = bgBar,
            ContentMarginLeft = 10,
        };
        theme.SetStylebox("normal", "LineEdit", lineEditStyle);
        var focusEdit = (StyleBoxFlat)lineEditStyle.Duplicate();
        focusEdit.BorderColor = accent;
        focusEdit.ShadowColor = new Color(accent.R, accent.G, accent.B, 0.2f);
        focusEdit.ShadowSize = 8;
        theme.SetStylebox("focus", "LineEdit", focusEdit);
        theme.SetColor("font_color", "LineEdit", textMain);
        theme.SetColor("placeholder_color", "LineEdit", textDim);

        var pbBg = new StyleBoxFlat { BgColor = bgApp, CornerRadiusTopLeft = 2, CornerRadiusBottomLeft = 2 };
        var pbFill = new StyleBoxFlat { BgColor = accent, CornerRadiusTopLeft = 2, CornerRadiusBottomLeft = 2 };
        theme.SetStylebox("background", "ProgressBar", pbBg);
        theme.SetStylebox("fill", "ProgressBar", pbFill);

        theme.SetConstant("grabber_offset", "HSlider", 0);
        var sliderArea = new StyleBoxFlat
        {
            BgColor = bgDialog,
            ExpandMarginTop = 2, ExpandMarginBottom = 2,
            CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4,
        };
        theme.SetStylebox("slider", "HSlider", sliderArea);

        var itemBg = new StyleBoxFlat { BgColor = bgApp };
        var itemSelected = new StyleBoxFlat
        {
            BgColor = new Color(accent.R, accent.G, accent.B, 0.15f),
            BorderWidthLeft = 3,
            BorderColor = accent,
        };
        theme.SetStylebox("panel", "ItemList", itemBg);
        theme.SetStylebox("selected", "ItemList", itemSelected);
        theme.SetStylebox("selected_focus", "ItemList", itemSelected);
        theme.SetColor("font_color", "ItemList", textMain);
        theme.SetColor("selected_font_color", "ItemList", Colors.White);

        theme.SetStylebox("bg", "ScrollContainer", new StyleBoxEmpty());

        theme.SetColor("color", "Separator", new Color(1, 1, 1, 0.06f));

        var splitStyle = new StyleBoxFlat
        {
            BgColor = Color.FromHtml("#343942"),
            ContentMarginLeft = 3,
            ContentMarginRight = 3,
        };
        theme.SetStylebox("bg", "HSplitContainer", splitStyle);
        theme.SetStylebox("bg", "VSplitContainer", splitStyle);
        theme.SetConstant("separation", "HSplitContainer", 6);
        theme.SetConstant("separation", "VSplitContainer", 6);

        var tabUnselected = new StyleBoxFlat
        {
            BgColor = bgApp,
            ContentMarginLeft = 10, ContentMarginRight = 10,
        };
        var tabSelected = new StyleBoxFlat
        {
            BgColor = bgDialog,
            BorderWidthTop = 2,
            BorderColor = accent,
            ContentMarginLeft = 10, ContentMarginRight = 10,
        };
        theme.SetStylebox("tab_unselected", "TabContainer", tabUnselected);
        theme.SetStylebox("tab_selected", "TabContainer", tabSelected);

        var canvasStyle = new StyleBoxFlat
        {
            BgColor = Color.FromHtml("#11121C"),
            BorderWidthLeft = 1, BorderWidthRight = 1,
            BorderWidthTop = 1, BorderWidthBottom = 1,
            BorderColor = Color.FromHtml("#2a2a3a"),
        };
        theme.SetStylebox("panel", "WorkspaceCanvas", canvasStyle);

        theme.SetConstant("separation", "VBoxContainer", 8);

        theme.SetConstant("separation", "SpinBox", 4);

        var windowBg = new StyleBoxFlat
        {
            BgColor = bgDialog,
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
        };
        theme.SetStylebox("panel", "Window", windowBg);
        theme.SetColor("title_color", "Window", textMain);
        theme.SetConstant("title_height", "Window", 32);

        var checkNormal = new StyleBoxFlat { BgColor = new Color(0.15f, 0.15f, 0.15f), CornerRadiusBottomLeft = 2, CornerRadiusBottomRight = 2, CornerRadiusTopLeft = 2, CornerRadiusTopRight = 2 };
        var checkHover = new StyleBoxFlat { BgColor = new Color(0.25f, 0.25f, 0.25f), CornerRadiusBottomLeft = 2, CornerRadiusBottomRight = 2, CornerRadiusTopLeft = 2, CornerRadiusTopRight = 2 };
        var checkChecked = new StyleBoxFlat { BgColor = accent, CornerRadiusBottomLeft = 2, CornerRadiusBottomRight = 2, CornerRadiusTopLeft = 2, CornerRadiusTopRight = 2 };
        theme.SetStylebox("normal", "CheckBox", checkNormal);
        theme.SetStylebox("hover", "CheckBox", checkHover);
        theme.SetStylebox("pressed", "CheckBox", checkChecked);
        theme.SetStylebox("checked", "CheckBox", checkChecked);
        theme.SetStylebox("checked_pressed", "CheckBox", checkChecked);
        theme.SetColor("font_color", "CheckBox", textMain);
        theme.SetColor("font_pressed_color", "CheckBox", new Color(0, 0, 0));
        theme.SetColor("font_hover_color", "CheckBox", textMain);
        theme.SetColor("font_checked_color", "CheckBox", new Color(0, 0, 0));
        theme.SetConstant("check_v_offset", "CheckBox", 1);
        theme.SetConstant("separation", "CheckBox", 6);

        return theme;
    }
}
