using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using static Ui;
using static Loc;

/// <summary>
/// 资金模式的交易界面（CanvasLayer，不跟着游戏画面缩放）：游戏画面缩到 GameScale 放在左上角（Main 设 CanvasTransform），
/// 右边一列是账户、下单、四个阵营的价格、持仓、成交记录，下面一条是胜率走势图（WinChart）。
/// 价格 = WinModel 算的获胜概率：按模拟时间每 PriceEvery 秒更新一次，不平滑（成交就按这一刻的胜率），限制在 1%~99%；
/// 剩 4 队、600 秒后再加一层「晚段新高加价」（WinModel.LateBoost，每局从开局记状态）。--winlog 时每次更新打一行 [lp]（核对用）。
/// 阵营被淘汰 → 它的合约按 0 结算；分出胜负（或只剩一家）→ 赢家按 $1、其余按 0 结算，这局不能再下单。
/// 按 R 重开 / 自动下一局：剩下的持仓按现价平掉（_ExitTree）；F2 回主菜单、关窗口由 Main 调 CloseAll 再结算出场。
/// 调试：--trade=秒:队:long|short:金额[:杠杆]、--trade=秒:队|all:close（模拟时间，每局都执行）。
/// </summary>
public partial class TradePanel : CanvasLayer
{
    /// <summary>走势图上的下单 / 平仓标记。</summary>
    public sealed class Mark
    {
        public float Time;
        public double P;               // 那一刻这个阵营的胜率（标记画在它的线上）
        public int Team;
        public bool Open, Long;
    }

    public const float GameScale = 0.72f;
    public const float GameW = 1920f * GameScale, GameH = 1080f * GameScale;
    private const float PriceEvery = 0.25f;     // 价格更新间隔（模拟秒）
    private const float EffSmooth = 1f;         // 「胜率构成」表的指数平滑（模拟秒，只是显示用；价格本身不平滑）
    private const float HistEvery = 1f;         // 走势图取点间隔（模拟秒）
    private const float NoteEvery = 5f;         // 记一次场内总值（崩溃恢复用，真实秒）
    private const float FlashTime = 5f;

    public Main Host;

    public float Now => _now;
    public int GameNo => Market.Current?.Game ?? 0;
    public readonly double[] P = new double[Main.TeamCount];
    public readonly bool[] Dead = new bool[Main.TeamCount];
    public readonly List<Vector2>[] Hist = new List<Vector2>[Main.TeamCount];
    public readonly List<Mark> Marks = new();

    private readonly double[] _raw = new double[Main.TeamCount];
    /// <summary>_raw 加上晚段新高加价（_late）以后的价，还没限制到 1%~99%。</summary>
    private readonly double[] _q = new double[Main.TeamCount];
    private readonly WinModel.LateBoost _late = new();
    private readonly WinModel.Snap _snap = new();
    private readonly double[,] _effRaw = new double[Main.TeamCount, WinModel.GroupCount], _eff = new double[Main.TeamCount, WinModel.GroupCount];
    private float _now, _lastPrice, _lastHist = -1e9f, _lastLog = -1e9f, _nextWl;
    private bool _primed, _over;
    private int _winner = -1;
    private float _noteTimer, _uiTimer, _flashTimer;
    private int _lev = 1, _levShown = -1;

    // 界面
    private WinChart _chart;
    private Label _clock, _cash, _posVal, _equity, _pnl, _hint, _status, _posHeader, _noPos;
    private LineEdit _amount;
    private readonly Button[] _levBtns = new Button[Market.Leverages.Length];
    private readonly Label[] _pct = new Label[Main.TeamCount], _chg = new Label[Main.TeamCount], _name = new Label[Main.TeamCount];
    private readonly Button[] _longBtn = new Button[Main.TeamCount], _shortBtn = new Button[Main.TeamCount];
    private VBoxContainer _posList;
    private readonly Label[] _fills = new Label[6];
    private Label[,] _effCells;
    private Button _closeAll;
    private readonly List<PosRow> _rows = new();
    private string _flash;
    private Color _flashCol;

    private sealed class PosRow
    {
        public Market.Position Pos;
        public Label Liq, Price, Pnl, Value, Drawn;
    }

    // ── 调试下单 ──
    private struct DebugOrder
    {
        public float T;
        public int Team;               // -1 = 全部（只用于 close）
        public int Kind;               // 0 做多、1 做空、2 平仓
        public double Amount;
        public int Lev;
    }

    private static readonly List<DebugOrder> DebugOrders = new();
    private bool[] _dbgDone;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>--trade=秒:队:long|short:金额[:杠杆] 或 --trade=秒:队|all:close；队 = 0~3、r/g/b/y 或 红/绿/蓝/黄。认不出来返回 false。</summary>
    public static bool AddDebugOrder(string spec)
    {
        string[] a = spec.Split(':');
        if (a.Length < 3 || !float.TryParse(a[0], NumberStyles.Float, Inv, out float t)) return false;
        int team = ParseTeam(a[1]);
        string kind = a[2].ToLowerInvariant();
        if (kind == "close")
        {
            if (team < 0 && a[1].ToLowerInvariant() != "all") return false;
            DebugOrders.Add(new DebugOrder { T = t, Team = team, Kind = 2 });
            return true;
        }
        if (team < 0 || (kind != "long" && kind != "short") || a.Length < 4) return false;
        if (!double.TryParse(a[3], NumberStyles.Float, Inv, out double amount)) return false;
        int lev = 1;
        if (a.Length >= 5 && !int.TryParse(a[4], out lev)) return false;
        DebugOrders.Add(new DebugOrder { T = t, Team = team, Kind = kind == "long" ? 0 : 1, Amount = amount, Lev = lev });
        return true;
    }

