using Godot;
using System;

/// <summary>
/// 钱包：玩家的资金（美元）、答题进度、进行中的资金模式。存在 user://wallet.cfg（和 settings.cfg 分开），第一次打开给 StartBalance。
/// 资金模式：进场时从余额里拿出一笔当本金（StartSession），出场（F2 回主菜单 / 关窗口）时持仓按现价平掉，场内现金全部回到余额（EndSession）。
/// 游戏没正常关（崩溃、强杀）：下次打开按最后记下的场内总值（NoteSession）退回余额。
/// 调试运行（带调试参数）不读也不存，余额 = StartBalance 或 --wallet=金额。
/// </summary>
public static class Wallet
{
    public const double StartBalance = 100.0;
    public const int QuizBatch = 5;            // 每答对 5 题……
    public const double QuizReward = 10.0;     // ……得 $10

    private const string CfgPath = "user://wallet.cfg";

    /// <summary>读写 wallet.cfg（Main 决定：正常启动 true，调试运行 false）。</summary>
    public static bool Persist;

    public static double Balance { get; private set; } = StartBalance;

    // 答题：题库按 QuizSeed 洗一次牌，按顺序出题，答完一题 QuizNext 才往后走（中途退出下次还是这题）
    public static int QuizSeed;
    public static int QuizNext;
    public static int QuizAnswered, QuizCorrect;    // 累计答过 / 答对（答对数每到 5 的倍数发一次奖）
    public static double QuizEarned;

    // 资金模式
    public static bool InSession { get; private set; }
    public static double SessionIn { get; private set; }       // 带进去的本金
    public static double SessionValue { get; private set; }    // 最近一次记下的场内总值（现金 + 持仓按现价）
    public static double FundsPnl { get; private set; }        // 资金模式累计盈亏（每次出场加一次）
    public static int FundsSessions { get; private set; }      // 进过几次资金模式

    /// <summary>启动时要告诉玩家的事（上次资金模式没正常退出、退回了多少），主菜单显示一次后清掉。
    /// 是个函数：读钱包时还没读设置里的语言，到显示时再按当时的语言拼字。</summary>
    public static Func<string> Notice;

    public static double Cents(double v) => Math.Round(v * 100.0, MidpointRounding.AwayFromZero) / 100.0;

    /// <summary>调试运行：不碰存档，余额直接设成这个数。</summary>
    public static void SetDebug(double balance)
    {
        Persist = false;
        Balance = Cents(Math.Max(0.0, balance));
    }

    public static void Load()
    {
        var cf = new ConfigFile();
        if (cf.Load(CfgPath) != Error.Ok)
        {
            Save();                                     // 第一次打开：给 StartBalance
            return;
        }
        Balance = Cents(Math.Max(0.0, cf.GetValue("wallet", "balance", StartBalance).AsDouble()));
        QuizSeed = cf.GetValue("quiz", "seed", 0).AsInt32();
        QuizNext = Math.Max(0, cf.GetValue("quiz", "next", 0).AsInt32());
        QuizAnswered = Math.Max(0, cf.GetValue("quiz", "answered", 0).AsInt32());
        QuizCorrect = Math.Max(0, cf.GetValue("quiz", "correct", 0).AsInt32());
        QuizEarned = Math.Max(0.0, cf.GetValue("quiz", "earned", 0.0).AsDouble());
        FundsPnl = cf.GetValue("funds", "pnl", 0.0).AsDouble();
        FundsSessions = Math.Max(0, cf.GetValue("funds", "sessions", 0).AsInt32());
        if (cf.GetValue("session", "active", false).AsBool())
        {
            double v = Cents(Math.Max(0.0, cf.GetValue("session", "value", 0.0).AsDouble()));
            double brought = cf.GetValue("session", "in", 0.0).AsDouble();
            Balance = Cents(Balance + v);
            FundsPnl = Cents(FundsPnl + v - brought);
            FundsSessions++;
            Notice = () => Loc.L($"上次资金模式没有正常退出：按最后记下的场内总值把 {Ui.Money(v)} 退回了余额（当时带进去 {Ui.Money(brought)}）",
                                 $"The last Trading session didn't end normally: its last recorded value, {Ui.Money(v)}, went back to the balance (you had brought in {Ui.Money(brought)})");
            GD.Print($"[wallet] recovered session value {v:0.00} (in {brought:0.00})");
            Save();
        }
    }

    public static void Save()
    {
        if (!Persist) return;
        var cf = new ConfigFile();
        cf.SetValue("wallet", "balance", Balance);
        cf.SetValue("quiz", "seed", QuizSeed);
        cf.SetValue("quiz", "next", QuizNext);
        cf.SetValue("quiz", "answered", QuizAnswered);
        cf.SetValue("quiz", "correct", QuizCorrect);
        cf.SetValue("quiz", "earned", QuizEarned);
        cf.SetValue("funds", "pnl", FundsPnl);
        cf.SetValue("funds", "sessions", FundsSessions);
        cf.SetValue("session", "active", InSession);
        cf.SetValue("session", "in", SessionIn);
        cf.SetValue("session", "value", SessionValue);
        Error err = cf.Save(CfgPath);
        if (err != Error.Ok) GD.PrintErr($"[wallet] save {CfgPath} failed: {err}");
    }

    /// <summary>进资金模式：从余额里拿出 amount 当本金。钱不够或已经在资金模式里返回 false。</summary>
    public static bool StartSession(double amount)
    {
        amount = Cents(amount);
        if (InSession || amount <= 0.0 || amount > Balance + 1e-9) return false;
        Balance = Cents(Balance - amount);
        InSession = true;
        SessionIn = SessionValue = amount;
        Save();
        GD.Print($"[wallet] session start {amount:0.00}, balance {Balance:0.00}");
        return true;
    }

    /// <summary>资金模式里定时记一下场内总值（崩溃了下次按这个退回）。</summary>
    public static void NoteSession(double value)
    {
        if (!InSession) return;
        value = Cents(Math.Max(0.0, value));
        if (Math.Abs(value - SessionValue) < 0.005) return;
        SessionValue = value;
        Save();
    }

    /// <summary>出资金模式：场内现金 cash 回到余额，返回这次的盈亏。</summary>
    public static double EndSession(double cash)
    {
        if (!InSession) return 0.0;
        cash = Cents(Math.Max(0.0, cash));
        double pnl = Cents(cash - SessionIn);
        Balance = Cents(Balance + cash);
        FundsPnl = Cents(FundsPnl + pnl);
        FundsSessions++;
        InSession = false;
        SessionIn = SessionValue = 0.0;
        Save();
        GD.Print($"[wallet] session end {cash:0.00} (pnl {pnl:+0.00;-0.00}), balance {Balance:0.00}");
        return pnl;
    }

    /// <summary>答完一题（下一题往后走一个）；答对数到了 5 的倍数就发 $10，返回这次发没发。</summary>
    public static bool QuizAnswer(bool correct)
    {
        QuizAnswered++;
        QuizNext++;
        bool paid = false;
        if (correct)
        {
            QuizCorrect++;
            if (QuizCorrect % QuizBatch == 0)
            {
                Balance = Cents(Balance + QuizReward);
                QuizEarned += QuizReward;
                paid = true;
            }
        }
        Save();
        return paid;
    }

    /// <summary>题库用完一遍（或第一次）：换个种子重新洗牌，从头出。</summary>
    public static void QuizReshuffle(int seed)
    {
        QuizSeed = seed;
        QuizNext = 0;
        Save();
    }
}
