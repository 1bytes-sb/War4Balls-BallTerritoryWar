using Godot;
using System;
using System.Globalization;

/// <summary>
/// 菜单界面共用的样式和小工具：主菜单、设置界面、答题、资金模式的交易面板都用这一套（深蓝底、圆角框、白字按钮）。
/// </summary>
public static class Ui
{
    public static readonly Color BgCol = new Color(0.008f, 0.027f, 0.075f);
    public static readonly Color PanelCol = new Color(0.035f, 0.063f, 0.13f, 0.97f);
    public static readonly Color PanelBorder = new Color(0.30f, 0.45f, 0.75f, 0.55f);
    public static readonly Color TextCol = new Color(0.86f, 0.89f, 0.95f);
    public static readonly Color DimCol = new Color(0.56f, 0.62f, 0.72f);
    public static readonly Color AccentCol = new Color(0.47f, 0.74f, 1f);
    public static readonly Color BadCol = new Color(1f, 0.46f, 0.46f);
    public static readonly Color GoodCol = new Color(0.36f, 0.86f, 0.53f);
    public static readonly Color MoneyCol = new Color(1f, 0.82f, 0.36f);

    public static readonly Color BtnBlue = new Color(0.16f, 0.42f, 0.85f);
    public static readonly Color BtnGrey = new Color(0.14f, 0.19f, 0.30f);
    public static readonly Color BtnGreen = new Color(0.10f, 0.50f, 0.30f);
    public static readonly Color BtnRed = new Color(0.62f, 0.17f, 0.22f);

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>菜单的主题：界面字体 24 号，输入框、滑条、滚动条的样式。</summary>
    public static Theme MakeTheme()
    {
        var theme = new Theme { DefaultFont = Fonts.Ui, DefaultFontSize = 24 };
        theme.SetStylebox("normal", "LineEdit", Box(new Color(0.055f, 0.09f, 0.17f), new Color(0.28f, 0.38f, 0.58f), 6, 12f, 6f));
        theme.SetStylebox("focus", "LineEdit", Outline(AccentCol, 6));
        theme.SetStylebox("read_only", "LineEdit", Box(new Color(0.045f, 0.07f, 0.12f), new Color(0.2f, 0.26f, 0.38f), 6, 12f, 6f));
        theme.SetStylebox("slider", "HSlider", Box(new Color(0.16f, 0.21f, 0.32f), default, 3, 0f, 3f));
        theme.SetStylebox("grabber_area", "HSlider", Box(new Color(0.24f, 0.50f, 0.88f), default, 3, 0f, 3f));
        theme.SetStylebox("grabber_area_highlight", "HSlider", Box(new Color(0.34f, 0.60f, 0.98f), default, 3, 0f, 3f));
        theme.SetStylebox("scroll", "VScrollBar", Box(new Color(0.06f, 0.09f, 0.16f), default, 4, 4f, 4f));
        theme.SetStylebox("grabber", "VScrollBar", Box(new Color(0.24f, 0.32f, 0.48f), default, 4, 4f, 4f));
        theme.SetStylebox("grabber_highlight", "VScrollBar", Box(new Color(0.32f, 0.44f, 0.64f), default, 4, 4f, 4f));
        theme.SetStylebox("grabber_pressed", "VScrollBar", Box(new Color(0.40f, 0.54f, 0.78f), default, 4, 4f, 4f));
        return theme;
    }

