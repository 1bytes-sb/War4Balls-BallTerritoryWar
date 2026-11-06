using Godot;
using System;
using System.Globalization;
using static Ui;
using static Loc;

/// <summary>
/// 游戏设置（主菜单里点「游戏设置」进来）：语言、初始护盾、初始机枪火力、弹珠落进 5 个槽的概率、资金模式的两个交易选项（浮盈加仓、合并仓位）。
/// 点「保存」时写进 Main.Settings，并存到 user://settings.cfg（下次打开还在），然后回主菜单；「返回」不保存（语言也换回进来时的）。
/// 正常模式和资金模式开局都用这里的设置。
/// 槽概率和默认一样时弹珠按物理自然落槽（默认值就是自然落槽大约的比例）；改了就在底部加一个固定的拉力场，
/// 把比例往设定值拉（大约，见 GameSettings.SlotPull*、LeftArena.BuildSlotPull）。
/// 界面：标题、语言一行、底下的按钮固定；中间一段放不下就上下滑动。换语言马上整个重搭（填了还没保存的值都留着）。
/// </summary>
public partial class SettingsMenu : CanvasLayer
{
    /// <summary>点了「保存」（设置已经写进 Main.Settings）或「返回」之后调用：回主菜单。</summary>
    public Action OnDone;

    /// <summary>保存时把设置存进 user://settings.cfg（带 --set 的调试运行不存）。</summary>
    public bool Persist = true;

    private const string CfgPath = "user://settings.cfg";
    private const double MaxAmount = 1e12;      // 初始护盾 / 机枪火力最多 1T
    private static readonly string[] SlotKeys = { "storm", "mg", "shield", "ball", "sniper" };
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static GameSettings S => Main.Settings;
    private static readonly GameSettings Defaults = GameSettings.CreateDefault();

    /// <summary>还没保存的输入（换语言重搭界面时带过去）。</summary>
    private sealed class Draft
    {
        public string Shield, Mg;
        public readonly double[] Slots = new double[5];
        public bool UseProfit, Merge;
        public int Scroll;
    }

    private VBoxContainer _box;
    private ScrollContainer _scroll;
    private Control _scrollBody;
    private LineEdit _shieldEdit, _mgEdit;
    private Label _shieldHint, _mgHint, _sumLabel, _status;
    private readonly HSlider[] _sliders = new HSlider[5];
    private readonly SpinBox[] _spins = new SpinBox[5];
    private readonly Label[] _pct = new Label[5];
    private Button[] _langBtns, _profitBtns, _mergeBtns;
    private Button _startBtn;
    private bool _useProfit, _merge;
    private bool _savedEn;                      // 进来时的语言：「返回」时换回去
    private int _pendingScroll, _scrollTries;   // 换语言重搭后把滑动位置恢复（要等排版好）
    private bool _sync;                         // 滑条和数字框互相同步时不重复触发

    // =====================================================================
    //  存档
    // =====================================================================

    /// <summary>读上次存的设置到 Main.Settings（没有存档就不动），语言也在这里读。</summary>
    public static void LoadSaved()
    {
        var cf = new ConfigFile();
        if (cf.Load(CfgPath) != Error.Ok) return;
        S.StartShield = (float)Math.Clamp(cf.GetValue("start", "shield", S.StartShield).AsDouble(), 0.0, MaxAmount);
        S.StartMachineGunFire = (float)Math.Clamp(cf.GetValue("start", "mg", S.StartMachineGunFire).AsDouble(), 0.0, MaxAmount);
        for (int k = 0; k < 5; k++)
            S.SetSlotShare(k, Mathf.Clamp(cf.GetValue("slots", SlotKeys[k], S.SlotShare(k)).AsSingle(), 0f, 100f));
        float total = 0f;
        for (int k = 0; k < 5; k++) total += S.SlotShare(k);
        if (total <= 0f)
            for (int k = 0; k < 5; k++) S.SetSlotShare(k, Defaults.SlotShare(k));
        S.FundsUseProfit = cf.GetValue("funds", "use_profit", S.FundsUseProfit).AsBool();
        S.FundsMerge = cf.GetValue("funds", "merge", S.FundsMerge).AsBool();
        Loc.En = cf.GetValue("ui", "lang", Loc.En ? "en" : "zh").AsString() == "en";
    }

