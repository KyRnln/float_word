using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Media;
using System.Text.Json;
using System.Windows;

namespace FloatWordWpf;

/// <summary>
/// 离线神经语音（Piper）。
///
/// 关键点：piper.exe 每次启动都要重新加载 60MB 模型（约 450ms），
/// 所以这里**常驻一个进程**，用 --json-input 逐行喂文本、--output_dir 收 WAV，
/// 实测每词降到约 60~120ms。
///
/// 为什么不用 stdout 取音频：piper 写管道时无法回填 RIFF 的长度字段，
/// 按长度切分会错位，所以走文件输出（文件路径它会正确回填）。
///
/// 并发：同一时刻只允许一次合成（信号量），并且**失败的请求也一定把自己的
/// 产物消费掉**，否则残留文件会被下一个词误当成自己的音频。
/// </summary>
public sealed class PiperService : IDisposable
{
    // 用 winmm 的 SoundPlayer 播放，而不是 WPF 的 MediaPlayer：
    // MediaPlayer 依赖 Windows「媒体功能」，用户禁用「Windows Media Player」后就没声了；
    // SoundPlayer 走 PlaySound，不受该开关影响。代价是没有独立音量 → 把音量并入采样增益。
    private SoundPlayer? _player;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _procLock = new();

    private readonly string _outDir = Path.Combine(Path.GetTempPath(), "floatword_piper");
    private readonly string _playFile = Path.Combine(Path.GetTempPath(), "floatword_play.wav");

    private Process? _proc;
    private string _voice = "";
    private double _lengthScale = 1.0;
    private int _gen;
    private bool _disposed;

    public static string BinPath => Path.Combine(AppPaths.AppDir, "piper", "bin", "piper.exe");
    public static string VoicesDir => Path.Combine(AppPaths.AppDir, "piper", "voices");

    /// <summary>引擎 + 至少一个语音模型都在才算可用。</summary>
    public static bool Available => File.Exists(BinPath) && AvailableVoices().Length > 0;