    private static int ParseTeam(string s)
    {
        s = s.Trim().ToLowerInvariant();
        if (int.TryParse(s, out int i) && i >= 0 && i < Main.TeamCount) return i;
        int k = Array.IndexOf(new[] { "r", "g", "b", "y" }, s);
        return k >= 0 ? k : Array.IndexOf(Main.TeamNames, s);
    }

    // ── 调试截图：--tradeshots=目录，每次淘汰、分出胜负后 0.5 秒（真实时间）存一张 g局_事件.png（Main 不认这个参数，这里自己读）──
    private static string _shotDir;
    private static bool _shotArgRead;
    private readonly List<(ulong at, string path)> _shotQueue = new();

    private void QueueShot(string name)
    {
        if (!_shotArgRead)
        {
            _shotArgRead = true;
            foreach (string a in OS.GetCmdlineUserArgs())
                if (a.StartsWith("--tradeshots=")) _shotDir = a.Substring(13);
        }
        if (_shotDir != null && DisplayServer.GetName() != "headless")
            _shotQueue.Add((Time.GetTicksMsec() + 500, $"{_shotDir}/g{GameNo}_{name}.png"));
    }

    private void RunShots()
    {
        for (int i = _shotQueue.Count - 1; i >= 0; i--)
        {
            if (Time.GetTicksMsec() < _shotQueue[i].at) continue;
            GetViewport().GetTexture().GetImage().SavePng(_shotQueue[i].path);
            GD.Print($"[screenshot] saved to {_shotQueue[i].path} sim={_now:0.0}");
            _shotQueue.RemoveAt(i);
        }
    }

    private void RunDebug(DebugOrder o)
    {
        if (o.Kind == 2)
        {
            var m = Market.Current;
            for (int i = m.Positions.Count - 1; i >= 0; i--)
                if (o.Team < 0 || m.Positions[i].Team == o.Team) ClosePosition(m.Positions[i]);
            return;
        }
        if (!Order(o.Team, o.Kind == 0, o.Amount, o.Lev))
            GD.Print($"[trade] rejected {Main.TeamNames[o.Team]} {(o.Kind == 0 ? "long" : "short")} {o.Amount:0.00} x{o.Lev}: {_flash}");
    }

    // ════════════════════════════════════════════════════════════════════════════

    public override void _Ready()
    {
        Layer = 5;
        var m = Market.Current;
        if (m != null) m.Game++;
        for (int t = 0; t < Main.TeamCount; t++)
        {
            Hist[t] = new List<Vector2>();
            P[t] = 1.0 / Main.TeamCount;
        }
        _dbgDone = new bool[DebugOrders.Count];
        Build();
        Refresh();
        GD.Print($"[market] game {GameNo} start, cash {m?.Cash ?? 0:0.00}");
    }

    public override void _Process(double delta)
    {
        var m = Market.Current;
        var r = Host?.Right;
        if (m == null || r == null) return;
        _now = r.SimTime;
        if (_shotQueue.Count > 0) RunShots();

        if (!_over) CheckOutcome(r);
        if (!_over && !Host.WinLogOn && _now >= _nextWl)
        {
            // 每 5 秒（模拟时间）一行局面（[wl] 原始数值 / [wp] 模型胜率），以后查胜率走势、重新拟合用；格式同 --winlog
            _nextWl += WinModel.WinLogEvery;
            GD.Print(WinModel.LogLine(r, Host.Left));
        }
        if (!_over && (!_primed || _now - _lastPrice >= PriceEvery))
        {
            UpdatePrices(r);
            var hit = m.Liquidate(P, _now);
            if (hit != null)
                foreach (var pos in hit)
                {
                    AddMark(pos, false);
                    string bid = Market.Cents(m.Bid(pos, P[pos.Team]));
                    Flash(pos.Drawn
                        ? L($"强平：{PosName(pos)} 卖价跌到 {bid}，持仓价值归零（借走的浮盈已经用掉），这笔合计 {SignedMoney(-pos.Margin)}",
                            $"Liquidated: {PosName(pos)} bid fell to {bid}, value is 0 (the drawn profit was already used), {SignedMoney(-pos.Margin)} in total")
                        : L($"强平：{PosName(pos)} 卖价跌到 {bid}，投入的 {Money(pos.Margin)} 亏光了",
                            $"Liquidated: {PosName(pos)} bid fell to {bid}, the {Money(pos.Margin)} margin is lost"), BadCol);
                }
            if (_now - _lastHist >= HistEvery)
            {
                _lastHist = _now;
                for (int t = 0; t < Main.TeamCount; t++)
                    if (!Dead[t]) Hist[t].Add(new Vector2(_now, (float)P[t]));
            }
            if (_now - _lastLog >= 30f)
            {
                _lastLog = _now;
                GD.Print($"[px] g{GameNo} t={_now:0} p={string.Join(",", Array.ConvertAll(P, x => x.ToString("0.000", Inv)))} equity={m.Equity(P):0.00}");
            }
            _chart.QueueRedraw();
        }

        if (!_over)
            for (int i = 0; i < _dbgDone.Length; i++)
                if (!_dbgDone[i] && DebugOrders[i].T <= _now)
                {
                    _dbgDone[i] = true;
                    RunDebug(DebugOrders[i]);
                }

        float dt = (float)delta;
        _noteTimer += dt;
        if (_noteTimer >= NoteEvery)
        {
            _noteTimer = 0f;
            Wallet.NoteSession(m.Equity(P));
        }
        if (_flashTimer > 0f)
        {
            _flashTimer -= dt;
            if (_flashTimer <= 0f) _flash = null;
        }
        _uiTimer -= dt;
        if (_uiTimer <= 0f)
        {
            _uiTimer = 0.1f;
            Refresh();
        }
    }

