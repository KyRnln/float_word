using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

    /// <summary>显示单词多久后，用 AI 台词替换音标（毫秒）。</summary>
    private const int AiQuoteDelayMs = 2000;

    /// <summary>换词时单词从右侧滑入的距离（像素）与时长（毫秒）。</summary>
    private const double WordSlideFrom = 44;
    private const int WordSlideMs = 260;

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

    private readonly AiQuoteService _ai = new();
    private CancellationTokenSource? _aiCts;
    private string _aiWord = "";     // 已为哪个词发起过台词请求
    private MovieQuote? _aiQuote;    // 当前要显示的台词（null = 仍显示音标）
    private string _aiSignature = "\u0000";   // AI 配置指纹：变了就作废当前台词重新请求
    private string _shownWord = "\u0000";     // 上一次渲染的单词：变化时播「从右向左」滑入

    private bool _hintHeld;          // 提示按钮是否正被按住（按住显示答案）
    private bool _hintUsed;          // 本词是否用过提示（用提示会清零连续天数）
    private string _verdict = "";    // 上一次复习判定结果（留到下次输入前）
    private bool _verdictBad;        // 判定结果是否为负向（用提示 / 默写错误）

    private bool _skipSaveOnClose;   // 恢复备份后重启时用：别让关闭时的保存盖掉刚恢复的配置

    public MainWindow(AppSettings settings, List<WordDictionary> library)
    {
        _s = settings;
        _lib = library;
        InitializeComponent();

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _s.Save(AppPaths.ConfigFile); };

        _toolbarTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _toolbarTimer.Tick += (_, _) => { _toolbarTimer.Stop(); MaybeHideToolbar(); };

        // 窗口一移动就把屏幕坐标记进配置（含 WebDAV 备份）
        LocationChanged += OnWindowLocationChanged;
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
        RestoreOrAnchorPosition();
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
        Info.Opacity = Mean.Opacity = Phon.Opacity = Quote.Opacity = Feedback.Opacity = Word.Opacity = top;

        // 释义按词性分行，每行长短差别很大：给一个随屏幕自适应的上限，
        // 既让绝大多数词性行不折行，又不会把窗口撑到屏幕外。
        double textLimit = Math.Clamp(SystemParameters.WorkArea.Width * 0.62, 480, 1400);

        // 注释（释义）：字体 / 字号 / 加粗 / 颜色 / 描边，与单词的设置项一一对应
        Mean.FontFamilyName = _s.MeanFontFamily;
        Mean.FontSize = _s.MeanSize;
        Mean.Bold = _s.MeanBold;
        Mean.TextBrush = Theme.Brush(_s.MeanColor);
        Mean.OutlineWidth = _s.MeanOutlineW;
        Mean.OutlineBrush = Theme.Brush(_s.MeanOutlineColor);
        Mean.TextMaxWidth = textLimit;

        // 发音（音标）：同上（平时很短，同样给换行上限以防被台词撑宽）
        Phon.FontFamilyName = _s.PhonFontFamily;
        Phon.FontSize = _s.PhonSize;
        Phon.Bold = _s.PhonBold;
        Phon.TextBrush = Theme.Brush(_s.PhonColor);
        Phon.OutlineWidth = _s.PhonOutlineW;
        Phon.OutlineBrush = Theme.Brush(_s.PhonOutlineColor);
        Phon.TextMaxWidth = textLimit;

        // AI 台词块：整套跟随「发音」；左列（台词+翻译）宽一些，右列（片名+年份）窄一些
        foreach (var t in new[] { QuoteLeft, QuoteDot, QuoteMovie, QuoteYear })
        {
            t.FontFamilyName = _s.PhonFontFamily;
            t.FontSize = _s.PhonSize;
            t.Bold = _s.PhonBold;
            t.TextBrush = Theme.Brush(_s.PhonColor);
            t.OutlineWidth = _s.PhonOutlineW;
            t.OutlineBrush = Theme.Brush(_s.PhonOutlineColor);
        }
        QuoteLeft.TextMaxWidth = textLimit * 0.62;
        QuoteMovie.TextMaxWidth = textLimit * 0.38;
        QuoteYear.TextMaxWidth = textLimit * 0.38;
        QuoteLeft.HighlightBrush = Theme.Brush(_s.QuoteHlColor);   // 台词里命中当前单词的高亮色

        Word.FontFamilyName = _s.FontFamily;
        Word.FontSize = _s.FontSize;
        Word.Bold = _s.FontBold;
        Word.OutlineWidth = _s.OutlineW;
        Word.TextBrush = Theme.Brush(_s.TextColor);
        Word.OutlineBrush = Theme.Brush(_s.OutlineColor);
        Word.HintBrush = Theme.Brush(_s.HintColor, _s.HintAlpha / 100.0);
        Word.Relayout();

        // AI 配置（开关 / 地址 / Key / 模型）变了 → 作废当前台词，下次渲染重新请求
        var aiSig = $"{_s.AiEnabled}|{_s.AiBaseUrl}|{_s.AiApiKey}|{_s.AiModel}|{_s.AiUseSystemProxy}";
        if (aiSig != _aiSignature)
        {
            _aiSignature = aiSig;
            CancelAiQuote();
        }

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
            KeepAnchoredLater();
            return;
        }

        UpdateAiState(w);   // 换词时重置台词状态（仅学习模式）

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

        // 学习模式下，显示满 2 秒后用 AI 台词块替换音标
        bool showQuote = revealed && _phase == Phase.Study && _aiQuote is not null && _aiWord == w.Word;
        Phon.Visibility = showQuote ? Visibility.Collapsed : Visibility.Visible;
        Quote.Visibility = showQuote ? Visibility.Visible : Visibility.Collapsed;
        if (showQuote)
        {
            QuoteLeft.Text = _aiQuote!.LeftLines();     // 左列：台词 + 翻译
            QuoteLeft.HighlightText = w.Word;           // 在台词里高亮当前单词（含词形变化）
            QuoteMovie.Text = _aiQuote.MovieLine();     // 右列上行：《片名》
            QuoteYear.Text = _aiQuote.YearLine();       // 右列下行：（年份），居中于片名之下
            // 没有片名时右列是空的，分隔点也就没有存在的意义
            QuoteDot.Visibility = _aiQuote.MovieLine().Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            Phon.Text = revealed ? w.Phonetic : "";
            Phon.TextBrush = revealed ? Theme.Brush(_s.PhonColor) : Theme.Brush(Theme.Muted);
        }

        // 换到别的单词时，让单词从右侧缓动滑入（渲染变换，不影响居中与窗口测量）
        bool wordChanged = w.Word != _shownWord;
        _shownWord = w.Word;

        // 默写/复习用等宽槽位排版（占位横线等宽、间距一致）；学习模式按字母真实宽度
        Word.UniformCells = _phase != Phase.Study;
        // 顺序要紧：先写 Typed 再写 Word —— Word 会触发 Relayout，把待输入框瞬间归位到新词；
        // 若反序，会先用旧词的格子播一次无意义的滑动动画。
        Word.Typed = _typed;
        Word.Word = w.Word;
        Word.Reveal = revealed;
        Word.Error = false;

        // 提示按钮只在复习模式下出现
        HintBtn.Visibility = _phase == Phase.Review ? Visibility.Visible : Visibility.Collapsed;

        if (wordChanged) SlideWordIn();
        KeepAnchoredLater();
    }

    /// <summary>换词时让单词从右向左缓动滑入。</summary>
    private void SlideWordIn()
    {
        var tt = new TranslateTransform(WordSlideFrom, 0);
        Word.RenderTransform = tt;
        tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
        {
            From = WordSlideFrom,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(WordSlideMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void Remember()
    {
        SaveWindowPos();   // 位置也随配置落盘（放进 WebDAV 备份）
        if (Dict is null) return;
        // 复习/本组默写时，_index 仍指向词典位置，保存它不会破坏学习进度
        _s.SetIndex(Dict.Name, ProgressKey, _index);
        ScheduleSave();
    }

    private void SpeakIfAuto()
    {
        if (_s.Autoplay) Speak();
    }

    // ---------- AI 台词 ----------
    /// <summary>
    /// 换词时重置台词状态：学习模式下发起一次请求，满 2 秒后把音标替换成台词；
    /// 默写 / 复习不显示（台词里通常带着答案）。同一个词不重复请求（服务里有缓存）。
    /// </summary>
    private void UpdateAiState(WordItem w)
    {
        if (!_s.AiEnabled || _phase != Phase.Study)
        {
            CancelAiQuote();
            return;
        }

        if (_aiWord == w.Word) return;   // 还是同一个词，保持现状

        _aiWord = w.Word;
        _aiQuote = null;
        _aiCts?.Cancel();
        var cts = new CancellationTokenSource();
        _aiCts = cts;
        _ = RunAiQuoteAsync(w, cts);
    }

    private void CancelAiQuote()
    {
        if (_aiWord.Length == 0 && _aiQuote is null) return;
        _aiWord = "";
        _aiQuote = null;
        _aiCts?.Cancel();
    }

    /// <summary>清空台词缓存后调用：作废当前词的台词并重新请求（不影响学习进度）。</summary>
    public void RefreshAiQuote()
    {
        _aiWord = "";        // 置空后 UpdateAiState 会当作"换词"重新发起请求
        _aiQuote = null;
        _aiCts?.Cancel();
        Render();
        ScheduleSave();      // 让清空后的缓存落盘
    }

    private async Task RunAiQuoteAsync(WordItem w, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            var fetch = _ai.FetchAsync(w.Word, _s, ct);

            // 先让用户看一会儿音标，满 2 秒再替换；请求慢的话等它回来再显示
            await Task.WhenAll(fetch, Task.Delay(AiQuoteDelayMs, ct));
            if (ct.IsCancellationRequested || _aiWord != w.Word) return;

            var outcome = fetch.Result;
            if (outcome.Ok)
            {
                _aiQuote = outcome.Quote;   // 台词 / 翻译 / · 《片名》 / （年份）
                ScheduleSave();          // 台词缓存写进配置，下次不再请求
                Render();
            }
            else if (outcome.Error is { } err)
            {
                // 失败时把原因显示出来，避免"没反应又不知道哪里错了"
                Feedback.Text = err.Length > 60 ? "AI：" + err[..60] + "…" : "AI：" + err;
                Feedback.Foreground = Theme.Brush(Theme.Muted);
            }
        }
        catch (OperationCanceledException)
        {
            // 换词 / 关闭功能时取消，属正常路径
        }
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
        SoundFx.PlayDing();                 // 拼写正确的「叮」声反馈
        if (_s.SpeakCorrect) Speak();

        if (_phase != Phase.Study && Current is { } c)
        {
            // 默写/复习：先把正确答案亮出来再走
            Word.Reveal = true;
            Phon.Text = c.Phonetic;
            Phon.TextBrush = Theme.Brush(_s.PhonColor);
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

        // 工具栏出现/隐藏会改变高度，按当前锚定边摆回去 → 正文位置不跳
        Anchored(() => Toolbar.Visibility = target);
    }

    // ---------- 窗口位置 / 拖动 ----------
    /// <summary>
    /// 启动定位：优先恢复上次保存的屏幕坐标（写在配置里，会随 WebDAV 备份一起走）；
    /// 首次运行或坐标失效时退回屏幕右下角。坐标一律按工作区（不含任务栏）夹紧，保证不出屏。
    /// </summary>
    private void RestoreOrAnchorPosition()
    {
        UpdateLayout();
        var wa = SystemParameters.WorkArea;

        if (_s.WinLeft is { } l && _s.WinTop is { } t)
        {
            Left = Math.Clamp(l, wa.Left, Math.Max(wa.Left, wa.Right - ActualWidth));
            Top = Math.Clamp(t, wa.Top, Math.Max(wa.Top, wa.Bottom - ActualHeight));
            return;
        }

        Left = wa.Right - ActualWidth - 30;
        Top = wa.Bottom - ActualHeight - 30;
    }

    /// <summary>
    /// 内容变化（换词、换字号、显示工具栏等）会改变窗口尺寸。
    /// 这里以**屏幕坐标为基准**：窗口中心落在屏幕右/下半边时固定右/下边，否则固定左/上边，
    /// 尺寸变化后按该锚定边摆回去 —— 窗口就不会随着单词变化而整体移动，也不会长到屏幕外。
    /// </summary>
    private void KeepAnchoredLater() => Anchored(() => { });

    private void Anchored(Action change)
    {
        if (!IsLoaded || double.IsNaN(Left) || double.IsNaN(Top))
        {
            change();
            return;
        }

        var wa = SystemParameters.WorkArea;
        bool fixRight = Left + ActualWidth / 2 > wa.Left + wa.Width / 2;
        bool fixBottom = Top + ActualHeight / 2 > wa.Top + wa.Height / 2;

        // 记下变化前的位置与锚定边
        double left = Left, top = Top;
        double rightEdge = Left + ActualWidth;
        double bottomEdge = Top + ActualHeight;

        change();

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsLoaded) return;
            UpdateLayout();

            double nl = fixRight ? rightEdge - ActualWidth : left;
            double nt = fixBottom ? bottomEdge - ActualHeight : top;
            Left = Math.Clamp(nl, wa.Left, Math.Max(wa.Left, wa.Right - ActualWidth));
            Top = Math.Clamp(nt, wa.Top, Math.Max(wa.Top, wa.Bottom - ActualHeight));
        }), DispatcherPriority.Loaded);
    }

    /// <summary>把窗口当前的屏幕坐标写回配置（配置会被 WebDAV 一起备份）。</summary>
    private void SaveWindowPos()
    {
        if (!IsLoaded || double.IsNaN(Left) || double.IsNaN(Top)) return;
        _s.WinLeft = Left;
        _s.WinTop = Top;
    }

    private void OnWindowLocationChanged(object? sender, EventArgs e)
    {
        if (!_ready) return;
        SaveWindowPos();
        ScheduleSave();
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
        _aiCts?.Cancel();

        // 恢复备份后重启时跳过保存：此时文件里已经是新配置，这里写会把旧的内存状态盖回去
        if (!_skipSaveOnClose)
        {
            Remember();
            _saveTimer.Stop();
            _s.Save(AppPaths.ConfigFile);
            AutoBackup();
        }

        _tts.Dispose();
    }

    /// <summary>把当前会话状态（学到哪、当前词典）写回配置并落盘。设置页备份前会调它。</summary>
    public void SyncToSettings()
    {
        Remember();              // _index -> _s
        _saveTimer.Stop();       // 不等防抖，直接落盘
        _s.Save(AppPaths.ConfigFile);
    }

    /// <summary>
    /// 用下载回来的配置覆盖本地并重启程序 —— 重启才能让悬浮窗与设置页都按新配置重建。
    /// </summary>
    public void RestoreAndRestart(string json)
    {
        _skipSaveOnClose = true;
        _aiCts?.Cancel();
        _saveTimer.Stop();

        try { File.WriteAllText(AppPaths.ConfigFile, json); }
        catch (Exception ex) { Log.Error("写入恢复的配置失败", ex); }

        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error("重启程序失败", ex); }

        Application.Current.Shutdown();
    }

    /// <summary>
    /// 退出时自动备份到 WebDAV。同步等待最多 8 秒，失败只记日志、不打扰用户。
    /// </summary>
    private void AutoBackup()
    {
        if (!_s.WebDavAuto || !WebDavService.Configured(_s, out _)) return;

        var json = _s.ToJson();   // 在 UI 线程先取快照，避免后台线程读到半途状态
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            // 这里要阻塞 UI 线程等结果，所以必须丢到线程池执行：直接 await 会死锁
            var t = Task.Run(() => WebDavService.UploadAsync(_s, json, cts.Token));
            if (!t.Wait(TimeSpan.FromSeconds(8)))
            {
                cts.Cancel();
                Log.Warn("退出自动备份超时，已放弃");
                return;
            }

            if (t.Result.Ok)
            {
                _s.WebDavLast = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                _s.Save(AppPaths.ConfigFile);
            }
        }
        catch (Exception ex)
        {
            Log.Error("退出自动备份失败", ex);
        }
    }
}
