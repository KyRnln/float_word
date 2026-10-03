using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace FloatWordWpf;

/// <summary>
/// 极简 WebDAV 客户端：只做「把配置文件传上去 / 取回来」这一件事，够用即可。
/// </summary>
/// <remarks>
/// 默认对接**坚果云**：账号 = 登录邮箱，密码 = 网页端「账户信息 → 安全选项 → 添加应用密码」
/// 生成的**应用密码**（登录密码对 WebDAV 无效）。
/// 坚果云是国内服务，这里不走系统代理，直连更稳。
/// </remarks>
public static class WebDavService
{
    /// <summary>坚果云 WebDAV 入口。</summary>
    public const string NutstoreUrl = "https://dav.jianguoyun.com/dav/";

    /// <summary>远端固定用这个文件名，恢复时也读它。</summary>
    public const string FileName = "floatword_config.json";

    public readonly record struct Result(bool Ok, string Message);

    /// <summary>必要信息是否填全。</summary>
    public static bool Configured(AppSettings s, out string why)
    {
        if (string.IsNullOrWhiteSpace(s.WebDavUrl)) { why = "还没填服务器地址"; return false; }
        if (string.IsNullOrWhiteSpace(s.WebDavUser)) { why = "还没填账号"; return false; }
        if (string.IsNullOrWhiteSpace(s.WebDavPass)) { why = "还没填应用密码"; return false; }
        why = "";
        return true;
    }

    /// <summary>备份：把配置传上去（PUT）。远端目录不存在会先逐级创建。</summary>
    public static async Task<Result> UploadAsync(AppSettings s, string json, CancellationToken ct = default)
    {
        if (!Configured(s, out var why)) return new(false, why);
        try
        {
            using var http = MakeClient(s);
            await EnsureDirAsync(http, s, ct);

            using var req = new HttpRequestMessage(HttpMethod.Put, FileUrl(s));
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await http.SendAsync(req, ct);
            return Report("备份", resp);
        }
        catch (OperationCanceledException)
        {
            Log.Warn("WebDAV 备份超时或被取消");
            return new(false, "请求超时（30 秒），请检查网络或服务器地址");
        }
        catch (Exception ex)
        {
            Log.Error("WebDAV 备份出错", ex);
            return new(false, "备份出错：" + ex.Message);
        }
    }

    /// <summary>恢复：把配置取回来（GET）。成功时 <c>Json</c> 为文件内容。</summary>
    public static async Task<(Result Result, string? Json)> DownloadAsync(
        AppSettings s, CancellationToken ct = default)
    {
        if (!Configured(s, out var why)) return (new(false, why), null);
        try
        {
            using var http = MakeClient(s);
            using var req = new HttpRequestMessage(HttpMethod.Get, FileUrl(s));
            using var resp = await http.SendAsync(req, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var bad = resp.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "认证失败：账号或应用密码不对",
                    HttpStatusCode.NotFound => "云端还没有备份文件",
                    _ => $"下载失败：HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}"
                };
                Log.Warn($"WebDAV 恢复失败：{bad}");
                return (new(false, bad), null);
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            Log.Info($"WebDAV 恢复成功，{json.Length} 字节");
            return (new(true, "下载成功"), json);
        }
        catch (OperationCanceledException)
        {
            Log.Warn("WebDAV 恢复超时或被取消");
            return (new(false, "请求超时（30 秒），请检查网络或服务器地址"), null);
        }
        catch (Exception ex)
        {
            Log.Error("WebDAV 恢复出错", ex);
            return (new(false, "恢复出错：" + ex.Message), null);
        }
    }

    /// <summary>连通性 + 认证测试。云端还没有备份文件也算「连接正常」。</summary>
    public static async Task<Result> TestAsync(AppSettings s, CancellationToken ct = default)
    {
        var (r, json) = await DownloadAsync(s, ct);
        if (r.Ok) return new(true, $"连接与认证正常，云端已有备份（{json!.Length} 字节）");
        if (r.Message.Contains("还没有备份文件")) return new(true, "连接与认证正常，云端还没有备份文件");
        return r;
    }

    // ---------- 内部 ----------

    /// <summary>坚果云等国内服务直连最稳；鉴权用 HTTP Basic。</summary>
    private static HttpClient MakeClient(AppSettings s)
    {
        var http = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{s.WebDavUser}:{s.WebDavPass}")));
        return http;
    }

    private static string BaseUrl(AppSettings s)
    {
        var u = s.WebDavUrl.Trim();
        return u.EndsWith('/') ? u : u + "/";
    }

    private static string FileUrl(AppSettings s)
    {
        var dir = s.WebDavDir.Trim().Trim('/');
        return BaseUrl(s) + (dir.Length > 0 ? dir + "/" : "") + FileName;
    }

    /// <summary>逐级 MKCOL。已存在会返回 405 / 301，忽略即可；失败也不阻断后续 PUT。</summary>
    private static async Task EnsureDirAsync(HttpClient http, AppSettings s, CancellationToken ct)
    {
        var path = BaseUrl(s);
        foreach (var part in s.WebDavDir.Trim().Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path += part + "/";
            try
            {
                using var req = new HttpRequestMessage(new HttpMethod("MKCOL"), path);
                using var resp = await http.SendAsync(req, ct);
            }
            catch
            {
                return;   // 建目录失败不在这里报错，让 PUT 去暴露真正的问题
            }
        }
    }

    private static Result Report(string what, HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode)
        {
            Log.Info($"WebDAV {what}成功：HTTP {(int)resp.StatusCode}");
            return new(true, $"{what}成功");
        }

        var msg = resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "认证失败：账号或应用密码不对",
            HttpStatusCode.Conflict => "远端目录不存在且自动创建失败，请检查「远程目录」",
            _ => $"{what}失败：HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}"
        };
        Log.Warn($"WebDAV {what}失败：{msg}");
        return new(false, msg);
    }
}
