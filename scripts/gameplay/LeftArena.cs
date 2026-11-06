using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// 左侧弹珠台，屏幕坐标 760×1080，节点置于 (0,0)。
/// 包含：物理边框/杯/柱/旋转钉环，中央气流，触发器，绘图，公开 API。
/// </summary>
public partial class LeftArena : Node2D
{
    // ── 公开 API ────────────────────────────────────────────────────────────────
    public Action<int, string, float> OnZoneTriggered;
    public static readonly string[] ZoneNames = { "风暴", "机枪", "护盾", "大球", "狙击" };

    // ── 杯子配置（左组；右组 x' = 760 − x） ──────────────────────────────────
    // 杯中心固定，杯宽随时间线性变宽：w(t) = w0 + t/60。
    // 杯与杯之间是实心玻璃块（组外沿 0、内沿 222.5 固定），所以杯越宽玻璃块越薄。
    private struct CupDef
    {
        public float Center;   // 杯中心 x
        public float W0;       // t=0 时的全宽
        public float Mult;     // 倍率
    }

    private static readonly CupDef[] CupsLeft =
    {
        new CupDef { Center = 26.5f, W0 = 23.75f, Mult = 8f },
        new CupDef { Center = 91.5f, W0 = 47.75f, Mult = 4f },
        new CupDef { Center = 177f,  W0 = 62.75f, Mult = 2f },
    };

    private const float CupGroupW       = 222.5f;   // 杯组内沿
    private const float CupWallTopSlope = 0.132f;   // 块顶 y = 427.6 + 0.132·x（左组坐标）
    private const float CupWallTopBase  = 427.6f;
    private const float CupFloorY       = 536f;
    private const float CupBaseY        = 540.5f;
    private const float CupTriggerY     = 490f;
    private const float MinBlockW       = 2f;       // 玻璃块最薄保留

    // ── 底部柱子配置 ──────────────────────────────────────────────────────────
    private struct PillarDef
    {
        public float StartX, EndX;
    }

    private static readonly PillarDef[] Pillars =
    {
        new PillarDef { StartX = 150.5f, EndX = 115.5f },
        new PillarDef { StartX = 302.5f, EndX = 276.5f },
        new PillarDef { StartX = 457.5f, EndX = 483.5f },
        new PillarDef { StartX = 609.5f, EndX = 644.5f },
    };

    private const float PillarW       = 24f;
    private const float PillarTopK    = 0.000128f;  // top_y = 935.8 - K*(cx-380)^2
    private const float PillarTopBase = 935.8f;
    private const float PillarT       = 960f;       // ease 完成时间
    private const float SlotTriggerY  = 965f;

    // ── 墙体与地板 ──────────────────────────────────────────────────────────────
    private const float BoardW   = 760f;
    private const float BoardH   = 1080f;
    private const float WallL2   = 11.5f;
    private const float WallR1   = 748.5f;
    private const float WallTopY = 918f;    // 底部两侧短墙顶
    private const float FloorY   = 1051f;

    // ── 气流区域 ─────────────────────────────────────────────────────────────────
    private const float UpdraftX1 = 222.5f;
    private const float UpdraftX2 = 537.5f;
    private const float UpdraftY1 = 430f;
    private const float UpdraftY2 = 560f;

    // ── 传送落点（视频实测：顶部 x 在 380 ± 258 内均匀分布，不会落到最外侧的 X8/X4 上方） ──
    private const float TeleportX1 = 122f;
    private const float TeleportX2 = 638f;

    // ── 颜色（取自视频） ──────────────────────────────────────────────────────────
    private static readonly Color BgCenter     = new Color(6f/255, 24f/255, 46f/255);
    private static readonly Color BgMid        = new Color(4f/255, 19f/255, 37f/255);
    private static readonly Color BgEdge       = new Color(1f/255, 12f/255, 25f/255);
    private static readonly Color GridColor    = new Color(1f, 1f, 1f, 0.025f);
    private static readonly Color GlassTop     = new Color(14f/255, 35f/255, 58f/255);
    private static readonly Color GlassBottom  = new Color(26f/255, 69f/255, 91f/255);
    private static readonly Color PillarTop    = new Color(4f/255, 21f/255, 35f/255);
    private static readonly Color PillarBottom = new Color(15f/255, 50f/255, 63f/255);
    private static readonly Color GlassOutline = new Color(135f/255, 168f/255, 192f/255);
    private static readonly Color SlotPanel    = new Color(3f/255, 12f/255, 26f/255);
    private static readonly Color SlotGlow     = new Color(9f/255, 27f/255, 44f/255);
    private static readonly Color FloorLine    = new Color(84f/255, 106f/255, 117f/255);
    private static readonly Color BelowFloor   = new Color(1f/255, 10f/255, 21f/255);
    private static readonly Color LabelColor   = new Color(215f/255, 238f/255, 1f);
    private static readonly Color CountColor   = new Color(0.90f, 0.95f, 1f);
    private static readonly Color UpdraftArrow = new Color(0.62f, 0.76f, 0.88f, 0.55f);
    private static readonly Color SpawnLineColor = new Color(0.55f, 0.75f, 0.90f, 0.40f);

    // ── 内部状态 ─────────────────────────────────────────────────────────────────
    private static GameSettings S => Main.Settings;
    public static float TraceEvery;          // 调试：>0 时每隔这么多模拟秒打 [mt] 弹珠轨迹（--mtrace）
    private float _nextTrace;

    private StaticBody2D _wallBody;
    private StaticBody2D _cupBody;
    private StaticBody2D _pillarBody;

    // 杯组玻璃块：左 4 块 + 右 4 块
    private CollisionShape2D[] _blockShapes;
    private float _cupRebuildTimer;
    private const float CupRebuildInterval = 1.0f;

    private CollisionShape2D[] _pillarShapes;
    private float _pillarRebuildTimer;
    private const float PillarRebuildInterval = 0.25f;

    private static readonly (float cx, float cy, float r, float omega)[] SpinnerDefs =
    {
        (212f, 222f, 123.5f, -1.0f),
        (547f, 222f, 123.5f,  1.0f),
        (212f, 718f, 135f,   -1.2f),
        (547f, 718f, 135f,    1.2f),
    };

    private readonly List<GrowthBall> _balls = new List<GrowthBall>();
    // 改过槽概率时的固定拉力场：4 段各自的水平加速度（px/s²，开局时 BuildSlotPull 算好），_slotPullOn = false 就是没有
    private readonly float[] _slotPull = new float[4];
    private bool _slotPullOn;

    // 杯子触发计数 / 发光（左 3 + 右 3）
    private readonly int[]   _cupHitCount  = new int[6];
    private readonly float[] _cupGlowTimer = new float[6];
    private readonly int[]   _cupGlowTeam  = new int[6];
    private const float CupGlowTime = 0.8f;

    // 槽触发计数 / 发光
    private readonly int[]   _slotHitCount  = new int[5];
    private readonly float[] _slotGlowTimer = new float[5];
    private readonly int[]   _slotGlowTeam  = new int[5];
    private const float SlotGlowTime = 0.8f;

    // 传送光柱（旧位置 → 新位置）
    private struct Beam
    {
        public Vector2 From, To;
        public float Life;
        public Color Col;
        public float W;                  // 半宽 = 球半径（视频：光带宽 ≈ 球直径）
    }
    private readonly List<Beam> _beams = new List<Beam>();
    private const float BeamLife = 1.15f;   // 视频：约 1.1–1.15 s 线性淡出

    // 槽触发扩散环
    private struct Ring
    {
        public Vector2 Pos;
        public float Life;
        public Color Col;
    }
    private readonly List<Ring> _rings = new List<Ring>();

    private float _arrowTime;            // 气流箭头连续计时（不再按周期回绕，避免跳变）

    // 颜色球尾巴层：所有球的尾巴攒成一个三角形数组一次画完（ZIndex 在球下面）
    private DrawLayer _trailLayer;
    private readonly List<Vector2> _tp = new List<Vector2>();
    private readonly List<Color> _tc = new List<Color>();
    private readonly List<int> _ti = new List<int>();

    // 背景 / 气流的渐变贴图
    private GradientTexture2D _bgTex;
    private GradientTexture2D _updraftTex;

    // 每帧复用的几何缓冲
    private readonly float[] _cupL = new float[3];
    private readonly float[] _cupR = new float[3];
    private readonly float[] _blockA = new float[4];
    private readonly float[] _blockB = new float[4];

