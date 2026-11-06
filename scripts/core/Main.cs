using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;

/// <summary>
/// 全局入口：左侧弹珠台 + 右侧领土战场 + 两侧之间的粒子充能 HUD。
///
/// 坐标约定：
///   左侧场地节点位于 (0,0)，屏幕坐标 760×1080。
///   右侧领土战场 1000×1000，节点放在 FieldRect（840,40）。
///   HUD 两列：x = 800（红上 / 蓝下），x = 1880（绿上 / 黄下）。
///
/// 两侧连接：左侧槽落球 → RightArena.InjectZone；右侧充能满 → LeftArena.SpawnExtraBall。
/// </summary>
public partial class Main : Node2D
{
    // =====================================================================
    //  全局设置与时间
    // =====================================================================

    public static GameSettings Settings { get; private set; } = GameSettings.CreateDefault();

    /// <summary>游戏运行时间（秒），每帧在 _Process 中累加，_Ready 时重置为 0。</summary>
    public static float GameTime { get; private set; }

    // 主菜单（MainMenu）：程序启动后第一次进场景、没带调试参数、不是 headless 时先显示；游戏里按 F2 也回到这里。
    // 从主菜单进：正常模式 / 游戏设置（SettingsMenu）/ 资金模式（带钱进场，TradePanel）/ 补充资金（QuizMenu）。
    // 赢了自动重开、按 R 重开都不回主菜单，直接按原来的模式开下一局
    private static bool _firstLoad = true;
    private static bool _menuRequested;

    /// <summary>资金模式：游戏画面缩小，旁边是交易界面（TradePanel）；账户在 Market.Current。</summary>
    public static bool FundsMode { get; private set; }

    private bool _setArgs;         // 带了 --set：设置界面不读也不存 settings.cfg
    private TradePanel _trade;

    // =====================================================================
    //  四大阵营
    // =====================================================================

    public const int TeamCount = 4;

    /// <summary>阵营顺序：0 红(左上) 1 绿(右上) 2 蓝(左下) 3 黄(右下)。</summary>
    public static readonly string[] TeamNames = { "红", "绿", "蓝", "黄" };

    /// <summary>视频原色。</summary>
    public static readonly Color[] TeamColors =
    {
        new Color(219f/255, 51f/255,  84f/255),   // 红
        new Color(35f/255,  191f/255, 118f/255),  // 绿
        new Color(21f/255,  134f/255, 214f/255),  // 蓝
        new Color(209f/255, 138f/255, 49f/255),   // 黄
    };

    /// <summary>右侧领土战场：1000×1000 像素。</summary>
    public static readonly Rect2 FieldRect = new Rect2(840f, 40f, 1000f, 1000f);

    public LeftArena Left { get; private set; }
    public RightArena Right { get; private set; }

    // =====================================================================
    //  HUD 布局（视频实测，屏幕坐标）
    // =====================================================================

    private static readonly float[] HudX = { 800f, 1880f, 800f, 1880f };
    private static readonly bool[] HudTop = { true, true, false, false };

    private const float TopLabelY = 39f, TopValueY = 59f, TopMaxY = 77.5f, TopMarbleLabelY = 100f, TopRingY = 131f;
    private const float TopBarZero = 495f, TopBarFull = 152f;
    private const float BotRingY = 947.5f, BotMarbleLabelY = 977.5f, BotLabelY = 1001.5f, BotValueY = 1020f, BotMaxY = 1039f;
    private const float BotBarZero = 584f, BotBarFull = 928f;

    private static readonly Color HudText = new Color(0.80f, 0.84f, 0.90f);
    private static readonly Color HudTextDim = new Color(0.68f, 0.72f, 0.78f);

    // =====================================================================
    //  CLI 调试参数
    // =====================================================================

