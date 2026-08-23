using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ServerUI.Services.PlatformAdapters;

public class CodebergAdapter : IMirrorPlatform
{
    public string Name => "Codeberg";

    const string TokenB64 = "WlRKa09HVmpOR1E1TW1Zek5UUmpZVFZrT0dOa1kyTTFaVFUyWmpNek1EVTNaRGRpTVRVM01RPT0=";
    const string Repo = "118coder/ServerS4A12.86JP";
    static string Token => Encoding.UTF8.GetString(Convert.FromBase64String(Encoding.UTF8.GetString(Convert.FromBase64String(TokenB64))));
    string ApiBase => $"https://codeberg.org/api/v1/repos/{Repo}/contents";
    string Auth => "token " + Token;

    static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public async Task<bool> UploadPackageAsync(string pkgName, byte[] zip, string sha)
    {
        try
        {
            var zipPath = "mirrors/" + pkgName + ".zip";
            var zipB64 = Convert.ToBase64String(zip);
            var zipBody = JsonSerializer.Serialize(new
            {
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
                download_url = $"https://codeberg.org/{Repo}/raw/branch/main/mirrors/{pkgName}.zip"
            });

            var metaB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(meta));
            var metaBody = JsonSerializer.Serialize(new
            {
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
            postReq.Headers.Add("Authorization", Auth);
            postReq.Content = new StringContent(body, Encoding.UTF8, "application/json");
            var resp = await _http.SendAsync(postReq);
            if (resp.IsSuccessStatusCode) return true;

            var status = (int)resp.StatusCode;
            if (status == 409 || status == 422)
            {
                var getReq = new HttpRequestMessage(HttpMethod.Get, url);
                getReq.Headers.Add("Authorization", Auth);
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
                            content = bodyDoc.RootElement.TryGetProperty("content", out var c) ? c.GetString() : "",
                            message = bodyDoc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : "update",
                            sha = fileSha.GetString(),
                            branch = "main"
                        });
                        var putReq = new HttpRequestMessage(HttpMethod.Put, url);
                        putReq.Headers.Add("Authorization", Auth);
                        putReq.Content = new StringContent(putBody, Encoding.UTF8, "application/json");
                        resp = await _http.SendAsync(putReq);
                        return resp.IsSuccessStatusCode;
                    }
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
            var path = "mirrors/%E6%9B%B4%E6%96%B0%E6%97%A5%E5%BF%97.txt";
            var b64 = Convert.ToBase64String(data);
            var body = JsonSerializer.Serialize(new { content = b64, message, branch = "main" });
            return await PutOrPostFileAsync(path, body);
        }
        catch { return false; }
    }

    public async Task CleanupOldPackagesAsync(int keepCount = 5)
    {
        try
        {
            var url = $"{ApiBase}/mirrors";
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Authorization", Auth);
            var resp = await _http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var files = doc.RootElement.EnumerateArray()
                    .Select(f => (Name: f.TryGetProperty("name", out var n) ? n.GetString() : "",
                                  Sha: f.TryGetProperty("sha", out var s) ? s.GetString() : ""))
                    .Where(f => f.Name.StartsWith("ServerS4A12-") && f.Name.EndsWith(".zip"))
                    .OrderByDescending(f => f.Name)
                    .ToList();

                foreach (var f in files.Skip(keepCount))
                {
                    var delBody = JsonSerializer.Serialize(new { message = "清理旧镜像", sha = f.Sha, branch = "main" });
                    var delReq = new HttpRequestMessage(HttpMethod.Delete, $"{url}/{f.Name}");
                    delReq.Headers.Add("Authorization", Auth);
                    delReq.Content = new StringContent(delBody, Encoding.UTF8, "application/json");
                    await _http.SendAsync(delReq);
                }
            }
        }
        catch { }
    }
}