    // ── _Ready ─────────────────────────────────────────────────────────────────
    public override void _Ready()
    {
        var wallMat = new PhysicsMaterial { Friction = 0.1f, Bounce = 0.3f };
        var glassMat = new PhysicsMaterial { Friction = 0.0f, Bounce = 0.3f };

        // 外框：左右边缘、天花板、地板；底部两侧短墙（只有 918..1051 这一段）
        _wallBody = new StaticBody2D { CollisionLayer = 64u, CollisionMask = 32u, PhysicsMaterialOverride = wallMat };
        _wallBody.AddChild(MakeBox(-40f, -40f, 0f, BoardH + 40f));
        _wallBody.AddChild(MakeBox(BoardW, -40f, BoardW + 40f, BoardH + 40f));
        _wallBody.AddChild(MakeBox(-40f, -40f, BoardW + 40f, 0f));
        _wallBody.AddChild(MakeBox(-40f, FloorY, BoardW + 40f, FloorY + 40f));
        _wallBody.AddChild(MakePoly(RoundedTopRect(0f, WallL2, WallTopY, FloorY, 3f, 2)));
        _wallBody.AddChild(MakePoly(RoundedTopRect(WallR1, BoardW, WallTopY, FloorY, 3f, 2)));
        AddChild(_wallBody);

        // 杯组：8 个玻璃块（随时间重建）+ 2 块底板
        _cupBody = new StaticBody2D { CollisionLayer = 64u, CollisionMask = 32u, PhysicsMaterialOverride = glassMat };
        _blockShapes = new CollisionShape2D[8];
        for (int i = 0; i < 8; i++)
        {
            var cs = new CollisionShape2D { Shape = new ConvexPolygonShape2D() };
            _cupBody.AddChild(cs);
            _blockShapes[i] = cs;
        }
        _cupBody.AddChild(MakeBox(0f, CupFloorY, CupGroupW, CupBaseY));
        _cupBody.AddChild(MakeBox(BoardW - CupGroupW, CupFloorY, BoardW, CupBaseY));
        AddChild(_cupBody);
        RebuildCups();

        // 底部柱子
        _pillarBody = new StaticBody2D { CollisionLayer = 64u, CollisionMask = 32u, PhysicsMaterialOverride = glassMat };
        _pillarShapes = new CollisionShape2D[4];
        for (int i = 0; i < 4; i++)
        {
            var cs = new CollisionShape2D { Shape = new ConvexPolygonShape2D() };
            _pillarBody.AddChild(cs);
            _pillarShapes[i] = cs;
        }
        AddChild(_pillarBody);
        RebuildPillars();
        BuildSlotPull();

        foreach (var (cx, cy, r, omega) in SpinnerDefs)
        {
            var sp = new Spinner { Omega = omega, OrbitRadius = r };
            sp.Position = new Vector2(cx, cy);
            AddChild(sp);
        }

        _trailLayer = new DrawLayer { ZIndex = 2, OnDraw = DrawTrails };
        AddChild(_trailLayer);
        TextureFilter = TextureFilterEnum.LinearWithMipmaps;

        BuildGradients();
        SpawnOpeningBalls();
    }

    private void BuildGradients()
    {
        // 背景：中心偏上稍亮的径向渐变
        var g = new Gradient();
        g.Offsets = new float[] { 0f, 0.55f, 1f };
        g.Colors  = new Color[] { BgCenter, BgMid, BgEdge };
        _bgTex = new GradientTexture2D
        {
            Gradient = g, Width = 256, Height = 256,
            Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.38f), FillTo = new Vector2(1.1f, 0.38f),
        };

