using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;

/// <summary>
/// 右侧领土战场（1000×1000，节点放在屏幕 (840,40)）。
///
/// 规则（原始需求见 docs/original-brief.txt）：
///   · 四个中心各自吸收自家领土产出的粒子：九成进机枪火力、一成进粒子充能；领土越大粒子越多，吸收不减领土。
///     大球吸进的粒子让球变大，同时给球所属的阵营充能（按要求加的，见 GameSettings.BallParticleChargeShare）。
///   · 充能满 480K（之后每次 +240K）→ 额外弹珠 +1，左侧顶部生成一颗同色 16 值弹珠。
///   · 开局（视频实测）：0 秒狙击 +1M，四束激光射向场地正中间对撞；1.8 秒机枪 +500K；4.8 秒护盾 +25M。
///   · 机枪一直开火、顺时针旋转；敌方子弹/大球靠近时才转去拦截（点防）。每步打一颗，每颗 = 当前火力的 0.03%
///     （能涂多少像素）。子弹不会过期：一直飞、碰边反弹，直到在中立/敌方领土上涂完，或者打在护盾/大球/敌方子弹上。
///   · 左侧槽：机枪/护盾直接加数值；狙击 = 先在中心周围乱飞、再沿机枪方向收拢飞出的一束子弹（激光）；
///     风暴 = 从中心向外分三圈打出的子弹环；大球 = 沿机枪方向发射的大球。所有加成都在中心上方弹通知。
///   · 大球/护盾互撞：双方各扣较小者的 80%（视频实测，和撞击角度无关），然后弹开。
///   · 护盾吸收敌方子弹/大球伤害；护盾破了之后中心再被打中 → 阵营灭亡，机枪火力和左侧每颗弹珠各变成一个大球飞出。
///   · 一种颜色占满全场 → 游戏结束，8 秒后重开（R 立即重开）。
///
/// 渲染：每帧在 CPU 上合成「领土底色 + 加色粒子拖尾 + 子弹光点」，一次上传成贴图；
/// 护盾环、大球光圈等发光用加色混合的子节点画，文字和图标再叠一层普通子节点。
/// </summary>
public partial class RightArena : Node2D
{
    public const int N = TerritoryField.Size;
    private const float Step = 1f / 60f;
    private const int SmoothEvery = 6;                        // 每 6 步（0.1 秒）清理一遍领土里的碎点
    private const float CenterHitRadius = 12f;
    // 大球互相重叠时每秒最多推开这么多像素：新发射的球一出来就压在旁边的球上、阵营灭亡时弹出的球挤在一起时，
    // 以前一步就整个推开（一帧跳十几到几十像素），现在几帧里滑开
    private const float BallSepSpeed = 360f;
    private const float AbsorbRadius = 8f;
    private const float BulletCollideDist = 4f;
    // 风暴子弹和别的子弹的碰撞距离放大到一格：点防弹 + 风暴弹迎面一步要相对走 12 px，4 px 的判定会互相穿过去；
    // 放大以后点防 / 机枪弹扫过风暴环（远处相邻两颗约 9 px）基本都能撞上一颗（≤ Cell，九宫格查得到）
    private const float StormCollideDist = Cell;
    private const float StormThreatMargin = 6f;           // 风暴子弹直线飞下去离护盾不到这么远就算会撞上（点防管它）
    private const float PdTurnRate = 3f * Mathf.Pi;       // 点防时炮台转得很快
    private const float PdAimTolerance = 0.26f;           // 对准 ±15° 以内才用点防射速
    private const float StormStartRadius = 10f;
    private const float BigBallRefreshAge = 0.5f;         // 大球碾过自家领土也刷成新鲜色（视频里整条轨迹一样亮）

    /// <summary>四个中心（场内坐标，视频实测）。0 红左上 1 绿右上 2 蓝左下 3 黄右下。</summary>
    public static readonly Vector2[] Centers =
    {
        new Vector2(129f, 129f), new Vector2(869f, 129f),
        new Vector2(129f, 869f), new Vector2(869f, 869f),
    };

    /// <summary>护盾环 / 炮管 / 大球光圈的亮色（视频实测）。</summary>
    public static readonly Color[] RingColors =
    {
        new Color(255f / 255, 31f / 255,  74f / 255),
        new Color(45f / 255,  247f / 255, 178f / 255),
        new Color(73f / 255,  210f / 255, 255f / 255),
        new Color(255f / 255, 198f / 255, 79f / 255),
    };

    /// <summary>狙击激光路径线的颜色（视频实测亮带中间那条线：红方是偏紫的粉红，绿、蓝方青色，黄方柠檬黄）。</summary>
    private static readonly Color[] LaserColors =
    {
        new Color(255f / 255, 30f / 255,  175f / 255),
        new Color(0f,         250f / 255, 245f / 255),
        new Color(20f / 255,  250f / 255, 255f / 255),
        new Color(255f / 255, 248f / 255, 15f / 255),
    };

    private static GameSettings S => Main.Settings;

    public TerritoryField Field { get; private set; }
    public readonly Faction[] Factions = new Faction[Main.TeamCount];
    public readonly List<BigBall> Balls = new();
    public int Winner { get; private set; } = -1;
    public float SimTime => _time;

    /// <summary>调试：--events 时把大额注入、破盾、灭亡、大球发射打印出来。</summary>
    public static bool LogEvents;
    /// <summary>调试：>0 时每隔这么多模拟秒打 [bl] 大球位置 / 速度（--balllog，查大球卡角落用）。</summary>
    public static float BallLogEvery;
    private float _nextBallLog;
    private void Ev(string msg) { if (LogEvents) GD.Print($"[ev] t={_time:0.0} {msg}"); }
    public int ParticleCount => _partCount;
    public int BulletCount => _bulletCount;

