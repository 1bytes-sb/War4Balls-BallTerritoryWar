using Godot;

/// <summary>
/// 左侧弹珠台的颜色球。4 种颜色对应右侧 4 个阵营，数值随翻倍槽增长。
/// 半径随数值按对数变大；身后拖一条同色彗星尾巴（视频里的样子）。
/// </summary>
public partial class GrowthBall : RigidBody2D
{
    public int TeamIndex;
    public Color BallColor = Colors.White;
    public float BallMass = 16f;          // 球的数值
    public float CurrentRadius = 7f;
    public float MaxYSinceTeleport;       // 调试：传送后到过的最低点（屏幕 y 最大值）
    public float ZoneX = -1f, ZoneT;      // 调试：传送后第一次落到底部槽上方那段（y > SlotEdgePullTop）时的 x 和时间
    public float PassStart;               // 这一轮开始的时刻（GameTime）：撞杯 / 进槽传送回顶上时重置；出生时 0（胜率模型的期望定价用）

    private CollisionShape2D _shape;
    private Vector2? _teleportPos;
    private Vector2 _teleportVel;
    private Vector2 _lastPos;
    private float _stuckTimer;

    // 彗星尾巴：最近若干个物理帧的位置（环形缓冲，约 0.3 秒）。尾巴由 LeftArena 的尾巴层统一画
    public const int TrailLen = 48;
    private readonly Vector2[] _trail = new Vector2[TrailLen];
    private int _trailHead;
    private int _trailCount;
    public int TrailCount => _trailCount;
    /// <summary>第 k 个尾巴点（k=0 最新），父节点坐标。</summary>
    public Vector2 TrailPoint(int k) => _trail[(_trailHead - 1 - k + TrailLen * 2) % TrailLen];

    private static GameSettings S => Main.Settings;

    public override void _Ready()
    {
        Mass = 10f;                         // 物理质量固定，数值再大也不会把别的球撞飞
        GravityScale = 0f;                  // 重力由脚本手动施加，方便和气流叠加
        LinearDampMode = DampMode.Replace;
        LinearDamp = 0f;
        AngularDampMode = DampMode.Replace;
        AngularDamp = 5f;
        LockRotation = true;
        PhysicsMaterialOverride = new PhysicsMaterial { Friction = 0.2f, Bounce = 0.45f };

        CurrentRadius = RadiusFor(BallMass, 0f);
        _shape = new CollisionShape2D { Shape = new CircleShape2D { Radius = CurrentRadius } };
        AddChild(_shape);

        CollisionLayer = 32u;               // 球
        CollisionMask = 96u;                // 32(球) | 64(墙/钉子)
        ZIndex = 3;
        TextureFilter = TextureFilterEnum.LinearWithMipmaps;
        _lastPos = Position;
    }

    /// <summary>
    /// 半径：n = log2(v / ref(t))，ref(t) = 16·2^(t/BallRadiusDoublingSeconds)（140 s，比气流用的 115 s 慢：用户要求弹珠变小慢一点），x = 6 + 0.95·n。
    /// 旧版范围内照旧 clamp(x, 6, 24)；超过 24 按对数慢慢变大；比参考值轻过 LightDoublings 那条线（打满 10 分钟后才有）才慢慢缩小。
    /// </summary>
    public static float RadiusFor(float value, float time)
    {
        double sec = S.BallRadiusDoublingSeconds;
        double n = Doublings(value, time, sec), x = 6.0 + 0.95 * n, rs = S.BallSoftRadius, k = S.BallSoftRadiusScale;
        if (x > rs) return (float)(rs + k * System.Math.Log(1.0 + (x - rs) / k));
        double m = LightDoublings(value, time, sec);
        return (float)(m > 0.0 ? 6.0 * System.Math.Pow(2.0, -m / S.BallShrinkDoublings) : System.Math.Max(6.0, x));
    }

    /// <summary>比旧版下限那条线（参考值再轻 BallFlatSeconds/sec 次翻倍）还轻几次翻倍；数值不低于 16 的球前 10 分钟恒为 0。</summary>
    public static double LightDoublings(float value, float time) => LightDoublings(value, time, S.BallSizeDoublingSeconds);

    /// <summary>同上，参考值每 sec 秒翻倍（半径用 BallRadiusDoublingSeconds，气流用 BallSizeDoublingSeconds）。</summary>
    public static double LightDoublings(float value, float time, double sec) =>
        System.Math.Max(0.0, -Doublings(value, time, sec) - S.BallFlatSeconds / sec);

    /// <summary>数值比同一时刻的参考值 16·2^(t/115) 多几次翻倍（按对数算，打多久都不会溢出）。</summary>
    public static double Doublings(float value, float time) => Doublings(value, time, S.BallSizeDoublingSeconds);

    public static double Doublings(float value, float time, double sec) =>
        System.Math.Log2(System.Math.Max(value, 1e-6)) - System.Math.Log2(S.BallStartValue) - time / sec;

    /// <summary>v·SizeScale(t) = 开局时同样大小的球的数值（半径只取决于它）。</summary>
    public static float SizeScale(float time) => Mathf.Pow(2f, -time / S.BallSizeDoublingSeconds);

    /// <summary>老接口：不传时间时用 GameTime（兼容旧调用）。</summary>
    public static float RadiusFor(float value) => RadiusFor(value, Main.GameTime);

    public void UpdateRadiusFromMass()
    {
        float newR = RadiusFor(BallMass, Main.GameTime);
        if (Mathf.Abs(newR - CurrentRadius) > 0.05f)
        {
            CurrentRadius = newR;
            if (_shape?.Shape is CircleShape2D c) c.Radius = CurrentRadius;
        }
        QueueRedraw();                      // 数值变了，文字要重画
    }

