using System.IO;
using System.Text;

namespace FloatWordWpf;

/// <summary>
/// 极简日志：追加写入 exe 同目录的 <c>logs\floatword.log</c>。
///
/// 崩溃、AI 调用等关键事件都记在这里 —— 之前「闪退」「没反应」类问题查不到原因，
/// 就是因为没有任何现场记录。日志本身出问题绝不能影响程序，所以全部 try/catch 吞掉。
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private const long MaxBytes = 1_000_000;   // 超过 1MB 轮换成 .1，避免无限增长

    public static string FilePath => Path.Combine(AppPaths.AppDir, "logs", "floatword.log");

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : message + Environment.NewLine + ex);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                Rotate(path);
                File.AppendAllText(
                    path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // 写日志失败不影响程序
        }
    }

    private static void Rotate(string path)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists || fi.Length < MaxBytes) return;

        var bak = path + ".1";
        try
        {
            if (File.Exists(bak)) File.Delete(bak);
            File.Move(path, bak);
        }
        catch
        {
            // 轮换失败就继续往原文件追加
        }
    }
}
