using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ServerUI.Web.Bridge.Aum;

/// <summary>
/// 下载地址策略。生产路径只用 <see cref="Strict"/>：
///   - 仅 HTTPS；主机白名单（精确匹配）；重定向最多 5 跳且逐跳校验。
/// 测试路径（<c>--updatetest</c>）显式构造一个只放行 127.0.0.1 的实例，
/// 不读取任何环境变量，因此程序正常运行时不存在"关闭校验"的入口。
/// </summary>
internal sealed class AumNetPolicy
{
    public bool AllowLoopbackHttp { get; }

    private AumNetPolicy(bool allowLoopback) { AllowLoopbackHttp = allowLoopback; }

    public static readonly AumNetPolicy Strict = new AumNetPolicy(false);

    /// <summary>仅用于 --updatetest（本地 127.0.0.1 静态服务）。</summary>
    public static AumNetPolicy ForLoopbackTests() => new AumNetPolicy(true);

    public bool TryValidate(string url, out Uri uri, out string error)
    {
        uri = null;
        error = null;
        if (string.IsNullOrWhiteSpace(url)) { error = "URL 为空"; return false; }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) { error = "URL 非法：" + url; return false; }
        if (!string.IsNullOrEmpty(u.UserInfo)) { error = "URL 不得包含用户信息"; return false; }

        if (AllowLoopbackHttp && u.Scheme == Uri.UriSchemeHttp &&
            string.Equals(u.Host, "127.0.0.1", StringComparison.Ordinal))
        {
            uri = u;
            return true;
        }

