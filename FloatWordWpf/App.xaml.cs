using System.IO;
using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace FloatWordWpf;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        HookCrashLogging();
        Log.Info($"=== FloatWord 启动 === exe 目录：{AppPaths.AppDir}");

        var settings = AppSettings.Load(AppPaths.ConfigFile);
        var library = DictionaryLibrary.Load(AppPaths.AppDir);
        Log.Info($"词典 {library.Count} 本，当前「{settings.Dict}」，AI 台词：{(settings.AiEnabled ? "开" : "关")}");

        // 当前用 Piper 离线语音：把配置里的音色归一到本机已有的语音模型，
        // 保证设置里显示的和实际发声的一致。
        var voices = PiperService.AvailableVoices();
        if (voices.Length > 0 && !voices.Contains(settings.Voice)) settings.Voice = voices[0];

        var win = new MainWindow(settings, library);
        MainWindow = win;
        AppTheme.Apply(settings.UiTheme, win);   // 界面主题：跟随系统 / 深色 / 浅色（先应用再显示，避免闪一下）
        win.Show();
        win.ApplySettings();                     // 主题确定后重算卡片底色（浅色主题用浅色卡片）
        Log.Info("主窗口已显示");
    }

    /// <summary>
    /// 兜底：任何未处理异常都写进日志，并且不让程序直接闪退 ——
    /// 闪退会把正在背的进度一起丢掉，代价比「带病继续跑」更大。
    /// </summary>
    private void HookCrashLogging()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("UI 线程未处理异常", args.Exception);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("非 UI 线程未处理异常", args.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("未观察的任务异常", args.Exception);
            args.SetObserved();
        };
    }
}

/// <summary>程序所在目录 / 配置文件路径。</summary>
public static class AppPaths
{
    public static string AppDir => AppContext.BaseDirectory;

    public static string ConfigFile => Path.Combine(AppDir, "floatword_config.json");
}

/// <summary>
/// 界面深浅色主题的统一入口：把配置里的 system / dark / light 应用到 WPF-UI。
/// 只影响 Fluent 控件（设置窗口、浮窗工具栏 / 提示按钮等）；浮窗文字颜色仍由「外观」单独控制。
/// </summary>
public static class AppTheme
{
    private static bool _watching;
    private static Window? _watched;

    /// <summary>当前是否为浅色主题（供浮窗选择卡片底色）。</summary>
    public static bool IsLight => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light;

    /// <summary>应用主题。<paramref name="theme"/> 取 system / dark / light，其它值按 system。</summary>
    public static void Apply(string theme, Window window)
    {
        try
        {
            if (theme == "light" || theme == "dark")
            {
                StopWatching();
                ApplySafely(() => ApplicationThemeManager.Apply(
                    theme == "light" ? ApplicationTheme.Light : ApplicationTheme.Dark,
                    WindowBackdropType.None,
                    updateAccent: false));
            }
            else if (_watching)
            {
                ApplySafely(() => ApplicationThemeManager.Apply(CurrentSystemTheme(), WindowBackdropType.None,
                                                                updateAccent: false));
            }
            else
            {
                // Watch 首次调用会立即套用系统主题，并在系统深浅色变化时自动跟随
                ApplySafely(() => SystemThemeWatcher.Watch(window, WindowBackdropType.None, updateAccents: false));
                _watching = true;
                _watched = window;
            }
        }
        catch (Exception ex)
        {
            Log.Error("应用界面主题失败", ex);
        }
    }

    /// <summary>
    /// WPF-UI 换主题时会顺手给 Application.MainWindow（浮窗）及其「子窗口」重设背景效果，
    /// 这会把设置窗口的 Mica 一起抹掉。执行期间临时摘掉 MainWindow，让这一步整体跳过。
    /// </summary>
    private static void ApplySafely(Action apply)
    {
        var app = Application.Current;
        var saved = app?.MainWindow;
        if (app is not null) app.MainWindow = null;
        try
        {
            apply();
        }
        finally
        {
            if (app is not null) app.MainWindow = saved;
        }
    }

    private static ApplicationTheme CurrentSystemTheme() =>
        ApplicationThemeManager.GetSystemTheme() is SystemTheme.Dark or SystemTheme.CapturedMotion or SystemTheme.Glow
            ? ApplicationTheme.Dark
            : ApplicationTheme.Light;

    private static void StopWatching()
    {
        if (!_watching) return;
        try
        {
            if (_watched is { IsLoaded: true }) SystemThemeWatcher.UnWatch(_watched);
        }
        catch (Exception ex)
        {
            Log.Error("取消监听系统主题失败", ex);
        }
        _watching = false;
        _watched = null;
    }
}
