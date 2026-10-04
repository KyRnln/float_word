using System.IO;
using System.Media;

namespace FloatWordWpf;

/// <summary>
/// 程序内合成的提示音（拼写正确时的「叮」）：
/// 直接生成一段短促的衰减正弦波 WAV 放在内存里播放，免去外部音频文件，
/// 也不受系统提示音开关影响。<see cref="SoundPlayer.Play"/> 在后台线程播放，不会卡界面。
/// </summary>
public static class SoundFx
{
    /// <summary>「叮」的时长（毫秒）。winmm 的 PlaySound 是单通道，紧接着朗读会把提示音掐掉，调用方需要等这么久。</summary>
    public const int DingMs = 320;

    private static SoundPlayer? _ding;

    /// <summary>播放「叮」——拼写正确时用。</summary>
    public static void PlayDing()
    {
        try
        {
            _ding ??= new SoundPlayer(new MemoryStream(MakeDingWav()));
            _ding.Play();
        }
        catch (Exception ex)
        {
            Log.Error("播放提示音失败", ex);
        }
    }

    /// <summary>合成「叮」：基频 + 一点高八度泛音，指数衰减包络，两端做淡入淡出防爆音。</summary>
    private static byte[] MakeDingWav()
    {
        const int rate = 44100;
        double dur = DingMs / 1000.0;
        int n = (int)(rate * dur);
        var pcm = new short[n];
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / rate;
            double env = Math.Exp(-t / 0.09) * Math.Min(1.0, t / 0.004);
            double s = Math.Sin(2 * Math.PI * 1318.5 * t) * 0.75     // E6
                     + Math.Sin(2 * Math.PI * 2637.0 * t) * 0.25;    // 高八度泛音，更清亮
            pcm[i] = (short)(Math.Clamp(s * env, -1, 1) * short.MaxValue * 0.55);
        }

        int dataBytes = pcm.Length * 2;
        using var ms = new MemoryStream(44 + dataBytes);
        using var w = new BinaryWriter(ms);

        w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
        w.Write(36 + dataBytes);
        w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });
        w.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
        w.Write(16);            // fmt 块长度
        w.Write((short)1);      // PCM
        w.Write((short)1);      // 单声道
        w.Write(rate);
        w.Write(rate * 2);      // 字节率
        w.Write((short)2);      // 块对齐
        w.Write((short)16);     // 位深
        w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
        w.Write(dataBytes);
        foreach (var v in pcm) w.Write(v);
        w.Flush();

        return ms.ToArray();
    }
}
