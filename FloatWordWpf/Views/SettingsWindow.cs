using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
// WPF-UI 里有和 WPF 同名的控件类型，这里只按需取别名，避免 Button/CheckBox 之类产生歧义
using ControlAppearance = Wpf.Ui.Controls.ControlAppearance;
using FluentButton = Wpf.Ui.Controls.Button;
using FluentTitleBar = Wpf.Ui.Controls.TitleBar;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;
using WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType;

namespace FloatWordWpf;

/// <summary>
/// 设置窗口 —— Fluent 版。
///
/// 使用 WPF-UI 的 FluentWindow：
///   · WindowBackdropType = Mica → Windows 11 的云母材质背景
///   · ExtendsContentIntoTitleBar + ui:TitleBar → Fluent 标题栏
///   · 所有输入控件（Slider/ComboBox/CheckBox/Button）交给 WPF-UI 的隐式样式，
///     因此不再需要自己写控件模板。
///
/// 设置项仍由 Build() 里的描述式调用生成，新增一项只需加一行。
/// </summary>
public sealed class SettingsWindow : FluentWindow
{
    private static readonly FontFamily TextFont = new("Segoe UI Variable Text, Segoe UI");
    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas");

    private readonly MainWindow _main;
    private readonly AppSettings _s;
    private readonly StackPanel _body = new();
    private readonly List<Action> _progressRefreshers = new();
    private ComboBox? _dictCombo;
    private readonly AiQuoteService _ai = new();

    /// <summary>当前设置项的写入目标：普通项写 _body，折叠组内写组内的面板。</summary>
    private Panel _target = null!;

    public SettingsWindow(MainWindow main)
    {
        _main = main;
        _s = main.Settings;

        Title = "FloatWord 设置";
        Width = 420;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        // Fluent 云母背景 + 内容延伸到标题栏
        WindowBackdropType = WindowBackdropType.Mica;
        ExtendsContentIntoTitleBar = true;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var titleBar = new FluentTitleBar
        {
            Title = "设置",
            ShowMinimize = false,
            ShowMaximize = false,
            ShowClose = true
        };
        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            // 左右内边距完全对称
            Padding = new Thickness(22, 4, 22, 20)
        };