        if (u.Scheme != Uri.UriSchemeHttps) { error = "只允许 HTTPS：" + u.Scheme + "://" + u.Host; return false; }
        if (!u.IsDefaultPort) { error = "URL 端口非默认：" + u.Port; return false; }
        if (!IsAllowedHost(u.Host)) { error = "主机不在白名单内：" + u.Host; return false; }
        uri = u;
        return true;
    }

    public static bool IsAllowedHost(string host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        foreach (var h in AumNet.AllowedHosts)
            if (string.Equals(host, h, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

/// <summary>
/// 发行包/源码下载。
///   - 边下边算 SHA256，落临时文件；size 与 sha256 都必须与清单一致
///   - Content-Length 与清单 size 不符 → 在读取正文之前失败
///   - Content-Type 为 HTML 或首 512 字节含 &lt;!DOCTYPE / &lt;html → 拒绝（门户劫持/错误页）
///   - 连接 15s / 整体 180s；不做断点续传
/// </summary>
internal static class AumNet
{
    public const string UserAgent = "ServerUI-AUM-Updater/2.21";
    public const int MaxRedirects = 5;
    public const int ConnectTimeoutSeconds = 15;
    public const int TotalTimeoutSeconds = 180;

    /// <summary>
    /// GitHub 及其资源主机。前 4 个是任务书 §4 明确要求的；后 2 个是 GitHub
    /// 对 archive / release 资产实际返回的重定向目标，不加则无法下载任何包。
    /// </summary>
    public static readonly string[] AllowedHosts =
    {
        "github.com",
        "api.github.com",
        "objects.githubusercontent.com",
        "raw.githubusercontent.com",
        "codeload.github.com",
        "release-assets.githubusercontent.com"
    };

    public static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(ConnectTimeoutSeconds),
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            UseCookies = false
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json, application/octet-stream, application/json, */*");
        return client;
    }

    internal sealed class DownloadOutcome
    {
        public bool Ok;
        public string Error;
        public string FinalUrl;
        public long Bytes;
        public string Sha256;
        public string ContentType;
    }

    /// <param name="expectedSize">清单声明的大小；0 表示未知（源码包）</param>
    /// <param name="maxBytes">硬上限</param>
    /// <param name="expectedSha256">清单声明的小写 sha256；null 表示不校验</param>
    public static async Task<DownloadOutcome> DownloadAsync(
        HttpClient client,
        AumNetPolicy policy,
        string url,
        string destPath,
        long expectedSize,
        long maxBytes,
        string expectedSha256,
        Action<string> log,
        Action<long, long> onProgress,
        CancellationToken token)
    {
        var outcome = new DownloadOutcome();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(TotalTimeoutSeconds));

        try
        {
            var current = url;
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                if (!policy.TryValidate(current, out var uri, out var urlError))
                {
                    outcome.Error = "下载地址被拒绝：" + urlError;
                    return outcome;
                }

                using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);

                var code = (int)resp.StatusCode;
                if (code == 301 || code == 302 || code == 303 || code == 307 || code == 308)
                {
                    var loc = resp.Headers.Location;
                    if (loc == null) { outcome.Error = "重定向缺少 Location（HTTP " + code + "）"; return outcome; }
                    if (hop == MaxRedirects) { outcome.Error = "重定向超过 " + MaxRedirects + " 跳，已中止"; return outcome; }
                    var next = loc.IsAbsoluteUri ? loc : new Uri(uri, loc);
                    log?.Invoke("[AUM更新] 重定向 → " + next.Host);
                    current = next.ToString();
                    continue;
                }

                if (!resp.IsSuccessStatusCode)
                {
                    outcome.Error = "下载失败：HTTP " + code + " " + resp.ReasonPhrase + "（" + uri.Host + "）";
                    return outcome;
                }

                outcome.FinalUrl = uri.ToString();
                outcome.ContentType = resp.Content.Headers.ContentType?.MediaType;

                if (!string.IsNullOrEmpty(outcome.ContentType) &&
                    outcome.ContentType.IndexOf("html", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    outcome.Error = "服务器返回 HTML（" + outcome.ContentType + "），疑似错误页/门户劫持";
                    return outcome;
                }

                var contentLength = resp.Content.Headers.ContentLength;
                if (contentLength.HasValue)
                {
                    if (expectedSize > 0 && contentLength.Value != expectedSize)
                    {
                        outcome.Error = "Content-Length=" + contentLength.Value + " 与清单 size=" + expectedSize + " 不符，已中止（未下载正文）";
                        return outcome;
                    }
                    if (maxBytes > 0 && contentLength.Value > maxBytes)
                    {
                        outcome.Error = "响应大小 " + contentLength.Value + " 超过上限 " + maxBytes;
                        return outcome;
                    }
                }

                using var src = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                using var sha = SHA256.Create();
                using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                var buffer = new byte[81920];
                long total = 0;
                var sniffed = false;
                var lastReport = 0L;

                while (true)
                {
                    var n = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token).ConfigureAwait(false);
                    if (n <= 0) break;
                    total += n;

                    if (!sniffed && total >= 32)
                    {
                        sniffed = true;
                        if (LooksLikeHtml(buffer, n))
                        {
                            outcome.Error = "响应内容像 HTML 页面（首 512 字节含 <!DOCTYPE / <html），疑似错误页";
                            return outcome;
                        }
                    }

                    if (maxBytes > 0 && total > maxBytes)
                    {
                        outcome.Error = "下载超过上限 " + maxBytes + " 字节，已中止";
                        return outcome;
                    }

                    sha.TransformBlock(buffer, 0, n, null, 0);
                    await dst.WriteAsync(buffer.AsMemory(0, n), cts.Token).ConfigureAwait(false);

                    if (total - lastReport >= 512 * 1024)
                    {
                        lastReport = total;
                        onProgress?.Invoke(total, expectedSize > 0 ? expectedSize : -1);
                    }
                }

                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                outcome.Bytes = total;
                outcome.Sha256 = ToHex(sha.Hash);

                if (expectedSize > 0 && total != expectedSize)
                {
                    outcome.Error = "下载大小 " + total + " 与清单 size=" + expectedSize + " 不符";
                    return outcome;
                }
                if (!string.IsNullOrEmpty(expectedSha256) &&
                    !string.Equals(outcome.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    outcome.Error = "SHA256 校验失败：期望 " + expectedSha256 + "，实际 " + outcome.Sha256;
                    return outcome;
                }

                outcome.Ok = true;
                onProgress?.Invoke(total, expectedSize > 0 ? expectedSize : total);
                return outcome;
            }

            outcome.Error = "重定向处理异常";
            return outcome;
        }
        catch (OperationCanceledException)
        {
            outcome.Error = token.IsCancellationRequested ? "已取消" : "下载超时（" + TotalTimeoutSeconds + " 秒）";
            return outcome;
        }
        catch (Exception ex)
        {
            outcome.Error = "下载异常：" + ex.Message;
            return outcome;
        }
    }

    public static async Task<(bool Ok, string Body, string Error)> GetStringAsync(
        HttpClient client, AumNetPolicy policy, string url, int maxBytes, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var current = url;
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                if (!policy.TryValidate(current, out var uri, out var urlError))
                    return (false, null, "地址被拒绝：" + urlError);

                using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                var code = (int)resp.StatusCode;
                if (code == 301 || code == 302 || code == 303 || code == 307 || code == 308)
                {
                    var loc = resp.Headers.Location;
                    if (loc == null) return (false, null, "重定向缺少 Location");
                    current = (loc.IsAbsoluteUri ? loc : new Uri(uri, loc)).ToString();
                    continue;
                }
                if (!resp.IsSuccessStatusCode)
                    return (false, null, "HTTP " + code + " " + resp.ReasonPhrase);

                var len = resp.Content.Headers.ContentLength;
                if (len.HasValue && len.Value > maxBytes) return (false, null, "响应过大：" + len.Value);

                using var src = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                using var ms = new MemoryStream();
                var buf = new byte[16384];
                while (true)
                {
                    var n = await src.ReadAsync(buf.AsMemory(0, buf.Length), cts.Token).ConfigureAwait(false);
                    if (n <= 0) break;
                    if (ms.Length + n > maxBytes) return (false, null, "响应过大");
                    ms.Write(buf, 0, n);
                }
                return (true, Encoding.UTF8.GetString(ms.ToArray()), null);
            }
            return (false, null, "重定向超过上限");
        }
        catch (OperationCanceledException) { return (false, null, "请求超时"); }
        catch (Exception ex) { return (false, null, ex.Message); }
    }

    private static bool LooksLikeHtml(byte[] buffer, int count)
    {
        var limit = Math.Min(count, 512);
        if (limit <= 0) return false;
        var head = Encoding.ASCII.GetString(buffer, 0, limit).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        return head.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
               || head.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
    }

    private static string ToHex(byte[] bytes)
    {
        if (bytes == null) return null;
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    public static string Sha256File(string path)
    {
        using var sha = SHA256.Create();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
        var hash = sha.ComputeHash(fs);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024.0)).ToString("F1") + " MB";
        return (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("F2") + " GB";
    }
}
