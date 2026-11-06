using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// 资金模式的账户：场内现金、持仓、成交记录。像 Polymarket：每个阵营一个「会不会赢」的合约，价格 = 获胜概率（1¢~99¢），
/// 结束时赢家每股 $1、其余 $0。
///   做多 = 按 p 买「赢」；做空（先卖后买）= 按 1 − p 买「输」，押它的胜率往下走。
///   买卖价差随机 0.1¢~0.5¢（每个阵营各自的，每次更新价格重新抽，Spread）：买价 = 这一边的价格 + 半个价差，卖价 = − 半个价差。
///   不然盯着左边快进槽 / 快撞杯的弹珠抢跑，按同一个价买卖能稳赚一点。
///   一笔单：投入 Margin、杠杆 Lev，股数 = Margin × Lev / 开仓价（买价）；价值 = Margin + 股数 × (卖价 − 开仓价)，最低 0（亏损不超过投入）。
///   亏到价值 0（卖价跌到开仓价 × (1 − 1/Lev)）就强制平仓；不加杠杆只有结算到 0 才会归零。
///   阵营被淘汰：它的做多按 0、做空按 $1 结算；分出胜负：赢家做多按 $1、做空按 0，其余反过来（结算没有价差）。
/// 没有手续费：价差 0.3¢ 时 25¢ 买入（付 25.15¢）$10、50¢ 卖出（得 49.85¢）≈ 赚 $9.82。持仓只在一局里，现金跨局保留（Current 在资金模式期间一直在）。
/// 两个可选的交易选项（游戏设置里开，进资金模式时定下来）：
///   浮盈加仓 UseProfit：现金不够时，从赚钱的持仓里按比例借出浮盈（价值 − 一共投进去的 Stake）来开新仓。
///     借出 x：那笔的 Margin 减 x、现金加 x，价值也少 x；强平价变成 Entry − Margin / 股数（往上移，Margin 可以是负的），盈亏 = 价值 − Margin 不变。
///   合并仓位 Merge：同一阵营、同一方向、同一杠杆的新单并进原来那笔：股数相加，开仓价按股数加权成均价，Margin / Stake 相加（价值正好是两笔之和）。
/// </summary>
public sealed class Market
{
    public sealed class Position
    {
        public int Id, Team, Lev;
        public bool Long;              // true = 做多（押赢），false = 做空（押输）
        public double Margin;          // 投入的钱（浮盈加仓借走浮盈后会变少，可以是负的）
        public double Stake;           // 一共投进去的钱（借浮盈不减；能借的浮盈 = 价值 − Stake）
        public double Entry;           // 开仓价（这一边的价格：做多 = p，做空 = 1 − p；合并后是均价）
        public double Shares;          // 股数 = Margin × Lev / Entry
        public float Time;             // 开仓时的模拟时间

        public double SidePrice(double p) => Long ? p : 1.0 - p;

        /// <summary>卖价 = 这一边的价格 − 半个价差 half（到期结算 half = 0）。</summary>
        public double Bid(double p, double half) => Math.Max(0.0, SidePrice(p) - half);

        /// <summary>按阵营胜率 p 的卖价算的价值（≥ 0），就是现在平掉能拿回的钱。</summary>
        public double Value(double p, double half) => Math.Max(0.0, Margin + Shares * (Bid(p, half) - Entry));

        /// <summary>借走过浮盈（Margin 比一共投进去的少）。</summary>
        public bool Drawn => Margin < Stake - 1e-9;

        /// <summary>强平价（这一边的卖价跌到这里价值归零）；不加杠杆是 0。借走过浮盈的：Entry − Margin / 股数（不加杠杆也会强平）。</summary>
        public double LiqPrice => !Drawn ? (Lev <= 1 ? 0.0 : Entry * (1.0 - 1.0 / Lev)) : Math.Max(0.0, Entry - Margin / Shares);
    }

