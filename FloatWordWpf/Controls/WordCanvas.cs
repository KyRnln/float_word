using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FloatWordWpf;

/// <summary>
/// 逐字绘制的单词控件：每个字符独立着色，并给字形几何描边。
/// 描边做法是 FormattedText.BuildGeometry() 取字形轮廓 + Pen 描边
/// （TextBlock 本身不支持 Stroke，这是 WPF 里做文字描边的正确方式）。
///
/// 由于窗口使用 AllowsTransparency，WPF 输出自带 per-pixel alpha，
/// 字号再大、背景再透明，边缘也不会出现颜色键方案那种"深色毛边"。
/// </summary>
public class WordCanvas : FrameworkElement
{
    /// <summary>提示框横向移动的动画时长（毫秒）。</summary>
    private const int HintAnimMs = 170;

    private readonly List<double> _cellX = new();
    private readonly List<double> _cellW = new();
    private double _lineHeight;

    private string _word = "";
    private int _typed;
    private bool _reveal = true;
    private bool _error;
    private string _family = "Consolas";
    private double _fontSize = 28;
    private bool _bold = true;
    private double _outlineWidth = 2;

    public string Word { get => _word; set { if (_word != value) { _word = value; Relayout(); } } }
    public int Typed
    {
        get => _typed;
        set
        {
            if (_typed == value) return;
            _typed = value;
            AnimateHint();      // 待输入框横向缓动滑到新的一格
            InvalidateVisual();
        }
    }
    public bool Reveal { get => _reveal; set { if (_reveal != value) { _reveal = value; InvalidateVisual(); } } }
    public bool Error { get => _error; set { if (_error != value) { _error = value; InvalidateVisual(); } } }

    public string FontFamilyName { get => _family; set { if (_family != value) { _family = value; Relayout(); } } }
    public double FontSize { get => _fontSize; set { if (Math.Abs(_fontSize - value) > 0.01) { _fontSize = value; Relayout(); } } }
    public bool Bold { get => _bold; set { if (_bold != value) { _bold = value; Relayout(); } } }
    public double OutlineWidth { get => _outlineWidth; set { if (Math.Abs(_outlineWidth - value) > 0.01) { _outlineWidth = value; Relayout(); } } }

    private bool _uniformCells;
    /// <summary>true = 等宽槽位排版（默写模式），false = 按字母真实宽度（学习模式）。</summary>
    public bool UniformCells { get => _uniformCells; set { if (_uniformCells != value) { _uniformCells = value; Relayout(); } } }

    public Brush TextBrush { get; set; } = Theme.Brush(Theme.Fg);
    public Brush DoneBrush { get; set; } = Theme.Brush(Theme.Green);
    public Brush ErrorBrush { get; set; } = Theme.Brush(Theme.Red);
    public Brush MutedBrush { get; set; } = Theme.Brush(Theme.Muted);
    public Brush HintBrush { get; set; } = Theme.Brush(Theme.BgSoft);
    public Brush OutlineBrush { get; set; } = Brushes.Black;

