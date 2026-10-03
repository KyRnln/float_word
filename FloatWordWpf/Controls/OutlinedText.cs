using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace FloatWordWpf;

/// <summary>
/// 支持描边的文本控件（用于「注释 / 发音」）。
///
/// TextBlock 不支持 Stroke，WPF 里给文字描边的正确做法是
/// <c>FormattedText.BuildGeometry()</c> 取字形轮廓，再用 Pen 描边 —— 与 WordCanvas 同一套思路。
///
/// 两个关键点：
///   1. 总是给 <c>FormattedText</c> 设 <c>MaxTextWidth</c>：文本按这个宽度换行。
///      行内为左对齐（释义按词性分行后左对齐更整齐），
///      多行文本的每一行都从块左边缘开始。
///   2. 控件尺寸取「墨迹范围」而不是对齐宽度：这样控件恰好包住文字，
///      既不会被裁切，也不会因为 MaxTextWidth 很大而把窗口撑宽；
///      **整块的水平居中**交给外层 <c>HorizontalAlignment="Center"</c> 完成。
/// </summary>
public class OutlinedText : FrameworkElement
{
    private string _text = "";
    private string _family = "Segoe UI Variable Text";
    private double _fontSize = 13;
    private bool _bold;
    private double _outlineWidth;
    private double _textMaxWidth = double.PositiveInfinity;

    private FormattedText? _ft;
    private Size _size;
    private double _dx, _dy;   // 把几何图形平移到控件左上角

    public string Text { get => _text; set { if (_text != value) { _text = value; Relayout(); } } }
    public string FontFamilyName { get => _family; set { if (_family != value) { _family = value; Relayout(); } } }
    public double FontSize { get => _fontSize; set { if (Math.Abs(_fontSize - value) > 0.01) { _fontSize = value; Relayout(); } } }
    public bool Bold { get => _bold; set { if (_bold != value) { _bold = value; Relayout(); } } }
    public double OutlineWidth { get => _outlineWidth; set { if (Math.Abs(_outlineWidth - value) > 0.01) { _outlineWidth = value; Relayout(); } } }

    /// <summary>换行宽度上限（不用基类的 MaxWidth，避免与布局约束混淆）。</summary>
    public double TextMaxWidth { get => _textMaxWidth; set { if (Math.Abs(_textMaxWidth - value) > 0.01) { _textMaxWidth = value; Relayout(); } } }

    private Brush _textBrush = Theme.Brush(Theme.Fg);
    private Brush _outlineBrush = Brushes.Black;

    // 画笔只影响上色、不影响排版，所以只重绘、不重新测量
    public Brush TextBrush { get => _textBrush; set { _textBrush = value; InvalidateVisual(); } }
    public Brush OutlineBrush { get => _outlineBrush; set { _outlineBrush = value; InvalidateVisual(); } }

    private Typeface Face() =>
        new(new FontFamily(FontFamilyName), FontStyles.Normal,
            Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);

    private double Dpi()
    {
        try { return VisualTreeHelper.GetDpi(this).PixelsPerDip; }
        catch { return 1.0; }
    }

    private FormattedText Make(double maxTextWidth) =>
        new(_text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            Face(), FontSize, _textBrush, Dpi())
        {
            // 行内左对齐；整块的水平居中由外层 HorizontalAlignment 负责
            TextAlignment = TextAlignment.Left,
            MaxTextWidth = Math.Max(1, maxTextWidth)
        };

    /// <summary>文字 / 字体 / 字号 / 描边 / 限宽变化后重新测量。</summary>
    public void Relayout()
    {
        if (string.IsNullOrEmpty(_text))
        {
            _ft = null;
            _size = new Size(0, 0);
            InvalidateMeasure();
            InvalidateVisual();
            return;
        }

        double pad = OutlineWidth * 2;

        // 不限宽时用「自然宽度」当对齐宽度，保证 TextAlignment 仍然生效（多行才会居中对齐）
        double limit = double.IsInfinity(TextMaxWidth)
            ? Make(1_000_000).Width + 1     // 先量一次自然宽度
            : TextMaxWidth - pad;
        limit = Math.Max(1, limit);

        var ft = Make(limit);
        var geo = ft.BuildGeometry(new Point(0, 0));
        if (geo is null)
        {
            _ft = null;
            _size = new Size(0, 0);
            InvalidateMeasure();
            InvalidateVisual();
            return;
        }

        var b = geo.Bounds;
        double top = Math.Min(0, b.Top);      // 墨迹可能高于行顶（重音符号等）

        _ft = ft;
        _dx = -b.Left + OutlineWidth;
        _dy = OutlineWidth - top;
        _size = new Size(b.Width + pad, ft.Height - top + pad);

        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => _size;

    protected override void OnRender(DrawingContext dc)
    {
        if (_ft is null) return;

        var geo = _ft.BuildGeometry(new Point(0, 0));
        if (geo is null) return;
        geo.Transform = new TranslateTransform(_dx, _dy);

        if (OutlineWidth > 0)
            dc.DrawGeometry(null, new Pen(OutlineBrush, OutlineWidth) { LineJoin = PenLineJoin.Round }, geo);
        dc.DrawGeometry(TextBrush, null, geo);
    }
}