    public override void _Input(InputEvent e)
    {
        // 金额框里按 Esc / 点到别处：放开焦点（不然 R、F2 这些键都被输入框吃掉）
        if (_amount != null && _amount.HasFocus())
        {
            if (e is InputEventKey k && k.Pressed && k.Keycode == Key.Escape)
            {
                _amount.ReleaseFocus();
                GetViewport().SetInputAsHandled();
            }
            else if (e is InputEventMouseButton mb && mb.Pressed && !_amount.GetGlobalRect().HasPoint(mb.Position))
                _amount.ReleaseFocus();
        }
    }

    public override void _ExitTree()
    {
        // 重开（R / 自动下一局）：剩下的持仓按现价平掉；F2 / 关窗口时 Main 已经先平过了
        var m = Market.Current;
        if (m == null || m.Positions.Count == 0) return;
        m.CloseAll(P, _now, "重开平仓");
        Wallet.NoteSession(m.Cash);
    }

    // ── 价格和结算 ──

    private void UpdatePrices(RightArena r)
    {
        WinModel.Probabilities(r, Host.Left, _raw, _snap);
        WinModel.Effects(_snap, _raw, _effRaw);
        bool all4 = true;
        for (int t = 0; t < Main.TeamCount; t++)
            if (!(_snap.V[t, WinModel.Alive] > 0.5)) all4 = false;
        Array.Copy(_raw, _q, Main.TeamCount);
        _late.Apply(_now, _q, all4);
        if (Host.WinLogOn)
        {
            static string J(double[] x) => string.Join(",", Array.ConvertAll(x, v => v.ToString("R", Inv)));
            GD.Print($"[lp] g{GameNo} t={((double)_now).ToString("R", Inv)} c={(_late.Committed ? 1 : 0)} n4={(all4 ? 1 : 0)} " +
                     $"p={J(_raw)} q={J(_q)} m={J(_late.M)} sm={J(_late.SM)}");
        }
        Market.Current?.RollSpreads();
        double a = _primed ? 1.0 - Math.Exp(-(_now - _lastPrice) / EffSmooth) : 1.0;
        _primed = true;
        _lastPrice = _now;
        for (int t = 0; t < Main.TeamCount; t++)
        {
            if (Dead[t]) continue;
            // 价格不平滑：平滑过的价格落后于左边看得见的事件（撞杯翻倍、进槽、充能满），看到价格开始动再追进去几乎稳赚
            //（原来 1 秒平滑时：追 1 秒内动了 2¢ 的、拿 2 秒，平均 +4.9%，88% 赚；按这一刻的胜率成交就没有了，_frames/s10/lat2.py）
            P[t] = Math.Clamp(_q[t], Market.MinPrice, Market.MaxPrice);
            for (int g = 0; g < WinModel.GroupCount; g++) _eff[t, g] += a * (_effRaw[t, g] - _eff[t, g]);
        }
    }

    private void CheckOutcome(RightArena r)
    {
        if (r.Winner >= 0)
        {
            Finish(r.Winner);
            return;
        }
        int alive = 0, last = -1;
        for (int t = 0; t < Main.TeamCount; t++)
        {
            if (!Dead[t] && !r.Factions[t].Alive) Eliminate(t);
            if (!Dead[t]) (alive, last) = (alive + 1, t);
        }
        if (alive == 1) Finish(last);      // 只剩一家：就是它赢（等场上收尾还要一阵）
    }

    private void Eliminate(int t)
    {
        Dead[t] = true;
        Hist[t].Add(new Vector2(_now, (float)P[t]));
        Hist[t].Add(new Vector2(_now, 0f));
        P[t] = 0.0;
        double pnl = SettleTeam(t, 0.0, "淘汰结算", out int n);
        Flash(n > 0 ? L($"{Team(t)} 被淘汰：它的 {n} 笔持仓按 0 结算，{SignedMoney(pnl)}", $"{Team(t)} is out: {n} position(s) settled at 0, {SignedMoney(pnl)}")
                    : L($"{Team(t)} 被淘汰", $"{Team(t)} is out"),
            n > 0 ? (pnl >= 0 ? GoodCol : BadCol) : TextCol);
        _chart.QueueRedraw();
        QueueShot("elim_" + "rgby"[t]);
    }

    private void Finish(int w)
    {
        _over = true;
        _winner = w;
        double pnl = 0.0;
        int n = 0;
        for (int t = 0; t < Main.TeamCount; t++)
        {
            if (Dead[t]) continue;                      // 早就淘汰、结算过了
            double final = t == w ? 1.0 : 0.0;
            Hist[t].Add(new Vector2(_now, (float)P[t]));
            Hist[t].Add(new Vector2(_now, (float)final));
            P[t] = final;
            if (t != w) Dead[t] = true;
            pnl += SettleTeam(t, final, t == w ? "胜利结算" : "结算", out int k);
            n += k;
        }
        Flash(n > 0 ? L($"{Team(w)} 获胜！{n} 笔持仓结算，{SignedMoney(pnl)}", $"{Team(w)} wins! {n} position(s) settled, {SignedMoney(pnl)}")
                    : L($"{Team(w)} 获胜！", $"{Team(w)} wins!"),
            n > 0 ? (pnl >= 0 ? GoodCol : BadCol) : RightArena.RingColors[w]);
        GD.Print($"[market] g{GameNo} winner {Main.TeamNames[w]} t={_now:0.0} cash={Market.Current.Cash:0.00}");
        if (!Host.WinLogOn) GD.Print(string.Format(Inv, "[wl] win={0} t={1:0.0}", w, _now));
        Wallet.NoteSession(Market.Current.Cash);
        _chart.QueueRedraw();
        Refresh();
        QueueShot("over");
    }

