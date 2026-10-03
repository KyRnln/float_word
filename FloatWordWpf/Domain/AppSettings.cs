using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FloatWordWpf;

/// <summary>
/// 全部可持久化设置。JSON 键名与 Python 版 floatword_config.json 完全一致，
/// 因此两个版本可以共用同一个配置文件。
/// 未知键（例如 Python 版的 config_version / crisp）通过 Extra 原样保留，不会被清掉。
/// </summary>
public sealed class AppSettings
{
    // ---- 外观 ----
    [JsonPropertyName("dict")] public string Dict { get; set; } = "";
    [JsonPropertyName("bg_alpha")] public double BgAlpha { get; set; } = 80;
    [JsonPropertyName("text_alpha")] public double TextAlpha { get; set; } = 100;
    [JsonPropertyName("font_size")] public double FontSize { get; set; } = 28;
    [JsonPropertyName("phon_size")] public double PhonSize { get; set; } = 11;
    [JsonPropertyName("mean_size")] public double MeanSize { get; set; } = 13;
    [JsonPropertyName("text_color")] public string TextColor { get; set; } = Theme.Accent;
    [JsonPropertyName("hint_color")] public string HintColor { get; set; } = Theme.BgSoft;
    [JsonPropertyName("hint_alpha")] public double HintAlpha { get; set; } = 100;
    [JsonPropertyName("outline_w")] public double OutlineW { get; set; } = 2;
    [JsonPropertyName("outline_color")] public string OutlineColor { get; set; } = "#000000";
    [JsonPropertyName("font_family")] public string FontFamily { get; set; } = "Consolas";
    [JsonPropertyName("font_bold")] public bool FontBold { get; set; } = true;
    [JsonPropertyName("toolbar_pinned")] public bool ToolbarPinned { get; set; }
    [JsonPropertyName("show_dict_name")] public bool ShowDictName { get; set; }

    // ---- 发音 ----
    [JsonPropertyName("volume")] public double Volume { get; set; } = 100;
    [JsonPropertyName("rate")] public double Rate { get; set; }
    [JsonPropertyName("gain")] public double Gain { get; set; } = 100;
    [JsonPropertyName("voice")] public string Voice { get; set; } = "";
    [JsonPropertyName("fallback_tts")] public bool FallbackTts { get; set; }
    [JsonPropertyName("autoplay")] public bool Autoplay { get; set; } = true;
    [JsonPropertyName("speak_correct")] public bool SpeakCorrect { get; set; } = true;
    [JsonPropertyName("speak_wrong")] public bool SpeakWrong { get; set; } = true;

    // ---- 学习进度： 词典 -> 模式 -> 下标 ----
    [JsonPropertyName("progress")]
    public Dictionary<string, Dictionary<string, int>> Progress { get; set; } = new();

    /// <summary>
    /// 间隔复习状态：词典 -> 单词 -> 进度。
    /// 判定规则见 <see cref="JudgeReview"/>。
    /// </summary>
    [JsonPropertyName("learn")]
    public Dictionary<string, Dictionary<string, WordProgress>> Learn { get; set; } = new();

