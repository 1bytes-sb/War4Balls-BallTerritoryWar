using Godot;

/// <summary>
/// 共享字体。默认主题字体不带中文字形，这里直接用系统字体：
/// 中文走微软雅黑，大数字走等宽粗体（和视频里「743,545」那种等宽数字一致）。
/// </summary>
public static class Fonts
{
    private static Font _ui;
    private static Font _uiBold;
    private static Font _num;

    private static readonly string[] CjkNames =
        { "Microsoft YaHei UI", "Microsoft YaHei", "PingFang SC", "Noto Sans CJK SC", "Source Han Sans SC", "SimHei" };

    private static readonly string[] MonoNames =
        { "Consolas", "Cascadia Mono", "JetBrains Mono", "DejaVu Sans Mono", "Menlo", "Courier New" };

    public static Font Ui => _ui ??= Make(400, CjkNames);
    public static Font UiBold => _uiBold ??= Make(700, CjkNames);
    public static Font Num => _num ??= Make(700, MonoNames);

    private static Font Make(int weight, string[] names)
    {
        return new SystemFont
        {
            FontNames = names,
            FontWeight = weight,
            AllowSystemFallback = true,
            Antialiasing = TextServer.FontAntialiasing.Gray,
            SubpixelPositioning = TextServer.SubpixelPositioning.Disabled,
        };
    }

    /// <summary>以 center 为中心画一行字（center.Y 是视觉中线）。</summary>
    public static void DrawCentered(CanvasItem ci, Font font, Vector2 center, string text, int size, Color color,
        int outline = 0, Color outlineColor = default)
    {
        Vector2 sz = font.GetStringSize(text, HorizontalAlignment.Left, -1f, size);
        float ascent = font.GetAscent(size);
        float descent = font.GetDescent(size);
        var pos = new Vector2(center.X - sz.X * 0.5f, center.Y + (ascent - descent) * 0.5f);
        if (outline > 0)
            ci.DrawStringOutline(font, pos, text, HorizontalAlignment.Left, -1f, size, outline, outlineColor);
        ci.DrawString(font, pos, text, HorizontalAlignment.Left, -1f, size, color);
    }

    /// <summary>左对齐：leftMid 是文字左端、垂直居中的位置。</summary>
    public static void DrawLeft(CanvasItem ci, Font font, Vector2 leftMid, string text, int size, Color color,
        int outline = 0, Color outlineColor = default)
    {
        float ascent = font.GetAscent(size);
        float descent = font.GetDescent(size);
        var pos = new Vector2(leftMid.X, leftMid.Y + (ascent - descent) * 0.5f);
        if (outline > 0)
            ci.DrawStringOutline(font, pos, text, HorizontalAlignment.Left, -1f, size, outline, outlineColor);
        ci.DrawString(font, pos, text, HorizontalAlignment.Left, -1f, size, color);
    }

    /// <summary>右对齐：rightMid 是文字右端、垂直居中的位置。</summary>
    public static void DrawRight(CanvasItem ci, Font font, Vector2 rightMid, string text, int size, Color color,
        int outline = 0, Color outlineColor = default)
    {
        Vector2 sz = font.GetStringSize(text, HorizontalAlignment.Left, -1f, size);
        float ascent = font.GetAscent(size);
        float descent = font.GetDescent(size);
        var pos = new Vector2(rightMid.X - sz.X, rightMid.Y + (ascent - descent) * 0.5f);
        if (outline > 0)
            ci.DrawStringOutline(font, pos, text, HorizontalAlignment.Left, -1f, size, outline, outlineColor);
        ci.DrawString(font, pos, text, HorizontalAlignment.Left, -1f, size, color);
    }
}
