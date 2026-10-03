using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace FloatWordWpf;

public partial class MainWindow : Window
{
    /// <summary>学习模式下每学满几个词，就进入一次本组默写。</summary>
    private const int RoundSize = 3;

    /// <summary>本组默写里同一个词连续输错多少次，就亮出答案并打回重新学习。</summary>
    private const int MaxWrongStreak = 5;

    /// <summary>亮出答案的时长（毫秒）。</summary>
    private const int AutoHintMs = 3000;

    private enum Phase
    {
        /// <summary>学习：显示单词详情，边看边打。</summary>
        Study,
        /// <summary>默写：隐藏单词详情，听音/看释义拼写。</summary>
        Dictation,
        /// <summary>复习：把最近几天学过的词打乱后默写。</summary>
        Review
    }

    private readonly AppSettings _s;
    private readonly List<WordDictionary> _lib;
    private readonly PiperService _tts = new();
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _toolbarTimer;

    private SettingsWindow? _settingsWin;
    private int _dictIndex;
    private int _index;              // 在词典中的位置（持久化）
    private int _typed;

    private Phase _phase = Phase.Study;
    private readonly List<WordItem> _round = new();   // 本组已学的词（用于本组默写）
    private int _roundIndex;
    private readonly List<WordItem> _relearn = new(); // 本组默写连错 5 次、被退回需重新学习的词
    private readonly List<WordItem> _review = new();  // 复习队列（打乱后）
    private int _reviewIndex;

    private bool _busy;
    private bool _ready;
    private int _wrongStreak;        // 当前词连续输错次数（本组默写用）
    private bool _autoReveal;        // 连错满 5 次后自动亮出答案中

    private bool _hintHeld;          // 提示按钮是否正被按住（按住显示答案）
    private bool _hintUsed;          // 本词是否用过提示（用提示会清零连续天数）
    private string _verdict = "";    // 上一次复习判定结果（留到下次输入前）
    private bool _verdictBad;        // 判定结果是否为负向（用提示 / 默写错误）

    public MainWindow(AppSettings settings, List<WordDictionary> library)
    {
        _s = settings;
        _lib = library;
        InitializeComponent();

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _s.Save(AppPaths.ConfigFile); };

