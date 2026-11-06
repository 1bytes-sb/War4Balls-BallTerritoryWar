using Godot;
using System;
using System.Collections.Generic;
using static Ui;
using static Loc;

/// <summary>
/// 资金模式下方的胜率走势图（像 Polymarket）：四条阵营线（0~100%），横轴是这局的模拟时间；
/// 下单 / 平仓的位置画小标记（▲ 做多、▼ 做空、○ 平仓）；鼠标移上去显示那一刻的各队胜率。
/// 数据都在 TradePanel 里（Hist、Marks、P）。
/// </summary>
public partial class WinChart : Control
{
    public TradePanel Src;

    private const float PadL = 58f, PadT = 44f, PadB = 30f;
    private static float PadR => En ? 112f : 92f;     // 右边放线尾的「队名 胜率」，英文队名长一点
    private static readonly Color GridCol = new Color(0.30f, 0.40f, 0.58f, 0.22f);
    private static readonly Color AxisText = new Color(0.56f, 0.62f, 0.72f);

    private float _hoverX = -1f;
    private StyleBoxFlat _bg;
    private double _lo, _hi = 1.0;     // 纵轴范围：跟着线自动缩放（开局四条线都在 25% 附近，也看得出走势）

    public override void _Ready()
    {
        _bg = Box(PanelCol, PanelBorder, 12, 0f, 0f);
        MouseFilter = MouseFilterEnum.Pass;
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseMotion mm)
        {
            _hoverX = mm.Position.X;
            QueueRedraw();
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationMouseExit)
        {
            _hoverX = -1f;
            QueueRedraw();
        }
    }

    private Rect2 Plot => new Rect2(PadL, PadT, Size.X - PadL - PadR, Size.Y - PadT - PadB);

    private float TMax => Math.Max(60f, Src?.Now ?? 0f);

    private Vector2 ToScreen(float t, double p)
    {
        Rect2 r = Plot;
        return new Vector2(r.Position.X + r.Size.X * t / TMax, r.End.Y - r.Size.Y * (float)Math.Clamp((p - _lo) / (_hi - _lo), 0.0, 1.0));
    }

    /// <summary>纵轴范围：包住所有线（上下各留 3%），对齐到 5% 的整格，至少跨 20%。</summary>
    private void UpdateRange()
    {
        double mn = 1.0, mx = 0.0;
        for (int t = 0; t < Main.TeamCount; t++)
        {
            foreach (Vector2 v in Src.Hist[t])
            {
                mn = Math.Min(mn, v.Y);
                mx = Math.Max(mx, v.Y);
            }
            if (!Src.Dead[t])
            {
                mn = Math.Min(mn, Src.P[t]);
                mx = Math.Max(mx, Src.P[t]);
            }
        }
        if (mn > mx) (mn, mx) = (0.0, 1.0);
        const double step = 0.05, pad = 0.03, minSpan = 0.2;
        double lo = Math.Max(0.0, Math.Floor((mn - pad) / step + 1e-9) * step);
        double hi = Math.Min(1.0, Math.Ceiling((mx + pad) / step - 1e-9) * step);
        if (hi - lo < minSpan)
        {
            lo = Math.Floor(((lo + hi) / 2.0 - minSpan / 2.0) / step + 1e-9) * step;
            hi = lo + minSpan;
            if (lo < 0.0) (lo, hi) = (0.0, minSpan);
            if (hi > 1.0) (lo, hi) = (1.0 - minSpan, 1.0);
        }
        (_lo, _hi) = (lo, hi);
    }

    public static string Clock(float t)
    {
        int s = Math.Max(0, (int)t);
        return $"{s / 60}:{s % 60:00}";
    }

    public override void _Draw()
    {
        DrawStyleBox(_bg, new Rect2(Vector2.Zero, Size));
        if (Src == null) return;
        Rect2 plot = Plot;
        UpdateRange();

        string title = L("胜率走势", "Win odds");
        Fonts.DrawLeft(this, Fonts.UiBold, new Vector2(18f, 22f), title, 21, TextCol);
        float subX = 18f + Fonts.UiBold.GetStringSize(title, HorizontalAlignment.Left, -1f, 21).X + 10f;
        Fonts.DrawLeft(this, Fonts.Ui, new Vector2(subX, 23f), L($"第 {Src.GameNo} 局 · {Clock(Src.Now)}", $"Game {Src.GameNo} · {Clock(Src.Now)}"), 17, DimCol);

        // 图例：右上角，各队现在的胜率
        float lx = Size.X - 18f;
        for (int t = Main.TeamCount - 1; t >= 0; t--)
        {
            string s = Src.Dead[t] ? $"{Team(t)} {L("淘汰", "out")}" : $"{Team(t)} {Src.P[t] * 100.0:0.0}%";
            float w = Fonts.UiBold.GetStringSize(s, HorizontalAlignment.Left, -1f, 17).X;
            Fonts.DrawLeft(this, Fonts.UiBold, new Vector2(lx - w, 22f), s, 17, Src.Dead[t] ? DimCol : RightArena.RingColors[t]);
            DrawCircle(new Vector2(lx - w - 12f, 22f), 5f, new Color(RightArena.RingColors[t], Src.Dead[t] ? 0.35f : 1f));
            lx -= w + 34f;
        }

        // 横线：范围小每 5% 一条，大一点每 10%，整个 0~100% 每 25%
        double span = _hi - _lo;
        int gridStep = span <= 0.301 ? 5 : span <= 0.601 ? 10 : 25;
        for (int pc = (int)Math.Round(_lo * 100.0); pc <= (int)Math.Round(_hi * 100.0); pc++)
        {
            if (pc % gridStep != 0) continue;
            float y = ToScreen(0f, pc / 100.0).Y;
            DrawLine(new Vector2(plot.Position.X, y), new Vector2(plot.End.X, y), pc == 50 ? new Color(GridCol, 0.4f) : GridCol, 1f);
            Fonts.DrawRight(this, Fonts.Ui, new Vector2(plot.Position.X - 8f, y), $"{pc}%", 15, AxisText);
        }

        // 时间刻度
        float tMax = TMax;
        float step = 30f;
        foreach (float st in new[] { 30f, 60f, 120f, 300f, 600f, 1200f })
        {
            step = st;
            if (tMax / st <= 9f) break;
        }
        for (float t = 0f; t <= tMax + 0.01f; t += step)
        {
            float x = ToScreen(t, 0.0).X;
            DrawLine(new Vector2(x, plot.Position.Y), new Vector2(x, plot.End.Y), new Color(GridCol, 0.5f), 1f);
            Fonts.DrawCentered(this, Fonts.Ui, new Vector2(x, plot.End.Y + 15f), Clock(t), 14, AxisText);
        }

        // 四条线
        var ends = new List<(float y, int t)>();
        for (int t = 0; t < Main.TeamCount; t++)
        {
            List<Vector2> h = Src.Hist[t];
            if (h.Count == 0) continue;
            int n = h.Count + (Src.Dead[t] ? 0 : 1);
            var pts = new Vector2[n];
            for (int i = 0; i < h.Count; i++) pts[i] = ToScreen(h[i].X, h[i].Y);
            if (!Src.Dead[t]) pts[n - 1] = ToScreen(Src.Now, Src.P[t]);
            Color c = RightArena.RingColors[t];
            if (n >= 2)
            {
                DrawPolyline(pts, new Color(c, 0.18f), 6f, true);
                DrawPolyline(pts, c, 2.4f, true);
            }
            Vector2 end = pts[n - 1];
            DrawCircle(end, 4.5f, c);
            if (!Src.Dead[t]) ends.Add((end.Y, t));
        }

        // 线尾标签（太挤就上下推开）
        ends.Sort((a, b) => a.y.CompareTo(b.y));
        for (int i = 1; i < ends.Count; i++)
            if (ends[i].y - ends[i - 1].y < 18f) ends[i] = (ends[i - 1].y + 18f, ends[i].t);
        float over = ends.Count > 0 ? ends[^1].y - (plot.End.Y + 6f) : 0f;
        for (int i = 0; i < ends.Count; i++)
        {
            float y = ends[i].y - Math.Max(0f, over);
            int t = ends[i].t;
            Fonts.DrawLeft(this, Fonts.UiBold, new Vector2(plot.End.X + 10f, y), $"{Team(t)} {Src.P[t] * 100.0:0}%", 16,
                RightArena.RingColors[t]);
        }

        // 下单 / 平仓标记
        foreach (var m in Src.Marks)
        {
            Vector2 p = ToScreen(m.Time, m.P);
            Color c = RightArena.RingColors[m.Team];
            if (m.Open)
            {
                float d = m.Long ? -1f : 1f;              // ▲ 做多（尖朝上）、▼ 做空
                var tri = new[] { p + new Vector2(0f, 8f * d), p + new Vector2(-6.5f, -4f * d), p + new Vector2(6.5f, -4f * d) };
                DrawColoredPolygon(tri, c);
                DrawPolyline(new[] { tri[0], tri[1], tri[2], tri[0] }, Colors.White, 1.4f, true);
            }
            else
            {
                DrawCircle(p, 5.5f, new Color(0.03f, 0.05f, 0.1f));
                DrawArc(p, 5.5f, 0f, Mathf.Tau, 20, Colors.White, 1.6f, true);
            }
        }

        // 鼠标所在时刻
        if (_hoverX >= plot.Position.X && _hoverX <= plot.End.X)
        {
            float t = (_hoverX - plot.Position.X) / plot.Size.X * tMax;
            if (t <= Src.Now)
            {
                DrawLine(new Vector2(_hoverX, plot.Position.Y), new Vector2(_hoverX, plot.End.Y), new Color(1f, 1f, 1f, 0.35f), 1f);
                var lines = new List<(string s, Color c)> { (Clock(t), TextCol) };
                for (int k = 0; k < Main.TeamCount; k++)
                {
                    double? v = ValueAt(k, t);
                    if (v != null) lines.Add(($"{Team(k)} {v.Value * 100.0:0.0}%", RightArena.RingColors[k]));
                }
                float bw = 112f, bh = 10f + 21f * lines.Count;
                foreach (var (ls, _) in lines) bw = Math.Max(bw, 20f + Fonts.UiBold.GetStringSize(ls, HorizontalAlignment.Left, -1f, 16).X);
                float bx = _hoverX + 12f + bw > Size.X - 4f ? _hoverX - 12f - bw : _hoverX + 12f;
                float by = plot.Position.Y + 4f;
                DrawRect(new Rect2(bx, by, bw, bh), new Color(0.02f, 0.04f, 0.09f, 0.92f));
                DrawRect(new Rect2(bx, by, bw, bh), new Color(PanelBorder, 0.8f), false, 1f);
                for (int i = 0; i < lines.Count; i++)
                    Fonts.DrawLeft(this, i == 0 ? Fonts.Ui : Fonts.UiBold, new Vector2(bx + 10f, by + 15f + 21f * i), lines[i].s, 16, lines[i].c);
            }
        }
    }

    /// <summary>阵营 team 在时刻 t 的胜率（那之前最近的一个点）；还没开始或已淘汰之后返回 null。</summary>
    private double? ValueAt(int team, float t)
    {
        List<Vector2> h = Src.Hist[team];
        if (h.Count == 0 || t < h[0].X) return null;
        if (Src.Dead[team] && t > h[^1].X) return null;
        int lo = 0, hi = h.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (h[mid].X <= t) lo = mid;
            else hi = mid - 1;
        }
        return h[lo].Y;
    }
}
