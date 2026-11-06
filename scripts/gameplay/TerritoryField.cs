using Godot;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

/// <summary>
/// 右侧领土战场：1000×1000 = 一百万个像素。
///
///   Owner     —— 归属（0..3，255=中立），战斗判定用
///   PaintTime —— 最近一次被涂色的时间（秒），决定亮度
///   Pixels    —— 每帧上传一次的 RGBA8：RGB = 加色层（护盾环、大球、粒子、子弹），A = 归属 + 新鲜度打包码。
///                真正的领土颜色由 GPU 合成（TerritoryCompose.gdshader，画进 1000×1000 的 SubViewport）。
///
/// 领土颜色 = 暗色 + (鲜色 − 暗色)·g。g 由「距上次涂色的时间」算出（视频实测曲线：
/// 1 秒内保持最亮，之后 exp(−(t−1)/18) 下降，约 32 秒沉到底），
/// 再对整张 g 图做一次 9×9 盒式模糊（GPU 上做）—— 视频里同一阵营内部是柔和的云状渐变，
/// 而不同阵营的交界仍然是清晰的（颜色按 Owner 取，不参与模糊）。模糊只平均有主的像素（中立像素不算），
/// 一两像素宽的细线、贴着空地的边缘都不会被旁边的空地拉暗。
///
/// 风暴、狙击扫过的地方会「过曝」成饱和的亮队色（视频实测）。做法是把 PaintTime 设到未来，年龄为负时落在过曝段：
/// 新鲜度 0..200 = 暗色→鲜色，200→220→240→255 = 鲜色→狙击老亮带色→狙击新亮带色→风暴闪光色。
/// 风暴闪光（LongFlash = 0）前 0.4 秒最亮、最后 0.4 秒退回鲜色；狙击亮带（LongFlash = 1）过曝 SniperFlashSeconds 秒，
/// 从新亮带色慢慢褪成老亮带色，最后退回鲜色。
/// </summary>
public sealed unsafe class TerritoryField
{
    public const int Size = 1000;
    public const int Total = Size * Size;
    public const byte Neutral = 255;

    public readonly byte[] Owner = new byte[Total];
    public readonly float[] PaintTime = new float[Total];
    public readonly byte[] LongFlash = new byte[Total];        // PaintTime 在未来时：1 = 狙击亮带（慢慢褪），0 = 风暴闪光
    public readonly int[] Count = new int[4];
    public readonly byte[] Pixels = new byte[Total * 4];

    /// <summary>Pixels 的 32 位视图（R 在低字节），数组已固定，可在并行循环里直接写。</summary>
    public readonly uint* Px;

    public Image Img;
    public ImageTexture Tex;
    public ImageTexture LutTex;                       // 256 × 4：新鲜度字节 → 各阵营颜色（给合成着色器）

    // 新鲜度查找表：年龄 1/32 s 一档，共 40 s（之后恒为 0）
    private const int AgeSteps = 1280;
    private const float AgeStepsPerSec = 32f;
    // 刚翻过来的像素先亮一下（视频实测：翻转后 0.25 s 已接近亮带色，1 s 回到鲜色），活跃的战线因此有一道亮边
    private const float NewBoost = 45f, NewTau = 0.33f;
    public const int BlurR = 4;                       // 盒式模糊半径（9×9，σ ≈ 2.6 像素；GPU 上做）
    public const int CodeLevels = 63;                 // A 通道打包码：owner·63 + q，q = 0..62；255 = 中立
    public const int NeutralCode = 255;
    private const int FreshLevel = 200;               // 新鲜度字节：0..200 暗→鲜，200..255 鲜→过曝（见 BuildLut）
    private const int OldLevel = 220, YoungLevel = 240;
    private const float FlashFadeSeconds = 0.4f;      // 风暴闪光最后 0.4 秒退回鲜色
    private const float LongStepsPerSec = 16f;        // 狙击亮带按剩余过曝时间查表：1/16 秒一档
    public const float FlashSeconds = 0.8f;           // 风暴闪光总时长（前 0.4 秒保持最亮）
    private const int Bands = 50;                     // 并行时按 20 行 / 20 列一条带切分