        _toolbarTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _toolbarTimer.Tick += (_, _) => { _toolbarTimer.Stop(); MaybeHideToolbar(); };
    }

    // ---------- 对外给设置窗口用 ----------
    public AppSettings Settings => _s;
    public List<WordDictionary> Library => _lib;
    public string CurrentDictName => Dict?.Name ?? "";

    private WordDictionary? Dict =>
        _lib.Count == 0 ? null : _lib[Math.Clamp(_dictIndex, 0, _lib.Count - 1)];

    /// <summary>当前正在练的词表：复习队列 / 本组 / 待重学 / 整本词典。</summary>
    private List<WordItem>? ActiveList =>
        _review.Count > 0 ? _review
        : _phase == Phase.Dictation && _round.Count > 0 ? _round
        : _relearn.Count > 0 ? _relearn
        : Dict?.Words;

    private int ActiveIndex
    {
        get
        {
            if (_review.Count > 0) return _reviewIndex;
            if (_phase == Phase.Dictation && _round.Count > 0) return _roundIndex;
            if (_relearn.Count > 0) return 0;   // 待重学的词一次只呈现一个
            return _index;
        }
    }

    private WordItem? Current
    {
        get
        {
            var list = ActiveList;
            if (list is null || list.Count == 0) return null;
            return list[Math.Clamp(ActiveIndex, 0, list.Count - 1)];
        }
    }

    /// <summary>
    /// 进度只记「学习」这一档：默写是本组学习流程中自动进入的一段，
    /// 复习是临时会话，两者都不单独存进度。
    /// </summary>
    private const string ProgressKey = "study";

    private int ClampIndex(int i) => Math.Clamp(i, 0, Math.Max(0, (Dict?.Words.Count ?? 1) - 1));

    // ---------- 启动 ----------
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        if (_lib.Count > 0)
        {
            int i = _lib.FindIndex(d => d.Name == _s.Dict);
            _dictIndex = i >= 0 ? i : 0;
            _s.Dict = CurrentDictName;
            _index = ClampIndex(_s.GetIndex(CurrentDictName, ProgressKey));
        }

        _ready = true;
        ApplySettings();
        Render();
        AnchorBottomRight();
        Activate();
        Focus();
        SpeakIfAuto();
    }

    // ---------- 设置 → 界面 ----------
    public void ApplySettings()
    {
        // 卡片半透明用"颜色的 alpha"，文字透明度用元素 Opacity —— 两者互不影响。
        // 用 CardBrush 而非 Brush：背景透明度拉到 0 时仍保留命中测试，否则空白处拖不动窗口。
        Card.Background = Theme.CardBrush(Theme.Bg, _s.BgAlpha / 100.0);

        // Fluent 的层次感来自 elevation 阴影；背景全透明时阴影要一起淡出，
        // 否则会看到一圈"悬空的暗色矩形"。
        ShadowLayer.Opacity = Math.Clamp(_s.BgAlpha / 100.0, 0, 1);

        double top = Math.Clamp(_s.TextAlpha / 100.0, 0, 1);
        Info.Opacity = Mean.Opacity = Phon.Opacity = Feedback.Opacity = Word.Opacity = top;

        var textColor = Theme.Brush(_s.TextColor);

        // 注释（释义）：字号 + 独立描边
        Mean.TextBrush = textColor;
        Mean.FontSize = _s.MeanSize;
        Mean.OutlineWidth = _s.MeanOutlineW;
        Mean.OutlineBrush = Theme.Brush(_s.MeanOutlineColor);

        // 发音（音标）：字号 + 独立描边
        Phon.TextBrush = textColor;
        Phon.FontSize = _s.PhonSize;
        Phon.OutlineWidth = _s.PhonOutlineW;
        Phon.OutlineBrush = Theme.Brush(_s.PhonOutlineColor);

        // 释义按词性分行，每行长短差别很大：给一个随屏幕自适应的上限，
        // 既让绝大多数词性行不折行，又不会把窗口撑到屏幕外。
        Mean.TextMaxWidth = Math.Clamp(SystemParameters.WorkArea.Width * 0.62, 480, 1400);

        Word.FontFamilyName = _s.FontFamily;
        Word.FontSize = _s.FontSize;
        Word.Bold = _s.FontBold;
        Word.OutlineWidth = _s.OutlineW;
        Word.TextBrush = textColor;
        Word.OutlineBrush = Theme.Brush(_s.OutlineColor);
        Word.HintBrush = Theme.Brush(_s.HintColor, _s.HintAlpha / 100.0);
        Word.Relayout();

        if (_s.ToolbarPinned) SetToolbarVisible(true);
    }

    /// <summary>设置窗口改完设置后调用。</summary>
    public void RefreshFromSettings()
    {
        ApplySettings();
        Render();
        ScheduleSave();
    }

    /// <summary>切换词库（并恢复该词库在当前模式下的进度）。</summary>
    public void ChangeDict(string name)
    {
        int i = _lib.FindIndex(d => d.Name == name);
        if (i < 0) return;

        _s.SetIndex(CurrentDictName, ProgressKey, _index);
        _dictIndex = i;
        _s.Dict = name;
        _index = ClampIndex(_s.GetIndex(name, ProgressKey));
        _round.Clear();
        _roundIndex = 0;
        _review.Clear();
        _reviewIndex = 0;
        _relearn.Clear();
        ResetTyped();
        Render();
        ScheduleSave();
        SpeakIfAuto();
    }

    /// <summary>重新扫描程序目录下的 dicts/（导入词典后调用）。</summary>
    public void ReloadLibrary()
    {
        var fresh = DictionaryLibrary.Load(AppPaths.AppDir);
        _lib.Clear();
        _lib.AddRange(fresh);

        int i = _lib.FindIndex(d => d.Name == _s.Dict);
        _dictIndex = i >= 0 ? i : 0;

        if (_lib.Count > 0)
        {
            _s.Dict = CurrentDictName;
            _index = ClampIndex(_s.GetIndex(_s.Dict, ProgressKey));
        }
        else
        {
            _index = 0;
        }

        _round.Clear();
        _roundIndex = 0;
        _review.Clear();
        _reviewIndex = 0;
        _relearn.Clear();
        ResetTyped();
        Render();
    }

    public void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>
    /// 清空全部学习进度，并把当前会话拉回「学习模式 + 词典第一个词」。
    /// 复习队列 / 本组默写都会作废，避免清空后残留的旧队列又写回进度。
    /// </summary>
    public void ClearProgress()
    {
        _s.ClearProgress();

        _round.Clear();
        _roundIndex = 0;
        _review.Clear();
        _reviewIndex = 0;
        _relearn.Clear();
        _phase = Phase.Study;
        _index = 0;

        ResetTyped();
        Remember();      // 写入新下标并触发保存
        Render();
    }

    // ---------- 渲染 ----------
    private void Render()
    {
        if (!_ready) return;

        var w = Current;
        if (w is null)
        {
            Info.Text = "";
            Mean.Text = "未找到词库：请在程序目录放置 dicts/*.json 或 words.json";
            Phon.Text = "";
            Feedback.Text = "";
            Word.Word = "";
            KeepCenterLater();
            return;
        }

        // 进度：复习显示队列位置，本组默写显示组内位置，重学显示剩余个数，否则显示词典位置
        string pos;
        if (_review.Count > 0) pos = $"复习 {_reviewIndex + 1}/{_review.Count}";
        else if (_phase == Phase.Dictation && _round.Count > 0) pos = $"本组 {_roundIndex + 1}/{_round.Count}";
        else if (_relearn.Count > 0) pos = $"重学 {_relearn.Count}";
        else pos = $"{_index + 1}/{Dict!.Words.Count}";

        // 词典位置后面跟该词的学习情况：未学 / 学习中 / 连续 N/3 天 / 首次成功 / 二次成功 / 已完成
        string status = AppSettings.StatusText(_s.GetProgress(Dict!.Name, w.Word));
        Info.Text = _s.ShowDictName ? $"{Dict!.Name} · {pos} · {status}" : $"{pos} · {status}";
        Mean.Text = w.Meaning;

        // 模式按钮显示当前模式，点击循环切换
        // 字形：E7BC 阅读 / E70F 编辑 / E81C 历史
        (ModeText.Text, ModeIcon.Text) = _phase switch
        {
            Phase.Study => ("学习", "\uE7BC"),
            Phase.Dictation => ("默写", "\uE70F"),
            _ => ("复习", "\uE81C")
        };

        // 学习模式始终显示；按住提示、连错 5 次自动亮答案、或拼对了都显示
        bool revealed = _phase == Phase.Study || _hintHeld || _autoReveal || _typed >= w.Word.Length;
        Phon.Text = revealed ? w.Phonetic : "";
        Phon.TextBrush = revealed ? Theme.Brush(_s.TextColor) : Theme.Brush(Theme.Muted);

        // 默写/复习用等宽槽位排版（占位横线等宽、间距一致）；学习模式按字母真实宽度
        Word.UniformCells = _phase != Phase.Study;
        Word.Word = w.Word;
        Word.Typed = _typed;
        Word.Reveal = revealed;
        Word.Error = false;

        // 提示按钮只在复习模式下出现
        HintBtn.Visibility = _phase == Phase.Review ? Visibility.Visible : Visibility.Collapsed;

        KeepCenterLater();
    }

    private void Remember()
    {
        if (Dict is null) return;
        // 复习/本组默写时，_index 仍指向词典位置，保存它不会破坏学习进度
        _s.SetIndex(Dict.Name, ProgressKey, _index);
        ScheduleSave();
    }

    private void SpeakIfAuto()
    {
        if (_s.Autoplay) Speak();
    }

    private void ResetTyped()
    {
        _typed = 0;
        _busy = false;
        _hintUsed = false;
        _hintHeld = false;
        _wrongStreak = 0;
        _autoReveal = false;
        Word.Error = false;
    }

    // ---------- 提示：按住显示答案，松开隐藏 ----------
    private void OnHintDown(object sender, MouseButtonEventArgs e)
    {
        // 按下即视为"用了提示" —— 无论最终拼得对错，连续天数都会清零
        _hintHeld = true;
        _hintUsed = true;
        Render();
    }

    private void OnHintUp(object sender, MouseButtonEventArgs e) => ReleaseHint();

    // 鼠标在按钮外松开时 Button 收不到 Up 事件，用 MouseLeave 兜底
    private void OnHintLeave(object sender, MouseEventArgs e) => ReleaseHint();

    private void ReleaseHint()
    {
        if (!_hintHeld) return;
        _hintHeld = false;
        Render();
    }

    // ---------- 输入 ----------
    private void OnWindowKey(object sender, KeyEventArgs e)
    {
        if (!_ready) return;

        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                return;
            case Key.Left:
                Move(-1);
                e.Handled = true;
                return;
            case Key.Right:
                Move(1);
                e.Handled = true;
                return;
            case Key.Return:
                if (Current is { } c && _typed >= c.Word.Length) Move(1);
                e.Handled = true;
                return;
        }

        var ch = KeyToChar(e.Key);
        if (ch is { } c2)
        {
            TypeChar(c2);
            e.Handled = true;
        }
    }

    private static char? KeyToChar(Key k)
    {
        if (k >= Key.A && k <= Key.Z) return (char)('a' + (k - Key.A));
        if (k >= Key.D0 && k <= Key.D9) return (char)('0' + (k - Key.D0));
        if (k >= Key.NumPad0 && k <= Key.NumPad9) return (char)('0' + (k - Key.NumPad0));
        return null;
    }

    private void TypeChar(char ch)
    {
        if (_busy) return;
        var w = Current;
        if (w is null || w.Word.Length == 0 || _typed >= w.Word.Length) return;

        if (char.ToLowerInvariant(ch) == char.ToLowerInvariant(w.Word[_typed]))
        {
            _typed++;
            Word.Typed = _typed;
            if (_typed >= w.Word.Length) _ = CorrectAsync();
        }
        else
        {
            _ = WrongAsync();
        }
    }

    private async Task WrongAsync()
    {
        _busy = true;
        _verdict = "";
        _verdictBad = true;
        Word.Error = true;
        Feedback.Text = "✗ 输入错误，已清空";
        Feedback.Foreground = Theme.Brush(Theme.Red);
        if (_s.SpeakWrong) Speak();

        // 复习判定：阶段 0 拼错不影响连续天数；阶段 1/2 拼错则打回阶段 0 重新学
        // （注意：只有"用提示"才会清零连续天数，单纯拼错不算）
        if (_phase == Phase.Review && Dict is not null && Current is { } rw)
        {
            var v = _s.JudgeWrong(Dict.Name, rw.Word);
            if (v.Length > 0)
            {
                _verdict = v;
                Feedback.Text = "✗ " + v;
            }
        }

        // 本组默写：连续输错满 5 次 → 亮出答案 3 秒，并把该词移出复习系统、退回重新学习
        bool gaveUp = false;
        if (_phase == Phase.Dictation && Dict is not null && Current is { } dw)
        {
            _wrongStreak++;
            if (_wrongStreak >= MaxWrongStreak)
            {
                gaveUp = true;
                _s.RemoveFromReview(Dict.Name, dw.Word);          // 不进入复习队列
                if (!_relearn.Any(x => x.Word == dw.Word)) _relearn.Add(dw);  // 退回重新学习
                Feedback.Text = $"✗ 连续错误 {MaxWrongStreak} 次：已显示答案，本词需重新学习";
            }
        }

        if (gaveUp)
        {
            // 自动亮出答案（相当于自动按住「提示」），3 秒后收回、清空输入重新开始
            Word.Error = false;
            _autoReveal = true;
            Render();
            await Task.Delay(AutoHintMs);
            if (!IsLoaded) return;

            _autoReveal = false;
            _busy = false;
            _typed = 0;
            _wrongStreak = 0;   // 提示过之后重新计数
            Render();
            return;
        }

        await Task.Delay(450);
        if (!IsLoaded) return;

        _busy = false;
        _typed = 0;
        Word.Error = false;
        Feedback.Text = _verdict;
        Render();
    }

    private async Task CorrectAsync()
    {
        _busy = true;
        _verdict = "";
        _verdictBad = false;
        Feedback.Text = "✓ 正确";
        Feedback.Foreground = Theme.Brush(Theme.Green);
        if (_s.SpeakCorrect) Speak();

        if (_phase != Phase.Study && Current is { } c)
        {
            // 默写/复习：先把正确答案亮出来再走
            Word.Reveal = true;
            Phon.Text = c.Phonetic;
            Phon.TextBrush = Theme.Brush(_s.TextColor);
        }

        // 复习判定：按「是否用过提示 + 连续天数 + 阶段」推进 SRS 状态
        if (_phase == Phase.Review && Dict is not null && Current is { } rw)
        {
            _verdict = _s.JudgeReview(Dict.Name, rw.Word, _hintUsed);
            _verdictBad = _hintUsed;
            if (_verdict.Length > 0)
            {
                Feedback.Text = "✓ " + _verdict;
                Feedback.Foreground = Theme.Brush(_hintUsed ? Theme.Red : Theme.Green);
            }
        }

        // 判定文案可能较长，多留一点阅读时间
        await Task.Delay(_verdict.Length > 0 ? 1500 : 900);
        if (!IsLoaded) return;

        _busy = false;
        Advance();
    }

    /// <summary>拼对一个词之后：推进流程（这是"每学 3 个默写一次"的核心）。</summary>
    private void Advance()
    {
        // 1) 复习：走完队列就回到学习
        if (_review.Count > 0)
        {
            _reviewIndex++;
            if (_reviewIndex >= _review.Count)
            {
                _review.Clear();
                _reviewIndex = 0;
                _phase = Phase.Study;
                _index = ClampIndex(_index);
                ResetTyped();
                Feedback.Text = "复习完成 ✓";
                Render();
                return;
            }
            ResetTyped();
            // 把上一条判定结果留在反馈栏，直到下次输入再被替换
            Feedback.Text = _verdict;
            Feedback.Foreground = Theme.Brush(_verdictBad ? Theme.Red : Theme.Green);
            Render();
            SpeakIfAuto();
            return;
        }

        // 2) 本组默写：3 个都拼完，回到学习开始下一轮
        if (_phase == Phase.Dictation && _round.Count > 0)
        {
            _roundIndex++;
            if (_roundIndex >= _round.Count)
            {
                _round.Clear();
                _roundIndex = 0;
                _phase = Phase.Study;
                ResetTyped();
                Feedback.Text = "本组完成 ✓";
                Render();
                SpeakIfAuto();
                return;
            }
            ResetTyped();
            Feedback.Text = "";
            Render();
            SpeakIfAuto();
            return;
        }

        // 3) 学习：把词记进本组、记录学习日期、词典下标前进
        if (_phase == Phase.Study && Dict is not null && Current is { } learned)
        {
            // 待重新学习的词优先：学完一个就退出队列并重新纳入复习系统，词典下标不动
            if (_relearn.Count > 0)
            {
                _relearn.RemoveAt(0);
                _s.MarkLearned(Dict.Name, learned.Word);
                ResetTyped();
                Feedback.Text = _relearn.Count > 0
                    ? $"重新学习完成，还剩 {_relearn.Count} 个"
                    : "重新学习完成 ✓";
                Render();
                SpeakIfAuto();
                return;
            }

            _s.MarkLearned(Dict.Name, learned.Word);
            if (_round.Count < RoundSize && !_round.Any(x => x.Word == learned.Word))
                _round.Add(learned);

            _index = Dict.Words.Count == 0 ? 0 : (_index + 1) % Dict.Words.Count;
            Remember();

            ResetTyped();
            if (_round.Count >= RoundSize)
            {
                // 学满一组 → 立刻进入本组默写
                _phase = Phase.Dictation;
                _roundIndex = 0;
                Feedback.Text = $"开始默写这 {RoundSize} 个";
            }
            else
            {
                Feedback.Text = $"已学 {_round.Count}/{RoundSize}";
            }
            Render();
            SpeakIfAuto();
            return;
        }

        // 4) 手动默写（非本组）：按词典顺序走
        Move(1);
    }

    private static int Wrap(int value, int count) =>
        count <= 0 ? 0 : (value % count + count) % count;

    private void Move(int delta)
    {
        // 待重新学习的词必须学完才能过，不允许跳过
        if (_phase == Phase.Study && _relearn.Count > 0) return;

        if (_review.Count > 0)
        {
            _reviewIndex = Wrap(_reviewIndex + delta, _review.Count);
        }
        else if (_phase == Phase.Dictation && _round.Count > 0)
        {
            _roundIndex = Wrap(_roundIndex + delta, _round.Count);
        }
        else
        {
            var d = Dict;
            if (d is null || d.Words.Count == 0) return;
            _index = Wrap(_index + delta, d.Words.Count);
            Remember();
        }

        ResetTyped();
        Feedback.Text = "";
        Render();
        SpeakIfAuto();
    }

    private void Speak()
    {
        if (Current is { } w) _tts.Speak(w.Word, _s);
    }

    // ---------- 复习模式 ----------
    private void StartReview()
    {
        if (Dict is null) return;

        // 队列由 SRS 状态机给出：阶段 0 每日必默写 + 到期阶段 1（随机 10 个）+ 到期阶段 2
        var names = _s.BuildReviewQueue(Dict.Name);
        var lookup = new Dictionary<string, WordItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Dict.Words) lookup[item.Word] = item;

        _review.Clear();
        foreach (var n in names)
            if (lookup.TryGetValue(n, out var item))
                _review.Add(item);

        if (_review.Count == 0)
        {
            _phase = Phase.Study;
            _round.Clear();
            _roundIndex = 0;
            ResetTyped();
            Render();
            Feedback.Text = "今天没有需要复习的单词";
            Feedback.Foreground = Theme.Brush(Theme.Muted);
            return;
        }

        // 打乱顺序
        var rng = new Random();
        for (int i = _review.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (_review[i], _review[j]) = (_review[j], _review[i]);
        }

        _round.Clear();
        _roundIndex = 0;
        _reviewIndex = 0;
        _phase = Phase.Review;
        ResetTyped();
        Feedback.Text = "";
        Render();
        SpeakIfAuto();
    }

    // ---------- 工具栏按钮 ----------
    /// <summary>
    /// 模式按钮：只在学习 ⇄ 复习之间切换。
    /// 「默写」已经不需要手动进入 —— 它是「每学 3 个自动默写一次」流程里的一段，
    /// 到了会自动切过去，拼完自动切回学习。
    /// </summary>
    private void OnModeClick(object sender, RoutedEventArgs e)
    {
        if (_phase == Phase.Review)
        {
            // 复习 → 学习
            _review.Clear();
            _reviewIndex = 0;
            _phase = Phase.Study;
            _index = ClampIndex(_s.GetIndex(CurrentDictName, ProgressKey));
            ResetTyped();
            Feedback.Text = "";
            Render();
            SpeakIfAuto();
        }
        else
        {
            // 学习 / 本组默写中 → 进入复习（本组若没默写完就作废）
            _round.Clear();
            _roundIndex = 0;
            ResetTyped();
            Feedback.Text = "";
            StartReview();
        }
        Focus();
    }

    private void OnSpeakClick(object sender, RoutedEventArgs e)
    {
        Speak();
        Focus();
    }

    private void OnPrevClick(object sender, RoutedEventArgs e) { Move(-1); Focus(); }

    private void OnNextClick(object sender, RoutedEventArgs e) { Move(1); Focus(); }

    private void OnGearClick(object sender, RoutedEventArgs e)
    {
        if (_settingsWin is { IsLoaded: true })
        {
            _settingsWin.Activate();
            return;
        }
        _settingsWin = new SettingsWindow(this) { Owner = this };
        _settingsWin.Closed += (_, _) => _settingsWin = null;
        _settingsWin.Show();
        Focus();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // ---------- 工具栏显示 / 隐藏 ----------
    private void OnWindowMouseEnter(object sender, MouseEventArgs e)
    {
        if (!_s.ToolbarPinned) SetToolbarVisible(true);
    }

    private void OnWindowMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_s.ToolbarPinned)
        {
            _toolbarTimer.Stop();
            _toolbarTimer.Start();
        }
    }

    private void MaybeHideToolbar()
    {
        if (_s.ToolbarPinned || IsMouseOver) return;
        if (_settingsWin is { IsLoaded: true } w && w.IsMouseOver) return;
        SetToolbarVisible(false);
    }

    private void SetToolbarVisible(bool visible)
    {
        var target = visible ? Visibility.Visible : Visibility.Collapsed;
        if (Toolbar.Visibility == target) return;

        double bottom = Top + ActualHeight;
        Toolbar.Visibility = target;

        // 高度变化时保持底边不动 → 正文位置不跳
        Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateLayout();
            Top = bottom - ActualHeight;
        }), DispatcherPriority.Loaded);
    }

    // ---------- 窗口位置 / 拖动 ----------
    /// <summary>启动时的初始位置：屏幕右下角。</summary>
    private void AnchorBottomRight()
    {
        UpdateLayout();
        Left = SystemParameters.WorkArea.Right - ActualWidth - 30;
        Top = SystemParameters.WorkArea.Bottom - ActualHeight - 30;
    }

    /// <summary>
    /// 内容变化（换词、换字号、换字体）会改变窗口尺寸。
    /// 这里保证 **窗口中心不动**：否则窗口会以左上角为基准向右下生长，
    /// 每换一个词整块文字都会跟着位移。
    /// </summary>
    private void KeepCenterLater()
    {
        if (!IsLoaded || double.IsNaN(Left) || double.IsNaN(Top)) return;

        // 先记下"旧尺寸下的中心"，布局更新后再按新尺寸把中心摆回去
        double cx = Left + ActualWidth / 2;
        double cy = Top + ActualHeight / 2;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsLoaded) return;
            UpdateLayout();
            Left = cx - ActualWidth / 2;
            Top = cy - ActualHeight / 2;
        }), DispatcherPriority.Loaded);
    }

    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && IsInteractive(d)) return;
        Activate();
        Focus();
        try
        {
            DragMove();
        }
        catch { /* 拖拽异常忽略 */ }
    }

    private static bool IsInteractive(DependencyObject? d)
    {
        while (d is not null)
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : null;
        }
        return false;
    }

    // ---------- 退出 ----------
    private void OnWindowClosed(object? sender, EventArgs e)
    {
        Remember();
        _saveTimer.Stop();
        _s.Save(AppPaths.ConfigFile);
        _tts.Dispose();
    }
}
