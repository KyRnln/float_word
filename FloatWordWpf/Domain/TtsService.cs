namespace FloatWordWpf;

/// <summary>
/// 语音播报的统一入口：按设置里的「TTS 引擎」把朗读分发给本地 Piper 或火山引擎。
/// 悬浮窗只依赖这个类，切换引擎不改动调用方。
/// </summary>
public sealed class TtsService : IDisposable
{
    private readonly PiperService _piper = new();
    private readonly VolcanoTtsService _volcano = new();

    public void Speak(string text, AppSettings s)
    {
        // 切引擎时先停掉另一个，避免上一句还在响
        if (s.TtsEngine == "volcano")
        {
            _piper.Stop();
            _volcano.Speak(text, s);
        }
        else
        {
            _volcano.Stop();
            _piper.Speak(text, s);
        }
    }

    public void Stop()
    {
        _piper.Stop();
        _volcano.Stop();
    }

    public void Dispose()
    {
        _piper.Dispose();
        _volcano.Dispose();
    }
}
