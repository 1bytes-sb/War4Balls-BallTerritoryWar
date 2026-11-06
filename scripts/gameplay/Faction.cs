using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// 右侧战场上的一个阵营：机枪火力、护盾、狙击/风暴/大球火力、粒子充能、炮台朝向、通知。
/// 数值用 double，免得两千五百万的护盾被一颗 100 的子弹扣不动。
/// </summary>
public sealed class Faction
{
    public int     Team;
    public Vector2 Center;       // 场内坐标
    public bool    Alive = true;

    // ── 资源 ───────────────────────────────────────────────────────────────────
    public double MgFire;        // 机枪火力：每步打一颗，每颗带走当前火力的 0.03%
    public double MgShown;       // 显示值：往上跳的时候按对数数上去，往下直接跟
    public float  MgBoxAlpha;    // 机枪框：机枪火力从 0 变成非零时淡入
    public double Shield;        // 护盾（吸收敌方子弹/大球伤害）
    public double ShieldShown;   // 显示值，向 Shield 缓动
    public float  RingR, RingV;  // 护盾环画出来的半径和它的变化速度：护盾从无到有时从中心长出来（临界阻尼弹簧追 ShieldRadius）
    public float  BreakLife, BreakR;   // 护盾刚被狙击打破：碎掉的环还挡着狙击子弹的剩余时间、环的半径（见 RightArena.HitShieldOrCenter）
    public float  ShieldFlash;   // 护盾增加以后那一行闪框、数字染粉的剩余时间
    private double _lastShield;
    public double SniperFire;    // 狙击火力（攒够一发再打）
    public double StormFire;     // 风暴火力：还没打出去的那几圈
    public int    StormPulsesLeft;
    public float  StormTimer;    // 距下一圈的时间
    public float  StormAngle;    // 这次风暴各圈共用的角度偏移（视频里三圈子弹走同一组射线）
    public double BigBallFire;   // 大球火力

    // ── 充能 ────────────────────────────────────────────────────────────────────
    public double Charge;
    public int    ExtraMarbles;  // 充满过几次（HUD 圆环里的数字）
    public double ChargeThreshold =>
        Main.Settings.ChargeFull + Main.Settings.ChargeStep * ExtraMarbles;

    // ── 炮台 ─────────────────────────────────────────────────────────────────────
    public float TurretAngle;    // 机枪朝向（弧度，y 向下，角度增大 = 顺时针）
    public bool  PdActive;       // 正在点防
    public double PartIncome;   // 调试：累计粒子收入（被中心吸收的粒子价值）
    public double BallCharge;   // 调试：自己的大球吸粒子累计给了多少充能
    public long  PdShots;        // 调试：累计打了多少颗点防子弹
    public readonly double[] ShieldHitBy = new double[5];   // 调试：护盾累计被机枪 / 点防 / 狙击 / 风暴 / 大球扣了多少
    public readonly double[] StormKilledBy = new double[3]; // 调试：累计打掉了多少敌方风暴（机枪 / 点防 / 其他子弹）
    public double PdPaintLoss;   // 调试：点防子弹路上涂色累计花掉多少
    public float PdHold;         // 点防目标丢失后再保持一小会儿

    // ── 特效计时 ─────────────────────────────────────────────────────────────────
    public float HitFlash;       // 护盾被打时的闪光
    public float DeathTime = -1f;
    public bool  Revived;        // 已经用掉了复活机会（第一次阵亡时左侧最大的弹珠变成护盾）
    public bool  Reviving;       // 复活弹珠正从左侧飞过来：落地才有护盾，这段时间中心打不死
    public float LandTime = -1f; // 复活弹珠落地的时刻（模拟秒，没落地过 = −1）：胜率模型看落地以后活了多久

    // ── 通知（中心上方：标题 + 数额） ──────────────────────────────────────────
    public string NoteTitle;
    public double NoteAmount;
    public float  NoteLife;
    public const float NoteDuration = 1.8f;
    // 每条提示在中心放一次脉冲（光晕盘 + 亮边线从中心长到半径 58.5 再淡掉）：这里记各个脉冲放出来多久了
    public readonly List<float> Pulses = new();
    public const float PulseLife = 1.8f;

    public Faction(int team, Vector2 center, float startAngle)
    {
        Team        = team;
        Center      = center;
        TurretAngle = startAngle;
    }

    public bool ShieldUp => Shield > 0.0;

    /// <summary>
    /// 护盾半径 r = 27.7 + 1.014·护盾^(1/4)：视频里 17 个护盾读数和环半径拟合（1.7M ≈ 64、25M ≈ 99、
    /// 2.4 亿 ≈ 155、49 亿 ≈ 296 像素，均方根误差 3 像素）。这也是子弹、大球撞护盾的半径。
    /// </summary>
    public static float ShieldRadiusFor(double shield) => (float)ShieldRadiusD(shield);

