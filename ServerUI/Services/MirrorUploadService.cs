using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ServerUI.Services.PlatformAdapters;

namespace ServerUI.Services;

public class MirrorUploadService
{
    const string GitGudTokenB64 = "WjJkcGIxOUZkbUpmUmtScFpqRnNWVlJXUVZGcmR6QjZTMWRIT0RaTlVYQXhUMnBLYWxvelowc3VNREV1TVRBeFozVXhhMnBq";
    static string GitGudToken => Decode2(GitGudTokenB64);
    const string GitHubRepo = "118coder/ServerS4A12.86JP";
    const string GitGudZip = "https://gitgud.io/api/v4/projects/rewio%2F86JP/repository/archive.zip?sha=main";
    const int LockTimeout = 600;
    const int MaxRetry = 3;

    static class BeijingTime
    {
        static readonly TimeZoneInfo _cst = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        public static DateTime Now => TimeZoneInfo.ConvertTime(DateTime.UtcNow, _cst);
    }

    public event Action<string> OutputReceived;

    static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    static MirrorUploadService() { _http.DefaultRequestHeaders.Add("User-Agent", "ServerUI-Mirror/1.0"); }

    static string Decode2(string b64)
    {
        var once = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        return Encoding.UTF8.GetString(Convert.FromBase64String(once));
    }

    readonly IMirrorPlatform[] _platforms = new IMirrorPlatform[]
    {
        new GitHubAdapter(),
        new CodebergAdapter(),
        new GiteeAdapter()
    };

    IMirrorPlatform GitHub => _platforms[0];
    IMirrorPlatform Codeberg => _platforms[1];
    IMirrorPlatform Gitee => _platforms[2];