    /// <summary>用指定时间更新半径（开局或重建时使用）。</summary>
    public void UpdateRadiusFromTime(float time)
    {
        float newR = RadiusFor(BallMass, time);
        if (Mathf.Abs(newR - CurrentRadius) > 0.05f)
        {
            CurrentRadius = newR;
            if (_shape?.Shape is CircleShape2D c) c.Radius = CurrentRadius;
            QueueRedraw();                  // 球体画在本地坐标里，只有半径变了才需要重画
        }
    }

    public void ApplyMultiplier(float mult)
    {
        BallMass *= mult;
        UpdateRadiusFromMass();
    }

    public void ResetValue()
    {
        BallMass = S.BallStartValue;
        UpdateRadiusFromMass();
    }

    /// <summary>现在的位置和速度；刚撞杯 / 进槽、还没传送过去的用传送的落点和初速度（胜率模型用）。</summary>
    public void PosVel(out Vector2 pos, out Vector2 vel)
    {
        pos = _teleportPos ?? GlobalPosition;
        vel = _teleportPos.HasValue ? _teleportVel : LinearVelocity;
    }

    public void TeleportTo(Vector2 pos, Vector2 velocity)
    {
        _teleportPos = pos;
        _teleportVel = velocity;
        PassStart = Main.GameTime;
        MaxYSinceTeleport = pos.Y;
        ZoneX = -1f;
        _trailCount = 0;                    // 传送不拖尾巴，光柱由 LeftArena 画
    }

    public override void _IntegrateForces(PhysicsDirectBodyState2D state)
    {
        // 传送必须在这里做，位置修正才不会被物理服务器覆盖
        if (_teleportPos.HasValue)
        {
            state.Transform = new Transform2D(0f, _teleportPos.Value);
            state.LinearVelocity = _teleportVel;
            _teleportPos = null;
            _stuckTimer = 0f;
            _lastPos = state.Transform.Origin;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;

        // 最大速度限制
        if (LinearVelocity.Length() > S.GrowthBallMaxSpeed)
            LinearVelocity = LinearVelocity.Normalized() * S.GrowthBallMaxSpeed;

        // 重力
        ApplyCentralForce(Vector2.Down * S.GrowthBallGravity * Mass);

        // 空气阻力（速度越大越明显）
        float spd = LinearVelocity.Length();
        if (spd > 20f)
            ApplyCentralForce(-LinearVelocity.Normalized() * (spd * spd * S.GrowthBallDrag * Mass));

        // 卡住检测：长时间几乎不动就给一脚
        if (Position.DistanceSquaredTo(_lastPos) < S.GrowthBallStuckDistance * S.GrowthBallStuckDistance)
        {
            _stuckTimer += dt;
            if (_stuckTimer >= S.GrowthBallStuckThreshold)
            {
                LinearVelocity = new Vector2((float)GD.RandRange(-80f, 80f), -260f);
                _stuckTimer = 0f;
            }
        }
        else
        {
            _stuckTimer = 0f;
            _lastPos = Position;
        }

        // 记录尾巴（等待传送时不记，免得旧位置连到新位置拉出一条长线）
        if (_teleportPos.HasValue) return;
        _trail[_trailHead] = Position;
        _trailHead = (_trailHead + 1) % TrailLen;
        if (_trailCount < TrailLen) _trailCount++;
    }

    public override void _Draw()
    {
        long _pt0 = RightArena.Prof ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        float r = CurrentRadius;
        // 玻璃球贴图（缓存、带 mipmap）：一次调用，边缘抗锯齿
        DrawTextureRect(BallArt.Ball(BallColor), new Rect2(-r, -r, 2f * r, 2f * r), false);

        // 数值文字：白色粗体，宽 ≈ 0.8 个直径（视频），深色同色系细描边
        string txt = FormatValue(BallMass);
        int fs = FitFontSize(txt, r);
        Fonts.DrawCentered(this, Fonts.UiBold, new Vector2(0f, 0.3f), txt, fs, Colors.White,
            fs < 8 ? 1 : Mathf.Max(2, fs / 6), new Color(BallColor.Darkened(0.75f), 0.85f));
        if (RightArena.Prof) RightArena.ProfX[2] += System.Diagnostics.Stopwatch.GetTimestamp() - _pt0;
    }

    private static int FitFontSize(string txt, float r)
    {
        const int refSize = 20;
        float w = Fonts.UiBold.GetStringSize(txt, HorizontalAlignment.Left, -1f, refSize).X;
        int fs = (int)(refSize * r * 1.6f / Mathf.Max(w, 1f));
        fs = Mathf.Min(fs, (int)(r * 1.4f));
        // 旧版 8–32 号；球比 6 px 小、比 24 px 大时（打很久才会有）按半径等比放宽（小于 8 号时描边改成 1 px）
        int lo = r >= 6f ? 8 : Mathf.Max(4, Mathf.RoundToInt(8f * r / 6f));
        int hi = r <= 24f ? 32 : Mathf.RoundToInt(32f * r / 24f);
        return Mathf.Clamp(fs, lo, hi);
    }

    /// <summary>
    /// 数值格式（视频）：1000 以下是整数；再往上用 K/M/B/T，不到 10 保留一位小数、10 以上取整（四舍五入），
    /// 例如 512、4.1K、16K、131K、2.1M、34M、1.1B。
    /// </summary>
    public static string FormatValue(double v)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (v < 0) v = 0;
        if (v < 999.5) return System.Math.Round(v).ToString("0", inv);
        string[] units = { "K", "M", "B", "T", "Q" };
        int u = -1;
        while (v >= 999.5 && u < units.Length - 1) { v /= 1000; u++; }
        return (v < 9.95 ? v.ToString("0.#", inv) : System.Math.Round(v).ToString("0", inv)) + units[u];
    }
}