    // ── 分段计时（--prof）：每段累计耗时，PrintStats 时按帧平均输出 ──
    public static bool Prof;
    private static readonly string[] ProfNames =
        { "turret", "laser", "bullet", "collide", "ball", "part", "compose", "shield", "ballR", "partR", "bulletR", "upload", "glow", "top", "smooth" };
    private readonly long[] _prof = new long[ProfNames.Length];
    // 别的节点的耗时（左侧绘制 / 左侧物理 / 颜色球绘制 / 钉子绘制 / HUD 绘制），各自用 Stopwatch 累加
    public static readonly long[] ProfX = new long[5];
    private static readonly string[] ProfXNames = { "leftD", "leftP", "ballD", "pegD", "hudD" };
    private long _profT;
    private int _profFrames, _profSteps;
    private void ProfStart() { if (Prof) _profT = Stopwatch.GetTimestamp(); }
    private void ProfMark(int slot)
    {
        if (!Prof) return;
        long now = Stopwatch.GetTimestamp();
        _prof[slot] += now - _profT;
        _profT = now;
    }
    public string ProfReport()
    {
        if (_profFrames == 0) return "";
        var sb = new System.Text.StringBuilder($"[prof] frames={_profFrames} steps/frame={_profSteps / (double)_profFrames:0.0} ms/frame:");
        double total = 0;
        for (int i = 0; i < _prof.Length; i++)
        {
            double ms = _prof[i] * 1000.0 / Stopwatch.Frequency / _profFrames;
            total += ms;
            sb.Append(CultureInfo.InvariantCulture, $" {ProfNames[i]}={ms:0.00}");
            _prof[i] = 0;
        }
        sb.Append(CultureInfo.InvariantCulture, $" | sum={total:0.00} |");
        for (int i = 0; i < ProfX.Length; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $" {ProfXNames[i]}={ProfX[i] * 1000.0 / Stopwatch.Frequency / _profFrames:0.00}");
            ProfX[i] = 0;
        }
        _profFrames = _profSteps = 0;
        return sb.ToString();
    }

    // ── 调试（--radial）：每队粒子按离自家中心的距离分段的真实密度 ──
    public static bool Radial;
    private static readonly float[] RadialBins = { 0, 60, 120, 180, 250, 320, 400, 500, 650 };
    private static int RadialBin(float dx, float dy)
    {
        float d = MathF.Sqrt(dx * dx + dy * dy);
        for (int b = 0; b + 1 < RadialBins.Length; b++)
            if (d < RadialBins[b + 1]) return b;
        return -1;
    }

    /// <summary>每段：粒子数 / 千个自家领土像素、平均亮度系数（淡入淡出之后）、平均年龄（秒）、自家像素数（千）。</summary>
    public string RadialReport()
    {
        int nb = RadialBins.Length - 1;
        var pix = new long[4 * nb];
        var cnt = new long[4 * nb];
        var lit = new double[4 * nb];
        var age = new double[4 * nb];
        byte[] owner = Field.Owner;
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                int o = owner[y * N + x];
                if (o >= 4) continue;
                int b = RadialBin(x + 0.5f - Centers[o].X, y + 0.5f - Centers[o].Y);
                if (b >= 0) pix[o * nb + b]++;
            }
        for (int i = 0; i < _partCount; i++)
        {
            ref Particle p = ref _parts[i];
            int b = RadialBin(p.X - Centers[p.Team].X, p.Y - Centers[p.Team].Y);
            if (b < 0) continue;
            float fade = p.Bright;
            if (p.Life < 1f) fade *= p.Life;
            if (p.Age < 0.3f) fade *= p.Age * (1f / 0.3f);
            int k = p.Team * nb + b;
            cnt[k]++;
            lit[k] += fade;
            age[k] += p.Age;
        }
        var sb = new System.Text.StringBuilder();
        for (int t = 0; t < 4; t++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"[radial] {t}:");
            for (int b = 0; b < nb; b++)
            {
                int k = t * nb + b;
                if (pix[k] < 500) { sb.Append(" | -"); continue; }
                double n = cnt[k] * 1000.0 / pix[k];
                double m = cnt[k] > 0 ? lit[k] / cnt[k] : 0, a = cnt[k] > 0 ? age[k] / cnt[k] : 0;
                sb.Append(CultureInfo.InvariantCulture, $" | {n:0.0} {m:0.00} {a:0.0}s {pix[k] / 1000.0:0}k");
            }
            if (t < 3) sb.Append('\n');
        }
        return sb.ToString();
    }

    // ── 子弹 ────────────────────────────────────────────────────────────────────
    private enum Kind : byte { Mg, Pd, Sniper, Storm }

    private struct Bullet
    {
        public float X, Y, VX, VY;
        public float Value, Life;       // Life ≤ 0 表示已被消耗/散掉，等待压缩（普通子弹活着时一直是 1；风暴子弹是剩余寿命，秒）
        public float Dmg;               // 按读数实际少的那份算的数值 = Value × Dmg：打护盾、打大球、子弹互撞都用它，涂色只花 Value。
                                        // 普通机枪每颗的上限只管涂色（见 StepTurret）；其余 1
        public float Age;               // 拖尾长度用的飞行时间（碰墙、被弹开时清零）
        public float T;                 // 出膛后总共飞了多久
        public int Team;
        public Kind Kind;
        public byte Fold;               // 狙击：碰左右墙 / 上下墙次数的奇偶（bit0 / bit1）
        public short Group;             // 狙击子弹所属的激光（_lasers 下标），其余为 −1
        public float SlotO, SlotL;      // 狙击队形里的位置：横向偏移 / 落后激光头的距离
        public float Spin;              // 狙击出膛前乱飞时的转弯角速度（rad/s，正 = 屏幕顺时针）
        public float Orbit;             // 狙击出膛后转弯的曲率 1/半径（+ 屏幕顺时针、− 逆时针）
        public float Cruise, Fast;      // 狙击：出膛前乱飞的速度 / 出膛后打转的速度
        public float Catch;             // 狙击：追队形时的最快速度
        public float Thick;             // 狙击：出膛后涂的细线有多粗的随机系数 q²（0..1，每只苍蝇不一样）
        public short Fid;               // 狙击：在这束激光里是第几只（画淡拖尾用）
        public byte Lane;               // 狙击：在第几条细线上（同一条线一起摆动）
        public byte Stage;              // 狙击：0 出膛前乱飞、1 出膛后甩大弧线、2 直线追自己的队形位置、3 已归队
    }

    private Bullet[] _bullets = new Bullet[16384];
    private int _bulletCount;

    private const int Cell = 8;
    private const int GridW = N / Cell;
    private readonly int[] _gridHead = new int[4 * GridW * GridW];   // 每个阵营一张网格
    private int[] _gridNext = new int[16384];

    // 点防威胁扫描结果（每步刷新）
    private readonly int[] _harvest = new int[4];                    // 这一步各阵营占下的中立像素
    public readonly long[] HarvestTotal = new long[4];               // 调试：累计收成（像素）
    public double BallIntake;                                         // 调试：所有大球累计吸进的粒子数
    private readonly double[] _harvestPool = new double[4];          // 收成的两级缓冲（还没到账的像素数）
    private readonly double[] _harvestFlow = new double[4];
    // 充能加成（破盾 / 淘汰，见 GameSettings.ShieldBreakCharge）：每队一条队列，排头那笔按缓动曲线一点点到账
    private sealed class ChargeBonus { public double Amount, Given; public float Wait, Age; }
    private readonly List<ChargeBonus>[] _chargeBonus = { new(), new(), new(), new() };
    private int _smoothTick;
    private readonly int[] _threatSpecial = new int[4];
    private readonly int[] _threatNormal = new int[4];

    // ── 狙击激光 ────────────────────────────────────────────────────────────────
    private sealed class Laser
    {
        public int Team;
        public Vector2 Origin;                        // 发射中心
        public Vector2 Head, Dir;                     // 激光头（出膛后才动，碰边反弹）：队形的锚点
        public Vector2 Tip;                           // 画出来的激光头（白光、路径线的末端）：撞上敌方护盾时停在护盾环上，护盾破了再追上 Head
        public Vector2? Target;                       // 出膛时朝这里飞（开局那束射向场地正中间）；没有就沿当时的机枪方向
        public float Age, FlyTime;
        public float Glow;                            // 路径线现在的亮度（跟着归队比例走，慢半拍）
        public bool Launched, Ended;
        public int Fold;                              // 激光头碰墙次数的奇偶（同 Bullet.Fold）
        public int Lanes;
        // 每条细线的横向摆动 = 沿路径距离 s 的三个正弦之和（按距离而不是时间摆，同一条线上的子弹才走同一条弯线）
        public const int MaxLanes = 32;
        public readonly float[] WobA = new float[MaxLanes * 3], WobK = new float[MaxLanes * 3], WobP = new float[MaxLanes * 3];
        public float Fade = 1f;                       // 结束后路径细线淡出
        public int Count, Near, Joined;               // 上一步还活着的子弹 / 其中贴着激光头的 / 已经归队的
        public double Value;                          // 上一步整束还剩多少数值
        public ulong Clashed;                         // 已经和哪些激光（槽号）撞过（每对只炸一次光）
        // 出膛前苍蝇身后的淡拖尾：每只最近 GLen 步的位置（环形缓冲，Gs = 第几步写的）
        public float[] Gx, Gy;
        public int[] Gs;
        public int GStep, GLen, GFlies;
        // 路径线：起点、各反弹点，激光头每走 8 像素再记一个点，连同当时的亮度（归队了多少）和时刻；画线时再接上激光头
        public readonly List<Vector2> Path = new();
        public readonly List<float> PathA = new(), PathT = new();
    }
    private readonly Laser[] _lasers = new Laser[64];

    // ── 粒子 ────────────────────────────────────────────────────────────────────
    private struct Particle
    {
        public float X, Y, VX, VY, Life, Age;
        public float Bright;            // 亮度倍率（视频里各点亮度不一）
        public float F, Fc, Fs;         // 自己的速度倍率 F，和 F·cos / F·sin（向心方向转过的角度 = 绕行角 + 自己的偏角）
        public int Team;
    }

    private Particle[] _parts;
    private int _partCount;
    private float _spawnCarry;
    private const int PartChunk = 8192;
    private const int MaxBallSnap = 24;
    private int[] _chunkKeep = new int[64];
    private int[] _chunkAbsorb = new int[64 * 4];
    private double[] _chunkBallGain = new double[64 * MaxBallSnap];
    private int[] _chunkBallTeam = new int[64 * MaxBallSnap * 4];
    private readonly float[] _snapX = new float[MaxBallSnap];
    private readonly float[] _snapY = new float[MaxBallSnap];
    private readonly float[] _snapR = new float[MaxBallSnap];
    private readonly float[] _snapG2 = new float[MaxBallSnap];
    private readonly BigBall[] _snapBall = new BigBall[MaxBallSnap];

    // ── 杂项 ────────────────────────────────────────────────────────────────────
    private float _time;
    private float _acc;
    private float _overTimer;
    private int _opening;                  // 开局事件进行到第几步（狙击 → 机枪 → 护盾）
    private uint _rng;
    private Main _main;
    private Node2D _glow;
    private Node2D _top;
    // 狙击激光的路径线单独画进一张 1000×1000 的加色图，合成着色器把它加在领土上、大球下面（按要求：拖尾不压在大球上）
    private SubViewport _laserVp;
    private Node2D _laserLayer;

    // GPU 领土合成：1000×1000 的 SubViewport 里一张铺满的 ColorRect，着色器读 Field.Tex（加色层 + 打包码）合成最终画面
    private SubViewport _composeVp;
    private ShaderMaterial _composeMat;
    private const int MaxShaderBalls = 64;
    private readonly Vector4[] _ballUniform = new Vector4[MaxShaderBalls];
    private readonly StyleBoxFlat[] _mgBox = new StyleBoxFlat[4];
    private readonly StyleBoxFlat[] _shieldBox = new StyleBoxFlat[4];

    private const int PartLevels = 32;
    private readonly uint[] _partLut = new uint[4 * PartLevels * 3];   // 阵营 × 亮度档 × (中心/边/角)
    private readonly uint[] _bulletLut = new uint[4 * 4 * 25];         // 种类 × 阵营 × 5×5 弹头核
    private readonly float[] _trailCol = new float[4 * 4 * 3];         // 种类 × 阵营 × RGB（拖尾头部亮度）
    private readonly float[] _ringProfile = new float[(RingIn + RingOut) * RingLutPerPx + 1];

    // 大球径向亮度（视频实测，d = 到球心距离 − 半径）：
    //   球芯把领土压到 ~6% 亮度；环峰值 = 环色；向内按 (1 + d/w)² 暗下去，w = 半径的一半；
    //   峰带约 4 像素宽。视频里环外还有一圈 ~9 像素指数衰减的光晕，叠在领土上像一层更亮的滤镜，
    //   按用户要求去掉了，环外只留 1 像素的抗锯齿边
    private const int BallLutPerPx = 4;
    private const float BallEdge = 2f;
    private const float BallCoreKeep = 0.06f;
    private float[] _ballProfile = new float[1024];

    // 护盾环径向亮度（视频实测，d = r − R，峰值 = 1；环内侧的光晕比外侧拖得长）
    private const int RingIn = 48, RingOut = 36, RingLutPerPx = 4;
    private const float RingGain = 0.9f;
    private static readonly float[] RingProfileD =
        { -48f, -31.5f, -28.5f, -25.5f, -22.5f, -19.5f, -16.5f, -13.5f, -10.5f, -7.5f, -4.5f, -1.5f, 0f,
          1.5f, 4.5f, 7.5f, 10.5f, 13.5f, 16.5f, 19.5f, 22.5f, 25.5f, 28.5f, 31.5f, 36f };
    private static readonly float[] RingProfileV =
        { 0f, 0.07f, 0.08f, 0.10f, 0.12f, 0.16f, 0.21f, 0.26f, 0.40f, 0.59f, 0.83f, 1f, 1f,
          0.90f, 0.48f, 0.31f, 0.22f, 0.17f, 0.13f, 0.10f, 0.07f, 0.05f, 0.04f, 0.03f, 0f };

    private struct Shock
    {
        public Vector2 Pos;
        public float Life, MaxLife, R0, R1, Width;
        public Color Col;
        public bool Flash;              // 一团发光圆盘（阵营灭亡时的白光），不是往外扩的环
        public bool Bloom;              // 激光对撞的一大团青白光（BloomAmp）
        public bool Linear;             // 环匀速扩张（默认先快后慢）
        public float Delay;             // 过这么久才出现
    }
    private readonly List<Shock> _shocks = new();

    // 复活特效（叠加混合画在光晕层）：中心一团金白光、一圈慢慢转的光芒、两道往外扩的环、一把往外溅的火星；
    // 只画在场地里面（中心在角上，大环和长光芒不往场地外面漏光）
    private sealed class Revive
    {
        public Vector2 Pos;
        public float Age, Spin;
        public int Team;
        public float[] RayLen, SparkAng, SparkSpeed, SparkLife;
    }
    private readonly List<Revive> _revives = new();
    private const float ReviveLife = 1.8f;
    private const int ReviveRays = 16, ReviveSparks = 48;
    private static readonly Color ReviveGold = new(1f, 0.86f, 0.52f);

    // 复活传送：左侧最大的那颗弹珠原地化成一团光往里一收，变成一颗拖着长尾巴的光球，沿一道浅弧飞到自己阵营中心，
    // 落地才有护盾（ReviveArrive：复活爆光、护盾环从中心长出来）。叠加混合，画在最上面一层（盖得住左侧弹珠）
    private sealed class Warp
    {
        public Vector2 From, Ctrl;          // 场内坐标（起点在左侧弹珠台，x 是负的）；Ctrl 是二次贝塞尔弧的控制点
        public float Age, R, Fly, Spin;     // R 弹珠半径；Fly 飞行时间（按距离）
        public double Value;
        public int Team;
        public bool Arrived;
        public float[] DustU, DustLife;     // 一路撒下的金色光点：飞到进度 DustU 时撒下，往 DustVel 飘
        public Vector2[] DustVel;
    }
    private readonly List<Warp> _warps = new();
    private Node2D _warpLayer;
    private GradientTexture2D _softDot;      // 白色软光点（中心实、往外淡到 0），叠加混合画光团
    private const float WarpGather = 0.18f;  // 化光往里收
    private const float WarpTail = 0.3f;     // 尾巴 = 光球最近这么多飞行进度（占整段飞行时间的比例）走过的那段
    private const float WarpFade = 0.6f;     // 落地以后尾巴收进中心、光点淡掉，再留这么久
    private const int WarpDust = 28, WarpSeg = 20;
    private readonly List<Vector2> _arcBuf = new();
    private readonly Vector2[] _tri = new Vector2[3], _quad = new Vector2[4];
    private readonly Color[] _triC = new Color[3], _quadC = new Color[4];

    // 碰撞火花：狙击子弹撞上点防 / 机枪子弹（对消）的地方放一个 0.3 秒的小光点，叠加混合（环形缓冲，满了就覆盖最早的）；
    // 点防一直拦截来袭残余的地方几十个叠在一起，就是视频 8~10 秒护盾外那一大团白光
    private const int SparkMax = 512;
    private const float SparkLife = 0.3f;
    private readonly Vector2[] _sparkPos = new Vector2[SparkMax];
    private readonly float[] _sparkT = new float[SparkMax];
    private int _sparkNext, _sparkCount, _sparkLogTick;

    // ════════════════════════════════════════════════════════════════════════════
    //  生命周期
    // ════════════════════════════════════════════════════════════════════════════

    public override void _Ready()
    {
        _main = GetParent() as Main;
        _rng = (uint)GD.Randi() | 1u;

        Field = new TerritoryField();
        _parts = new Particle[S.MaxParticles];

        for (int t = 0; t < Main.TeamCount; t++)
        {
            Vector2 toMid = new Vector2(N * 0.5f, N * 0.5f) - Centers[t];
            float ang = toMid.Angle() - Mathf.Pi / 4f;
            // 机枪、护盾、狙击都是开局那几秒里按视频的时间点加上去的（StepOpening）
            var f = new Faction(t, Centers[t], ang);
            Factions[t] = f;

            _mgBox[t] = new StyleBoxFlat
            {
                BgColor = new Color(TeamTint(t, 0.25f), 0.30f),
                BorderColor = new Color(TeamTint(t, 0.75f), 0.70f),
                AntiAliasing = true,
            };
            _mgBox[t].SetBorderWidthAll(1);
            _mgBox[t].SetCornerRadiusAll(4);
            _shieldBox[t] = (StyleBoxFlat)_mgBox[t].Duplicate();
        }

        BuildLuts();

        _glow = new Node2D
        {
            Name = "Glow",
            Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add },
        };
        _glow.Draw += DrawGlow;
        AddChild(_glow);

        _top = new Node2D { Name = "Top" };
        _top.Draw += DrawTop;
        AddChild(_top);

        var sg = new Gradient();
        sg.Offsets = new float[] { 0f, 0.25f, 0.6f, 1f };
        sg.Colors  = new Color[] { Colors.White, new Color(1f, 1f, 1f, 0.5f), new Color(1f, 1f, 1f, 0.12f), new Color(1f, 1f, 1f, 0f) };
        _softDot = new GradientTexture2D
        {
            Gradient = sg, Width = 64, Height = 64,
            Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(1f, 0.5f),
        };
        // 复活传送：从左侧弹珠台飞过来，要盖在左侧弹珠（ZIndex 3）上面
        _warpLayer = new Node2D
        {
            Name = "Warp", ZIndex = 10,
            Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add },
        };
        _warpLayer.Draw += DrawWarps;
        AddChild(_warpLayer);

        _laserVp = new SubViewport
        {
            Name = "LaserLines",
            Size = new Vector2I(N, N),
            Disable3D = true,
            TransparentBg = true,
            Msaa2D = Viewport.Msaa.Msaa2X,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        _laserLayer = new Node2D
        {
            Name = "Lines",
            Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add },
        };
        _laserLayer.Draw += DrawLaserLines;
        _laserVp.AddChild(_laserLayer);
        AddChild(_laserVp);                              // 放在合成视口前面：同一帧里先画好

        _composeMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/TerritoryCompose.gdshader") };
        _composeMat.SetShaderParameter("field", Field.Tex);
        _composeMat.SetShaderParameter("lasers", _laserVp.GetTexture());
        _composeMat.SetShaderParameter("lut", Field.LutTex);
        uint np = Field.NeutralPacked;
        _composeMat.SetShaderParameter("neutral_col", new Vector3((np & 0xFF) / 255f, ((np >> 8) & 0xFF) / 255f, ((np >> 16) & 0xFF) / 255f));
        _composeMat.SetShaderParameter("blur_r", TerritoryField.BlurR);
        _composeMat.SetShaderParameter("logo", Logos.BuildLayer(N, S.LogoSize));
        _composeMat.SetShaderParameter("logo_opacity", S.LogoOpacity);
        _composeVp = new SubViewport
        {
            Name = "Compose",
            Size = new Vector2I(N, N),
            Disable3D = true,
            TransparentBg = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        _composeVp.AddChild(new ColorRect { Size = new Vector2(N, N), Material = _composeMat });
        AddChild(_composeVp);
    }

    public override void _Process(double delta)
    {
        float dt = Math.Min((float)delta, 0.5f);

        if (Winner < 0)
        {
            _acc += dt;
            int maxSteps = Math.Max(4, (int)MathF.Ceiling((float)Engine.TimeScale * 2.5f));
            int n = 0;
            while (_acc >= Step && n < maxSteps)
            {
                SimStep(Step);
                _acc -= Step;
                n++;
            }
            if (n >= maxSteps) _acc = 0f;      // 掉帧时丢弃积压，免得越积越多
            _profSteps += n;
            ProfStart();
            StepParticles(MathF.Min(dt, maxSteps * Step), true);
            ProfMark(5);
        }
        else
        {
            StepParticles(dt, false);          // 结束画面里粒子继续流，只是不再产出
            TickEffects(dt);
            _overTimer -= dt;
            if (_overTimer <= 0f)
            {
                GetTree().ReloadCurrentScene();
                return;
            }
        }

        Render();
        QueueRedraw();
        _glow.QueueRedraw();
        _laserLayer.QueueRedraw();
        _top.QueueRedraw();
        _warpLayer.QueueRedraw();
        if (Prof) _profFrames++;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey k && k.Pressed && !k.Echo && k.Keycode == Key.R)
            GetTree().ReloadCurrentScene();
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  固定步长模拟
    // ════════════════════════════════════════════════════════════════════════════

    private void SimStep(float dt)
    {
        _time += dt;
        StepOpening();
        TickEffects(dt);
        if (LogEvents && ++_sparkLogTick >= 30)
        {
            _sparkLogTick = 0;
            if (_sparkCount > 0) Ev($"sparks {_sparkCount} in 0.5 s");
            _sparkCount = 0;
        }
        ProfStart();
        ResolveShieldClashes();
        ScanThreats();
        for (int t = 0; t < Main.TeamCount; t++)
        {
            if (!Factions[t].Alive) continue;
            StepTurret(Factions[t], dt);
            StepStorm(Factions[t], dt);
        }
        ProfMark(0);
        StepLasers(dt);
        ProfMark(1);
        StepDeathWaves();
        StepBullets(dt);
        ProfMark(2);
        CollideBullets();
        ProfMark(3);
        StepBigBalls(dt);
        CreditHarvest(dt);
        StepChargeBonus(dt);
        ProfMark(4);
        if (++_smoothTick >= SmoothEvery)
        {
            _smoothTick = 0;
            Field.Smooth();
        }
        ProfMark(14);
        CompactBullets();
        CheckWin();
        ProfMark(4);
    }

    /// <summary>通知、显示值缓动、冲击波：跟着模拟时间走（模拟跟不上的时候不会比画面提前结束）；结束画面里跟着帧时间走。</summary>
    private void TickEffects(float dt)
    {
        for (int t = 0; t < Main.TeamCount; t++) Factions[t].Tick(dt);
        for (int i = _shocks.Count - 1; i >= 0; i--)
        {
            var s = _shocks[i];
            if (s.Delay > 0f) s.Delay -= dt;
            else s.Life -= dt;
            if (s.Life <= 0f) _shocks.RemoveAt(i);
            else _shocks[i] = s;
        }
        for (int i = _revives.Count - 1; i >= 0; i--)
            if ((_revives[i].Age += dt) >= ReviveLife) _revives.RemoveAt(i);
        for (int i = _warps.Count - 1; i >= 0; i--)
        {
            var w = _warps[i];
            w.Age += dt;
            if (!w.Arrived && w.Age >= WarpGather + w.Fly) ReviveArrive(w);
            if (w.Age >= WarpGather + w.Fly + WarpFade) _warps.RemoveAt(i);
        }
    }

    /// <summary>开局那几秒（视频实测）：0 秒狙击 +1M 射向场地正中间，1.8 秒机枪 +500K，4.8 秒护盾 +25M。</summary>
    private void StepOpening()
    {
        if (_opening >= 3) return;
        if (_opening == 0)
        {
            _opening = 1;
            var mid = new Vector2(N * 0.5f, N * 0.5f);
            foreach (var f in Factions)
            {
                f.AddNote("狙击火力", S.OpeningSniper, S.OpeningSniperLead);   // 提示和脉冲也是视频第一帧之前 0.21 秒就出来了
                FireSniper(f, S.OpeningSniper, mid, S.OpeningSniperLead);
            }
        }
        if (_opening == 1 && _time >= S.OpeningMgTime)
        {
            _opening = 2;
            foreach (var f in Factions)
            {
                if (!f.Alive || S.StartMachineGunFire <= 0f) continue;   // 设置界面里填 0 = 开局不加
                f.MgFire += S.StartMachineGunFire;
                f.AddNote("机枪火力", S.StartMachineGunFire);
            }
        }
        if (_opening == 2 && _time >= S.OpeningShieldTime)
        {
            _opening = 3;
            foreach (var f in Factions)
            {
                if (!f.Alive || S.StartShield <= 0f) continue;
                f.Shield += S.StartShield;
                f.AddNote("护盾增幅", S.StartShield);
            }
        }
    }

    // ── 炮台 / 点防 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 每个阵营找离中心最近、正在靠近的敌方特殊子弹（300 内）和普通子弹（150 内）。
    /// 风暴子弹是一整圈一起压过来的：只算直线飞下去会撞上护盾的，擦边过去的不管（不然点防老在打两边飞过去的）。
    /// </summary>
    private void ScanThreats()
    {
        Span<float> bestS = stackalloc float[4];
        Span<float> bestN = stackalloc float[4];
        float rs2 = S.PointDefenseRange * S.PointDefenseRange;
        float rn2 = S.PointDefenseNormalRange * S.PointDefenseNormalRange;
        Span<float> hit2 = stackalloc float[4];
        for (int t = 0; t < 4; t++)
        {
            _threatSpecial[t] = -1;
            _threatNormal[t] = -1;
            bestS[t] = rs2;
            bestN[t] = rn2;
            var f = Factions[t];
            float hr = MathF.Max(f.ShieldUp ? f.ShieldRadius : 0f, Faction.ZeroShieldLineRadius) + StormThreatMargin;
            hit2[t] = hr * hr;
        }
        for (int i = 0; i < _bulletCount; i++)
        {
            ref Bullet b = ref _bullets[i];
            if (b.Life <= 0f) continue;
            bool special = b.Kind == Kind.Sniper || b.Kind == Kind.Storm;
            for (int t = 0; t < 4; t++)
            {
                if (t == b.Team || !Factions[t].Alive) continue;
                float dx = Centers[t].X - b.X, dy = Centers[t].Y - b.Y;
                float d2 = dx * dx + dy * dy;
                if (dx * b.VX + dy * b.VY <= 0f) continue;          // 在远离，不管
                if (special)
                {
                    if (b.Kind == Kind.Storm)
                    {
                        float cr = dx * b.VY - dy * b.VX;                   // 直线飞下去离中心最近 = |cr| / |v|
                        if (cr * cr > hit2[t] * (b.VX * b.VX + b.VY * b.VY)) continue;
                    }
                    if (d2 < bestS[t]) { bestS[t] = d2; _threatSpecial[t] = i; }
                }
                else if (d2 < bestN[t]) { bestN[t] = d2; _threatNormal[t] = i; }
            }
        }
    }

    private bool FindPdTarget(Faction f, out float aim)
    {
        aim = 0f;
        Vector2 c = f.Center;
        float pdSpeed = S.BulletSpeed * S.PointDefenseSpeedMultiplier;

        // 1) 敌方大球
        BigBall bestBall = null;
        float bestD = S.PointDefenseRange;
        for (int i = 0; i < Balls.Count; i++)
        {
            var b = Balls[i];
            if (b.Dead || b.Team == f.Team) continue;
            float d = (b.Pos - c).Length() - b.Radius;
            if (d < bestD) { bestD = d; bestBall = b; }
        }
        if (bestBall != null)
        {
            aim = InterceptAngle(c, bestBall.Pos, bestBall.Vel, pdSpeed);
            return true;
        }

        // 2) 狙击/风暴子弹，3) 普通子弹
        int bi = _threatSpecial[f.Team] >= 0 ? _threatSpecial[f.Team] : _threatNormal[f.Team];
        if (bi < 0) return false;
        ref Bullet t = ref _bullets[bi];
        aim = InterceptAngle(c, new Vector2(t.X, t.Y), new Vector2(t.VX, t.VY), pdSpeed);
        return true;
    }

    /// <summary>从 from 以速度 s 发射，求能击中「p 处以 v 运动的目标」的方向角。</summary>
    private static float InterceptAngle(Vector2 from, Vector2 p, Vector2 v, float s)
    {
        Vector2 d = p - from;
        float a = v.Dot(v) - s * s;
        float b = 2f * d.Dot(v);
        float c = d.Dot(d);
        float t = 0f;
        if (MathF.Abs(a) < 1e-3f)
        {
            if (MathF.Abs(b) > 1e-6f) t = MathF.Max(0f, -c / b);
        }
        else
        {
            float disc = b * b - 4f * a * c;
            if (disc >= 0f)
            {
                float sq = MathF.Sqrt(disc);
                float t1 = (-b - sq) / (2f * a), t2 = (-b + sq) / (2f * a);
                float lo = MathF.Min(t1, t2), hi = MathF.Max(t1, t2);
                t = lo > 0f ? lo : (hi > 0f ? hi : 0f);
            }
        }
        return (p + v * t - from).Angle();
    }

    private void StepTurret(Faction f, float dt)
    {
        bool pd = false;
        if (FindPdTarget(f, out float aim))
        {
            float diff = Mathf.Wrap(aim - f.TurretAngle, -Mathf.Pi, Mathf.Pi);
            float turn = PdTurnRate * dt;
            f.TurretAngle += Math.Clamp(diff, -turn, turn);
            pd = MathF.Abs(diff) < PdAimTolerance;
            f.PdActive = true;
        }
        else
        {
            f.PdActive = false;
            f.TurretAngle += S.TurretRotationSpeed * dt;         // y 向下：角度增大 = 顺时针
        }
        f.TurretAngle = Mathf.Wrap(f.TurretAngle, 0f, Mathf.Tau);

        // 点防：每步三颗，每颗带走当前火力的 0.03%，飞得快、打得准
        if (pd)
        {
            int shots = (int)S.PointDefenseFireMultiplier;
            float speed = S.BulletSpeed * S.PointDefenseSpeedMultiplier;
            for (int k = 0; k < shots; k++)
            {
                double value = f.MgFire * S.MgShotFraction;
                if (value < 1.0) break;
                f.MgFire -= value;
                Vector2 dir = Vector2.FromAngle(f.TurretAngle + RandRange(-0.035f, 0.035f));
                float frac = (k + Rand01()) / shots;             // 同一步的几发沿射线错开
                SpawnBullet(f.Team, f.Center + dir * (12f + speed * dt * frac), dir * speed, (float)value, Kind.Pd);
                f.PdShots++;
            }
            return;
        }

        // 普通机枪：读数每步少 0.03%；一步打几颗、每颗多少点见 GameSettings（视频实测：高火力一帧也只有两三颗，每颗有上限）。
        // 不到 1 点连一个像素都涂不起，就不打了
        double fire = f.MgFire, total = fire * S.MgShotFraction;
        if (total < 1.0) return;
        f.MgFire -= total;
        int n = Math.Clamp((int)(Math.Pow(Math.Max(total / S.MgBulletValue, 1.0), S.MgShotsExponent) + Rand01()),
                           1, Math.Max(1, S.MgMaxShotsPerStep));
        // 场上子弹太多：过了 MaxBullets 的一半就按剩下的空位少打，满了不打
        int half = Math.Max(1, S.MaxBullets / 2), room = S.MaxBullets - _bulletCount;
        if (room < half) n = (int)(n * (float)Math.Max(room, 0) / half + Rand01());
        if (n <= 0) return;
        float each = (float)Math.Min(total / n, S.MgBulletCapAt1M * Math.Pow(fire / 1e6, S.MgBulletCapExponent));
        // 上限以外的部分不涂色，但打护盾、打大球、子弹互撞照样算：视频高火力机枪读数少多少，敌方护盾就掉多少（见 GameSettings）
        float dmg = (float)Math.Max(total / n / each, 1.0);
        float spread = Mathf.DegToRad(S.MgSpreadDeg);
        float lag = S.TurretRotationSpeed * dt;
        for (int k = 0; k < n; k++)
        {
            // 这一步里越早打出去的飞得越远、那时炮口也越靠后；方向 ±MgSpread（三角分布）、速度 ±MgSpeedJitter 随机
            float frac = (k + Rand01()) / n;
            float ang = f.TurretAngle - lag * frac + (Rand01() - Rand01()) * spread;
            float sp = S.BulletSpeed * (1f + RandRange(-S.MgSpeedJitter, S.MgSpeedJitter));
            Vector2 dir = Vector2.FromAngle(ang);
            int bi = SpawnBullet(f.Team, f.Center + dir * (12f + sp * dt * frac), dir * sp, each, Kind.Mg);
            _bullets[bi].Dmg = dmg;
        }
    }

    private int SpawnBullet(int team, Vector2 p, Vector2 v, float value, Kind kind)
    {
        if (_bulletCount >= _bullets.Length)
            Array.Resize(ref _bullets, _bullets.Length * 2);
        int i = _bulletCount++;
        ref Bullet b = ref _bullets[i];
        b.X = Math.Clamp(p.X, 0f, N - 0.01f);
        b.Y = Math.Clamp(p.Y, 0f, N - 0.01f);
        b.VX = v.X;
        b.VY = v.Y;
        b.Value = value;
        b.Dmg = 1f;
        b.Life = 1f;
        b.Age = 0f;
        b.T = 0f;
        b.Team = team;
        b.Kind = kind;
        b.Fold = 0;
        b.Group = -1;
        b.SlotO = b.SlotL = 0f;
        b.Spin = 0f;
        b.Orbit = 0f;
        b.Cruise = b.Fast = b.Catch = 0f;
        b.Thick = 0f;
        b.Fid = 0;
        b.Lane = 0;
        b.Stage = 0;
        return i;
    }

    // ── 子弹 ────────────────────────────────────────────────────────────────────

    private void StepBullets(float dt)
    {
        float pr = S.BulletPaintRadius;
        const float hi = N - 0.01f;

        for (int s = 0; s < _lasers.Length; s++)
        {
            var l = _lasers[s];
            if (l == null) continue;
            l.Count = l.Near = l.Joined = 0;
            l.Value = 0.0;
        }

        for (int i = 0; i < _bulletCount; i++)
        {
            ref Bullet b = ref _bullets[i];
            if (b.Life <= 0f) continue;
            b.Age += dt;
            b.T += dt;
            if (b.Kind == Kind.Storm && S.StormBulletLife > 0f)
            {
                b.Life -= dt;                                    // 风暴子弹寿命到了就散掉
                if (b.Life <= 0f) continue;
            }

            bool hidden = b.Group >= 0 && SteerSniper(ref b, dt);
            float ox = b.X, oy = b.Y;
            b.X += b.VX * dt;
            b.Y += b.VY * dt;
            int fold = 0;
            if (b.X < 0f) { b.X = -b.X; b.VX = MathF.Abs(b.VX); fold = 1; }
            else if (b.X > hi) { b.X = 2f * hi - b.X; b.VX = -MathF.Abs(b.VX); fold = 1; }
            if (b.Y < 0f) { b.Y = -b.Y; b.VY = MathF.Abs(b.VY); fold |= 2; }
            else if (b.Y > hi) { b.Y = 2f * hi - b.Y; b.VY = -MathF.Abs(b.VY); fold |= 2; }
            if (fold != 0)
            {
                b.Age = 0f;                                      // 拖尾从墙上重新长出来，不穿墙
                b.Fold ^= (byte)fold;
            }
            b.X = Math.Clamp(b.X, 0f, hi);
            b.Y = Math.Clamp(b.Y, 0f, hi);

            if (HitShieldOrCenter(ref b)) continue;
            if (HitBigBall(ref b)) continue;
            if (hidden) continue;                                // 狙击子弹刚开始乱飞：不涂色

            // 圆盘里（圆心 + 周围 8 个探测点）有非己方像素才涂色：敌方像素每个花 1 点、中立像素 NeutralPaintCost 点
            // （中立的记成收成），花完了子弹才没；只在自家领土上飞就一点不花，一直飞、碰边反弹。
            // 风暴子弹刚出膛那几秒扫过的地方（包括自家领土）会过曝一下，拼成视频里那种亮圆盘。
            // 狙击子弹每只涂一条细线：还没归队的（花、扫帚）涂比鲜色稍暗的颜色、不带新涂的过曝；归队以后涂的、扫过的自家领土都过曝好几秒，
            // 一束几十条并排拼成一条亮带（视频实测：只有收拢以后的那一段是亮的）
            if (b.Kind == Kind.Sniper)
            {
                // 线细、苍蝇又飞得快：一步里沿路径涂几个小圆，连成一条不断的细线
                bool lit = b.Stage == 3, sweep = lit && b.Group >= 0;
                float rad = lit ? S.SniperBeamLineRadius * (1f + S.SniperBeamLineSpread * b.Thick)
                                : S.SniperFlyPaintRadius * (1f + S.SniperFlyThickSpread * b.Thick);
                float until = lit ? _time + S.SniperFlashSeconds : _time - S.SniperFlyPaintAge;
                byte lf = lit ? (byte)1 : (byte)0;
                float sx = b.X - ox, sy = b.Y - oy;
                int m = Math.Clamp((int)(MathF.Sqrt(sx * sx + sy * sy) / rad) + 1, 1, 32);
                for (int q = 1; q <= m && b.Life > 0f; q++)
                {
                    float qx = ox + sx * q / m, qy = oy + sy * q / m;
                    if (Field.AnyForeign(qx, qy, rad * 0.8f, b.Team))
                    {
                        b.Value -= Field.PaintDisc(qx, qy, rad, b.Team, b.Value, S.NeutralPaintCost, until, out int nq, lf);
                        _harvest[b.Team] += nq;
                        if (b.Value <= 0.001f) b.Life = -1f;
                    }
                    if (sweep) Field.FlashDisc(qx, qy, rad, b.Team, until, 1);
                }
                continue;
            }
            bool storm = b.Kind == Kind.Storm;
            float paintAt = storm ? _time + TerritoryField.FlashSeconds : _time;
            if (Field.AnyForeign(b.X, b.Y, pr * 0.8f, b.Team))
            {
                float cost = Field.PaintDisc(b.X, b.Y, pr, b.Team, b.Value, S.NeutralPaintCost, paintAt, out int neutral);
                if (b.Kind == Kind.Pd) Factions[b.Team].PdPaintLoss += cost;
                b.Value -= cost;
                _harvest[b.Team] += neutral;
                if (b.Value <= 0.001f) b.Life = -1f;
            }
            else if (storm && b.T < S.StormFlashSeconds)
            {
                Field.FlashDisc(b.X, b.Y, pr + 1f, b.Team, paintAt);
            }
        }
    }

    private bool HitShieldOrCenter(ref Bullet b)
    {
        for (int t = 0; t < 4; t++)
        {
            var f = Factions[t];
            if (t == b.Team || !f.Alive) continue;
            float dx = b.X - f.Center.X, dy = b.Y - f.Center.Y;
            float d2 = dx * dx + dy * dy;
            if (f.ShieldUp)
            {
                // 狙击子弹撞的是画出来的护盾环（护盾被打得往里缩时环慢半拍：子弹不会钻进环里才撞上）
                float r = b.Kind == Kind.Sniper ? SniperBlockRadius(f) : f.ShieldRadius;
                if (d2 < r * r)
                {
                    if (b.Kind == Kind.Sniper && b.Value > f.Shield)
                    {
                        // 狙击穿甲：打破护盾后剩下的不马上钻进去 —— 先在环上顶住 SniperShieldBreakHold 秒（环碎掉淡出），
                        // 再穿过去，护盾破了就能打到中心
                        b.Value -= (float)f.Shield;
                        f.ShieldHitBy[(int)Kind.Sniper] += f.Shield;
                        float d = MathF.Sqrt(d2) + 1e-3f;
                        DamageShield(f, f.Shield, b.Team, "Sniper", r, f.Center + new Vector2(dx, dy) * (r / d));
                        HoldAtRing(ref b, f, r, d);
                        continue;
                    }
                    double hit = b.Value * (double)b.Dmg;
                    f.ShieldHitBy[(int)b.Kind] += Math.Min(hit, f.Shield);
                    DamageShield(f, hit, b.Team, b.Kind.ToString());
                    b.Life = -1f;
                    return true;
                }
            }
            else if (b.Kind == Kind.Sniper && f.BreakLife > 0f)
            {
                // 护盾刚被打破、环还在碎：狙击子弹顶在环上
                float r = f.BreakR;
                if (d2 < r * r) HoldAtRing(ref b, f, r, MathF.Sqrt(d2) + 1e-3f);
            }
            else if (d2 < CenterHitRadius * CenterHitRadius)
            {
                b.Life = -1f;
                KillFaction(f, b.Team, b.Kind.ToString());
                return true;
            }
        }
        return false;
    }

    /// <summary>狙击撞敌方护盾的半径：护盾在 = 画出来的环和护盾半径取大的；刚被打破、环还在碎 = 碎环的半径；否则 0（不挡）。</summary>
    private static float SniperBlockRadius(Faction f) =>
        f.ShieldUp ? MathF.Max(f.RingR, f.ShieldRadius) : f.BreakLife > 0f ? f.BreakR : 0f;

    /// <summary>把子弹放回护盾环外面，去掉往里的速度（d = 子弹到中心的距离）。</summary>
    private static void HoldAtRing(ref Bullet b, Faction f, float r, float d)
    {
        float nx = (b.X - f.Center.X) / d, ny = (b.Y - f.Center.Y) / d;
        b.X = Math.Clamp(f.Center.X + nx * (r + 0.5f), 0f, N - 0.01f);
        b.Y = Math.Clamp(f.Center.Y + ny * (r + 0.5f), 0f, N - 0.01f);
        float vn = b.VX * nx + b.VY * ny;
        if (vn < 0f) { b.VX -= vn * nx; b.VY -= vn * ny; }
    }

    private bool HitBigBall(ref Bullet b)
    {
        for (int i = 0; i < Balls.Count; i++)
        {
            var ball = Balls[i];
            if (ball.Dead) continue;
            float r = ball.Radius;
            float dx = b.X - ball.Pos.X, dy = b.Y - ball.Pos.Y;
            if (dx * dx + dy * dy >= r * r) continue;
            double hit = b.Value * (double)b.Dmg;                // 按读数实际少的那份算（见 Bullet.Dmg）
            if (ball.Team == b.Team)
            {
                // 己方子弹：被吸收，大球变大，同时把子弹的动量加到球上（质量 ∝ 数值^0.58）。
                // 只加不减：球不会被扇面里四面八方的子弹拖慢；速度最多到 BigBallSpeed × 速度倍数 × OwnPushMaxSpeed（已经更快的不再加）
                double mb = Math.Pow(Math.Max(hit, 1.0), S.BigBallMassExponent);
                double mB = Math.Pow(Math.Max(ball.Value, 1.0), S.BigBallMassExponent);
                float w = (float)(S.OwnBulletPush * mb / (mb + mB));
                Vector2 v0 = ball.Vel;
                float sp0 = v0.Length();
                ball.Vel += w * new Vector2(b.VX, b.VY);
                float cap = MathF.Max(sp0, S.BigBallSpeed * ball.SpeedMul * S.OwnPushMaxSpeed), sp = ball.Vel.Length();
                if (sp > cap) ball.Vel *= cap / sp;
                // 迎面来的只改方向、不减速（以前会减速：大机枪的子弹在墙角来回弹，四面八方打在自家球上，把球一点点刹住，
                // 每圈扫射又把它推回离自家最远的那个角，球就卡在角落里了）
                else if (sp < sp0) ball.Vel = sp > 1e-3f ? ball.Vel * (sp0 / sp) : v0;
                ball.Value += hit;
            }
            else
            {
                ball.HitFlash = 0.12f;
                if (b.Kind == Kind.Sniper && b.Value > ball.Value)
                {
                    b.Value -= (float)ball.Value;                // 狙击把大球打穿，剩下的继续飞
                    ball.Value = 0.0;
                    ball.Dead = true;
                    continue;
                }
                ball.Value -= hit;                               // 敌方子弹：消耗大球
                if (ball.Value < 1.0) ball.Dead = true;
            }
            b.Life = -1f;
            return true;
        }
        return false;
    }

    /// <summary>
    /// 敌方护盾重叠（用户要求）：两边同时扣同样的数值 x，x 取刚好让两圈不再接触的最小值（二分，半径按数值算，
    /// 见 Faction.ShieldRadiusD）；扣到小的那边归零还碰着，就是小的破掉、大的扣掉同样多。
    /// 相邻阵营中心相距 740，对角 1046 > 2 × 500（护盾半径上限），对角永远碰不到。
    /// </summary>
    private void ResolveShieldClashes()
    {
        if (!S.ShieldClash) return;
        for (int i = 0; i < Main.TeamCount; i++)
        {
            for (int j = i + 1; j < Main.TeamCount; j++)
            {
                Faction a = Factions[i], b = Factions[j];
                if (!a.Alive || !b.Alive || !a.ShieldUp || !b.ShieldUp) continue;
                double d = a.Center.DistanceTo(b.Center);
                double sa = a.Shield, sb = b.Shield;
                if (Faction.ShieldRadiusD(sa) + Faction.ShieldRadiusD(sb) <= d) continue;
                // 扣完留 0.25 像素的缝，下一帧不会因为舍入又判成接触
                double gap = d - 0.25, hi = Math.Min(sa, sb);
                if (Faction.ShieldRadiusD(sa - hi) + Faction.ShieldRadiusD(sb - hi) <= gap)
                {
                    double lo = 0.0;
                    for (int it = 0; it < 64; it++)
                    {
                        double mid = 0.5 * (lo + hi);
                        if (Faction.ShieldRadiusD(sa - mid) + Faction.ShieldRadiusD(sb - mid) <= gap) hi = mid; else lo = mid;
                    }
                }
                double x = hi;
                DamageShield(a, x, j, "shield clash");
                DamageShield(b, x, i, "shield clash");
                Ev($"shield clash {Main.TeamNames[i]} {Main.TeamNames[j]} -{x:0} -> {a.Shield:0} / {b.Shield:0}, r {a.ShieldRadius:0.0} + {b.ShieldRadius:0.0} vs d {d:0}");
                // 两圈相碰的地方闪一下（按抵消后的半径算接触点，环缩回去正好在这里碰上）
                Vector2 dir = (b.Center - a.Center) / (float)d;
                Vector2 at = a.ShieldUp ? a.Center + dir * a.ShieldRadius
                           : b.ShieldUp ? b.Center - dir * b.ShieldRadius
                           : (a.Center + b.Center) * 0.5f;
                _sparkPos[_sparkNext] = at;
                _sparkT[_sparkNext] = _time;
                _sparkNext = (_sparkNext + 1) % SparkMax;
                _shocks.Add(new Shock { Pos = at, Life = 0.45f, MaxLife = 0.45f, R0 = 8f, R1 = 24f, Flash = true, Col = Colors.White });
            }
        }
    }

    /// <summary>
    /// 扣护盾；扣没了就破。breakR &gt; 0（狙击打破）：护盾环在 breakR 上碎掉淡出（SniperShieldBreakHold 秒里还挡着狙击子弹），
    /// 撞上的地方 at 炸一团白光。
    /// </summary>
    private void DamageShield(Faction f, double amount, int by, string how, bool flash = true)
        => DamageShield(f, amount, by, how, 0f, Vector2.Zero, flash);

    private void DamageShield(Faction f, double amount, int by, string how, float breakR, Vector2 at, bool flash = true)
    {
        f.Shield -= amount;
        if (flash) f.HitFlash = 0.15f;
        if (f.Shield < 1.0)
        {
            Ev($"shield broken {Main.TeamNames[f.Team]} by {Main.TeamNames[by]} {how}" + (breakR > 0f ? $"  R {breakR:0.0} at ({at.X:0},{at.Y:0})" : ""));
            f.Shield = 0.0;
            f.ShieldShown = 0.0;
            if (by != f.Team) QueueChargeBonus(by, S.ShieldBreakCharge, S.ShieldBreakChargeDelay, $"broke {Main.TeamNames[f.Team]} shield");
            if (breakR > 0f)
            {
                f.BreakLife = S.SniperShieldBreakHold;
                f.BreakR = breakR;
                float life = S.SniperShieldBreakHold + 0.15f;
                _shocks.Add(new Shock
                {
                    Pos = f.Center, Life = life, MaxLife = life, R0 = breakR, R1 = breakR + 30f, Width = 5f,
                    Col = RingColors[f.Team].Lerp(Colors.White, 0.5f),
                });
                _shocks.Add(new Shock { Pos = at, Life = 0.5f, MaxLife = 0.5f, R0 = 10f, R1 = 40f, Flash = true, Col = TeamTint(by, 0.7f) });
            }
            _shocks.Add(new Shock
            {
                Pos = f.Center, Life = 0.8f, MaxLife = 0.8f, R0 = Faction.ZeroShieldLineRadius, R1 = 150f, Width = 6f,
                Col = RingColors[f.Team],
            });
        }
    }

    /// <summary>
    /// 敌对子弹相撞：数值差 5 倍以上 → 双方都扣较小者（小的直接消失）；否则各扣较小者的 20% 并弹开。
    /// 狙击、风暴子弹不弹开：和谁撞都是直接对消（双方都扣较小者），剩下的接着飞——
    /// 高火力的点防 / 机枪子弹和风暴子弹数值差不多，以前撞上只是互相弹开（各扣 20%），风暴几乎挡不住。
    /// 每个阵营单独一张网格，子弹只查编号比自己大的阵营：同阵营的密集弹流不用互相比较，每对敌对子弹只查一次。
    /// </summary>
    private void CollideBullets()
    {
        const int G2 = GridW * GridW;
        if (_gridNext.Length < _bullets.Length) _gridNext = new int[_bullets.Length];
        Array.Fill(_gridHead, -1);
        int teams = 0;
        for (int i = 0; i < _bulletCount; i++)
        {
            ref Bullet b = ref _bullets[i];
            if (b.Life <= 0f) continue;
            int c = b.Team * G2 + Math.Min((int)(b.Y / Cell), GridW - 1) * GridW + Math.Min((int)(b.X / Cell), GridW - 1);
            _gridNext[i] = _gridHead[c];
            _gridHead[c] = i;
            teams |= 1 << b.Team;
        }
        if ((teams & (teams - 1)) == 0) return;                  // 场上只有一个阵营的子弹

        const float r2 = BulletCollideDist * BulletCollideDist, rs2 = StormCollideDist * StormCollideDist;
        for (int i = 0; i < _bulletCount; i++)
        {
            ref Bullet a = ref _bullets[i];
            if (a.Life <= 0f || (teams >> (a.Team + 1)) == 0) continue;
            int cx = Math.Min((int)(a.X / Cell), GridW - 1);
            int cy = Math.Min((int)(a.Y / Cell), GridW - 1);
            for (int u = a.Team + 1; u < 4 && a.Life > 0f; u++)
            {
                if ((teams & (1 << u)) == 0) continue;
                int grid = u * G2;
                for (int gy = cy - 1; gy <= cy + 1 && a.Life > 0f; gy++)
                {
                    if ((uint)gy >= GridW) continue;
                    for (int gx = cx - 1; gx <= cx + 1 && a.Life > 0f; gx++)
                    {
                        if ((uint)gx >= GridW) continue;
                        for (int j = _gridHead[grid + gy * GridW + gx]; j >= 0 && a.Life > 0f; j = _gridNext[j])
                        {
                            ref Bullet b = ref _bullets[j];
                            if (b.Life <= 0f) continue;
                            float dx = b.X - a.X, dy = b.Y - a.Y;
                            float d2 = dx * dx + dy * dy;
                            if (d2 > (a.Kind == Kind.Storm || b.Kind == Kind.Storm ? rs2 : r2)) continue;
                            ResolveBullets(ref a, ref b, dx, dy, d2);
                        }
                    }
                }
            }
        }
    }

    private void ResolveBullets(ref Bullet a, ref Bullet b, float dx, float dy, float d2)
    {
        // 按读数实际少的那份比、扣（Value × Dmg，见 Bullet.Dmg），扣完再折回各自的 Value
        float ea = a.Value * a.Dmg, eb = b.Value * b.Dmg;
        float mn = MathF.Min(ea, eb);
        float mx = MathF.Max(ea, eb);
        if (mx >= 5f * mn || a.Kind == Kind.Sniper || b.Kind == Kind.Sniper || a.Kind == Kind.Storm || b.Kind == Kind.Storm)
        {
            if ((a.Kind == Kind.Sniper) != (b.Kind == Kind.Sniper))     // 狙击撞上点防 / 机枪子弹（狙击对狙击在对撞光团里，不另外放）
            {
                _sparkPos[_sparkNext] = new Vector2(a.X + dx * 0.5f, a.Y + dy * 0.5f);
                _sparkT[_sparkNext] = _time;
                _sparkNext = (_sparkNext + 1) % SparkMax;
                _sparkCount++;
            }
            if ((a.Kind == Kind.Storm) != (b.Kind == Kind.Storm))
            {
                ref Bullet o = ref (a.Kind == Kind.Storm ? ref b : ref a);
                Factions[o.Team].StormKilledBy[o.Kind == Kind.Mg ? 0 : o.Kind == Kind.Pd ? 1 : 2] += mn;
            }
            a.Value = ea <= mn ? 0f : a.Value - mn / a.Dmg;
            b.Value = eb <= mn ? 0f : b.Value - mn / b.Dmg;
            if (a.Value <= 0.001f) a.Life = -1f;
            if (b.Value <= 0.001f) b.Life = -1f;
            return;
        }

        float d = MathF.Sqrt(d2);
        float nx = 1f, ny = 0f;
        if (d > 1e-4f) { nx = dx / d; ny = dy / d; }
        float vn = (a.VX - b.VX) * nx + (a.VY - b.VY) * ny;     // > 0 表示正在靠近
        if (vn <= 0f) return;

        float loss = 0.2f * mn;
        a.Value -= loss / a.Dmg;
        b.Value -= loss / b.Dmg;
        float j = 0.5f * (1f + 0.9f) * vn;
        a.VX -= j * nx; a.VY -= j * ny;
        b.VX += j * nx; b.VY += j * ny;
        a.Age = b.Age = 0f;                                      // 方向变了，拖尾重新长
        if (a.Value <= 0.001f) a.Life = -1f;
        if (b.Value <= 0.001f) b.Life = -1f;
    }

    private void CompactBullets()
    {
        int w = 0;
        for (int i = 0; i < _bulletCount; i++)
        {
            if (_bullets[i].Life <= 0f) continue;
            if (w != i) _bullets[w] = _bullets[i];
            w++;
        }
        _bulletCount = w;
    }

    // ── 大球 ────────────────────────────────────────────────────────────────────

    /// <summary>大球和护盾 / 别的大球相撞：双方各扣较小者的 80%（视频实测，和撞击角度无关）。</summary>
    private double ClashLoss(double a, double b) => Math.Min(a, b) * S.BigBallClashLoss;

    /// <summary>大球撞护盾：差 BigBallShieldWipeRatio 倍以上、且大的一方至少 BigBallShieldWipeMin 时小的一方整个扣光，否则同 ClashLoss。</summary>
    private double ShieldClashLoss(double ball, double shield)
    {
        double lo = Math.Min(ball, shield), hi = Math.Max(ball, shield);
        return S.BigBallShieldWipeRatio > 0f && hi >= lo * S.BigBallShieldWipeRatio && hi >= S.BigBallShieldWipeMin
            ? lo : lo * S.BigBallClashLoss;
    }

    /// <summary>两个大球相撞时 a 这边分到的份额 = mb / (ma + mb)，质量 ∝ 数值^BigBallMassExponent；越重分得越少。</summary>
    private float BallShare(double a, double b) => (float)(1.0 / (1.0 + Math.Pow(Math.Max(a, 1.0) / Math.Max(b, 1.0), S.BigBallMassExponent)));

    /// <summary>大球速度倍数：100 万以下 1，往上按 log10(数值) 线性涨，10 亿以上 1 + BigBallSpeedBoost（见 GameSettings）。</summary>
    private float BallSpeedMul(double value)
    {
        double lo = S.BigBallSpeedBoostFrom, hi = S.BigBallSpeedBoostTo;
        if (S.BigBallSpeedBoost <= 0f || lo <= 0.0 || hi <= lo) return 1f;
        double u = Math.Log10(Math.Max(value, 1.0) / lo) / Math.Log10(hi / lo);
        return 1f + S.BigBallSpeedBoost * (float)Math.Clamp(u, 0.0, 1.0);
    }

    private void StepBigBalls(float dt)
    {
        for (int i = 0; i < Balls.Count; i++)
        {
            var ball = Balls[i];
            if (ball.Dead) continue;
            ball.Age += dt;
            if (ball.HitFlash > 0f) ball.HitFlash -= dt;
            ball.EaseRadius(dt, _time);

            // 匀速直线运动，碰边镜面反弹（视频实测：两次碰撞之间速度不变；和别的大球撞过以后有快有慢，
            // 全场中位约 95 px/s）。撞球、被推、被冲击波踢之后都不超过 BigBallMaxSpeed × 速度倍数。
            // 高数值的球快一点：数值变了速度按新旧倍数之比跟着变
            float m = BallSpeedMul(ball.Value);
            if (m != ball.SpeedMul) { ball.Vel *= m / ball.SpeedMul; ball.SpeedMul = m; }
            float sp = ball.Vel.Length(), vmax = S.BigBallMaxSpeed * m, vmin = S.BigBallSpeed * m * S.BigBallMinSpeed;
            if (sp > vmax) ball.Vel *= vmax / sp;
            else if (sp < vmin)
            {
                // 被撞得很慢：沿当前方向慢慢加速回 vmin（见 GameSettings.BigBallMinSpeed），停住了就朝场地中间走
                Vector2 dir = sp > 1e-3f ? ball.Vel / sp : (new Vector2(N * 0.5f, N * 0.5f) - ball.Pos).Normalized();
                if (dir == Vector2.Zero) dir = Vector2.Right;
                ball.Vel = dir * MathF.Min(vmin, sp + S.BigBallReaccel * dt);
            }
            ball.Pos += ball.Vel * dt;

            float r = ball.Radius;
            if (ball.Pos.X < r) { ball.Pos.X = r; ball.Vel.X = MathF.Abs(ball.Vel.X); }
            else if (ball.Pos.X > N - r) { ball.Pos.X = N - r; ball.Vel.X = -MathF.Abs(ball.Vel.X); }
            if (ball.Pos.Y < r) { ball.Pos.Y = r; ball.Vel.Y = MathF.Abs(ball.Vel.Y); }
            else if (ball.Pos.Y > N - r) { ball.Pos.Y = N - r; ball.Vel.Y = -MathF.Abs(ball.Vel.Y); }

            // 敌方护盾：双方互相消耗，然后弹开；护盾已破则撞到中心 → 阵营灭亡
            for (int t = 0; t < 4; t++)
            {
                var f = Factions[t];
                if (t == ball.Team || !f.Alive) continue;
                Vector2 d = ball.Pos - f.Center;
                float dist = d.Length();
                Vector2 nrm = dist > 1e-3f ? d / dist : Vector2.Right;
                if (f.ShieldUp)
                {
                    // 撞的是画出来的护盾环 RingR（临界阻尼弹簧追 ShieldRadius），不是 ShieldRadius：高数值的大球一撞扣掉护盾 80%
                    // （或直接打破）时 ShieldRadius 当场缩一大截，以前按扣完以后的半径摆球，球会被一下拽进去几十像素
                    // （用户反馈的「瞬移」）；护盾突然变大（复活、弹珠进护盾槽）时又会把旁边的球一下弹到新半径外面。
                    // 环是连续变的：撞上时只把球推回环外，环往外长时推着球走
                    float R = f.RingR;
                    if (dist < R + r)
                    {
                        float vn = ball.Vel.Dot(nrm);
                        // 往里走的才算撞上（双方互相消耗、镜面反弹）；已经在往外走、被长大的护盾环追上的只推不扣
                        if (vn < 0f)
                        {
                            double loss = ShieldClashLoss(ball.Value, f.Shield);
                            Ev($"ball->shield {Main.TeamNames[ball.Team]} {ball.Value:0} vs {Main.TeamNames[t]} {f.Shield:0}  -{loss:0}  |v| {ball.Vel.Length():0}  R {R:0.0} r {r:0.0} d {dist:0.0}");
                            ball.Value -= loss;
                            // 视频 686.8 秒：大球撞上护盾时护盾环亮度不变（不闪），接触点也没有光环
                            f.ShieldHitBy[4] += loss;
                            DamageShield(f, loss, ball.Team, $"ball {ball.Value + loss:0}", flash: false);
                            ball.Vel -= 2f * vn * nrm;
                        }
                        ball.Pos = f.Center + nrm * (R + r + 0.5f);
                    }
                }
                else if (dist < r + CenterHitRadius)
                {
                    KillFaction(f, ball.Team, $"ball {ball.Value:0}");
                }
            }

            PaintBall(ball);
            if (ball.Value < 1.0) ball.Dead = true;
        }

        // 大球互撞：不同阵营互相消耗；都弹开（弹性碰撞，质量 ∝ 数值^0.58，按撞之前的数值算：大球几乎不动，小球弹得远）
        for (int i = 0; i < Balls.Count; i++)
        {
            var a = Balls[i];
            if (a.Dead) continue;
            for (int j = i + 1; j < Balls.Count; j++)
            {
                var b = Balls[j];
                if (b.Dead) continue;
                Vector2 d = b.Pos - a.Pos;
                float dist = d.Length();
                float rs = a.Radius + b.Radius;
                if (dist >= rs) continue;
                Vector2 nrm = dist > 1e-3f ? d / dist : Vector2.Right;
                float vn = (a.Vel - b.Vel).Dot(nrm);
                float wa = BallShare(a.Value, b.Value), wb = 1f - wa;
                if (vn > 0f)
                {
                    if (a.Team != b.Team)
                    {
                        double loss = ClashLoss(a.Value, b.Value);
                        Ev($"ball<->ball {Main.TeamNames[a.Team]} {a.Value:0} vs {Main.TeamNames[b.Team]} {b.Value:0}  -{loss:0}  " +
                           $"cos {vn / MathF.Max((a.Vel - b.Vel).Length(), 1e-3f):0.00}  |va| {a.Vel.Length():0} |vb| {b.Vel.Length():0}  at {a.Pos + nrm * a.Radius:0}");
                        a.Value -= loss;
                        b.Value -= loss;
                        // 接触点冒一个青白小环（视频：半径 5 → 10 像素，0.2 秒），两个球本身不闪
                        _shocks.Add(new Shock
                        {
                            Pos = a.Pos + nrm * a.Radius, Life = 0.2f, MaxLife = 0.2f, R0 = 5f, R1 = 10f, Width = 2f,
                            Col = new Color(0.75f, 0.95f, 1f),
                        });
                    }
                    a.Vel -= 2f * wa * vn * nrm;
                    b.Vel += 2f * wb * vn * nrm;
                    if (a.Team != b.Team) Ev($"  -> |va| {a.Vel.Length():0} |vb| {b.Vel.Length():0}  share a {wa:0.00} b {wb:0.00}");
                }
                float overlap = MathF.Min(rs - dist, BallSepSpeed * dt);
                a.Pos -= nrm * (overlap * wa);
                b.Pos += nrm * (overlap * wb);
                if (a.Value < 1.0) a.Dead = true;
                if (b.Value < 1.0) b.Dead = true;
            }
        }

        // 调试：一步里挪得比速度能走的远 8 像素以上 = 瞬移（撞护盾、撞球以后摆位置摆出来的；正常推开每步最多 6 像素）
        if (LogEvents)
            foreach (var b in Balls)
            {
                float sp = b.Vel.Length(), jump = (b.Pos - b.DbgPos).Length();
                if (b.Age > 1.5f * dt && !b.Dead && jump > MathF.Max(sp, b.DbgSpeed) * dt + 8f)
                    Ev($"ball jump {Main.TeamNames[b.Team]} {b.Value:0} r {b.Radius:0.0} {b.DbgPos:0} -> {b.Pos:0}  {jump:0.0} px");
                b.DbgPos = b.Pos;
                b.DbgSpeed = sp;
            }

        if (BallLogEvery > 0f && _time >= _nextBallLog)
        {
            _nextBallLog = _time + BallLogEvery;
            var sb = new System.Text.StringBuilder($"[bl] t={_time:0.00}");
            foreach (var b in Balls)
                if (!b.Dead)
                    sb.Append(System.FormattableString.Invariant($" {b.Team}:{b.Value:0}:{b.Pos.X:0.0}:{b.Pos.Y:0.0}:{b.Vel.X:0.0}:{b.Vel.Y:0.0}:{b.Radius:0.0}:{b.Age:0.0}"));
            GD.Print(sb.ToString());
        }

        Balls.RemoveAll(b => b.Dead);
    }

    /// <summary>大球碾过：敌方像素 1 点换 1 像素、中立像素便宜一半（记成收成）；自家像素刷新亮度（留下一道较亮的轨迹）。</summary>
    private void PaintBall(BigBall ball)
    {
        float r = ball.Radius;
        float cx = ball.Pos.X, cy = ball.Pos.Y;
        int x0 = Math.Max(0, (int)(cx - r)), x1 = Math.Min(N - 1, (int)(cx + r));
        int y0 = Math.Max(0, (int)(cy - r)), y1 = Math.Min(N - 1, (int)(cy + r));
        float r2 = r * r;
        int team = ball.Team;
        float refresh = _time - BigBallRefreshAge;

        // 和包围盒相交的敌方护盾（护盾内的领土不能被大球抢）
        Span<float> sx = stackalloc float[4];
        Span<float> sy = stackalloc float[4];
        Span<float> sr2 = stackalloc float[4];
        int ns = 0;
        for (int t = 0; t < 4; t++)
        {
            var f = Factions[t];
            if (t == team || !f.Alive || !f.ShieldUp) continue;
            float R = f.ShieldRadius;
            if (f.Center.X + R < x0 || f.Center.X - R > x1 || f.Center.Y + R < y0 || f.Center.Y - R > y1) continue;
            sx[ns] = f.Center.X; sy[ns] = f.Center.Y; sr2[ns] = R * R; ns++;
        }

        byte[] owner = Field.Owner;
        float[] pt = Field.PaintTime;
        double budget = ball.Value, spent = 0.0;
        double nc = S.NeutralPaintCost;
        int neutral = 0;
        for (int y = y0; y <= y1; y++)
        {
            float dy = y + 0.5f - cy;
            int row = y * N;
            for (int x = x0; x <= x1; x++)
            {
                float dx = x + 0.5f - cx;
                if (dx * dx + dy * dy > r2) continue;
                int idx = row + x;
                if (owner[idx] == team)
                {
                    if (pt[idx] < refresh) pt[idx] = refresh;
                    continue;
                }
                if (spent >= budget) continue;
                if (ns > 0)
                {
                    bool shielded = false;
                    for (int s = 0; s < ns; s++)
                    {
                        float ex = x + 0.5f - sx[s], ey = y + 0.5f - sy[s];
                        if (ex * ex + ey * ey < sr2[s]) { shielded = true; break; }
                    }
                    if (shielded) continue;
                }
                if (owner[idx] == TerritoryField.Neutral) { neutral++; spent += nc; }
                else spent += 1.0;
                Field.Paint(idx, team, _time);
            }
        }
        ball.Value -= spent;
        _harvest[team] += neutral;
    }

    // ── 粒子 ────────────────────────────────────────────────────────────────────

    private void SpawnParticles(float dt)
    {
        float want = S.ParticleSpawnPerPixel * dt * TerritoryField.Total + _spawnCarry;
        int m = (int)want;
        _spawnCarry = want - m;
        byte[] owner = Field.Owner;
        float[] paint = Field.PaintTime;
        float lifeMin = S.ParticleLifeMin, lifeSpan = S.ParticleLifeMax - S.ParticleLifeMin;
        float sw0 = S.ParticleSwirlDeg * MathF.PI / 180f, swJ = S.ParticleSwirlJitterDeg * MathF.PI / 180f;
        float spJ = S.ParticleSpeedJitter;
        float bMin = S.ParticleBrightMin, bSpan = S.ParticleBrightSpan;
        // 新涂的像素出粒子的概率低（见 ParticleFreshMin）；年龄 = 现在 − 涂色时间 − ParticleFreshDelay，过曝像素的涂色时间在未来
        float fMin = S.ParticleFreshMin, fNow = _time - S.ParticleFreshDelay, fInvTau = 1f / MathF.Max(0.01f, S.ParticleMatureTau);
        bool fresh = fMin < 1f;
        for (int k = 0; k < m && _partCount < _parts.Length; k++)
        {
            int i = (int)(((ulong)NextU() * TerritoryField.Total) >> 32);
            int o = owner[i];
            if (o == TerritoryField.Neutral || !Factions[o].Alive) continue;
            if (fresh)
            {
                float age = fNow - paint[i];
                float acc = age <= 0f ? fMin : 1f - (1f - fMin) * MathF.Exp(-age * fInvTau);
                if (Rand01() >= acc) continue;
            }
            ref Particle p = ref _parts[_partCount++];
            p.X = i % N + Rand01();
            p.Y = i / N + Rand01();
            p.VX = 0f;
            p.VY = 0f;
            p.Life = lifeMin + lifeSpan * Rand01();
            p.Age = 0f;
            float u = Rand01();
            p.Bright = bMin + bSpan * u * MathF.Sqrt(u);         // 中位 ≈ 0.75，少数很亮（见 ParticleBrightMin）
            // 速度倍率和偏角：三角分布（两个均匀数相加），一辈子不变
            float f = MathF.Max(0.05f, 1f + spJ * (Rand01() + Rand01() - 1f));
            float ang = sw0 + swJ * (Rand01() + Rand01() - 1f);
            p.F = f;
            p.Fc = f * MathF.Cos(ang);
            p.Fs = f * MathF.Sin(ang);
            p.Team = o;
        }
    }

    /// <summary>
    /// 粒子按速度场流向自家中心：v(d) = base + K/(d+d0)，方向向心再偏转一个小角度（顺时针绕行）；
    /// 每颗粒子再乘上自己的速度倍率、加上自己的偏角（出生时定，见 ParticleSpeedJitter），各走各的。
    /// 寿命到了或到达中心 → 算作被中心吸收；落到敌方领土上 → 直接消失。
    /// 靠近大球时速度渐渐换成被大球吸（离球面 BallGravityInner 以内全归大球，往外到 BallGravityReach 渐弱，
    /// 再按两边场强之比软切换，没有硬边界；越过球环 BallAbsorbDepth 才算吸收，大球近处的场更强），
    /// 被吸的粒子一边往里掉一边绕球公转（越靠近球转得越快，见 BallSwirlDeg）。
    /// 并行分块更新，每块就地压缩后再拼接。
    /// </summary>
    private void StepParticles(float dt, bool spawn)
    {
        if (spawn) SpawnParticles(dt);
        int n = _partCount;
        if (n == 0) return;

        int chunks = (n + PartChunk - 1) / PartChunk;
        if (_chunkKeep.Length < chunks)
        {
            _chunkKeep = new int[chunks];
            _chunkAbsorb = new int[chunks * 4];
            _chunkBallGain = new double[chunks * MaxBallSnap];
            _chunkBallTeam = new int[chunks * MaxBallSnap * 4];
        }

        int nb = 0;
        for (int i = 0; i < Balls.Count && nb < MaxBallSnap; i++)
        {
            var b = Balls[i];
            if (b.Dead) continue;
            _snapBall[nb] = b;
            _snapX[nb] = b.Pos.X;
            _snapY[nb] = b.Pos.Y;
            _snapR[nb] = b.Radius;
            float g = b.GravityRadius;
            _snapG2[nb] = g * g;
            nb++;
        }

        _waves.RemoveAll(w => w.Done);
        int nwv = 0;
        foreach (var w in _waves)
        {
            float u = (_time - w.T0) / S.DeathWaveLife;
            if (u < 0f || nwv >= 4) continue;
            _wvX[nwv] = w.Pos.X; _wvY[nwv] = w.Pos.Y; _wvR[nwv] = WaveRadius(u);
            _wvP[nwv] = S.DeathWavePush * (1f - u);
            nwv++;
        }
        float[] wvX = _wvX, wvY = _wvY, wvR = _wvR, wvP = _wvP;
        float wvInvW = 1f / MathF.Max(1f, S.DeathWaveWidth);

        Particle[] parts = _parts;
        float vBase = S.ParticleSpeedBase, vK = S.ParticleSpeedK, vD0 = S.ParticleSpeedD0;
        float bNear = S.BallPullNear, bInvLen = 1f / MathF.Max(1f, S.BallPullLen), bDepth = S.BallAbsorbDepth;
        float bCut = 12f * S.BallPullLen;
        float bTan = MathF.Tan(Math.Clamp(S.BallSwirlDeg, -85f, 85f) * MathF.PI / 180f);
        float bInvReach = 1f / MathF.Max(1f, S.BallGravityReach);
        float wIn = MathF.Min(S.BallGravityInner, S.BallGravityReach - 1f);
        float wInvSpan = 1f / MathF.Max(1f, S.BallGravityReach - wIn);
        float cx0 = Centers[0].X, cy0 = Centers[0].Y, cx1 = Centers[1].X, cy1 = Centers[1].Y;
        float cx2 = Centers[2].X, cy2 = Centers[2].Y, cx3 = Centers[3].X, cy3 = Centers[3].Y;
        int[] keep = _chunkKeep;
        int[] absorb = _chunkAbsorb;
        double[] gain = _chunkBallGain;
        int[] gainTeam = _chunkBallTeam;
        float[] bxs = _snapX, bys = _snapY, brs = _snapR, bg2s = _snapG2;
        byte[] owner = Field.Owner;

        Parallel.For(0, chunks, c =>
        {
            int i0 = c * PartChunk, i1 = Math.Min(n, i0 + PartChunk);
            int w = i0;
            int a0 = 0, a1 = 0, a2 = 0, a3 = 0;
            int gOff = c * MaxBallSnap;
            for (int b = 0; b < nb; b++) gain[gOff + b] = 0.0;
            Array.Clear(gainTeam, gOff * 4, nb * 4);

            for (int i = i0; i < i1; i++)
            {
                Particle p = parts[i];
                p.Life -= dt;
                p.Age += dt;
                if (p.Life <= 0f)
                {
                    // 寿命到了：视作已被中心吸收
                    switch (p.Team) { case 0: a0++; break; case 1: a1++; break; case 2: a2++; break; default: a3++; break; }
                    continue;
                }

                float tcx, tcy;
                switch (p.Team)
                {
                    case 0: tcx = cx0; tcy = cy0; break;
                    case 1: tcx = cx1; tcy = cy1; break;
                    case 2: tcx = cx2; tcy = cy2; break;
                    default: tcx = cx3; tcy = cy3; break;
                }
                float dx = tcx - p.X, dy = tcy - p.Y;
                float d = MathF.Sqrt(dx * dx + dy * dy) + 1e-4f;
                float s = vBase + vK / (d + vD0);
                float tx = dx / d, ty = dy / d;
                float vx = (tx * p.Fc - ty * p.Fs) * s, vy = (tx * p.Fs + ty * p.Fc) * s;

                // 大球的引力：权重 = 距离权重（离球面 wIn 以内 1，往外到 BallGravityReach smoothstep 降到 0）
                //              × 场强之比的软切换 q/(1+q)，q = (sb/s)^4（大球的场比自家中心强得多 → 1，一样强 → 0.5）。
                // 粒子速度 = 背景流 ×(1 − 权重和) + 各个球的吸引 × 权重：方向和快慢都连续变化
                float wSum = 0f, bvx = 0f, bvy = 0f, wBest = 0f;
                int target = -1, eaten = -1;
                for (int b = 0; b < nb; b++)
                {
                    float rx = p.X - bxs[b], ry = p.Y - bys[b];
                    float r2 = rx * rx + ry * ry;
                    if (r2 >= bg2s[b]) continue;
                    float r0 = MathF.Sqrt(r2) + 1e-4f, br = brs[b];
                    if (r0 <= br - bDepth) { eaten = b; break; }
                    float surf = MathF.Max(0f, r0 - br);
                    float u = (surf - wIn) * wInvSpan;
                    if (u >= 1f) continue;
                    float wd = u <= 0f ? 1f : 1f - u * u * (3f - 2f * u);
                    float sb = vBase + vK / (surf + vD0);
                    if (surf < bCut) sb += bNear * MathF.Exp(-surf * bInvLen);
                    float q = sb / s;
                    q *= q;
                    q *= q;
                    float wb = wd * q / (q + 1f);
                    // 绕球公转：离球心的距离照常每步少 sb·dt（什么时候被吸收不变），同时绕球心转过 tan(角)·sb·dt / r 弧度，
                    // 按位置精确地转（不是把速度方向偏一下：那样往里掉得慢、离散步长还会把粒子越甩越远）。负角 = 屏幕上逆时针。
                    // 转的幅度从引力圈边上的 0 线性涨到球面上的满角度
                    float stepB = sb * dt * p.F;               // 快慢按这颗粒子自己的倍率
                    float taper = MathF.Max(0f, 1f - surf * bInvReach);
                    float a = Math.Clamp(-bTan * taper * stepB / MathF.Max(r0, br), -0.5f, 0.5f);
                    float ca = MathF.Cos(a), sa = MathF.Sin(a), k = MathF.Max(0f, r0 - stepB) / r0;
                    bvx += wb * ((rx * ca - ry * sa) * k - rx);
                    bvy += wb * ((rx * sa + ry * ca) * k - ry);
                    wSum += wb;
                    if (wb > wBest) { wBest = wb; target = b; }
                }
                if (eaten >= 0)
                {
                    gain[gOff + eaten] += 1.0;
                    gainTeam[(gOff + eaten) * 4 + p.Team]++;
                    continue;
                }
                if (wSum > 0f)
                {
                    float inv = 1f / dt;
                    if (wSum > 1f) { inv /= wSum; wSum = 1f; }     // 几个球的引力圈叠在一起：只在球之间分
                    vx = vx * (1f - wSum) + bvx * inv;
                    vy = vy * (1f - wSum) + bvy * inv;
                }

                if (wSum < 0.5f)
                {
                    if (d < AbsorbRadius || s * p.F * dt >= d)
                    {
                        switch (p.Team) { case 0: a0++; break; case 1: a1++; break; case 2: a2++; break; default: a3++; break; }
                        continue;
                    }
                    // 脚下的像素已经被敌方占了 → 粒子被吃掉（中立像素可以穿过）
                    int ix = (int)p.X, iy = (int)p.Y;
                    if ((uint)ix < N && (uint)iy < N)
                    {
                        int o = owner[iy * N + ix];
                        if (o != p.Team && o != TerritoryField.Neutral) continue;
                    }
                }

                // 冲击波波前附近的粒子被往外推一把
                for (int k = 0; k < nwv; k++)
                {
                    float qx = p.X - wvX[k], qy = p.Y - wvY[k];
                    float qr = MathF.Sqrt(qx * qx + qy * qy) + 1e-3f;
                    float f = 1f - MathF.Abs(qr - wvR[k]) * wvInvW;
                    if (f <= 0f) continue;
                    vx += qx / qr * wvP[k] * f;
                    vy += qy / qr * wvP[k] * f;
                }

                float nx = p.X + vx * dt, ny = p.Y + vy * dt;
                if (target >= 0)
                {
                    // 这一步落进了球环里面 BallAbsorbDepth 处 → 被这个球吸收
                    float ex = nx - bxs[target], ey = ny - bys[target], rIn = MathF.Max(1f, brs[target] - bDepth);
                    if (ex * ex + ey * ey <= rIn * rIn)
                    {
                        gain[gOff + target] += 1.0;
                        gainTeam[(gOff + target) * 4 + p.Team]++;
                        continue;
                    }
                }
                p.VX = vx;
                p.VY = vy;
                p.X = nx;
                p.Y = ny;
                parts[w++] = p;
            }
            keep[c] = w - i0;
            absorb[c * 4 + 0] = a0;
            absorb[c * 4 + 1] = a1;
            absorb[c * 4 + 2] = a2;
            absorb[c * 4 + 3] = a3;
        });

        int write = keep[0];
        for (int c = 1; c < chunks; c++)
        {
            int k = keep[c];
            int src = c * PartChunk;
            if (k > 0 && src != write) Array.Copy(parts, src, parts, write, k);
            write += k;
        }
        _partCount = write;

        for (int t = 0; t < 4; t++)
        {
            long total = 0;
            for (int c = 0; c < chunks; c++) total += absorb[c * 4 + t];
            if (total > 0 && Factions[t].Alive && Winner < 0) AbsorbParticles(Factions[t], total * (double)S.ParticleValue);
        }
        for (int b = 0; b < nb; b++)
        {
            double g = 0.0;
            for (int c = 0; c < chunks; c++) g += gain[c * MaxBallSnap + b];
            if (g > 0.0 && !_snapBall[b].Dead)
            {
                _snapBall[b].Value += g * S.BallParticleValue;
                BallIntake += g;
                // 自己阵营的大球吸的粒子也有一部分进充能（同样多的粒子被中心吸收给的充能 × BallParticleChargeShare）
                Faction bf = Factions[_snapBall[b].Team];
                if (S.BallParticleChargeShare > 0f && bf.Alive && Winner < 0)
                {
                    double ch = g * S.ParticleValue * (1.0 - S.ParticleMgShare) * S.ParticleChargeBoost * S.BallParticleChargeShare;
                    bf.BallCharge += ch;
                    AddCharge(bf, ch);
                }
            }
            for (int t = 0; t < 4; t++)
            {
                int m = 0;
                for (int c = 0; c < chunks; c++) m += gainTeam[(c * MaxBallSnap + b) * 4 + t];
                _snapBall[b].CoreIntake[t] += m;
                _snapBall[b].Absorbed += m;
            }
            _snapBall[b] = null;
        }
    }

    /// <summary>吸收的粒子：九成进机枪火力，一成进充能（视频 60 秒时机枪收入 ≈ 12.4K/秒、充能 ≈ 1.2K/秒）。</summary>
    private void AbsorbParticles(Faction f, double value)
    {
        f.PartIncome += value;
        f.MgFire += value * S.ParticleMgShare;
        AddCharge(f, value * (1.0 - S.ParticleMgShare) * S.ParticleChargeBoost);
    }

    /// <summary>
    /// 占下的中立像素记成收成：机枪火力 + 充能。收成先过一个两级缓冲（各 τ = HarvestTau）再到账，
    /// 读数是平滑涨上去的；开局机枪火力到账（1.8 秒）之前一直攒着 —— 视频里苍蝇 1 秒起就在涂弧线，
    /// 机枪读数却是 1.8 秒才从 0 跳到 50 万，之后约 1 秒里再涨 13 万（1.95 秒 507K → 3.0 秒 635K）。
    /// </summary>
    private void CreditHarvest(float dt)
    {
        double k = 1.0 - Math.Exp(-dt / S.HarvestTau);
        for (int t = 0; t < 4; t++)
        {
            _harvestPool[t] += _harvest[t];
            HarvestTotal[t] += _harvest[t];
            _harvest[t] = 0;
            var f = Factions[t];
            if (!f.Alive || Winner >= 0) { _harvestPool[t] = _harvestFlow[t] = 0.0; continue; }
            if (_opening < 2) continue;
            double a = _harvestPool[t] * k, n = _harvestFlow[t] * k;
            _harvestPool[t] -= a;
            _harvestFlow[t] += a - n;
            f.MgFire += n * S.NeutralHarvestMg;
            AddCharge(f, n * S.NeutralHarvestCharge);
        }
    }

    /// <summary>充能：充满 → 额外弹珠 +1，左侧顶部生成一颗同色弹珠。</summary>
    private void AddCharge(Faction f, double value)
    {
        f.Charge += value;
        while (f.Charge >= f.ChargeThreshold)
        {
            f.Charge -= f.ChargeThreshold;
            f.ExtraMarbles++;
            _main?.Left?.SpawnExtraBall(f.Team);
        }
    }

    /// <summary>
    /// 充能加成排队：wait 秒后开始，BonusChargeTime 秒里按 1−(1−x)³ 涨完（视频读数先快后慢）；同一队前一笔涨完才轮到下一笔。
    /// 收的一方已经阵亡就不给。
    /// </summary>
    private void QueueChargeBonus(int team, double amount, float wait, string why)
    {
        var f = Factions[team];
        if (amount <= 0.0 || !f.Alive) return;
        _chargeBonus[team].Add(new ChargeBonus { Amount = amount, Wait = wait });
        Ev($"charge bonus {Main.TeamNames[team]} +{amount:0} ({why}) charge {f.Charge:0}/{f.ChargeThreshold:0} extra {f.ExtraMarbles}");
    }

    private void StepChargeBonus(float dt)
    {
        for (int t = 0; t < Main.TeamCount; t++)
        {
            var q = _chargeBonus[t];
            if (q.Count == 0) continue;
            if (!Factions[t].Alive) { q.Clear(); continue; }
            foreach (var c in q) c.Wait -= dt;
            var h = q[0];
            if (h.Wait > 0f) continue;
            h.Age += dt;
            double x = Math.Min(1.0, h.Age / Math.Max(1e-3, S.BonusChargeTime));
            double given = h.Amount * (1.0 - (1.0 - x) * (1.0 - x) * (1.0 - x));
            AddCharge(Factions[t], given - h.Given);
            h.Given = given;
            if (x >= 1.0) q.RemoveAt(0);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  左侧槽 → 右侧火力
    // ════════════════════════════════════════════════════════════════════════════

    public void InjectZone(int team, string zone, float value)
    {
        var f = Factions[team];
        if (!f.Alive || Winner >= 0 || value <= 0f) return;
        if (value >= 100_000f) Ev($"inject {Main.TeamNames[team]} {zone} {value:0}");
        switch (zone)
        {
            case "kill":                     // 调试：--inject=秒:阵营:kill:1 直接让这个阵营覆灭
                KillFaction(f, team, "debug");
                break;
            case "机枪":
                f.MgFire += value;
                f.AddNote("机枪火力", value);
                break;
            case "护盾":
                f.Shield += value;
                f.AddNote("护盾增幅", value);
                break;
            case "狙击":
                f.SniperFire += value;
                f.AddNote("狙击火力", value);
                FireSniper(f, f.SniperFire);
                f.SniperFire = 0.0;
                break;
            case "风暴":
                f.StormFire += value;
                f.AddNote("风暴火力", value);
                f.StormPulsesLeft = S.StormPulses;               // 第一圈马上打，剩下的由 StepStorm 按间隔打
                f.StormTimer = 0f;
                f.StormAngle = RandRange(0f, Mathf.Tau);
                StepStorm(f, 0f);
                break;
            case "大球":
                f.BigBallFire += value;
                f.AddNote("大球火力", value);
                FireBigBall(f, f.BigBallFire, f.TurretAngle);
                f.BigBallFire = 0.0;
                break;
        }
    }

    /// <summary>
    /// 狙击：一群子弹先在中心周围像苍蝇一样乱飞（SniperSwarmTime），然后收拢成楔形队形飞出去：
    /// 有 target 就朝它飞（开局那束射向场地正中间），否则沿那一刻的机枪方向。
    /// 子弹数 = 数值/100，8..SniperBulletsMax；每颗在队形里有固定的横向偏移和落后距离（尖头在中间）：
    /// 每 3 只一条细线（最多 SniperLanes 条），并排铺满 SniperBeamWidth 宽（数值每 ×10 宽 50%），拼成一条亮带。
    /// lead：苍蝇先按炸开的速度衰减直接往外推进这么久（开局那束在视频第一帧之前就打出来了），出膛时间不变。
    /// </summary>
    private void FireSniper(Faction f, double value, Vector2? target = null, float lead = 0f)
    {
        int slot = Array.IndexOf(_lasers, null);
        if (slot >= 0) _lasers[slot] = new Laser { Team = f.Team, Origin = f.Center, Target = target, Value = value };
        int n = (int)Math.Clamp(Math.Round(value / 100.0), 8, S.SniperBulletsMax);
        float each = (float)(value / n);
        int lanes = Math.Clamp(n / 3, 3, Math.Min(S.SniperLanes, Laser.MaxLanes));
        float width = S.SniperBeamWidth * Math.Clamp(1f + 0.5f * (float)Math.Log10(Math.Max(value, 1.0) / 1e6), 0.5f, 2f);
        float gap = width / (lanes - 1);
        if (slot >= 0)
        {
            var l = _lasers[slot];
            l.Lanes = lanes;
            for (int j = 0; j < lanes * 3; j++)
            {
                l.WobA[j] = S.SniperWobble * RandRange(0.4f, 1f) * (j % 3 == 0 ? 1f : 0.6f);
                l.WobK[j] = Mathf.Tau / RandRange(70f, 220f);    // 波长 70..220 像素
                l.WobP[j] = RandRange(0f, Mathf.Tau);
            }
            l.GLen = Math.Max(2, (int)(S.SniperGhostSeconds * 60f));
            l.GFlies = n;
            l.Gx = new float[n * l.GLen];
            l.Gy = new float[n * l.GLen];
            l.Gs = new int[n * l.GLen];
            Array.Fill(l.Gs, -1);
        }
        for (int k = 0; k < n; k++)
        {
            // 苍蝇：从中心往四面八方炸开（很快减到巡航速度），然后在中心周围乱飞（SteerSniper）
            Vector2 u = Vector2.FromAngle(RandRange(0f, Mathf.Tau));
            int i = SpawnBullet(f.Team, f.Center + u * RandRange(0f, 8f), u * RandRange(S.SniperBurstMin, S.SniperBurstMax), each, Kind.Sniper);
            ref Bullet b = ref _bullets[i];
            if (slot < 0)
            {
                // 激光槽用完了（几乎不可能）：直接沿机枪方向飞
                Vector2 d = target.HasValue ? (target.Value - f.Center).Normalized() : Vector2.FromAngle(f.TurretAngle);
                b.VX = d.X * S.SniperSpeed;
                b.VY = d.Y * S.SniperSpeed;
                continue;
            }
            b.Spin = (SwirlDir != 0f ? SwirlDir : Rand01() < 0.5f ? -1f : 1f) * RandRange(0.5f, 1.2f) * S.SniperWander;
            b.Cruise = RandRange(S.SniperCruiseMin, S.SniperCruiseMax);
            if (lead > 0f)
            {
                // 速度 v0 → 巡航速度按 e^(−t/τ) 衰减：走过的距离 = vc·t + (v0 − vc)·τ·(1 − e^(−t/τ))
                float v0 = MathF.Sqrt(b.VX * b.VX + b.VY * b.VY), vc = b.Cruise, tau = S.SniperBurstTau;
                float e = MathF.Exp(-lead / tau), d = vc * lead + (v0 - vc) * tau * (1f - e), v1 = vc + (v0 - vc) * e;
                b.X += u.X * d;
                b.Y += u.Y * d;
                b.VX = u.X * v1;
                b.VY = u.Y * v1;
            }
            float q = Rand01();
            b.Fast = S.SniperFlingMin + (S.SniperFlingMax - S.SniperFlingMin) * q * q;   // 大多数两三百，少数飞得很快
            b.Catch = S.SniperCatchSpeed * S.SniperSpeed * RandRange(1f - S.SniperCatchJitter, 1f + S.SniperCatchJitter);
            b.Fid = (short)k;
            float qt = Rand01();
            b.Thick = qt * qt;
            b.Group = (short)slot;
            b.Lane = (byte)(k % lanes);
            float o = (k % lanes - (lanes - 1) * 0.5f) * gap;
            b.SlotO = o + RandRange(-0.25f, 0.25f);
            b.SlotL = MathF.Abs(o) * 0.6f + (k / lanes) * 1.5f + Rand01();
        }
    }

    /// <summary>激光头：乱飞阶段结束后从中心出膛（朝目标或沿机枪方向），匀速直线、碰边反弹（记下反弹点画线用）。</summary>
    private void StepLasers(float dt)
    {
        const float hi = N - 0.01f;
        for (int s = 0; s < _lasers.Length; s++)
        {
            var l = _lasers[s];
            if (l == null) continue;
            if (l.Ended)
            {
                l.Fade -= dt / S.SniperLineFade;
                if (l.Fade <= 0f && l.Count == 0) _lasers[s] = null;
                continue;
            }
            l.Age += dt;
            l.GStep++;
            if (LogEvents && l.Team == 0) FlyReport(l, s);
            if (!l.Launched)
            {
                if (l.Age < S.SniperSwarmTime) continue;
                l.Launched = true;
                l.Dir = l.Target.HasValue && l.Target.Value.DistanceSquaredTo(l.Origin) > 1f
                    ? (l.Target.Value - l.Origin).Normalized()
                    : Vector2.FromAngle(Factions[l.Team].TurretAngle);
                l.Head = l.Tip = l.Origin;
                AddPathPoint(l, l.Origin);
                Ev($"sniper launch {Main.TeamNames[l.Team]} {Mathf.RadToDeg(l.Dir.Angle()):0}deg value={l.Value:0}");
                continue;
            }
            // 刚被打破的敌方护盾还在碎：激光顶在环上，整束停住，等环碎完再穿过去
            if (HeldByBrokenShield(l)) continue;
            l.FlyTime += dt;
            l.Glow += (LineGlowTarget(l) - l.Glow) * (1f - MathF.Exp(-dt / S.SniperLineLag));
            l.Head += l.Dir * (S.SniperSpeed * dt);
            Vector2 wall = new Vector2(Math.Clamp(l.Head.X, 0f, hi), Math.Clamp(l.Head.Y, 0f, hi));
            int fold = 0;
            if (l.Head.X < 0f) { l.Head.X = -l.Head.X; l.Dir.X = MathF.Abs(l.Dir.X); fold = 1; }
            else if (l.Head.X > hi) { l.Head.X = 2f * hi - l.Head.X; l.Dir.X = -MathF.Abs(l.Dir.X); fold = 1; }
            if (l.Head.Y < 0f) { l.Head.Y = -l.Head.Y; l.Dir.Y = MathF.Abs(l.Dir.Y); fold |= 2; }
            else if (l.Head.Y > hi) { l.Head.Y = 2f * hi - l.Head.Y; l.Dir.Y = -MathF.Abs(l.Dir.Y); fold |= 2; }
            UpdateTip(l, dt, fold != 0);
            if (fold != 0)
            {
                l.Fold ^= fold;
                AddPathPoint(l, wall);
            }
            else if (l.Tip.DistanceSquaredTo(l.Path[^1]) >= 8f * 8f) AddPathPoint(l, l.Tip);
            // 队形维持满 SniperLife，或收拢之后激光头附近已经没有子弹了（打在护盾上 / 被拦掉）→ 结束，细线淡出；
            // 子弹散开各飞各的，直到数值花完
            if (l.FlyTime >= S.SniperLife || (l.FlyTime > S.SniperConvergeTime + 1f && l.Near == 0))
            {
                l.Ended = true;
                Ev($"sniper end {Main.TeamNames[l.Team]} at ({l.Head.X:0},{l.Head.Y:0}) left={l.Value:0} n={l.Count}");
            }
        }
        ClashLasers();
    }

    /// <summary>
    /// 画出来的激光头：跟着 Head 走（落后了按 4 倍速度追上去，碰墙直接跳过去）；在敌方护盾（或正在碎的护盾）里面时
    /// 沿激光方向往回退到进环的那一点 —— 白光和路径线停在环上，护盾被打得往里缩就跟着往里，护盾破了再追上 Head。
    /// </summary>
    private void UpdateTip(Laser l, float dt, bool fold)
    {
        Vector2 tip = fold ? l.Head : l.Tip.MoveToward(l.Head, 4f * S.SniperSpeed * dt);
        for (int t = 0; t < 4; t++)
        {
            var f = Factions[t];
            if (t == l.Team || !f.Alive) continue;
            float R = SniperBlockRadius(f);
            if (R <= 0f) continue;
            Vector2 rel = tip - f.Center;
            float c = rel.LengthSquared() - R * R;
            if (c >= 0f) continue;
            float bq = rel.Dot(l.Dir);
            tip -= l.Dir * (bq + MathF.Sqrt(bq * bq - c));
        }
        l.Tip = tip;
    }

    /// <summary>激光头顶在一个刚被打破、还在碎的敌方护盾环上。</summary>
    private bool HeldByBrokenShield(Laser l)
    {
        for (int t = 0; t < 4; t++)
        {
            var f = Factions[t];
            if (t != l.Team && f.Alive && !f.ShieldUp && f.BreakLife > 0f && l.Tip.DistanceTo(f.Center) <= f.BreakR + 2f) return true;
        }
        return false;
    }

    /// <summary>路径线记一个点：连同这时候的亮度（按位置固定在地上：视频里那条线是从归队的地方慢慢亮起来的）和时刻。</summary>
    private static void AddPathPoint(Laser l, Vector2 p)
    {
        l.Path.Add(p);
        l.PathA.Add(LineGlow(l));
        l.PathT.Add(l.FlyTime);
    }

    /// <summary>路径线的亮度：跟着 LineGlowTarget 走，一阶滞后 SniperLineLag（归队比例三秒多就满了，线要到激光头走出 340 像素才全亮）。</summary>
    private static float LineGlow(Laser l) => l.Glow;

    /// <summary>路径线该有的亮度：归队的子弹超过 SniperLineJoin 才开始亮，全归队最亮（平方，越来越快地亮起来）。</summary>
    private static float LineGlowTarget(Laser l)
    {
        float j = l.Count == 0 ? 0f : (float)l.Joined / l.Count;
        j = Math.Clamp((j - S.SniperLineJoin) / (1f - S.SniperLineJoin), 0f, 1f);
        return j * j;
    }

    /// <summary>
    /// 敌对激光头撞在一起：炸一团青白色的光，随后两圈淡蓝色的细环匀速扩出去（每对激光只炸一次，
    /// 同一个地方刚炸过就不再叠）。数值不在这里算：两边的子弹在 CollideBullets 里一颗一颗地对消
    /// （狙击子弹相撞不弹开），队形主体对消掉以后漏过去的零星子弹照样继续飞
    /// （开局四束在正中间撞出一团白光，残余一路飞到对角的阵营，被点防拦下）。
    /// </summary>
    private void ClashLasers()
    {
        float d2max = S.SniperClashDist * S.SniperClashDist;
        for (int s = 0; s < _lasers.Length; s++)
        {
            var a = _lasers[s];
            if (!Clashable(a)) continue;
            for (int j = s + 1; j < _lasers.Length; j++)
            {
                var o = _lasers[j];
                if (!Clashable(o) || o.Team == a.Team || ((a.Clashed >> j) & 1) != 0
                    || o.Head.DistanceSquaredTo(a.Head) > d2max) continue;
                a.Clashed |= 1UL << j;
                o.Clashed |= 1UL << s;
                Vector2 at = (a.Head + o.Head) * 0.5f;
                Ev($"sniper clash {Main.TeamNames[a.Team]} {a.Value:0} vs {Main.TeamNames[o.Team]} {o.Value:0} at ({at.X:0},{at.Y:0})");
                bool fresh = true;
                foreach (var sh in _shocks)
                    if (sh.Bloom && sh.Pos.DistanceSquaredTo(at) < 40f * 40f && sh.MaxLife - sh.Life < 0.5f) { fresh = false; break; }
                if (!fresh) continue;
                // 光团放在附近所有激光头的中间（开局四束同时到，第一对先碰上的地方偏在一边）
                Vector2 sum = Vector2.Zero;
                int cnt = 0;
                foreach (var h in _lasers)
                    if (Clashable(h) && h.Head.DistanceSquaredTo(at) < 60f * 60f) { sum += h.Head; cnt++; }
                if (cnt > 0) at = sum / cnt;
                // 一大团青白光（BloomAmp）；0.33 秒起每 0.05 秒从半径 20 放出一圈青色细环，先放的扩得快，
                // 都在碰上以后 1.07 秒没了（视频 5.40 秒环在 30、44，5.75 秒在 45~98，6.0 秒在 61~115）
                _shocks.Add(new Shock { Pos = at, Life = 0.75f, MaxLife = 0.75f, Bloom = true });
                for (int k = 0; k < ClashRingSpeeds.Length; k++)
                {
                    float delay = 0.33f + 0.05f * k, life = 1.07f - delay;
                    _shocks.Add(new Shock { Pos = at, Delay = delay, Life = life, MaxLife = life, R0 = 20f, R1 = 20f + ClashRingSpeeds[k] * life,
                        Width = 2f, Linear = true, Col = new Color(0.35f, 0.82f, 1f) });
                }
            }
        }
    }

    private static bool Clashable(Laser l) => l != null && l.Launched && !l.Ended;

    private static readonly float[] ClashRingSpeeds = { 170f, 140f, 110f, 85f, 65f };

    // 碰撞火花每个点的五层同心圆（半径 / 叠加亮度）：几十个叠起来是白芯 + 往外约 20 像素一个 e 倍衰减的光晕
    private static readonly float[] SparkRadius = { 40f, 28f, 18f, 10f, 5f };
    private static readonly float[] SparkAlpha = { 0.025f, 0.03f, 0.045f, 0.06f, 0.06f };

    // 激光对撞那团光的强度 A（ln A 按时间分段线性）：视频 4.93 秒激光头碰上，0.02 秒 4.8、0.07 秒 10、0.12 秒 18、
    // 0.17 秒 35（最亮：白芯半径约 30，青色光晕到 70 像素）、0.32 秒 13、0.47 秒 6，0.75 秒没了
    private static readonly float[] BloomT = { 0f, 0.02f, 0.07f, 0.12f, 0.17f, 0.22f, 0.32f, 0.47f, 0.6f, 0.75f };
    private static readonly float[] BloomLnA = { 0.5f, 1.56f, 2.3f, 2.87f, 3.56f, 3.43f, 2.56f, 1.82f, 0.7f, -4f };

    private static float BloomAmp(float t)
    {
        if (t >= BloomT[^1]) return 0f;
        int j = 1;
        while (j < BloomT.Length - 1 && BloomT[j] < t) j++;
        float u = Math.Clamp((t - BloomT[j - 1]) / (BloomT[j] - BloomT[j - 1]), 0f, 1f);
        return MathF.Exp(BloomLnA[j - 1] + (BloomLnA[j] - BloomLnA[j - 1]) * u);
    }

    /// <summary>苍蝇统一的打转方向：1 = 屏幕顺时针，−1 = 逆时针，0 = 各转各的（SniperSwirlDir 的符号）。</summary>
    private float SwirlDir => S.SniperSwirlDir > 0f ? 1f : S.SniperSwirlDir < 0f ? -1f : 0f;

    /// <summary>
    /// 狙击子弹的运动（视频实测）。出膛前像一群苍蝇：从中心炸开，很快慢下来，在中心周围乱飞
    /// （转弯角速度随机游走；按要求都朝 SniperSwirlDir 绕中心转，像个漩涡，见 GameSettings），不涂色，身后拖一条淡拖尾。
    /// 激光头出膛那一刻苍蝇加速（速度往 Fast 收），朝同一个方向以固定半径转弯（每只一个半径，SniperTurnRadius*），
    /// 不受中心拉：在中心附近画出一个个交叠的圈，边飞边涂细线；飞得快的转得也快，所以涂色都集中在中心周围；
    /// 激光头飞出去 SniperCommitDelay 以后，谁的飞行方向差不多对准了自己在队形里的位置（或者等满 SniperConvergeTime）
    /// 谁就直线追过去 —— 一大把细线从花里收拢到激光头后面，像一把扫帚；追上以后就归队，跟着激光头走。
    /// 队形位置：激光头后面 SlotL、横向 SlotO + 所在细线的摆动。
    /// 墙都是轴对齐的，反弹就是镜像：子弹和激光头各记着碰墙次数的奇偶，哪一维不一致就先把子弹镜像到
    /// 激光头那一侧再算，算完再镜像回去；还没归队的子弹每一步都挑离自己的队形位置近的那个像。
    /// 返回 true = 还在出膛前乱飞（这一步不涂色）。
    /// </summary>
    private bool SteerSniper(ref Bullet b, float dt)
    {
        var l = _lasers[b.Group];
        if (l == null || l.Ended) { b.Group = -1; return false; }   // 激光结束了：各飞各的（也让出激光槽）
        l.Count++;
        l.Value += b.Value;
        if (l.Gx != null && b.Fid < l.GFlies && l.Age < S.SniperSwarmTime + S.SniperGhostSeconds)
        {
            int gi = b.Fid * l.GLen + l.GStep % l.GLen;
            l.Gx[gi] = b.X;
            l.Gy[gi] = b.Y;
            l.Gs[gi] = l.GStep;
        }

        if (b.Stage < 2)
        {
            float rx = b.X - l.Origin.X, ry = b.Y - l.Origin.Y, r = MathF.Sqrt(rx * rx + ry * ry) + 1e-3f;
            if (b.Stage == 0 && l.Launched)
            {
                // 出膛：都朝 SniperSwirlDir 转弯（为 0 时按当时绕中心转的方向，r × v 的符号），每只一个固定的转弯半径（大多数七八十，少数一两百）
                b.Stage = 1;
                float rad = S.SniperTurnRadiusMin + (S.SniperTurnRadiusMax - S.SniperTurnRadiusMin) * MathF.Pow(Rand01(), S.SniperTurnRadiusPow);
                b.Orbit = (SwirlDir != 0f ? SwirlDir : rx * b.VY - ry * b.VX >= 0f ? 1f : -1f) / rad;
            }
            if (b.Stage == 0)
            {
                // 乱飞：转弯角速度随机游走（均值 方向·SniperSwirlSpin）
                float tau = S.SniperWanderTau, dir = SwirlDir, steer;
                b.Spin += (dir * S.SniperSwirlSpin - b.Spin) * dt / tau + S.SniperWander * MathF.Sqrt(2f * dt / tau) * (Rand01() * 2f - 1f) * 1.732f;
                if (dir != 0f)
                {
                    // 漩涡：往 dir 方向的切线拐（离中心远了往里偏、近了往外偏），夹角越大拐得越急，走最近的那边
                    float beta = Math.Clamp((r - S.SniperLeashRadius) / S.SniperLeashWidth, -1f, 1f) * S.SniperSwirlTilt;
                    float cb = MathF.Cos(beta), sb = MathF.Sin(beta);
                    float tgx = (-ry * dir * cb - rx * sb) / r, tgy = (rx * dir * cb - ry * sb) / r;   // 切线 (−ry, rx)·dir/r 往中心转 beta
                    steer = S.SniperSwirlSteer * MathF.Atan2(b.VX * tgy - b.VY * tgx, b.VX * tgx + b.VY * tgy);
                }
                else
                {
                    // 各转各的（旧）：离中心远了就往中心那边拐（v × (−r) > 0 → 中心在右手边，往顺时针拐）
                    float lea = Math.Clamp((r - S.SniperLeashRadius) / S.SniperLeashWidth, 0f, 1f) * S.SniperLeashTurn;
                    steer = b.VY * rx - b.VX * ry >= 0f ? lea : -lea;
                }
                float w = (b.Spin + steer) * dt;
                float c = MathF.Cos(w), sn = MathF.Sin(w), ovx = b.VX;
                b.VX = ovx * c - b.VY * sn;
                b.VY = ovx * sn + b.VY * c;
                RelaxSpeed(ref b, b.Cruise, S.SniperBurstTau, dt);
                b.Fold = 0;                                      // 出膛前都算和激光头在同一侧
                return true;
            }
            // 打转：按固定半径转（角速度 = 速度 × 曲率），速度往 Fast 收
            float wt = b.Orbit * MathF.Sqrt(b.VX * b.VX + b.VY * b.VY) * dt, ct = MathF.Cos(wt), st = MathF.Sin(wt), ovx1 = b.VX;
            b.VX = ovx1 * ct - b.VY * st;
            b.VY = ovx1 * st + b.VY * ct;
            RelaxSpeed(ref b, b.Fast, S.SniperFlingTau, dt);
        }

        float v = S.SniperSpeed;
        float dx = l.Dir.X, dy = l.Dir.Y;
        const float hi = N - 0.01f;
        if (b.Stage < 3)
        {
            // 还没归队：挑离队形位置（约在激光头后面 SlotL）近的那个像
            float sx = l.Head.X - dx * b.SlotL, sy = l.Head.Y - dy * b.SlotL;
            float mxp = l.Head.X < N * 0.5f ? -b.X : 2f * hi - b.X;
            float myp = l.Head.Y < N * 0.5f ? -b.Y : 2f * hi - b.Y;
            int fx = MathF.Abs(sx - mxp) < MathF.Abs(sx - b.X) ? 1 : 0;
            int fy = MathF.Abs(sy - myp) < MathF.Abs(sy - b.Y) ? 2 : 0;
            b.Fold = (byte)(l.Fold ^ fx ^ fy);
        }

        // 镜像到激光头那一侧（离激光头近的那面墙）
        int diff = b.Fold ^ l.Fold;
        float px = b.X, py = b.Y, vx = b.VX, vy = b.VY;
        if ((diff & 1) != 0) { px = l.Head.X < N * 0.5f ? -px : 2f * hi - px; vx = -vx; }
        if ((diff & 2) != 0) { py = l.Head.Y < N * 0.5f ? -py : 2f * hi - py; vy = -vy; }
        float hx = l.Head.X - px, hy = l.Head.Y - py;
        if (hx * hx + hy * hy < 60f * 60f) l.Near++;
        if (b.Stage == 3) l.Joined++;

        // 队形位置：激光头每反射一次，队形的左右就翻一次面；
        // 所在细线在「激光头走过的距离 − 落后距离」处的横向摆动（偏移 y，斜率 dy/ds → 横向速度）
        float sg = ((l.Fold ^ (l.Fold >> 1)) & 1) != 0 ? -1f : 1f;
        float s = l.FlyTime * v - b.SlotL, y = 0f, dyds = 0f;
        for (int j = b.Lane * 3, j1 = j + 3; j < j1; j++)
        {
            float ph = l.WobK[j] * s + l.WobP[j];
            y += l.WobA[j] * MathF.Sin(ph);
            dyds += l.WobA[j] * l.WobK[j] * MathF.Cos(ph);
        }
        float lat = (b.SlotO + y) * sg, lv = dyds * v * sg;
        float ex = l.Head.X - dx * b.SlotL - dy * lat - px;
        float ey = l.Head.Y - dy * b.SlotL + dx * lat - py;

        if (b.Stage == 1)
        {
            // 转够了以后飞行方向对准了自己的队形位置（或者等太久了）→ 直线追过去
            float el = MathF.Sqrt(ex * ex + ey * ey), sp = MathF.Sqrt(vx * vx + vy * vy);
            if ((l.FlyTime >= S.SniperCommitDelay && ex * vx + ey * vy > S.SniperCommitCos * el * sp)
                || l.FlyTime >= S.SniperConvergeTime) b.Stage = 2;
            else return false;
        }

        // 追队形位置：前馈（激光头速度 + 横向摆动速度）+ 位置误差 × 增益，平滑转向，限速；
        // 归队以后同一套控制让它贴着队形走
        float gain = b.Stage == 2 ? S.SniperCatchGain : 5f;
        float dvx = dx * v - dy * lv + ex * gain, dvy = dy * v + dx * lv + ey * gain;
        float dl = MathF.Sqrt(dvx * dvx + dvy * dvy), vmax = b.Catch;
        if (dl > vmax) { dvx *= vmax / dl; dvy *= vmax / dl; }
        float k = 1f - MathF.Exp(-10f * dt);
        vx += (dvx - vx) * k;
        vy += (dvy - vy) * k;
        b.VX = (diff & 1) != 0 ? -vx : vx;
        b.VY = (diff & 2) != 0 ? -vy : vy;
        if (b.Stage == 2 && ex * ex + ey * ey < 8f * 8f) b.Stage = 3;
        return false;
    }

    private static readonly int[] FlyReportSteps = { 60, 78, 90, 108, 120, 138, 156, 168, 180, 192, 204, 216, 228, 246, 270 };

    /// <summary>调试（--events）：开局那几秒苍蝇离中心多远、飞多快、各阶段各几只。</summary>
    private void FlyReport(Laser l, int slot)
    {
        if (Array.IndexOf(FlyReportSteps, l.GStep) < 0) return;
        var rs = new List<float>();
        var vs = new List<float>();
        Span<int> st = stackalloc int[4];
        for (int i = 0; i < _bulletCount; i++)
        {
            ref Bullet b = ref _bullets[i];
            if (b.Life <= 0f || b.Kind != Kind.Sniper || b.Group != slot) continue;
            float rx = b.X - l.Origin.X, ry = b.Y - l.Origin.Y;
            rs.Add(MathF.Sqrt(rx * rx + ry * ry));
            vs.Add(MathF.Sqrt(b.VX * b.VX + b.VY * b.VY));
            st[Math.Min((int)b.Stage, 3)]++;
        }
        if (rs.Count == 0) return;
        rs.Sort();
        vs.Sort();
        static float P(List<float> a, float q) => a[Math.Min(a.Count - 1, (int)(q * a.Count))];
        Ev($"fly {Main.TeamNames[l.Team]} n={rs.Count} r {P(rs, 0.1f):0}/{P(rs, 0.5f):0}/{P(rs, 0.9f):0}/{rs[^1]:0}"
           + $" v {P(vs, 0.1f):0}/{P(vs, 0.5f):0}/{P(vs, 0.9f):0} stage {st[0]}/{st[1]}/{st[2]}/{st[3]}"
           + $" head {l.Head.DistanceTo(l.Origin):0} glow {LineGlow(l):0.00}");
    }

    /// <summary>速度大小往 target 收（方向不变）。</summary>
    private static void RelaxSpeed(ref Bullet b, float target, float tau, float dt)
    {
        float sp = MathF.Sqrt(b.VX * b.VX + b.VY * b.VY);
        if (sp < 1e-3f) return;
        float k = (sp + (target - sp) * (1f - MathF.Exp(-dt / tau))) / sp;
        b.VX *= k;
        b.VY *= k;
    }

    /// <summary>风暴分几圈打：每圈从剩余火力里记掉 1/剩余圈数，打出去的是 风暴火力 × StormPulseValueFraction
    /// （视频里每圈带的比三圈平分多，约一半多一点）。</summary>
    private void StepStorm(Faction f, float dt)
    {
        if (f.StormPulsesLeft <= 0) return;
        f.StormTimer -= dt;
        if (f.StormTimer > 0f) return;
        double v = f.StormFire / f.StormPulsesLeft;
        f.StormFire -= v;
        f.StormPulsesLeft--;
        f.StormTimer += S.StormPulseInterval;
        FireStorm(f, v * S.StormPulses * S.StormPulseValueFraction);
    }

    /// <summary>风暴的一圈：从中心向四周射出一圈子弹，护盾圆盘闪一下。子弹数 = 数值/100，36..StormBulletsMax。
    /// 同一次风暴的几圈角度完全一样（视频实测），后面的圈沿前一圈涂出的射线飞。</summary>
    private void FireStorm(Faction f, double value)
    {
        int n = (int)Math.Clamp(Math.Round(value / 100.0), 36, S.StormBulletsMax);
        float each = (float)(value / n);
        float off = f.StormAngle;
        float speed = S.BulletSpeed * S.StormSpeedMultiplier;
        for (int k = 0; k < n; k++)
        {
            // 视频里风暴环从中心长出来（先把护盾内的圆盘照亮），不是从护盾边上出发
            Vector2 dir = Vector2.FromAngle(off + k * Mathf.Tau / n);
            int i = SpawnBullet(f.Team, f.Center + dir * StormStartRadius, dir * speed, each, Kind.Storm);
            // 视频里风暴环是一点点散掉的，不是同时消失：每颗的寿命各自抖一点
            if (S.StormBulletLife > 0f)
                _bullets[i].Life = Math.Max(0.1f, S.StormBulletLife + RandRange(-S.StormLifeJitter, S.StormLifeJitter));
        }
    }

    private void FireBigBall(Faction f, double value, float angle, float startRadius = -1f)
    {
        Ev($"bigball {Main.TeamNames[f.Team]} {value:0}");
        Vector2 dir = Vector2.FromAngle(angle);
        float m = BallSpeedMul(value);
        Balls.Add(new BigBall(f.Team, f.Center + dir * 14f, dir * (S.BigBallSpeed * m), value, _time, startRadius) { SpeedMul = m });
    }

    /// <summary>
    /// 资金模式的胜率用（WinModel）：各队飞行中的子弹按种类加起来，按 Value × Dmg 算（就是打到护盾上的伤害）。
    /// own[队, 种类]：0 机枪、1 点防、2 狙击、3 风暴；near[队, 0 / 1]：离这队中心 r1 / r2 以内的敌方进攻子弹（不算点防）。
    /// </summary>
    public void BulletSums(double[,] own, double[,] near, float r1, float r2)
    {
        Array.Clear(own);
        Array.Clear(near);
        float q1 = r1 * r1, q2 = r2 * r2;
        for (int i = 0; i < _bulletCount; i++)
        {
            ref Bullet b = ref _bullets[i];
            if (b.Life <= 0f || (uint)b.Team >= 4u) continue;
            double v = b.Value * (double)b.Dmg;
            own[b.Team, (int)b.Kind] += v;
            if (b.Kind == Kind.Pd) continue;
            for (int t = 0; t < 4; t++)
            {
                if (t == b.Team || !Factions[t].Alive) continue;
                float dx = b.X - Factions[t].Center.X, dy = b.Y - Factions[t].Center.Y;
                float d2 = dx * dx + dy * dy;
                if (d2 < q1) near[t, 0] += v;
                if (d2 < q2) near[t, 1] += v;
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  灭亡 / 胜负
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 护盾已破的中心被打中：阵营灭亡，中心一团白光；机枪（连同没打完的狙击/风暴/大球火力）变成一个大球，
    /// 左侧它的每颗弹珠也各变成一个大球，一起四散飞出（先是小球，约 0.3 秒长到应有的大小）。
    /// </summary>
    private void KillFaction(Faction f, int by, string how)
    {
        if (!f.Alive || f.Reviving) return;          // 复活弹珠还在路上：中心打不死
        // 每个阵营有一次复活机会：第一次阵亡时左侧数值最大的弹珠传送到阵营中心，落地变成护盾（从中心长出来），额外弹珠 −1
        if (!f.Revived)
        {
            Vector2 from = Vector2.Zero;
            float fromR = 0f;
            float v = _main?.Left != null ? _main.Left.TakeLargestBall(f.Team, out from, out fromR) : -1f;
            if (v > 0f)
            {
                f.Revived = true;
                double thr0 = f.ChargeThreshold, ch0 = f.Charge;
                int ex0 = f.ExtraMarbles;
                f.ExtraMarbles = Math.Max(0, f.ExtraMarbles - 1);
                f.Charge *= f.ChargeThreshold / thr0;            // 充能圈进度不变：门槛降了，别马上又冒出一颗弹珠
                Ev($"revive {Main.TeamNames[f.Team]} by {Main.TeamNames[by]} {how} shield={v:0} extra {ex0}->{f.ExtraMarbles} charge {ch0:0}/{thr0:0} -> {f.Charge:0}/{f.ChargeThreshold:0}");
                f.Reviving = true;
                AddWarp(f, ToLocal(from), fromR, v);
                return;
            }
        }
        Ev($"killed {Main.TeamNames[f.Team]} by {Main.TeamNames[by]} {how} mg={f.MgFire:0}");
        f.Alive = false;
        f.DeathTime = _time;
        // 视频：有阵营被淘汰，每个还活着的阵营充能都 +480K（不光是击杀者）
        for (int t = 0; t < Main.TeamCount; t++)
            if (t != f.Team) QueueChargeBonus(t, S.EliminationCharge, S.EliminationChargeDelay, $"{Main.TeamNames[f.Team]} eliminated");
        f.Shield = 0.0;
        f.ShieldShown = 0.0;

        var values = new List<double>();
        double pool = f.MgFire + f.SniperFire + f.StormFire + f.BigBallFire;
        f.MgFire = f.SniperFire = f.StormFire = f.BigBallFire = 0.0;
        if (pool >= 1.0) values.Add(pool);
        List<float> marbles = _main?.Left?.RemoveTeamBalls(f.Team);
        if (marbles != null)
            foreach (float v in marbles)
                if (v >= 1f) values.Add(v);

        float off = RandRange(0f, Mathf.Tau);
        for (int i = 0; i < values.Count; i++)
            FireBigBall(f, values[i], off + i * Mathf.Tau / values.Count + RandRange(-0.2f, 0.2f), S.BigBallSpawnRadius);

        _shocks.Add(new Shock { Pos = f.Center, Life = 0.8f, MaxLife = 0.8f, R0 = 20f, R1 = 60f, Flash = true, Col = Colors.White });
        _shocks.Add(new Shock
        {
            Pos = f.Center, Life = 1.2f, MaxLife = 1.2f, R0 = 10f, R1 = 260f, Width = 10f, Col = RingColors[f.Team],
        });
        f.AddNote("阵营覆灭", 0.0);
        _waves.Add(new DeathWave { Pos = f.Center, T0 = _time, Team = f.Team });
    }

    private void AddRevive(Faction f)
    {
        var rv = new Revive
        {
            Pos = f.Center, Team = f.Team, Spin = RandRange(0f, Mathf.Tau),
            RayLen = new float[ReviveRays], SparkAng = new float[ReviveSparks],
            SparkSpeed = new float[ReviveSparks], SparkLife = new float[ReviveSparks],
        };
        for (int i = 0; i < ReviveRays; i++) rv.RayLen[i] = (i % 2 == 0 ? 1f : 0.6f) * RandRange(0.85f, 1.15f);
        for (int i = 0; i < ReviveSparks; i++)
        {
            rv.SparkAng[i] = RandRange(0f, Mathf.Tau);
            rv.SparkSpeed[i] = RandRange(250f, 700f);
            rv.SparkLife[i] = RandRange(0.6f, 1.2f);
        }
        _revives.Add(rv);
    }

    /// <summary>复活传送开始：from 是左侧那颗弹珠的位置（场内坐标），r 是它的半径；飞行时间按距离 0.8~1.4 秒</summary>
    private void AddWarp(Faction f, Vector2 from, float r, double value)
    {
        Vector2 to = f.Center, d = to - from;
        float len = MathF.Max(d.Length(), 1f);
        Vector2 n = new Vector2(-d.Y, d.X) / len;
        if ((n.Y > 0f) != ((from.Y + to.Y) * 0.5f < N * 0.5f)) n = -n;      // 弧往屏幕上下中线那边鼓（不出屏幕）
        var w = new Warp
        {
            From = from, Ctrl = (from + to) * 0.5f + n * (0.2f * len), R = MathF.Max(r, 6f), Value = value, Team = f.Team,
            Fly = Math.Clamp(0.55f + len / 1500f, 0.8f, 1.4f), Spin = RandRange(0f, Mathf.Tau),
            DustU = new float[WarpDust], DustLife = new float[WarpDust], DustVel = new Vector2[WarpDust],
        };
        for (int i = 0; i < WarpDust; i++)
        {
            w.DustU[i] = RandRange(0.04f, 0.96f);
            w.DustLife[i] = RandRange(0.3f, 0.55f);
            w.DustVel[i] = Vector2.FromAngle(RandRange(0f, Mathf.Tau)) * RandRange(15f, 60f);
        }
        _warps.Add(w);
    }

    /// <summary>复活弹珠落到阵营中心：这才有护盾（环从中心长出来）、复活爆光、提示</summary>
    private void ReviveArrive(Warp w)
    {
        w.Arrived = true;
        var f = Factions[w.Team];
        f.Reviving = false;
        f.LandTime = _time;
        if (!f.Alive) return;
        f.Shield += w.Value;
        f.HitFlash = 0.15f;
        f.AddNote("复活护盾", w.Value);
        AddRevive(f);
        Ev($"revive arrive {Main.TeamNames[f.Team]} shield={f.Shield:0}");
    }

    private void CheckWin()
    {
        for (int t = 0; t < 4; t++)
        {
            if (Field.Count[t] >= TerritoryField.Total) { Win(t); return; }
        }
        int alive = 0, last = -1;
        for (int t = 0; t < 4; t++)
            if (Factions[t].Alive) { alive++; last = t; }
        if (alive == 0)
        {
            int best = 0;
            for (int t = 1; t < 4; t++) if (Field.Count[t] > Field.Count[best]) best = t;
            Win(best);
        }
        else if (alive == 1)
        {
            // 只剩一家、场上没有别家的大球了、剩下零星几个像素：直接判胜，免得等子弹扫尾。
            // 自己的大球不算：敌人都没了它们就一直在场上弹（弹珠台还在出新的），等不到 0 个；
            // 角落里的几个像素大球够不着，子弹也不一定扫得到 —— 以前这样的局永远结束不了。
            // 保底：最后一个敌人倒下 SoleSurvivorWin 秒以后直接判胜（别家留下的大球还在涂色也一样）
            bool enemyBalls = false;
            foreach (var b in Balls)
                if (!b.Dead && b.Team != last) { enemyBalls = true; break; }
            float lastDeath = 0f;
            for (int t = 0; t < 4; t++)
                if (!Factions[t].Alive) lastDeath = MathF.Max(lastDeath, Factions[t].DeathTime);
            if ((!enemyBalls && Field.Count[last] >= 0.999 * TerritoryField.Total) || _time - lastDeath >= SoleSurvivorWin) Win(last);
        }
    }

    /// <summary>只剩一家以后最多再打多少秒（模拟时间）就判胜。校准数据里正常扫尾基本 35 秒以内。</summary>
    private const float SoleSurvivorWin = 60f;

    private void Win(int team)
    {
        Ev($"win {Main.TeamNames[team]} terr={Field.Count[team] / (double)TerritoryField.Total:P1}");
        Winner = team;
        _overTimer = 8f;
        _shocks.Add(new Shock
        {
            Pos = Factions[team].Center, Life = 2f, MaxLife = 2f, R0 = 20f, R1 = 1400f, Width = 16f,
            Col = RingColors[team],
        });
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  渲染：CPU 合成
    // ════════════════════════════════════════════════════════════════════════════

    private void BuildLuts()
    {
        for (int t = 0; t < 4; t++)
        {
            // 粒子：灰白小点（视频实测 R≈G≈B），只带一点点队色。
            // 3×3 核：中心 1、上下左右 0.17、四角 0.02（视频实测孤立粒子 0.15～0.19 / ≈0，均方根半径 ≈0.65 px）；
            // 亮度档 0..31 对应倍率 0..2
            Color pc = Colors.White.Lerp(RingColors[t], 0.1f);
            for (int l = 0; l < PartLevels; l++)
            {
                float k = (l + 0.5f) / PartLevels * 2f * 62f;
                int o = (t * PartLevels + l) * 3;
                _partLut[o + 0] = TerritoryField.Pack(pc.R * k, pc.G * k, pc.B * k);
                _partLut[o + 1] = TerritoryField.Pack(pc.R * k * 0.17f, pc.G * k * 0.17f, pc.B * k * 0.17f);
                _partLut[o + 2] = TerritoryField.Pack(pc.R * k * 0.02f, pc.G * k * 0.02f, pc.B * k * 0.02f);
            }

            // 子弹：5×5 高斯弹头（σ = 1，狙击 0.67）+ 曳光拖尾。视频里弹头是偏白的队色小亮点，拖尾是细细的一条线
            for (int kind = 0; kind < 4; kind++)
            {
                Color bc = (Kind)kind switch
                {
                    Kind.Sniper => RingColors[t].Lerp(Colors.White, 0.8f),
                    Kind.Storm => RingColors[t].Lerp(Colors.White, 0.6f),
                    _ => RingColors[t].Lerp(Colors.White, 0.55f),
                };
                float headGain = (Kind)kind switch
                {
                    Kind.Sniper => 0.8f,
                    Kind.Storm => 0.9f,
                    Kind.Pd => 0.8f,
                    _ => 1f,
                };
                float trailGain = (Kind)kind switch
                {
                    Kind.Sniper => 0.5f,
                    Kind.Pd => 0.45f,
                    _ => 0.6f,
                };
                int key = kind * 4 + t;
                float k2 = (Kind)kind == Kind.Sniper ? 1.1f : 0.5f;         // 狙击的弹头更小（σ ≈ 0.67）
                for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    float w = MathF.Exp(-(dx * dx + dy * dy) * k2) * headGain;
                    _bulletLut[key * 25 + (dy + 2) * 5 + (dx + 2)] =
                        TerritoryField.Pack(bc.R * 255f * w, bc.G * 255f * w, bc.B * 255f * w);
                }
                _trailCol[key * 3 + 0] = bc.R * 255f * trailGain;
                _trailCol[key * 3 + 1] = bc.G * 255f * trailGain;
                _trailCol[key * 3 + 2] = bc.B * 255f * trailGain;
            }
        }

        // 护盾环径向亮度表：按实测点分段线性插值
        for (int i = 0; i < _ringProfile.Length; i++)
        {
            float d = i / (float)RingLutPerPx - RingIn;
            int j = 1;
            while (j < RingProfileD.Length - 1 && RingProfileD[j] < d) j++;
            float d0 = RingProfileD[j - 1], d1 = RingProfileD[j];
            float u = Math.Clamp((d - d0) / (d1 - d0), 0f, 1f);
            _ringProfile[i] = RingProfileV[j - 1] + (RingProfileV[j] - RingProfileV[j - 1]) * u;
        }
    }

    private void Render()
    {
        ProfStart();
        Field.Compose(_time);
        ProfMark(6);
        DrawShieldsToField();
        ProfMark(7);
        DrawBallsToField();
        ProfMark(8);
        DrawParticlesToField();
        DrawBallCoreDots();
        ProfMark(9);
        DrawBulletsToField();
        ProfMark(10);
        Field.Upload();
        UpdateComposeBalls();
        ProfMark(11);
    }

    /// <summary>大球球芯压暗领土底色：把球心、半径² 和压暗系数传给合成着色器。</summary>
    private void UpdateComposeBalls()
    {
        int n = Math.Min(Balls.Count, MaxShaderBalls);
        float keep = (int)(BallCoreKeep * 256f) / 256f;          // 和 CPU 上压暗护盾光的定点系数一致
        for (int i = 0; i < n; i++)
        {
            var b = Balls[i];
            _ballUniform[i] = new Vector4(b.Pos.X, b.Pos.Y, b.Radius * b.Radius, keep);
        }
        _composeMat.SetShaderParameter("ball_count", n);
        if (n > 0) _composeMat.SetShaderParameter("balls", _ballUniform);

        // 护盾特效：强度按护盾数值的对数从 1000 万的 ShieldFxMin 涨到 30 亿的 1；环刚长出来时跟着淡入
        int m = 0;
        for (int t = 0; t < 4; t++)
        {
            var f = Factions[t];
            if (!f.Alive || f.ShieldShown <= 1.0 || f.RingR <= 30f) continue;
            float u = Math.Clamp(((float)Math.Log10(f.ShieldShown) - 7f) / 2.5f, 0f, 1f);
            float s = (S.ShieldFxMin + (1f - S.ShieldFxMin) * MathF.Pow(u, S.ShieldFxCurve)) * MathF.Min(1f, (f.RingR - 30f) / 30f) * S.ShieldFx;
            Color rc = RingColors[t].Lerp(Colors.White, 0.2f);
            _shieldUniform[m] = new Vector4(f.Center.X, f.Center.Y, f.RingR, s);
            _shieldColUniform[m] = new Vector4(rc.R, rc.G, rc.B, MathF.Min(1f, f.HitFlash / 0.15f) * S.ShieldHitFlash);
            m++;
        }
        _composeMat.SetShaderParameter("shield_count", m);
        if (m > 0)
        {
            _composeMat.SetShaderParameter("shield_edge", S.ShieldEdgeGain);
            _composeMat.SetShaderParameter("shields", _shieldUniform);
            _composeMat.SetShaderParameter("shield_cols", _shieldColUniform);
        }
        _composeMat.SetShaderParameter("time", _time);

        // 冲击波
        int nw = 0;
        for (int i = 0; i < _waves.Count && nw < 4; i++)
        {
            var w = _waves[i];
            float u = (_time - w.T0) / S.DeathWaveLife;
            if (u < 0f || u >= 1f) continue;
            Color wc = RingColors[w.Team].Lerp(Colors.White, 0.25f);
            _waveUniform[nw] = new Vector4(w.Pos.X, w.Pos.Y, WaveRadius(u), MathF.Pow(1f - u, 1.5f) * MathF.Min(1f, u * 20f));
            _waveColUniform[nw] = new Vector4(wc.R, wc.G, wc.B, 1f);
            nw++;
        }
        _composeMat.SetShaderParameter("wave_count", nw);
        if (nw > 0)
        {
            _composeMat.SetShaderParameter("waves", _waveUniform);
            _composeMat.SetShaderParameter("wave_cols", _waveColUniform);
        }
    }

    // 阵营覆灭冲击波：从中心往外扩，先快后慢（半径 = Max·(1 − (1 − u)³)）
    private struct DeathWave { public Vector2 Pos; public float T0, LastR; public int Team, NBullet, NBall; public bool Done; }
    private readonly List<DeathWave> _waves = new();
    private readonly Vector4[] _waveUniform = new Vector4[4];
    private readonly Vector4[] _waveColUniform = new Vector4[4];
    /// <summary>
    /// 冲击波波前这一步扫过的子弹（激光队形里的除外）和大球：子弹速度方向往外偏（快慢不变），大球往外踢一脚
    /// （越大的球踢得越少，速度最多到 BigBallSpeed × 速度倍数 × DeathWaveBallMaxSpeed，已经更快的不再加）。越往后越弱。
    /// </summary>
    private void StepDeathWaves()
    {
        for (int k = 0; k < _waves.Count; k++)
        {
            var w = _waves[k];
            float u = (_time - w.T0) / S.DeathWaveLife;
            if (u < 0f) continue;
            if (u >= 1f)
            {
                if (!w.Done) Ev($"death wave {Main.TeamNames[w.Team]} turned {w.NBullet} bullets, kicked {w.NBall} balls");
                w.Done = true;
                _waves[k] = w;
                continue;
            }
            float r0 = w.LastR, r1 = WaveRadius(u), fade = 1f - u;
            w.LastR = r1;
            _waves[k] = w;
            if (r1 <= r0) continue;
            float r02 = r0 * r0, r12 = r1 * r1;

            float turn = S.DeathWaveBulletTurn * fade;
            if (turn > 0f)
                for (int i = 0; i < _bulletCount; i++)
                {
                    ref Bullet b = ref _bullets[i];
                    if (b.Life <= 0f || b.Group >= 0) continue;
                    float dx = b.X - w.Pos.X, dy = b.Y - w.Pos.Y, d2 = dx * dx + dy * dy;
                    if (d2 <= r02 || d2 > r12) continue;
                    float d = MathF.Sqrt(d2) + 1e-3f, sp = MathF.Sqrt(b.VX * b.VX + b.VY * b.VY);
                    if (sp < 1e-3f) continue;
                    float nx = b.VX / sp + dx / d * turn, ny = b.VY / sp + dy / d * turn;
                    float nl = MathF.Sqrt(nx * nx + ny * ny);
                    if (nl < 1e-4f) { nx = dx / d; ny = dy / d; nl = 1f; }
                    b.VX = nx / nl * sp;
                    b.VY = ny / nl * sp;
                    b.Age = 0f;                                  // 拖尾从转弯处重新长出来
                    w.NBullet++;
                }

            foreach (var ball in Balls)
            {
                if (ball.Dead) continue;
                Vector2 dv = ball.Pos - w.Pos;
                float d2 = dv.LengthSquared();
                if (d2 <= r02 || d2 > r12) continue;
                float sp0 = ball.Vel.Length();
                float m = (float)Math.Pow(Math.Max(ball.Value, 1.0) / 1e8, S.BigBallMassExponent);
                ball.Vel += dv / (MathF.Sqrt(d2) + 1e-3f) * (S.DeathWaveBallKick * fade / (1f + m));
                float cap = MathF.Max(sp0, S.BigBallSpeed * ball.SpeedMul * S.DeathWaveBallMaxSpeed), sp = ball.Vel.Length();
                if (sp > cap) ball.Vel *= cap / sp;
                w.NBall++;
            }
            _waves[k] = w;
        }
    }

    private float WaveRadius(float u) => S.DeathWaveRadius * (1f - (1f - u) * (1f - u) * (1f - u));
    private readonly float[] _wvX = new float[4], _wvY = new float[4], _wvR = new float[4], _wvP = new float[4];

    private readonly Vector4[] _shieldUniform = new Vector4[4];
    private readonly Vector4[] _shieldColUniform = new Vector4[4];

    /// <summary>逐通道饱和加色（整数增量）。A 通道是领土打包码，原样保留。</summary>
    private static uint AddRgbI(uint p, int r, int g, int b)
    {
        int R = (int)(p & 0xFFu) + r;
        int G = (int)((p >> 8) & 0xFFu) + g;
        int B = (int)((p >> 16) & 0xFFu) + b;
        if (R > 255) R = 255;
        if (G > 255) G = 255;
        if (B > 255) B = 255;
        return (uint)R | ((uint)G << 8) | ((uint)B << 16) | (p & 0xFF000000u);
    }

    /// <summary>逐通道饱和加色（浮点增量）。</summary>
    private static uint AddRgb(uint p, float r, float g, float b)
    {
        int R = (int)(p & 0xFFu) + (int)r;
        int G = (int)((p >> 8) & 0xFFu) + (int)g;
        int B = (int)((p >> 16) & 0xFFu) + (int)b;
        if (R > 255) R = 255;
        if (G > 255) G = 255;
        if (B > 255) B = 255;
        return (uint)R | ((uint)G << 8) | ((uint)B << 16) | (p & 0xFF000000u);
    }

    /// <summary>
    /// 护盾环：在 CPU 上按视频实测的径向亮度表加色（峰值 = 环色，内侧光晕拖 30 多像素，外侧短一些）。
    /// 护盾破了只剩 30% 的淡环；被打中时闪亮。
    /// </summary>
    private unsafe void DrawShieldsToField()
    {
        float[] prof = _ringProfile;
        int profMax = prof.Length - 1;
        uint* px = Field.Px;
        for (int t = 0; t < 4; t++)
        {
            var f = Factions[t];
            if (!f.Alive) continue;
            // 护盾为 0：只有一圈淡细线（DrawGlowInner 里画）；护盾刚有的时候环从中心长出来，半径 30 → 60 之间跟着淡入
            if (f.ShieldShown <= 1.0 || f.RingR <= 30f) continue;
            float R = f.RingR;
            float k = MathF.Min(1f, (f.RingR - 30f) / 30f);
            if (f.HitFlash > 0f) k *= 1f + 2.5f * f.HitFlash * S.ShieldHitFlash;
            Color rc = RingColors[t];
            k *= RingGain * S.ShieldEdgeGain;
            float cr = rc.R * 255f * k, cg = rc.G * 255f * k, cb = rc.B * 255f * k;
            float cx = f.Center.X, cy = f.Center.Y;
            float rIn = MathF.Max(0f, R - RingIn), rOut = R + RingOut;
            float rIn2 = rIn * rIn, rOut2 = rOut * rOut;
            int y0 = Math.Max(0, (int)(cy - rOut)), y1 = Math.Min(N - 1, (int)(cy + rOut));

            Parallel.For(y0, y1 + 1, y =>
            {
                float dy = y + 0.5f - cy;
                float dy2 = dy * dy;
                if (dy2 >= rOut2) return;
                float half = MathF.Sqrt(rOut2 - dy2);
                int xa = Math.Max(0, (int)(cx - half)), xb = Math.Min(N - 1, (int)(cx + half));
                // 环内侧的空心部分直接跳过
                int holeA = int.MaxValue, holeB = int.MinValue;
                if (dy2 < rIn2)
                {
                    float inner = MathF.Sqrt(rIn2 - dy2);
                    holeA = (int)MathF.Ceiling(cx - inner);
                    holeB = (int)MathF.Floor(cx + inner) - 1;
                }
                uint* row = px + y * N;
                for (int x = xa; x <= xb; x++)
                {
                    if (x >= holeA && x <= holeB) { x = holeB; continue; }
                    float dx = x + 0.5f - cx;
                    float r = MathF.Sqrt(dx * dx + dy2);
                    int idx = (int)((r - R + RingIn) * RingLutPerPx);
                    if ((uint)idx > (uint)profMax) continue;
                    float w = prof[idx];
                    if (w <= 0.004f) continue;
                    row[x] = AddRgb(row[x], cr * w, cg * w, cb * w);
                }
            });
        }
    }

    /// <summary>
    /// 大球本体（黑芯 + 亮环 + 内发光，环外没有光晕）直接画进领土画布。
    /// 画在粒子之前：被吸进来的粒子会在黑色球芯上显出小亮点，和视频一致。数值文字在顶层画。
    /// </summary>
    private unsafe void DrawBallsToField()
    {
        uint* px = Field.Px;
        for (int i = 0; i < Balls.Count; i++)
        {
            var b = Balls[i];
            float R = b.Radius;
            float w = MathF.Max(4f, R * 0.5f);
            float dMin = -R, dMax = BallEdge;
            int lutLen = (int)((dMax - dMin) * BallLutPerPx) + 2;
            if (_ballProfile.Length < lutLen) _ballProfile = new float[lutLen * 2];
            float[] prof = _ballProfile;
            for (int j = 0; j < lutLen; j++)
            {
                float d = dMin + j / (float)BallLutPerPx;
                float v;
                if (d <= -w) v = 0f;
                else if (d < -3f) { float u = (d + w) / (w - 3f); v = u * u; }
                else if (d <= 1f) v = 1f;
                else v = MathF.Max(0f, 2f - d);              // 环外 1 像素抗锯齿，不画光晕
                prof[j] = v;
            }

            float k = b.HitFlash > 0f ? 1f + 1.5f * b.HitFlash / 0.25f : 1f;
            Color rc = RingColors[b.Team];
            float cr = rc.R * 255f * k, cg = rc.G * 255f * k, cb = rc.B * 255f * k;
            float cx = b.Pos.X, cy = b.Pos.Y;
            float rOut = R + dMax, rOut2 = rOut * rOut, R2 = R * R;
            uint keep = (uint)(BallCoreKeep * 256f);
            int y0 = Math.Max(0, (int)(cy - rOut)), y1 = Math.Min(N - 1, (int)(cy + rOut));

            Parallel.For(y0, y1 + 1, y =>
            {
                float dy = y + 0.5f - cy;
                float dy2 = dy * dy;
                if (dy2 >= rOut2) return;
                float half = MathF.Sqrt(rOut2 - dy2);
                int xa = Math.Max(0, (int)(cx - half)), xb = Math.Min(N - 1, (int)(cx + half));
                uint* row = px + y * N;
                for (int x = xa; x <= xb; x++)
                {
                    float dx = x + 0.5f - cx;
                    float r2 = dx * dx + dy2;
                    uint p = row[x];
                    if (r2 < R2)
                    {
                        // 球芯：压暗已经叠上的护盾光（领土底色在合成着色器里按同样的比例压暗）
                        p = (((p & 0xFFu) * keep) >> 8) | (((((p >> 8) & 0xFFu) * keep) >> 8) << 8)
                          | (((((p >> 16) & 0xFFu) * keep) >> 8) << 16) | (p & 0xFF000000u);
                    }
                    int idx = (int)((MathF.Sqrt(r2) - R - dMin) * BallLutPerPx);
                    float v = (uint)idx < (uint)lutLen ? prof[idx] : 0f;
                    row[x] = v > 0.004f ? AddRgb(p, cr * v, cg * v, cb * v) : p;
                }
            });
        }
    }

    /// <summary>
    /// 球芯闪烁亮点（视频实测：18M 的球在自家密集领土里每帧约 160 个，只亮一帧、前后帧不相关；
    /// 每 1000 px² 的密度从 0.2 倍半径处 1.5 升到 0.5 倍以外 ≈ 6–7；亮度中位 ≈ 118，比普通粒子亮一倍多）。
    /// 上次绘制以来每吸进一个粒子，按 BallCoreDotFraction 的概率在球芯里随机位置画一个 3×3 亮点。独立的随机数，不影响模拟。
    /// </summary>
    private unsafe void DrawBallCoreDots()
    {
        uint* px = Field.Px;
        float frac = S.BallCoreDotFraction;
        for (int i = 0; i < Balls.Count; i++)
        {
            var b = Balls[i];
            float R = b.Radius - 2f;
            for (int t = 0; t < 4; t++)
            {
                int intake = b.CoreIntake[t];
                b.CoreIntake[t] = 0;
                if (intake <= 0 || b.Dead || R < 4f) continue;
                float want = intake * frac;
                int m = (int)want;
                if (FxRand01() < want - m) m++;
                Color pc = Colors.White.Lerp(RingColors[t], 0.1f);
                for (int k = 0; k < m; k++)
                {
                    // 半径分布：面密度在 0.5 倍半径以内按 r 线性变小（拒绝采样），外面均匀
                    float r = 0f;
                    for (int tries = 0; tries < 4; tries++)
                    {
                        r = R * MathF.Sqrt(FxRand01());
                        if (FxRand01() * 0.5f * R < r) break;
                    }
                    float a = FxRand01() * MathF.Tau;
                    int ix = (int)(b.Pos.X + r * MathF.Cos(a)), iy = (int)(b.Pos.Y + r * MathF.Sin(a));
                    if ((uint)(ix - 1) >= N - 2 || (uint)(iy - 1) >= N - 2) continue;
                    float u = FxRand01();
                    float L = 55f + 150f * MathF.Pow(u, 1.3f);          // 亮度 p10/50/90 ≈ 62/116/186（视频 62/118/193）
                    uint cC = TerritoryField.Pack(pc.R * L, pc.G * L, pc.B * L);
                    uint cE = TerritoryField.Pack(pc.R * L * 0.24f, pc.G * L * 0.24f, pc.B * L * 0.24f);
                    uint cD = TerritoryField.Pack(pc.R * L * 0.10f, pc.G * L * 0.10f, pc.B * L * 0.10f);
                    uint* q = px + iy * N + ix;
                    q[0] = TerritoryField.AddSat(q[0], cC);
                    q[-1] = TerritoryField.AddSat(q[-1], cE);
                    q[1] = TerritoryField.AddSat(q[1], cE);
                    q[-N] = TerritoryField.AddSat(q[-N], cE);
                    q[N] = TerritoryField.AddSat(q[N], cE);
                    q[-N - 1] = TerritoryField.AddSat(q[-N - 1], cD);
                    q[-N + 1] = TerritoryField.AddSat(q[-N + 1], cD);
                    q[N - 1] = TerritoryField.AddSat(q[N - 1], cD);
                    q[N + 1] = TerritoryField.AddSat(q[N + 1], cD);
                }
            }
        }
    }

    /// <summary>
    /// 粒子：灰白小点（3×3 核）+ 沿速度反方向 速度×ParticleTrailSeconds 像素的短拖尾
    /// （背景里约 1 像素；被大球吸的粒子跑得快，最多 ParticleTrailMax 像素）。
    /// 出生 0.3 秒内淡入，最后 1 秒淡出。
    /// </summary>
    private unsafe void DrawParticlesToField()
    {
        int n = _partCount;
        if (n == 0) return;
        int chunks = (n + PartChunk - 1) / PartChunk;
        Particle[] parts = _parts;
        uint[] lut = _partLut;
        uint* px = Field.Px;
        float trailSec = S.ParticleTrailSeconds;
        int trailMax = S.ParticleTrailMax;
        Parallel.For(0, chunks, c =>
        {
            int i0 = c * PartChunk, i1 = Math.Min(n, i0 + PartChunk);
            for (int i = i0; i < i1; i++)
            {
                ref Particle p = ref parts[i];
                float fade = p.Bright;
                if (p.Life < 1f) fade *= p.Life;
                if (p.Age < 0.3f) fade *= p.Age * (1f / 0.3f);
                int lvl = (int)(fade * (PartLevels * 0.5f));
                if (lvl <= 0) continue;
                if (lvl >= PartLevels) lvl = PartLevels - 1;
                int lb = p.Team * PartLevels;
                int o = (lb + lvl) * 3;
                int ix = (int)p.X, iy = (int)p.Y;

                if ((uint)(ix - 1) < N - 2 && (uint)(iy - 1) < N - 2)
                {
                    uint* q = px + iy * N + ix;
                    uint cC = lut[o], cE = lut[o + 1], cD = lut[o + 2];
                    q[0] = TerritoryField.AddSat(q[0], cC);
                    q[-1] = TerritoryField.AddSat(q[-1], cE);
                    q[1] = TerritoryField.AddSat(q[1], cE);
                    q[-N] = TerritoryField.AddSat(q[-N], cE);
                    q[N] = TerritoryField.AddSat(q[N], cE);
                    q[-N - 1] = TerritoryField.AddSat(q[-N - 1], cD);
                    q[-N + 1] = TerritoryField.AddSat(q[-N + 1], cD);
                    q[N - 1] = TerritoryField.AddSat(q[N - 1], cD);
                    q[N + 1] = TerritoryField.AddSat(q[N + 1], cD);
                }
                else if ((uint)ix < N && (uint)iy < N)
                {
                    uint* q = px + iy * N + ix;
                    *q = TerritoryField.AddSat(*q, lut[o]);
                }

                float vx = p.VX, vy = p.VY;
                float s2 = vx * vx + vy * vy;
                if (s2 <= 1f) continue;
                float s = MathF.Sqrt(s2);
                int len = Math.Min(trailMax, (int)(s * trailSec + 0.5f));
                float bx = -vx / s, by = -vy / s;
                float x = p.X + bx, y = p.Y + by;
                for (int k = 1; k <= len; k++, x += bx, y += by)
                {
                    int l = lvl * (len + 1 - k) / (len + 1);
                    if (l <= 0) break;
                    int tx = (int)x, ty = (int)y;
                    if ((uint)tx >= N || (uint)ty >= N) break;
                    uint* q = px + ty * N + tx;
                    *q = TerritoryField.AddSat(*q, lut[(lb + l) * 3]);
                }
            }
        });
    }

    /// <summary>
    /// 拖尾时长（秒）：机枪曳光最长；点防弹飞得快所以短一些；
    /// 风暴环在视频里是一圈密排的亮点，几乎没有拖尾（拖尾长了会变成梳子状的放射线）。
    /// </summary>
    private static float TrailSeconds(Kind k) => k switch
    {
        Kind.Mg => S.BulletTrailSeconds,
        Kind.Pd => S.BulletTrailSeconds * 0.25f,
        Kind.Storm => S.BulletTrailSeconds * 0.12f,
        _ => S.BulletTrailSeconds * 0.4f,
    };

    /// <summary>子弹：沿速度反方向画一条渐隐的细拖尾（Wu 式两点抗锯齿），再叠 5×5 高斯弹头。按块并行。</summary>
    private unsafe void DrawBulletsToField()
    {
        int n = _bulletCount;
        if (n == 0) return;
        const int chunk = 256;
        int chunks = (n + chunk - 1) / chunk;
        Bullet[] bullets = _bullets;
        uint[] lut = _bulletLut;
        float[] tcol = _trailCol;
        uint* px = Field.Px;
        float s0 = TrailSeconds(Kind.Mg), s1 = TrailSeconds(Kind.Pd);
        float s2 = TrailSeconds(Kind.Sniper), s3 = TrailSeconds(Kind.Storm);

        Parallel.For(0, chunks, c =>
        {
            int i0 = c * chunk, i1 = Math.Min(n, i0 + chunk);
            for (int i = i0; i < i1; i++)
            {
                ref Bullet b = ref bullets[i];
                if (b.Life <= 0f) continue;
                int kind = (int)b.Kind;
                int key = kind * 4 + b.Team;

                float sp = MathF.Sqrt(b.VX * b.VX + b.VY * b.VY);
                // 还没归队的苍蝇不画直拖尾：出膛前画的是弯的淡拖尾（DrawGhosts），出膛后涂出来的细线就是它的拖尾
                if (sp > 1f && !(b.Kind == Kind.Sniper && b.Stage < 3 && b.Group >= 0))
                {
                    float ts = kind == 0 ? s0 : kind == 1 ? s1 : kind == 2 ? s2 : s3;
                    float ux = -b.VX / sp, uy = -b.VY / sp;
                    bool xMajor = MathF.Abs(ux) >= MathF.Abs(uy);
                    float m = xMajor ? MathF.Abs(ux) : MathF.Abs(uy);
                    int steps = (int)(MathF.Min(ts, b.Age) * sp * m);    // 沿主轴走几个像素
                    if (steps >= 2)
                    {
                        // 每步沿拖尾走 1/m 长，亮度也乘 1/m（单位长度的亮度和方向无关）；颜色 ×256 定点
                        float gain = 256f / m;
                        int cr = (int)(tcol[key * 3] * gain), cg = (int)(tcol[key * 3 + 1] * gain), cb = (int)(tcol[key * 3 + 2] * gain);
                        float sx = ux / m, sy = uy / m;
                        float x = b.X - 0.5f + sx, y = b.Y - 0.5f + sy;
                        int inv = 65536 / (steps + 1);
                        for (int k = 1; k <= steps; k++, x += sx, y += sy)
                        {
                            int w = 256 - ((k * inv) >> 8);              // 线性渐隐 256 → 0
                            int xi, yi, f, step;
                            if (xMajor)
                            {
                                float fy = MathF.Floor(y);
                                xi = (int)(x + 0.5f);
                                yi = (int)fy;
                                f = (int)((y - fy) * 256f);
                                step = N;
                            }
                            else
                            {
                                float fx = MathF.Floor(x);
                                xi = (int)fx;
                                yi = (int)(y + 0.5f);
                                f = (int)((x - fx) * 256f);
                                step = 1;
                            }
                            if ((uint)xi >= N - 1 || (uint)yi >= N - 1) continue;
                            uint* q = px + yi * N + xi;
                            int w1 = (w * f) >> 8, w0 = w - w1;
                            q[0] = AddRgbI(q[0], cr * w0 >> 16, cg * w0 >> 16, cb * w0 >> 16);
                            q[step] = AddRgbI(q[step], cr * w1 >> 16, cg * w1 >> 16, cb * w1 >> 16);
                        }
                    }
                }

                int bx = (int)b.X, by = (int)b.Y;
                int lb = key * 25;
                for (int dy = -2; dy <= 2; dy++)
                {
                    int yy = by + dy;
                    if ((uint)yy >= N) continue;
                    uint* row = px + yy * N;
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int xx = bx + dx;
                        if ((uint)xx >= N) continue;
                        row[xx] = TerritoryField.AddSat(row[xx], lut[lb + (dy + 2) * 5 + (dx + 2)]);
                    }
                }
            }
        });
        DrawGhosts(px);
    }

    /// <summary>
    /// 出膛前苍蝇身后那条淡淡的灰白细弧线（视频里约比底色亮 40）：沿最近 SniperGhostSeconds 走过的路径画，
    /// 越旧越淡；出膛以后苍蝇开始涂色，淡拖尾在同样长的时间里淡掉。
    /// </summary>
    private unsafe void DrawGhosts(uint* px)
    {
        for (int s = 0; s < _lasers.Length; s++)
        {
            var l = _lasers[s];
            if (l == null || l.Gx == null) continue;
            float fade = l.Launched ? 1f - (l.Age - S.SniperSwarmTime) / S.SniperGhostSeconds : 1f;
            if (fade <= 0f) continue;
            Color c = RingColors[l.Team].Lerp(Colors.White, 0.8f);
            float cr = c.R * 55f * fade, cg = c.G * 55f * fade, cb = c.B * 55f * fade;
            int len = l.GLen, step = l.GStep;
            for (int f = 0; f < l.GFlies; f++)
            {
                int o = f * len;
                for (int j = 0; j + 1 < len; j++)
                {
                    int s0 = step - j, s1 = s0 - 1;
                    if (s1 < 0) break;
                    int i0 = o + s0 % len, i1 = o + s1 % len;
                    if (l.Gs[i0] != s0 || l.Gs[i1] != s1) break;
                    float w = 1f - (j + 0.5f) / len;
                    AddLine(px, l.Gx[i0], l.Gy[i0], l.Gx[i1], l.Gy[i1], cr * w, cg * w, cb * w);
                }
            }
        }
    }

    /// <summary>加色画一段 1 像素宽的细线：沿主轴逐像素走，副轴方向按小数部分分给相邻两个像素（单位长度亮度和方向无关）。</summary>
    private static unsafe void AddLine(uint* px, float x0, float y0, float x1, float y1, float r, float g, float b)
    {
        float dx = x1 - x0, dy = y1 - y0;
        bool xMajor = MathF.Abs(dx) >= MathF.Abs(dy);
        int steps = Math.Max(1, (int)MathF.Ceiling(xMajor ? MathF.Abs(dx) : MathF.Abs(dy)));
        float gain = MathF.Sqrt(dx * dx + dy * dy) / steps;
        int cr = (int)(r * gain), cg = (int)(g * gain), cb = (int)(b * gain);
        float sx = dx / steps, sy = dy / steps;
        float x = x0 - 0.5f + sx, y = y0 - 0.5f + sy;
        for (int k = 0; k < steps; k++, x += sx, y += sy)
        {
            int xi, yi, f, st;
            if (xMajor)
            {
                float fy = MathF.Floor(y);
                xi = (int)(x + 0.5f);
                yi = (int)fy;
                f = (int)((y - fy) * 256f);
                st = N;
            }
            else
            {
                float fx = MathF.Floor(x);
                xi = (int)fx;
                yi = (int)(y + 0.5f);
                f = (int)((x - fx) * 256f);
                st = 1;
            }
            if ((uint)xi >= N - 1 || (uint)yi >= N - 1) continue;
            uint* q = px + yi * N + xi;
            int w0 = 256 - f;
            q[0] = AddRgbI(q[0], cr * w0 >> 8, cg * w0 >> 8, cb * w0 >> 8);
            q[st] = AddRgbI(q[st], cr * f >> 8, cg * f >> 8, cb * f >> 8);
        }
    }

    public override void _Draw()
    {
        DrawTexture(_composeVp.GetTexture(), Vector2.Zero);
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  渲染：发光层（加色混合）
    // ════════════════════════════════════════════════════════════════════════════

    private void DrawGlow()
    {
        ProfStart();
        DrawGlowInner();
        ProfMark(12);
    }

    /// <summary>
    /// 提示脉冲（视频实测：开局 +1M 在 0~1.6 秒，+500K 在 1.8~2.5 秒，+25M 在 4.9~5.9 秒；中途每条提示都有）：
    /// 中心一个深红色光晕盘，盘边一圈亮粉细线三次缓出长到 58.5（放出来 0.21 秒在半径 29、0.41 秒 45、0.71 秒 57），
    /// 1.0 秒以后慢慢淡掉，1.8 秒没了。中途中心已经是红领土，光晕盘看不出来，只剩那圈亮线。
    /// </summary>
    private static float PulseEdge(float age)
    {
        float u = Math.Clamp((age + 0.01f) / 1.04f, 0f, 1f), w = 1f - u;
        return 58.5f * (1f - w * w * w);
    }

    /// <summary>
    /// 提示脉冲的样子：深红色光晕盘（队色去掉灰的成分：红方 (1, 0, 0.2)，不带绿），径向渐变 I(r) = 0.15 + 0.3·e^(−(r−20)/8)，
    /// 盘边外还有一圈 I ≈ 0.11 的余光（平到盘边外 10 像素，13 像素处没了）；叠加混合、一圈圈往里叠出来；盘边一圈亮粉细线。
    /// </summary>
    private static void DrawPulse(Node2D g, Vector2 c, float edge, float sa, Color rc)
    {
        float m = MathF.Min(rc.R, MathF.Min(rc.G, rc.B)), k = 1f / (1f - m);
        var dc = new Color((rc.R - m) * k, (rc.G - m) * k, (rc.B - m) * k);
        float prev = 0f;
        for (float r = edge + 13f; r > 5f; r -= 2f)
        {
            float I = r > edge ? 0.11f * Math.Clamp((edge + 13f - r) / 3f, 0f, 1f)
                               : MathF.Min(0.5f, 0.15f + 0.3f * MathF.Exp(-(r - 20f) / 8f));
            if (I > prev) g.DrawCircle(c, r, new Color(dc, (I - prev) * sa));
            prev = MathF.Max(prev, I);
        }
        g.DrawArc(c, edge, 0f, Mathf.Tau, 128, new Color(rc.Lerp(Colors.White, 0.5f), 0.32f * sa), 1.6f, true);
    }

    /// <summary>
    /// 复活特效：16 道光芒 0.1 秒射出去（长短交替、整圈慢慢转，根部金白、往尖上带队色淡出；根部离中心 26 px，不把中心的数字盖白）；
    /// 中心一团淡淡的金白光 0.6 秒；
    /// 队色环（20 → 330 px，1.1 秒）和金白环（晚 0.15 秒，15 → 220 px）先快后慢往外扩；48 颗火星往外溅、越飞越慢、拖一小段尾巴。
    /// 0.4 秒以后整体慢慢淡掉，ReviveLife 秒没了
    /// </summary>
    private void DrawRevive(Node2D g, Revive rv)
    {
        float t = rv.Age;
        Vector2 c = rv.Pos;
        Color tc = RingColors[rv.Team];
        float x = Math.Clamp((t - 0.4f) / (ReviveLife - 0.4f), 0f, 1f), fade = 1f - x * x * (3f - 2f * x);

        // 复活弹珠落地那一下（0.2 秒）：一大团队色软光、中间一小团白光
        if (t < 0.2f)
        {
            float u = t / 0.2f, a = (1f - u) * (1f - u);
            Dot(g, c, 60f + 60f * u, new Color(tc, 0.5f * a));
            Dot(g, c, 24f + 20f * u, new Color(1f, 0.97f, 0.9f, 0.55f * a));
        }

        if (t < 0.6f)
        {
            float u = t / 0.6f, a = 1f - u, fr = 22f + 48f * MathF.Sqrt(u);
            g.DrawCircle(c, fr * 1.8f, new Color(tc, 0.10f * a));
            g.DrawCircle(c, fr, new Color(ReviveGold, 0.22f * a));
            g.DrawCircle(c, fr * 0.55f, new Color(1f, 0.97f, 0.88f, 0.25f * a));
            g.DrawCircle(c, fr * 0.25f, new Color(1f, 1f, 1f, 0.35f * a));
        }

        float grow = 1f - MathF.Exp(-t / 0.08f), ra = MathF.Min(1f, t / 0.05f) * fade;
        for (int i = 0; i < ReviveRays; i++)
        {
            Vector2 d = Vector2.FromAngle(rv.Spin + 0.4f * t + i * (Mathf.Tau / ReviveRays));
            float len = 250f * rv.RayLen[i] * grow;
            FieldRay(g, c, d, 26f, len, 10f, new Color(ReviveGold, 0.14f * ra), new Color(tc, 0f));
            FieldRay(g, c, d, 26f, len * 0.9f, 3.5f, new Color(1f, 0.96f, 0.85f, 0.42f * ra), new Color(tc, 0f));
        }

        ReviveRing(g, c, t, 0f, 1.1f, 20f, 330f, 10f, tc.Lerp(Colors.White, 0.3f));
        ReviveRing(g, c, t, 0.15f, 0.95f, 15f, 220f, 6f, ReviveGold);

        // 火星：走过的距离 v·τ·(1 − e^(−t/τ))，τ = 0.3 秒；尾巴是 0.05 秒前到现在这一段
        const float tau = 0.3f;
        for (int i = 0; i < ReviveSparks; i++)
        {
            float life = rv.SparkLife[i];
            if (t >= life) continue;
            float sa = 1f - t / life, v = rv.SparkSpeed[i] * tau;
            Vector2 d = Vector2.FromAngle(rv.SparkAng[i]);
            Vector2 p1 = c + d * (10f + v * (1f - MathF.Exp(-t / tau)));
            if (!InField(p1)) continue;
            Vector2 p0 = c + d * (10f + v * (1f - MathF.Exp(-MathF.Max(0f, t - 0.05f) / tau)));
            g.DrawLine(p0, p1, new Color(ReviveGold, 0.6f * sa), 2f, true);
            g.DrawCircle(p1, 1.8f, new Color(1f, 1f, 0.95f, 0.9f * sa));
        }
    }

    private void DrawWarps()
    {
        foreach (var w in _warps) DrawWarp(_warpLayer, w);
    }

    /// <summary>
    /// 复活传送：① WarpGather 秒里弹珠化成一团光，从弹珠大小收成光球大小、越收越白，外面一圈队色环、十道金线跟着往里收；
    /// ② 起飞：原地一团白光、一道小环往外扩；③ 光球沿弧线先加速后减速飞到阵营中心，拖一条尾巴（最近 WarpTail 那段飞行）
    /// （头上金白，越往后越细越淡、变成队色），一路撒金色光点；落地以后尾巴收进中心（落地的爆光在 DrawRevive）
    /// </summary>
    private void DrawWarp(Node2D g, Warp w)
    {
        float t = w.Age, R = w.R, hw = Math.Clamp(0.55f * R, 5f, 11f);
        Color tc = RingColors[w.Team], wc = new(1f, 0.97f, 0.88f), rc = tc.Lerp(Colors.White, 0.35f);
        Vector2 p0 = w.From;

        if (t < WarpGather)
        {
            float k = t / WarpGather, e = k * k, rb = R + (hw - R) * e;
            Dot(g, p0, rb * 3f, new Color(tc, 0.35f + 0.25f * k));
            g.DrawCircle(p0, rb, new Color(tc.Lerp(wc, 0.3f + 0.5f * k), 0.6f));
            Dot(g, p0, rb * 1.3f, new Color(wc, 0.5f + 0.5f * k));
            float rr = rb + (2.4f * R + 16f) * (1f - e);
            g.DrawArc(p0, rr, 0f, Mathf.Tau, 48, new Color(rc, 0.8f * k), 2f, true);
            for (int i = 0; i < 10; i++)
            {
                Vector2 d = Vector2.FromAngle(w.Spin + i * (Mathf.Tau / 10f));
                g.DrawLine(p0 + d * (rr + 5f), p0 + d * (rr + 12f + 16f * (1f - k)),
                    new Color(ReviveGold, 0.7f * MathF.Min(1f, 3f * k)), 1.6f, true);
            }
        }
        else if (t < WarpGather + 0.3f)
        {
            float k = (t - WarpGather) / 0.3f, a = 1f - k;
            Dot(g, p0, (R + 10f) * (1.6f + 0.8f * k), new Color(tc, 0.5f * a * a));
            Dot(g, p0, (R * 0.5f + 6f) * (1.2f + 0.4f * k), new Color(wc, 0.8f * a * a));
            g.DrawArc(p0, R + 4f + 36f * (1f - a * a), 0f, Mathf.Tau, 48, new Color(rc, 0.7f * a), 2f, true);
        }
        if (t <= WarpGather) return;

        float ur = (t - WarpGather) / w.Fly, uh = MathF.Min(1f, ur), ut = Math.Clamp(ur - WarpTail, 0f, 1f);
        if (uh - ut > 1e-4f)
        {
            Vector2 pa = WarpAt(w, ut), na = WarpNormal(w, ut);
            for (int i = 1; i <= WarpSeg; i++)
            {
                float qa = (float)(i - 1) / WarpSeg, q = (float)i / WarpSeg;
                float u = ut + (uh - ut) * q;
                Vector2 pb = WarpAt(w, u), nb = WarpNormal(w, u);
                float wa = hw * MathF.Pow(qa, 0.7f), wb = hw * MathF.Pow(q, 0.7f);
                SoftBand(g, pa, na, 2.6f * wa, new Color(tc, 0.35f * qa), pb, nb, 2.6f * wb, new Color(tc, 0.35f * q));
                SoftBand(g, pa, na, wa, new Color(tc.Lerp(wc, qa), 0.9f * qa * qa), pb, nb, wb, new Color(tc.Lerp(wc, q), 0.9f * q * q));
                pa = pb;
                na = nb;
            }
        }
        if (uh < 1f)
        {
            Vector2 p = WarpAt(w, uh);
            float fa = 1f - 0.6f * Math.Clamp((uh - 0.85f) / 0.15f, 0f, 1f);     // 快到中心淡一点（中心的字在下面一层）
            Dot(g, p, hw * 4f, new Color(tc, 0.45f * fa));
            Dot(g, p, hw * 2f, new Color(ReviveGold, 0.6f * fa));
            g.DrawCircle(p, hw * 0.6f, new Color(wc, 0.9f * fa));
        }
        for (int i = 0; i < WarpDust; i++)
        {
            float a = t - WarpGather - w.DustU[i] * w.Fly;
            if (a < 0f || a >= w.DustLife[i]) continue;
            float k = 1f - a / w.DustLife[i];
            g.DrawCircle(WarpAt(w, w.DustU[i]) + w.DustVel[i] * a, 1f + 0.8f * k, new Color(ReviveGold, 0.85f * k));
        }
    }

    /// <summary>光球在飞行进度 u（0..1，按时间）处的位置：先加速后减速（u²(3−2u)）走二次贝塞尔弧</summary>
    private Vector2 WarpAt(Warp w, float u)
    {
        float s = u * u * (3f - 2f * u), a = 1f - s;
        return a * a * w.From + 2f * a * s * w.Ctrl + s * s * Factions[w.Team].Center;
    }

    private Vector2 WarpNormal(Warp w, float u)
    {
        float s = u * u * (3f - 2f * u);
        Vector2 d = (1f - s) * (w.Ctrl - w.From) + s * (Factions[w.Team].Center - w.Ctrl);
        return d.LengthSquared() > 1e-6f ? new Vector2(-d.Y, d.X).Normalized() : Vector2.Up;
    }

    /// <summary>一段软边光带 pa → pb：中线颜色 ca → cb，往两边（法线 na / nb、半宽 wa / wb）淡到 0</summary>
    private void SoftBand(Node2D g, Vector2 pa, Vector2 na, float wa, Color ca, Vector2 pb, Vector2 nb, float wb, Color cb)
    {
        Color ea = new(ca, 0f), eb = new(cb, 0f);
        for (int side = -1; side <= 1; side += 2)
        {
            _quad[0] = pa; _quad[1] = pb; _quad[2] = pb + nb * (wb * side); _quad[3] = pa + na * (wa * side);
            _quadC[0] = ca; _quadC[1] = cb; _quadC[2] = eb; _quadC[3] = ea;
            g.DrawPrimitive(_quad, _quadC, null);
        }
    }

    /// <summary>一团软光（_softDot 贴图，半径 r）</summary>
    private void Dot(Node2D g, Vector2 p, float r, Color c) =>
        g.DrawTextureRect(_softDot, new Rect2(p.X - r, p.Y - r, 2f * r, 2f * r), false, c);

    private void ReviveRing(Node2D g, Vector2 c, float t, float delay, float life, float r0, float r1, float w, Color col)
    {
        float u = (t - delay) / life;
        if (u <= 0f || u >= 1f) return;
        float a = 1f - u, r = r0 + (r1 - r0) * (1f - a * a);
        FieldArc(g, c, r, new Color(col, 0.25f * a), w * 2.5f);
        FieldArc(g, c, r, new Color(col, 0.85f * a), w * 0.5f);
    }

    /// <summary>一道光芒：c + d·r0 处宽 2w、到 c + d·len 收成尖，颜色 cb → ct；出了场地的那截剪掉。</summary>
    private void FieldRay(Node2D g, Vector2 c, Vector2 d, float r0, float len, float w, Color cb, Color ct)
    {
        if (len <= r0 + 1f) return;
        float tb = float.MaxValue;
        if (d.X < -1e-6f) tb = MathF.Min(tb, -c.X / d.X); else if (d.X > 1e-6f) tb = MathF.Min(tb, (N - c.X) / d.X);
        if (d.Y < -1e-6f) tb = MathF.Min(tb, -c.Y / d.Y); else if (d.Y > 1e-6f) tb = MathF.Min(tb, (N - c.Y) / d.Y);
        if (tb <= r0) return;
        Vector2 n = new(-d.Y, d.X), b = c + d * r0;
        if (tb >= len)
        {
            _tri[0] = b + n * w; _tri[1] = c + d * len; _tri[2] = b - n * w;
            _triC[0] = cb; _triC[1] = ct; _triC[2] = cb;
            g.DrawPrimitive(_tri, _triC, null);
            return;
        }
        float s = (tb - r0) / (len - r0), wc = w * (1f - s);
        Color cc = cb.Lerp(ct, s);
        Vector2 e = c + d * tb;
        _quad[0] = b + n * w; _quad[1] = e + n * wc; _quad[2] = e - n * wc; _quad[3] = b - n * w;
        _quadC[0] = cb; _quadC[1] = cc; _quadC[2] = cc; _quadC[3] = cb;
        g.DrawPrimitive(_quad, _quadC, null);
    }

    /// <summary>整圈分 128 段画环，只画场地里面的部分（进出场地的地方截在场地边上）。</summary>
    private void FieldArc(Node2D g, Vector2 c, float r, Color col, float width)
    {
        const int seg = 128;
        int k0 = -1;
        for (int k = 0; k < seg && k0 < 0; k++)
            if (!InField(c + r * Vector2.FromAngle(k * Mathf.Tau / seg))) k0 = k;
        if (k0 < 0) { g.DrawArc(c, r, 0f, Mathf.Tau, seg, col, width, true); return; }
        // 从出界的一点开始绕一圈：场地里面连续的点连成一段画，最后回到起点（出界）时最后一段也画掉了
        _arcBuf.Clear();
        Vector2 prev = c + r * Vector2.FromAngle(k0 * Mathf.Tau / seg);
        for (int k = 1; k <= seg; k++)
        {
            Vector2 p = c + r * Vector2.FromAngle((k0 + k) * Mathf.Tau / seg);
            bool pin = InField(p), qin = InField(prev);
            if (pin && !qin) _arcBuf.Add(EdgeCross(p, prev));
            if (pin) _arcBuf.Add(p);
            else if (qin)
            {
                _arcBuf.Add(EdgeCross(prev, p));
                if (_arcBuf.Count >= 2) g.DrawPolyline(_arcBuf.ToArray(), col, width, true);
                _arcBuf.Clear();
            }
            prev = p;
        }
    }

    private static bool InField(Vector2 p) => p.X >= 0f && p.Y >= 0f && p.X <= N && p.Y <= N;

    /// <summary>从场内的点 a 往场外的点 b 走，碰到场地边的那一点。</summary>
    private static Vector2 EdgeCross(Vector2 a, Vector2 b)
    {
        float s = 1f;
        if (b.X < 0f) s = MathF.Min(s, a.X / (a.X - b.X));
        if (b.X > N) s = MathF.Min(s, (N - a.X) / (b.X - a.X));
        if (b.Y < 0f) s = MathF.Min(s, a.Y / (a.Y - b.Y));
        if (b.Y > N) s = MathF.Min(s, (N - a.Y) / (b.Y - a.Y));
        return a + (b - a) * s;
    }

    private void DrawGlowInner()
    {
        var g = _glow;
        for (int t = 0; t < 4; t++)
        {
            var f = Factions[t];
            if (!f.Alive) continue;
            Color rc = RingColors[t];
            Vector2 c = f.Center;
            // 护盾环本身在 DrawShieldsToField 里按实测径向亮度画进领土画布；
            // 风暴的闪光圆盘来自领土过曝（TerritoryField.FlashDisc）

            // 提示脉冲
            foreach (float age in f.Pulses)
                DrawPulse(g, c, PulseEdge(age), Math.Clamp((Faction.PulseLife - age) / 0.8f, 0f, 1f), rc);
            // 护盾为 0：一圈很淡的偏白细线（开局跟在开局那次脉冲的盘边外 12 像素长出来，最后停在 70 像素上）
            if (f.ShieldShown <= 1.0)
                g.DrawArc(c, MathF.Min(Faction.ZeroShieldLineRadius, PulseEdge(_time + S.OpeningSniperLead) + 12f), 0f, Mathf.Tau, 128,
                    new Color(rc.Lerp(Colors.White, 0.55f), 0.09f), 1.3f, true);

            // 炮管光晕
            Vector2 dir = Vector2.FromAngle(f.TurretAngle);
            g.DrawLine(c + dir * 6f, c + dir * 28f, new Color(rc, 0.35f), 9f, true);
        }

        for (int i = 0; i < SparkMax; i++)
        {
            float age = _time - _sparkT[i];
            if (age < 0f || age >= SparkLife || _sparkT[i] <= 0f) continue;
            float sa = 1f - age / SparkLife;
            Vector2 p = _sparkPos[i];
            for (int k = 0; k < SparkRadius.Length; k++)
                g.DrawCircle(p, SparkRadius[k], new Color(1f, 0.95f, 0.97f, SparkAlpha[k] * sa));
        }

        foreach (var s in _shocks)
        {
            if (s.Delay > 0f) continue;
            float u = 1f - s.Life / s.MaxLife;
            float a = 1f - u;
            if (s.Bloom)
            {
                // I(r) = A·e^(−r/16)，颜色 (0.2, 0.78, 1) 叠加：一圈圈往里叠，中心叠到饱和就是白的（视频 5.10 秒的径向亮度）
                float A = BloomAmp(s.MaxLife - s.Life);
                if (A < 0.02f) continue;
                var bc = new Color(0.2f, 0.78f, 1f);
                float prev = 0f;
                for (float rb = 110f; rb > 1f; rb -= 2f)
                {
                    float I = MathF.Min(5.5f, A * MathF.Exp(-rb / 16f));
                    if (I - prev < 0.004f) continue;
                    g.DrawCircle(s.Pos, rb, new Color(bc, I - prev));
                    prev = I;
                }
                continue;
            }
            if (s.Flash)
            {
                // 一团光：白芯 + 外面一圈淡淡的光晕，边胀大边淡掉
                float fr = s.R0 + (s.R1 - s.R0) * MathF.Sqrt(u);
                g.DrawCircle(s.Pos, fr * 1.8f, new Color(s.Col, 0.10f * a));
                g.DrawCircle(s.Pos, fr, new Color(s.Col, 0.30f * a));
                g.DrawCircle(s.Pos, fr * 0.55f, new Color(1f, 1f, 1f, 0.55f * a));
                g.DrawCircle(s.Pos, fr * 0.25f, new Color(1f, 1f, 1f, 0.9f * a));
                continue;
            }
            float r = s.Linear ? s.R0 + (s.R1 - s.R0) * u : s.R0 + (s.R1 - s.R0) * (1f - (1f - u) * (1f - u));
            g.DrawArc(s.Pos, r, 0f, Mathf.Tau, 128, new Color(s.Col, 0.25f * a), s.Width * 2.5f, true);
            g.DrawArc(s.Pos, r, 0f, Mathf.Tau, 128, new Color(s.Col, 0.8f * a), s.Width * 0.5f, true);
        }
        foreach (var rv in _revives) DrawRevive(g, rv);

        // 狙击激光头一小团白光（视频 3.8~4.8 秒：一把细线收成的扫帚头上一个十来像素的白亮点，没有大光晕）。
        // 苍蝇还在收拢的时候没有，归队的越多越亮；数值越大光团越大。路径线在 DrawLaserLines（大球下面）
        for (int s = 0; s < _lasers.Length; s++)
        {
            var l = _lasers[s];
            if (l == null || !l.Launched || l.Ended) continue;
            float join = l.Count == 0 ? 0f : (float)l.Joined / l.Count;
            join *= join;
            float big = Math.Clamp(((float)Math.Log10(Math.Max(l.Value, 1.0)) - 5f) / 3f, 0.3f, 1f);   // 1M → 0.33、100M → 1
            Vector2 h = l.Tip - l.Dir * 4f;
            Color hc = TeamTint(l.Team, 0.7f);
            g.DrawCircle(h, 30f * big, new Color(hc, 0.08f * join));
            g.DrawCircle(h, 18f * big, new Color(hc, 0.16f * join));
            g.DrawCircle(h, MathF.Max(10f * big, 5f), new Color(1f, 1f, 1f, 0.35f * join));
            g.DrawCircle(h, MathF.Max(5f * big, 2.5f), new Color(1f, 1f, 1f, 0.8f * join));
        }
    }

    /// <summary>
    /// 狙击激光的路径线：起点 → 各反弹点 → 激光头的一条霓虹色细线（视频实测亮带中间：红方 (253,28,170)、绿方 (0,248,244)、
    /// 蓝方 (15,251,255)、黄方 (255,247,14)）。苍蝇还在收拢的时候没有，归队的越多越亮；每一段的亮度按激光头经过时的样子
    /// 固定在地上，之后慢慢变淡。画进 _laserVp，合成着色器把它加在大球下面。
    /// </summary>
    private void DrawLaserLines()
    {
        var g = _laserLayer;
        for (int s = 0; s < _lasers.Length; s++)
        {
            var l = _lasers[s];
            if (l == null || !l.Launched) continue;
            float a = l.Ended ? l.Fade : 1f;
            if (a <= 0f) continue;
            int n = l.Path.Count;
            var pts = new Vector2[n + 1];
            var core = new Color[n + 1];
            var halo = new Color[n + 1];
            Color lc = LaserColors[l.Team];
            float w = 0f, top = 0f;
            for (int k = 0; k <= n; k++)
            {
                // 激光头那一端接上现在的亮度（结束了就沿用最后一段的）
                if (k < n) w = l.PathA[k] * MathF.Exp((l.PathT[k] - l.FlyTime) / S.SniperLineDecay) * a;
                else if (!l.Ended) w = LineGlow(l);
                pts[k] = k < n ? l.Path[k] : l.Tip;
                core[k] = new Color(lc, 0.8f * w);
                halo[k] = new Color(lc, 0.25f * w);
                top = MathF.Max(top, w);
            }
            if (top > 0.01f && (pts.Length > 2 || pts[0].DistanceSquaredTo(pts[1]) > 1f))
            {
                g.DrawPolylineColors(pts, halo, 6f, true);
                g.DrawPolylineColors(pts, core, 2.5f, true);
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  渲染：顶层（大球本体、中心组件、通知、结束画面）
    // ════════════════════════════════════════════════════════════════════════════

    private void DrawTop()
    {
        ProfStart();
        DrawTopInner();
        ProfMark(13);
    }

    private void DrawTopInner()
    {
        var g = _top;

        // 大球本体已画进领土画布，这里只写数值
        for (int i = 0; i < Balls.Count; i++)
        {
            var b = Balls[i];
            float r = b.Radius;
            if (r < 8f) continue;
            string txt = GrowthBall.FormatValue(b.Value);
            int fs = Math.Clamp((int)(r * 0.54f), 8, 72);
            Fonts.DrawCentered(g, Fonts.UiBold, b.Pos, txt, fs, Colors.White, Math.Max(2, fs / 10), new Color(0f, 0f, 0f, 0.5f));
        }

        for (int t = 0; t < 4; t++) DrawCenterWidget(g, Factions[t]);

        // 场地外框
        g.DrawRect(new Rect2(0.5f, 0.5f, N - 1f, N - 1f), new Color(16f / 255, 22f / 255, 34f / 255), false, 1f);

        if (Winner >= 0) DrawGameOver(g);
    }

    private static Color TeamTint(int t, float white) => RingColors[t].Lerp(Colors.White, white);

    private void DrawCenterWidget(Node2D g, Faction f)
    {
        int t = f.Team;
        Vector2 c = f.Center;
        Color rc = RingColors[t];

        if (!f.Alive)
        {
            DrawNote(g, f);
            return;
        }

        // 炮管
        Vector2 dir = Vector2.FromAngle(f.TurretAngle);
        g.DrawLine(c + dir * 6f, c + dir * 26f, rc.Lerp(Colors.White, 0.15f), 3.5f, true);

        // 中心靶心（视频放大实测）：外面一圈淡粉白光晕（到半径 16 左右）、白环（半径 7.6~9.6）、环内侧一圈暗一点的红、
        // 饱和的队色芯（红方 (218, 6, 38)）、正中白点（半径 2.5）外一圈暗一点的红；圆心放在像素中心，白点才是圆的
        Vector2 ic = c + new Vector2(0.5f, 0.5f);
        g.DrawCircle(ic, 16f, new Color(1f, 0.82f, 0.9f, 0.05f));
        g.DrawCircle(ic, 13f, new Color(1f, 0.82f, 0.9f, 0.07f));
        g.DrawCircle(ic, 11f, new Color(1f, 0.85f, 0.92f, 0.12f));
        g.DrawCircle(ic, 9.6f, new Color(1f, 0.96f, 0.98f));
        Color core = Color.FromHsv(rc.H, MathF.Min(1f, rc.S * 1.1f), 0.86f);
        g.DrawCircle(ic, 7.6f, core.Darkened(0.16f));
        g.DrawCircle(ic, 6.1f, core);
        g.DrawCircle(ic, 3.4f, core.Darkened(0.18f));
        g.DrawCircle(ic, 2.5f, new Color(1f, 0.92f, 0.98f));

        // 护盾图标 + 数值：两样作为一组以 x = +5.5 居中，图标 16 宽、间隔 23，数值右对齐在一个至少 54 宽的格子里
        // （视频实测「0.00」时图标 −41..−25、数值 +11..+52；「25.00M」时图标 −50..−34、数值 −11..+61；数字高 14）
        // 护盾一增加，这一行闪框 0.6 秒（框比这一组左宽 20、右宽 10，y = −31..−5），数字同时染粉
        string sv = FormatShield(f.ShieldShown);
        float sw = MathF.Max(Fonts.UiBold.GetStringSize(sv, HorizontalAlignment.Left, -1f, 20).X, 54f);
        float half = (16f + 23f + sw) * 0.5f;
        float fa = Math.Clamp(f.ShieldFlash / 0.35f, 0f, 1f);
        if (fa > 0f)
        {
            var box = _shieldBox[t];
            box.BgColor = new Color(TeamTint(t, 0.25f), 0.30f * fa);
            box.BorderColor = new Color(TeamTint(t, 0.75f), 0.70f * fa);
            g.DrawStyleBox(box, new Rect2(c.X + 5.5f - half - 20f, c.Y - 31.5f, 2f * half + 30f, 27f));
        }
        DrawShieldIcon(g, c + new Vector2(5.5f - half + 8f, -16.5f), TeamTint(t, 0.55f));
        Fonts.DrawRight(g, Fonts.UiBold, c + new Vector2(7f + half, -18.5f), sv, 20, Colors.White.Lerp(TeamTint(t, 0.6f), fa));

        // 机枪框（机枪火力是 0 的时候没有，一有火力淡入；视频框线 y = +12 / +39、x = −72 / +72.5）+ 图标 + 数值（右对齐在 +65.5，数字高 13）
        if (f.MgBoxAlpha > 0f)
        {
            var box = _mgBox[t];
            box.BgColor = new Color(TeamTint(t, 0.25f), 0.30f * f.MgBoxAlpha);
            box.BorderColor = new Color(TeamTint(t, 0.75f), 0.70f * f.MgBoxAlpha);
            g.DrawStyleBox(box, new Rect2(c.X - 72.5f, c.Y + 11.5f, 145f, 28f));
        }
        DrawGunIcon(g, c + new Vector2(0f, 2.5f), TeamTint(t, 0.75f), rc);
        Fonts.DrawRight(g, Fonts.Num, c + new Vector2(66f, 27f), FormatMg(f.MgShown), 21, TeamTint(t, 0.82f));

        DrawNote(g, f);
    }

    private static void DrawNote(Node2D g, Faction f)
    {
        if (f.NoteLife <= 0f || string.IsNullOrEmpty(f.NoteTitle)) return;
        float a = Math.Clamp(f.NoteLife / 0.4f, 0f, 1f);
        Vector2 c = f.Center;
        Fonts.DrawCentered(g, Fonts.UiBold, c + new Vector2(0f, -60f), Loc.Term(f.NoteTitle), 12, new Color(TeamTint(f.Team, 0.8f), a));
        if (f.NoteAmount > 0.0)
            Fonts.DrawCentered(g, Fonts.UiBold, c + new Vector2(0f, -41f), "+" + GrowthBall.FormatValue(f.NoteAmount), 15,
                new Color(1f, 1f, 1f, a));
    }

    // 护盾图标（视频放大实测）：外框 1 像素粉线，隔 1 像素暗缝，再一条 1 像素内框，里面右上、左下两格填色；共 16×19 像素
    private static readonly Vector2[] ShieldOuter =
    {
        new(-7.5f, -7f), new(0f, -9f), new(7.5f, -7f), new(7.5f, 0.5f),
        new(5.5f, 4.5f), new(0f, 9f), new(-5.5f, 4.5f), new(-7.5f, 0.5f), new(-7.5f, -7f),
    };

    private static readonly Vector2[] ShieldInner =
    {
        new(-5.5f, -5.3f), new(0f, -6.8f), new(5.5f, -5.3f), new(5.5f, 0.2f),
        new(4f, 3.3f), new(0f, 6.4f), new(-4f, 3.3f), new(-5.5f, 0.2f), new(-5.5f, -5.3f),
    };

    private static void DrawShieldIcon(Node2D g, Vector2 p, Color col)
    {
        p = new Vector2(MathF.Round(p.X), MathF.Round(p.Y));          // 竖线落在像素中心上，1 像素的线才清楚
        var outer = new Vector2[ShieldOuter.Length];
        var inner = new Vector2[ShieldInner.Length];
        for (int i = 0; i < outer.Length; i++) outer[i] = p + ShieldOuter[i];
        for (int i = 0; i < inner.Length; i++) inner[i] = p + ShieldInner[i];
        g.DrawPolyline(outer, col, 1f);                               // 不开抗锯齿：羽化会把两条线中间那 1 像素暗缝糊掉
        g.DrawPolyline(inner, new Color(col, 0.9f), 1f);
        // 右上、左下两格填实（视频图标的样子）
        g.DrawColoredPolygon(new[] { p + new Vector2(0.5f, -6.2f), p + new Vector2(4.5f, -5.2f), p + new Vector2(4.5f, -0.5f), p + new Vector2(0.5f, -0.5f) },
            new Color(col, 0.9f));
        g.DrawColoredPolygon(new[] { p + new Vector2(-4.5f, 0.5f), p + new Vector2(-0.5f, 0.5f), p + new Vector2(-0.5f, 5.6f), p + new Vector2(-3.6f, 2.9f) },
            new Color(col, 0.9f));
    }

    private static void DrawGunIcon(Node2D g, Vector2 c, Color col, Color muzzle)
    {
        // 机枪剪影，枪口朝右：x −60..−35，y +17..+27
        g.DrawColoredPolygon(new[] { c + new Vector2(-60f, 19.5f), c + new Vector2(-54f, 20f), c + new Vector2(-54f, 24f), c + new Vector2(-60f, 25.5f) }, col);
        g.DrawRect(new Rect2(c.X - 54f, c.Y + 19f, 11f, 4.5f), col);
        g.DrawRect(new Rect2(c.X - 52f, c.Y + 17.2f, 6f, 1.8f), col);
        g.DrawRect(new Rect2(c.X - 43f, c.Y + 20.2f, 7.5f, 1.8f), col);
        g.DrawRect(new Rect2(c.X - 49.5f, c.Y + 23.5f, 2.6f, 3.5f), col);
        g.DrawRect(new Rect2(c.X - 53.2f, c.Y + 23.5f, 1.8f, 3f), col);
        g.DrawLine(c + new Vector2(-41f, 22f), c + new Vector2(-43f, 27f), col, 1f, true);
        g.DrawLine(c + new Vector2(-41f, 22f), c + new Vector2(-39f, 27f), col, 1f, true);
        g.DrawCircle(c + new Vector2(-34.8f, 21.1f), 1.3f, muzzle);
    }

    private void DrawGameOver(Node2D g)
    {
        g.DrawRect(new Rect2(0f, 0f, N, N), new Color(0f, 0f, 0f, 0.35f));
        Color tc = RingColors[Winner];
        Fonts.DrawCentered(g, Fonts.UiBold, new Vector2(N * 0.5f, N * 0.5f - 30f),
            Loc.L($"{Main.TeamNames[Winner]}方 占领全场！", $"{Loc.Team(Winner)} Wins!"), 64, tc, 8, new Color(0f, 0f, 0f, 0.8f));
        Fonts.DrawCentered(g, Fonts.Ui, new Vector2(N * 0.5f, N * 0.5f + 40f),
            Loc.L($"{Math.Max(0f, _overTimer):0} 秒后重新开始（按 R 立即重开）", $"Restarting in {Math.Max(0f, _overTimer):0} s (press R to restart now)"), 22, new Color(0.85f, 0.9f, 1f), 4, new Color(0f, 0f, 0f, 0.8f));
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  格式化 / 随机数
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>护盾：两位小数截断，例如 24.99M。</summary>
    public static string FormatShield(double v)
    {
        if (v < 0) v = 0;
        var inv = CultureInfo.InvariantCulture;
        if (v >= 1e12) return (Math.Floor(v / 1e10) / 100).ToString("0.00", inv) + "T";
        if (v >= 1e9) return (Math.Floor(v / 1e7) / 100).ToString("0.00", inv) + "B";
        if (v >= 1e6) return (Math.Floor(v / 1e4) / 100).ToString("0.00", inv) + "M";
        if (v >= 1e3) return (Math.Floor(v / 10) / 100).ToString("0.00", inv) + "K";
        return (Math.Floor(v * 100) / 100).ToString("0.00", inv);
    }

    /// <summary>机枪火力：一百万以下带千分位（743,545），以上四位小数（3.6470M）。</summary>
    public static string FormatMg(double v)
    {
        if (v < 0) v = 0;
        var inv = CultureInfo.InvariantCulture;
        if (v < 1e6) return ((long)v).ToString("N0", inv);
        if (v < 1e9) return (Math.Floor(v / 100) / 10000).ToString("0.0000", inv) + "M";
        if (v < 1e12) return (Math.Floor(v / 1e5) / 10000).ToString("0.0000", inv) + "B";
        return (Math.Floor(v / 1e8) / 10000).ToString("0.0000", inv) + "T";
    }

    private uint NextU()
    {
        uint x = _rng;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _rng = x;
        return x;
    }

    private float Rand01() => (NextU() >> 8) * (1f / 16777216f);

    private uint _fxRng = 0x9E3779B9u;

    /// <summary>纯视觉效果用的随机数（和模拟的 _rng 分开，画不画都不影响模拟结果）。</summary>
    private float FxRand01()
    {
        uint x = _fxRng;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _fxRng = x;
        return (x >> 8) * (1f / 16777216f);
    }

    private float RandRange(float a, float b) => a + (b - a) * Rand01();
}
