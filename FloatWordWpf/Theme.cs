using System.Globalization;
using System.Windows.Media;

namespace FloatWordWpf;

/// <summary>
/// 调色板与颜色工具。
///
/// 取值对齐 **Fluent 2（Windows 11 深色）** 设计令牌：
///   卡片底 CardBackgroundFillColorDefault = #2B2B2B
///   控件填充 ControlFillColorDefault      = #323232
///   主文本 TextFillColorPrimary           = #FFFFFF
///   次文本 TextFillColorSecondary         = #C5C5C5
///   三级文本 TextFillColorTertiary        = #8A8A8A
///   强调色 SystemAccentColor（深色）      = #60CDFF
///   成功 #6CCB5F / 危险 #FF99A4
/// 窗口与设置界面的底色、描边、悬停等直接复用 WPF-UI 的令牌资源，
/// 这里只保留「需要以代码计算 alpha」的那部分颜色。
/// </summary>
public static class Theme
{
    public const string Bg = "#2B2B2B";
    public const string BgSoft = "#323232";
    public const string Fg = "#FFFFFF";
    public const string FgSecondary = "#C5C5C5";
    public const string Accent = "#60CDFF";
    public const string Green = "#6CCB5F";
    public const string Red = "#FF99A4";
    public const string Muted = "#8A8A8A";

    /// <summary>Fluent 控件描边（白 8%），叠在卡片上模拟亚克力的玻璃边。</summary>
    public const string CardStroke = "#14FFFFFF";

    public static readonly string[] ColorPresets =
    {
        "#60CDFF", "#6CCB5F", "#FCE100", "#FFB77C",
        "#FF99A4", "#B69CFF", "#57D9C8", "#FFFFFF"
    };

    public static readonly string[] OutlinePresets =
    {
        "#000000", "#0A0A0A", "#1F1F1F", "#2B2B2B",
        "#FFFFFF", "#FCE100", "#60CDFF", "#FF99A4"
    };

    public static readonly string[] HintPresets =
    {
        "#2B2B2B", "#323232", "#3D3D3D", "#1F1F1F",
        "#60CDFF", "#FCE100", "#6CCB5F", "#FF99A4"
    };

    public static readonly string[] FontChoices =
    {
        "Segoe UI Variable Display", "Segoe UI Variable Text", "Segoe UI",
        "Consolas", "Cascadia Mono", "Arial", "Verdana", "Tahoma", "Calibri",
        "Georgia", "Times New Roman", "Cambria", "Courier New",
        "Comic Sans MS", "Microsoft YaHei UI", "SimHei", "KaiTi"
    };

    /// <summary>把 #RRGGBB 解析成 Color，非法输入回退到灰色。</summary>
    public static Color Col(string hex)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            return Color.FromRgb(0x8A, 0x8A, 0x8A);
        }
    }

    public static SolidColorBrush Brush(string hex) => new(Col(hex));

    /// <summary>带透明度的画刷，alpha 取 0..1。</summary>
    public static SolidColorBrush Brush(string hex, double alpha)
    {
        var c = Col(hex);
        byte a = (byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255);
        return new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B));
    }

    /// <summary>
    /// 卡片背景专用：保证 alpha 字节至少为 1。
    /// 因为 alpha 恰好为 0 时 WPF 会跳过该元素的命中测试，
    /// 卡片空白处收不到鼠标事件 → 窗口就拖不动了（实测 bg=0 完全拖不动）。
    /// 下限取字节 1（1/255 ≈ 0.4%），肉眼与全透明无异，但命中测试恢复正常。
    /// 注意不能写成 0.001：round(0.001*255)=0，等于没限制。
    /// </summary>
    public static SolidColorBrush CardBrush(string hex, double alpha)
    {
        var c = Col(hex);
        byte a = (byte)Math.Clamp(Math.Round(Math.Clamp(alpha, 0, 1) * 255), 1, 255);
        return new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B));
    }

    /// <summary>把颜色 c1 按比例 a 混向 c2（a=1 取 c1）。</summary>
    public static Color Blend(string c1, string c2, double a)
    {
        a = Math.Clamp(a, 0, 1);
        var x = Col(c1);
        var y = Col(c2);
        return Color.FromRgb(
            (byte)Math.Round(x.R * a + y.R * (1 - a)),
            (byte)Math.Round(x.G * a + y.G * (1 - a)),
            (byte)Math.Round(x.B * a + y.B * (1 - a)));
    }

    public static string Hex(Color c) =>
        string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);

    /// <summary>用于复制到剪贴板的颜色字符串。</summary>
    public static string ToHexString(this Color c) => Hex(c);
}