    private static void Save()
    {
        var cf = new ConfigFile();
        cf.SetValue("start", "shield", (double)S.StartShield);
        cf.SetValue("start", "mg", (double)S.StartMachineGunFire);
        for (int k = 0; k < 5; k++) cf.SetValue("slots", SlotKeys[k], S.SlotShare(k));
        cf.SetValue("funds", "use_profit", S.FundsUseProfit);
        cf.SetValue("funds", "merge", S.FundsMerge);
        cf.SetValue("ui", "lang", Loc.En ? "en" : "zh");
        Error err = cf.Save(CfgPath);
        if (err != Error.Ok) GD.PrintErr($"[settings] save {CfgPath} failed: {err}");
    }

    private static bool SharesDefault(Func<int, float> share)
    {
        for (int k = 0; k < 5; k++)
            if (Mathf.Abs(share(k) - Defaults.SlotShare(k)) > 0.001f) return false;
        return true;
    }

    // =====================================================================
    //  数值输入：25M、500K、1.5B、2500万、3亿、25,000,000、1e9
    // =====================================================================

    /// <summary>认不出来（或是负数）返回 null。后缀 K/M/B/T/W（万）不分大小写，也认「万」「亿」。</summary>
    public static double? ParseAmount(string text)
    {
        string s = (text ?? "").Trim().Replace(",", "").Replace("，", "").Replace(" ", "").Replace("_", "").ToUpperInvariant();
        double mult = 1.0;
        (string suf, double m)[] sufs = { ("K", 1e3), ("M", 1e6), ("B", 1e9), ("T", 1e12), ("W", 1e4), ("万", 1e4), ("亿", 1e8) };
        for (bool found = true; found && s.Length > 0;)
        {
            found = false;
            foreach (var (suf, m) in sufs)
                if (s.EndsWith(suf, StringComparison.Ordinal))
                {
                    mult *= m;
                    s = s.Substring(0, s.Length - suf.Length);
                    found = true;
                    break;
                }
        }
        if (s.Length == 0 || !double.TryParse(s, NumberStyles.Float, Inv, out double v) || double.IsNaN(v) || double.IsInfinity(v) || v < 0)
            return null;
        return v * mult;
    }

    /// <summary>25000000 → 25M、500000 → 500K；换成单位会丢精度的就写整数。</summary>
    public static string FormatAmount(double v)
    {
        (double u, string s)[] units = { (1e12, "T"), (1e9, "B"), (1e6, "M"), (1e3, "K") };
        foreach (var (u, s) in units)
        {
            if (v < u) continue;
            string txt = (v / u).ToString("0.###", Inv) + s;
            double? back = ParseAmount(txt);
            if (back != null && Math.Abs(back.Value - v) <= v * 1e-6) return txt;
            break;
        }
        return v.ToString("0", Inv);
    }

    // =====================================================================
    //  界面
    // =====================================================================

    public override void _Ready()
    {
        Layer = 10;
        _savedEn = Loc.En;
        var d = new Draft
        {
            Shield = FormatAmount(S.StartShield), Mg = FormatAmount(S.StartMachineGunFire),
            UseProfit = S.FundsUseProfit, Merge = S.FundsMerge,
        };
        for (int k = 0; k < 5; k++) d.Slots[k] = S.SlotShare(k);
        Build(d);
        _startBtn.CallDeferred(Control.MethodName.GrabFocus);
    }

    private void Build(Draft d)
    {
        _startBtn = null;                       // 搭完之前 Refresh 不动
        var box = _box = Screen(this, 980f);

        box.AddChild(MakeLabel(L("游戏设置", "Settings"), Fonts.UiBold, 44, TextCol, HorizontalAlignment.Center));
        box.AddChild(Gap(4));
        _langBtns = ToggleRow(box, "语言 / Language", new[] { "中文", "English" }, Loc.En ? 1 : 0, i => SetLang(i == 1), null, 130f);
        box.AddChild(Gap(2));

        // 中间这段放不下就上下滑（高度在 FitScroll 里定）；右边留一点给滚动条
        _scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true };
        var pad = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        pad.AddThemeConstantOverride("margin_right", 16);
        _scroll.AddChild(pad);
        _scrollBody = pad;
        var c = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        c.AddThemeConstantOverride("separation", 12);
        pad.AddChild(c);
        box.AddChild(_scroll);

        c.AddChild(MakeLabel(L("开局数值（每队一样）", "Start values (same for every team)"), Fonts.UiBold, 28, AccentCol));
        _shieldEdit = AmountRow(c, L("初始护盾", "Start shield"), d.Shield, out _shieldHint);
        _mgEdit = AmountRow(c, L("初始机枪火力", "Start MG fire"), d.Mg, out _mgHint);
        c.AddChild(MakeLabel(L("可以写 25M、500K、1.5B、2500万、3亿 或 25000000；填 0 = 开局不加",
                               "Write 25M, 500K, 1.5B or 25000000 (万 / 亿 work too); 0 = none at start"), Fonts.Ui, 19, DimCol));
        c.AddChild(Gap(10));

