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

        Check("显示词典名称", () => _s.ShowDictName, v => _s.ShowDictName = v);

        SliderRow("背景透明度", 0, 100, () => _s.BgAlpha, v => _s.BgAlpha = v);
        SliderRow("文字透明度", 0, 100, () => _s.TextAlpha, v => _s.TextAlpha = v);
        SliderRow("单词字号", 14, 72, () => _s.FontSize, v => _s.FontSize = v);
        SliderRow("音标字号", 8, 32, () => _s.PhonSize, v => _s.PhonSize = v);
        SliderRow("释义字号", 8, 36, () => _s.MeanSize, v => _s.MeanSize = v);
        SliderRow("描边宽度", 0, 8, () => _s.OutlineW, v => _s.OutlineW = v);

        ColorRow("文字颜色", Theme.ColorPresets, () => _s.TextColor, v => _s.TextColor = v);
        ColorRow("描边颜色", Theme.OutlinePresets, () => _s.OutlineColor, v => _s.OutlineColor = v);
        ColorRow("提示框颜色", Theme.HintPresets, () => _s.HintColor, v => _s.HintColor = v);
        SliderRow("提示框透明度", 0, 100, () => _s.HintAlpha, v => _s.HintAlpha = v);

        Choice("字体", Theme.FontChoices, () => _s.FontFamily, v => _s.FontFamily = v);
        Check("单词加粗", () => _s.FontBold, v => _s.FontBold = v);
        Check("工具栏常显（否则鼠标移入才显示）", () => _s.ToolbarPinned, v => _s.ToolbarPinned = v);

        Section("发音（Piper 离线神经语音）");

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

        Note("提示：鼠标移入悬浮窗显示工具栏；点击窗口后直接在单词上逐字输入；← → 切换单词。");
        Note("WPF 版使用真 per-pixel alpha，半透明背景下文字边缘也不会有毛边，因此不需要「清晰文字」开关。");
        Note($"设置自动保存在 {AppPaths.ConfigFile}");
    }

    // ---------- 行构造 ----------
    private Grid NewRow()
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(106) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _body.Children.Add(g);
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
        _body.Children.Add(t);
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
        _body.Children.Add(t);
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
            await Warn("读取失败：" + ex.Message);
            return;
        }

        if (items.Count == 0)
        {
            await Warn("这个文件里没有解析出任何单词。\n\n支持的格式：\n" +
                       "[{ \"word\": \"abandon\", \"phonetic\": \"/əˈbændən/\", \"meaning\": \"v. 放弃\" }]\n\n" +
                       "或 Qwerty Learner 格式：\n" +
                       "[{ \"name\": \"abandon\", \"trans\": [\"v. 放弃\"], \"usphone\": \"ə'bændən\" }]");
            return;
        }

        var name = Path.GetFileNameWithoutExtension(dlg.FileName);
        var dir = Path.Combine(AppPaths.AppDir, "dicts");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, name + ".json");

        if (File.Exists(target) && !await Confirm($"已存在同名词库「{name}」，是否覆盖？"))
            return;

        try
        {
            File.Copy(dlg.FileName, target, true);
        }
        catch (Exception ex)
        {
            await Warn("写入失败：" + ex.Message);
            return;
        }

        // 重新扫描目录 → 刷新下拉 → 切到新导入的词典
        _main.ReloadLibrary();
        RefreshDictChoices(name);
        _main.ChangeDict(name);

        await Info($"已导入「{name}」，共 {items.Count} 个单词。");
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

    // 用 WPF-UI 的 Fluent 对话框，避免系统原生（白底）弹窗在深色界面里突兀
    private async Task Warn(string message)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = "导入词典",
            Content = message,
            CloseButtonText = "知道了"
        };
        await box.ShowDialogAsync();
    }

    private async Task Info(string message)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = "导入词典",
            Content = message,
            CloseButtonText = "好的"
        };
        await box.ShowDialogAsync();
    }

    private async Task<bool> Confirm(string message)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = "导入词典",
            Content = message,
            PrimaryButtonText = "覆盖",
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