    private readonly byte[] _snap = new byte[Total];           // 清理碎点用的归属快照
    private readonly int[] _bandDelta = new int[Bands * 4];     // 清理碎点时每条带各阵营像素数的变化
    private readonly int[] _bandNeutral = new int[Bands * 4];   // 其中有几个原来是中立像素
    public readonly long[] SmoothNeutral = new long[4];         // 调试：清理碎点累计占掉的中立像素
    private static readonly int[] Dither = new int[16];      // 4×4 有序抖动（0..254），把 0..255 的新鲜度量化成 63 级时用
    private readonly byte[] _ageLut = new byte[AgeSteps];
    private byte[] _longLut;
    private readonly uint[] _lut = new uint[4 * 256];
    public readonly uint NeutralPacked;
    private GCHandle _pin;

    public static readonly Color NeutralColor = new Color(2f / 255, 10f / 255, 22f / 255);

    /// <summary>刚打下来的领土（视频实测：涂色后 1 秒时的颜色）。</summary>
    public static readonly Color[] FreshColors =
    {
        new Color(187f / 255, 27f / 255,  57f / 255),
        new Color(3f / 255,   145f / 255, 92f / 255),
        new Color(3f / 255,   132f / 255, 226f / 255),
        new Color(171f / 255, 101f / 255, 8f / 255),
    };

    /// <summary>风暴 / 狙击扫过时的闪光色：饱和的亮队色（视频实测狙击那条亮带红 ≈ (248,27,85)、绿 (20,250,195)、
    /// 蓝 (25,225,255)、黄 (255,235,22)；风暴扫过的蓝方领土 ≈ (9,233,253)）。</summary>
    public static readonly Color[] FlashColors =
    {
        new Color(250f / 255, 27f / 255,  90f / 255),
        new Color(15f / 255,  250f / 255, 195f / 255),
        new Color(15f / 255,  230f / 255, 255f / 255),
        new Color(255f / 255, 238f / 255, 20f / 255),
    };

    /// <summary>狙击亮带刚涂上（0.5 秒）的颜色（视频实测离中心 250 像素处）。</summary>
    public static readonly Color[] SniperYoungColors =
    {
        new Color(245f / 255, 25f / 255,  80f / 255),
        new Color(5f / 255,   241f / 255, 177f / 255),
        new Color(11f / 255,  207f / 255, 255f / 255),
        new Color(249f / 255, 215f / 255, 10f / 255),
    };

    /// <summary>狙击亮带过曝 3.4 秒后的颜色（视频实测；之后再退回鲜色）。</summary>
    public static readonly Color[] SniperOldColors =
    {
        new Color(235f / 255, 22f / 255,  69f / 255),
        new Color(15f / 255,  232f / 255, 161f / 255),
        new Color(9f / 255,   174f / 255, 247f / 255),
        new Color(241f / 255, 190f / 255, 12f / 255),
    };

    /// <summary>老领土最终沉到的颜色（视频里后方领土去掉粒子后的底色）。</summary>
    public static readonly Color[] DarkColors =
    {
        new Color(58f / 255, 0f,        14f / 255),
        new Color(0f,        26f / 255, 16f / 255),
        new Color(1f / 255,  12f / 255, 36f / 255),
        new Color(40f / 255, 17f / 255, 1f / 255),
    };

    public TerritoryField()
    {
        _pin = GCHandle.Alloc(Pixels, GCHandleType.Pinned);
        Px = (uint*)_pin.AddrOfPinnedObject();
        NeutralPacked = Pack(NeutralColor.R * 255f, NeutralColor.G * 255f, NeutralColor.B * 255f);
        int[] bayer = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };
        for (int k = 0; k < 16; k++) Dither[k] = (bayer[k] * 255 + 127) / 16;

        BuildLut();
        Reset();
        Compose(0f);
        Img = Image.CreateFromData(Size, Size, false, Image.Format.Rgba8, Pixels);
        Tex = ImageTexture.CreateFromImage(Img);

