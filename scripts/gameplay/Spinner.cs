using Godot;

/// <summary>
/// 旋转钉环：1 个固定中心钉 + 6 个卫星钉绕其匀速旋转（视频里左侧 4 组）。
/// 卫星挂在一个 AnimatableBody2D 上整体旋转，物理引擎能算出接触速度，
/// 所以颜色球撞上去会被「拨」走，而不是撞到一个瞬移的静态体。
/// </summary>
public partial class Spinner : Node2D
{
    public float Omega = 1.0f;          // rad/s，正 = 顺时针（屏幕 y 轴向下）
    public float OrbitRadius = 123f;
    public int SatelliteCount = 6;
    public float StartAngle;

    private AnimatableBody2D _ring;
    private DrawLayer _ringVis;          // 卫星钉的画面：只画一次，靠 Rotation 转动，不必每帧重画
    private float _angle;

    private static GameSettings S => Main.Settings;

    public override void _Ready()
    {
        var mat = new PhysicsMaterial { Friction = 0.05f, Bounce = 0.35f };

        // 中心固定钉
        var center = new StaticBody2D { CollisionLayer = 64u, CollisionMask = 32u, PhysicsMaterialOverride = mat };
        center.AddChild(new CollisionShape2D { Shape = new CircleShape2D { Radius = S.PegRadius } });
        AddChild(center);

        // 6 个卫星钉
        _ring = new AnimatableBody2D
        {
            CollisionLayer = 64u,
            CollisionMask = 32u,
            SyncToPhysics = true,
            PhysicsMaterialOverride = mat
        };
        for (int i = 0; i < SatelliteCount; i++)
        {
            float a = i * Mathf.Tau / SatelliteCount;
            _ring.AddChild(new CollisionShape2D
            {
                Shape = new CircleShape2D { Radius = S.PegRadius },
                Position = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * OrbitRadius
            });
        }
        AddChild(_ring);

        _ringVis = new DrawLayer { OnDraw = l =>
        {
            for (int i = 0; i < SatelliteCount; i++)
            {
                float a = i * Mathf.Tau / SatelliteCount;
                DrawPeg(l, new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * OrbitRadius);
            }
        } };
        AddChild(_ringVis);
        TextureFilter = TextureFilterEnum.LinearWithMipmaps;

        _angle = StartAngle;
        _ring.Rotation = _angle;
        _ringVis.Rotation = _angle;
    }

    public override void _PhysicsProcess(double delta)
    {
        _angle = Mathf.Wrap(_angle + Omega * (float)delta, 0f, Mathf.Tau);
        _ring.Rotation = _angle;
        _ringVis.Rotation = _angle;
    }

    public override void _Draw()
    {
        DrawPeg(this, Vector2.Zero);
    }

    /// <summary>钉子用缓存贴图画（带 mipmap 的抗锯齿圆片），一个钉子一次调用。</summary>
    private static void DrawPeg(CanvasItem ci, Vector2 p)
    {
        float r = S.PegRadius + 0.5f;
        ci.DrawTextureRect(BallArt.Peg(), new Rect2(p.X - r, p.Y - r, 2f * r, 2f * r), false);
    }
}