    /// <summary>阵营 t 的合约到期（final = 1 赢 / 0 输），返回这些单子的盈亏合计，n = 结算了几笔。</summary>
    private double SettleTeam(int t, double final, string why, out int n)
    {
        var m = Market.Current;
        double pnl = 0.0;
        n = 0;
        for (int i = m.Positions.Count - 1; i >= 0; i--)
        {
            var pos = m.Positions[i];
            if (pos.Team != t) continue;
            AddMark(pos, false, final);
            pnl += m.Close(pos, final, _now, why, true);
            n++;
        }
        return pnl;
    }

    // ── 下单 / 平仓 ──

    /// <summary>按现价开一笔；不行（这局结束了、阵营已淘汰、钱不够……）返回 false，原因在 Flash 里。</summary>
    public bool Order(int team, bool isLong, double amount, int lev)
    {
        var m = Market.Current;
        string err = null;
        double power = 0.0;
        if (m == null || _over) err = L("这局已经结束了，等下一局", "This game is over, wait for the next one");
        else if (Dead[team]) err = L($"{Team(team)} 已经被淘汰了", $"{Team(team)} is already out");
        else if (amount < 0.01 - 1e-9) err = L("每笔至少 $0.01", "Minimum $0.01 per order");
        else if (Wallet.Cents(amount) > (power = m.BuyingPower(P)) + 1e-9) err = NoMoney(m, power);
        else if (m.InstantLiq(team, P[team], isLong, lev))
            err = L($"价格太低：{lev}× 杠杆一开仓就会被价差强平，降低杠杆", $"Price too low: {lev}× would be liquidated by the spread at once, lower the leverage");
        if (err != null)
        {
            Flash(err, BadCol);
            return false;
        }
        double price = m.Ask(team, P[team], isLong), amt = Wallet.Cents(amount);
        var pos = m.Open(team, isLong, amount, lev, P, _now);
        if (pos == null)
        {
            Flash(L("下单失败：价格超出 1¢~99¢", "Order failed: price outside 1¢–99¢"), BadCol);
            return false;
        }
        AddMark(pos, true);
        double d = m.LastDrawn;
        string zhUse = d > 0.0 ? $"，用浮盈 {Money(d)}" : "", enUse = d > 0.0 ? $", {Money(d)} from profit" : "";
        Flash(m.LastMerged
            ? L($"加仓 {PosName(pos)} {Money(amt)} @ {Market.Cents(price)} → 均价 {Market.Cents(pos.Entry)}（共 {pos.Shares:0.##} 股{zhUse}）",
                $"Added {Money(amt)} to {PosName(pos)} @ {Market.Cents(price)} → avg {Market.Cents(pos.Entry)} ({pos.Shares:0.##} shares{enUse})")
            : L($"买入 {PosName(pos)} {Money(amt)} @ {Market.Cents(pos.Entry)}（{pos.Shares:0.##} 股{zhUse}）",
                $"Bought {PosName(pos)} {Money(amt)} @ {Market.Cents(pos.Entry)} ({pos.Shares:0.##} shares{enUse})"), TextCol);
        Wallet.NoteSession(m.Equity(P));
        Refresh();
        return true;
    }

    /// <summary>钱不够的提示（power = 能用的钱：现金，开了浮盈加仓再加上能借的浮盈）。</summary>
    private static string NoMoney(Market m, double power)
    {
        if (power < 0.01) return L("现金用完了：平掉持仓，或 F2 回主菜单", "Out of cash: close a position, or F2 for the main menu");
        if (m.UseProfit && power > m.Cash + 0.005)
            return L($"最多 {Money(power)}（现金 {Money(m.Cash)} + 可借浮盈 {Money(power - m.Cash)}）",
                     $"Max {Money(power)} ({Money(m.Cash)} cash + {Money(power - m.Cash)} profit)");
        return L($"现金只有 {Money(m.Cash)}", $"Only {Money(m.Cash)} cash");
    }

    public void ClosePosition(Market.Position pos)
    {
        var m = Market.Current;
        if (m == null || !m.Positions.Contains(pos)) return;
        AddMark(pos, false);
        double pnl = m.Close(pos, P[pos.Team], _now, "平仓");
        string bid = Market.Cents(m.Bid(pos, P[pos.Team]));
        Flash(L($"平仓 {PosName(pos)} @ {bid}，{SignedMoney(pnl)}", $"Closed {PosName(pos)} @ {bid}, {SignedMoney(pnl)}"), pnl >= 0 ? GoodCol : BadCol);
        Wallet.NoteSession(m.Equity(P));
        Refresh();
    }

    /// <summary>全部按现价平掉（F2 回主菜单、关窗口、调试退出前由 Main 调）。</summary>
    public void CloseAll(string why)
    {
        var m = Market.Current;
        if (m == null || m.Positions.Count == 0) return;
        foreach (var pos in m.Positions) AddMark(pos, false);
        m.CloseAll(P, _now, why);
    }

    private void AddMark(Market.Position pos, bool open, double? p = null) =>
        Marks.Add(new Mark { Time = _now, P = p ?? P[pos.Team], Team = pos.Team, Open = open, Long = pos.Long });

    private static string PosName(Market.Position pos) => Market.Label(pos, En);

