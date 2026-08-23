using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ServerUI.Services.PlatformAdapters;

public class GiteeAdapter : IMirrorPlatform
{
    public string Name => "Gitee";

    const string TokenB64 = "WlRsbVpXWmlPRE0zWWpsaU5UVTBaamRpTVdaak4yRXdZbVprTlRKaFpUaz0=";
    const string Repo = "c118oder/ServerS4A12.86JP";
    static string Token => Encoding.UTF8.GetString(Convert.FromBase64String(Encoding.UTF8.GetString(Convert.FromBase64String(TokenB64))));
    string ApiBase => $"https://gitee.com/api/v5/repos/{Repo}/contents";

    static readonly HttpClient _http = new(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate })
    {
        Timeout = TimeSpan.FromSeconds(180)
    };
    static GiteeAdapter() { _http.DefaultRequestHeaders.Add("User-Agent", "ServerUI-Mirror/1.0"); }

    public async Task<bool> UploadPackageAsync(string pkgName, byte[] zip, string sha)
    {
        try
        {
            var zipPath = "mirrors/" + pkgName + ".zip";
            var zipB64 = Convert.ToBase64String(zip);
            var zipBody = JsonSerializer.Serialize(new
            {
                access_token = Token,
                content = zipB64,
                message = $"镜像同步 {pkgName}",
                branch = "main"
            });

            var ok = await PutOrPostFileAsync(zipPath, zipBody);
            if (!ok) return false;

            var meta = JsonSerializer.Serialize(new
            {
                package = pkgName,
                release_date = DateTime.UtcNow.AddHours(8).ToString("yyyy-MM-ddTHH:mm:sszzz"),
                sha256 = sha,
                size_bytes = zip.Length,
                download_url = $"https://gitee.com/{Repo}/raw/main/mirrors/{pkgName}.zip"
            });

            var metaB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(meta));
            var metaBody = JsonSerializer.Serialize(new
            {
                access_token = Token,
                content = metaB64,
                message = $"更新元数据 {pkgName}",
                branch = "main"
            });

            await PutOrPostFileAsync("latest.json", metaBody);
            return true;
        }
        catch { return false; }
    }

    public async Task<bool> UploadFileAsync(string remotePath, byte[] data, string message)
    {
        try
        {
            var b64 = Convert.ToBase64String(data);
            var body = JsonSerializer.Serialize(new
            {
                access_token = Token,
                content = b64,
                message,
                branch = "main"
            });
            return await PutOrPostFileAsync(remotePath, body);
        }
        catch { return false; }
    }

    async Task<bool> PutOrPostFileAsync(string path, string body)
    {
        try
        {
            var url = $"{ApiBase}/{path}";
            var postReq = new HttpRequestMessage(HttpMethod.Post, url);
            postReq.Content = new StringContent(body, Encoding.UTF8, "application/json");
            var resp = await _http.SendAsync(postReq);
            if (resp.IsSuccessStatusCode) return true;

            // POST 失败时（任意状态码），尝试获取 SHA 后用 PUT 更新
            var status = (int)resp.StatusCode;
            var getReq = new HttpRequestMessage(HttpMethod.Get, $"{url}?access_token={Token}");
            var getResp = await _http.SendAsync(getReq);
            if (getResp.IsSuccessStatusCode)
            {
                var getJson = await getResp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(getJson);
                if (doc.RootElement.TryGetProperty("sha", out var fileSha))
                {
                    using var bodyDoc = JsonDocument.Parse(body);
                    var putBody = JsonSerializer.Serialize(new
                    {
                        access_token = Token,
                        content = bodyDoc.RootElement.TryGetProperty("content", out var c) ? c.GetString() : "",
                        message = bodyDoc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : "update",
                        sha = fileSha.GetString(),
                        branch = "main"
                    });
                    var putReq = new HttpRequestMessage(HttpMethod.Put, url);
                    putReq.Content = new StringContent(putBody, Encoding.UTF8, "application/json");
                    resp = await _http.SendAsync(putReq);
                    return resp.IsSuccessStatusCode;
                }
            }
            return false;
        }
        catch { return false; }
    }

    public async Task<bool> UploadChangelogAsync(byte[] data, string sha, string message)
    {
        try
        {
            // 显式编码中文文件名，避免 Uri 构造时的编码歧义
            var encodedName = Uri.EscapeDataString("更新日志.txt");
            return await UploadFileAsync("mirrors/" + encodedName, data, message);
        }
        catch { return false; }
    }

    public async Task CleanupOldPackagesAsync(int keepCount = 5)
    {
        try
        {
            var url = $"{ApiBase}/mirrors?access_token={Token}&ref=main";
            var getReq = new HttpRequestMessage(HttpMethod.Get, url);
            var getResp = await _http.SendAsync(getReq);
            if (!getResp.IsSuccessStatusCode) return;

            var json = await getResp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var files = doc.RootElement.EnumerateArray()
                .Select(f => (Name: f.TryGetProperty("name", out var n) ? n.GetString() : "",
                              Sha: f.TryGetProperty("sha", out var s) ? s.GetString() : ""))
                .Where(f => f.Name.StartsWith("ServerS4A12-") && f.Name.EndsWith(".zip"))
                .OrderByDescending(f => f.Name)
                .ToList();

            foreach (var f in files.Skip(keepCount))
            {
                var delBody = JsonSerializer.Serialize(new
                {
                    access_token = Token,
                    message = "清理旧镜像",
                    sha = f.Sha,
                    branch = "main"
                });
                var delReq = new HttpRequestMessage(HttpMethod.Delete, $"{ApiBase}/mirrors/{f.Name}?access_token={Token}");
                delReq.Content = new StringContent(delBody, Encoding.UTF8, "application/json");
                await _http.SendAsync(delReq);
            }
        }
        catch { }
    }
}