    // 待输入框的位置与宽度做成依赖属性，才能用 DoubleAnimation 做缓动。
    public static readonly DependencyProperty HintXProperty =
        DependencyProperty.Register(nameof(HintX), typeof(double), typeof(WordCanvas),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HintWProperty =
        DependencyProperty.Register(nameof(HintW), typeof(double), typeof(WordCanvas),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double HintX
    {
        get => (double)GetValue(HintXProperty);
        set => SetValue(HintXProperty, value);
    }

    public double HintW
    {
        get => (double)GetValue(HintWProperty);
        set => SetValue(HintWProperty, value);
    }

    /// <summary>把待输入框瞬间定位到当前格，不做动画（换词 / 改字体时用）。</summary>
    private void SnapHint()
    {
        BeginAnimation(HintXProperty, null);
        BeginAnimation(HintWProperty, null);
        if (_cellX.Count == 0 || _typed < 0 || _typed >= _cellX.Count) return;
        HintX = _cellX[_typed];
        HintW = _cellW[_typed];
    }

    /// <summary>待输入框从上一格缓动滑到当前格。</summary>
    private void AnimateHint()
    {
        if (_cellX.Count == 0 || _typed < 0 || _typed >= _cellX.Count) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = new Duration(TimeSpan.FromMilliseconds(HintAnimMs));
        BeginAnimation(HintXProperty, new DoubleAnimation(_cellX[_typed], dur) { EasingFunction = ease });
        BeginAnimation(HintWProperty, new DoubleAnimation(_cellW[_typed], dur) { EasingFunction = ease });
    }

    private Typeface Face() =>
        new(new FontFamily(FontFamilyName), FontStyles.Normal,
            Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);

    private double Dpi()
    {
        try { return VisualTreeHelper.GetDpi(this).PixelsPerDip; }
        catch { return 1.0; }
    }

    private FormattedText Make(string s, Brush brush) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            Face(), FontSize, brush, Dpi());

    /// <summary>内容 / 字体 / 字号变化后重新测量每格宽度。</summary>
    public void Relayout()
    {
        _cellX.Clear();
        _cellW.Clear();
        _lineHeight = 0;

        if (!string.IsNullOrEmpty(_word))
        {
            double pad = OutlineWidth + 2;

            if (UniformCells)
            {
                // 默写模式：等宽槽位排版。
                // 以每个字为基准量出所需宽度，取**最大值**作为统一槽宽 ——
                // 这样占位横线等宽、间距看起来一致，而且揭示字母时不会跳动。
                // （用字母自身宽度当格子则必然疏密不匀：i/l 窄、m/w 宽）
                double maxW = 0;
                foreach (var ch in _word)
                {
                    var probe = Make(ch.ToString(), Brushes.White);
                    maxW = Math.Max(maxW, probe.WidthIncludingTrailingWhitespace);
                    _lineHeight = Math.Max(_lineHeight, probe.Height);
                }
                // 全是窄字母（如 ill）时也不至于挤成一条
                double slot = Math.Max(maxW, _lineHeight * 0.42);
                for (int i = 0; i < _word.Length; i++)
                {
                    _cellX.Add(pad + i * slot);
                    _cellW.Add(slot);
                }
            }
            else
            {
                // 学习模式：按字母真实宽度排（正常单词外观）
                double x = pad;
                foreach (var ch in _word)
                {
                    var probe = Make(ch.ToString(), Brushes.White);
                    double w = probe.WidthIncludingTrailingWhitespace;
                    _cellX.Add(x);
                    _cellW.Add(w);
                    x += w;
                    _lineHeight = Math.Max(_lineHeight, probe.Height);
                }
            }
        }

        InvalidateMeasure();
        InvalidateVisual();
        SnapHint();     // 换词 / 改字体后立刻归位，不播滑动动画
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = _cellX.Count > 0 ? _cellX[^1] + _cellW[^1] : 0;
        return new Size(w + OutlineWidth + 2, _lineHeight + OutlineWidth * 2);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_cellX.Count == 0) return;

        var pen = OutlineWidth > 0
            ? new Pen(OutlineBrush, OutlineWidth) { LineJoin = PenLineJoin.Round }
            : null;

        for (int i = 0; i < _cellX.Count; i++)
        {
            double x = _cellX[i];
            double y = OutlineWidth;

            // 当前待输入位置的高亮块（Fluent 风格：4px 圆角），位置与宽度带缓动
            if (!Error && i == Typed && Typed < _cellX.Count)
                dc.DrawRoundedRectangle(HintBrush, null,
                    new Rect(HintX, y, Math.Max(HintW, 0), _lineHeight), 4, 4);

            bool done = i < Typed;
            // 已输入的正确字符、或已揭示答案时显示字母；否则显示占位
            bool showChar = Reveal || (done && !Error);
            Brush fill = Error ? ErrorBrush : (done ? DoneBrush : (Reveal ? TextBrush : MutedBrush));

            if (!showChar)
            {
                // 占位用**等宽横线**（填字风格）：
                // 宽度只由槽宽决定（槽宽已统一），所以每一条都一样宽、间距均匀，
                // 且与具体是哪个字母、用哪种字体都无关。
                double barW = _cellW[i] * 0.72;
                double barH = Math.Clamp(FontSize * 0.06, 2, 6);
                dc.DrawRoundedRectangle(fill, null,
                    new Rect(x + (_cellW[i] - barW) / 2,
                             y + _lineHeight * 0.75 - barH / 2,
                             barW, barH),
                    barH / 2, barH / 2);
                continue;
            }

            var ft = Make(_word[i].ToString(), fill);
            // 字母宽度可能小于格子宽度，居中放进同一格，避免揭示时位置跳动
            double dx = x + Math.Max(0, (_cellW[i] - ft.WidthIncludingTrailingWhitespace) / 2);
            var geo = ft.BuildGeometry(new Point(dx, y));

            // 先描边、再填色 → 得到干净的“外描边”
            if (pen is not null) dc.DrawGeometry(null, pen, geo);
            dc.DrawGeometry(fill, null, geo);
        }
    }
}