    private readonly List<(float at, string path)> _shots = new();
    private readonly List<(float at, int team, string zone, float value)> _injects = new();
    private float _quitAt  = -1f;
    private bool _noLeft;          // --noleft：左侧弹珠台的触发不送到右侧（只留 --inject，测数值用）
    private float _slowAt = -1f, _slowScale = 1f;
    private float _statsEvery = -1f;
    private float _statsNext;
    private bool _forceMenu;       // --menu：带调试参数也先显示主菜单（截图、自动退出按在菜单里待的秒数算）
    private float _menuTime;
    private float _menuEnterAt = -1f;
    // --keys=秒:键,秒:键…：按真实时间模拟按键（测菜单、答题用；只在第一次进场景时读，跨重开一直算）
    private static readonly List<(float at, Key key)> _keys = new();
    private static float _wallTime;
    private bool _winLog;          // --winlog[=秒]：每 5 秒（或给定秒数，模拟时间）打一行各队局面、分出胜负打赢家（拟合胜率模型用，见 WinModel）
    public bool WinLogOn => _winLog;   // 资金模式自己也打局面记录，开了 --winlog 就不重复打
    private float _winLogEvery = WinModel.WinLogEvery;
    private float _winLogNext;
    private bool _winLogged;

    // =====================================================================
    //  生命周期
    // =====================================================================

