using System.IO;
using System.Windows;

namespace FloatWordWpf;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var settings = AppSettings.Load(AppPaths.ConfigFile);
        var library = DictionaryLibrary.Load(AppPaths.AppDir);

        // 当前用 Piper 离线语音：把配置里的音色归一到本机已有的语音模型，
        // 保证设置里显示的和实际发声的一致。
        var voices = PiperService.AvailableVoices();
        if (voices.Length > 0 && !voices.Contains(settings.Voice)) settings.Voice = voices[0];

        var win = new MainWindow(settings, library);
        MainWindow = win;
        win.Show();
    }
}

/// <summary>程序所在目录 / 配置文件路径。</summary>
public static class AppPaths
{
    public static string AppDir => AppContext.BaseDirectory;

    public static string ConfigFile => Path.Combine(AppDir, "floatword_config.json");
}