        c.AddChild(MakeLabel(L("弹珠落进各槽的概率（按比例算，不用凑满 100）", "Marble slot odds (relative weights, need not add up to 100)"),
                             Fonts.UiBold, 28, AccentCol));
        for (int k = 0; k < 5; k++) SlotRow(c, k, d.Slots[k]);
        _sumLabel = MakeLabel("", Fonts.Ui, 19, DimCol);
        c.AddChild(_sumLabel);
        _status = MakeText("", 21, DimCol);
        c.AddChild(_status);
        c.AddChild(Gap(10));

        c.AddChild(MakeLabel(L("资金模式的交易选项", "Trading mode options"), Fonts.UiBold, 28, AccentCol));
        _useProfit = d.UseProfit;
        _merge = d.Merge;
        string[] offOn = { L("关", "Off"), L("开", "On") };
        _profitBtns = ToggleRow(c, L("浮盈加仓", "Use profit"), offOn, _useProfit ? 1 : 0, i => _useProfit = i == 1,
            L("现金不够时，可以借用持仓里的浮盈开新仓\n被借的那笔投入变少，强平价会上移",
              "When cash runs short, use unrealized profit\nfrom open positions (their liq. price rises)"));
        _mergeBtns = ToggleRow(c, L("合并仓位", "Merge positions"), offOn, _merge ? 1 : 0, i => _merge = i == 1,
            L("同一阵营、同方向、同杠杆的新单并进原来那笔\n开仓价按股数加权，变成成本均价",
              "Same team, side and leverage: add to the\nexisting position at the average price"));

        box.AddChild(Gap(10));
        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        buttons.AddThemeConstantOverride("separation", 32);
        var reset = MakeButton(L("恢复默认", "Defaults"), BtnGrey);
        reset.Pressed += ResetDefaults;
        buttons.AddChild(reset);
        var back = MakeButton(L("返回", "Back"), BtnGrey);
        back.Pressed += Back;
        buttons.AddChild(back);
        _startBtn = MakeButton(L("保存", "Save"), BtnBlue);
        _startBtn.Pressed += TrySave;
        buttons.AddChild(_startBtn);
        box.AddChild(buttons);

        box.AddChild(Gap(2));
        box.AddChild(MakeLabel(L("「返回」不保存改动 · Esc 也是返回 · 正常模式和资金模式开局都用这里的设置",
                                 "Back discards changes · Esc = Back · Normal and Trading modes both use these settings"),
                               Fonts.Ui, 19, DimCol, HorizontalAlignment.Center));

        // 键盘 ↑↓：滑到可见区外面的交易开关按位置找不到（会直接跳到底部按钮），手动接上；滑动区跟着焦点走
        LinkDown(_sliders[4], _profitBtns[0]);
        LinkDown(_spins[4], _profitBtns[1]);
        LinkDown(_spins[4].GetLineEdit(), _profitBtns[1]);
        LinkUp(reset, _mergeBtns[0]);
        LinkUp(back, _mergeBtns[1]);
        LinkUp(_startBtn, _mergeBtns[1]);
        // 跟着焦点滑时只保证按钮露出来，右边两行说明比按钮高一点会被切掉：整行都滑进来
        var sc = _scroll;
        foreach (var b in new[] { _profitBtns[0], _profitBtns[1], _mergeBtns[0], _mergeBtns[1] })
        {
            var row = (Control)b.GetParent().GetParent();
            b.FocusEntered += () => Callable.From(() =>
            {
                if (IsInstanceValid(sc) && IsInstanceValid(row) && sc.IsAncestorOf(row)) sc.EnsureControlVisible(row);
            }).CallDeferred();
        }

