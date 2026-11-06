using Godot;
using System;

/// <summary>
/// 补充资金用的题库：res://data/questions.txt，一行一题，tab 分列：分类、科目、题目、A、B、C、D、答案字母、解析
/// （文字里的换行写成 \n、反斜杠写成 \\）。第一次用到时读进来，题目用到时才拆。
/// 出题顺序：按 Wallet.QuizSeed 洗一次牌，出第 Wallet.QuizNext 张；整副出完换个种子重洗。
/// 题目来自 C-Eval、CMMLU（CC BY-NC-SA 4.0）和 MMLU 的中文翻译（MIT），整理脚本 _frames/s10/qconv.py。
/// </summary>
public static class QuestionBank
{
    public sealed class Question
    {
        public string Category, Subject, Text, Explanation;
        public readonly string[] Options = new string[4];
        public int Answer;                          // 0..3 = A..D
    }

    public static string Attribution => Loc.L("题目来自 C-Eval、CMMLU（CC BY-NC-SA 4.0）和 MMLU 中文翻译（MIT）",
                                              "Questions from C-Eval, CMMLU (CC BY-NC-SA 4.0) and the Chinese MMLU translation (MIT)");

    private const string DataPath = "res://data/questions.txt";

    private static string[] _lines;
    private static int[] _order;
    private static int _orderSeed;

    public static int Count
    {
        get
        {
            Load();
            return _lines.Length;
        }
    }

    private static void Load()
    {
        if (_lines != null) return;
        using var f = FileAccess.Open(DataPath, FileAccess.ModeFlags.Read);
        if (f == null)
        {
            GD.PrintErr($"[quiz] can't open {DataPath}: {FileAccess.GetOpenError()}");
            _lines = Array.Empty<string>();
            return;
        }
        _lines = f.GetAsText().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        GD.Print($"[quiz] {_lines.Length} questions");
    }

    /// <summary>现在该答的题（Wallet.QuizNext 那张）；题库读不到返回 null。</summary>
    public static Question Current()
    {
        Load();
        if (_lines.Length == 0) return null;
        if (Wallet.QuizSeed == 0 || Wallet.QuizNext >= _lines.Length)
            Wallet.QuizReshuffle((int)(GD.Randi() & 0x7fffffff) | 1);
        if (_order == null || _orderSeed != Wallet.QuizSeed || _order.Length != _lines.Length)
            Shuffle(Wallet.QuizSeed);
        return Parse(_lines[_order[Wallet.QuizNext]]);
    }

    private static void Shuffle(int seed)
    {
        var rng = new Random(seed);
        _order = new int[_lines.Length];
        for (int i = 0; i < _order.Length; i++) _order[i] = i;
        for (int i = _order.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (_order[i], _order[j]) = (_order[j], _order[i]);
        }
        _orderSeed = seed;
    }

    private static Question Parse(string line)
    {
        string[] c = line.TrimEnd('\r').Split('\t');
        var q = new Question();
        if (c.Length < 8)
        {
            q.Text = Loc.L("（这一行题目读不出来）", "(this question line couldn't be read)");
            for (int k = 0; k < 4; k++) q.Options[k] = "—";
            return q;
        }
        q.Category = c[0];
        q.Subject = c[1];
        q.Text = Unescape(c[2]);
        for (int k = 0; k < 4; k++) q.Options[k] = Unescape(c[3 + k]);
        q.Answer = Math.Clamp(c[7].Length > 0 ? c[7][0] - 'A' : 0, 0, 3);
        q.Explanation = c.Length > 8 ? Unescape(c[8]) : "";
        return q;
    }

    private static string Unescape(string s)
    {
        if (s.IndexOf('\\') < 0) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch == '\\' && i + 1 < s.Length)
            {
                char nx = s[i + 1];
                if (nx == 'n') { sb.Append('\n'); i++; continue; }
                if (nx == '\\') { sb.Append('\\'); i++; continue; }
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