    private void Flash(string text, Color col)
    {
        _flash = text;
        _flashCol = col;
        _flashTimer = FlashTime;
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  界面
    // ════════════════════════════════════════════════════════════════════════════

    private static Button SmallButton(string text, Color col, float w, float h, int size)
    {
        var b = MakeButton(text, col, w, h, size);
        SetButtonColor(b, col, 6f, 2f);
        b.AddThemeStyleboxOverride("disabled", Box(new Color(0.09f, 0.11f, 0.16f), default, 10, 6f, 2f));
        b.FocusMode = Control.FocusModeEnum.None;        // 不抢焦点：免得空格 / 回车又下一单
        return b;
    }

    private static Label Small(string text, int size, Color col, bool bold = false) =>
        MakeLabel(text, bold ? Fonts.UiBold : Fonts.Ui, size, col);

    /// <summary>字的颜色变了才改（Refresh 一秒十次，每次改都会触发重排）。</summary>
    private static void Col(Label l, Color c)
    {
        if (!l.HasThemeColorOverride("font_color") || l.GetThemeColor("font_color") != c) SetColor(l, c);
    }

    private static Control Line() => new ColorRect { Color = new Color(PanelBorder, 0.35f), CustomMinimumSize = new Vector2(0f, 1f) };

    private void Build()
    {
        var root = new Control { Theme = MakeTheme(), MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);

        // 游戏画面以外的底色（右边一列、下面一条）
        root.AddChild(new ColorRect { Color = BgCol, Position = new Vector2(GameW, 0f), Size = new Vector2(1920f - GameW, 1080f), MouseFilter = Control.MouseFilterEnum.Ignore });
        root.AddChild(new ColorRect { Color = BgCol, Position = new Vector2(0f, GameH), Size = new Vector2(GameW, 1080f - GameH), MouseFilter = Control.MouseFilterEnum.Ignore });

        const float factW = 440f;
        _chart = new WinChart { Src = this, Position = new Vector2(6f, GameH + 6f), Size = new Vector2(GameW - 16f - factW, 1080f - GameH - 12f) };
        root.AddChild(_chart);
        root.AddChild(BuildFactors(new Vector2(GameW - 4f - factW, GameH + 6f), new Vector2(factW, 1080f - GameH - 12f)));

        var col = new PanelContainer { Position = new Vector2(GameW + 4f, 6f), Size = new Vector2(1920f - GameW - 10f, 1068f) };
        col.AddThemeStyleboxOverride("panel", Box(PanelCol, PanelBorder, 12, 16f, 12f));
        root.AddChild(col);
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 9);
        col.AddChild(v);

        // 账户
        var head = new HBoxContainer();
        head.AddChild(Small(L("资金模式", "Trading"), 26, GoodCol, true));
        head.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        _clock = Small("", 18, DimCol);
        head.AddChild(_clock);
        v.AddChild(head);

        var acc = new GridContainer { Columns = 4 };
        acc.AddThemeConstantOverride("h_separation", 10);
        acc.AddThemeConstantOverride("v_separation", 2);
        Label Cell(string name)
        {
            acc.AddChild(Small(name, 18, DimCol));
            var l = Small("", 21, TextCol, true);
            l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            acc.AddChild(l);
            return l;
        }
        _cash = Cell(L("现金", "Cash"));
        _posVal = Cell(L("持仓", "Positions"));
        _equity = Cell(L("总值", "Equity"));
        _pnl = Cell(L("盈亏", "P&L"));
        v.AddChild(acc);
        v.AddChild(Line());

        // 下单金额、杠杆
        var amtRow = new HBoxContainer();
        amtRow.AddThemeConstantOverride("separation", 6);
        amtRow.AddChild(Small(L("每笔", "Stake"), 19, TextCol));
        _amount = new LineEdit { Text = "10", CustomMinimumSize = new Vector2(En ? 80f : 96f, 38f), Alignment = HorizontalAlignment.Center, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        // LineEdit 自己的最小宽是 4 个「M」+ 边距 ≈ 110，比上面的最小宽还大，这一行会把右边一列撑出屏幕：去掉，按上面的最小宽来（ExpandFill 再分剩下的）
        _amount.AddThemeConstantOverride("minimum_character_width", 0);
        _amount.AddThemeFontOverride("font", Fonts.UiBold);
        _amount.AddThemeFontSizeOverride("font_size", 20);
        _amount.TextChanged += _ => Refresh();
        _amount.TextSubmitted += _ => _amount.ReleaseFocus();
        amtRow.AddChild(_amount);
        foreach (var (label, val) in new (string, double)[] { ("$1", -1), ("$5", -5), ("$10", -10), ("$50", -50), ("¼", 0.25), ("½", 0.5), (L("全部", "All"), 1.0) })
        {
            var b = SmallButton(label, BtnGrey, val == 1.0 && !En ? 52f : 42f, 38f, 17);
            double f = val;
            b.Pressed += () =>
            {
                double cash = Market.Current?.BuyingPower(P) ?? 0.0;      // 开了浮盈加仓：现金 + 能借的浮盈
                double a = f < 0 ? -f : Math.Floor(cash * f * 100.0 + 1e-6) / 100.0;
                _amount.Text = a.ToString(Math.Abs(a - Math.Round(a)) < 0.005 ? "0" : "0.00", Inv);
                Refresh();
            };
            amtRow.AddChild(b);
        }
        v.AddChild(amtRow);

        var levRow = new HBoxContainer();
        levRow.AddThemeConstantOverride("separation", 6);
        levRow.AddChild(Small(L("杠杆", "Leverage"), 19, TextCol));
        for (int i = 0; i < Market.Leverages.Length; i++)
        {
            int lev = Market.Leverages[i];
            var b = SmallButton($"{lev}×", BtnGrey, 60f, 36f, 18);
            b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            b.Pressed += () =>
            {
                _lev = lev;
                Refresh();
            };
            _levBtns[i] = b;
            levRow.AddChild(b);
        }
        v.AddChild(levRow);
        _hint = Small("", 16, DimCol);
        _hint.ClipText = true;
        v.AddChild(_hint);
        v.AddChild(Line());

        // 四个阵营：胜率 + 做多 / 做空
        for (int t = 0; t < Main.TeamCount; t++)
        {
            int team = t;
            var row = new HBoxContainer { CustomMinimumSize = new Vector2(0f, 50f) };
            row.AddThemeConstantOverride("separation", 8);
            // 英文的阵营名长（Yellow）：字小一点、按钮窄一点，不然这一行比右边一列还宽
            _name[t] = Small($"● {Team(t)}", En ? 19 : 22, RightArena.RingColors[t], true);
            row.AddChild(_name[t]);
            _pct[t] = Small("", 25, TextCol, true);
            _pct[t].CustomMinimumSize = new Vector2(En ? 84f : 92f, 0f);
            _pct[t].HorizontalAlignment = HorizontalAlignment.Right;
            row.AddChild(_pct[t]);
            _chg[t] = Small("", 15, DimCol);
            _chg[t].CustomMinimumSize = new Vector2(En ? 50f : 58f, 0f);
            _chg[t].SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(_chg[t]);
            _longBtn[t] = SmallButton("", BtnGreen, En ? 118f : 124f, 44f, En ? 17 : 18);
            _longBtn[t].Pressed += () => OrderFromTicket(team, true);
            row.AddChild(_longBtn[t]);
            _shortBtn[t] = SmallButton("", BtnRed, En ? 118f : 124f, 44f, En ? 17 : 18);
            _shortBtn[t].Pressed += () => OrderFromTicket(team, false);
            row.AddChild(_shortBtn[t]);
            v.AddChild(row);
        }
        var rules = Small(L("做多 = 押它赢 · 做空 = 押它输 · 赢家每股结算 $1 · 价差随机 0.1¢~0.5¢",
                            "Long = bet it wins · Short = bet it loses · winner pays $1 a share · spread 0.1–0.5¢"), 15, DimCol);
        if (En) rules.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        v.AddChild(rules);
        v.AddChild(Line());

        // 持仓
        var ph = new HBoxContainer();
        _posHeader = Small(L("持仓", "Positions"), 21, TextCol, true);
        ph.AddChild(_posHeader);
        ph.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        _closeAll = SmallButton(L("全部平仓", "Close All"), BtnRed, 104f, 34f, 17);
        _closeAll.Pressed += () =>
        {
            var m = Market.Current;
            if (m == null) return;
            for (int i = m.Positions.Count - 1; i >= 0; i--) ClosePosition(m.Positions[i]);
        };
        ph.AddChild(_closeAll);
        v.AddChild(ph);

        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _posList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _posList.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_posList);
        v.AddChild(scroll);
        _noPos = Small(L("还没有持仓：点上面的「做多 / 做空」按现价下单", "No positions yet: press Long / Short above"), 17, DimCol);
        _posList.AddChild(_noPos);
        v.AddChild(Line());

        // 成交记录
        v.AddChild(Small(L("成交记录", "Trades"), 18, DimCol, true));
        for (int i = 0; i < _fills.Length; i++)
        {
            _fills[i] = Small("", 15, DimCol);
            _fills[i].ClipText = true;
            _fills[i].TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            _fills[i].SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            v.AddChild(_fills[i]);
        }
        v.AddChild(Line());
        _status = Small("", 16, DimCol);
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _status.CustomMinimumSize = new Vector2(0f, 44f);
        _status.VerticalAlignment = VerticalAlignment.Top;
        v.AddChild(_status);
    }

