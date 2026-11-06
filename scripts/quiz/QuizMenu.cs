using Godot;
using System;
using static Ui;
using static Loc;

/// <summary>
/// 补充资金：答题，累计每答对 5 题得 $10（Wallet.QuizAnswer 发钱、存档）。出题顺序和进度存在钱包里，中途退出下次还是这题。
/// 选完马上判：正确答案标绿、选错的标红，有解析就显示解析，然后「下一题」。
/// 键盘：1-4 / A-D 选，选完 Enter 下一题，Esc 返回主菜单。
/// </summary>
public partial class QuizMenu : CanvasLayer
{
    /// <summary>点「返回」之后调用：回主菜单。</summary>
    public Action OnDone;

    private static readonly Color OptCol = new Color(0.09f, 0.14f, 0.25f);
    private static readonly Color RightCol = new Color(0.10f, 0.47f, 0.28f);
    private static readonly Color WrongCol = new Color(0.60f, 0.16f, 0.20f);
    private static readonly Color OffCol = new Color(0.07f, 0.10f, 0.17f);

    private Label _balance, _dots, _progress, _tag, _question, _result, _reward, _explain;
    private readonly Button[] _opts = new Button[4];
    private Button _next;
    private QuestionBank.Question _q;
    private int _picked = -1;                       // 这题选了哪个（-1 = 还没选）

