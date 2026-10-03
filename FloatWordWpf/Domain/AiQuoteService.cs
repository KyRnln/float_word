using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FloatWordWpf;

/// <summary>一次 AI 台词请求的结果：成功给 <see cref="Quote"/>，失败给 <see cref="Error"/>。</summary>
public readonly record struct AiQuoteOutcome(MovieQuote? Quote, string? Error)
{
    public bool Ok => Quote is not null;
}

/// <summary>
/// AI 台词：用 OpenAI 兼容的 <c>/chat/completions</c> 接口，为单词找一句含该词的经典电影台词。
///
/// 关注点：
///   · 结果统一成「台词---电影名称」单行文本（破折号统一成 <c>---</c>）；
///   · 取到就写进配置缓存，同一个词只请求一次；
///   · **不再静默失败**：失败原因通过 <see cref="AiQuoteOutcome.Error"/> 返回，便于排查。
/// </summary>
public sealed class AiQuoteService
{
    private static readonly HttpClient Direct = NewClient(false);
    private static readonly HttpClient ViaSystemProxy = NewClient(true);

    // 60 秒：推理型模型（如 deepseek 的 reasoner / flash）生成一句台词可能要 20~40 秒，
    // 20 秒会把它们直接切断。普通对话模型通常几百毫秒就回来了。
    private static HttpClient NewClient(bool useProxy) =>
        new(new HttpClientHandler { UseProxy = useProxy }) { Timeout = TimeSpan.FromSeconds(60) };

    // 台词长度上限（按英文单词数）：提示模型尽量 20 词内，超过 40 词的硬截断，
    // 避免台词过长把悬浮窗撑得很宽，影响观感。
    private const int PreferQuoteWords = 20;
    private const int MaxQuoteWords = 40;

    private static readonly string SystemPrompt =
        "你是英语学习助手。请为给定的英文单词找一句包含该单词的经典电影台词。" +
        $"台词要尽量简短：优先 {PreferQuoteWords} 个单词以内，任何情况下都不要超过 {MaxQuoteWords} 个单词。" +
        "只输出一个 JSON 对象，不要输出 markdown 代码块、不要任何解释，格式：" +
        "{\"quote\":\"英文台词原文（尽量短，最多 " + MaxQuoteWords + " 个单词）\"," +
        "\"translation\":\"台词的中文翻译（同样简洁）\"," +
        "\"movie\":\"电影的中文片名（不要书名号）\",\"year\":\"上映年份，4 位数字\"}";

    private static readonly JsonSerializerOptions ParseOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>是否已具备调用条件。返回 null 表示可用，否则返回缺失项说明。</summary>
    public static string? MissingConfig(AppSettings s)
    {
        if (!s.AiEnabled) return "未启用「AI 台词」";
        if (string.IsNullOrWhiteSpace(s.AiBaseUrl)) return "Base URL 为空";
        if (string.IsNullOrWhiteSpace(s.AiApiKey)) return "API Key 为空";
        if (string.IsNullOrWhiteSpace(s.AiModel)) return "模型名为空";
        return null;
    }

