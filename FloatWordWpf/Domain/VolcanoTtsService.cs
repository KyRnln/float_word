using System.IO;
using System.Media;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace FloatWordWpf;

/// <summary>
/// 火山引擎（豆包）语音合成 —— V3 HTTP Chunked 单向流式接口。
///
/// 接口要点（见官方文档 6561/1598757）：
///   · POST https://openspeech.bytedance.com/api/v3/tts/unidirectional
///   · 鉴权走请求头：X-Api-Key（新版控制台）+ X-Api-Resource-Id（模型/计费版本）+ X-Api-Request-Id
///   · 请求体 { user, req_params { text, speaker, audio_params } }
///   · 响应是 chunked 流，可能包含**多个** JSON 对象，每个的 data 是 base64 音频分片，拼起来才是完整音频
///
/// 为什么请求 pcm 而不是 wav：流式场景下传 wav 会**多次返回 wav header**，拼起来是坏文件；
/// 官方建议流式用 pcm。所以这里收全 pcm（16bit 单声道）后自己补一个 WAV 头，
/// 这样能继续复用 winmm 的 SoundPlayer 播放（与本地 Piper 一致，不依赖系统媒体功能）。
/// </summary>
public sealed class VolcanoTtsService
{
    private const string Url = "https://openspeech.bytedance.com/api/v3/tts/unidirectional";
    private const int SampleRate = 24000;   // 火山默认采样率
    private const double TargetRms = 0.25 * 32767;   // 音量 100% 时的目标平均能量（约 -12 dBFS，对齐云 TTS 的响度）
    private const double MaxGain = 16;               // 增益上限：避免把极轻的底噪也一起放大

    private static readonly HttpClient Http =
        new(new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(30) };

    private readonly string _playFile = Path.Combine(Path.GetTempPath(), "floatword_volcano.wav");
    private SoundPlayer? _player;
    private static SoundPlayer? _preview;     // 设置页「测试」预听用，保活
    private int _gen;
    private bool _disposed;

    public static bool Configured(AppSettings s) =>
        !string.IsNullOrWhiteSpace(s.VolcanoApiKey) && !string.IsNullOrWhiteSpace(s.VolcanoSpeaker);

    public void Speak(string text, AppSettings s)
    {
        if (_disposed || string.IsNullOrWhiteSpace(text)) return;

        int my = Interlocked.Increment(ref _gen);
        Stop();

        _ = Task.Run(async () =>
        {
            try
            {
                var (wav, err) = await SynthesizeAsync(text, s, CancellationToken.None).ConfigureAwait(false);
                if (_disposed || my != Volatile.Read(ref _gen)) return;
                if (wav is null)
                {
                    Log.Warn($"火山语音合成失败 word={text}：{err}");
                    return;
                }

                File.WriteAllBytes(_playFile, wav);
                await Application.Current!.Dispatcher.InvokeAsync(() =>
                {
                    if (_disposed || my != Volatile.Read(ref _gen)) return;
                    try
                    {
                        var p = new SoundPlayer(_playFile);
                        _player = p;
                        p.Play();
                    }
                    catch { }
                });
            }
            catch (Exception ex)
            {
                Log.Error($"火山语音合成异常 word={text}", ex);
            }
        });
    }