    /// <summary>
    /// 整屏菜单：深色底 + 正中一块圆角面板，返回面板里的竖排容器（间距 12）。layer 是挂这一屏的 CanvasLayer。
    /// </summary>
    public static VBoxContainer Screen(CanvasLayer layer, float width)
    {
        var root = new Control { Theme = MakeTheme() };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(root);

        var bg = new ColorRect { Color = BgCol };
        bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(bg);

        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(center);

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(width, 0f) };
        panel.AddThemeStyleboxOverride("panel", Box(PanelCol, PanelBorder, 14, 44f, 34f));
        center.AddChild(panel);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 12);
        panel.AddChild(box);
        return box;
    }

    public static Label MakeLabel(string text, Font font, int size, Color col, HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var l = new Label { Text = text, HorizontalAlignment = align, VerticalAlignment = VerticalAlignment.Center };
        l.AddThemeFontOverride("font", font);
        l.AddThemeFontSizeOverride("font_size", size);
        SetColor(l, col);
        return l;
    }

    /// <summary>会自动换行的说明文字（宽度跟着容器走）。</summary>
    public static Label MakeText(string text, int size, Color col, HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var l = MakeLabel(text, Fonts.Ui, size, col, align);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return l;
    }

    public static void SetColor(Label l, Color c) => l.AddThemeColorOverride("font_color", c);

    public static Control Gap(float h) => new Control { CustomMinimumSize = new Vector2(0f, h) };

    public static Button MakeButton(string text, Color baseCol, float w = 240f, float h = 58f, int fontSize = 26)
    {
        var b = new Button { Text = text, CustomMinimumSize = new Vector2(w, h) };
        b.AddThemeFontOverride("font", Fonts.UiBold);
        b.AddThemeFontSizeOverride("font_size", fontSize);
        SetButtonColor(b, baseCol);
        b.AddThemeStyleboxOverride("disabled", Box(new Color(0.10f, 0.12f, 0.17f), default, 10, 20f, 8f));
        b.AddThemeStyleboxOverride("focus", Outline(new Color(0.85f, 0.92f, 1f, 0.9f), 10));
        foreach (string c in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color", "font_hover_pressed_color" })
            b.AddThemeColorOverride(c, Colors.White);
        b.AddThemeColorOverride("font_disabled_color", new Color(0.4f, 0.44f, 0.52f));
        return b;
    }

    /// <summary>按钮底色（普通 / 悬停 / 按下三态）。</summary>
    public static void SetButtonColor(Button b, Color baseCol, float padX = 20f, float padY = 8f)
    {
        b.AddThemeStyleboxOverride("normal", Box(baseCol, default, 10, padX, padY));
        b.AddThemeStyleboxOverride("hover", Box(baseCol.Lightened(0.15f), default, 10, padX, padY));
        b.AddThemeStyleboxOverride("pressed", Box(baseCol.Darkened(0.2f), default, 10, padX, padY));
    }

    /// <summary>实心圆角框；border 是默认值（全 0）时不画边。</summary>
    public static StyleBoxFlat Box(Color bg, Color border, int radius, float padX, float padY)
    {
        var sb = new StyleBoxFlat { BgColor = bg };
        if (border.A > 0f)
        {
            sb.BorderColor = border;
            sb.SetBorderWidthAll(2);
        }
        sb.SetCornerRadiusAll(radius);
        sb.ContentMarginLeft = sb.ContentMarginRight = padX;
        sb.ContentMarginTop = sb.ContentMarginBottom = padY;
        return sb;
    }

    public static StyleBoxFlat Outline(Color col, int radius)
    {
        var sb = new StyleBoxFlat { DrawCenter = false, BorderColor = col };
        sb.SetBorderWidthAll(2);
        sb.SetCornerRadiusAll(radius);
        return sb;
    }

    /// <summary>金额：$1,234.56（负数 −$3.20）。</summary>
    public static string Money(double v) =>
        (v < -0.004 ? "−$" : "$") + Math.Abs(v).ToString("N2", Inv);

    /// <summary>带正负号的金额：+$3.20 / −$3.20。</summary>
    public static string SignedMoney(double v) =>
        (v < -0.004 ? "−$" : "+$") + Math.Abs(v).ToString("N2", Inv);

    /// <summary>认金额：$12.5、12.5、1,000、1k；认不出来或是负数返回 null。</summary>
    public static double? ParseMoney(string text)
    {
        string s = (text ?? "").Trim().Replace("$", "").Replace("＄", "").Replace(" ", "");
        return SettingsMenu.ParseAmount(s);
    }
}
