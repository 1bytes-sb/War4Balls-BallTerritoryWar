using System.Collections.Generic;

/// <summary>
/// 界面语言：中文（默认）/ English。只管显示出来的字；日志、阵营名 / 槽名 / 提示标题这些逻辑里比较用的键一直是中文。
/// 用法：using static Loc; 然后 L("中文", "English")。在游戏设置里切换，存在 settings.cfg 的 [ui] lang（调试用 --lang=en|zh）。
/// </summary>
public static class Loc
{
    public static bool En;

    public static string L(string zh, string en) => En ? en : zh;

    private static readonly string[] TeamEn = { "Red", "Green", "Blue", "Yellow" };
    private static readonly string[] ZoneEn = { "Storm", "MG", "Shield", "Ball", "Sniper" };

    /// <summary>阵营名（Main.TeamNames 的显示版）。</summary>
    public static string Team(int t) => Team(t, En);
    public static string Team(int t, bool en) => en ? TeamEn[t] : Main.TeamNames[t];

    /// <summary>左边的槽名（LeftArena.ZoneNames 的显示版）。</summary>
    public static string Zone(int k) => En ? ZoneEn[k] : LeftArena.ZoneNames[k];

    /// <summary>右边阵营头上的提示标题（Faction.AddNote 的 title）。</summary>
    private static readonly Dictionary<string, string> TermEn = new()
    {
        ["狙击火力"] = "Sniper Fire",
        ["机枪火力"] = "MG Fire",
        ["护盾增幅"] = "Shield Boost",
        ["风暴火力"] = "Storm Fire",
        ["大球火力"] = "Big Ball Fire",
        ["阵营覆灭"] = "Wiped Out",
        ["复活护盾"] = "Revival Shield",
        // 资金模式平仓 / 结算的原因（Market.Close 的 why）
        ["平仓"] = "Closed",
        ["强平"] = "Liquidated",
        ["结算"] = "Settled",
        ["胜利结算"] = "Settled (win)",
        ["淘汰结算"] = "Settled (out)",
        ["重开平仓"] = "Closed (restart)",
        ["退出平仓"] = "Closed (exit)",
    };

    public static string Term(string zh) => Term(zh, En);
    public static string Term(string zh, bool en) => en && zh != null && TermEn.TryGetValue(zh, out var e) ? e : zh;

    // 交易面板「胜率构成」的组名和说明（中文版在 WinModel.GroupNames / GroupTips）
    private static readonly string[] GroupNamesEn = { "Territory", "Shield", "MG Fire", "Sniper Fire", "Storm Fire", "Bullets", "Big Balls", "Marbles" };

    private static readonly string[] GroupTipsEn =
    {
        "Territory share among the living teams, and how much of the 150 px around the team's centre it owns",
        "Shield value; extra penalty once the shield is gone",
        "MG fire pool",
        "Sniper fire pool and sniper shots in flight",
        "Storm fire pool (rings not fired yet) and storm bullets in flight",
        "Own MG bullets in flight (plus); enemy bullets within 300 px of the team's centre (minus)",
        "Own big balls and big-ball fire pool (plus); enemy big balls within 300 px of the team's centre (minus)",
        "Own marbles on the left board (expected value, incl. multipliers they may still hit, counted in total resources), whether the team can still revive, extra marbles and charge progress",
    };

    public static string Group(int g) => En ? GroupNamesEn[g] : WinModel.GroupNames[g];
    public static string GroupTip(int g) => En ? GroupTipsEn[g] : WinModel.GroupTips[g];
}