    public async Task<bool> CanReach(string url)
    {
        try
        {
            var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<bool> CanReachGitGud()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        int hits = 0;
        while (DateTime.UtcNow < deadline && hits < 3)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var resp = await new HttpClient { Timeout = TimeSpan.FromSeconds(3) }
                    .GetAsync("https://gitgud.io/rewio/86JP", cts.Token);
                hits++;
            }
            catch { }
            if (hits >= 3) break;
            if (DateTime.UtcNow < deadline) await Task.Delay(1000);
        }
        return hits >= 3;
    }
    public async Task<bool> CanReachGitHub() => await CanReach("https://api.github.com");
    public async Task<bool> CanReachGitee() => await CanReach("https://gitee.com");
    public async Task<bool> CanReachCodeberg() => await CanReach("https://codeberg.org");

    public async Task<bool> ValidateTokensAsync()
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{GitHubRepo}");
            req.Headers.Add("Authorization", "token " + Decode2("WjJod1gxQlpaVEZNYzBjMlpWZElhMkZNUTNWa1RVbHNkVTFEVmxKb1pqVlllREZwTUVoa01BPT0="));
            var resp = await _http.SendAsync(req);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    async Task<(bool Success, string Sha, string Error)> TryAcquireLock(string version, string hostname)
    {
        var path = ".mirror-lock";
        var url = $"https://api.github.com/repos/{GitHubRepo}/contents/{path}";
        var token = Decode2("WjJod1gxQlpaVEZNYzBjMlpWZElhMkZNUTNWa1RVbHNkVTFEVmxKb1pqVlllREZwTUVoa01BPT0=");

        for (int i = 0; i < MaxRetry; i++)
        {
            try
            {
                string existingSha = null;
                try
                {
                    var checkReq = new HttpRequestMessage(HttpMethod.Get, url);
                    checkReq.Headers.Add("Authorization", "token " + token);
                    var checkResp = await _http.SendAsync(checkReq);
                    if (checkResp.IsSuccessStatusCode)
                    {
                        var checkJson = await checkResp.Content.ReadAsStringAsync();
                        using var checkDoc = JsonDocument.Parse(checkJson);
                        if (checkDoc.RootElement.TryGetProperty("content", out var contentEl))
                        {
                            var bytes = Convert.FromBase64String(contentEl.GetString() ?? "");
                            var lockContent = Encoding.UTF8.GetString(bytes);
                            using var lockDoc = JsonDocument.Parse(lockContent);
                            if (lockDoc.RootElement.TryGetProperty("timestamp", out var tsEl))
                            {
                                var lockTime = DateTime.Parse(tsEl.GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind);
                                var elapsed = DateTime.UtcNow - lockTime;
                                if (elapsed.TotalSeconds < LockTimeout)
                                {
                                    var host = lockDoc.RootElement.TryGetProperty("hostname", out var h) ? h.GetString() : "?";
                                    return (false, null, $"锁被 {host} 持有，已等待{(int)elapsed.TotalMinutes}分钟");
                                }
                                if (checkDoc.RootElement.TryGetProperty("sha", out var sh))
                                    existingSha = sh.GetString();
                            }
                        }
                    }
                }
                catch { }

                var lockData = new
                {
                    timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    hostname,
                    process_id = Compat.Pid,
                    version
                };
                var lockJson = JsonSerializer.Serialize(lockData);

                var putBody = new
                {
                    message = $"获取上传锁 [{hostname}]",
                    content = Convert.ToBase64String(Encoding.UTF8.GetBytes(lockJson)),
                    sha = existingSha
                };

                var putReq = new HttpRequestMessage(HttpMethod.Put, url);
                putReq.Headers.Add("Authorization", "token " + token);
                putReq.Content = new StringContent(JsonSerializer.Serialize(putBody), Encoding.UTF8, "application/json");

                var putResp = await _http.SendAsync(putReq);
                if (putResp.IsSuccessStatusCode)
                {
                    var respJson = await putResp.Content.ReadAsStringAsync();
                    using var respDoc = JsonDocument.Parse(respJson);
                    var newSha = respDoc.RootElement.TryGetProperty("content", out var c)
                        && c.TryGetProperty("sha", out var s) ? s.GetString() : null;
                    return (true, newSha, null);
                }

                if ((int)putResp.StatusCode == 409 || (int)putResp.StatusCode == 422)
                    return (false, null, "锁已被其他用户抢占");
            }
            catch (Exception ex)
            {
                if (i >= MaxRetry - 1) return (false, null, $"获取锁异常: {ex.Message}");
            }
            await Task.Delay(2000 * (i + 1));
        }

        return (false, null, "重试耗尽");
    }

    async Task<bool> ReleaseLock(string sha)
    {
        try
        {
            var url = $"https://api.github.com/repos/{GitHubRepo}/contents/.mirror-lock";
            var token = Decode2("WjJod1gxQlpaVEZNYzBjMlpWZElhMkZNUTNWa1RVbHNkVTFEVmxKb1pqVlllREZwTUVoa01BPT0=");
            var body = new { message = "释放上传锁", sha };
            var req = new HttpRequestMessage(HttpMethod.Delete, url);
            req.Headers.Add("Authorization", "token " + token);
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            var resp = await _http.SendAsync(req);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<bool> RunUploaderAsync(string version, string hostname)
    {
        OutputReceived?.Invoke("[镜像] 检测到可访问 GitGud，启动上传者模式...");

        var now = BeijingTime.Now;
        var commitCount = await GetGitGudCommitCount();
        var pkgName = $"ServerS4A12-{now:yyyyMMdd}-{now:HHmm}-{commitCount}";
        OutputReceived?.Invoke($"[镜像] 包名: {pkgName}");

        try
        {
            OutputReceived?.Invoke("[镜像] 正在从 GitGud 下载源码...");
            byte[] zip = null;
            for (int a = 1; a <= 3; a++)
            {
                try
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                    client.DefaultRequestHeaders.Add("User-Agent", "ServerUI-Mirror/1.0");
                    client.DefaultRequestHeaders.Add("PRIVATE-TOKEN", GitGudToken);
                    zip = await client.GetByteArrayAsync(GitGudZip);
                    if (zip.Length > 51200) break;
                }
                catch { if (a < 3) await Task.Delay(2000); }
            }

            if (zip == null || zip.Length < 51200)
            {
                OutputReceived?.Invoke("[镜像] GitGud 下载失败，跳过镜像同步");
                return false;
            }

            var sha = Compat.Sha256Hex(zip).ToLower();
            var zipSize = zip.Length;
            OutputReceived?.Invoke($"[镜像] 下载完成, 大小:{zipSize / 1024}KB, SHA:{sha.Substring(0, 8)}...");

            try
            {
                var latestDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AUM管理组件", "latest");
                if (!Directory.Exists(latestDir)) Directory.CreateDirectory(latestDir);
                File.WriteAllBytes(Path.Combine(latestDir, "ServerS4A12-latest.zip"), zip);
                OutputReceived?.Invoke("[镜像] 已更新本地缓存 ServerS4A12-latest.zip");
            }
            catch { }

            var (hasMeta, oldSha, oldVer, oldTime) = await FetchMirrorMetadata();
            if (hasMeta && oldSha == sha)
            {
                OutputReceived?.Invoke("[镜像] 层1: SHA相同 → 文件内容未变更，跳过。");
                return false;
            }

            if (hasMeta && oldVer == pkgName && oldSha != sha)
            {
                OutputReceived?.Invoke("[镜像] 层2: 版本相同但SHA不同 → 强制更新。");
            }
            else if (hasMeta && oldVer == pkgName)
            {
                OutputReceived?.Invoke("[镜像] 层2: 版本相同且SHA相同 → 跳过。");
                return false;
            }

            if (hasMeta && oldTime != default)
            {
                var elapsed = BeijingTime.Now - oldTime;
                if (elapsed.TotalSeconds < 300)
                {
                    OutputReceived?.Invoke($"[镜像] 层3: {(int)elapsed.TotalSeconds}秒前刚更新过 → 跳过。");
                    return false;
                }
            }

            OutputReceived?.Invoke("[镜像] 尝试获取上传锁...");
            var (ok, lockSha, err) = await TryAcquireLock(pkgName, hostname);
            if (!ok)
            {
                OutputReceived?.Invoke($"[镜像] 层4: {err}");
                return false;
            }

            var (recheck, reSha, _, _) = await FetchMirrorMetadata();
            if (recheck && reSha == sha)
            {
                OutputReceived?.Invoke("[镜像] 层5: 锁期间他人已上传相同内容 → 释放锁跳过。");
                if (lockSha != null) await ReleaseLock(lockSha);
                return false;
            }

            try
            {
                var results = new System.Collections.Generic.Dictionary<string, bool>();
                foreach (var p in _platforms)
                {
                    OutputReceived?.Invoke($"[镜像] 上传到 {p.Name}...");
                    var platformOk = await p.UploadPackageAsync(pkgName, zip, sha);
                    results[p.Name] = platformOk;
                    OutputReceived?.Invoke($"[镜像] {p.Name}: {(platformOk ? "OK" : "失败")}");
                }

                var anyOk = results[GitHub.Name] || results[Codeberg.Name] || results[Gitee.Name];

                if (anyOk)
                {
                    OutputReceived?.Invoke("[镜像] 上传更新日志...");
                    await UploadChangelog();

                    OutputReceived?.Invoke("[镜像] 上传 latest 副本...");
                    await UploadLatestCopy(zip);

                    OutputReceived?.Invoke("[镜像] 同步 GM 工具源码...");
                    await MirrorGMTool(sha.Substring(0, 8));

                    var ghDownloadUrl = $"https://raw.githubusercontent.com/{GitHubRepo}/main/mirrors/{pkgName}.zip";
                    var cbDownloadUrl = $"https://codeberg.org/118coder/ServerS4A12.86JP/raw/branch/main/mirrors/{pkgName}.zip";
                    var gtDownloadUrl = $"https://gitee.com/c118oder/ServerS4A12.86JP/raw/main/mirrors/{pkgName}.zip";
                    var meta = JsonSerializer.Serialize(new
                    {
                        version = pkgName,
                        package = pkgName,
                        release_date = BeijingTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
                        sha256 = sha,
                        size_bytes = zipSize,
                        download_gitee = gtDownloadUrl,
                        download_github = ghDownloadUrl,
                        download_codeberg = cbDownloadUrl
                    });
                    var metaBytes = Encoding.UTF8.GetBytes(meta);

                    foreach (var p in _platforms)
                        await p.UploadFileAsync("latest.json", metaBytes, $"镜像更新 {pkgName}");
                }

                foreach (var p in _platforms)
                    await p.CleanupOldPackagesAsync(5);

                return results[GitHub.Name] || results[Codeberg.Name];
            }
            finally
            {
                if (lockSha != null) await ReleaseLock(lockSha);
                OutputReceived?.Invoke("[镜像] 已释放上传锁");
            }
        }
        catch (Exception ex)
        {
            OutputReceived?.Invoke($"[镜像] 异常: {ex.Message}");
            return false;
        }
    }

    async Task<(bool exists, string sha, string version, DateTime time)> FetchMirrorMetadata()
    {
        try
        {
            var token = Decode2("WjJod1gxQlpaVEZNYzBjMlpWZElhMkZNUTNWa1RVbHNkVTFEVmxKb1pqVlllREZwTUVoa01BPT0=");
            var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{GitHubRepo}/contents/latest.json?ref=main&t="
                + DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            req.Headers.Add("Authorization", "token " + token);
            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return (false, "", "", default);

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("content", out var contentEl)) return (false, "", "", default);
            var bytes = Convert.FromBase64String(contentEl.GetString() ?? "");
            var text = Encoding.UTF8.GetString(bytes);
            using var meta = JsonDocument.Parse(text);

            var sha = meta.RootElement.TryGetProperty("sha256", out var s) ? s.GetString() ?? "" : "";
            var ver = meta.RootElement.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
            var date = BeijingTime.Now;
            if (meta.RootElement.TryGetProperty("release_date", out var d)
                && DateTime.TryParse(d.GetString(), out var parsed))
                date = TimeZoneInfo.ConvertTime(parsed.ToUniversalTime(),
                    TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"));

            return (true, sha, ver, date);
        }
        catch { return (false, "", "", default); }
    }

    async Task<int> GetGitGudCommitCount()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.Add("User-Agent", "ServerUI-Mirror");
            client.DefaultRequestHeaders.Add("PRIVATE-TOKEN", GitGudToken);
            var baseUrl = "https://gitgud.io/api/v4/projects/rewio%2F86JP/repository/commits?ref_name=main&per_page=100&page=";
            int total = 0, page = 1;
            while (true)
            {
                var resp = await client.GetStringAsync(baseUrl + page);
                using var doc = JsonDocument.Parse(resp);
                int count = 0;
                foreach (var _ in doc.RootElement.EnumerateArray()) count++;
                total += count;
                if (count < 100) break;
                page++;
            }
            return total;
        }
        catch { return 0; }
    }

    async Task MirrorGMTool(string shaPrefix)
    {
        try
        {
            // v1.919: 从 GitGud 主源下载 GM 工具源码（与旧版参考脚本一致）
            var gmUrls = new[] {
                "https://gitgud.io/api/v4/projects/rewio%2F86JPGMTool/repository/archive.zip?sha=main"
            };
            byte[] gmZip = null;
            foreach (var url in gmUrls)
            {
                try
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                    client.DefaultRequestHeaders.Add("User-Agent", "ServerUI-Mirror/1.0");
                    if (url.Contains("gitgud.io"))
                        client.DefaultRequestHeaders.Add("PRIVATE-TOKEN", GitGudToken);
                    gmZip = await client.GetByteArrayAsync(url);
                    if (gmZip.Length > 10240) break;
                }
                catch { }
            }

            if (gmZip == null || gmZip.Length < 10240)
            {
                OutputReceived?.Invoke("[镜像] GM下载失败，跳过。");
                return;
            }

            var gmSha = Compat.Sha256Hex(gmZip).ToLower();
            OutputReceived?.Invoke($"[镜像] GM: {gmZip.Length/1024}KB, SHA:{gmSha.Substring(0, 8)}...");

            try
            {
                var latestDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AUM管理组件", "latest");
                if (!Directory.Exists(latestDir)) Directory.CreateDirectory(latestDir);
                File.WriteAllBytes(Path.Combine(latestDir, "DfoGmTool-latest.zip"), gmZip);
            }
            catch { }

            try
            {
                var token = Decode2("WjJod1gxQlpaVEZNYzBjMlpWZElhMkZNUTNWa1RVbHNkVTFEVmxKb1pqVlllREZwTUVoa01BPT0=");
                var req = new HttpRequestMessage(HttpMethod.Get,
                    $"https://api.github.com/repos/{GitHubRepo}/contents/mirrors/DfoGmTool-latest.zip?ref=main");
                req.Headers.Add("Authorization", "token " + token);
                var resp = await _http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("content", out var c))
                    {
                        var oldBytes = Convert.FromBase64String(c.GetString() ?? "");
                        var oldSha = Compat.Sha256Hex(oldBytes).ToLower();
                        if (oldSha == gmSha)
                        {
                            OutputReceived?.Invoke("[镜像] GM SHA相同 → 跳过。");
                            return;
                        }
                    }
                }
            }
            catch { }

            var gmPkg = "DfoGmTool-" + BeijingTime.Now.ToString("yyyyMMdd") + "-" + shaPrefix;
            var results = new System.Collections.Generic.Dictionary<string, bool>();

            foreach (var p in _platforms)
            {
                var ok = await p.UploadFileAsync("mirrors/" + gmPkg + ".zip", gmZip, "GM镜像 " + gmPkg);
                results[p.Name] = ok;
            }

            foreach (var p in _platforms)
                await p.UploadFileAsync("mirrors/DfoGmTool-latest.zip", gmZip, "GM latest");

            OutputReceived?.Invoke($"[镜像] GM镜像: {(results[GitHub.Name] || results[Codeberg.Name] || results[Gitee.Name] ? "OK" : "FAIL")}");
        }
        catch (Exception ex)
        {
            OutputReceived?.Invoke($"[镜像] GM异常: {ex.Message}");
        }
    }

    async Task UploadLatestCopy(byte[] zip)
    {
        try
        {
            foreach (var p in _platforms)
                await p.UploadFileAsync("mirrors/ServerS4A12-latest.zip", zip, "更新 latest");

            OutputReceived?.Invoke("[镜像] latest 副本已更新");
        }
        catch { }
    }

    async Task UploadChangelog()
    {
        try
        {
            var exeDir = AppDomain.CurrentDomain.BaseDirectory;
            var logFile = Path.Combine(exeDir, "AUM管理组件", "更新日志.txt");
            if (!File.Exists(logFile))
                logFile = Path.Combine(exeDir, "更新日志.txt");
            if (!File.Exists(logFile))
            {
                OutputReceived?.Invoke("[镜像] 本地无更新日志，跳过上传。");
                return;
            }

            var bytes = File.ReadAllBytes(logFile);
            var sha = Compat.Sha256Hex(bytes).ToLower();
            OutputReceived?.Invoke($"[镜像] 更新日志 SHA:{sha.Substring(0, 8)}... 大小:{bytes.Length}B");

            // 先检查 GitHub 上的 SHA 是否已相同（快速跳过）
            try
            {
                var token = Decode2("WjJod1gxQlpaVEZNYzBjMlpWZElhMkZNUTNWa1RVbHNkVTFEVmxKb1pqVlllREZwTUVoa01BPT0=");
                var req = new HttpRequestMessage(HttpMethod.Get,
                    $"https://api.github.com/repos/{GitHubRepo}/contents/mirrors/%E6%9B%B4%E6%96%B0%E6%97%A5%E5%BF%97.txt?ref=main");
                req.Headers.Add("Authorization", "token " + token);
                var resp = await _http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("content", out var c))
                    {
                        var existingBytes = Convert.FromBase64String(c.GetString() ?? "");
                        var existingSha = Compat.Sha256Hex(existingBytes).ToLower();
                        if (existingSha == sha)
                        {
                            OutputReceived?.Invoke("[镜像] 更新日志 SHA相同 → 跳过。");
                            return;
                        }
                        OutputReceived?.Invoke($"[镜像] 更新日志变更: 旧SHA={existingSha.Substring(0, 8)}... → 新SHA={sha.Substring(0, 8)}...");
                    }
                }
            }
            catch { OutputReceived?.Invoke("[镜像] 更新日志首次上传。"); }

            // 每个平台使用各自的独立上传逻辑（自行处理编码差异）
            foreach (var p in _platforms)
            {
                var platformOk = await p.UploadChangelogAsync(bytes, sha, "更新日志同步");
                OutputReceived?.Invoke($"[镜像] {p.Name} 更新日志: {(platformOk ? "OK" : "失败")}");
            }
        }
        catch (Exception ex)
        {
            OutputReceived?.Invoke($"[镜像] 更新日志上传异常: {ex.Message}");
        }
    }
}