    /// <summary>保留 Python 版特有的键，避免来回切换时丢失。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extra { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options)
                        ?? new AppSettings();
                s.MigrateHistory();
                return s;
            }
        }
        catch
        {
            // 配置损坏时用默认值，不影响启动
        }
        return new AppSettings();
    }

    /// <summary>
    /// 旧版的学习记录存在 history（词典 -> 单词 -> 学习日期），没有阶段概念。
    /// 首次升级时把它们迁移成阶段 0（学习中），这样已学过的词能继续走新的复习流程。
    /// </summary>
    private void MigrateHistory()
    {
        if (Learn.Count > 0) return;
        if (!Extra.TryGetValue("history", out var h) || h.ValueKind != JsonValueKind.Object) return;

        foreach (var dictProp in h.EnumerateObject())
        {
            var m = LearnOf(dictProp.Name);
            foreach (var wordProp in dictProp.Value.EnumerateObject())
            {
                m[wordProp.Name] = new WordProgress
                {
                    Stage = 0,
                    Learned = wordProp.Value.GetString() ?? Today()
                };
            }
        }
    }

    public void Save(string path)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // 写盘失败（例如目录只读）不应影响使用
        }
    }

    // ---------- 进度 ----------
    public int GetIndex(string dict, string mode)
        => Progress.TryGetValue(dict, out var m) && m.TryGetValue(mode, out var i) ? i : 0;

    public void SetIndex(string dict, string mode, int index)
    {
        if (!Progress.TryGetValue(dict, out var m))
            Progress[dict] = m = new Dictionary<string, int>();
        m[mode] = index;
    }

    // ---------- 学习记录 / 间隔复习 ----------
    private static string Today() => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string PlusDays(int days) =>
        DateTime.Today.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateTime? ParseDate(string s) =>
        DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                               DateTimeStyles.None, out var d) ? d : null;

    private Dictionary<string, WordProgress> LearnOf(string dict)
    {
        if (!Learn.TryGetValue(dict, out var m))
            Learn[dict] = m = new Dictionary<string, WordProgress>(StringComparer.OrdinalIgnoreCase);
        return m;
    }

    public WordProgress? GetProgress(string dict, string word) =>
        Learn.TryGetValue(dict, out var m) && m.TryGetValue(word, out var p) ? p : null;

    /// <summary>学习模式下打过卡：进入阶段 0（学习中），记录学习日期。已有进度则不覆盖。</summary>
    public void MarkLearned(string dict, string word)
    {
        var m = LearnOf(dict);
        if (!m.TryGetValue(word, out var p))
            m[word] = p = new WordProgress();
        p.Learned = Today();
    }

    /// <summary>
    /// 组成今天的复习队列：
    ///   1) 阶段 0（学习中）且最近 3 天内学过、今天还没通过的词 —— 连续 3 天默写期
    ///   2) 阶段 1（首次成功）已到期的词 —— 10 天后随机取 10 个
    ///   3) 阶段 2（二次成功）已到期的词 —— 30 天后再考
    /// </summary>
    public List<string> BuildReviewQueue(string dict)
    {
        var result = new List<string>();
        if (!Learn.TryGetValue(dict, out var m)) return result;

        var today = DateTime.Today;
        var todayStr = Today();
        var stage1 = new List<string>();

        foreach (var (word, p) in m)
        {
            switch (p.Stage)
            {
                case 0:
                    // 学习期：每天都要默写一次，直到连续 3 天无提示通过
                    // （不包括今天已判定过的，避免同一天重复出现）
                    if (p.LastOk != todayStr) result.Add(word);
                    break;

                case 1:
                    // 首次成功：满 10 天后每天随机取 10 个重新加入复习
                    if (ParseDate(p.Due) is { } d1 && d1.Date <= today) stage1.Add(word);
                    break;

                case 2:
                    // 二次成功：满 30 天后再默写一次
                    if (ParseDate(p.Due) is { } d2 && d2.Date <= today) result.Add(word);
                    break;
            }
        }

        // 首次成功的词：每次只随机取 10 个重新加入复习
        var rng = new Random();
        for (int i = stage1.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (stage1[i], stage1[j]) = (stage1[j], stage1[i]);
        }
        result.AddRange(stage1.Take(10));

        return result;
    }

    /// <summary>
    /// 复习默写正确时的判定。hinted = 本次是否用过提示。
    /// 返回一句给用户看的结果说明（空串表示无需提示）。
    ///
    /// 规则：
    ///   · **用了提示** → 连续天数清零，今天不再计入（这是唯一的"重置"来源）
    ///   · 阶段 0：连续无提示通过满 3 天 → 阶段 1（首次学习成功），10 天后复习
    ///   · 阶段 1：通过 → 阶段 2（二次学习成功），30 天后复习
    ///   · 阶段 2：通过 → 阶段 3（学习完成）
    /// </summary>
    public string JudgeReview(string dict, string word, bool hinted)
    {
        var p = P(dict, word);
        var today = Today();

        if (hinted)
        {
            p.Streak = 0;
            p.LastOk = today;          // 今天不再计入
            return "用了提示：连续天数已清零";
        }

        switch (p.Stage)
        {
            case 0:
                if (p.LastOk == today) return "今天已经通过过了";
                // "连续"：上次通过必须是昨天；断档则今天从头算
                if (p.LastOk.Length > 0
                    && ParseDate(p.LastOk) is { } last
                    && last.Date < DateTime.Today.AddDays(-1))
                {
                    p.Streak = 0;
                }
                p.Streak++;
                p.LastOk = today;
                if (p.Streak >= 3)
                {
                    p.Stage = 1;
                    p.Due = PlusDays(10);
                    return "连续 3 天通过 → 首次学习成功，10 天后复习";
                }
                return $"连续 {p.Streak}/3 天";

            case 1:
                p.Stage = 2;
                p.Due = PlusDays(30);
                return "复习通过 → 二次学习成功，30 天后复习";

            case 2:
                p.Stage = 3;
                p.Due = "";
                return "复习通过 → 学习完成 ✓";

            default:
                return "已完成";
        }
    }

    /// <summary>
    /// 复习默写错误时的判定。
    /// 阶段 0（学习期）的拼错**不影响连续天数**（只有提示会清零）；
    /// 阶段 1/2 的复习错则打回阶段 0，需要重新学。
    /// </summary>
    public string JudgeWrong(string dict, string word)
    {
        var p = P(dict, word);
        if (p.Stage == 0) return "";

        p.Stage = 0;
        p.Streak = 0;
        p.LastOk = "";
        p.Due = "";
        return "默写错误 → 回到未学习，需要重新学";
    }

    private WordProgress P(string dict, string word)
    {
        var m = LearnOf(dict);
        if (!m.TryGetValue(word, out var p))
            m[word] = p = new WordProgress();
        return p;
    }

    /// <summary>统计某本词典中处于各阶段的单词数，索引 0~3 分别对应阶段 0/1/2/3。</summary>
    public int[] StageCounts(string dict)
    {
        var c = new int[4];
        if (!Learn.TryGetValue(dict, out var m)) return c;
        foreach (var p in m.Values)
            if (p.Stage is >= 0 and < 4) c[p.Stage]++;
        return c;
    }

    /// <summary>词典位置旁边显示的学习情况。</summary>
    public static string StatusText(WordProgress? p)
    {
        if (p is null) return "未学";
        return p.Stage switch
        {
            0 => p.Streak > 0 ? $"连续 {p.Streak}/3 天" : "学习中",
            1 => "首次成功",
            2 => "二次成功",
            _ => "已完成"
        };
    }

    public void Reset()
    {
        var progress = Progress;
        var extra = Extra;
        var fresh = new AppSettings { Progress = progress, Extra = extra };
        foreach (var p in typeof(AppSettings).GetProperties())
            if (p.CanWrite && p.Name != nameof(Progress) && p.Name != nameof(Extra))
                p.SetValue(this, p.GetValue(fresh));
    }
}