    public override void _Ready()
    {
        Layer = 10;
        var box = Screen(this, 1240f);

        box.AddChild(MakeLabel(L("补充资金 · 答题", "Add Funds · Quiz"), Fonts.UiBold, 44, TextCol, HorizontalAlignment.Center));
        box.AddChild(MakeLabel(L($"累计每答对 {Wallet.QuizBatch} 题得 {Money(Wallet.QuizReward)}，答错不扣钱",
                                 $"+{Money(Wallet.QuizReward)} for every {Wallet.QuizBatch} correct answers, wrong answers cost nothing (the questions are in Chinese)"),
                               Fonts.Ui, 21, DimCol, HorizontalAlignment.Center));

        var status = new HBoxContainer();
        status.AddThemeConstantOverride("separation", 18);
        _balance = MakeLabel("", Fonts.UiBold, 28, MoneyCol);
        status.AddChild(_balance);
        var fill = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        status.AddChild(fill);
        _dots = MakeLabel("", Fonts.Ui, 26, MoneyCol);
        status.AddChild(_dots);
        _progress = MakeLabel("", Fonts.Ui, 22, TextCol);
        status.AddChild(_progress);
        box.AddChild(status);

        var line = new ColorRect { Color = PanelBorder, CustomMinimumSize = new Vector2(0f, 2f) };
        box.AddChild(line);

        _tag = MakeLabel("", Fonts.Ui, 20, AccentCol);
        box.AddChild(_tag);
        _question = MakeText("", 28, TextCol);
        box.AddChild(_question);
        box.AddChild(Gap(4));

        for (int k = 0; k < 4; k++)
        {
            int kk = k;
            var b = new Button
            {
                Alignment = HorizontalAlignment.Left,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(0f, 54f),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                FocusMode = Control.FocusModeEnum.None,
            };
            b.AddThemeFontOverride("font", Fonts.Ui);
            foreach (string c in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color", "font_hover_pressed_color" })
                b.AddThemeColorOverride(c, TextCol);
            SetButtonColor(b, OptCol, 18f, 10f);
            b.Pressed += () => Pick(kk);
            _opts[k] = b;
            box.AddChild(b);
        }

        var res = new HBoxContainer();
        res.AddThemeConstantOverride("separation", 18);
        _result = MakeLabel("", Fonts.UiBold, 26, TextCol);
        res.AddChild(_result);
        _reward = MakeLabel("", Fonts.UiBold, 26, MoneyCol);
        res.AddChild(_reward);
        res.CustomMinimumSize = new Vector2(0f, 38f);
        box.AddChild(res);

        _explain = MakeText("", 20, DimCol);
        box.AddChild(_explain);

        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        buttons.AddThemeConstantOverride("separation", 32);
        var back = MakeButton(L("返回", "Back"), BtnGrey);
        back.Pressed += Back;
        buttons.AddChild(back);
        _next = MakeButton(L("下一题", "Next"), BtnBlue);
        _next.Pressed += Next;
        buttons.AddChild(_next);
        box.AddChild(buttons);

        box.AddChild(MakeLabel(L("1-4 / A-D 选择 · Enter 下一题 · Esc 返回 · ", "1-4 / A-D = pick · Enter = next · Esc = back · ") + QuestionBank.Attribution,
            Fonts.Ui, 17, DimCol, HorizontalAlignment.Center));

        ShowQuestion();
    }

    private void ShowQuestion()
    {
        _q = QuestionBank.Current();
        _picked = -1;
        _result.Text = "";
        _reward.Text = "";
        _explain.Text = "";
        _explain.Visible = false;
        _next.Disabled = true;
        if (_q == null)
        {
            _tag.Text = "";
            _question.Text = L("题库读不出来（data/questions.txt），没法答题。", "Couldn't read the question bank (data/questions.txt), so there is no quiz.");
            foreach (var b in _opts) b.Visible = false;
            RefreshStatus();
            return;
        }

        _tag.Text = string.IsNullOrEmpty(_q.Subject) || _q.Subject == _q.Category ? _q.Category : $"{_q.Category} · {_q.Subject}";
        _question.Text = _q.Text;
        _question.AddThemeFontSizeOverride("font_size", _q.Text.Length <= 80 ? 30 : _q.Text.Length <= 180 ? 26 : 23);
        int longest = 0;
        foreach (string o in _q.Options) longest = Math.Max(longest, o.Length);
        int optSize = longest <= 40 ? 24 : 21;
        for (int k = 0; k < 4; k++)
        {
            var b = _opts[k];
            b.Visible = true;
            b.Text = $"{(char)('A' + k)}.  {_q.Options[k]}";
            b.AddThemeFontSizeOverride("font_size", optSize);
            b.MouseFilter = Control.MouseFilterEnum.Stop;
            SetButtonColor(b, OptCol, 18f, 10f);
        }
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        _balance.Text = L("余额 ", "Balance ") + Money(Wallet.Balance);
        int got = Wallet.QuizCorrect % Wallet.QuizBatch;
        _dots.Text = new string('●', got) + new string('○', Wallet.QuizBatch - got);
        _progress.Text = L($"再答对 {Wallet.QuizBatch - got} 题得 {Money(Wallet.QuizReward)} · 累计答对 {Wallet.QuizCorrect}/{Wallet.QuizAnswered} · 已赚 {Money(Wallet.QuizEarned)}",
                           $"{Wallet.QuizBatch - got} more correct for {Money(Wallet.QuizReward)} · {Wallet.QuizCorrect}/{Wallet.QuizAnswered} correct · earned {Money(Wallet.QuizEarned)}");
    }

    public void Pick(int k)
    {
        if (_q == null || _picked >= 0 || k < 0 || k > 3) return;
        _picked = k;
        bool ok = k == _q.Answer;
        bool paid = Wallet.QuizAnswer(ok);
        GD.Print($"[quiz] pick {(char)('A' + k)} answer {(char)('A' + _q.Answer)} {(ok ? "right" : "wrong")}{(paid ? " paid" : "")} balance {Wallet.Balance:0.00}");

        for (int i = 0; i < 4; i++)
        {
            var b = _opts[i];
            b.MouseFilter = Control.MouseFilterEnum.Ignore;
            SetButtonColor(b, i == _q.Answer ? RightCol : i == k ? WrongCol : OffCol, 18f, 10f);
        }
        _result.Text = ok ? L("✓ 答对了", "✓ Correct") : L($"✗ 答错了，正确答案是 {(char)('A' + _q.Answer)}", $"✗ Wrong, the answer is {(char)('A' + _q.Answer)}");
        SetColor(_result, ok ? GoodCol : BadCol);
        _reward.Text = paid ? L($"+{Money(Wallet.QuizReward)} 已到账！", $"+{Money(Wallet.QuizReward)} added!") : "";
        if (!string.IsNullOrEmpty(_q.Explanation))
        {
            _explain.Text = L("解析：", "Explanation: ") + _q.Explanation;
            _explain.Visible = true;
        }
        _next.Disabled = false;
        _next.GrabFocus();
        RefreshStatus();
    }

    private void Next()
    {
        if (_picked < 0) return;
        ShowQuestion();
    }

    private void Back()
    {
        QueueFree();
        OnDone?.Invoke();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventKey key || !key.Pressed || key.Echo) return;
        int pick = key.Keycode switch
        {
            Key.Key1 or Key.Kp1 or Key.A => 0,
            Key.Key2 or Key.Kp2 or Key.B => 1,
            Key.Key3 or Key.Kp3 or Key.C => 2,
            Key.Key4 or Key.Kp4 or Key.D => 3,
            _ => -1,
        };
        if (pick >= 0)
        {
            GetViewport().SetInputAsHandled();
            Pick(pick);
        }
        else if (key.Keycode is Key.Enter or Key.KpEnter && _picked >= 0)
        {
            GetViewport().SetInputAsHandled();
            Next();
        }
        else if (key.Keycode == Key.Escape)
        {
            GetViewport().SetInputAsHandled();
            Back();
        }
    }
}
