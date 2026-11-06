using Godot;
using System;

/// <summary>
/// 大球：由左侧大球槽的火力沿机枪方向发射，阵营灭亡时它的机枪和弹珠也会变成大球飞出。
/// 自带引力吸粒子（和中心抢），吸到就变大；碾过敌方/中立领土按 1 点/像素消耗数值；
/// 碰敌方大球或护盾：双方各扣较小者的 80% 并弹开（视频实测，和撞击角度无关）；敌方子弹扣它，己方子弹喂它。
/// 发射后匀速直线运动，碰墙 / 护盾镜面反弹，和别的大球弹性碰撞（质量 ∝ 数值^0.58，视频实测）；
/// 数值越高跑得越快一点（按要求，10 亿以上 +20%，见 GameSettings.BigBallSpeedBoost）。
/// 撞护盾按画出来的护盾环算，护盾突然变大 / 变小时球不会跟着瞬移（见 RightArena.StepBigBalls）。
///
/// 外观（视频实测）：黑色球芯 + 贴着球边的亮队色环，环向内是一圈渐暗的内发光（约半个半径宽），
/// 环外不画光晕（视频里有一小圈，用户要求去掉）；数值变化后半径不是瞬间跳变，而是很快缓动过去（τ ≈ 0.1 秒）。
/// 同样的数值越到后期画得越小（见 RadiusFor）。
/// </summary>
public sealed class BigBall
{
    // 视频实测：8.6M 的球被撞成 1.7M，半径 109.5 → 102.5 → 95.2 → 91.5 → 90.0（每 0.1 秒）；刚生成时也是约 0.3 秒长到应有的大小
    private const float RadiusEaseTau = 0.1f;
    private const float MinRadius = 6f;

    public int     Team;
    public Vector2 Pos;
    public Vector2 Vel;
    public double  Value;
    public bool    Dead;
    public float   Age;
    public float   HitFlash;
    /// <summary>上次绘制以来吸进来的粒子数（按粒子阵营）：球芯闪烁亮点按它画，画完清零。</summary>
    public readonly int[] CoreIntake = new int[4];
    /// <summary>累计吸进来的粒子数（调试统计用）。</summary>
    public long Absorbed;
    /// <summary>调试（--events）：上一步结束时的位置和速度，查「一步里挪得比速度能走的远」的瞬移。</summary>
    public Vector2 DbgPos;
    public float DbgSpeed;

    /// <summary>当前的速度倍数（数值越高越快，见 GameSettings.BigBallSpeedBoost）：数值变了，速度按新旧倍数之比跟着缩放。</summary>
    public float SpeedMul = 1f;

    /// <summary>当前半径（向 RadiusFor(Value, 对局时间) 缓动；碰撞、涂色、引力圈、绘制都用它）。</summary>
    public float Radius;

    /// <param name="time">生成时的对局时间（秒）。</param>
    /// <param name="startRadius">生成时的半径（&lt;0 = 直接按数值算）；之后很快长到应有的大小。</param>
    public BigBall(int team, Vector2 pos, Vector2 vel, double value, float time, float startRadius = -1f)
    {
        Team   = team;
        Pos    = pos;
        Vel    = vel;
        Value  = value;
        float r = RadiusFor(value, time);
        Radius = startRadius < 0f ? r : MathF.Min(startRadius, r);
    }

    /// <summary>
    /// 半径 = 15.6 + 24.9·log10(数值 / 1000) − 0.0194·t（t = 对局秒数），至少 6。
    /// 视频实测 43 个球（59K..2.7B，t = 96..1440 秒，量球边亮环的外沿）：同样的数值越到后期画得越小，
    /// 相当于「同样大小的球对应的数值」每 387 秒翻一倍。只按数值拟合残差 4.7 px，加上时间项 2.0 px。
    /// 例（视频 / 公式）：179K@96 秒 72 / 70，2.9M@500 秒 92 / 92，16M@1022 秒 99 / 100，2.7B@1275 秒 156 / 151。
    /// </summary>
    public static float RadiusFor(double value, float time) =>
        MathF.Max(MinRadius, 15.6f + 24.9f * (float)Math.Log10(Math.Max(value, 1.0) / 1000.0) - 0.0194f * time);

    public void EaseRadius(float dt, float time)
    {
        Radius += (RadiusFor(Value, time) - Radius) * (1f - MathF.Exp(-dt / RadiusEaseTau));
    }

    /// <summary>引力半径 = 球半径 + 球面外的吸引距离（GameSettings.BallGravityReach）。</summary>
    public float GravityRadius => Radius + Main.Settings.BallGravityReach;

    public Color Tint => Main.TeamColors[Team];
}