    /// <summary>走势图右边的「胜率构成」：每组因素让每个阵营的胜率变了几个百分点（WinModel.Effects，和价格一样平滑）。组名上悬停看包含哪些东西。</summary>
    private Control BuildFactors(Vector2 pos, Vector2 size)
    {
        var panel = new PanelContainer { Position = pos, Size = size };
        panel.AddThemeStyleboxOverride("panel", Box(PanelCol, PanelBorder, 12, 14f, 10f));
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 6);
        panel.AddChild(v);
        v.AddChild(Small(L("胜率构成 · 各项让胜率变了几个百分点", "Win % breakdown · points each group adds"), 16, DimCol, true));

        var grid = new GridContainer { Columns = 1 + Main.TeamCount };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 3);
        grid.AddChild(new Control());
        for (int t = 0; t < Main.TeamCount; t++)
        {
            var h = Small($"● {Team(t)}", En ? 15 : 17, RightArena.RingColors[t], true);
            h.HorizontalAlignment = HorizontalAlignment.Right;
            h.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            grid.AddChild(h);
        }
        _effCells = new Label[Main.TeamCount, WinModel.GroupCount];
        for (int g = 0; g < WinModel.GroupCount; g++)
        {
            var name = Small(Group(g), 17, TextCol);
            name.TooltipText = GroupTip(g);
            name.MouseFilter = Control.MouseFilterEnum.Stop;
            grid.AddChild(name);
            for (int t = 0; t < Main.TeamCount; t++)
            {
                var c = Small("", 17, DimCol, true);
                c.HorizontalAlignment = HorizontalAlignment.Right;
                c.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                _effCells[t, g] = c;
                grid.AddChild(c);
            }
        }
        v.AddChild(grid);
        return panel;
    }

    private void OrderFromTicket(int team, bool isLong)
    {
        double? a = TicketAmount(out string err);
        if (a == null)
        {
            Flash(err, BadCol);
            return;
        }
        Order(team, isLong, a.Value, _lev);
    }

    /// <summary>金额框里的数（不能用返回 null，err 说为什么）。</summary>
    private double? TicketAmount(out string err)
    {
        var m = Market.Current;
        double? a = ParseMoney(_amount.Text);
        err = null;
        if (m == null) err = L("不在资金模式里", "Not in trading mode");
        else if (a == null) err = L("金额看不懂：填数字，比如 10 或 2.5", "Can't read the amount: enter a number, like 10 or 2.5");
        else if (Wallet.Cents(a.Value) < 0.01) err = L("每笔至少 $0.01", "Minimum $0.01 per order");
        else
        {
            double power = m.BuyingPower(P);
            if (Wallet.Cents(a.Value) > power + 1e-9) err = NoMoney(m, power);
        }
        return err == null ? Wallet.Cents(a.Value) : null;
    }

    /// <summary>胜率 sec 秒前到现在变了多少（百分点）；没有那么早的点就从这局第一个点算。</summary>
    private double Change(int t, float sec)
    {
        var h = Hist[t];
        if (h.Count == 0) return 0.0;
        float t0 = _now - sec;
        int lo = 0, hi = h.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (h[mid].X <= t0) lo = mid;
            else hi = mid - 1;
        }
        return (P[t] - h[lo].Y) * 100.0;
    }

    private void Refresh()
    {
        var m = Market.Current;
        if (m == null || _cash == null) return;

        _clock.Text = L($"第 {m.Game} 局 · {WinChart.Clock(_now)}", $"Game {m.Game} · {WinChart.Clock(_now)}");
        double posVal = m.PositionsValue(P), eq = m.Cash + posVal, pnl = eq - m.In;
        _cash.Text = Money(m.Cash);
        _posVal.Text = Money(posVal);
        _equity.Text = Money(eq);
        string pct = (m.In > 0 ? pnl / m.In * 100.0 : 0.0).ToString("+0.0;-0.0;0.0", Inv);
        _pnl.Text = L($"{SignedMoney(pnl)}（{pct}%）", $"{SignedMoney(pnl)} ({pct}%)");
        Col(_pnl, pnl > 0.004 ? GoodCol : pnl < -0.004 ? BadCol : TextCol);

        // 下单
        double? amt = TicketAmount(out string err);
        if (_levShown != _lev)
        {
            _levShown = _lev;
            for (int i = 0; i < _levBtns.Length; i++)
                SetButtonColor(_levBtns[i], Market.Leverages[i] == _lev ? BtnBlue : BtnGrey, 6f, 2f);
        }
        bool draw = amt != null && m.UseProfit && amt.Value > m.Cash + 1e-9;       // 这一单要借浮盈
        if (amt == null) _hint.Text = err;
        else if (draw)
            _hint.Text = L($"现金 {Money(m.Cash)} + 借浮盈 {Money(amt.Value - m.Cash)} · 被借的持仓强平价会上移",
                           $"{Money(m.Cash)} cash + {Money(amt.Value - m.Cash)} profit · raises those liq. prices");
        else if (_lev == 1) _hint.Text = L($"每笔 {Money(amt.Value)} · 不加杠杆：最多亏掉这笔投入", $"{Money(amt.Value)} per order · no leverage: max loss is the stake");
        else
        {
            string a = Money(amt.Value), b = Money(amt.Value * _lev), x = (100.0 / _lev).ToString("0.#", Inv);
            _hint.Text = L($"{a} × {_lev} = {b} 仓位 · 价格反向跌 {x}% 就强平", $"{a} × {_lev} = {b} · liquidated if the price falls {x}%");
        }
        Col(_hint, amt == null ? BadCol : draw || _lev >= 5 ? MoneyCol : DimCol);

        for (int t = 0; t < Main.TeamCount; t++)
        {
            bool dead = Dead[t];
            bool win = _over && t == _winner;
            _pct[t].Text = dead ? L("淘汰", "Out") : win ? L("获胜", "Won") : $"{P[t] * 100.0:0.0}%";
            Col(_pct[t], dead ? DimCol : TextCol);
            Col(_name[t], dead ? new Color(RightArena.RingColors[t], 0.4f) : RightArena.RingColors[t]);
            double c = dead || _over ? 0.0 : Change(t, 60f);
            _chg[t].Text = Math.Abs(c) >= 0.05 ? $"{(c > 0 ? "▲" : "▼")}{Math.Abs(c):0.0}" : "";
            Col(_chg[t], c > 0 ? GoodCol : BadCol);
            bool can = !_over && !dead && amt != null;
            _longBtn[t].Text = dead || _over ? "—" : $"{L("做多", "Long")} {Market.Cents(m.Ask(t, P[t], true))}";
            _shortBtn[t].Text = dead || _over ? "—" : $"{L("做空", "Short")} {Market.Cents(m.Ask(t, P[t], false))}";
            _longBtn[t].Disabled = _shortBtn[t].Disabled = !can;
        }

        // 胜率构成
        for (int t = 0; t < Main.TeamCount; t++)
            for (int g = 0; g < WinModel.GroupCount; g++)
            {
                var c = _effCells[t, g];
                double e = _eff[t, g] * 100.0;
                c.Text = Dead[t] ? "—" : Math.Abs(e) < 0.05 ? "0.0" : e.ToString("+0.0;-0.0", Inv);
                Col(c, Dead[t] || Math.Abs(e) < 0.5 ? DimCol : e > 0.0 ? GoodCol : BadCol);
            }

        RefreshPositions(m);

        // 成交记录（新的在上面）
        for (int i = 0; i < _fills.Length; i++)
        {
            int k = m.Fills.Count - 1 - i;
            if (k < 0)
            {
                _fills[i].Text = "";
                continue;
            }
            var f = m.Fills[k];
            string when = f.Game == m.Game ? WinChart.Clock(f.Time) : L($"第{f.Game}局", $"G{f.Game}");
            _fills[i].Text = $"{when}  {f.Text}{(f.Closing ? "  " + SignedMoney(f.Pnl) : "")}";
            Col(_fills[i], !f.Closing ? DimCol : f.Pnl > 0.004 ? GoodCol : f.Pnl < -0.004 ? BadCol : TextCol);
        }

        if (_flash != null)
        {
            _status.Text = _flash;
            Col(_status, _flashCol);
        }
        else
        {
            _status.Text = _over
                ? L($"{Team(_winner)} 获胜，持仓已结算 · 稍后自动下一局（R 立即开始）· F2 回主菜单",
                    $"{Team(_winner)} won, positions settled · next game starts soon (R: now) · F2: main menu")
                : L("F2 回主菜单 · R 重开 · 持仓都按现价平掉", "F2: main menu · R: restart · open positions close at the current price");
            Col(_status, DimCol);
        }
    }

    private void RefreshPositions(Market m)
    {
        bool same = _rows.Count == m.Positions.Count;
        for (int i = 0; same && i < _rows.Count; i++) same = _rows[i].Pos == m.Positions[i];
        if (!same)
        {
            foreach (Node c in _posList.GetChildren())
                if (c != _noPos) c.QueueFree();
            _rows.Clear();
            foreach (var pos in m.Positions) _rows.Add(AddRow(pos));
        }
        _noPos.Visible = m.Positions.Count == 0;
        int n = m.Positions.Count;
        _posHeader.Text = n > 0 ? L($"持仓 {n} 笔", $"Positions ({n})") : L("持仓", "Positions");
        _closeAll.Disabled = n == 0;

        foreach (var row in _rows)
        {
            // 投入按 Stake 显示（借走的浮盈不减）；盈亏 = 价值 − 剩下的 Margin，也就是连借出去的那部分一起算
            var pos = row.Pos;
            double p = P[pos.Team], value = m.Value(pos, p), pnl = value - pos.Margin, liq = pos.LiqPrice;
            row.Price.Text = $"{Money(pos.Stake)} @ {Market.Cents(pos.Entry)} → {Market.Cents(m.Bid(pos, p))}";
            row.Pnl.Text = $"{SignedMoney(pnl)} {pnl / pos.Stake * 100.0:+0;-0;0}%";
            row.Value.Text = L("价值 ", "Value ") + Money(value);
            row.Drawn.Visible = pos.Drawn;          // 借出的浮盈单独一行：和价值挤一行太宽，会把右边一列撑出屏幕
            if (pos.Drawn) row.Drawn.Text = L($"借出 {Money(pos.Stake - pos.Margin)}", $"{Money(pos.Stake - pos.Margin)} drawn");
            row.Liq.Text = liq > 0.0 ? L($"强平 {Market.Cents(liq)}", $"Liq. {Market.Cents(liq)}") : "";     // 合并、借浮盈以后会变
            Col(row.Pnl, pnl > 0.004 ? GoodCol : pnl < -0.004 ? BadCol : TextCol);
        }
    }

    private PosRow AddRow(Market.Position pos)
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", Box(new Color(0.06f, 0.095f, 0.18f), default, 8, 10f, 5f));
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 10);
        panel.AddChild(h);

        // 左：哪一笔、开仓价 → 现价
        var info = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        info.AddThemeConstantOverride("separation", 0);
        var l1 = new HBoxContainer();
        l1.AddThemeConstantOverride("separation", 10);
        l1.AddChild(Small(PosName(pos), 18, RightArena.RingColors[pos.Team], true));
        // 强平价、价格这两行太长（金额很大）就截掉末尾，不把整列撑宽（中间的盈亏、价值不截）
        var liq = Small("", 15, new Color(BadCol, 0.85f));        // 强平价（RefreshPositions 里填）
        liq.ClipText = true;
        liq.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        liq.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        l1.AddChild(liq);
        info.AddChild(l1);
        var price = Small("", 15, DimCol);
        price.ClipText = true;
        price.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        info.AddChild(price);
        h.AddChild(info);

        // 中：盈亏、价值
        var mid = new VBoxContainer();
        mid.AddThemeConstantOverride("separation", 0);
        var pnl = Small("", 18, TextCol, true);
        pnl.HorizontalAlignment = HorizontalAlignment.Right;
        mid.AddChild(pnl);
        var value = Small("", 15, DimCol);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        mid.AddChild(value);
        var drawn = Small("", 14, MoneyCol);                      // 借出的浮盈（开了浮盈加仓、这笔被借过才显示）
        drawn.HorizontalAlignment = HorizontalAlignment.Right;
        drawn.Visible = false;
        mid.AddChild(drawn);
        h.AddChild(mid);

        var close = SmallButton(L("平仓", "Close"), BtnGrey, 64f, 40f, 18);
        close.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        close.Pressed += () => ClosePosition(pos);
        h.AddChild(close);

        _posList.AddChild(panel);
        return new PosRow { Pos = pos, Liq = liq, Price = price, Pnl = pnl, Value = value, Drawn = drawn };
    }
}