        var lutBytes = new byte[256 * 4 * 4];
        Buffer.BlockCopy(_lut, 0, lutBytes, 0, lutBytes.Length);
        LutTex = ImageTexture.CreateFromImage(Image.CreateFromData(256, 4, false, Image.Format.Rgba8, lutBytes));
    }

    ~TerritoryField()
    {
        if (_pin.IsAllocated) _pin.Free();
    }

    /// <summary>视频实测的变暗曲线：t ≤ hold 为 1，之后 (e^(−(t−hold)/τ) − floor)/(1 − floor)，夹到 0。</summary>
    public static float Freshness(float age)
    {
        var s = Main.Settings;
        if (age <= s.TerritoryFadeHold) return 1f;
        float e = MathF.Exp(-(age - s.TerritoryFadeHold) / s.TerritoryFadeTau);
        return MathF.Max(0f, (e - s.TerritoryFadeFloor) / (1f - s.TerritoryFadeFloor));
    }

    private void BuildLut()
    {
        for (int a = 0; a < AgeSteps; a++)
        {
            float age = a / AgeStepsPerSec;
            float v = Freshness(age) * FreshLevel;
            if (age < Main.Settings.TerritoryFadeHold) v += NewBoost * MathF.Exp(-age / NewTau);
            _ageLut[a] = (byte)Math.Clamp((int)(v + 0.5f), 0, 255);
        }

        // 狙击亮带：按剩余过曝时间 r 查新鲜度（已经过曝了 a = D − r 秒）：前 0.5 秒 250 → 240（新亮带色），
        // 3.4 秒褪到 220（老亮带色），到 D 秒退回 200（鲜色）
        float D = Main.Settings.SniperFlashSeconds;
        _longLut = new byte[(int)(D * LongStepsPerSec) + 2];
        for (int k = 0; k < _longLut.Length; k++)
        {
            float a = MathF.Max(D - k / LongStepsPerSec, 0f);
            float v = a <= 0.5f ? 250f - 20f * a
                : a <= 3.4f ? YoungLevel - (YoungLevel - OldLevel) * (a - 0.5f) / 2.9f
                : OldLevel - (OldLevel - FreshLevel) * (a - 3.4f) / MathF.Max(D - 3.4f, 0.1f);
            _longLut[k] = (byte)Math.Clamp((int)(v + 0.5f), FreshLevel, 255);
        }

        for (int t = 0; t < 4; t++)
        {
            Color fresh = FreshColors[t], dark = DarkColors[t], flash = FlashColors[t];
            Color old = SniperOldColors[t], young = SniperYoungColors[t];
            for (int v = 0; v < 256; v++)
            {
                Color c = v <= FreshLevel ? dark.Lerp(fresh, v / (float)FreshLevel)
                    : v <= OldLevel ? fresh.Lerp(old, (v - FreshLevel) / (float)(OldLevel - FreshLevel))
                    : v <= YoungLevel ? old.Lerp(young, (v - OldLevel) / (float)(YoungLevel - OldLevel))
                    : young.Lerp(flash, (v - YoungLevel) / (255f - YoungLevel));
                _lut[t * 256 + v] = Pack(c.R * 255f, c.G * 255f, c.B * 255f);
            }
        }
    }

    public static uint Pack(float r, float g, float b)
    {
        uint R = (uint)Math.Clamp((int)(r + 0.5f), 0, 255);
        uint G = (uint)Math.Clamp((int)(g + 0.5f), 0, 255);
        uint B = (uint)Math.Clamp((int)(b + 0.5f), 0, 255);
        return R | (G << 8) | (B << 16) | 0xFF000000u;
    }

    public void Reset()
    {
        Array.Fill(Owner, Neutral);
        Array.Fill(PaintTime, -1000f);
        Array.Clear(LongFlash);
        Array.Clear(Count);
    }

    /// <summary>占领一个像素。返回 true 表示真正换了主人（调用方按此扣数值）。longFlash = 1：狙击亮带（time 在未来）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Paint(int i, int team, float time, byte longFlash = 0)
    {
        byte prev = Owner[i];
        if (prev == team) return false;
        if (prev != Neutral) Count[prev]--;
        Owner[i] = (byte)team;
        Count[team]++;
        PaintTime[i] = time;
        LongFlash[i] = longFlash;
        return true;
    }

    /// <summary>过曝（风暴、狙击扫过自家领土）：圆盘内己方像素的 PaintTime 推到 until（未来时刻），longFlash 同 Paint。</summary>
    public void FlashDisc(float cx, float cy, float radius, int team, float until, byte longFlash = 0)
    {
        int x0 = Math.Max(0, (int)MathF.Floor(cx - radius));
        int x1 = Math.Min(Size - 1, (int)MathF.Floor(cx + radius));
        int y0 = Math.Max(0, (int)MathF.Floor(cy - radius));
        int y1 = Math.Min(Size - 1, (int)MathF.Floor(cy + radius));
        float r2 = radius * radius;
        for (int y = y0; y <= y1; y++)
        {
            float dy = y + 0.5f - cy;
            int row = y * Size;
            for (int x = x0; x <= x1; x++)
            {
                float dx = x + 0.5f - cx;
                if (dx * dx + dy * dy > r2) continue;
                int i = row + x;
                if (Owner[i] == team && PaintTime[i] < until) { PaintTime[i] = until; LongFlash[i] = longFlash; }
            }
        }
    }

    /// <summary>
    /// 圆盘里有没有不属于 team 的像素：只看圆心和周围 8 个方向、距离 d 的 9 个点（出界的点不算）。
    /// 子弹飞在自家领土边上、或者从没涂到的小洞旁边飞过时也会发现，顺手补上。
    /// </summary>
    public bool AnyForeign(float cx, float cy, float d, int team)
    {
        byte[] o = Owner;
        int x = (int)cx, y = (int)cy;
        if (o[y * Size + x] != team) return true;
        int a = (int)(d + 0.5f), b = (int)(d * 0.7071f + 0.5f);
        return Foreign(o, x - a, y, team) || Foreign(o, x + a, y, team) ||
               Foreign(o, x, y - a, team) || Foreign(o, x, y + a, team) ||
               Foreign(o, x - b, y - b, team) || Foreign(o, x + b, y - b, team) ||
               Foreign(o, x - b, y + b, team) || Foreign(o, x + b, y + b, team);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Foreign(byte[] o, int x, int y, int team) =>
        (uint)x < Size && (uint)y < Size && o[y * Size + x] != team;

    /// <summary>
    /// 子弹涂色：半径 radius（≤ DiscMaxR − 1）的圆盘内把非己方像素改成己方，敌方像素每个花 1 点、中立像素每个花
    /// neutralCost 点，花到 budget 为止（最后一个像素可以超一点）。返回花掉的点数；neutral = 其中占了几个中立像素。
    /// 按离圆心从近到远的顺序涂：钱不够时涂出来的是一个小一点的圆（贴着已经涂好的地方长出去），
    /// 不会像逐行扫描那样只涂半个圆盘、留下一截截碎边和黑缝。
    /// </summary>
    public float PaintDisc(float cx, float cy, float radius, int team, float budget, float neutralCost, float time, out int neutral, byte longFlash = 0)
    {
        neutral = 0;
        if (budget <= 0f) return 0f;
        int ix = (int)cx, iy = (int)cy;
        float fx = cx - ix - 0.5f, fy = cy - iy - 0.5f;          // 圆心相对所在像素中心的偏移（≤ 0.71）
        float r2 = radius * radius;
        float lim = (radius + 0.75f) * (radius + 0.75f);        // 偏移表按整数距离排序，超过这个距离就不可能在圆里了
        int[] dxs = DiscDx, dys = DiscDy, d2s = DiscD2;
        float spent = 0f;
        for (int k = 0; k < d2s.Length && d2s[k] <= lim; k++)
        {
            int dx = dxs[k], dy = dys[k];
            float ex = dx - fx, ey = dy - fy;
            if (ex * ex + ey * ey > r2) continue;
            int x = ix + dx, y = iy + dy;
            if ((uint)x >= Size || (uint)y >= Size) continue;
            int i = y * Size + x;
            byte prev = Owner[i];
            if (!Paint(i, team, time, longFlash)) continue;
            if (prev == Neutral) { neutral++; spent += neutralCost; }
            else spent += 1f;
            if (spent >= budget) return spent;
        }
        return spent;
    }

    /// <summary>
    /// 清理碎点：按快照做一遍 3×3 多数滤波（并行）。周围 8 格里某个阵营占 ≥5 格 → 改成那个阵营。
    /// 领土里没涂到的黑点（中立像素）、别家留下的零星碎点都会被抹掉，边上的小毛刺、小缺口也会磨圆；
    /// 只会改成阵营色，不会把像素改回中立。改过的像素亮度取周围那几格的平均，看不出接缝。
    /// </summary>
    public void Smooth()
    {
        Buffer.BlockCopy(Owner, 0, _snap, 0, Total);
        Array.Clear(_bandDelta);
        Array.Clear(_bandNeutral);
        Parallel.For(0, Bands, band =>
        {
            const int rows = Size / Bands;
            byte[] s = _snap, owner = Owner, lng = LongFlash;
            float[] paint = PaintTime;
            int[] delta = _bandDelta, neut = _bandNeutral;
            Span<int> nb = stackalloc int[8];
            Span<byte> nf = stackalloc byte[8];
            Span<int> cnt = stackalloc int[4];
            for (int y = band * rows, y1 = y + rows; y < y1; y++)
            {
                int r = y * Size;
                int ru = (y > 0 ? y - 1 : 0) * Size;                  // 出界按自己算（贴边的像素不容易被改）
                int rd = (y < Size - 1 ? y + 1 : Size - 1) * Size;
                for (int x = 0; x < Size; x++)
                {
                    int o = s[r + x];
                    int xl = x > 0 ? x - 1 : 0, xr = x < Size - 1 ? x + 1 : Size - 1;
                    // 上下左右都同色 → 同色至少 4 格，不用改（绝大多数像素走这里）
                    if (s[r + xl] == o && s[r + xr] == o && s[ru + x] == o && s[rd + x] == o) continue;

                    nb[0] = ru + xl; nb[1] = ru + x; nb[2] = ru + xr; nb[3] = r + xl;
                    nb[4] = r + xr; nb[5] = rd + xl; nb[6] = rd + x; nb[7] = rd + xr;
                    cnt.Clear();
                    for (int k = 0; k < 8; k++)
                    {
                        int v = s[nb[k]];
                        if (v != o && v < 4) cnt[v]++;
                    }
                    int m = 0;
                    for (int t = 1; t < 4; t++) if (cnt[t] > cnt[m]) m = t;
                    if (cnt[m] < 5) continue;

                    // 过曝的种类按那几格的多数算，亮度只平均同一种的（风暴闪光和狙击亮带的 PaintTime 不能混着平均）
                    int lit = 0;
                    for (int k = 0; k < 8; k++) { nf[k] = lng[nb[k]]; if (s[nb[k]] == m) lit += nf[k]; }
                    byte lf = (byte)(lit * 2 > cnt[m] ? 1 : 0);
                    float sum = 0f;
                    int n = 0;
                    for (int k = 0; k < 8; k++) if (s[nb[k]] == m && nf[k] == lf) { sum += paint[nb[k]]; n++; }
                    owner[r + x] = (byte)m;
                    paint[r + x] = sum / n;
                    lng[r + x] = lf;
                    if (o != Neutral) delta[band * 4 + o]--;
                    else neut[band * 4 + m]++;
                    delta[band * 4 + m]++;
                }
            }
        });
        for (int i = 0; i < _bandDelta.Length; i++)
        {
            Count[i & 3] += _bandDelta[i];
            SmoothNeutral[i & 3] += _bandNeutral[i];
        }
    }

    // 涂色圆盘的偏移表：半径 DiscMaxR 以内的整数偏移，按离圆心从近到远排好（同距离按角度）
    private const int DiscMaxR = 16;
    private static readonly int[] DiscDx, DiscDy, DiscD2;

    static TerritoryField()
    {
        var list = new System.Collections.Generic.List<(int d2, float ang, int dx, int dy)>();
        for (int dy = -DiscMaxR; dy <= DiscMaxR; dy++)
            for (int dx = -DiscMaxR; dx <= DiscMaxR; dx++)
            {
                int d2 = dx * dx + dy * dy;
                if (d2 <= DiscMaxR * DiscMaxR) list.Add((d2, MathF.Atan2(dy, dx), dx, dy));
            }
        list.Sort((a, b) => a.d2 != b.d2 ? a.d2.CompareTo(b.d2) : a.ang.CompareTo(b.ang));
        DiscDx = new int[list.Count];
        DiscDy = new int[list.Count];
        DiscD2 = new int[list.Count];
        for (int k = 0; k < list.Count; k++)
            (DiscD2[k], _, DiscDx[k], DiscDy[k]) = list[k];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte OwnerAt(int x, int y)
    {
        if ((uint)x >= Size || (uint)y >= Size) return Neutral;
        return Owner[y * Size + x];
    }

    public bool OwnsAll(int team) => Count[team] >= Total;

    /// <summary>
    /// 准备每帧上传的画布（并行一遍）：A = 归属 + 新鲜度打包码，RGB 清零（之后往上加护盾环、粒子和子弹）。
    /// 新鲜度 0..255 用 4×4 有序抖动量化成 63 级：GPU 上 9×9 平均后抖动抵消，看不出色阶。
    /// 模糊、查色、球芯压暗都在 TerritoryCompose.gdshader 里做。
    /// </summary>
    public void Compose(float now)
    {
        Parallel.For(0, Bands, band =>
        {
            const int rows = Size / Bands;
            const float flashPerSec = (255 - FreshLevel) / FlashFadeSeconds;
            byte[] owner = Owner, ageLut = _ageLut, lng = LongFlash, longLut = _longLut;
            float[] paint = PaintTime;
            int[] dither = Dither;
            uint* px = Px;
            for (int y = band * rows, y1 = y + rows; y < y1; y++)
            {
                int row = y * Size, drow = (y & 3) * 4;
                for (int x = 0; x < Size; x++)
                {
                    int i = row + x;
                    int o = owner[i];
                    if (o == Neutral) { px[i] = (uint)NeutralCode << 24; continue; }
                    float age = now - paint[i];
                    int v;
                    if (age >= 0f)
                    {
                        int a = (int)(age * AgeStepsPerSec);
                        v = a < AgeSteps ? ageLut[a] : 0;
                    }
                    else if (lng[i] == 0)
                    {
                        v = FreshLevel + (int)(-age * flashPerSec);
                        if (v > 255) v = 255;
                    }
                    else
                    {
                        int a = (int)(-age * LongStepsPerSec);
                        v = longLut[a < longLut.Length ? a : longLut.Length - 1];
                    }
                    int q = (v * (CodeLevels - 1) + dither[drow + (x & 3)]) / 255;
                    px[i] = (uint)(o * CodeLevels + q) << 24;
                }
            }
        });
    }

    /// <summary>逐通道饱和加色（A 通道是领土打包码，原样保留）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint AddSat(uint a, uint b)
    {
        uint r = (a & 0xFFu) + (b & 0xFFu);
        uint g = ((a >> 8) & 0xFFu) + ((b >> 8) & 0xFFu);
        uint bl = ((a >> 16) & 0xFFu) + ((b >> 16) & 0xFFu);
        if (r > 255u) r = 255u;
        if (g > 255u) g = 255u;
        if (bl > 255u) bl = 255u;
        return r | (g << 8) | (bl << 16) | (a & 0xFF000000u);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(int x, int y, uint c)
    {
        if ((uint)x >= Size || (uint)y >= Size) return;
        uint* p = Px + y * Size + x;
        *p = AddSat(*p, c);
    }

    /// <summary>把整块画布推给 GPU（每帧一次）。</summary>
    public void Upload()
    {
        Img.SetData(Size, Size, false, Image.Format.Rgba8, Pixels);
        Tex.Update(Img);
    }
}
