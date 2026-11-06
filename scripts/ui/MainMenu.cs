using Godot;
using System;
using static Ui;
using static Loc;

/// <summary>
/// 主菜单：显示当前资金；正常模式 / 游戏设置 / 资金模式 / 补充资金 / 退出。
/// 点「资金模式」先在同一屏选带多少钱进去，再进场（Main.StartFunds）。
/// 程序启动时先到这里（调试参数要加 --menu）；游戏里按 F2 回到这里（资金模式会先结算出场）。
/// </summary>
public partial class MainMenu : CanvasLayer
{
    public Action OnNormal, OnSettings, OnQuiz, OnQuit;
    public Action<double> OnFunds;

    /// <summary>回到主菜单时显示一次的话（资金模式结算结果等），显示后清掉。</summary>
    public static string Message;

    private Control _home, _funds;
    private Button _normalBtn, _fundsGo;
    private LineEdit _amount;
    private Label _fundsBalance, _fundsHint;

    public override void _Ready()
    {
        Layer = 10;
        var box = Screen(this, 900f);

        box.AddChild(MakeLabel("Ball Territory War", Fonts.UiBold, 46, TextCol, HorizontalAlignment.Center));
        box.AddChild(MakeLabel(L("当前资金", "Balance"), Fonts.Ui, 22, DimCol, HorizontalAlignment.Center));
        box.AddChild(MakeLabel(Money(Wallet.Balance), Fonts.UiBold, 60, MoneyCol, HorizontalAlignment.Center));

        string msg = Wallet.Notice?.Invoke();
        if (!string.IsNullOrEmpty(Message)) msg = string.IsNullOrEmpty(msg) ? Message : msg + "\n" + Message;
        Wallet.Notice = null;
        Message = null;
        if (!string.IsNullOrEmpty(msg))
        {
            var note = new PanelContainer();
            note.AddThemeStyleboxOverride("panel", Box(new Color(0.06f, 0.12f, 0.22f), new Color(0.35f, 0.55f, 0.9f, 0.6f), 10, 18f, 10f));
            note.AddChild(MakeText(msg, 21, TextCol, HorizontalAlignment.Center));
            box.AddChild(note);
        }
        box.AddChild(Gap(6));

        _home = BuildHome();
        box.AddChild(_home);
        _funds = BuildFunds();
        _funds.Visible = false;
        box.AddChild(_funds);

        box.AddChild(Gap(4));
        box.AddChild(MakeLabel(L("游戏中：F2 回主菜单 · R 重开 · F11 全屏", "In game: F2 = main menu · R = restart · F11 = fullscreen"),
                               Fonts.Ui, 19, DimCol, HorizontalAlignment.Center));

        _normalBtn.CallDeferred(Control.MethodName.GrabFocus);
    }

    private Control BuildHome()
    {
        var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        grid.AddThemeConstantOverride("h_separation", 26);
        grid.AddThemeConstantOverride("v_separation", 14);

        void Row(Button b, string desc)
        {
            grid.AddChild(b);
            grid.AddChild(MakeLabel(desc, Fonts.Ui, 21, DimCol));
        }

        _normalBtn = MakeButton(L("正常模式", "Normal Mode"), BtnBlue, 300f, 64f, 28);
        _normalBtn.Pressed += () => Leave(OnNormal);
        Row(_normalBtn, L("看四个阵营对战，不花钱", "Watch the four teams battle, no money involved"));

        var settings = MakeButton(L("游戏设置", "Settings"), BtnGrey, 300f, 64f, 28);
        settings.Pressed += () => Leave(OnSettings);
        Row(settings, L("初始护盾、机枪火力、落槽概率、交易选项、语言", "Start values, slot odds, trading options, language"));

        var funds = MakeButton(L("资金模式", "Trading Mode"), BtnGreen, 300f, 64f, 28);
        funds.Pressed += ShowFunds;
        Row(funds, L("实时胜率 · 做多 / 做空 · 可加杠杆", "Live win odds · long / short · optional leverage"));

        var quiz = MakeButton(L("补充资金", "Add Funds"), new Color(0.55f, 0.40f, 0.08f), 300f, 64f, 28);
        quiz.Pressed += () => Leave(OnQuiz);
        Row(quiz, L($"答题：每答对 {Wallet.QuizBatch} 题 +{Money(Wallet.QuizReward)}",
                    $"Quiz: +{Money(Wallet.QuizReward)} for every {Wallet.QuizBatch} correct answers"));

        var quit = MakeButton(L("退出", "Quit"), BtnGrey, 300f, 64f, 28);
        quit.Pressed += () => Leave(OnQuit);
        int n = Wallet.FundsSessions;
        string stats = n > 0 ? L($"资金模式累计盈亏 {SignedMoney(Wallet.FundsPnl)}（{n} 次）",
                                 $"Trading mode P&L so far {SignedMoney(Wallet.FundsPnl)} ({n} session{(n == 1 ? "" : "s")})") : "";
        Row(quit, stats);
        return grid;
    }