    public sealed class Fill
    {
        public int Game;
        public float Time;
        public string Text;
        public double Pnl;             // 平仓 / 结算的盈亏；开仓是 0
        public bool Closing;
    }

    public static readonly int[] Leverages = { 1, 2, 3, 5, 10 };
    public const double MinPrice = 0.01, MaxPrice = 0.99;
    public const double MinSpread = 0.001, MaxSpread = 0.005;     // 买卖价差在 0.1¢~0.5¢ 之间随机
    public const int MaxFills = 60;

    /// <summary>资金模式里的账户（进资金模式时建，回主菜单结算后清掉）。</summary>
    public static Market Current;

    public double In;                  // 带进来的本金
    public double Cash;                // 场内现金
    public double Realized;            // 已实现盈亏（平仓 + 结算 + 强平）
    public int Game;                   // 第几局（1 起）
    public int Trades, Liquidations;
    public readonly bool UseProfit, Merge;        // 交易选项（进场时按游戏设置定下来）：浮盈加仓 / 合并仓位
    public double LastDrawn;                      // 上一次 Open 借了多少浮盈
    public bool LastMerged;                       // 上一次 Open 是并进了已有的那笔
    public readonly List<Position> Positions = new();
    public readonly List<Fill> Fills = new();     // 新的在后面
    public readonly double[] Spread = new double[Main.TeamCount];   // 每个阵营现在的买卖价差（RollSpreads 换）
    private readonly Random _rng = new();
    private int _nextId = 1;

    public Market(double amount)
    {
        In = Cash = Wallet.Cents(amount);
        UseProfit = Main.Settings.FundsUseProfit;
        Merge = Main.Settings.FundsMerge;
        RollSpreads();
    }

    /// <summary>每个阵营重新抽一个价差（0.1¢~0.5¢ 均匀）；TradePanel 每次更新价格时调。</summary>
    public void RollSpreads()
    {
        for (int t = 0; t < Spread.Length; t++) Spread[t] = MinSpread + (MaxSpread - MinSpread) * _rng.NextDouble();
    }

    public double Half(int team) => 0.5 * Spread[team];