    /// <summary>同上（双精度，护盾互相抵消时二分用），封顶 ShieldMaxRadius = 场地边长的一半（用户要求；约 470 亿到顶）。</summary>
    public static double ShieldRadiusD(double shield) =>
        Math.Min(Main.Settings.ShieldMaxRadius, 27.7 + 1.014 * Math.Sqrt(Math.Sqrt(Math.Max(shield, 0.0))));

    /// <summary>护盾为 0 时那圈淡线、护盾破碎冲击波的起点半径（视频实测 ≈ 70 像素）。</summary>
    public const float ZeroShieldLineRadius = 70f;

    public float ShieldRadius => ShieldRadiusFor(Shield);
    public float ShieldRadiusShown => ShieldRadiusFor(ShieldShown);

    /// <summary>新通知：同一标题还没消失就累加数额，否则替换。</summary>
    public void AddNote(string title, double amount, float age = 0f)
    {
        if (NoteLife > 0.3f && NoteTitle == title) NoteAmount += amount;
        else { NoteTitle = title; NoteAmount = amount; }
        NoteLife = NoteDuration;
        // 视频里每条提示（狙击、机枪、护盾……）都跟着一次脉冲；挨得太近的就不再放，最多同时 4 个
        if (Pulses.Count == 0 || Pulses[^1] > 0.15f)
        {
            Pulses.Add(age);
            if (Pulses.Count > 4) Pulses.RemoveAt(0);
        }
    }

    public void Tick(float dt)
    {
        if (NoteLife > 0f) NoteLife -= dt;
        for (int i = Pulses.Count - 1; i >= 0; i--)
        {
            Pulses[i] += dt;
            if (Pulses[i] >= PulseLife) Pulses.RemoveAt(i);
        }
        // 护盾增加：那一行闪框 0.6 秒（视频开局 +25M 在 5.0~5.5 秒、中途 +16K 在 57.3~57.8 秒）
        if (Shield > _lastShield + 0.5) ShieldFlash = 0.6f;
        _lastShield = Shield;
        if (ShieldFlash > 0f) ShieldFlash -= dt;
        // 护盾环半径：临界阻尼弹簧追 ShieldRadius（τ = 0.42 s）；没护盾就回到 0，下次从中心长出来
        // （视频开局 4.88 秒 +25M：5.75 秒 63、6.0 秒 75、6.25 秒 84、6.5 秒 89、7 秒 96、8 秒 99 像素）
        if (Shield <= 0.0) { RingR = 0f; RingV = 0f; }
        else
        {
            const float w = 1f / 0.42f;
            float h = MathF.Min(dt, 1f / 30f);                   // 卡顿时（结束画面按帧时间走）步子太大会振荡
            RingV += (w * w * (ShieldRadius - RingR) - 2f * w * RingV) * h;
            RingR += RingV * h;
        }
        if (HitFlash > 0f) HitFlash -= dt;
        if (Shield > 0.0) BreakLife = 0f;
        else if (BreakLife > 0f) BreakLife -= dt;
        // 显示值缓动：往上涨 τ ≈ 1 s（视频开局 +25M 后 5.0 秒 2.76M、6.0 秒 16.84M、7.0 秒 22.00M），
        // 再加每秒 1% 目标值的匀速，尾巴不拖（视频 8.0 秒 24.08M，9.5 秒以前显示到位）；
        // 被打掉时很快跟上（τ ≈ 0.04 s：视频 686.8 秒大球撞掉 0.96M，约 0.15 秒显示到位）
        double d = Shield - ShieldShown, k = 1.0 - Math.Exp(-dt / (d < 0.0 ? 0.04 : 1.0));
        double step = d * k + Math.Sign(d) * 0.01 * Math.Abs(Shield) * dt;
        if (Math.Abs(step) >= Math.Abs(d) || Math.Abs(d) < 50.0) ShieldShown = Shield;
        else ShieldShown += step;
        // 机枪数值往上跳的时候按对数匀速数上去，每秒 57 个数量级（视频开局 +500K：1.80 → 1.90 秒从两位数数到七位数）；往下直接跟
        if (MgFire <= MgShown) MgShown = MgFire;
        else MgShown = Math.Min(MgFire, Math.Exp(Math.Log(MgShown + 1.0) + 131.0 * dt) - 1.0);
        // 机枪框：机枪火力是 0 的时候没有框，一有火力 0.1 秒淡入
        MgBoxAlpha = Math.Clamp(MgBoxAlpha + (MgShown >= 1.0 ? dt : -dt) / 0.1f, 0f, 1f);
    }
}