    /// <summary>取台词。useCache=false 时忽略缓存（用于设置页的「测试」）。取消时抛出 OperationCanceledException。</summary>
    public async Task<AiQuoteOutcome> FetchAsync(string word, AppSettings s, CancellationToken ct, bool useCache = true)
    {
        // 缓存里可能是限长之前存下的长台词，读出来也统一截断，保证显示一致
        if (useCache && s.GetAiQuote(word) is { } cached)
        {
            cached.Quote = ClampWords(cached.Quote);
            return new(cached, null);
        }

        if (MissingConfig(s) is { } missing) return new(null, missing);

        var url = s.AiBaseUrl.Trim().TrimEnd('/') + "/chat/completions";
        var http = s.AiUseSystemProxy ? ViaSystemProxy : Direct;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Log.Info($"AI 请求 word={word} model={s.AiModel.Trim()} 代理={(s.AiUseSystemProxy ? "系统代理" : "直连")} url={url}");

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + s.AiApiKey.Trim());
            req.Content = JsonContent.Create(new
            {
                model = s.AiModel.Trim(),
                messages = new object[]
                {
                    new { role = "system", content = SystemPrompt },
                    new { role = "user", content = "单词：" + word }
                }
            });

            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = $"HTTP {(int)resp.StatusCode}：{Brief(body)}";
                Log.Warn($"AI 请求失败 word={word} {err}");
                return new(null, err);
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                Log.Warn($"AI 返回没有 choices word={word}：{Brief(body)}");
                return new(null, "返回里没有 choices：" + Brief(body));
            }

            var quote = ParseQuote(ExtractContent(choices[0].GetProperty("message")));
            if (quote is null)
            {
                Log.Warn($"AI 返回内容无法解析 word={word}：{Brief(body)}");
                return new(null, "返回内容无法解析：" + Brief(body));
            }

            if (useCache) s.SetAiQuote(word, quote);   // 缓存，避免重复请求（省钱、也更快）
            Log.Info($"AI 成功 word={word} 耗时 {sw.Elapsed.TotalSeconds:F1}s -> {quote.Quote} / " +
                     $"{quote.Movie}（{quote.Year}）{quote.Translation}");
            return new(quote, null);
        }
        catch (OperationCanceledException)
        {
            Log.Info($"AI 请求取消/超时 word={word}（{url}）");
            throw;   // 换词 / 关闭时的正常取消，交给调用方判断
        }
        catch (Exception ex)
        {
            Log.Error($"AI 请求异常 word={word}（{url}）", ex);
            return new(null, $"{ex.GetType().Name}：{ex.Message}（{url}）");
        }
    }

    /// <summary>message.content 可能是字符串，也可能是分段数组，两种都兼容。</summary>
    private static string? ExtractContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var c)) return null;

        if (c.ValueKind == JsonValueKind.String) return c.GetString();

        if (c.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var part in c.EnumerateArray())
                if (part.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                    sb.Append(tx.GetString());
            return sb.ToString();
        }

        return null;
    }

    /// <summary>
    /// 解析模型返回的 JSON（容忍 ```json 围栏和前后多余文字）。
    /// 完全不是 JSON 时退化成「整段当台词」，至少还能显示点东西。
    /// </summary>
    private static MovieQuote? ParseQuote(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var text = raw.Trim();

        // 去掉可能的 ```json ... ``` 围栏
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            int nl = text.IndexOf('\n');
            if (nl >= 0) text = text[(nl + 1)..];
            int fence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0) text = text[..fence];
            text = text.Trim();
        }

        // 截取第一个 { ... }
        int s = text.IndexOf('{');
        int e = text.LastIndexOf('}');
        if (s >= 0 && e > s) text = text[s..(e + 1)];

        MovieQuote? q = null;
        try { q = JsonSerializer.Deserialize<MovieQuote>(text, ParseOptions); }
        catch { /* 不是合法 JSON，走下面的兜底 */ }

        if (q is null)
        {
            var fallback = ClampWords(Clean(raw));
            return fallback.Length == 0 ? null : new MovieQuote { Quote = fallback };
        }

        q.Quote = ClampWords(Clean(q.Quote));
        q.Translation = Clean(q.Translation);
        q.Movie = Clean(q.Movie);
        q.Year = Clean(q.Year);
        return q.HasContent ? q : null;
    }

    /// <summary>压成单行（换行 / 连续空白都并成一个空格）。</summary>
    private static string Clean(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "" : Regex.Replace(s, @"\s+", " ").Trim();

    /// <summary>台词按空格计词，超过 <see cref="MaxQuoteWords"/> 词就截断并加省略号。</summary>
    private static string ClampWords(string s)
    {
        if (s.Length == 0) return s;
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= MaxQuoteWords
            ? s
            : string.Join(' ', words.Take(MaxQuoteWords)) + "…";
    }

    /// <summary>截断长文本，便于塞进一行提示里。</summary>
    private static string Brief(string s)
    {
        s = Regex.Replace(s ?? "", @"\s+", " ").Trim();
        return s.Length <= 160 ? s : s[..160] + "…";
    }
}