    public static string Cents(double price) => (price * 100.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "¢";

    public static string SideName(bool isLong) => isLong ? "做多" : "做空";
    public static string SideName(bool isLong, bool en) => en ? (isLong ? "Long" : "Short") : SideName(isLong);

    /// <summary>「红做多 3×」/「Red Long 3×」。</summary>
    public static string Label(Position pos, bool en) =>
        $"{Loc.Team(pos.Team, en)}{(en ? " " : "")}{SideName(pos.Long, en)}{(pos.Lev > 1 ? $" {pos.Lev}×" : "")}";

    /// <summary>记一条成交：界面上按当前语言显示，[market] 日志一直是中文。</summary>
    private void AddFill(float time, string zh, string en, double pnl, bool closing)
    {
        Fills.Add(new Fill { Game = Game, Time = time, Text = Loc.En ? en : zh, Pnl = pnl, Closing = closing });
        if (Fills.Count > MaxFills) Fills.RemoveAt(0);
        GD.Print($"[market] g{Game} t={time:0.0} {zh}{(closing ? $" pnl={pnl:+0.00;-0.00}" : "")} cash={Cash:0.00}");
    }

    /// <summary>这一边现在的买价（阵营胜率 p）。</summary>
    public double Ask(int team, double p, bool isLong) => (isLong ? p : 1.0 - p) + Half(team);

    /// <summary>这一笔现在的卖价 / 按卖价算的价值。</summary>
    public double Bid(Position pos, double p) => pos.Bid(p, Half(pos.Team));
    public double Value(Position pos, double p) => pos.Value(p, Half(pos.Team));

    /// <summary>这个杠杆开仓后马上就会被价差强平（卖价 ≤ 强平价）。</summary>
    public bool InstantLiq(int team, double p, bool isLong, int lev) =>
        lev > 1 && Ask(team, p, isLong) - Spread[team] <= Ask(team, p, isLong) * (1.0 - 1.0 / lev) + 1e-9;

    /// <summary>这一笔现在能借出来的浮盈：按卖价算的价值 − 一共投进去的钱，取整到分，≥ 0。</summary>
    public double Drawable(Position pos, double p) =>
        Math.Max(0.0, Math.Floor((Value(pos, p) - pos.Stake) * 100.0 + 1e-6) / 100.0);

    /// <summary>能用来下单的钱：现金；开了浮盈加仓再加上所有持仓能借出的浮盈。p = 各阵营胜率。</summary>
    public double BuyingPower(double[] p)
    {
        double v = Cash;
        if (UseProfit)
            foreach (var pos in Positions) v += Drawable(pos, p[pos.Team]);
        return Wallet.Cents(v);
    }

    /// <summary>浮盈加仓：从能借浮盈的持仓里按各自可借的比例借出 need（整分），加到现金，返回借了多少。</summary>
    private double DrawProfit(double need, double[] p)
    {
        var rows = new List<(Position pos, long c)>();
        long total = 0;
        foreach (var pos in Positions)
        {
            long c = (long)Math.Round(Drawable(pos, p[pos.Team]) * 100.0);
            if (c > 0) { rows.Add((pos, c)); total += c; }
        }
        long n = Math.Min((long)Math.Round(need * 100.0), total), left = n;
        if (n <= 0) return 0.0;
        var take = new long[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            take[i] = (long)((Int128)n * rows[i].c / total);
            left -= take[i];
        }
        for (int i = 0; i < rows.Count && left > 0; i++)        // 取整剩下的几分，给还有余量的
        {
            long x = Math.Min(left, rows[i].c - take[i]);
            take[i] += x;
            left -= x;
        }
        for (int i = 0; i < rows.Count; i++)
        {
            if (take[i] <= 0) continue;
            rows[i].pos.Margin = Wallet.Cents(rows[i].pos.Margin - take[i] / 100.0);
            Cash = Wallet.Cents(Cash + take[i] / 100.0);
        }
        return n / 100.0;
    }

    /// <summary>下单：按阵营胜率 p[team] 的买价开一笔。钱不够（开了浮盈加仓算上能借的浮盈）、价格不在 1¢~99¢ 返回 null。
    /// 开了合并仓位：同一阵营、同一方向、同一杠杆已经有一笔就并进去（返回那一笔）。</summary>
    public Position Open(int team, bool isLong, double margin, int lev, double[] p, float time)
    {
        margin = Wallet.Cents(margin);
        double pt = p[team], mid = isLong ? pt : 1.0 - pt, price = Ask(team, pt, isLong);
        if (margin <= 0.0 || margin > BuyingPower(p) + 1e-9 || mid < MinPrice - 1e-9 || mid > MaxPrice + 1e-9 || InstantLiq(team, pt, isLong, lev)) return null;
        lev = Math.Clamp(lev, 1, Leverages[^1]);
        double drawn = margin > Cash + 1e-9 ? DrawProfit(margin - Cash, p) : 0.0;
        double shares = margin * lev / price;
        Cash = Math.Max(0.0, Wallet.Cents(Cash - margin));
        Trades++;
        string zhUse = drawn > 0.0 ? $"（用浮盈 {Ui.Money(drawn)}）" : "", enUse = drawn > 0.0 ? $" ({Ui.Money(drawn)} from profit)" : "";
        var pos = Merge ? Positions.Find(q => q.Team == team && q.Long == isLong && q.Lev == lev) : null;
        LastDrawn = drawn;
        LastMerged = pos != null;
        if (pos != null)
        {
            // 合并：价值 = Margin + 股数 × (卖价 − 开仓价) 两笔相加正好是这样
            pos.Entry = (pos.Shares * pos.Entry + shares * price) / (pos.Shares + shares);
            pos.Shares += shares;
            pos.Margin = Wallet.Cents(pos.Margin + margin);
            pos.Stake = Wallet.Cents(pos.Stake + margin);
            AddFill(time, $"加仓 {Label(pos, false)} {Ui.Money(margin)} @ {Cents(price)} → 均价 {Cents(pos.Entry)}{zhUse}",
                $"Add {Label(pos, true)} {Ui.Money(margin)} @ {Cents(price)} → avg {Cents(pos.Entry)}{enUse}", 0.0, false);
            return pos;
        }
        pos = new Position
        {
            Id = _nextId++, Team = team, Long = isLong, Lev = lev, Margin = margin, Stake = margin, Entry = price,
            Shares = shares, Time = time,
        };
        Positions.Add(pos);
        AddFill(time, $"买入 {Label(pos, false)} {Ui.Money(margin)} @ {Cents(price)}{zhUse}",
            $"Buy {Label(pos, true)} {Ui.Money(margin)} @ {Cents(price)}{enUse}", 0.0, false);
        return pos;
    }

    /// <summary>按阵营胜率 p 的卖价平掉一笔，钱回到现金，返回盈亏。why：平仓 / 强平 / 结算 / 退出时平仓。settle = 到期结算（p = 1 / 0，不扣价差）。</summary>
    public double Close(Position pos, double p, float time, string why, bool settle = false)
    {
        if (!Positions.Remove(pos)) return 0.0;
        double half = settle ? 0.0 : Half(pos.Team);
        double value = Wallet.Cents(pos.Value(p, half));
        double pnl = Wallet.Cents(value - pos.Margin);
        Cash = Wallet.Cents(Cash + value);
        Realized = Wallet.Cents(Realized + pnl);
        if (why == "强平") Liquidations++;
        string bid = Cents(pos.Bid(p, half));
        string zhIn = pos.Drawn ? $"{Ui.Money(pos.Stake)} − 借出 {Ui.Money(pos.Stake - pos.Margin)}" : Ui.Money(pos.Margin);
        string enIn = pos.Drawn ? $"{Ui.Money(pos.Stake)} − {Ui.Money(pos.Stake - pos.Margin)} drawn" : Ui.Money(pos.Margin);
        AddFill(time, $"{why} {Label(pos, false)} @ {bid}（{zhIn} → {Ui.Money(value)}）",
            $"{Loc.Term(why, true)} {Label(pos, true)} @ {bid} ({enIn} → {Ui.Money(value)})", pnl, true);
        return pnl;
    }

    /// <summary>这一边的价格跌到强平价的单子全部强平（价值归零），返回强平掉的单子（没有返回 null）。</summary>
    public List<Position> Liquidate(double[] p, float time)
    {
        List<Position> hit = null;
        for (int i = Positions.Count - 1; i >= 0; i--)
        {
            var pos = Positions[i];
            if (pos.LiqPrice > 0.0 && Bid(pos, p[pos.Team]) <= pos.LiqPrice + 1e-9)
            {
                Close(pos, p[pos.Team], time, "强平");
                (hit ??= new List<Position>()).Add(pos);
            }
        }
        return hit;
    }

    /// <summary>阵营 team 的合约到期：final = 1（赢了）或 0（输了 / 被淘汰）。</summary>
    public void Settle(int team, double final, float time, string why)
    {
        for (int i = Positions.Count - 1; i >= 0; i--)
            if (Positions[i].Team == team) Close(Positions[i], final, time, why, true);
    }

    /// <summary>全部按现价平掉（重开、回主菜单、关窗口）。</summary>
    public void CloseAll(double[] p, float time, string why)
    {
        for (int i = Positions.Count - 1; i >= 0; i--) Close(Positions[i], p[Positions[i].Team], time, why);
    }

    public double PositionsValue(double[] p)
    {
        double v = 0.0;
        foreach (var pos in Positions) v += Value(pos, p[pos.Team]);
        return v;
    }

    /// <summary>总值 = 现金 + 持仓按现价。</summary>
    public double Equity(double[] p) => Cash + PositionsValue(p);
}
