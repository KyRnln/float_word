using System.IO;
using System.Text.Json;

namespace FloatWordWpf;

public sealed class WordItem
{
    public string Word { get; set; } = "";
    public string Phonetic { get; set; } = "";
    public string Meaning { get; set; } = "";
}

public sealed class WordDictionary
{
    public string Name { get; set; } = "";
    public List<WordItem> Words { get; set; } = new();
}

/// <summary>
/// 词库加载：扫描 exe 同目录下的 dicts/*.json（每个文件一个词库），
/// 外加 words.json 作为“自定义”词库。与 Python 版行为保持一致。
/// </summary>
public static class DictionaryLibrary
{
    public static List<WordDictionary> Load(string appDir)
    {
        var list = new List<WordDictionary>();

        var folder = Path.Combine(appDir, "dicts");
        if (Directory.Exists(folder))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.json")
                                          .OrderBy(f => f, StringComparer.CurrentCulture))
            {
                var words = TryParse(File.ReadAllText(file));
                if (words.Count > 0)
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    ShuffleWords(words, name);
                    list.Add(new WordDictionary { Name = name, Words = words });
                }
            }
        }

        var single = Path.Combine(appDir, "words.json");
        if (File.Exists(single))
        {
            var words = TryParse(File.ReadAllText(single));
            if (words.Count > 0)
            {
                ShuffleWords(words, "自定义(words.json)");
                list.Add(new WordDictionary { Name = "自定义(words.json)", Words = words });
            }
        }

        return list;
    }

    /// <summary>
    /// 打乱单词顺序：用**词典名做种子的确定性洗牌**。
    ///
    /// 之所以不用真随机：学习进度是按下标保存的，如果每次启动顺序都变，
    /// "上次学到第 57 个"就没有意义了。用词典名当种子后，顺序看起来是随机的，
    /// 但同一本词典每次启动都一样，进度才能续上。
    ///
    /// 注意不能用 string.GetHashCode()：.NET Core 里它每个进程的种子都不同。
    /// </summary>
    private static void ShuffleWords(List<WordItem> words, string seedText)
    {
        int seed = 17;
        foreach (var c in seedText)
            seed = unchecked(seed * 31 + c);

        var rng = new Random(seed);
        for (int i = words.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (words[i], words[j]) = (words[j], words[i]);
        }
    }

    /// <summary>
    /// 兼容两种字段命名：
    ///   本项目            { word, phonetic, meaning }
    ///   Qwerty Learner    { name, usphone, trans }
    /// </summary>
    public static List<WordItem> TryParse(string json)
    {
        var result = new List<WordItem>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;

                string Get(params string[] keys)
                {
                    foreach (var k in keys)
                        if (el.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                            return v.GetString() ?? "";
                    return "";
                }

                var word = Get("word", "name");
                if (string.IsNullOrWhiteSpace(word)) continue;

                result.Add(new WordItem
                {
                    Word = word.Trim(),
                    Phonetic = NormalizePhonetic(Get("phonetic", "usphone", "ukphone", "phone")),
                    Meaning = GetMeaning(el)
                });
            }
        }
        catch
        {
            // 词库文件损坏时忽略该文件，不影响其它词库
        }
        return result;
    }

    /// <summary>
    /// 释义：兼容两种写法
    ///   本项目 / 常见格式  "meaning": "v. 放弃"
    ///   Qwerty Learner     "trans":   ["n. 接近", "通道"]
    /// 多义项按行拼接，界面上换行显示。
    /// </summary>
    private static string GetMeaning(JsonElement el)
    {
        foreach (var key in new[] { "meaning", "trans", "translation" })
        {
            if (!el.TryGetProperty(key, out var v)) continue;

            if (v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? "";

            if (v.ValueKind == JsonValueKind.Array)
            {
                var parts = v.EnumerateArray()
                             .Where(x => x.ValueKind == JsonValueKind.String)
                             .Select(x => (x.GetString() ?? "").Trim())
                             .Where(x => x.Length > 0);
                return string.Join("\n", parts);
            }
        }
        return "";
    }

    /// <summary>
    /// 音标统一成 /.../ 形式。
    /// Qwerty Learner 的词库用 ASCII 单引号表示重音（如 'kænsl），这里换成 IPA 的 ˈ。
    /// </summary>
    private static string NormalizePhonetic(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim();
        if (s.StartsWith('/')) return s;          // 已经是 /.../ 就不动
        return "/" + s.Replace("'", "ˈ") + "/";
    }
}
