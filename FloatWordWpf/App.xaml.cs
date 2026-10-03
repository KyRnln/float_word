using System.IO;
using System.Windows;

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
        win.Show();
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