/// <summary>
/// 单个单词的间隔复习进度。
///
/// 阶段流转：
///   0 学习中 ──连续 3 天无提示默写通过──> 1 首次学习成功 ──10 天后复习通过──> 2 二次学习成功 ──30 天后复习通过──> 3 学习完成
///
/// 重置规则：**只有用提示会清零连续天数**；学习期（阶段 0）拼错不影响连续天数。
/// 阶段 1/2 的复习默写错误则打回阶段 0，需要重新学。
/// </summary>
public sealed class WordProgress
{
    /// <summary>0 学习中 / 1 首次成功 / 2 二次成功 / 3 完成。</summary>
    [JsonPropertyName("stage")] public int Stage { get; set; }

    /// <summary>连续无提示默写通过的天数（满 3 天进入阶段 1）。</summary>
    [JsonPropertyName("streak")] public int Streak { get; set; }

    /// <summary>最近一次学习日期（学习模式打卡时写入）。</summary>
    [JsonPropertyName("learned")] public string Learned { get; set; } = "";

    /// <summary>最近一次判定为通过的天，防止同一天重复计数。</summary>
    [JsonPropertyName("last_ok")] public string LastOk { get; set; } = "";

    /// <summary>下次该复习的日期（阶段 1 为 +10 天，阶段 2 为 +30 天）。</summary>
    [JsonPropertyName("due")] public string Due { get; set; } = "";
}