        // 气流（视频）：一条横向的淡蓝光带，中间最亮、向四周柔和淡出（拉伸成椭圆的径向渐变）
        var gu = new Gradient();
        var glow = new Color(0.18f, 0.55f, 0.78f);
        gu.Offsets = new float[] { 0f, 0.35f, 0.7f, 1f };
        gu.Colors  = new Color[] { new Color(glow, 0.17f), new Color(glow, 0.12f), new Color(glow, 0.04f), new Color(glow, 0f) };
        _updraftTex = new GradientTexture2D
        {
            Gradient = gu, Width = 128, Height = 64,
            Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(1f, 0.5f),
        };
    }

    // ── 每帧更新 ────────────────────────────────────────────────────────────────
    public override void _Process(double delta)
    {
        float dt = (float)delta;

        _cupRebuildTimer += dt;
        if (_cupRebuildTimer >= CupRebuildInterval)
        {
            _cupRebuildTimer = 0f;
            RebuildCups();
        }

        _pillarRebuildTimer += dt;
        if (_pillarRebuildTimer >= PillarRebuildInterval)
        {
            _pillarRebuildTimer = 0f;
            RebuildPillars();
        }

        for (int i = 0; i < 6; i++) if (_cupGlowTimer[i] > 0f) _cupGlowTimer[i] -= dt;
        for (int i = 0; i < 5; i++) if (_slotGlowTimer[i] > 0f) _slotGlowTimer[i] -= dt;

        for (int i = _beams.Count - 1; i >= 0; i--)
        {
            var b = _beams[i];
            b.Life -= dt;
            if (b.Life <= 0f) _beams.RemoveAt(i);
            else _beams[i] = b;
        }
        for (int i = _rings.Count - 1; i >= 0; i--)
        {
            var rg = _rings[i];
            rg.Life -= dt;
            if (rg.Life <= 0f) _rings.RemoveAt(i);
            else _rings[i] = rg;
        }

        _arrowTime = (_arrowTime + dt) % 3200f;     // 3200 s 是 58·3200/32 的整数周期，回绕无跳变

        QueueRedraw();
        _trailLayer.QueueRedraw();
    }

    // ── 物理帧：气流 + 触发器 ────────────────────────────────────────────────────
    public override void _PhysicsProcess(double delta)
    {
        long _pt0 = RightArena.Prof ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        float t = Main.GameTime;
        GetCupEdges(t);
        // 气流看个头：阈值开局 33K，之后随半径参考值一起翻倍（按对数算，打多久都不封顶、不溢出）
        double windLog2Span = System.Math.Log2(S.WindMassThreshold / S.BallStartValue);

        for (int bi = _balls.Count - 1; bi >= 0; bi--)
        {
            if (bi >= _balls.Count) continue;   // 回调里可能移除了球
            var ball = _balls[bi];
            if (!IsInstanceValid(ball) || ball.IsQueuedForDeletion()) { _balls.RemoveAt(bi); continue; }

            Vector2 pos = ball.GlobalPosition;
            if (pos.Y > ball.MaxYSinceTeleport) ball.MaxYSinceTeleport = pos.Y;

            // u: 和开局 16 一样大 → 0，阈值 → 1（按个头算，见 GrowthBall.Doublings）；u < 0 的球升力照旧版按 0 算
            float u = (float)(GrowthBall.Doublings(ball.BallMass, t) / windLog2Span);
            bool leftHalf = pos.X < BoardW * 0.5f;

            // ─── 吹不动的球：在杯组上方被中间的气流口吸过去，大多直接掉进空隙，偶尔才进杯 ───
            if (u >= 1f && pos.Y > S.WindHeavyPullTop && pos.Y < CupTriggerY - 25f &&
                (leftHalf ? pos.X < UpdraftX1 + ball.CurrentRadius : pos.X > UpdraftX2 - ball.CurrentRadius))
            {
                ball.ApplyCentralForce(new Vector2(leftHalf ? S.WindHeavyPull : -S.WindHeavyPull, 0f) * ball.Mass);
            }

            // ─── 气流：力 = 质量 × 加速度（ApplyCentralForce 本身就按物理步积分，不能再乘 dt） ───
            if (pos.X >= UpdraftX1 && pos.X <= UpdraftX2 &&
                pos.Y >= UpdraftY1 && pos.Y <= UpdraftY2)
            {
                // 越大的球升力越小，阈值以下始终大于重力（旧版）；比参考值轻过 LightDoublings 那条线的小球
                // （打满 10 分钟后才有）再按半条斜率慢慢变大。过了阈值只剩不到重力的升力 —— 球减速后慢慢沉下去
                float lift0 = u < 1f
                    ? S.WindLiftMax + (S.WindLiftMin - S.WindLiftMax) * Mathf.Max(u, 0f)
                      + (S.WindLiftMax - S.WindLiftMin) * S.WindLightGain * (float)(GrowthBall.LightDoublings(ball.BallMass, t) / windLog2Span)
                    : S.GrowthBallGravity * S.WindHeavyLift * Mathf.Max(0f, 1f - (u - 1f) / S.WindHeavyFadeU);

                // 靠近气流顶部时减弱，球被「吐」出去而不是贴着顶边
                float lift = lift0 * Mathf.Clamp((pos.Y - UpdraftY1) / 60f, 0f, 1f);

                // 上升限速：超过上限后只抵消重力，不再加速；升力比开局大的球，限速跟着 √升力 放宽
                float riseMax = lift0 > S.WindLiftMax ? S.WindMaxRiseSpeed * Mathf.Sqrt(lift0 / S.WindLiftMax) : S.WindMaxRiseSpeed;
                float vy = ball.LinearVelocity.Y;
                if (vy < -riseMax)
                    lift = Mathf.Min(lift, S.GrowthBallGravity);

                // 只把吹得动的球往两侧吐；沉下去的大球不推，免得被推进最内侧的 X2
                float jitter = (float)GD.RandRange(-S.WindJitter, S.WindJitter);
                float outward = u >= 1f ? 0f : leftHalf ? -S.WindOutwardPush : S.WindOutwardPush;
                ball.ApplyCentralForce(new Vector2(jitter + outward, -lift) * ball.Mass);
            }

            ball.UpdateRadiusFromTime(t);

            // ─── 杯子触发器 ──────────────────────────────────────────────────
            if (pos.Y > CupTriggerY && pos.Y < CupBaseY + 20f)
            {
                int cup = CupAt(pos.X);
                if (cup >= 0)
                {
                    TriggerCup(ball, cup, CupsLeft[cup % 3].Mult);
                    continue;
                }
            }

            // ─── 快落到底时：在最外两条槽（风暴、狙击）上方把球往里带一点 ───────
            // 视频 1440 秒五个槽 28/62/87/51/29（风暴、狙击各约 11%），原来我们是 14% / 19%；
            // 狙击那边用户要多一点，用 SniperEdgePull（比风暴那边的 SlotEdgePull 轻）
            if (pos.Y > S.SlotEdgePullTop && pos.Y < SlotTriggerY)
            {
                float lx = GetPillarX(t, 0) + S.SlotEdgeMargin, rx = GetPillarX(t, 3) - S.SlotEdgeMargin;
                float ax = pos.X < lx ? S.SlotEdgePull : pos.X > rx ? -S.SniperEdgePull : 0f;
                if (ax != 0f) ball.ApplyCentralForce(new Vector2(ax, 0f) * ball.Mass);
            }

            // ─── 护盾槽上方往两边推，离槽中心越远推得越狠（中间几乎不动，靠柱子的被带过去）；
            //     越往下越弱，快到槽口时已经没了 —— 快落进护盾槽的球不再被吹出去 ───
            if (S.ShieldSlotPush > 0f && pos.Y > S.SlotEdgePullTop && pos.Y < S.ShieldSlotPushEndY)
            {
                float p1 = GetPillarX(t, 1), p2 = GetPillarX(t, 2);
                if (pos.X > p1 && pos.X < p2)
                {
                    float mid = (p1 + p2) * 0.5f;
                    float fade = Mathf.Min(1f, (S.ShieldSlotPushEndY - pos.Y) / S.ShieldSlotPushFade);
                    ball.ApplyCentralForce(new Vector2(S.ShieldSlotPush * fade * (pos.X - mid) / (p2 - mid), 0f) * ball.Mass);
                }
            }
            // ─── 设置界面改了槽概率：再叠一个固定拉力场（只看位置，每颗球都一样，见 BuildSlotPull） ───
            //     重力只有 180，一直推的话往中间收的场会把球越推越快、甩过头（来回晃），所以只推到 |力| / SlotPullDrag 那么快
            if (_slotPullOn && pos.Y > S.SlotPullTop && pos.Y < S.SlotPullBottom)
            {
                float ax = SlotPullAt(pos.X, t), vx = ball.LinearVelocity.X;
                if (S.SlotPullDragMode == 0) ax -= S.SlotPullDrag * vx;
                else ax = Mathf.Sign(ax) * Mathf.Clamp(Mathf.Abs(ax) - S.SlotPullDrag * vx * Mathf.Sign(ax), 0f, Mathf.Abs(ax));
                ball.ApplyCentralForce(new Vector2(ax, 0f) * ball.Mass);
            }
            if (RightArena.LogEvents && ball.ZoneX < 0f && pos.Y > S.SlotEdgePullTop) { ball.ZoneX = pos.X; ball.ZoneT = t; }

            // ─── 底部槽触发器：槽号 = 柱子中心在球左侧的个数 ───────────────────
            if (pos.Y > SlotTriggerY)
            {
                int slot = 0;
                for (int i = 0; i < 4; i++)
                    if (GetPillarX(t, i) < pos.X) slot++;
                TriggerSlot(ball, slot);
            }
        }
        if (TraceEvery > 0f && t >= _nextTrace)
        {
            _nextTrace = t + TraceEvery;
            var sb = new System.Text.StringBuilder();
            foreach (var b in _balls)
            {
                if (!IsInstanceValid(b)) continue;
                Vector2 p = b.GlobalPosition, v = b.LinearVelocity;
                sb.Append($" {b.GetInstanceId() % 100000}:{b.TeamIndex}:{b.BallMass:0}:{p.X:0.0}:{p.Y:0.0}:{v.X:0}:{v.Y:0}:{b.CurrentRadius:0.0}");
            }
            GD.Print($"[mt] t={t:0.00}{sb}");
        }
        if (RightArena.Prof) RightArena.ProfX[1] += System.Diagnostics.Stopwatch.GetTimestamp() - _pt0;
    }

    /// <summary>x 落在哪个杯里：0..2 左组，3..5 右组，-1 不在杯内。</summary>
    private int CupAt(float x)
    {
        for (int ci = 0; ci < 3; ci++)
        {
            if (x >= _cupL[ci] && x <= _cupR[ci]) return ci;
            if (x >= BoardW - _cupR[ci] && x <= BoardW - _cupL[ci]) return ci + 3;
        }
        return -1;
    }

    // ── 杯组几何 ─────────────────────────────────────────────────────────────────
    /// <summary>
    /// 计算 t 时刻左组三个杯的 [L,R] 与四个玻璃块的 [A,B]（左组坐标；右组镜像）。
    /// </summary>
    private void GetCupEdges(float t)
    {
        for (int ci = 0; ci < 3; ci++)
        {
            float hw = (CupsLeft[ci].W0 + t / 60f) * 0.5f;
            _cupL[ci] = CupsLeft[ci].Center - hw;
            _cupR[ci] = CupsLeft[ci].Center + hw;
        }
        // 玻璃块最薄保留 MinBlockW
        _cupL[0] = Mathf.Max(_cupL[0], MinBlockW);
        for (int ci = 0; ci < 2; ci++)
        {
            if (_cupL[ci + 1] - _cupR[ci] < MinBlockW)
            {
                float mid = (_cupL[ci + 1] + _cupR[ci]) * 0.5f;
                _cupR[ci] = mid - MinBlockW * 0.5f;
                _cupL[ci + 1] = mid + MinBlockW * 0.5f;
            }
        }
        _cupR[2] = Mathf.Min(_cupR[2], CupGroupW - MinBlockW);

        _blockA[0] = 0f;        _blockB[0] = _cupL[0];
        _blockA[1] = _cupR[0];  _blockB[1] = _cupL[1];
        _blockA[2] = _cupR[1];  _blockB[2] = _cupL[2];
        _blockA[3] = _cupR[2];  _blockB[3] = CupGroupW;
    }

    private static float CupTopY(float xLeftGroup) => CupWallTopBase + CupWallTopSlope * xLeftGroup;

    private void RebuildCups()
    {
        GetCupEdges(Main.GameTime);
        for (int k = 0; k < 4; k++)
        {
            float a = _blockA[k], b = _blockB[k];
            float c = Mathf.Min(3f, (b - a) * 0.45f);
            var pts = new Vector2[]
            {
                new Vector2(a,     CupTopY(a) + c),
                new Vector2(a + c, CupTopY(a + c)),
                new Vector2(b - c, CupTopY(b - c)),
                new Vector2(b,     CupTopY(b) + c),
                new Vector2(b,     CupBaseY),
                new Vector2(a,     CupBaseY),
            };
            ((ConvexPolygonShape2D)_blockShapes[k].Shape).Points = pts;

            var mirrored = new Vector2[pts.Length];
            for (int i = 0; i < pts.Length; i++)
                mirrored[i] = new Vector2(BoardW - pts[pts.Length - 1 - i].X, pts[pts.Length - 1 - i].Y);
            ((ConvexPolygonShape2D)_blockShapes[k + 4].Shape).Points = mirrored;
        }
    }

    // ── 柱子 ─────────────────────────────────────────────────────────────────────
    private void RebuildPillars()
    {
        float t = Main.GameTime;
        for (int i = 0; i < 4; i++)
        {
            float cx = GetPillarX(t, i);
            float topY = PillarTopY(cx);
            ((ConvexPolygonShape2D)_pillarShapes[i].Shape).Points =
                RoundedTopRect(cx - PillarW * 0.5f, cx + PillarW * 0.5f, topY, FloorY, 3f, 2);
        }
    }

    private static float PillarTopY(float cx) => PillarTopBase - PillarTopK * (cx - 380f) * (cx - 380f);

    private static float GetPillarX(float t, int i)
    {
        float u = Mathf.Clamp(t / PillarT, 0f, 1f);
        float frac = (1f - u) * (1f - u);
        return Pillars[i].EndX + (Pillars[i].StartX - Pillars[i].EndX) * frac;
    }

    // ── 改过槽概率时的固定拉力场（GameSettings.SlotPull*） ──────────────────────────────
    // 反推用的一维模型：虚拟球个数（每个时刻）、一局里取几个时刻、步长、格子（力先按格子算好再插值）、迭代
    private const int PullModelBalls = 400;
    private const int PullModelTimes = 8;
    private const float PullModelDs = 0.02f;
    private const float PullModelCell = 2f;
    private const int PullModelIters = 200;
    private const float PullModelStep = 400f;     // 累计份额差 1（= 100%）改多少 px/s²

    /// <summary>
    /// 开局时按设定的槽份额算 4 段拉力（第 i 段以中间三个槽的中心为界，从左往右，主要管第 i 根柱子）。
    /// 一维模型：柱子一局里会往外挪（槽跟着变宽变窄），所以在 0 到 PillarT 之间均匀取 PullModelTimes 个时刻，柱子和段的边界都按那个时刻，
    /// 各放 PullModelBalls 个虚拟球，起点是那时的「自然落点」（按默认份额在各槽里均匀），
    /// 顺着拉力场（SlotPullAt 那个形状，再左右各 SlotPullModelSmooth 像素平均）走 dx/ds = 力，s 走到 SlotPullModelK，
    /// 终点在哪个槽就算落哪个槽，几个时刻平均。4 个力从 0 开始，哪根柱子左边的累计份额多了就把那段往右推一点，反过来往左，
    /// 迭代到和设定差不多（最多 SlotPullMax）。份额是默认值时一开始就对上了，力全是 0，场没有。
    /// 模型漂的是「终点」而不是真的球，往同一边推的两段会接力（推进外段的球再被外段推），这个模型里也有。
    /// </summary>
    private void BuildSlotPull()
    {
        var def = GameSettings.CreateDefault();
        string[] bp = S.SlotPullBase.Split(',');
        var nat = new float[5];
        float total = 0f, natTotal = 0f;
        for (int k = 0; k < 5; k++)
        {
            total += Mathf.Max(0f, S.SlotShare(k));
            nat[k] = bp.Length == 5 ? float.Parse(bp[k], System.Globalization.CultureInfo.InvariantCulture) : def.SlotShare(k);
            natTotal += nat[k];
        }
        _slotPullOn = false;
        System.Array.Clear(_slotPull);
        if (!string.IsNullOrEmpty(S.SlotPullForce))
        {
            string[] p = S.SlotPullForce.Split(',');
            for (int i = 0; i < 4 && i < p.Length; i++)
                _slotPull[i] = float.Parse(p[i], System.Globalization.CultureInfo.InvariantCulture);
            _slotPullOn = true;
            GD.Print($"[slotpull] {_slotPull[0]:0}/{_slotPull[1]:0}/{_slotPull[2]:0}/{_slotPull[3]:0} (given)");
            return;
        }
        if (total <= 0f) return;

        // 自然落点的累计分布：折线，0 → 0，第 k 根柱子（那个时刻的位置）→ 自然份额（SlotPullBase，没填用默认份额）累计到第 k 个槽，右墙 → 1
        var gy = new float[6];
        gy[5] = 1f;
        float acc = 0f, cum = 0f;
        var target = new float[4];
        for (int i = 0; i < 4; i++)
        {
            acc += nat[i] / natTotal;
            gy[i + 1] = acc;
            cum += Mathf.Max(0f, S.SlotShare(i)) / total;
            target[i] = cum;
        }
        var gx = new float[PullModelTimes][];
        var start = new float[PullModelTimes][];
        var basis = new float[PullModelTimes][];
        for (int m = 0; m < PullModelTimes; m++)
        {
            float t = (m + 0.5f) / PullModelTimes * PillarT;
            gx[m] = new float[6];
            gx[m][5] = BoardW;
            for (int i = 0; i < 4; i++) gx[m][i + 1] = GetPillarX(t, i);
            basis[m] = SlotPullBasis(gx[m]);
            start[m] = new float[PullModelBalls];
            for (int b = 0, j = 1; b < PullModelBalls; b++)
            {
                float u = (b + 0.5f) / PullModelBalls;
                while (j < 5 && gy[j] < u) j++;
                start[m][b] = gx[m][j - 1] + (gx[m][j] - gx[m][j - 1]) * (u - gy[j - 1]) / (gy[j] - gy[j - 1]);
            }
        }

        var a = new float[4];
        var got = new float[4];
        int iters = 0;
        while (iters < PullModelIters)
        {
            ModelSlotPull(a, start, gx, basis, got);
            iters++;
            float worst = 0f;
            for (int i = 0; i < 4; i++)
            {
                float e = got[i] - target[i];
                a[i] = Mathf.Clamp(a[i] + PullModelStep * e, -S.SlotPullMax, S.SlotPullMax);
                // 已经顶到 SlotPullMax 还想再往外推的不算（份额太极端，这个力到头了）
                if (Mathf.Abs(a[i]) < S.SlotPullMax || e * a[i] < 0f) worst = Mathf.Max(worst, Mathf.Abs(e));
            }
            if (worst < 0.5f / PullModelBalls) break;
        }
        ModelSlotPull(a, start, gx, basis, got);
        for (int i = 0; i < 4; i++)
        {
            _slotPull[i] = a[i];
            if (Mathf.Abs(a[i]) > 0.5f) _slotPullOn = true;
        }
        if (_slotPullOn)
            GD.Print($"[slotpull] {a[0]:0}/{a[1]:0}/{a[2]:0}/{a[3]:0} model " +
                $"{got[0] * 100:0.0}/{(got[1] - got[0]) * 100:0.0}/{(got[2] - got[1]) * 100:0.0}/{(got[3] - got[2]) * 100:0.0}/{(1f - got[3]) * 100:0.0} iters {iters}");
    }

    /// <summary>一维模型（见 BuildSlotPull）：力是 a 时虚拟球的终点落在每根柱子左边的比例（各个时刻平均）→ got（累计）。
    /// 场对 4 个力是线性的：basis[m] 是第 m 个时刻每段单独为 1 时平均过的场（SlotPullBasis），这里按 a 加起来。</summary>
    private void ModelSlotPull(float[] a, float[][] start, float[][] gx, float[][] basis, float[] got)
    {
        int cells = basis[0].Length / 4;
        var f = new float[cells];
        int steps = Mathf.Max(1, Mathf.RoundToInt(S.SlotPullModelK / PullModelDs));
        System.Array.Clear(got);
        for (int m = 0; m < start.Length; m++)
        {
            float[] bm = basis[m];
            for (int c = 0; c < cells; c++)
                f[c] = a[0] * bm[c] + a[1] * bm[cells + c] + a[2] * bm[2 * cells + c] + a[3] * bm[3 * cells + c];
            float wgt = 1f / (start.Length * start[m].Length);
            foreach (float x0 in start[m])
            {
                float x = x0;
                for (int s = 0; s < steps; s++)
                {
                    float g = Mathf.Clamp(x / PullModelCell, 0f, cells - 1.001f);
                    int c = (int)g;
                    x = Mathf.Clamp(x + PullModelDs * Mathf.Lerp(f[c], f[c + 1], g - c), 0f, BoardW);
                }
                for (int i = 0; i < 4; i++)
                    if (x < gx[m][i + 1]) got[i] += wgt;
            }
        }
    }

    /// <summary>ModelSlotPull 用的基（柱子按 px[1..4]）：只有第 i 段的力是 1 时，每格左右各 SlotPullModelSmooth 像素平均过的场 → [i × 格数 + 格]。</summary>
    private float[] SlotPullBasis(float[] px)
    {
        int cells = (int)(BoardW / PullModelCell) + 1;
        var basis = new float[4 * cells];
        var unit = new float[4];
        float sm = Mathf.Max(S.SlotPullModelSmooth, 0f);
        for (int i = 0; i < 4; i++)
        {
            System.Array.Clear(unit);
            unit[i] = 1f;
            for (int c = 0; c < cells; c++)
            {
                float sum = 0f;
                for (int o = -10; o <= 10; o++) sum += SlotPullRaw(unit, c * PullModelCell + sm * o / 10f, px);
                basis[i * cells + c] = sum / 21f;
            }
        }
        return basis;
    }

    /// <summary>拉力场的形状：4 段在槽中心交界（柱子 px[1..4]），左右各 SlotPullBlend 像素里线性过渡；正 = 往右。</summary>
    private float SlotPullRaw(float[] a, float x, float[] px)
    {
        float v = a[0], w = Mathf.Max(S.SlotPullBlend, 1f);
        for (int i = 1; i < 4; i++)
        {
            float c = (px[i] + px[i + 1]) * 0.5f;
            v += (a[i] - a[i - 1]) * Mathf.Clamp((x - c) / (2f * w) + 0.5f, 0f, 1f);
        }
        return v;
    }

    /// <summary>x 处的拉力（px/s²，正 = 往右）：形状同 SlotPullRaw，段的边界跟着柱子挪。</summary>
    private float SlotPullAt(float x, float t)
    {
        float a = _slotPull[0], w = Mathf.Max(S.SlotPullBlend, 1f);
        for (int i = 1; i < 4; i++)
        {
            float c = (GetPillarX(t, i - 1) + GetPillarX(t, i)) * 0.5f;
            a += (_slotPull[i] - _slotPull[i - 1]) * Mathf.Clamp((x - c) / (2f * w) + 0.5f, 0f, 1f);
        }
        return a;
    }

    // ── 杯子触发 ──────────────────────────────────────────────────────────────────
    private void TriggerCup(GrowthBall ball, int cupIdx, float mult)
    {
        Vector2 oldPos = ball.GlobalPosition;
        if (RightArena.LogEvents) GD.Print($"[cup] t={Main.GameTime:0.0} v={ball.BallMass:0} x{mult:0} team={ball.TeamIndex} maxy={ball.MaxYSinceTeleport:0} id={ball.GetInstanceId() % 100000}");
        ball.ApplyMultiplier(mult);
        _cupHitCount[cupIdx]++;
        _cupGlowTimer[cupIdx] = CupGlowTime;
        _cupGlowTeam[cupIdx] = ball.TeamIndex;
        TeleportBallTop(ball, oldPos);
    }

    // ── 槽触发 ────────────────────────────────────────────────────────────────────
    private void TriggerSlot(GrowthBall ball, int slotIdx)
    {
        Vector2 oldPos = ball.GlobalPosition;
        float value = ball.BallMass;
        if (RightArena.LogEvents) GD.Print($"[slot] t={Main.GameTime:0.0} v={value:0} slot={slotIdx} team={ball.TeamIndex} x={oldPos.X:0} id={ball.GetInstanceId() % 100000} zx={ball.ZoneX:0} zt={Main.GameTime - ball.ZoneT:0.00}");
        OnZoneTriggered?.Invoke(ball.TeamIndex, ZoneNames[slotIdx], value);
        _slotHitCount[slotIdx]++;
        _slotGlowTimer[slotIdx] = SlotGlowTime;
        _slotGlowTeam[slotIdx] = ball.TeamIndex;
        _rings.Add(new Ring { Pos = oldPos, Life = 0.7f, Col = Main.TeamColors[ball.TeamIndex] });

        ball.ResetValue();
        TeleportBallTop(ball, oldPos);
    }

    private void TeleportBallTop(GrowthBall ball, Vector2 oldPos)
    {
        float newX = (float)GD.RandRange(TeleportX1, TeleportX2);
        float newY = S.SpawnY;
        float vx = (float)GD.RandRange(-60f, 60f);
        float vy = (float)GD.RandRange(80f, 160f);
        ball.TeleportTo(new Vector2(newX, newY), new Vector2(vx, vy));
        _beams.Add(new Beam { From = oldPos, To = new Vector2(newX, newY), Life = BeamLife, Col = ball.BallColor, W = ball.CurrentRadius });
    }

    // ── 碰撞形状辅助 ──────────────────────────────────────────────────────────────
    private static CollisionShape2D MakeBox(float x1, float y1, float x2, float y2)
    {
        float w = Mathf.Max(x2 - x1, 1f);
        float h = Mathf.Max(y2 - y1, 1f);
        return new CollisionShape2D
        {
            Shape    = new RectangleShape2D { Size = new Vector2(w, h) },
            Position = new Vector2((x1 + x2) * 0.5f, (y1 + y2) * 0.5f),
        };
    }

    private static CollisionShape2D MakePoly(Vector2[] pts) =>
        new CollisionShape2D { Shape = new ConvexPolygonShape2D { Points = pts } };

    /// <summary>顶部两角为圆角的矩形轮廓（顺序：左下 → 左上圆角 → 右上圆角 → 右下）。</summary>
    private static Vector2[] RoundedTopRect(float x1, float x2, float top, float bottom, float rad, int seg)
    {
        rad = Mathf.Min(rad, (x2 - x1) * 0.5f);
        var pts = new List<Vector2> { new Vector2(x1, bottom) };
        for (int i = 0; i <= seg; i++)
        {
            float a = Mathf.Pi + i * (Mathf.Pi * 0.5f / seg);
            pts.Add(new Vector2(x1 + rad + Mathf.Cos(a) * rad, top + rad + Mathf.Sin(a) * rad));
        }
        for (int i = 0; i <= seg; i++)
        {
            float a = Mathf.Pi * 1.5f + i * (Mathf.Pi * 0.5f / seg);
            pts.Add(new Vector2(x2 - rad + Mathf.Cos(a) * rad, top + rad + Mathf.Sin(a) * rad));
        }
        pts.Add(new Vector2(x2, bottom));
        return pts.ToArray();
    }

    // ── 开局 12 个球 ────────────────────────────────────────────────────────────
    private void SpawnOpeningBalls()
    {
        int teamCount = Main.TeamCount;
        int perTeam = S.BallPerColor;
        float spacing = BoardW / (teamCount * perTeam + 1f);

        int k = 0;
        for (int team = 0; team < teamCount; team++)
        {
            for (int b = 0; b < perTeam; b++)
            {
                k++;
                AddBall(team, new Vector2(spacing * k, S.SpawnY), 0f);
            }
        }
    }

    private GrowthBall AddBall(int team, Vector2 pos, float time)
    {
        var ball = new GrowthBall
        {
            TeamIndex = team,
            BallColor = Main.TeamColors[team],
            BallMass  = S.BallStartValue,
        };
        ball.Position = pos;
        AddChild(ball);
        ball.LinearVelocity = new Vector2(
            (float)GD.RandRange(-40f, 40f),
            (float)GD.RandRange(100f, 180f));
        ball.UpdateRadiusFromTime(time);
        _balls.Add(ball);
        return ball;
    }

    // ── 公开 API ──────────────────────────────────────────────────────────────────
    /// <summary>粒子充能满：在左上角放一个新的 16 值球。</summary>
    public void SpawnExtraBall(int team)
    {
        AddBall(team, new Vector2(36f, S.SpawnY), Main.GameTime);
    }

    /// <summary>某队现在场上的弹珠：个数、数值和、最大的数值（胜率模型用）。</summary>
    public void TeamMarbles(int team, out int count, out double sum, out double max)
    {
        count = 0;
        sum = max = 0.0;
        foreach (var ball in _balls)
        {
            if (!IsInstanceValid(ball) || ball.TeamIndex != team) continue;
            count++;
            sum += ball.BallMass;
            max = Math.Max(max, ball.BallMass);
        }
    }

    /// <summary>某队现在场上的每颗弹珠：数值、这一轮开始的时刻 GameTime、位置、速度、半径（胜率模型的期望定价用）。</summary>
    public void TeamMarbleList(int team, List<WinModel.Marble> list)
    {
        list.Clear();
        foreach (var ball in _balls)
        {
            if (!IsInstanceValid(ball) || ball.TeamIndex != team) continue;
            ball.PosVel(out Vector2 pos, out Vector2 vel);
            float u = pos.Y >= WinModel.SlotZoneY ? SlotCoord(Main.GameTime, pos, vel) : -1f;
            list.Add(new WinModel.Marble { V = ball.BallMass, T0 = ball.PassStart, Pos = pos, Vel = vel, R = ball.CurrentRadius, U = u });
        }
    }

    /// <summary>
    /// 胜率模型的进槽预判用：从 pos / vel 往下积分到槽口（重力 + 底部边缘拉力 + 护盾槽推力 + 设置界面改出来的拉力场，不管碰撞），
    /// 返回落点的「槽坐标」u = 槽号 + 在这条槽里从左到右的位置（0..1，最外两条槽算到墙）。
    /// 和 _frames/s10/slotcal.py 的 ucoord() 一样（那边没有拉力场：校准用的是默认份额）。
    /// </summary>
    public float SlotCoord(float t, Vector2 pos, Vector2 vel)
    {
        const float dt = 1f / 60f;
        float x = pos.X, y = pos.Y, vx = vel.X, vy = vel.Y;
        for (int n = 0; n < 600 && y < SlotTriggerY; n++)
        {
            float ax = 0f;
            if (y > S.SlotEdgePullTop)
            {
                float lx = GetPillarX(t, 0) + S.SlotEdgeMargin, rx = GetPillarX(t, 3) - S.SlotEdgeMargin;
                ax = x < lx ? S.SlotEdgePull : x > rx ? -S.SniperEdgePull : 0f;
            }
            if (S.ShieldSlotPush > 0f && y > S.SlotEdgePullTop && y < S.ShieldSlotPushEndY)
            {
                float p1 = GetPillarX(t, 1), p2 = GetPillarX(t, 2);
                if (x > p1 && x < p2)
                {
                    float mid = (p1 + p2) * 0.5f;
                    float fade = Mathf.Min(1f, (S.ShieldSlotPushEndY - y) / S.ShieldSlotPushFade);
                    ax += S.ShieldSlotPush * fade * (x - mid) / (p2 - mid);
                }
            }
            if (_slotPullOn && y > S.SlotPullTop && y < S.SlotPullBottom)
            {
                float ap = SlotPullAt(x, t);
                if (S.SlotPullDragMode == 0) ap -= S.SlotPullDrag * vx;
                else ap = Mathf.Sign(ap) * Mathf.Clamp(Mathf.Abs(ap) - S.SlotPullDrag * vx * Mathf.Sign(ap), 0f, Mathf.Abs(ap));
                ax += ap;
            }
            vx += ax * dt;
            vy += S.GrowthBallGravity * dt;
            x = Mathf.Clamp(x + vx * dt, 12f, BoardW - 12f);
            y += vy * dt;
            t += dt;
        }
        int g = 0;
        for (int i = 0; i < 4; i++)
            if (GetPillarX(t, i) < x) g++;
        float e0 = g == 0 ? 12f : GetPillarX(t, g - 1), e1 = g == 4 ? BoardW - 12f : GetPillarX(t, g);
        return g + Mathf.Clamp((x - e0) / (e1 - e0), 0f, 0.999f);
    }

    /// <summary>
    /// 复活：拿走某队数值最大的球，返回它的数值；pos / radius 是它的位置（全局坐标）和半径（右侧画它传送到阵营中心）。
    /// 这队一个球都没有就返回 −1。
    /// </summary>
    public float TakeLargestBall(int team, out Vector2 pos, out float radius)
    {
        pos = Vector2.Zero;
        radius = 0f;
        _balls.RemoveAll(b => !IsInstanceValid(b));
        GrowthBall best = null;
        foreach (var ball in _balls)
            if (ball.TeamIndex == team && (best == null || ball.BallMass > best.BallMass)) best = ball;
        if (best == null) return -1f;
        float value = best.BallMass;
        if (RightArena.LogEvents) GD.Print($"[revive] t={Main.GameTime:0.0} v={value:0} team={team} id={best.GetInstanceId() % 100000}");
        pos = best.GlobalPosition;
        radius = best.CurrentRadius;
        best.QueueFree();
        _balls.Remove(best);
        return value;
    }

    /// <summary>移除某队全部球并返回它们的数值（阵营死亡时变成大球）。</summary>
    public List<float> RemoveTeamBalls(int team)
    {
        var values = new List<float>();
        for (int i = _balls.Count - 1; i >= 0; i--)
        {
            var ball = _balls[i];
            if (!IsInstanceValid(ball)) { _balls.RemoveAt(i); continue; }
            if (ball.TeamIndex == team)
            {
                values.Add(ball.BallMass);
                ball.QueueFree();
                _balls.RemoveAt(i);
            }
        }
        return values;
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  绘图
    // ════════════════════════════════════════════════════════════════════════════
    public override void _Draw()
    {
        long _pt0 = RightArena.Prof ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        GetCupEdges(Main.GameTime);
        DrawBackground();
        DrawUpdraft();
        DrawCups();
        DrawPillarsAndSlots();
        DrawBeams();
        DrawRings();
        DrawSpawnLine();
        if (RightArena.Prof) RightArena.ProfX[0] += System.Diagnostics.Stopwatch.GetTimestamp() - _pt0;
    }

    private void DrawBackground()
    {
        DrawTextureRect(_bgTex, new Rect2(0f, 0f, BoardW, BoardH), false);
        for (float x = 200f; x < BoardW; x += 200f)
            DrawLine(new Vector2(x, 0f), new Vector2(x, BoardH), GridColor, 1f);
        for (float y = 200f; y < BoardH; y += 200f)
            DrawLine(new Vector2(0f, y), new Vector2(BoardW, y), GridColor, 1f);
    }

    private void DrawSpawnLine()
    {
        float y = 48.5f;
        float dash = 8f, gap = 6f, cycle = dash + gap;
        for (float x = 0f; x < BoardW; x += cycle)
            DrawLine(new Vector2(x, y), new Vector2(Mathf.Min(x + dash, BoardW), y), SpawnLineColor, 1.2f);
    }

    // ── 气流发光 + 箭头 ─────────────────────────────────────────────────────────────
    private void DrawUpdraft()
    {
        DrawTextureRect(_updraftTex, new Rect2(UpdraftX1 - 20f, 437f, UpdraftX2 - UpdraftX1 + 40f, 80f), false);

        // 视频：5 列箭头以约 60 px/s 匀速上升，同列间距约 30 px，各列相位不同，箭头在 y≈520→450 之间可见。
        // 这里连续移动（贴图 + 线性过滤，亚像素也平滑），进出窗口时按 sin 淡入淡出，不再跳。
        float[] colX = { 255f, 317f, 380f, 443f, 507f };
        float[] colPh = { 0f, 0.55f, 0.2f, 0.75f, 0.4f };
        const float speed = 58f, spacing = 30f, yStart = 506f, yEnd = 444f, aw = 9f, ah = 23f;
        var tex = BallArt.Arrow();
        for (int i = 0; i < colX.Length; i++)
        {
            float first = Mathf.PosMod(_arrowTime * speed + colPh[i] * spacing, spacing);
            for (float travel = first; travel < yStart - yEnd; travel += spacing)
            {
                float fade = Mathf.Min(1f, 1.6f * Mathf.Sin(Mathf.Pi * travel / (yStart - yEnd)));
                float tipY = yStart - travel;
                DrawTextureRect(tex, new Rect2(colX[i] - aw * 0.5f, tipY - 2f, aw, ah), false,
                    new Color(UpdraftArrow, 0.85f * fade));
            }
        }
    }

    /// <summary>所有颜色球的彗星尾巴：横截面 4 个顶点（平顶、两边透明），边缘柔和；一次调用画完。</summary>
    private void DrawTrails(DrawLayer l)
    {
        _tp.Clear(); _tc.Clear(); _ti.Clear();
        foreach (var ball in _balls)
        {
            if (!IsInstanceValid(ball)) continue;
            int n = ball.TrailCount;
            if (n < 3) continue;
            float r = ball.CurrentRadius;
            Color c = ball.BallColor;
            int baseIdx = _tp.Count;
            Vector2 prevN = Vector2.Right;
            for (int k = 0; k < n; k++)
            {
                Vector2 p = ball.TrailPoint(k);
                Vector2 dir = ball.TrailPoint(Mathf.Max(k - 1, 0)) - ball.TrailPoint(Mathf.Min(k + 1, n - 1));
                Vector2 nrm = dir.LengthSquared() < 1e-4f ? prevN : new Vector2(-dir.Y, dir.X).Normalized();
                prevN = nrm;
                float f = 1f - (float)k / (n - 1);
                // 视频：横截面是平顶 + 软边（半宽 ≈ 0.55r，尾端收到 ≈ 0.3r），亮度沿长度近似线性衰减
                float w = r * 0.62f * (0.45f + 0.55f * f);
                float a = 0.8f * f;
                Color ca = new Color(c, a), c0 = new Color(c, 0f);
                _tp.Add(p + nrm * w);         _tc.Add(c0);
                _tp.Add(p + nrm * w * 0.4f);  _tc.Add(ca);
                _tp.Add(p - nrm * w * 0.4f);  _tc.Add(ca);
                _tp.Add(p - nrm * w);         _tc.Add(c0);
                if (k == 0) continue;
                int i0 = baseIdx + (k - 1) * 4, i1 = i0 + 4;
                for (int j = 0; j < 3; j++)
                {
                    _ti.Add(i0 + j); _ti.Add(i0 + j + 1); _ti.Add(i1 + j);
                    _ti.Add(i1 + j); _ti.Add(i0 + j + 1); _ti.Add(i1 + j + 1);
                }
            }
        }
        if (_ti.Count > 0)
            RenderingServer.CanvasItemAddTriangleArray(l.GetCanvasItem(), _ti.ToArray(), _tp.ToArray(), _tc.ToArray());
    }

    // ── 杯组：玻璃块（竖直渐变）+ 连续外轮廓 + 触发填色 + 标签 ────────────────────
    private void DrawCups()
    {
        string[] labels = { "X8", "X4", "X2" };
        float glassY0 = CupWallTopBase - 1f, glassY1 = CupBaseY;

        for (int side = 0; side < 2; side++)
        {
            bool mirror = side == 1;

            // 杯内：底部略亮的暗色渐变；触发时整杯填入队伍色
            for (int ci = 0; ci < 3; ci++)
            {
                int gi = side * 3 + ci;
                float l = _cupL[ci], r = _cupR[ci];
                var inner = new[]
                {
                    new Vector2(l, CupTopY(l)), new Vector2(r, CupTopY(r)),
                    new Vector2(r, CupFloorY),  new Vector2(l, CupFloorY),
                };
                var cols = new[]
                {
                    new Color(0.04f, 0.14f, 0.24f, 0f), new Color(0.04f, 0.14f, 0.24f, 0f),
                    new Color(0.04f, 0.14f, 0.24f, 0.55f), new Color(0.04f, 0.14f, 0.24f, 0.55f),
                };
                DrawPolygon(MirrorPts(inner, mirror), cols);

                if (_cupGlowTimer[gi] > 0f)
                {
                    Color tc = Main.TeamColors[_cupGlowTeam[gi]];
                    float a = Mathf.Clamp(_cupGlowTimer[gi] / CupGlowTime, 0f, 1f);
                    var gcols = new[]
                    {
                        new Color(tc, 0.55f * a), new Color(tc, 0.55f * a),
                        new Color(tc, 0.22f * a), new Color(tc, 0.22f * a),
                    };
                    var mp = MirrorPts(inner, mirror);
                    DrawPolygon(mp, gcols);
                    DrawLine(mp[0], mp[1], new Color(tc.Lightened(0.3f), 0.9f * a), 2f);
                }
            }

            // 玻璃块 + 底板
            for (int k = 0; k < 4; k++)
            {
                var poly = BlockOutline(_blockA[k], _blockB[k], CupBaseY);
                var mp = MirrorPts(poly, mirror);
                DrawPolygon(mp, VGradColors(mp, glassY0, GlassTop, glassY1, GlassBottom));
            }
            var basePlate = new[]
            {
                new Vector2(0f, CupFloorY), new Vector2(CupGroupW, CupFloorY),
                new Vector2(CupGroupW, CupBaseY), new Vector2(0f, CupBaseY),
            };
            DrawPolygon(MirrorPts(basePlate, mirror), new[] { GlassBottom, GlassBottom, GlassBottom, GlassBottom });

            // 连续外轮廓：沿每个块的两侧 + 顶，经杯底连到下一个块，最后沿底边闭合
            var outline = new List<Vector2>();
            for (int k = 0; k < 4; k++)
            {
                var blk = BlockOutline(_blockA[k], _blockB[k], CupFloorY);
                if (k == 0) outline.Add(new Vector2(0f, CupBaseY));
                for (int i = 0; i < blk.Length; i++)
                {
                    if (k == 0 && i == 0) continue;
                    if (k == 3 && i == blk.Length - 1) continue;
                    outline.Add(blk[i]);
                }
            }
            outline.Add(new Vector2(CupGroupW, CupBaseY));
            outline.Add(new Vector2(0f, CupBaseY));
            DrawPolyline(MirrorPts(outline.ToArray(), mirror), GlassOutline, 1.3f, true);

            // 标签与计数
            for (int ci = 0; ci < 3; ci++)
            {
                int gi = side * 3 + ci;
                float ctr = (_cupL[ci] + _cupR[ci]) * 0.5f;
                if (mirror) ctr = BoardW - ctr;
                bool lit = _cupGlowTimer[gi] > 0f;
                Color lc = lit ? Main.TeamColors[_cupGlowTeam[gi]].Lightened(0.55f) : LabelColor;
                Fonts.DrawCentered(this, Fonts.UiBold, new Vector2(ctr, 502f), labels[ci], 20, lc);
                Fonts.DrawCentered(this, Fonts.UiBold, new Vector2(ctr, 521f),
                    _cupHitCount[gi].ToString(), 14, CountColor);
            }
        }
    }

    /// <summary>
    /// 玻璃块轮廓（左组坐标）：从左下 (a,bottom) 起，沿左边向上、圆角、斜顶、圆角、沿右边到 (b,bottom)。
    /// </summary>
    private static Vector2[] BlockOutline(float a, float b, float bottom)
    {
        float rad = Mathf.Min(4f, (b - a) * 0.5f);
        const int seg = 4;
        var pts = new List<Vector2>(2 * seg + 4) { new Vector2(a, bottom) };
        float ta = CupTopY(a), tb = CupTopY(b);
        for (int i = 0; i <= seg; i++)
        {
            float ang = Mathf.Pi + i * (Mathf.Pi * 0.5f / seg);
            pts.Add(new Vector2(a + rad + Mathf.Cos(ang) * rad, ta + rad + Mathf.Sin(ang) * rad));
        }
        for (int i = 0; i <= seg; i++)
        {
            float ang = Mathf.Pi * 1.5f + i * (Mathf.Pi * 0.5f / seg);
            pts.Add(new Vector2(b - rad + Mathf.Cos(ang) * rad, tb + rad + Mathf.Sin(ang) * rad));
        }
        pts.Add(new Vector2(b, bottom));
        return pts.ToArray();
    }

    private static Vector2[] MirrorPts(Vector2[] pts, bool mirror)
    {
        if (!mirror) return pts;
        var o = new Vector2[pts.Length];
        for (int i = 0; i < pts.Length; i++) o[i] = new Vector2(BoardW - pts[i].X, pts[i].Y);
        return o;
    }

    /// <summary>按 y 线性插值的顶点色（多边形内部就是精确的竖直渐变）。</summary>
    private static Color[] VGradColors(Vector2[] pts, float y0, Color c0, float y1, Color c1)
    {
        var cols = new Color[pts.Length];
        for (int i = 0; i < pts.Length; i++)
            cols[i] = c0.Lerp(c1, Mathf.Clamp((pts[i].Y - y0) / (y1 - y0), 0f, 1f));
        return cols;
    }

    // ── 底部槽 + 柱子 ────────────────────────────────────────────────────────────
    private void DrawPillarsAndSlots()
    {
        float t = Main.GameTime;
        var pillarX = new float[4];
        for (int i = 0; i < 4; i++) pillarX[i] = GetPillarX(t, i);

        float[] iconY  = { 981f, 989f, 994f, 990f, 981f };
        float[] labelY = { 1008f, 1016f, 1020f, 1016f, 1008f };

        // 地板下方的暗条（计数所在）
        DrawRect(new Rect2(0f, FloorY, BoardW, BoardH - FloorY), BelowFloor);

        for (int s = 0; s < 5; s++)
        {
            float sx = s == 0 ? WallL2 : pillarX[s - 1] + PillarW * 0.5f;
            float ex = s == 4 ? WallR1 : pillarX[s] - PillarW * 0.5f;
            float sw = ex - sx;
            float midX = (sx + ex) * 0.5f;
            float topL = s == 0 ? WallTopY : PillarTopY(pillarX[s - 1]);
            float topR = s == 4 ? WallTopY : PillarTopY(pillarX[s]);
            float top = Mathf.Min(topL, topR);

            // 槽面板：暗底 + 标签处一团淡光
            DrawRect(new Rect2(sx, top, sw, FloorY - top), SlotPanel);
            DrawEllipseGlow(new Vector2(midX, labelY[s] - 12f), sw * 0.42f, 48f, SlotGlow);

            if (_slotGlowTimer[s] > 0f)
            {
                Color tc = Main.TeamColors[_slotGlowTeam[s]];
                float a = Mathf.Clamp(_slotGlowTimer[s] / SlotGlowTime, 0f, 1f);
                var pts = new[]
                {
                    new Vector2(sx, top), new Vector2(ex, top),
                    new Vector2(ex, FloorY), new Vector2(sx, FloorY),
                };
                DrawPolygon(pts, new[] { new Color(tc, 0.10f * a), new Color(tc, 0.10f * a), new Color(tc, 0.42f * a), new Color(tc, 0.42f * a) });
                DrawRect(new Rect2(sx + 1f, top + 1f, sw - 2f, FloorY - top - 2f), new Color(tc.Lightened(0.2f), 0.8f * a), false, 2f);
            }

            DrawSlotIcon(s, new Vector2(midX, iconY[s]));
            Fonts.DrawCentered(this, Fonts.UiBold, new Vector2(midX, labelY[s]), Loc.Zone(s), 15, LabelColor);
            DrawLine(new Vector2(midX - 28f, 1040f), new Vector2(midX + 28f, 1040f), new Color(0.35f, 0.47f, 0.58f, 0.8f), 1f);
            Fonts.DrawCentered(this, Fonts.UiBold, new Vector2(midX, 1065f), _slotHitCount[s].ToString(), 14, CountColor);
        }

        // 两侧短墙 + 4 根玻璃柱：竖直渐变 + 轮廓
        DrawGlassColumn(0f, WallL2, WallTopY);
        DrawGlassColumn(WallR1, BoardW, WallTopY);
        for (int i = 0; i < 4; i++)
        {
            float cx = pillarX[i];
            DrawGlassColumn(cx - PillarW * 0.5f, cx + PillarW * 0.5f, PillarTopY(cx));
        }

        // 地板线
        DrawLine(new Vector2(0f, FloorY + 0.5f), new Vector2(BoardW, FloorY + 0.5f), FloorLine, 1.2f);
    }

    private void DrawGlassColumn(float x1, float x2, float top)
    {
        var pts = RoundedTopRect(x1, x2, top, FloorY, 4f, 4);
        DrawPolygon(pts, VGradColors(pts, top, PillarTop, FloorY, PillarBottom));
        DrawPolyline(pts, new Color(GlassOutline, 0.55f), 1.0f, true);
    }

    private void DrawEllipseGlow(Vector2 c, float rx, float ry, Color col)
    {
        const int n = 24;
        var pts = new Vector2[n + 1];
        var cols = new Color[n + 1];
        pts[0] = c; cols[0] = col;
        // 扇形三角：中心不透明，外圈透明
        for (int i = 0; i < n; i++)
        {
            float a = i * Mathf.Tau / n;
            pts[i + 1] = c + new Vector2(Mathf.Cos(a) * rx, Mathf.Sin(a) * ry);
            cols[i + 1] = new Color(col, 0f);
        }
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            DrawPrimitive(new[] { pts[0], pts[i + 1], pts[j + 1] }, new[] { cols[0], cols[i + 1], cols[j + 1] }, null);
        }
    }

    // ── 槽图标（线条简笔画） ──────────────────────────────────────────────────────
    private void DrawSlotIcon(int slot, Vector2 center)
    {
        Color c = new Color(0.78f, 0.88f, 0.96f, 0.90f);
        float s = 8f;
        switch (slot)
        {
            case 0: // 风暴：旋涡
                for (int k = 0; k < 3; k++)
                {
                    float rr = s * (1f - k * 0.28f);
                    DrawArc(center + new Vector2(k * 1.5f, k * 0.8f), rr, -2.6f + k * 0.4f, 0.9f + k * 0.4f, 12, c, 1.5f, true);
                }
                break;
            case 1: // 机枪：枪身 + 枪管 + 握把
                DrawLine(center + new Vector2(-s * 1.2f, -1f), center + new Vector2(s * 1.3f, -1f), c, 2.2f);
                DrawLine(center + new Vector2(-s * 1.2f, -1f), center + new Vector2(-s * 1.4f, 3f), c, 2f);
                DrawLine(center + new Vector2(-s * 0.1f, 0f), center + new Vector2(-s * 0.3f, 4.5f), c, 1.8f);
                DrawLine(center + new Vector2(s * 0.5f, -1f), center + new Vector2(s * 0.5f, 3f), c, 1.4f);
                break;
            case 2: // 护盾
                {
                    var sh = new[]
                    {
                        center + new Vector2(0f, -s), center + new Vector2(s * 0.85f, -s * 0.6f),
                        center + new Vector2(s * 0.7f, s * 0.35f), center + new Vector2(0f, s),
                        center + new Vector2(-s * 0.7f, s * 0.35f), center + new Vector2(-s * 0.85f, -s * 0.6f),
                        center + new Vector2(0f, -s),
                    };
                    DrawPolyline(sh, c, 1.6f, true);
                    DrawLine(center + new Vector2(0f, -s * 0.55f), center + new Vector2(0f, s * 0.6f), new Color(c, 0.6f), 1.2f);
                }
                break;
            case 3: // 大球：彗星
                DrawCircle(center + new Vector2(s * 0.35f, 0f), s * 0.55f, c);
                for (int k = -1; k <= 1; k++)
                    DrawLine(center + new Vector2(-s * 0.1f, k * s * 0.35f), center + new Vector2(-s * 1.2f, k * s * 0.55f), new Color(c, 0.7f), 1.2f);
                break;
            case 4: // 狙击：准星
                DrawArc(center, s, 0f, Mathf.Tau, 20, c, 1.4f, true);
                DrawLine(center + new Vector2(0f, -s - 3f), center + new Vector2(0f, -s * 0.35f), c, 1.3f);
                DrawLine(center + new Vector2(0f, s * 0.35f), center + new Vector2(0f, s + 3f), c, 1.3f);
                DrawLine(center + new Vector2(-s - 3f, 0f), center + new Vector2(-s * 0.35f, 0f), c, 1.3f);
                DrawLine(center + new Vector2(s * 0.35f, 0f), center + new Vector2(s + 3f, 0f), c, 1.3f);
                break;
        }
    }

    // ── 传送光带（视频）：旧位置 → 新位置一条和球一样宽的平板光带，没有亮芯，约 1.15 s 线性淡出 ──────
    //    边缘 1 px 羽化；落点再加一团很快消失的柔光，表示「到达」。
    private static readonly Vector2[] _bq = new Vector2[4];
    private static readonly Color[] _bc = new Color[4];
    private void DrawBeams()
    {
        foreach (var b in _beams)
        {
            float a = Mathf.Clamp(b.Life / BeamLife, 0f, 1f);
            Color c = b.Col.Lightened(0.1f);
            Vector2 d = b.To - b.From;
            if (d.LengthSquared() > 1f)
            {
                Vector2 n = new Vector2(-d.Y, d.X).Normalized();
                float wi = Mathf.Max(b.W - 1f, 0.5f), wo = b.W + 1f;
                Color ca = new Color(c, 0.24f * a), c0 = new Color(c, 0f);
                BeamQuad(b.From + n * wi, b.To + n * wi, b.To - n * wi, b.From - n * wi, ca, ca, ca, ca);
                BeamQuad(b.From + n * wo, b.To + n * wo, b.To + n * wi, b.From + n * wi, c0, c0, ca, ca);
                BeamQuad(b.From - n * wi, b.To - n * wi, b.To - n * wo, b.From - n * wo, ca, ca, c0, c0);
            }
            float age = BeamLife - b.Life;
            if (age < 0.4f)
            {
                float h = 1f - age / 0.4f;
                float R = b.W * (2.0f + 1.6f * age / 0.4f);
                DrawTextureRect(BallArt.Soft(), new Rect2(b.To.X - R, b.To.Y - R, 2f * R, 2f * R), false,
                    new Color(c.Lightened(0.3f), 0.55f * h * h));
            }
        }
    }

    private void BeamQuad(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, Color c0, Color c1, Color c2, Color c3)
    {
        _bq[0] = p0; _bq[1] = p1; _bq[2] = p2; _bq[3] = p3;
        _bc[0] = c0; _bc[1] = c1; _bc[2] = c2; _bc[3] = c3;
        DrawPrimitive(_bq, _bc, null);
    }

    private void DrawRings()
    {
        foreach (var rg in _rings)
        {
            float frac = 1f - rg.Life / 0.7f;
            float r = 6f + frac * 50f;
            float a = rg.Life / 0.7f * 0.75f;
            DrawArc(rg.Pos, r, 0f, Mathf.Tau, 32, new Color(rg.Col, a), 2.0f, true);
        }
    }
}
