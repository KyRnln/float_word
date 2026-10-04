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
    // 默认值 = 作者当前使用的配置。
    // 词典默认仍为 CET4：Dict 留空时取词典列表的第一个（排序后即 CET4）。
    [JsonPropertyName("dict")] public string Dict { get; set; } = "";
    [JsonPropertyName("bg_alpha")] public double BgAlpha { get; set; } = 0;
    [JsonPropertyName("text_alpha")] public double TextAlpha { get; set; } = 100;
    [JsonPropertyName("font_size")] public double FontSize { get; set; } = 72;
    [JsonPropertyName("phon_size")] public double PhonSize { get; set; } = 25;
    [JsonPropertyName("phon_outline_w")] public double PhonOutlineW { get; set; } = 1;
    [JsonPropertyName("phon_outline_color")] public string PhonOutlineColor { get; set; } = "#1F1F1F";
    [JsonPropertyName("phon_color")] public string PhonColor { get; set; } = Theme.Accent;
    [JsonPropertyName("phon_font_family")] public string PhonFontFamily { get; set; } = Theme.MonoFontStack;
    [JsonPropertyName("phon_bold")] public bool PhonBold { get; set; }
    /// <summary>AI 台词里命中当前单词时的高亮色（跟随「发音/台词」组）。</summary>
    [JsonPropertyName("quote_hl_color")] public string QuoteHlColor { get; set; } = "#FCE100";
    [JsonPropertyName("mean_size")] public double MeanSize { get; set; } = 25;
    [JsonPropertyName("mean_outline_w")] public double MeanOutlineW { get; set; } = 1;
    [JsonPropertyName("mean_outline_color")] public string MeanOutlineColor { get; set; } = "#1F1F1F";
    [JsonPropertyName("mean_color")] public string MeanColor { get; set; } = Theme.Accent;
    [JsonPropertyName("mean_font_family")] public string MeanFontFamily { get; set; } = Theme.MonoFontStack;
    [JsonPropertyName("mean_bold")] public bool MeanBold { get; set; }
    /// <summary>单词颜色（注释 / 发音各有自己的 mean_color / phon_color）。</summary>
    [JsonPropertyName("text_color")] public string TextColor { get; set; } = Theme.Accent;
    [JsonPropertyName("hint_color")] public string HintColor { get; set; } = Theme.BgSoft;
    [JsonPropertyName("hint_alpha")] public double HintAlpha { get; set; } = 50;
    [JsonPropertyName("outline_w")] public double OutlineW { get; set; } = 1;
    [JsonPropertyName("outline_color")] public string OutlineColor { get; set; } = "#2B2B2B";
    [JsonPropertyName("font_family")] public string FontFamily { get; set; } = Theme.MonoFontStack;
    [JsonPropertyName("font_bold")] public bool FontBold { get; set; } = true;
    [JsonPropertyName("toolbar_pinned")] public bool ToolbarPinned { get; set; }
    [JsonPropertyName("show_dict_name")] public bool ShowDictName { get; set; } = true;

    /// <summary>
    /// 界面主题：system / dark / light（默认跟随系统）。
    /// 只影响设置窗口与工具栏等 Fluent 控件；浮窗的文字颜色仍在「外观」里单独配置。
    /// </summary>
    [JsonPropertyName("ui_theme")] public string UiTheme { get; set; } = "system";

    // ---- 窗口位置（屏幕绝对坐标，随配置一起保存 / 备份，换电脑可一起恢复）----
    [JsonPropertyName("win_left")] public double? WinLeft { get; set; }
    [JsonPropertyName("win_top")] public double? WinTop { get; set; }

    // ---- 发音 ----
    [JsonPropertyName("volume")] public double Volume { get; set; } = 60;
    [JsonPropertyName("rate")] public double Rate { get; set; } = -3;
    [JsonPropertyName("gain")] public double Gain { get; set; } = 100;
    [JsonPropertyName("voice")] public string Voice { get; set; } = "en_US-lessac-medium";
    [JsonPropertyName("fallback_tts")] public bool FallbackTts { get; set; }
    [JsonPropertyName("autoplay")] public bool Autoplay { get; set; } = true;
    [JsonPropertyName("speak_correct")] public bool SpeakCorrect { get; set; } = true;
    [JsonPropertyName("speak_wrong")] public bool SpeakWrong { get; set; } = true;

    // ---- AI 台词（OpenAI 兼容接口）----
    [JsonPropertyName("ai_enabled")] public bool AiEnabled { get; set; }
    [JsonPropertyName("ai_base_url")] public string AiBaseUrl { get; set; } = "https://api.openai.com/v1";
    [JsonPropertyName("ai_api_key")] public string AiApiKey { get; set; } = "";
    [JsonPropertyName("ai_model")] public string AiModel { get; set; } = "gpt-4o-mini";

    /// <summary>
    /// 是否让 AI 请求走系统代理。默认**直连** ——
    /// 国内 AI 服务（DeepSeek / 通义 / Ollama 等）直连最稳，不依赖代理软件是否开着；
    /// 用 OpenAI 等国外服务时再打开（且要保证代理软件在运行）。
    /// </summary>
    [JsonPropertyName("ai_use_system_proxy")] public bool AiUseSystemProxy { get; set; }

    /// <summary>单词 → 台词（结构化）。缓存起来避免重复请求 API。</summary>
    /// <remarks>
    /// 注意：旧版本这里是「单词 → 字符串」的 ai_cache。类型变更会让反序列化抛异常、
    /// 连带把学习进度一起重置，所以改用新键名 ai_quotes —— 旧的 ai_cache 会落进 Extra 原样保留。
    /// </remarks>
    [JsonPropertyName("ai_quotes")]
    public Dictionary<string, MovieQuote> AiQuotes { get; set; } = new();

    // ---------- WebDAV 数据备份 ----------
    /// <summary>WebDAV 服务器根地址。默认坚果云。</summary>
    [JsonPropertyName("webdav_url")] public string WebDavUrl { get; set; } = WebDavService.NutstoreUrl;
    /// <summary>备份文件所在的远端目录（自动创建）。</summary>
    [JsonPropertyName("webdav_dir")] public string WebDavDir { get; set; } = "floatword";
    /// <summary>WebDAV 账号。坚果云填登录邮箱。</summary>
    [JsonPropertyName("webdav_user")] public string WebDavUser { get; set; } = "";
    /// <summary>WebDAV 密码。坚果云用网页端生成的「应用密码」，不是登录密码。</summary>
    [JsonPropertyName("webdav_pass")] public string WebDavPass { get; set; } = "";
    /// <summary>退出程序时自动把配置备份上去（未填账号密码时自动跳过）。</summary>
    [JsonPropertyName("webdav_auto")] public bool WebDavAuto { get; set; } = true;
    /// <summary>上次成功备份的时间，仅用于显示。</summary>
    [JsonPropertyName("webdav_last")] public string WebDavLast { get; set; } = "";

    // ---------- 学习节奏 / 统计 ----------
    /// <summary>「学习日」的起始小时：在此之前算作前一天（默认 4 点，避开凌晨跨天）。</summary>
    [JsonPropertyName("day_start_hour")] public int DayStartHour { get; set; } = 4;
    /// <summary>每日新学目标（仅用于统计展示）。</summary>
    [JsonPropertyName("daily_goal")] public int DailyGoal { get; set; } = 20;
    /// <summary>已完成（阶段 3）的词是否定期抽查回炉。</summary>
    [JsonPropertyName("review_completed")] public bool ReviewCompleted { get; set; } = true;
    /// <summary>每日统计：日期 -> 当天新学 / 复习通过数（只保留最近 90 天）。</summary>
    [JsonPropertyName("stats")] public Dictionary<string, DayStat> Stats { get; set; } = new();

    /// <summary>已完成词的抽查周期（天）。</summary>
    public const int MaintainDays = 180;

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
                s.Migrate();
                return s;
            }
        }
        catch
        {
            // 配置损坏时用默认值，不影响启动
        }
        return new AppSettings();
    }

    /// <summary>加载后的兼容处理：旧字段迁移 + 补齐新字段的默认值。</summary>
    private void Migrate()
    {
        DayStartHour = Math.Clamp(DayStartHour, 0, 12);
        DailyGoal = Math.Clamp(DailyGoal, 1, 999);

        // 主题取值只允许 system / dark / light
        if (UiTheme is not ("dark" or "light")) UiTheme = "system";

        // 全局只用等宽字体：旧配置里的非等宽字体一律收敛到等宽
        FontFamily = Theme.CoerceMono(FontFamily);
        MeanFontFamily = Theme.CoerceMono(MeanFontFamily);
        PhonFontFamily = Theme.CoerceMono(PhonFontFamily);

        if (Learn.Count == 0) MigrateHistory();

        // 阶段 3 但没有抽查日期（旧数据）→ 从今天起排一个抽查周期
        foreach (var m in Learn.Values)
            foreach (var p in m.Values)
                if (p.Stage >= 3 && p.Due.Length == 0) p.Due = PlusDays(MaintainDays);

        PruneStats();
    }

    /// <summary>
    /// 旧版的学习记录存在 history（词典 -> 单词 -> 学习日期），没有阶段概念。
    /// 首次升级时把它们迁移成阶段 0（学习中），这样已学过的词能继续走新的复习流程。
    /// </summary>
    private void MigrateHistory()
    {
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
            File.WriteAllText(path, ToJson());
        }
        catch
        {
            // 写盘失败（例如目录只读）不应影响使用
        }
    }

    /// <summary>序列化成配置文件的内容（WebDAV 备份也用它，保证与本地文件完全一致）。</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>把 JSON 文本解析成配置，失败返回 null（用于校验 WebDAV 备份内容是否可用）。</summary>
    public static AppSettings? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<AppSettings>(json, Options); }
        catch { return null; }
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
    /// <summary>当前「学习日」：凌晨 <see cref="DayStartHour"/> 点之前算作前一天。</summary>
    public DateTime StudyDate()
    {
        var now = DateTime.Now;
        return now.TimeOfDay.TotalHours < DayStartHour ? now.Date.AddDays(-1) : now.Date;
    }

    private string Today() => Key(StudyDate());

    private string PlusDays(int days) => Key(StudyDate().AddDays(days));

    private static string Key(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateTime? ParseDate(string s) =>
        DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                               DateTimeStyles.None, out var d) ? d : null;

    // ---------- 每日统计 / 打卡 ----------
    /// <summary>今日新学词数。</summary>
    public int TodayLearned => Stats.TryGetValue(Key(StudyDate()), out var st) ? st.Learned : 0;

    /// <summary>今日复习通过数。</summary>
    public int TodayReviewed => Stats.TryGetValue(Key(StudyDate()), out var st) ? st.Reviewed : 0;

    /// <summary>打卡天数：从今天（今天还没活动就从昨天）往前，连续有学习或复习活动的天数。</summary>
    public int StreakDays()
    {
        var d = StudyDate();
        if (!HasActivity(d)) d = d.AddDays(-1);
        int n = 0;
        while (HasActivity(d)) { n++; d = d.AddDays(-1); }
        return n;
    }

    private bool HasActivity(DateTime d) =>
        Stats.TryGetValue(Key(d), out var st) && (st.Learned > 0 || st.Reviewed > 0);

    private DayStat TodayStat()
    {
        var k = Key(StudyDate());
        if (!Stats.TryGetValue(k, out var st)) Stats[k] = st = new DayStat();
        return st;
    }

    /// <summary>学习模式新学一个词时调用（只统计首次学习）。</summary>
    public void AddLearnedStat() => TodayStat().Learned++;

    /// <summary>复习通过一个词时调用。</summary>
    public void AddReviewedStat() => TodayStat().Reviewed++;

    /// <summary>只保留最近 90 天的统计，避免配置无限膨胀。</summary>
    private void PruneStats()
    {
        if (Stats.Count <= 90) return;
        var cutoff = StudyDate().AddDays(-89);
        foreach (var k in Stats.Keys.ToList())
            if (ParseDate(k) is { } d && d.Date < cutoff) Stats.Remove(k);
    }

    private Dictionary<string, WordProgress> LearnOf(string dict)
    {
        if (!Learn.TryGetValue(dict, out var m))
            Learn[dict] = m = new Dictionary<string, WordProgress>(StringComparer.OrdinalIgnoreCase);
        return m;
    }

    public WordProgress? GetProgress(string dict, string word) =>
        Learn.TryGetValue(dict, out var m) && m.TryGetValue(word, out var p) ? p : null;

    // ---------- AI 台词缓存 ----------
    public MovieQuote? GetAiQuote(string word) =>
        AiQuotes.TryGetValue(word, out var q) && q.HasContent ? q : null;

    public void SetAiQuote(string word, MovieQuote quote) => AiQuotes[word] = quote;

    /// <summary>清空全部 AI 台词缓存（不影响学习进度与外观设置），下次显示时重新请求。</summary>
    public void ClearAiQuotes() => AiQuotes.Clear();

    /// <summary>
    /// 把整体配色一键切到深色 / 浅色预设：界面主题 + 浮窗的文字、描边、提示框、台词高亮色。
    /// 只改配色，不动字号 / 字体 / 透明度。两者一起切，保证卡片底色与文字颜色始终搭配。
    /// </summary>
    public void ApplyAppearancePreset(bool light)
    {
        UiTheme = light ? "light" : "dark";
        if (light)
        {
            // 浅色底：文字用深色，描边用白色（浅底上的"外发光"），参考 Fluent 浅色强调色
            TextColor = "#005FB8";
            OutlineColor = "#FFFFFF";
            MeanColor = "#1A1A1A";
            MeanOutlineColor = "#FFFFFF";
            PhonColor = "#005FB8";
            PhonOutlineColor = "#FFFFFF";
            HintColor = "#E5E5E5";
            QuoteHlColor = "#B45309";
        }
        else
        {
            // 深色底：与程序默认值一致
            TextColor = Theme.Accent;
            OutlineColor = "#2B2B2B";
            MeanColor = Theme.Accent;
            MeanOutlineColor = "#1F1F1F";
            PhonColor = Theme.Accent;
            PhonOutlineColor = "#1F1F1F";
            HintColor = Theme.BgSoft;
            QuoteHlColor = "#FCE100";
        }
    }

    /// <summary>学习模式下打过卡：进入阶段 0（学习中），记录学习日期。已有进度则不覆盖。</summary>
    public void MarkLearned(string dict, string word)
    {
        var m = LearnOf(dict);
        if (!m.TryGetValue(word, out var p))
        {
            m[word] = p = new WordProgress();
            AddLearnedStat();          // 只统计"首次学习"的词
        }
        p.Learned = Today();
    }

    /// <summary>
    /// 把单词移出复习系统（需重新学习）。默写连续失败时使用：
    /// 该词不会再出现在复习队列里，直到重新学习（再次 <see cref="MarkLearned"/>）为止。
    /// </summary>
    public void RemoveFromReview(string dict, string word)
    {
        if (Learn.TryGetValue(dict, out var m)) m.Remove(word);
    }

    /// <summary>
    /// 组成今天的复习队列（各阶段都不设数量上限，按各自的节奏来）：
    ///   1) 阶段 0（学习中）今天还没通过的词 —— 每天必默写
    ///   2) 阶段 1（首次成功）已到期的词 —— 10 天后加入
    ///   3) 阶段 2（二次成功）已到期的词 —— 30 天后加入
    /// </summary>
    public List<string> BuildReviewQueue(string dict)
    {
        var result = new List<string>();
        if (!Learn.TryGetValue(dict, out var m)) return result;

        var today = StudyDate();
        var todayStr = Today();

        foreach (var (word, p) in m)
        {
            switch (p.Stage)
            {
                case 0:
                    // 学习期：每天都要默写一次，直到累计 3 次无提示通过
                    // （不包括今天已判定过的，避免同一天重复出现）
                    if (p.LastOk != todayStr) result.Add(word);
                    break;

                case 1:
                    // 首次成功：满 10 天后加入复习；当天已判定过的不再重复出现
                    // （用提示不推进阶段，若不去重会在同一天里反复考同一个词）
                    if (ParseDate(p.Due) is { } d1 && d1.Date <= today && p.LastOk != todayStr) result.Add(word);
                    break;

                case 2:
                    // 二次成功：满 30 天后再加入复习；同样按天去重
                    if (ParseDate(p.Due) is { } d2 && d2.Date <= today && p.LastOk != todayStr) result.Add(word);
                    break;

                case 3:
                    // 已完成：定期抽查回炉，防止长期遗忘（可在设置里关闭）
                    if (ReviewCompleted
                        && ParseDate(p.Due) is { } d3 && d3.Date <= today
                        && p.LastOk != todayStr)
                        result.Add(word);
                    break;
            }
        }

        // 顺序由 StartReview 统一打乱，这里不再抽签
        return result;
    }

    /// <summary>
    /// 复习默写正确时的判定。hinted = 本次是否用过提示。
    /// 返回一句给用户看的结果说明（空串表示无需提示）。
    ///
    /// 规则：
    ///   · **用了提示** → 进度清零，今天不再计入（这是唯一的"重置"来源）
    ///   · 阶段 0：累计无提示通过满 3 次 → 阶段 1（首次学习成功），10 天后复习
    ///     （不要求自然日连续，中间断档不清零；每天最多计 1 次）
    ///   · 阶段 1：通过 → 阶段 2（二次学习成功），30 天后复习
    ///   · 阶段 2：通过 → 阶段 3（学习完成），6 个月后抽查
    ///   · 阶段 3：抽查通过 → 保持完成，再等 6 个月
    /// </summary>
    public string JudgeReview(string dict, string word, bool hinted)
    {
        var p = P(dict, word);
        var today = Today();

        if (hinted)
        {
            p.Streak = 0;
            p.LastOk = today;          // 今天不再计入
            return "用了提示：通过次数已清零";
        }

        switch (p.Stage)
        {
            case 0:
                if (p.LastOk == today) return "今天已经通过过了";
                // 不要求自然日连续：断档也不清零，通过一次累计一次（每天最多 1 次）
                p.Streak++;
                p.LastOk = today;
                AddReviewedStat();
                if (p.Streak >= 3)
                {
                    p.Stage = 1;
                    p.Due = PlusDays(10);
                    return "累计 3 次通过 → 首次学习成功，10 天后复习";
                }
                return $"已通过 {p.Streak}/3 次";

            case 1:
                p.Stage = 2;
                p.Due = PlusDays(30);
                p.LastOk = today;
                AddReviewedStat();
                return "复习通过 → 二次学习成功，30 天后复习";

            case 2:
                p.Stage = 3;
                p.Due = PlusDays(MaintainDays);
                p.LastOk = today;
                AddReviewedStat();
                return $"复习通过 → 学习完成 ✓（{MaintainDays / 30} 个月后抽查）";

            case 3:
                // 维护抽查通过：保持完成状态，重新排下一次
                p.Due = PlusDays(MaintainDays);
                p.LastOk = today;
                AddReviewedStat();
                return $"抽查通过 ✓（{MaintainDays / 30} 个月后再抽查）";

            default:
                return "已完成";
        }
    }

    /// <summary>
    /// 复习默写错误时的判定。hinted = 本次是否用过提示。
    /// 阶段 0（学习期）的单纯拼错不影响进度，但**用了提示就清零**（堵住"看提示再故意拼错"）；
    /// 阶段 1/2 的复习错则打回阶段 0，需要重新学。
    /// </summary>
    public string JudgeWrong(string dict, string word, bool hinted)
    {
        var p = P(dict, word);

        if (p.Stage == 0)
        {
            if (!hinted) return "";        // 单纯拼错：不影响
            p.Streak = 0;
            p.LastOk = Today();            // 用提示：清零，且今天不再计入
            return "用了提示：通过次数已清零";
        }

        p.Stage = 0;
        p.Streak = 0;
        p.LastOk = hinted ? Today() : "";
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
            0 => p.Streak > 0 ? $"已通过 {p.Streak}/3 次" : "学习中",
            1 => "首次成功",
            2 => "二次成功",
            _ => "已完成"
        };
    }

    /// <summary>
    /// 清空全部学习进度：各词典的学习位置 + 所有单词的 SRS 记录。
    /// 外观、发音等设置不受影响。
    /// </summary>
    public void ClearProgress()
    {
        Progress.Clear();
        Learn.Clear();
        Stats.Clear();      // 每日统计与打卡也一并清掉
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

/// <summary>某一天的统计：新学词数与复习通过数（用于每日目标和打卡）。</summary>
public sealed class DayStat
{
    [JsonPropertyName("learned")] public int Learned { get; set; }
    [JsonPropertyName("reviewed")] public int Reviewed { get; set; }
}

/// <summary>
/// 单个单词的间隔复习进度。
///
/// 阶段流转：
///   0 学习中 ──累计 3 次无提示默写通过──> 1 首次学习成功 ──10 天后复习通过──> 2 二次学习成功 ──30 天后复习通过──> 3 学习完成
///   阶段 3 每 6 个月抽查一次，抽查不过则打回阶段 0 重学。
///
/// 重置规则：**只有用提示会清零进度**（对错都清）；学习期（阶段 0）单纯拼错不影响。
/// 阶段 1/2 的复习默写错误则打回阶段 0，需要重新学。
/// 计次不要求自然日连续：断档不清零，每天最多计 1 次。
/// </summary>
public sealed class WordProgress
{
    /// <summary>0 学习中 / 1 首次成功 / 2 二次成功 / 3 完成。</summary>
    [JsonPropertyName("stage")] public int Stage { get; set; }

    /// <summary>阶段 0 里累计无提示通过的次数（满 3 次进入阶段 1，每天最多计 1 次）。</summary>
    [JsonPropertyName("streak")] public int Streak { get; set; }

    /// <summary>最近一次学习日期（学习模式打卡时写入）。</summary>
    [JsonPropertyName("learned")] public string Learned { get; set; } = "";

    /// <summary>最近一次判定为通过的天，防止同一天重复计数。</summary>
    [JsonPropertyName("last_ok")] public string LastOk { get; set; } = "";

    /// <summary>下次该复习的日期（阶段 1 为 +10 天、阶段 2 为 +30 天、阶段 3 为 +180 天抽查）。</summary>
    [JsonPropertyName("due")] public string Due { get; set; } = "";
}

/// <summary>
/// 一条电影台词及附带信息，展示成「台词 / 翻译 / · 《片名》 / （年份）」四行。
/// </summary>
public sealed class MovieQuote
{
    [JsonPropertyName("quote")] public string Quote { get; set; } = "";
    [JsonPropertyName("translation")] public string Translation { get; set; } = "";
    [JsonPropertyName("movie")] public string Movie { get; set; } = "";
    [JsonPropertyName("year")] public string Year { get; set; } = "";

    [JsonIgnore]
    public bool HasContent => Quote.Length > 0 || Movie.Length > 0;

    /// <summary>左列：台词原文 + 中文翻译（两行）。</summary>
    public string LeftLines()
    {
        var lines = new List<string>(2);
        if (Quote.Length > 0) lines.Add(Quote);
        if (Translation.Length > 0) lines.Add(Translation);
        return string.Join('\n', lines);
    }

    /// <summary>右列第一行：《片名》（年份单独一行、居中于这一行之下；分隔点由界面单独居中放置）。</summary>
    public string MovieLine() => Movie.Length > 0 ? $"《{Movie}》" : "";

    /// <summary>右列第二行：（年份）。</summary>
    public string YearLine() => Year.Length > 0 ? $"（{Year}）" : "";

    /// <summary>单列预览（设置页「测试」弹窗用）。</summary>
    public string ToLines()
    {
        var blocks = new List<string>(2);
        var a = LeftLines();
        if (a.Length > 0) blocks.Add(a);

        var right = new List<string>(2);
        if (MovieLine().Length > 0) right.Add("· " + MovieLine());
        if (YearLine().Length > 0) right.Add(YearLine());
        if (right.Count > 0) blocks.Add(string.Join('\n', right));

        return string.Join("\n\n", blocks);
    }
}