        Refresh();
        _pendingScroll = d.Scroll;
        _scrollTries = 0;
        Callable.From(FitScroll).CallDeferred();
    }

    /// <summary>中间那段的高度：放得下就全显示（不滑），放不下就让整块面板刚好在屏幕里（上下各留 20 px）。</summary>
    private void FitScroll()
    {
        if (!IsInstanceValid(_scroll) || !_scroll.IsInsideTree()) return;
        _scroll.CustomMinimumSize = Vector2.Zero;
        float fixedH = ((Control)_box.GetParent()).GetCombinedMinimumSize().Y;
        float bodyH = _scrollBody.GetCombinedMinimumSize().Y;
        float room = GetViewport().GetVisibleRect().Size.Y - 40f - fixedH;
        _scroll.CustomMinimumSize = new Vector2(0f, Mathf.Max(160f, Mathf.Min(bodyH, room)));
    }

    public override void _Process(double delta)
    {
        if (_pendingScroll <= 0 || !IsInstanceValid(_scroll)) return;
        _scroll.ScrollVertical = _pendingScroll;
        if (_scroll.ScrollVertical == _pendingScroll || ++_scrollTries > 30) _pendingScroll = 0;
    }

    /// <summary>换语言：记下没保存的输入，下一帧整个界面按新语言重搭（不在按钮自己的信号里拆它）。</summary>
    private void SetLang(bool en)
    {
        if (Loc.En == en) return;
        var d = new Draft { Shield = _shieldEdit.Text, Mg = _mgEdit.Text, UseProfit = _useProfit, Merge = _merge, Scroll = _scroll.ScrollVertical };
        for (int k = 0; k < 5; k++) d.Slots[k] = _spins[k].Value;
        Loc.En = en;
        Callable.From(() =>
        {
            foreach (Node n in GetChildren())
            {
                RemoveChild(n);
                n.QueueFree();
            }
            Build(d);
            _langBtns[en ? 1 : 0].CallDeferred(Control.MethodName.GrabFocus);
        }).CallDeferred();
    }

    /// <summary>一行：名字 + 几个只能选一个的按钮（选中的是蓝色）+ 说明（可以没有）；返回按钮，下标就是选项。</summary>
    private static Button[] ToggleRow(VBoxContainer box, string name, string[] options, int selected, Action<int> onPick, string note, float w = 100f)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 18);
        var l = MakeLabel(name, Fonts.Ui, 25, TextCol);
        l.CustomMinimumSize = new Vector2(200f, 0f);
        row.AddChild(l);
        var group = new HBoxContainer();
        group.AddThemeConstantOverride("separation", 8);
        var btns = new Button[options.Length];
        for (int i = 0; i < options.Length; i++)
        {
            int idx = i;
            var b = MakeButton(options[i], BtnGrey, w, 46f, 22);
            b.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            b.Pressed += () =>
            {
                Pick(btns, idx);
                onPick(idx);
            };
            btns[i] = b;
            group.AddChild(b);
        }
        row.AddChild(group);
        if (note != null)
        {
            var n = MakeLabel(note, Fonts.Ui, 19, DimCol);
            n.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(n);
        }
        box.AddChild(row);
        Pick(btns, selected);
        return btns;
    }

    private static void Pick(Button[] btns, int sel)
    {
        for (int i = 0; i < btns.Length; i++) SetButtonColor(btns[i], i == sel ? BtnBlue : BtnGrey);
    }

    private static void LinkDown(Control from, Control to) => from.FocusNeighborBottom = from.GetPathTo(to);
    private static void LinkUp(Control from, Control to) => from.FocusNeighborTop = from.GetPathTo(to);

    private LineEdit AmountRow(VBoxContainer box, string name, string text, out Label hint)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 18);
        var l = MakeLabel(name, Fonts.Ui, 25, TextCol);
        l.CustomMinimumSize = new Vector2(200f, 0f);
        row.AddChild(l);
        var e = new LineEdit { Text = text, CustomMinimumSize = new Vector2(260f, 46f), SelectAllOnFocus = true };
        e.TextChanged += _ => Refresh();
        row.AddChild(e);
        hint = MakeLabel("", Fonts.Ui, 21, DimCol);
        hint.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(hint);
        box.AddChild(row);
        return e;
    }

    private void SlotRow(VBoxContainer box, int k, double value)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 18);
        var l = MakeLabel(Zone(k) + L("槽", " slot"), Fonts.Ui, 25, TextCol);
        l.CustomMinimumSize = new Vector2(200f, 0f);
        row.AddChild(l);
        var sl = new HSlider
        {
            MinValue = 0, MaxValue = 100, Step = 1, Value = value, Scrollable = false,     // 滚轮留给整段上下滑
            CustomMinimumSize = new Vector2(380f, 40f), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        var sp = new SpinBox
        {
            MinValue = 0, MaxValue = 100, Step = 1, Value = value,
            CustomMinimumSize = new Vector2(140f, 46f), UpdateOnTextChanged = true, SelectAllOnFocus = true,
        };
        sl.ValueChanged += v => Sync(sp, v);
        sp.ValueChanged += v => Sync(sl, v);
        row.AddChild(sl);
        row.AddChild(sp);
        var pct = MakeLabel("", Fonts.Num, 24, AccentCol, HorizontalAlignment.Right);
        pct.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(pct);
        _sliders[k] = sl;
        _spins[k] = sp;
        _pct[k] = pct;
        box.AddChild(row);
    }

    private void Sync(Godot.Range other, double v)
    {
        if (_sync) return;
        _sync = true;
        other.Value = v;
        _sync = false;
        Refresh();
    }

    /// <summary>重算提示：数值框下面的换算、各槽实际百分比、状态行；有填错的就不让保存。</summary>
    private void Refresh()
    {
        if (_startBtn == null) return;         // 还在搭界面
        bool ok = ShowAmount(_shieldEdit, _shieldHint) & ShowAmount(_mgEdit, _mgHint);

        double total = 0.0;
        for (int k = 0; k < 5; k++) total += _spins[k].Value;
        for (int k = 0; k < 5; k++)
            _pct[k].Text = total > 0.0 ? (100.0 * _spins[k].Value / total).ToString("0.0", Inv) + "%" : "—";
        string sum = total.ToString("0", Inv);
        _sumLabel.Text = L($"合计 {sum}，右边是换算成的实际概率", $"Total {sum}; the right column is the actual chance");

        if (total <= 0.0)
        {
            SetColor(_status, BadCol);
            _status.Text = L("至少要有一个槽大于 0", "At least one slot must be above 0");
            ok = false;
        }
        else if (SharesDefault(k => (float)_spins[k].Value))
        {
            SetColor(_status, DimCol);
            _status.Text = L("槽概率是默认值：弹珠按物理自然落槽，比例大约就是这样",
                             "Default odds: marbles fall into the slots by physics alone, at about these rates");
        }
        else
        {
            SetColor(_status, AccentCol);
            _status.Text = L("改过槽概率：底部加一个固定的拉力场，把落槽比例往上面的设定拉（每队都一样，比例是大约的）",
                             "Custom odds: a fixed pull field at the bottom steers marbles toward these rates (same for every team, approximate)");
        }
        _startBtn.Disabled = !ok;
    }

    private static bool ShowAmount(LineEdit e, Label hint)
    {
        double? v = ParseAmount(e.Text);
        if (v == null || v.Value > MaxAmount)
        {
            SetColor(hint, BadCol);
            hint.Text = v == null ? L("看不懂这个数", "Can't read this number") : L("太大了，最多 1T（1 万亿）", "Too big, max 1T");
            return false;
        }
        SetColor(hint, DimCol);
        hint.Text = v.Value == 0.0 ? L("= 0（开局不加）", "= 0 (none at start)") : "= " + v.Value.ToString("N0", Inv);
        return true;
    }

    private void ResetDefaults()
    {
        _shieldEdit.Text = FormatAmount(Defaults.StartShield);
        _mgEdit.Text = FormatAmount(Defaults.StartMachineGunFire);
        _sync = true;
        for (int k = 0; k < 5; k++)
        {
            _sliders[k].Value = Defaults.SlotShare(k);
            _spins[k].Value = Defaults.SlotShare(k);
        }
        _sync = false;
        _useProfit = Defaults.FundsUseProfit;
        _merge = Defaults.FundsMerge;
        Pick(_profitBtns, _useProfit ? 1 : 0);
        Pick(_mergeBtns, _merge ? 1 : 0);
        Refresh();
    }

    private void TrySave()
    {
        for (int k = 0; k < 5; k++) _spins[k].Apply();     // 数字框里打了字还没回车的也算上
        Refresh();
        if (_startBtn.Disabled) return;

        S.StartShield = (float)ParseAmount(_shieldEdit.Text).Value;
        S.StartMachineGunFire = (float)ParseAmount(_mgEdit.Text).Value;
        for (int k = 0; k < 5; k++) S.SetSlotShare(k, (float)_spins[k].Value);
        S.FundsUseProfit = _useProfit;
        S.FundsMerge = _merge;
        if (Persist) Save();
        GD.Print($"[settings] shield={S.StartShield:0} mg={S.StartMachineGunFire:0} slots={S.SlotShareStorm}/{S.SlotShareMg}/{S.SlotShareShield}/{S.SlotShareBall}/{S.SlotShareSniper}" +
                 $" lang={(Loc.En ? "en" : "zh")} use_profit={(S.FundsUseProfit ? 1 : 0)} merge={(S.FundsMerge ? 1 : 0)}");

        QueueFree();
        OnDone?.Invoke();
    }

    private void Back()
    {
        Loc.En = _savedEn;                      // 没保存：语言也换回去
        QueueFree();
        OnDone?.Invoke();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey k && k.Pressed && !k.Echo && k.Keycode == Key.Escape)
        {
            GetViewport().SetInputAsHandled();
            Back();
        }
    }
}