        // 让竖向滚动条"浮"在右侧留白里，而不是额外占一列宽度。
        // 否则右侧可視留白会被滚动条挤窄（这正是之前左右不对称的原因）。
        // 注意必须 BasedOn 官方隐式样式，直接 new Style 会把 Fluent 模板顶掉。
        var barStyle = new Style(typeof(System.Windows.Controls.Primitives.ScrollBar));
        if (Application.Current?.TryFindResource(typeof(System.Windows.Controls.Primitives.ScrollBar)) is Style baseBar)
            barStyle.BasedOn = baseBar;
        barStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(-12, 0, 0, 0)));
        scroll.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = barStyle;

        scroll.Content = _body;
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        Content = root;

        _target = _body;
        Build();
        Loaded += (_, _) => PositionNearMain();
        // 悬浮窗学习时设置窗口可能一直开着，重新聚焦时刷新进度
        Activated += (_, _) => RefreshProgress();
    }

    private void PositionNearMain()
    {
        var wa = SystemParameters.WorkArea;
        double w = Math.Max(Width, ActualWidth);
        double h = Math.Max(Height, ActualHeight);

        double left = _main.Left - w - 14;
        if (left < wa.Left) left = _main.Left + _main.ActualWidth + 14;
        Left = Math.Clamp(left, wa.Left, Math.Max(wa.Left, wa.Right - w));
        Top = Math.Clamp(_main.Top - 40, wa.Top, Math.Max(wa.Top, wa.Bottom - h));
    }

    // ---------- 设置项描述 ----------
    private void Build()
    {
        Section("学习进度");

        // 分母「合计」= 当前词典的总词汇数
        int[] Counts() => _s.StageCounts(_s.Dict);
        int Total() => _main.Library.FirstOrDefault(d => d.Name == _s.Dict)?.Words.Count ?? 0;

        ProgressRow("复习（阶段 0）", () => Counts()[0], Total);
        ProgressRow("阶段 1", () => Counts()[1], Total);
        ProgressRow("阶段 2", () => Counts()[2], Total);
        ProgressRow("已完成", () => Counts()[3], Total);
        Note("分母「合计」= 当前词典的总词汇数。复习（阶段 0）= 学习中、每天默写的单词；阶段 1 = 首次成功（10 天后复习）；阶段 2 = 二次成功（30 天后复习）；已完成 = 通过全部复习。");

        // 词典模块紧跟在进度下方：切换词典即可看到该词典的进度
        _dictCombo = Choice("词典", _main.Library.Select(d => d.Name).ToArray(),
                            () => _s.Dict, v => { _main.ChangeDict(v); RefreshProgress(); });
        ImportRow();

        Section("外观");

        // 单词（逐字绘制的 WordCanvas）
        Group("单词", () =>
        {
            Choice("字体", Theme.FontChoices, () => _s.FontFamily, v => _s.FontFamily = v);
            SliderRow("字号", 14, 72, () => _s.FontSize, v => _s.FontSize = v);
            Check("加粗", () => _s.FontBold, v => _s.FontBold = v);
            ColorRow("文字颜色", Theme.ColorPresets, () => _s.TextColor, v => _s.TextColor = v);
            SliderRow("描边宽度", 0, 8, () => _s.OutlineW, v => _s.OutlineW = v);
            ColorRow("描边颜色", Theme.OutlinePresets, () => _s.OutlineColor, v => _s.OutlineColor = v);
            ColorRow("提示框颜色", Theme.HintPresets, () => _s.HintColor, v => _s.HintColor = v);
            SliderRow("提示框透明度", 0, 100, () => _s.HintAlpha, v => _s.HintAlpha = v);
            Note("提示框是当前待输入字符的高亮块。");
        });

        // 注释（释义）
        Group("注释", () =>
        {
            Choice("字体", Theme.FontChoices, () => _s.MeanFontFamily, v => _s.MeanFontFamily = v);
            SliderRow("字号", 8, 36, () => _s.MeanSize, v => _s.MeanSize = v);
            Check("加粗", () => _s.MeanBold, v => _s.MeanBold = v);
            ColorRow("文字颜色", Theme.ColorPresets, () => _s.MeanColor, v => _s.MeanColor = v);
            SliderRow("描边宽度", 0, 8, () => _s.MeanOutlineW, v => _s.MeanOutlineW = v);
            ColorRow("描边颜色", Theme.OutlinePresets, () => _s.MeanOutlineColor, v => _s.MeanOutlineColor = v);
        });

        // 发音（音标）
        Group("发音", () =>
        {
            Choice("字体", Theme.FontChoices, () => _s.PhonFontFamily, v => _s.PhonFontFamily = v);
            SliderRow("字号", 8, 32, () => _s.PhonSize, v => _s.PhonSize = v);
            Check("加粗", () => _s.PhonBold, v => _s.PhonBold = v);
            ColorRow("文字颜色", Theme.ColorPresets, () => _s.PhonColor, v => _s.PhonColor = v);
            SliderRow("描边宽度", 0, 8, () => _s.PhonOutlineW, v => _s.PhonOutlineW = v);
            ColorRow("描边颜色", Theme.OutlinePresets, () => _s.PhonOutlineColor, v => _s.PhonOutlineColor = v);
            Note("AI 台词块（台词 / 翻译 / 片名 / 年份）也跟随这组设置。");
        });

        // 其余个性化项
        Group("其他", () =>
        {
            SliderRow("背景透明度", 0, 100, () => _s.BgAlpha, v => _s.BgAlpha = v);
            SliderRow("文字透明度", 0, 100, () => _s.TextAlpha, v => _s.TextAlpha = v);
            Check("显示词典名称", () => _s.ShowDictName, v => _s.ShowDictName = v);
            Check("工具栏常显（否则鼠标移入才显示）", () => _s.ToolbarPinned, v => _s.ToolbarPinned = v);
        });

        Section("语音播报（Piper 离线神经语音）");

        var voices = PiperService.AvailableVoices();
        if (voices.Length == 0)
        {
            Note("未找到 Piper 引擎或语音模型：请确认程序目录下存在 piper\\bin\\piper.exe 与 piper\\voices\\*.onnx");
        }
        else
        {
            Choice("语音", voices, () => _s.Voice, v => _s.Voice = v);
        }

        SliderRow("播报音量", 0, 100, () => _s.Volume, v => _s.Volume = v);
        SliderRow("播报增益 %", 100, 200, () => _s.Gain, v => _s.Gain = v);
        SliderRow("播报语速", -10, 10, () => _s.Rate, v => _s.Rate = v);
        Check("新单词自动播报", () => _s.Autoplay, v => _s.Autoplay = v);
        Check("答对后播报一次", () => _s.SpeakCorrect, v => _s.SpeakCorrect = v);
        Check("错误后播报一次", () => _s.SpeakWrong, v => _s.SpeakWrong = v);

        Section("AI 台词（OpenAI 兼容接口）");

        Check("启用 AI 台词（学习模式，2 秒后用台词替换音标）", () => _s.AiEnabled, v => _s.AiEnabled = v);
        TextRow("Base URL", () => _s.AiBaseUrl, v => _s.AiBaseUrl = v);
        TextRow("API Key", () => _s.AiApiKey, v => _s.AiApiKey = v);
        TextRow("模型", () => _s.AiModel, v => _s.AiModel = v);
        Check("AI 请求走系统代理（OpenAI 等国外服务勾上；DeepSeek / 通义 / Ollama 等国内服务别勾）",
              () => _s.AiUseSystemProxy, v => _s.AiUseSystemProxy = v);
        Note("为单词生成一句含该词的经典电影台词，格式「台词---电影名称」。仅在学习模式显示——" +
             "默写 / 复习不显示，避免台词带着答案。结果会缓存到配置文件，同一个词只请求一次；" +
             "API Key 以明文保存，请注意不要把它连同配置文件一起分享。");
        Note("建议用普通对话模型（如 deepseek-chat，几百毫秒就返回）。" +
             "推理型模型（如 deepseek-flash / reasoner）会先输出一大段思考内容，" +
             "生成一句台词要 20~40 秒，台词会明显延迟出现。");
        TestAiRow();

        Note("提示：鼠标移入悬浮窗显示工具栏；点击窗口后直接在单词上逐字输入；← → 切换单词。");
        Note("WPF 版使用真 per-pixel alpha，半透明背景下文字边缘也不会有毛边，因此不需要「清晰文字」开关。");
        Note($"设置自动保存在 {AppPaths.ConfigFile}");

        Section("日志");
        LogRow();

        // 破坏性操作放到最下方，且必须二次确认
        Section("重置");
        ResetRow();
    }

    // ---------- 行构造 ----------
    private Grid NewRow()
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(106) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _target.Children.Add(g);
        return g;
    }

    private void RowLabel(Grid g, string text)
    {
        var t = new TextBlock
        {
            Text = text,
            FontFamily = TextFont,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        Grid.SetColumn(t, 0);
        g.Children.Add(t);
    }

    private void Section(string title)
    {
        var t = new TextBlock
        {
            Text = title,
            FontFamily = TextFont,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 18, 0, 8)
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _target.Children.Add(t);
    }

    /// <summary>
    /// 折叠分组：标题栏是一个 Fluent 透明按钮，点击展开 / 收起内容。
    /// （WPF-UI 4.3 没有可直接使用的 Expander 控件，这里自己拼一个，外观与工具栏按钮一致。）
    /// 分组期间把写入目标切到组内面板，build() 里照常用 SliderRow / ColorRow / Check 即可。
    /// </summary>
    private void Group(string title, Action build)
    {
        var panel = new StackPanel
        {
            Margin = new Thickness(6, 2, 0, 8),
            Visibility = Visibility.Collapsed
        };

        var arrow = new TextBlock
        {
            Text = "\uE76C",                                        // ChevronRight
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 11,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        arrow.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var caption = new TextBlock
        {
            Text = title,
            FontFamily = TextFont,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(arrow);
        head.Children.Add(caption);

        var header = new FluentButton
        {
            Content = head,
            Appearance = ControlAppearance.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Height = 34,
            Margin = new Thickness(0, 2, 0, 2)
        };
        header.Click += (_, _) =>
        {
            bool open = panel.Visibility != Visibility.Visible;
            panel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            arrow.Text = open ? "\uE70D" : "\uE76C";                // ChevronDown / ChevronRight
        };

        _target.Children.Add(header);
        _target.Children.Add(panel);

        var prev = _target;
        _target = panel;
        build();
        _target = prev;
    }

    private void Note(string text)
    {
        var t = new TextBlock
        {
            Text = text,
            FontFamily = TextFont,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 17,
            Margin = new Thickness(0, 10, 0, 0)
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        _target.Children.Add(t);
    }

    private void SliderRow(string label, double min, double max,
                           Func<double> get, Action<double> set)
    {
        var g = NewRow();
        RowLabel(g, label);

        var val = new TextBlock
        {
            Text = ((int)Math.Round(get())).ToString(),
            FontFamily = MonoFont,
            FontSize = 12,
            Width = 32,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        val.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(get(), min, max),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
            IsSnapToTickEnabled = true,
            TickFrequency = 1
        };
        slider.ValueChanged += (_, e) =>
        {
            val.Text = ((int)Math.Round(e.NewValue)).ToString();
            set(e.NewValue);
            _main.RefreshFromSettings();
        };

        Grid.SetColumn(slider, 1);
        g.Children.Add(slider);
        Grid.SetColumn(val, 2);
        g.Children.Add(val);
    }

    /// <summary>单行文本输入（Base URL / API Key / 模型名）。</summary>
    private void TextRow(string label, Func<string> get, Action<string> set)
    {
        var g = NewRow();
        RowLabel(g, label);

        var tb = new TextBox
        {
            Text = get(),
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = MonoFont,
            FontSize = 12
        };
        tb.TextChanged += (_, _) =>
        {
            if (tb.Text == get()) return;
            set(tb.Text);
            _main.RefreshFromSettings();
        };

        Grid.SetColumn(tb, 1);
        Grid.SetColumnSpan(tb, 2);
        g.Children.Add(tb);
    }

    /// <summary>进度行：左名称、中进度条、右「当前 / 合计」。</summary>
    private void ProgressRow(string label, Func<int> count, Func<int> total)
    {
        var g = NewRow();
        RowLabel(g, label);

        var val = new TextBlock
        {
            FontFamily = MonoFont,
            FontSize = 12,
            Width = 64,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        val.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 6,
            Margin = new Thickness(8, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        Grid.SetColumn(bar, 1);
        g.Children.Add(bar);
        Grid.SetColumn(val, 2);
        g.Children.Add(val);

        void Refresh()
        {
            int c = count(), t = total();
            val.Text = $"{c} / {t}";
            bar.Value = t > 0 ? Math.Clamp(c * 100.0 / t, 0, 100) : 0;
        }

        Refresh();
        _progressRefreshers.Add(Refresh);
    }

    private void RefreshProgress()
    {
        foreach (var r in _progressRefreshers) r();
    }

    private void Check(string label, Func<bool> get, Action<bool> set)
    {
        var g = NewRow();
        var cb = new CheckBox
        {
            Content = label,
            IsChecked = get(),
            FontFamily = TextFont,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        cb.SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
        cb.Checked += (_, _) => { set(true); _main.RefreshFromSettings(); };
        cb.Unchecked += (_, _) => { set(false); _main.RefreshFromSettings(); };
        Grid.SetColumn(cb, 0);
        Grid.SetColumnSpan(cb, 3);
        g.Children.Add(cb);
    }

    private ComboBox Choice(string label, string[] options, Func<string> get, Action<string> set)
    {
        var g = NewRow();
        RowLabel(g, label);

        var combo = new ComboBox
        {
            ItemsSource = options,
            SelectedItem = options.Contains(get()) ? get() : options.FirstOrDefault(),
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = TextFont,
            FontSize = 13
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is string s && s != get())
            {
                set(s);
                _main.RefreshFromSettings();
            }
        };
        Grid.SetColumn(combo, 1);
        Grid.SetColumnSpan(combo, 2);
        g.Children.Add(combo);
        return combo;
    }

    // ---------- 词典导入 ----------
    private void ImportRow()
    {
        var g = NewRow();
        var btn = new FluentButton
        {
            Content = "导入词典…",
            Appearance = ControlAppearance.Secondary,
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 110,
            FontFamily = TextFont,
            FontSize = 13
        };
        btn.Click += (_, _) => ImportDictionary();
        Grid.SetColumn(btn, 1);
        Grid.SetColumnSpan(btn, 2);
        g.Children.Add(btn);

        Note("支持两种 JSON 词库：本项目的 [{ word, phonetic, meaning }]，" +
             "或 Qwerty Learner 的 [{ name, trans, usphone }]。导入后复制到程序目录的 dicts\\ 文件夹。");
    }

    private async void ImportDictionary()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入词典",
            Filter = "JSON 词库 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) != true) return;

        List<WordItem> items;
        try
        {
            items = DictionaryLibrary.TryParse(File.ReadAllText(dlg.FileName));
        }
        catch (Exception ex)
        {
            await Warn("读取失败：" + ex.Message, "导入词典");
            return;
        }

        if (items.Count == 0)
        {
            await Warn("这个文件里没有解析出任何单词。\n\n支持的格式：\n" +
                       "[{ \"word\": \"abandon\", \"phonetic\": \"/əˈbændən/\", \"meaning\": \"v. 放弃\" }]\n\n" +
                       "或 Qwerty Learner 格式：\n" +
                       "[{ \"name\": \"abandon\", \"trans\": [\"v. 放弃\"], \"usphone\": \"ə'bændən\" }]",
                "导入词典");
            return;
        }

        var name = Path.GetFileNameWithoutExtension(dlg.FileName);
        var dir = Path.Combine(AppPaths.AppDir, "dicts");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, name + ".json");

        if (File.Exists(target) && !await Confirm($"已存在同名词库「{name}」，是否覆盖？", "导入词典", "覆盖"))
            return;

        try
        {
            File.Copy(dlg.FileName, target, true);
        }
        catch (Exception ex)
        {
            await Warn("写入失败：" + ex.Message, "导入词典");
            return;
        }

        // 重新扫描目录 → 刷新下拉 → 切到新导入的词典
        _main.ReloadLibrary();
        RefreshDictChoices(name);
        _main.ChangeDict(name);

        await Info($"已导入「{name}」，共 {items.Count} 个单词。", "导入词典");
    }

    private void RefreshDictChoices(string? select)
    {
        if (_dictCombo is null) return;
        var names = _main.Library.Select(d => d.Name).ToArray();
        _dictCombo.ItemsSource = names;
        _dictCombo.SelectedItem = select is not null && names.Contains(select)
            ? select
            : names.FirstOrDefault();
    }

    // ---------- 清空进度 ----------
    /// <summary>清空学习进度。破坏性操作，放在设置最下方并要求二次确认。</summary>
    private void ResetRow()
    {
        var g = NewRow();
        var btn = new FluentButton
        {
            Content = "清空学习进度…",
            Appearance = ControlAppearance.Danger,
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 130,
            FontFamily = TextFont,
            FontSize = 13
        };
        btn.Click += async (_, _) => await ClearProgressAsync();
        Grid.SetColumn(btn, 0);
        Grid.SetColumnSpan(btn, 3);
        g.Children.Add(btn);

        Note("清空所有词典的学习位置，以及全部单词的复习阶段、连续天数、首次成功、二次成功、已完成记录。外观与发音设置不受影响。");
    }

    private async Task ClearProgressAsync()
    {
        // 二次确认：这是不可撤销的破坏性操作
        bool ok = await Confirm(
            "确定要清空全部学习进度吗？\n\n" +
            "即将删除：\n" +
            "· 各词典的学习位置（回到第一个单词）\n" +
            "· 所有单词的复习阶段、连续天数、首次成功、二次成功、已完成记录\n\n" +
            "此操作无法撤销。",
            "清空学习进度", "清空");

        if (!ok) return;

        _main.ClearProgress();   // 清数据并把悬浮窗拉回学习模式第一个词
        RefreshProgress();       // 刷新本页进度条
        await Info("学习进度已清空，所有单词回到「未学」状态。");
    }

    /// <summary>AI 配置测试：用当前配置真实发一次请求，把台词或错误原因原样显示出来。</summary>
    private void TestAiRow()
    {
        var g = NewRow();
        var btn = new FluentButton
        {
            Content = "测试 AI 连接…",
            Appearance = ControlAppearance.Secondary,
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 130,
            FontFamily = TextFont,
            FontSize = 13
        };
        btn.Click += async (_, _) =>
        {
            btn.IsEnabled = false;
            bool ok;
            string text;
            try
            {
                // 忽略缓存，确保每次都是真实请求（否则第一次成功后就一直是缓存结果）
                var r = await _ai.FetchAsync("abandon", _s, CancellationToken.None, useCache: false);
                ok = r.Ok;
                text = r.Ok ? "调用成功。\n\n以单词 abandon 为例：\n" + r.Quote!.ToLines()
                            : "调用失败：\n\n" + r.Error;
            }
            catch (OperationCanceledException)
            {
                ok = false;
                text = "请求超时或被取消（默认超时 20 秒）。";
            }
            catch (Exception ex)
            {
                Log.Error("AI 测试失败", ex);
                ok = false;
                text = "测试过程出错：\n\n" + ex.Message;
            }
            finally
            {
                btn.IsEnabled = true;
            }

            Log.Info($"AI 测试结果 ok={ok}：" + text.Replace("\n", " "));
            await ShowResultAsync(text, "AI 测试");
        };
        Grid.SetColumn(btn, 0);
        Grid.SetColumnSpan(btn, 3);
        g.Children.Add(btn);
    }

    /// <summary>弹窗自身出问题也不能把程序带崩（异常会被全局日志兜住）。</summary>
    private async Task ShowResultAsync(string message, string title)
    {
        try
        {
            var box = new Wpf.Ui.Controls.MessageBox
            {
                Title = title,
                Content = message,
                CloseButtonText = "知道了"
            };
            await box.ShowDialogAsync();
        }
        catch (Exception ex)
        {
            Log.Error("弹窗显示失败", ex);
        }
    }

    /// <summary>日志版块：方便出问题时一键打开现场记录。</summary>
    private void LogRow()
    {
        var g = NewRow();
        var btn = new FluentButton
        {
            Content = "打开日志文件…",
            Appearance = ControlAppearance.Secondary,
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 130,
            FontFamily = TextFont,
            FontSize = 13
        };
        btn.Click += (_, _) =>
        {
            try
            {
                Log.Info("用户从设置里打开了日志");
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Error("打开日志失败", ex);
            }
        };
        Grid.SetColumn(btn, 0);
        Grid.SetColumnSpan(btn, 3);
        g.Children.Add(btn);

        Note("崩溃、AI 调用等现场都记在这里（超过 1MB 会轮换成 .log.1）：" + Log.FilePath);
    }

    // 用 WPF-UI 的 Fluent 对话框，避免系统原生（白底）弹窗在深色界面里突兀
    private async Task Warn(string message, string title = "提示")
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = message,
            CloseButtonText = "知道了"
        };
        await box.ShowDialogAsync();
    }

    private async Task Info(string message, string title = "提示")
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = message,
            CloseButtonText = "好的"
        };
        await box.ShowDialogAsync();
    }

    private async Task<bool> Confirm(string message, string title = "确认", string primary = "确定")
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = message,
            PrimaryButtonText = primary,
            CloseButtonText = "取消"
        };
        var result = await box.ShowDialogAsync();
        return result == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }

    private void ColorRow(string label, string[] presets, Func<string> get, Action<string> set)
    {
        var g = NewRow();
        RowLabel(g, label);

        var panel = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };

        foreach (var hex in presets)
        {
            var b = new Button
            {
                Width = 22,
                Height = 22,
                Margin = new Thickness(0, 0, 6, 6),
                Background = Theme.Brush(hex),
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = hex,
                Template = SwatchTemplate()
            };
            b.Click += (_, _) => { set(hex); _main.RefreshFromSettings(); };
            panel.Children.Add(b);
        }

        var custom = new FluentButton
        {
            Content = "自定义",
            Appearance = ControlAppearance.Secondary,
            Height = 22,
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(2, 0, 0, 6),
            FontSize = 11,
            FontFamily = TextFont
        };
        custom.Click += (_, _) => PickCustomColor(label, get, set);
        panel.Children.Add(custom);

        Grid.SetColumn(panel, 1);
        Grid.SetColumnSpan(panel, 2);
        g.Children.Add(panel);
    }

    /// <summary>色块模板：4px 圆角 + 一圈浅描边（Fluent 控件的玻璃边）。</summary>
    private static ControlTemplate SwatchTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)));
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    /// <summary>没有内置取色器，这里用一个输入十六进制颜色的简单对话框。</summary>
    private void PickCustomColor(string title, Func<string> get, Action<string> set)
    {
        var win = new FluentWindow
        {
            Title = title,
            Width = 300,
            Height = 200,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowBackdropType = WindowBackdropType.Mica,
            ExtendsContentIntoTitleBar = true
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var bar = new FluentTitleBar { Title = title, ShowMinimize = false, ShowMaximize = false, ShowClose = true };
        Grid.SetRow(bar, 0);
        root.Children.Add(bar);

        var sp = new StackPanel { Margin = new Thickness(22, 4, 22, 18) };
        var hint = new TextBlock
        {
            Text = "输入十六进制颜色，例如 #60CDFF",
            FontFamily = TextFont,
            FontSize = 12
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        sp.Children.Add(hint);

        var tb = new TextBox
        {
            Text = get(),
            Margin = new Thickness(0, 10, 0, 16),
            FontFamily = MonoFont,
            FontSize = 13
        };
        sp.Children.Add(tb);

        var ok = new FluentButton
        {
            Content = "确定",
            Appearance = ControlAppearance.Primary,
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 88,
            FontFamily = TextFont
        };
        ok.Click += (_, _) =>
        {
            var v = tb.Text.Trim();
            if (v.Length > 0)
            {
                if (!v.StartsWith('#')) v = "#" + v;
                set(v);
                _main.RefreshFromSettings();
            }
            win.Close();
        };
        sp.Children.Add(ok);

        Grid.SetRow(sp, 1);
        root.Children.Add(sp);
        win.Content = root;
        win.ShowDialog();
    }
}