    private Control BuildFunds()
    {
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 12);
        v.AddChild(MakeLabel(L("资金模式", "Trading Mode"), Fonts.UiBold, 34, GoodCol, HorizontalAlignment.Center));
        _fundsBalance = MakeLabel("", Fonts.Ui, 22, TextCol, HorizontalAlignment.Center);
        v.AddChild(_fundsBalance);

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 12);
        row.AddChild(MakeLabel(L("带进去", "Bring in"), Fonts.Ui, 24, TextCol));
        _amount = new LineEdit { CustomMinimumSize = new Vector2(190f, 50f), Alignment = HorizontalAlignment.Center };
        _amount.AddThemeFontOverride("font", Fonts.UiBold);
        _amount.AddThemeFontSizeOverride("font_size", 26);
        _amount.TextChanged += _ => CheckAmount();
        _amount.TextSubmitted += _ => TryEnter();
        row.AddChild(_amount);
        foreach (var (label, frac) in new (string, double)[] { ("$10", -10), ("$50", -50), (L("一半", "Half"), 0.5), (L("全部", "All"), 1.0) })
        {
            var b = MakeButton(label, BtnGrey, 92f, 50f, 22);
            SetButtonColor(b, BtnGrey, 10f, 6f);
            double f = frac;
            b.Pressed += () =>
            {
                double a = f < 0 ? Math.Min(-f, Wallet.Balance) : Math.Floor(Wallet.Balance * f * 100.0) / 100.0;
                _amount.Text = FormatMoneyInput(a);
                CheckAmount();
            };
            row.AddChild(b);
        }
        v.AddChild(row);

        _fundsHint = MakeLabel("", Fonts.Ui, 20, DimCol, HorizontalAlignment.Center);
        v.AddChild(_fundsHint);

        var s = Main.Settings;
        string on = L("开", "On"), off = L("关", "Off");
        var rules = new PanelContainer();
        rules.AddThemeStyleboxOverride("panel", Box(new Color(0.045f, 0.075f, 0.14f), default, 10, 20f, 12f));
        rules.AddChild(MakeText(L(
            "· 每个阵营一个合约，价格 = 实时胜率（1¢～99¢），赢家每股结算 $1，其余 $0\n" +
            "· 做多：押它赢，胜率涨了平仓赚差价（25¢ 买 $10、50¢ 卖出 ≈ 赚 $9.8）\n" +
            "· 买卖价差随机 0.1¢～0.5¢：买价 = 胜率 + 半个价差，卖价 = 胜率 − 半个价差（到期结算没有价差）\n" +
            "· 做空（先卖后买）：押它输，按 1 − 胜率买入，胜率跌了赚\n" +
            "· 每次下单自己定金额，可加 2×～10× 杠杆：盈亏跟着放大，亏光这笔投入就强制平仓\n" +
            "· 阵营被淘汰、分出胜负时自动结算；按 R 重开时持仓按现价平掉\n" +
            "· F2 回主菜单：持仓按现价平掉，场内的钱全部回到余额\n" +
            $"· 交易选项（在「游戏设置」里改）：浮盈加仓 {(s.FundsUseProfit ? on : off)} · 合并仓位 {(s.FundsMerge ? on : off)}",
            "· One contract per team, priced at its live win chance (1¢–99¢); winner settles at $1, others at $0\n" +
            "· Long: bet that it wins; sell after the chance rises ($10 bought at 25¢, sold at 50¢ ≈ +$9.8)\n" +
            "· Random 0.1¢–0.5¢ spread: ask = chance + half of it, bid = chance − half (settlement has none)\n" +
            "· Short (sell first, buy back later): bet that it loses; you buy at 1 − chance and gain when it falls\n" +
            "· Set each order's amount; 2×–10× leverage scales gains and losses, and losing the stake liquidates it\n" +
            "· Positions settle when a team is out or the game is won; R (restart) closes them at the current price\n" +
            "· F2 back to the menu: positions close at the current price and all money in play returns to the balance\n" +
            $"· Trading options (change them in Settings): Use profit {(s.FundsUseProfit ? on : off)} · Merge positions {(s.FundsMerge ? on : off)}"),
            19, DimCol));
        v.AddChild(rules);

        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        buttons.AddThemeConstantOverride("separation", 32);
        var back = MakeButton(L("返回", "Back"), BtnGrey);
        back.Pressed += ShowHome;
        buttons.AddChild(back);
        _fundsGo = MakeButton(L("进入资金模式", "Start Trading"), BtnGreen, 280f);
        _fundsGo.Pressed += TryEnter;
        buttons.AddChild(_fundsGo);
        v.AddChild(buttons);
        return v;
    }

    private static string FormatMoneyInput(double a) =>
        a.ToString(Math.Abs(a - Math.Round(a)) < 0.005 ? "0" : "0.00", System.Globalization.CultureInfo.InvariantCulture);

    private void ShowFunds()
    {
        _home.Visible = false;
        _funds.Visible = true;
        _fundsBalance.Text = L("余额 " + Money(Wallet.Balance) + " · 带进去的钱在场内交易，出场时全部回到余额",
                               "Balance " + Money(Wallet.Balance) + " · the money you bring in is traded in play and all of it returns when you leave");
        _amount.Text = FormatMoneyInput(Math.Min(Wallet.Balance, 50.0));
        CheckAmount();
        _amount.GrabFocus();
        _amount.CaretColumn = _amount.Text.Length;
    }

    private void ShowHome()
    {
        _funds.Visible = false;
        _home.Visible = true;
        _normalBtn.GrabFocus();
    }

    /// <summary>检查输入的金额，返回能用的数（不能用返回 null，并在提示里说为什么）。</summary>
    private double? CheckAmount()
    {
        double? a = ParseMoney(_amount.Text);
        string err = null;
        if (Wallet.Balance < 0.01) err = L("余额是 $0，先去「补充资金」答题赚点钱吧", "Your balance is $0: earn some in Add Funds (quiz) first");
        else if (a == null) err = L("金额看不懂：填数字，比如 25 或 12.5", "Can't read the amount: enter a number such as 25 or 12.5");
        else if (a.Value < 0.01) err = L("至少带 $0.01", "Bring at least $0.01");
        else if (Wallet.Cents(a.Value) > Wallet.Balance + 1e-9) err = L($"余额只有 {Money(Wallet.Balance)}", $"Your balance is only {Money(Wallet.Balance)}");
        _fundsHint.Text = err ?? L($"带进去 {Money(Wallet.Cents(a.Value))}，余额剩 {Money(Wallet.Balance - Wallet.Cents(a.Value))}",
                                   $"Bring in {Money(Wallet.Cents(a.Value))}, leaving {Money(Wallet.Balance - Wallet.Cents(a.Value))} in the balance");
        SetColor(_fundsHint, err == null ? DimCol : BadCol);
        _fundsGo.Disabled = err != null;
        return err == null ? Wallet.Cents(a.Value) : null;
    }

    private void TryEnter()
    {
        double? a = CheckAmount();
        if (a == null) return;
        QueueFree();
        OnFunds?.Invoke(a.Value);
    }

    private void Leave(Action next)
    {
        QueueFree();
        next?.Invoke();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey k && k.Pressed && !k.Echo && k.Keycode == Key.Escape && _funds.Visible)
        {
            GetViewport().SetInputAsHandled();
            ShowHome();
        }
    }
}
