using Godot;
using System.Collections.Generic;

/// <summary>
/// 程序生成、按颜色缓存的小贴图（带 mipmap，配合 LinearWithMipmaps 过滤缩小时边缘平滑）：
/// 玻璃球、钉子、柔光圆、气流箭头。用一张贴图代替一堆 DrawCircle/DrawArc，既省绘制调用又有抗锯齿。
/// </summary>
public static class BallArt
{
    private const int BallPx = 128;
    private static readonly Dictionary<Color, ImageTexture> _balls = new();
    private static ImageTexture _peg, _soft, _halo, _arrow;

    private static float Smooth(float e0, float e1, float x)
    {
        float t = Mathf.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static ImageTexture Finish(Image img)
    {
        img.GenerateMipmaps();
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>
    /// 玻璃球（视频里的颜色球）：上半偏亮的光泽帽、下半偏暗并带一道亮月牙、左上柔和高光、细亮边。
    /// 透明像素的 RGB 填成边缘色，生成 mipmap 时不会出黑边。
    /// </summary>
    public static ImageTexture Ball(Color c)
    {
        if (_balls.TryGetValue(c, out var tex)) return tex;
        var img = Image.CreateEmpty(BallPx, BallPx, false, Image.Format.Rgba8);
        Color top = c.Lightened(0.10f), bottom = c.Darkened(0.50f);
        Color cap = c.Lerp(Colors.White, 0.45f), crescent = c.Lightened(0.30f), rim = c.Lerp(Colors.White, 0.62f);
        float half = BallPx * 0.5f;
        for (int y = 0; y < BallPx; y++)
        for (int x = 0; x < BallPx; x++)
        {
            float u = (x + 0.5f - half) / half, v = (y + 0.5f - half) / half;
            float d = Mathf.Sqrt(u * u + v * v);
            Color col = top.Lerp(bottom, Smooth(-0.85f, 0.95f, v) * 0.9f);
            // 光泽帽：上方一块椭圆
            float e = (u / 0.70f) * (u / 0.70f) + ((v + 0.44f) / 0.40f) * ((v + 0.44f) / 0.40f);
            col = col.Lerp(cap, (1f - Smooth(0.45f, 1.05f, e)) * 0.42f);
            // 下方亮月牙（环境反光）
            float k = Smooth(0.52f, 0.72f, d) * (1f - Smooth(0.82f, 0.93f, d)) * Smooth(0.15f, 0.75f, v);
            col = col.Lerp(crescent, k * 0.55f);
            // 左上柔和高光
            float sx = u + 0.34f, sy = v + 0.46f;
            float spec = Mathf.Exp(-(sx * sx + sy * sy) / (2f * 0.12f * 0.12f));
            col = col.Lerp(Colors.White, spec * 0.70f);
            // 细亮边
            col = col.Lerp(rim, Smooth(0.80f, 0.93f, d) * 0.92f);
            col.A = Mathf.Clamp((1f - d) * half + 0.5f, 0f, 1f);
            img.SetPixel(x, y, col);
        }
        tex = Finish(img);
        _balls[c] = tex;
        return tex;
    }

    /// <summary>钉子：蓝灰圆片，浅色细边、顶部略亮、左上一点小高光（视频里是平的浅灰圆片）。</summary>
    public static ImageTexture Peg()
    {
        if (_peg != null) return _peg;
        const int n = 64;
        var img = Image.CreateEmpty(n, n, false, Image.Format.Rgba8);
        Color baseC = new Color(118f / 255, 138f / 255, 157f / 255);
        Color top = baseC.Lightened(0.10f), bottom = baseC.Darkened(0.12f), rim = new Color(0.80f, 0.85f, 0.90f);
        float half = n * 0.5f;
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float u = (x + 0.5f - half) / half, v = (y + 0.5f - half) / half;
            float d = Mathf.Sqrt(u * u + v * v);
            Color col = top.Lerp(bottom, Smooth(-0.9f, 0.9f, v));
            float sx = u + 0.30f, sy = v + 0.34f;
            col = col.Lerp(Colors.White, Mathf.Exp(-(sx * sx + sy * sy) / (2f * 0.16f * 0.16f)) * 0.35f);
            col = col.Lerp(rim, Smooth(0.68f, 0.86f, d) * 0.75f);
            col.A = Mathf.Clamp((1f - d) * half + 0.5f, 0f, 1f);
            img.SetPixel(x, y, col);
        }
        return _peg = Finish(img);
    }

    /// <summary>白色柔光圆：中心 1，向外平滑落到 0（用 modulate 上色）。</summary>
    public static ImageTexture Soft()
    {
        if (_soft != null) return _soft;
        const int n = 64;
        var img = Image.CreateEmpty(n, n, false, Image.Format.Rgba8);
        float half = n * 0.5f;
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float u = (x + 0.5f - half) / half, v = (y + 0.5f - half) / half;
            float d = Mathf.Sqrt(u * u + v * v);
            float a = 1f - Smooth(0f, 1f, d);
            img.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));
        }
        return _soft = Finish(img);
    }

    /// <summary>白色光晕：比 Soft 平，中间一大片接近满值，外圈才淡出（HUD 弹珠的红色光晕）。</summary>
    public static ImageTexture Halo()
    {
        if (_halo != null) return _halo;
        const int n = 64;
        var img = Image.CreateEmpty(n, n, false, Image.Format.Rgba8);
        float half = n * 0.5f;
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float u = (x + 0.5f - half) / half, v = (y + 0.5f - half) / half;
            float a = 1f - Smooth(0.25f, 1f, Mathf.Sqrt(u * u + v * v));
            img.SetPixel(x, y, new Color(1f, 1f, 1f, a * Mathf.Sqrt(a)));
        }
        return _halo = Finish(img);
    }

    /// <summary>
    /// 气流箭头（白色，用 modulate 上色）：顶端箭头 + 竖杆，杆从头到尾渐隐。
    /// 贴图 36×96 对应屏幕 9×24 左右（4 倍超采样），线性过滤保证亚像素移动平滑。
    /// </summary>
    public static ImageTexture Arrow()
    {
        if (_arrow != null) return _arrow;
        const int w = 36, h = 96, ss = 4;               // ss = 贴图像素 / 屏幕像素
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        float cx = w * 0.5f, tipY = 2f * ss;
        float lw = 1.5f * ss * 0.5f;                     // 半线宽（贴图像素）
        Vector2 tip = new Vector2(cx, tipY);
        Vector2 wingL = tip + new Vector2(-3.6f, 5f) * ss, wingR = tip + new Vector2(3.6f, 5f) * ss;
        float tailY = h - 2f;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            var p = new Vector2(x + 0.5f, y + 0.5f);
            float dShaft = p.Y < tipY ? p.DistanceTo(tip) : p.Y > tailY ? p.DistanceTo(new Vector2(cx, tailY)) : Mathf.Abs(p.X - cx);
            float dWing = Mathf.Min(SegDist(p, tip, wingL), SegDist(p, tip, wingR));
            float aShaft = Mathf.Clamp(lw + 0.5f * ss - dShaft, 0f, ss) / ss;
            float aWing = Mathf.Clamp(lw + 0.5f * ss - dWing, 0f, ss) / ss;
            float grad = 1f - Smooth(0.05f, 1f, (p.Y - tipY) / (tailY - tipY)) * 0.9f;
            float a = Mathf.Max(aShaft * grad, aWing);
            img.SetPixel(x, y, new Color(1f, 1f, 1f, a));
        }
        return _arrow = Finish(img);
    }

    private static float SegDist(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = Mathf.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0f, 1f);
        return p.DistanceTo(a + ab * t);
    }
}

/// <summary>只负责画东西的空节点：_Draw 时调回调。用来把「很少变」的内容和每帧都变的内容分开。</summary>
public partial class DrawLayer : Node2D
{
    public System.Action<DrawLayer> OnDraw;
    public override void _Draw() => OnDraw?.Invoke(this);
}
