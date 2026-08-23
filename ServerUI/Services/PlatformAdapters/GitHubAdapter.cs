using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ServerUI.Services.PlatformAdapters;

public class GitHubAdapter : IMirrorPlatform
{
    public string Name => "GitHub";

    const string TokenB64 = "WjJod1gxQlpaVEZNYzBjMlpWZElhMkZNUTNWa1RVbHNkVTFEVmxKb1pqVlllREZwTUVoa01BPT0=";
    const string Repo = "118coder/ServerS4A12.86JP";
    static string Token => Encoding.UTF8.GetString(Convert.FromBase64String(Encoding.UTF8.GetString(Convert.FromBase64String(TokenB64))));

    static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public GitHubAdapter()
    {
        _http.DefaultRequestHeaders.Add("User-Agent", "ServerUI-Mirror/1.0");
    }

    public async Task<bool> UploadPackageAsync(string pkgName, byte[] zip, string sha)
    {
        try
        {
            var releaseUrl = $"https://api.github.com/repos/{Repo}/releases";

            try
            {
                var listReq = new HttpRequestMessage(HttpMethod.Get, releaseUrl);
                listReq.Headers.Add("Authorization", "token " + Token);
                var listResp = await _http.SendAsync(listReq);
                if (listResp.IsSuccessStatusCode)
                {
                    var listJson = await listResp.Content.ReadAsStringAsync();
                    using var listDoc = JsonDocument.Parse(listJson);
                    foreach (var rel in listDoc.RootElement.EnumerateArray())
                    {
                        if (rel.TryGetProperty("tag_name", out var tag) && tag.GetString() == pkgName)
                        {
                            if (rel.TryGetProperty("id", out var id))
                            {
                                var delReq = new HttpRequestMessage(HttpMethod.Delete, $"{releaseUrl}/{id.GetInt32()}");
                                delReq.Headers.Add("Authorization", "token " + Token);
                                await _http.SendAsync(delReq);
                                break;
                            }
                        }
                    }
                }
            }
            catch { }

            var createBody = JsonSerializer.Serialize(new
            {
                tag_name = pkgName,
                name = pkgName,
                body = $"自动镜像同步 {pkgName}\nSHA256: {sha}\n大小: {zip.Length / 1024} KB",
                draft = false,
                prerelease = false
            });

            var createReq = new HttpRequestMessage(HttpMethod.Post, releaseUrl);
            createReq.Headers.Add("Authorization", "token " + Token);
            createReq.Content = new StringContent(createBody, Encoding.UTF8, "application/json");
            var createResp = await _http.SendAsync(createReq);

            if (!createResp.IsSuccessStatusCode) return false;

            var createJson = await createResp.Content.ReadAsStringAsync();
            using var cd = JsonDocument.Parse(createJson);
            if (!cd.RootElement.TryGetProperty("upload_url", out var uploadUrlEl)) return false;
            var uploadUrl = uploadUrlEl.GetString()!.Replace("{?name,label}", $"?name={pkgName}.zip");

            using var uploadContent = new ByteArrayContent(zip);
            uploadContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            var uploadReq = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
            uploadReq.Headers.Add("Authorization", "token " + Token);
            uploadReq.Content = uploadContent;
            var uploadResp = await _http.SendAsync(uploadReq);
            return uploadResp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<bool> UploadFileAsync(string remotePath, byte[] data, string message)
    {
        try
        {
            var url = $"https://api.github.com/repos/{Repo}/contents/{remotePath}";
            string sha = null;
            try
            {
                var getReq = new HttpRequestMessage(HttpMethod.Get, url);
                getReq.Headers.Add("Authorization", "token " + Token);
                var getResp = await _http.SendAsync(getReq);
                if (getResp.IsSuccessStatusCode)
                {
                    var getJson = await getResp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(getJson);
                    if (doc.RootElement.TryGetProperty("sha", out var s)) sha = s.GetString();
                }
            }
            catch { }

            var b64 = Convert.ToBase64String(data);
            var body = JsonSerializer.Serialize(new
            {
                message,
                content = b64,
                sha,
                branch = "main"
            });

            var putReq = new HttpRequestMessage(HttpMethod.Put, url);
            putReq.Headers.Add("Authorization", "token " + Token);
            putReq.Content = new StringContent(body, Encoding.UTF8, "application/json");
            var resp = await _http.SendAsync(putReq);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<bool> UploadChangelogAsync(byte[] data, string sha, string message)
    {
        try
        {
            var path = "mirrors/%E6%9B%B4%E6%96%B0%E6%97%A5%E5%BF%97.txt";
            var url = $"https://api.github.com/repos/{Repo}/contents/{path}";

            string existingSha = null;
            try
            {
                var getReq = new HttpRequestMessage(HttpMethod.Get, url);
                getReq.Headers.Add("Authorization", "token " + Token);
                var getResp = await _http.SendAsync(getReq);
                if (getResp.IsSuccessStatusCode)
                {
                    var getJson = await getResp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(getJson);
                    if (doc.RootElement.TryGetProperty("sha", out var s))
                        existingSha = s.GetString();
                }
            }
            catch { }

            var b64 = Convert.ToBase64String(data);
            var body = JsonSerializer.Serialize(new { message, content = b64, sha = existingSha, branch = "main" });

            var putReq = new HttpRequestMessage(HttpMethod.Put, url);
            putReq.Headers.Add("Authorization", "token " + Token);
            putReq.Content = new StringContent(body, Encoding.UTF8, "application/json");
            var resp = await _http.SendAsync(putReq);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task CleanupOldPackagesAsync(int keepCount = 5)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{Repo}/releases?per_page=50");
            req.Headers.Add("Authorization", "token " + Token);
            var resp = await _http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var releases = doc.RootElement.EnumerateArray()
                    .Select(r => (Id: r.TryGetProperty("id", out var id) ? id.GetInt32() : 0,
                                  Tag: r.TryGetProperty("tag_name", out var t) ? t.GetString() : ""))
                    .Where(r => r.Tag.StartsWith("ServerS4A12-"))
                    .OrderByDescending(r => r.Tag)
                    .ToList();

                foreach (var rel in releases.Skip(keepCount))
                {
                    var del = new HttpRequestMessage(HttpMethod.Delete,
                        $"https://api.github.com/repos/{Repo}/releases/{rel.Id}");
                    del.Headers.Add("Authorization", "token " + Token);
                    await _http.SendAsync(del);
                }
            }
        }
        catch { }

        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{Repo}/contents/mirrors");
            req.Headers.Add("Authorization", "token " + Token);
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
                    var delReq = new HttpRequestMessage(HttpMethod.Delete, $"https://api.github.com/repos/{Repo}/contents/mirrors/{f.Name}");
                    delReq.Headers.Add("Authorization", "token " + Token);
                    delReq.Content = new StringContent(delBody, Encoding.UTF8, "application/json");
                    await _http.SendAsync(delReq);
                }
            }
        }
        catch { }
    }
}