    public override void _Ready()
    {
        GameTime = 0f;          // 重置！场景重载时防止时间漂移

        // 解析调试命令行
        bool debugArgs = false, setArgs = false, quizArg = false;
        double? walletArg = null;
        double fundsArg = 0.0;
        bool? langArg = null;
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg != "--fullscreen") debugArgs = true;
            if (arg.StartsWith("--timescale="))
            {
                if (float.TryParse(arg.Substring(12), NumberStyles.Float, CultureInfo.InvariantCulture, out float ts))
                {
                    Engine.TimeScale = ts;
                    // 加速时左侧物理每帧要走更多步，否则会落后于游戏时间
                    Engine.MaxPhysicsStepsPerFrame = Math.Max(8, (int)MathF.Ceiling(ts * 3f));
                }
            }
            else if (arg.StartsWith("--shot="))
            {
                string payload = arg.Substring(7);
                int colon = payload.IndexOf(':');
                if (colon > 0 &&
                    float.TryParse(payload.Substring(0, colon), NumberStyles.Float, CultureInfo.InvariantCulture, out float sec))
                {
                    _shots.Add((sec, payload.Substring(colon + 1)));     // 可重复：一次运行截多张
                    // 截图时按 1920×1080 原生分辨率渲染（窗口再缩放显示），方便和视频逐像素对比
                    GetTree().Root.ContentScaleMode = Window.ContentScaleModeEnum.Viewport;
                }
            }
            else if (arg.StartsWith("--slow="))
            {
                // --slow=秒:倍速：模拟到这一秒后改用这个倍速（先快进、再慢放逐帧截图用）
                string[] p = arg.Substring(7).Split(':');
                if (p.Length == 2 &&
                    float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float sec) &&
                    float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float sc))
                    (_slowAt, _slowScale) = (sec, sc);
            }
            else if (arg.StartsWith("--quit="))
            {
                if (float.TryParse(arg.Substring(7), NumberStyles.Float, CultureInfo.InvariantCulture, out float sec))
                    _quitAt = sec;
            }
            else if (arg.StartsWith("--stats="))
            {
                if (float.TryParse(arg.Substring(8), NumberStyles.Float, CultureInfo.InvariantCulture, out float sec))
                    _statsEvery = _statsNext = sec;
            }
            else if (arg == "--winlog" || arg.StartsWith("--winlog="))
            {
                _winLog = true;
                if (arg.Length > 9 && float.TryParse(arg.Substring(9), NumberStyles.Float, CultureInfo.InvariantCulture, out float every) && every > 0f)
                    _winLogEvery = every;
            }
            else if (arg == "--events")
            {
                RightArena.LogEvents = true;
            }
            else if (arg == "--balllog" || arg.StartsWith("--balllog="))
            {
                // 大球位置 / 速度（[bl]，默认每 0.5 模拟秒）：查大球卡角落用
                RightArena.BallLogEvery = 0.5f;
                if (arg.Length > 10 && float.TryParse(arg.Substring(10), NumberStyles.Float, CultureInfo.InvariantCulture, out float be) && be > 0f)
                    RightArena.BallLogEvery = be;
            }
            else if (arg == "--mtrace" || arg.StartsWith("--mtrace="))
            {
                // 弹珠轨迹（[mt]，默认每 0.1 模拟秒）：拟合弹珠这一轮进杯 / 进槽的预测用
                LeftArena.TraceEvery = 0.1f;
                if (arg.Length > 9 && float.TryParse(arg.Substring(9), NumberStyles.Float, CultureInfo.InvariantCulture, out float te) && te > 0f)
                    LeftArena.TraceEvery = te;
            }
            else if (arg.StartsWith("--set="))
            {
                // --set=字段名=数值，例如 --set=MgMaxShotsPerStep=8（调参实验用，可重复）
                setArgs = true;
                string[] p = arg.Substring(6).Split('=');
                var fi = p.Length == 2 ? typeof(GameSettings).GetField(p[0]) : null;
                if (fi == null) { GD.PrintErr($"[set] unknown setting: {arg}"); continue; }
                fi.SetValue(Settings, Convert.ChangeType(p[1], fi.FieldType, CultureInfo.InvariantCulture));
                GD.Print($"[set] {p[0]} = {fi.GetValue(Settings)}");
            }
            else if (arg == "--fullscreen")
            {
                SetFullscreen(true);
            }
            else if (arg == "--noleft")
            {
                _noLeft = true;
            }
            else if (arg == "--prof")
            {
                RightArena.Prof = true;
            }
            else if (arg == "--radial")
            {
                RightArena.Radial = true;
            }
            else if (arg == "--menu")
            {
                _forceMenu = true;
            }
            else if (arg == "--quiz")
            {
                quizArg = true;            // 直接进「补充资金」答题
            }
            else if (arg.StartsWith("--lang="))
            {
                langArg = arg.Substring(7) == "en";     // --lang=en|zh：界面语言（调试用；盖过存档里的，不存）
            }
            else if (arg.StartsWith("--wallet="))
            {
                // --wallet=金额：调试运行的余额（调试运行不读也不存 wallet.cfg，默认 $100）
                if (double.TryParse(arg.Substring(9), NumberStyles.Float, CultureInfo.InvariantCulture, out double w))
                    walletArg = w;
            }
            else if (arg.StartsWith("--funds="))
            {
                // --funds=金额：不进菜单，直接带这么多钱进资金模式
                if (double.TryParse(arg.Substring(8), NumberStyles.Float, CultureInfo.InvariantCulture, out double a))
                    fundsArg = a;
            }
            else if (arg.StartsWith("--trade="))
            {
                // --trade=秒:队:long|short:金额[:杠杆] 或 --trade=秒:队|all:close（资金模式按模拟时间下单 / 平仓，可重复）
                if (_firstLoad && !TradePanel.AddDebugOrder(arg.Substring(8))) GD.PrintErr($"[trade] bad order: {arg}");
            }
            else if (arg.StartsWith("--keys="))
            {
                if (_firstLoad)
                    foreach (string item in arg.Substring(7).Split(','))
                    {
                        int c = item.IndexOf(':');
                        Key key = c > 0 ? OS.FindKeycodeFromString(item.Substring(c + 1)) : Key.None;
                        if (key != Key.None && float.TryParse(item.Substring(0, c), NumberStyles.Float, CultureInfo.InvariantCulture, out float at))
                            _keys.Add((at, key));
                        else
                            GD.PrintErr($"[keys] bad key: {item}");
                    }
            }
            else if (arg.StartsWith("--menu-enter="))
            {
                // --menu-enter=秒：在主菜单待这么久后按一下回车（= 点「正常模式」），测菜单开局用
                if (float.TryParse(arg.Substring(13), NumberStyles.Float, CultureInfo.InvariantCulture, out float sec))
                    (_forceMenu, _menuEnterAt) = (true, sec);
            }
            else if (arg.StartsWith("--inject="))
            {
                // --inject=秒:阵营:槽名:数值，例如 --inject=5:1:ball:300000（调试右侧效果用，可重复）
                string[] p = arg.Substring(9).Split(':');
                if (p.Length == 4 &&
                    float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float sec) &&
                    int.TryParse(p[1], out int team) &&
                    float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
                {
                    string zone = p[2] switch
                    {
                        "storm" => "风暴", "mg" => "机枪", "shield" => "护盾", "ball" => "大球", "snipe" => "狙击",
                        _ => p[2],
                    };
                    _injects.Add((sec, team, zone, val));
                }
            }
        }

        bool first = _firstLoad;
        _firstLoad = false;
        _setArgs = setArgs;
        if (first && langArg != null) Loc.En = langArg.Value;
        if (first)
        {
            // 钱包：正常启动读写 user://wallet.cfg；带调试参数不碰存档（余额 $100 或 --wallet=）
            if (debugArgs) Wallet.SetDebug(walletArg ?? Wallet.StartBalance);
            else
            {
                Wallet.Persist = true;
                Wallet.Load();
            }
        }
        bool menu = _menuRequested || (first && (_forceMenu || quizArg || (!debugArgs && DisplayServer.GetName() != "headless")));
        _menuRequested = false;
        if (menu)
        {
            // 上次在设置界面里存的；带 --set 调试时不读也不存，免得测试值盖掉玩家自己的设置
            if (first && !setArgs) SettingsMenu.LoadSaved();
            if (first && langArg != null) Loc.En = langArg.Value;      // 存档里也有语言：--lang 优先
            if (first && quizArg) ShowQuiz();
            else ShowMainMenu();
        }
        else if (first && fundsArg > 0.0)
            StartFunds(fundsArg);
        else
            StartGame();            // 正常模式；资金模式里自动下一局 / 按 R 重开也走这里（FundsMode 还开着）
    }

    // ── 菜单之间的跳转 ──

    private void ShowMainMenu()
    {
        GetViewport().CanvasTransform = Transform2D.Identity;
        AddChild(new MainMenu
        {
            Name = "MainMenu",
            OnNormal = StartNormal,
            OnSettings = ShowSettings,
            OnFunds = StartFunds,
            OnQuiz = ShowQuiz,
            OnQuit = () => GetTree().Quit(),
        });
    }

    private void ShowSettings() => AddChild(new SettingsMenu { Name = "SettingsMenu", OnDone = ShowMainMenu, Persist = !_setArgs });

    private void ShowQuiz() => AddChild(new QuizMenu { Name = "QuizMenu", OnDone = ShowMainMenu });

    private void StartNormal()
    {
        FundsMode = false;
        StartGame();
    }

    /// <summary>进资金模式：从余额里拿出 amount 当场内本金，开局。</summary>
    private void StartFunds(double amount)
    {
        if (!Wallet.StartSession(amount))
        {
            GD.PrintErr($"[funds] can't bring {amount:0.00} (balance {Wallet.Balance:0.00})");
            MainMenu.Message = Loc.L($"没能进入资金模式：余额只有 {Ui.Money(Wallet.Balance)}",
                                     $"Couldn't start Trading mode: the balance is only {Ui.Money(Wallet.Balance)}");
            ShowMainMenu();
            return;
        }
        Market.Current = new Market(amount);
        FundsMode = true;
        StartGame();
    }

    /// <summary>出资金模式（F2 回主菜单、关窗口、调试退出）：持仓按现价平掉，场内现金回到余额，主菜单显示这次的结果。</summary>
    private void EndFunds()
    {
        var m = Market.Current;
        if (m == null) return;
        _trade?.CloseAll("退出平仓");
        double pnl = Wallet.EndSession(m.Cash);
        MainMenu.Message = Loc.L($"资金模式结束：带进 {Ui.Money(m.In)}，带出 {Ui.Money(m.Cash)}（{Ui.SignedMoney(pnl)}），玩了 {m.Game} 局",
                                 $"Trading mode over: brought in {Ui.Money(m.In)}, took out {Ui.Money(m.Cash)} ({Ui.SignedMoney(pnl)}), " +
                                 $"{m.Game} game{(m.Game == 1 ? "" : "s")}");
        GD.Print($"[funds] end: in {m.In:0.00} out {m.Cash:0.00} pnl {pnl:+0.00;-0.00} games {m.Game} trades {m.Trades} liquidations {m.Liquidations}");
        Market.Current = null;
        FundsMode = false;
        GetViewport().CanvasTransform = Transform2D.Identity;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest) EndFunds();      // 直接关窗口：也把钱结算回余额
    }

    /// <summary>建左侧弹珠台、右侧战场和静态 HUD，从 0 秒开始（主菜单选了模式，或者不显示主菜单时直接开局）；资金模式再加交易界面。</summary>
    private void StartGame()
    {
        GameTime = 0f;

        // 左侧弹珠台在 (0,0)
        Left = new LeftArena { Name = "LeftArena", Position = Vector2.Zero };
        Left.OnZoneTriggered = (team, zone, value) => { if (!_noLeft) HandleLeftZone(team, zone, value); };
        AddChild(Left);

        // 右侧领土战场
        Right = new RightArena { Name = "RightArena", Position = FieldRect.Position };
        AddChild(Right);

        // HUD 里很少变的部分（标签、刻度、额外弹珠）单独一层，只在内容变化时重画；放在最底层，层级和原来一样
        _hudStatic = new DrawLayer { Name = "HudStatic", OnDraw = l =>
        {
            if (Right?.Factions[0] == null) return;
            for (int t = 0; t < TeamCount; t++) DrawChargeStatic(l, t, Right.Factions[t]);
        } };
        _hudStatic.TextureFilter = TextureFilterEnum.LinearWithMipmaps;
        AddChild(_hudStatic);
        MoveChild(_hudStatic, 0);

        // 资金模式：游戏画面整体缩到 0.72 放在左上角，右边一列和下面一条是交易界面（它在 CanvasLayer 上，不跟着缩）
        if (FundsMode && Market.Current != null)
        {
            GetViewport().CanvasTransform = Transform2D.Identity.Scaled(new Vector2(TradePanel.GameScale, TradePanel.GameScale));
            _trade = new TradePanel { Name = "TradePanel", Host = this };
            AddChild(_trade);
        }
        else
            GetViewport().CanvasTransform = Transform2D.Identity;
    }

    /// <summary>F11 或 Alt+Enter 切换全屏，Esc 退出全屏（画面按 1920×1080 等比缩放，不会变形）；F2 回主菜单（资金模式先结算出场）。</summary>
    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventKey k || !k.Pressed || k.Echo) return;
        bool full = DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Fullscreen;
        if (k.Keycode == Key.F11 || (k.Keycode == Key.Enter && k.AltPressed))
            SetFullscreen(!full);
        else if (k.Keycode == Key.Escape && full)
            SetFullscreen(false);
        else if (k.Keycode == Key.F2 && Right != null)
        {
            GetViewport().SetInputAsHandled();          // 先标：ReloadCurrentScene 以后这个场景马上离开场景树，GetViewport() 就是 null 了
            EndFunds();
            _menuRequested = true;                      // 回主菜单
            GetTree().ReloadCurrentScene();
            return;
        }
        else
            return;
        GetViewport().SetInputAsHandled();
    }

    private static void SetFullscreen(bool on) =>
        DisplayServer.WindowSetMode(on ? DisplayServer.WindowMode.Fullscreen : DisplayServer.WindowMode.Windowed);

    public override void _Process(double delta)
    {
        _wallTime += (float)delta;
        while (_keys.Count > 0 && _wallTime >= _keys[0].at)
        {
            Key key = _keys[0].key;
            _keys.RemoveAt(0);
            GD.Print($"[keys] {_wallTime:0.00} {OS.GetKeycodeString(key)}");
            Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true });
            Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false });
        }

        if (Right == null)                              // 还在菜单里，不计时
        {
            _menuTime += (float)delta;
            for (int i = _shots.Count - 1; i >= 0; i--)
            {
                if (_menuTime < _shots[i].at) continue;
                TakeScreenshot(_shots[i].path);
                _shots.RemoveAt(i);
            }
            if (_menuEnterAt >= 0f && _menuTime >= _menuEnterAt)
            {
                _menuEnterAt = -1f;
                Input.ParseInputEvent(new InputEventAction { Action = "ui_accept", Pressed = true });
                Input.ParseInputEvent(new InputEventAction { Action = "ui_accept", Pressed = false });
            }
            if (_quitAt > 0f && _menuTime >= _quitAt) GetTree().Quit();
            return;
        }
        float dt = (float)delta;
        GameTime += dt;
        // 截图、统计、自动退出按右侧战场的模拟时间算（截图那几帧很卡，模拟每帧最多跑几步，会落后于 GameTime）
        float sim = Right?.SimTime ?? GameTime;

        if (_slowAt >= 0f && sim >= _slowAt)
        {
            Engine.TimeScale = _slowScale;
            _slowAt = -1f;
        }

        // 截图
        for (int i = _shots.Count - 1; i >= 0; i--)
        {
            if (sim < _shots[i].at) continue;
            TakeScreenshot(_shots[i].path);
            _shots.RemoveAt(i);
        }

        for (int i = _injects.Count - 1; i >= 0; i--)
        {
            if (GameTime < _injects[i].at) continue;
            HandleLeftZone(_injects[i].team, _injects[i].zone, _injects[i].value);
            _injects.RemoveAt(i);
        }

        if (_statsEvery > 0f && sim >= _statsNext)
        {
            _statsNext += _statsEvery;
            PrintStats();
        }

        if (_winLog && Right.Field != null)
        {
            if (Right.Winner < 0 && sim >= _winLogNext)
            {
                _winLogNext += _winLogEvery;
                GD.Print(WinModel.LogLine(Right, Left));
            }
            else if (Right.Winner >= 0 && !_winLogged)
            {
                _winLogged = true;
                GD.Print(WinModel.LogLine(Right, Left));
                GD.Print(string.Format(CultureInfo.InvariantCulture, "[wl] win={0} t={1:0.0}", Right.Winner, sim));
            }
        }

        // 自动退出
        if (_quitAt > 0f && sim >= _quitAt)
        {
            EndFunds();
            GetTree().Quit();
            return;
        }

        // 静态 HUD：门槛、点亮的刻度数、额外弹珠数有变化才重画
        if (Right?.Factions[0] != null)
        {
            for (int t = 0; t < TeamCount; t++)
            {
                var f = Right.Factions[t];
                double frac = Math.Clamp(f.Charge / f.ChargeThreshold, 0.0, 1.0);
                long sig = ((long)f.ChargeThreshold * 8 + (int)(frac * 4.0)) * 1000 + f.ExtraMarbles;
                if (sig != _hudSig[t]) { _hudSig[t] = sig; _hudStatic.QueueRedraw(); }
            }
        }
        QueueRedraw();
    }

    private DrawLayer _hudStatic;
    private readonly long[] _hudSig = { -1, -1, -1, -1 };

    private void TakeScreenshot(string path)
    {
        var tex = GetViewport()?.GetTexture();
        if (tex == null) { GD.PrintErr("[screenshot] viewport texture is null"); return; }
        var img = tex.GetImage();
        if (img == null) { GD.PrintErr("[screenshot] GetImage() returned null"); return; }
        img.SavePng(path);
        string ta = "";
        if (Right?.Factions != null)
            foreach (var f in Right.Factions)
                ta += string.Format(CultureInfo.InvariantCulture, "{0}{1:0.0}{2}", ta.Length > 0 ? "," : "", Mathf.RadToDeg(f.TurretAngle), f.PdActive ? "p" : "");
        GD.Print($"[screenshot] saved to {path} sim={Right?.SimTime:0.000} ta={ta}");
    }

    private void PrintStats()
    {
        if (Right?.Field == null) return;
        var inv = CultureInfo.InvariantCulture;
        string line = string.Format(inv, "[stats] t={0:0.0} sim={1:0.0} fps={2:0} parts={3} bullets={4} balls={5} bi={6:0} |",
            GameTime, Right.SimTime, Engine.GetFramesPerSecond(), Right.ParticleCount, Right.BulletCount, Right.Balls.Count, Right.BallIntake);
        for (int t = 0; t < TeamCount; t++)
        {
            var f = Right.Factions[t];
            var hb = f.ShieldHitBy;
            var sk = f.StormKilledBy;
            line += string.Format(inv, " {0}:{1:0.0}% mg={2:0} sh={3:0.00}M ch={4:0}+{5} hv={7:0.0}K sm={8:0.0}K pd={9} hb={10:0}/{11:0}/{12:0}/{13:0}/{14:0}K sk={15:0}/{16:0}/{17:0}K pp={18:0}K pi={19:0.0}K bc={20:0.0}K{6}",
                TeamNames[t], Right.Field.Count[t] / 10000.0, f.MgFire, f.Shield / 1e6, f.Charge, f.ExtraMarbles,
                f.Alive ? "" : "(亡)", Right.HarvestTotal[t] / 1000.0, Right.Field.SmoothNeutral[t] / 1000.0, f.PdShots,
                hb[0] / 1e3, hb[1] / 1e3, hb[2] / 1e3, hb[3] / 1e3, hb[4] / 1e3,
                sk[0] / 1e3, sk[1] / 1e3, sk[2] / 1e3, f.PdPaintLoss / 1e3, f.PartIncome / 1e3, f.BallCharge / 1e3);
        }
        foreach (var b in Right.Balls)
            if (!b.Dead)
                line += string.Format(inv, " | ball {0} v={1:0.00}M r={2:0.0} p=({3:0},{4:0}) abs={5} s={6:0}",
                    TeamNames[b.Team], b.Value / 1e6, b.Radius, b.Pos.X, b.Pos.Y, b.Absorbed, b.Vel.Length());
        GD.Print(line);
        if (RightArena.Radial)
            GD.Print(Right.RadialReport());
        if (RightArena.Prof)
            GD.Print(Right.ProfReport() + string.Format(inv, " | godot process={0:0.00} physics={1:0.00} draws={2:0} objs={3:0}",
                Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0,
                Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0,
                Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame),
                Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame)));
    }

    // =====================================================================
    //  左侧 → 右侧 资源路由
    // =====================================================================

    private void HandleLeftZone(int team, string zone, float value)
    {
        Right?.InjectZone(team, zone, value);
    }

    // =====================================================================
    //  HUD：粒子充能进度条 + 额外弹珠圆环
    // =====================================================================

    public override void _Draw()
    {
        if (Right == null || Right.Factions[0] == null) return;
        long _pt0 = RightArena.Prof ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        for (int t = 0; t < TeamCount; t++) DrawChargeDynamic(t, Right.Factions[t]);
        if (RightArena.Prof) RightArena.ProfX[4] += System.Diagnostics.Stopwatch.GetTimestamp() - _pt0;
    }

    /// <summary>每帧变化的部分：当前充能数值 + 进度条（暗线 + 发光段）。</summary>
    private void DrawChargeDynamic(int t, Faction f)
    {
        float x = HudX[t];
        bool top = HudTop[t];
        Color tc = TeamColors[t];
        Color rc = RightArena.RingColors[t];

        double frac = Math.Clamp(f.Charge / f.ChargeThreshold, 0.0, 1.0);
        string value = ((long)f.Charge).ToString("N0", CultureInfo.InvariantCulture);
        Fonts.DrawCentered(this, Fonts.Num, new Vector2(x, top ? TopValueY : BotValueY), value, 17, rc.Lerp(tc, 0.3f));

        Vector2 a = new Vector2(x, top ? TopBarZero : BotBarZero), b = new Vector2(x, top ? TopBarFull : BotBarFull);
        DrawLine(a, b, new Color(tc, 0.45f), 1.5f);
        Vector2 cur = a.Lerp(b, (float)frac);
        if (frac > 0.0005)
        {
            DrawLine(a, cur, new Color(rc, 0.10f), 9f);
            DrawLine(a, cur, new Color(rc, 0.22f), 5f);
            DrawLine(a, cur, rc.Lerp(Colors.White, 0.35f), 2f);
            DrawCircle(cur, 5f, new Color(rc, 0.18f));
            DrawCircle(cur, 2.2f, rc.Lerp(Colors.White, 0.6f));
        }
    }

    /// <summary>很少变的部分（画在 HudStatic 层）：标签、门槛、四分刻度、额外弹珠。</summary>
    private void DrawChargeStatic(CanvasItem ci, int t, Faction f)
    {
        float x = HudX[t];
        bool top = HudTop[t];
        bool labelsLeft = x < 1000f;          // 刻度文字放在外侧
        Color tc = TeamColors[t];
        Color rc = RightArena.RingColors[t];
        var inv = CultureInfo.InvariantCulture;

        double thr = f.ChargeThreshold;
        double frac = Math.Clamp(f.Charge / thr, 0.0, 1.0);
        string max = "/ " + ((long)thr).ToString("N0", inv);

        Fonts.DrawCentered(ci, Fonts.UiBold, new Vector2(x, top ? TopLabelY : BotLabelY), Loc.L("粒子充能", "Charge"), 14, HudText);
        Fonts.DrawCentered(ci, Fonts.UiBold, new Vector2(x, top ? TopMaxY : BotMaxY), max, 14, HudText);
        // 视频里「额外弹珠」更小、更暗
        Fonts.DrawCentered(ci, Fonts.UiBold, new Vector2(x, top ? TopMarbleLabelY : BotMarbleLabelY), Loc.L("额外弹珠", "Extra Marbles"), 10, HudTextDim);

        // 四分刻度：准星小点 + 数值
        Vector2 a = new Vector2(x, top ? TopBarZero : BotBarZero), b = new Vector2(x, top ? TopBarFull : BotBarFull);
        for (int q = 1; q <= 3; q++)
        {
            Vector2 p = a.Lerp(b, q * 0.25f);
            bool lit = frac >= q * 0.25f;
            Color dc = lit ? rc.Lerp(Colors.White, 0.3f) : new Color(tc, 0.75f);
            ci.DrawArc(p, 3.2f, 0f, Mathf.Tau, 16, new Color(dc, 0.8f), 1f, true);
            ci.DrawLine(p + new Vector2(-6f, 0f), p + new Vector2(-4f, 0f), dc, 1f);
            ci.DrawLine(p + new Vector2(4f, 0f), p + new Vector2(6f, 0f), dc, 1f);
            ci.DrawLine(p + new Vector2(0f, -6f), p + new Vector2(0f, -4f), dc, 1f);
            ci.DrawLine(p + new Vector2(0f, 4f), p + new Vector2(0f, 6f), dc, 1f);
            ci.DrawCircle(p, 1.3f, dc);
            string lab = GrowthBall.FormatValue(thr * q / 4.0);
            if (labelsLeft) Fonts.DrawRight(ci, Fonts.UiBold, p + new Vector2(-9f, 0f), lab, 11, HudText);
            else Fonts.DrawLeft(ci, Fonts.UiBold, p + new Vector2(9f, 0f), lab, 11, HudText);
        }

        // ── 额外弹珠（视频）：柔和的队伍色光晕 + 一圈细环 + 四角小点 + 两侧淡刻度，中间一颗玻璃弹珠 ──
        Vector2 c = new Vector2(x, top ? TopRingY : BotRingY);
        const float glowR = 34f;
        ci.DrawTextureRect(BallArt.Halo(), new Rect2(c.X - glowR, c.Y - glowR, 2f * glowR, 2f * glowR), false, new Color(rc, 0.5f));
        ci.DrawArc(c, 19.5f, 0f, Mathf.Tau, 64, new Color(rc.Lerp(Colors.White, 0.2f), 0.5f), 1f, true);
        Color dot = new Color(rc.Lerp(Colors.White, 0.2f), 0.25f);
        for (int sx = -1; sx <= 1; sx += 2)
        {
            for (int sy = -1; sy <= 1; sy += 2)
                ci.DrawCircle(c + new Vector2(sx * 26f, sy * 22f), 0.9f, dot, true, -1f, true);
            ci.DrawLine(c + new Vector2(sx * 23f, 0f), c + new Vector2(sx * 30f, 0f), new Color(rc, 0.3f), 1f, true);
            ci.DrawCircle(c + new Vector2(sx * 31f, 0f), 0.9f, dot, true, -1f, true);
        }
        const float mr = 14.5f;
        ci.DrawTextureRect(BallArt.Ball(tc.Darkened(0.2f)), new Rect2(c.X - mr, c.Y - mr, 2f * mr, 2f * mr), false);
        Fonts.DrawCentered(ci, Fonts.UiBold, c + new Vector2(0f, 0.5f), f.ExtraMarbles.ToString(inv), 17,
            Colors.White, 3, new Color(tc.Darkened(0.75f), 0.8f));
    }
}