    /// <summary>合成一个单词，成功返回完整 WAV 字节，失败返回原因。</summary>
    public static async Task<(byte[]? Wav, string? Error)> SynthesizeAsync(
        string text, AppSettings s, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.VolcanoApiKey)) return (null, "未填火山 API Key");
        if (string.IsNullOrWhiteSpace(s.VolcanoSpeaker)) return (null, "未填火山音色 speaker");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Url);
            req.Headers.TryAddWithoutValidation("X-Api-Key", s.VolcanoApiKey.Trim());
            req.Headers.TryAddWithoutValidation("X-Api-Resource-Id", s.VolcanoResourceId.Trim());
            req.Headers.TryAddWithoutValidation("X-Api-Request-Id", Guid.NewGuid().ToString());
            req.Content = JsonContent.Create(new
            {
                user = new { uid = "floatword" },
                req_params = new
                {
                    text,
                    speaker = s.VolcanoSpeaker.Trim(),
                    audio_params = new
                    {
                        format = "pcm",
                        sample_rate = SampleRate,
                        speech_rate = SpeechRate(s.Rate)
                    }
                }
            });

            using var resp = await Http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Warn($"火山语音 HTTP {(int)resp.StatusCode}：{Brief(body)}");
                return (null, $"HTTP {(int)resp.StatusCode}：{Brief(body)}");
            }

            // 成功与否以「有没有音频数据」为准：不同版本的成功码不一致
            // （文档示例写 0，实际 V3 返回 20000000），只把既非成功码、又没带 data 的返回记为错误，
            // 避免把正常返回误判成失败、也避免过早 break 丢掉后面的音频分片。
            var pcm = new MemoryStream();
            string? codeErr = null;
            int lastCode = -1;
            string lastMsg = "";
            foreach (var json in ExtractJsonObjects(body))
            {
                JsonDocument doc;
                try { doc = JsonDocument.Parse(json); }
                catch { continue; }
                using (doc)
                {
                    var root = doc.RootElement;

                    if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String)
                    {
                        try
                        {
                            var chunk = Convert.FromBase64String(d.GetString()!);
                            if (chunk.Length > 0) pcm.Write(chunk, 0, chunk.Length);
                        }
                        catch (FormatException) { /* 分片不是合法 base64，跳过 */ }
                    }

                    if (root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number)
                    {
                        lastCode = c.GetInt32();
                        lastMsg = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                        if (lastCode != 0 && lastCode != 20000000)
                            codeErr = $"{lastCode} {lastMsg}";
                    }
                }
            }

            if (pcm.Length == 0)
            {
                var reason = codeErr ?? $"没有返回音频数据（code={lastCode} {lastMsg}）：{Brief(body)}";
                Log.Warn($"火山语音合成失败：{reason}");
                return (null, reason);
            }
            Log.Info($"火山语音返回 code={lastCode} {lastMsg}，音频 {pcm.Length} 字节");

            // 响度在本地做：服务端 loudness 上限只有 2×，对本身就偏轻的合成结果明显不够
            int pcmLen = (int)pcm.Length;
            ApplyVolume(pcm.GetBuffer(), pcmLen, s.Volume / 100.0, out int peak, out int rms, out double gain);
            Log.Info($"火山语音响度：峰值 {peak}、平均 {rms}，本地增益 {gain:F2}×（播报音量 {s.Volume:0}）");

            var wav = WrapWav(pcm.ToArray(), SampleRate, 1, 16);
            Log.Info($"火山语音合成成功 word={text} speaker={s.VolcanoSpeaker.Trim()} " +
                     $"{wav.Length / 1024}KB 耗时 {sw.Elapsed.TotalSeconds:F1}s");
            return (wav, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"火山语音请求异常 word={text}（{Url}）", ex);
            return (null, $"{ex.GetType().Name}：{ex.Message}");
        }
    }

    /// <summary>播放一段 WAV（设置页「测试」预听用）。</summary>
    public static void Play(byte[] wav)
    {
        try
        {
            var p = new SoundPlayer(new MemoryStream(wav));
            _preview = p;
            p.Play();
        }
        catch { }
    }

    /// <summary>语速：本项目 Rate 取值 -10~10（+10 约 2 倍速）→ 火山 speech_rate 取值 [-50,100]。</summary>
    private static int SpeechRate(double rate) =>
        (int)Math.Round(Math.Clamp((Math.Pow(2, Math.Clamp(rate, -10, 10) / 10.0) - 1) * 100, -50, 100));

    /// <summary>
    /// 本地响度处理（16bit 单声道 PCM）：按**平均能量（RMS）**对齐到目标响度，再乘以「播报音量」，最后硬限幅。
    ///
    /// 为什么不用峰值归一化：若合成结果的峰值已接近满量程、但平均能量很低，
    /// 峰值归一化算出来的倍数≈1，等于没放大 —— 这正是「音量 100 仍偏小」的原因。
    /// 云 TTS 普遍做过响度归一化，所以这里按 RMS 把响度对齐到相近水平。
    /// </summary>
    private static void ApplyVolume(byte[] pcm, int length, double volume01,
                                    out int peak, out int rms, out double gain)
    {
        peak = 0;
        rms = 0;
        gain = 0;

        int n = length / 2;
        if (n <= 0) return;

        long sumSq = 0;
        for (int i = 0; i + 1 < length; i += 2)
        {
            int v = (short)(pcm[i] | (pcm[i + 1] << 8));
            int a = Math.Abs(v);
            if (a > peak) peak = a;
            sumSq += (long)v * v;
        }
        if (sumSq <= 0) return;

        rms = (int)Math.Sqrt(sumSq / (double)n);
        if (rms <= 0) return;

        gain = Math.Min(TargetRms / rms, MaxGain) * Math.Clamp(volume01, 0, 2);

        for (int i = 0; i + 1 < length; i += 2)
        {
            short v = (short)(pcm[i] | (pcm[i + 1] << 8));
            int x = (int)Math.Round(v * gain);
            if (x > short.MaxValue) x = short.MaxValue;
            else if (x < short.MinValue) x = short.MinValue;
            pcm[i] = (byte)(x & 0xFF);
            pcm[i + 1] = (byte)((x >> 8) & 0xFF);
        }
    }

    /// <summary>给裸 PCM 补一个 44 字节的标准 WAV 头。</summary>
    private static byte[] WrapWav(byte[] pcm, int sampleRate, short channels, short bitsPerSample)
    {
        int blockAlign = channels * bitsPerSample / 8;
        using var ms = new MemoryStream(44 + pcm.Length);
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + pcm.Length);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);                       // fmt 块长度
        w.Write((short)1);                 // PCM
        w.Write(channels);
        w.Write(sampleRate);
        w.Write(sampleRate * blockAlign);  // byte rate
        w.Write((short)blockAlign);
        w.Write(bitsPerSample);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(pcm.Length);
        w.Write(pcm);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// 从响应文本里逐个取出顶层 JSON 对象（chunked 流可能是多个对象拼接，也可能是逐行 JSON）。
    /// 用花括号配对扫描，能正确处理字符串里的花括号与转义。
    /// </summary>
    private static IEnumerable<string> ExtractJsonObjects(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            int start = s.IndexOf('{', i);
            if (start < 0) yield break;

            int depth = 0, j = start;
            bool inStr = false, esc = false;
            for (; j < s.Length; j++)
            {
                char c = s[j];
                if (inStr)
                {
                    if (esc) esc = false;
                    else if (c == '\\') esc = true;
                    else if (c == '"') inStr = false;
                }
                else if (c == '"') inStr = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0) { j++; break; }
            }

            if (depth != 0) yield break;   // 尾部不完整，忽略
            yield return s.Substring(start, j - start);
            i = j;
        }
    }

    private static string Brief(string s)
    {
        s = (s ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length <= 200 ? s : s[..200] + "…";
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
    }
}