    /// <summary>voices 目录下的 *.onnx，文件名即音色名。</summary>
    public static string[] AvailableVoices()
    {
        try
        {
            if (!Directory.Exists(VoicesDir)) return Array.Empty<string>();
            return Directory.EnumerateFiles(VoicesDir, "*.onnx")
                            .Select(Path.GetFileNameWithoutExtension)
                            .Where(n => !string.IsNullOrWhiteSpace(n))
                            .Select(n => n!)
                            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                            .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public PiperService()
    {
        try
        {
            Directory.CreateDirectory(_outDir);
            CleanDir();
        }
        catch { /* 临时目录不可用时不致命 */ }
    }

    public void Speak(string text, AppSettings s)
    {
        if (_disposed || string.IsNullOrWhiteSpace(text) || !Available) return;

        int my = Interlocked.Increment(ref _gen);

        // 立刻停掉上一句（"只播最新"）
        Stop();

        // 增益与音量一起写进采样：SoundPlayer 没有独立音量通道
        double gain = Math.Clamp(s.Gain / 100.0, 0, 2);
        double volume = Math.Clamp(s.Volume / 100.0, 0, 1);
        double factor = gain * volume;
        // piper 的 length_scale 与语速成反比：1.0 正常，0.5 快一倍
        double lengthScale = Math.Pow(2, -Math.Clamp(s.Rate, -10, 10) / 10.0);
        string voice = s.Voice;

        _ = Task.Run(async () =>
        {
            try
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_disposed) return;
                    // 已经过时的请求：还没发送，直接跳过，省一次合成
                    if (my != Volatile.Read(ref _gen)) return;

                    var wav = await SynthAsync(text, voice, lengthScale).ConfigureAwait(false);
                    if (wav is null || _disposed) return;

                    // 合成期间又来了新请求 → 丢弃这条
                    if (my != Volatile.Read(ref _gen)) return;

                    if (Math.Abs(factor - 1) > 0.001) wav = AmplifyWav(wav, factor);
                    File.WriteAllBytes(_playFile, wav);
                }
                finally
                {
                    _gate.Release();
                }

                await Application.Current!.Dispatcher.InvokeAsync(() =>
                {
                    if (_disposed || my != Volatile.Read(ref _gen)) return;
                    try
                    {
                        // 每次都新建实例：SoundPlayer 会缓存已加载的音频，复用会一直播旧内容
                        var p = new SoundPlayer(_playFile);
                        _player = p;
                        p.Play();
                    }
                    catch { }
                });
            }
            catch
            {
                // 合成 / 播放失败就静默，不打扰使用
            }
        });
    }

    private async Task<byte[]?> SynthAsync(string text, string voice, double lengthScale)
    {
        var proc = EnsureProcess(voice, lengthScale);
        if (proc is null || proc.HasExited) return null;

        CleanDir();      // 清掉残留（我们持有信号量，不会有并发产物）

        var line = "{\"text\":" + JsonSerializer.Serialize(text) + "}\n";
        try
        {
            await proc.StandardInput.WriteAsync(line).ConfigureAwait(false);
            await proc.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch
        {
            KillProcess();
            return null;
        }

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 8000)
        {
            foreach (var file in Directory.EnumerateFiles(_outDir, "*.wav"))
            {
                var bytes = TryReadComplete(file);
                if (bytes is not null)
                {
                    try { File.Delete(file); } catch { }
                    return bytes;
                }
            }
            await Task.Delay(4).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>文件能被独占打开说明 piper 已写完，否则下次再试。</summary>
    private static byte[]? TryReadComplete(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            if (fs.Length <= 44) return null;
            var buffer = new byte[fs.Length];
            fs.ReadExactly(buffer);
            return buffer;
        }
        catch
        {
            return null;
        }
    }

    private Process? EnsureProcess(string voice, double lengthScale)
    {
        lock (_procLock)
        {
            if (_proc is { HasExited: false }
                && _voice == voice
                && Math.Abs(_lengthScale - lengthScale) < 0.001)
            {
                return _proc;
            }

            KillProcess();

            var model = Path.Combine(VoicesDir, voice + ".onnx");
            if (!File.Exists(model) || !File.Exists(BinPath)) return null;

            var psi = new ProcessStartInfo(BinPath)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(BinPath)!
            };
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(model);
            psi.ArgumentList.Add("--json-input");
            psi.ArgumentList.Add("--output_dir");
            psi.ArgumentList.Add(_outDir);
            psi.ArgumentList.Add("--length_scale");
            psi.ArgumentList.Add(lengthScale.ToString("0.####", CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--quiet");

            try
            {
                var p = Process.Start(psi);
                if (p is null) return null;

                // 必须把 stdout/stderr 排空，否则管道写满会让 piper 卡住
                p.OutputDataReceived += (_, _) => { };
                p.ErrorDataReceived += (_, _) => { };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                _proc = p;
                _voice = voice;
                _lengthScale = lengthScale;
                return p;
            }
            catch
            {
                return null;
            }
        }
    }

    private void CleanDir()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_outDir, "*.wav"))
            {
                try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    private void KillProcess()
    {
        var p = _proc;
        _proc = null;
        if (p is null) return;
        try
        {
            p.StandardInput.Close();
        }
        catch { }
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch { }
        try { p.Dispose(); } catch { }
    }

    /// <summary>16bit PCM WAV 逐采样放大并截断（实现"播报增益"）。</summary>
    private static byte[] AmplifyWav(byte[] wav, double gain)
    {
        try
        {
            if (wav.Length < 44) return wav;

            int pos = 12;
            int bitsPerSample = 16;
            int dataStart = -1, dataSize = 0;

            while (pos + 8 <= wav.Length)
            {
                int size = BitConverter.ToInt32(wav, pos + 4);
                if (wav[pos] == 'f' && wav[pos + 1] == 'm' && wav[pos + 2] == 't')
                {
                    if (pos + 8 + 16 <= wav.Length)
                        bitsPerSample = BitConverter.ToInt16(wav, pos + 8 + 14);
                }
                else if (wav[pos] == 'd' && wav[pos + 1] == 'a' &&
                         wav[pos + 2] == 't' && wav[pos + 3] == 'a')
                {
                    dataStart = pos + 8;
                    dataSize = Math.Min(size, wav.Length - dataStart);
                    break;
                }
                if (size < 0) break;
                pos += 8 + size + (size % 2);
            }

            if (dataStart < 0 || bitsPerSample != 16) return wav;

            int end = dataStart + dataSize;
            for (int p = dataStart; p + 1 < end; p += 2)
            {
                short v = (short)(wav[p] | (wav[p + 1] << 8));
                int x = (int)Math.Round(v * gain);
                if (x > short.MaxValue) x = short.MaxValue;
                else if (x < short.MinValue) x = short.MinValue;
                wav[p] = (byte)(x & 0xFF);
                wav[p + 1] = (byte)((x >> 8) & 0xFF);
            }
            return wav;
        }
        catch
        {
            return wav;
        }
    }

    public void Stop()
    {
        var p = _player;
        _player = null;
        try { p?.Stop(); } catch { }
    }

    public void Dispose()
    {
        _disposed = true;
        Interlocked.Increment(ref _gen);
        Stop();
        lock (_procLock) KillProcess();
        _gate.Dispose();
    }
}
