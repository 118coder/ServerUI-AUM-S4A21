using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ServerUI.Web.Bridge.Aum;

/// <summary>一个候选 Release（已通过 draft/prerelease/tag 形式过滤）。</summary>
internal sealed class AumReleaseCandidate
{
    public string Version;          // 2.21
    public string Tag;              // v2.21
    public string ManifestUrl;      // latest.json 下载地址
    public string SourceZipUrl;     // 该 tag 的源码 zip
    public string VersionFileUrl;   // 该 tag 的 AUM-version.txt
    public string ReleasePageUrl;
    public Dictionary<string, string> AssetDigests = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Release 发现 + 清单获取。生产环境用 <see cref="AumGitHubSource"/>；
/// <c>--updatetest</c> 用 <see cref="AumStaticSource"/>（本地 127.0.0.1）。
/// </summary>
internal interface IAumReleaseSource
{
    string Name { get; }

    /// <summary>返回按版本降序排列的候选；空列表表示仓库没有任何可用 Release。</summary>
    Task<(List<AumReleaseCandidate> Items, string Error)> ListAsync(
        HttpClient http, AumNetPolicy policy, CancellationToken token, Action<string> log);

    /// <summary>取清单原文（可能来自 latest.json 资产）。</summary>
    Task<(bool Ok, string Body, string Error)> GetManifestTextAsync(
        HttpClient http, AumNetPolicy policy, AumReleaseCandidate c, CancellationToken token);

    /// <summary>取该 tag 的 AUM-version.txt 原文。</summary>
    Task<(bool Ok, string Body, string Error)> GetVersionFileAsync(
        HttpClient http, AumNetPolicy policy, AumReleaseCandidate c, CancellationToken token);

    /// <summary>下载发行包资产到 destPath。</summary>
    Task<AumNet.DownloadOutcome> DownloadAssetAsync(
        HttpClient http, AumNetPolicy policy, AumReleaseCandidate c, AumAsset asset,
        string destPath, Action<string> log, Action<long, long> onProgress, CancellationToken token);

    /// <summary>下载源码 zip（用于源码编译路径；大小未知）。</summary>
    Task<AumNet.DownloadOutcome> DownloadSourceAsync(
        HttpClient http, AumNetPolicy policy, AumReleaseCandidate c, string destPath,
        Action<string> log, Action<long, long> onProgress, CancellationToken token);
}

internal sealed class AumGitHubSource : IAumReleaseSource
{
    public const string Repo = "118coder/ServerUI-AUM-S4A21";
    public const string RepoUrl = "https://github.com/" + Repo;
    public const string ApiBase = "https://api.github.com/repos/" + Repo;

    private static readonly Regex TagRe = new Regex(@"^v(\d+(?:\.\d+){1,3})$", RegexOptions.Compiled);
    private const long MaxSourceZipBytes = 200L * 1024 * 1024;

    public string Name => "GitHub " + Repo;

    public async Task<(List<AumReleaseCandidate> Items, string Error)> ListAsync(
        HttpClient http, AumNetPolicy policy, CancellationToken token, Action<string> log)
    {
        var list = new List<AumReleaseCandidate>();

        var (ok, body, err) = await AumNet.GetStringAsync(
            http, policy, ApiBase + "/releases?per_page=50", 1024 * 1024, token).ConfigureAwait(false);

        if (ok)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var rel in doc.RootElement.EnumerateArray())
                    {
                        var c = ParseApiRelease(rel);
                        if (c != null) list.Add(c);
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("[AUM更新] Releases API 解析失败：" + ex.Message);
            }
        }
        else
        {
            log?.Invoke("[AUM更新] Releases API 不可用（" + err + "），改用 releases/latest 单一入口");
        }

        if (list.Count == 0)
        {
            var single = await ResolveLatestRedirectAsync(http, policy, token, log).ConfigureAwait(false);
            if (single != null) list.Add(single);
        }

        list.Sort((a, b) => AumVersion.Compare(b.Version, a.Version));
        return (list, list.Count == 0 ? (err ?? "仓库没有可用 Release") : null);
    }

    private static AumReleaseCandidate ParseApiRelease(JsonElement rel)
    {
        if (rel.ValueKind != JsonValueKind.Object) return null;
        if (rel.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
        if (rel.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;
        if (!rel.TryGetProperty("tag_name", out var tagEl) || tagEl.ValueKind != JsonValueKind.String) return null;

        var tag = tagEl.GetString() ?? "";
        var m = TagRe.Match(tag);
        if (!m.Success) return null;
        var version = m.Groups[1].Value;

        var c = new AumReleaseCandidate
        {
            Version = version,
            Tag = tag,
            ReleasePageUrl = RepoUrl + "/releases/tag/" + tag,
            VersionFileUrl = "https://raw.githubusercontent.com/" + Repo + "/" + tag + "/AUM-version.txt",
            SourceZipUrl = RepoUrl + "/archive/refs/tags/" + tag + ".zip",
            ManifestUrl = RepoUrl + "/releases/download/" + tag + "/latest.json"
        };

        if (rel.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                if (!a.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) continue;
                var name = nameEl.GetString();

                if (string.Equals(name, "latest.json", StringComparison.OrdinalIgnoreCase) &&
                    a.TryGetProperty("browser_download_url", out var urlEl) &&
                    urlEl.ValueKind == JsonValueKind.String)
                {
                    c.ManifestUrl = urlEl.GetString();
                }

                if (a.TryGetProperty("digest", out var digEl) && digEl.ValueKind == JsonValueKind.String)
                {
                    var digest = digEl.GetString() ?? "";
                    if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        c.AssetDigests[name] = digest.Substring(7).Trim().ToLowerInvariant();
                }
            }
        }

        return c;
    }

    /// <summary>GET /releases/latest（不自动跟跳）→ 从 Location 里取 tag。</summary>
    private async Task<AumReleaseCandidate> ResolveLatestRedirectAsync(
        HttpClient http, AumNetPolicy policy, CancellationToken token, Action<string> log)
    {
        try
        {
            var url = RepoUrl + "/releases/latest";
            if (!policy.TryValidate(url, out var uri, out var urlError))
            {
                log?.Invoke("[AUM更新] " + urlError);
                return null;
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            var code = (int)resp.StatusCode;
            var loc = resp.Headers.Location;
            if (code < 300 || code >= 400 || loc == null) return null;

            var target = loc.IsAbsoluteUri ? loc.ToString() : new Uri(uri, loc).ToString();
            var m = Regex.Match(target, @"/releases/tag/(v\d+(?:\.\d+){1,3})$", RegexOptions.IgnoreCase);
            if (!m.Success) return null;

            var tag = m.Groups[1].Value;
            var version = tag.Substring(1);
            log?.Invoke("[AUM更新] releases/latest → " + tag);
            return new AumReleaseCandidate
            {
                Version = version,
                Tag = tag,
                ReleasePageUrl = target,
                VersionFileUrl = "https://raw.githubusercontent.com/" + Repo + "/" + tag + "/AUM-version.txt",
                SourceZipUrl = RepoUrl + "/archive/refs/tags/" + tag + ".zip",
                ManifestUrl = RepoUrl + "/releases/download/" + tag + "/latest.json"
            };
        }
        catch (Exception ex)
        {
            log?.Invoke("[AUM更新] 解析 releases/latest 失败：" + ex.Message);
            return null;
        }
    }

    public Task<(bool Ok, string Body, string Error)> GetManifestTextAsync(
        HttpClient http, AumNetPolicy policy, AumReleaseCandidate c, CancellationToken token)
        => AumNet.GetStringAsync(http, policy, c.ManifestUrl, 256 * 1024, token);

    public async Task<(bool Ok, string Body, string Error)> GetVersionFileAsync(
        HttpClient http, AumNetPolicy policy, AumReleaseCandidate c, CancellationToken token)
    {
        var r = await AumNet.GetStringAsync(http, policy, c.VersionFileUrl, 4096, token).ConfigureAwait(false);
        return r.Ok ? (true, (r.Body ?? "").Trim(), null) : (false, null, r.Error);
    }

    public Task<AumNet.DownloadOutcome> DownloadAssetAsync(
        HttpClient http, AumNetPolicy policy, AumReleaseCandidate c, AumAsset asset,
        string destPath, Action<string> log, Action<long, long> onProgress, CancellationToken token)
    {
        var url = RepoUrl + "/releases/download/" + c.Tag + "/" + asset.Name;
        return AumNet.DownloadAsync(http, policy, url, destPath, asset.Size, asset.Size, asset.Sha256, log, onProgress, token);
    }

    public Task<AumNet.DownloadOutcome> DownloadSourceAsync(
        HttpClient http, AumNetPolicy policy, AumReleaseCandidate c, string destPath,
        Action<string> log, Action<long, long> onProgress, CancellationToken token)
        => AumNet.DownloadAsync(http, policy, c.SourceZipUrl, destPath, 0, MaxSourceZipBytes, null, log, onProgress, token);
}

/// <summary>
/// --updatetest 专用的静态源：所有内容都由本地 HTTP 服务提供。
/// 通过 Config 显式制造各种坏数据；正常运行路径不会构造这个类型。
/// </summary>
internal sealed class AumStaticSource : IAumReleaseSource
{
    internal sealed class Config
    {
        public string BaseUrl;
        public string TagOverride;            // 模拟 tag 与清单版本不符
        public string ManifestOverride;       // 直接替换清单正文
        public bool OmitManifest;             // 404
        public bool OmitVersionFile;
        public string VersionFileOverride;    // AUM-version.txt 内容
        public string SourcePath = "source.zip";
        public bool OmitSource;
        public string ManifestPath = "latest.json";
        public string AssetPathPrefix = "";   // 默认 /<name>
    }

    private readonly Config _cfg;
    public readonly List<string> Requests = new List<string>();

    public AumStaticSource(Config cfg) { _cfg = cfg; }

    public string Name => "静态测试源 " + _cfg.BaseUrl;

    public Task<(List<AumReleaseCandidate> Items, string Error)> ListAsync(
        HttpClient http, AumNetPolicy policy, CancellationToken token, Action<string> log)
    {
        var list = new List<AumReleaseCandidate>();
        if (_cfg.OmitManifest) return Task.FromResult((list, "测试配置：清单缺失"));

        var version = _cfg.TagOverride != null
            ? (_cfg.TagOverride.StartsWith("v") ? _cfg.TagOverride.Substring(1) : _cfg.TagOverride)
            : "0.0";
        var c = new AumReleaseCandidate
        {
            Version = version,
            Tag = "v" + version,
            ReleasePageUrl = _cfg.BaseUrl,
            ManifestUrl = _cfg.BaseUrl + "/" + _cfg.ManifestPath,
            VersionFileUrl = _cfg.BaseUrl + "/AUM-version.txt",
            SourceZipUrl = _cfg.BaseUrl + "/" + _cfg.SourcePath
        };
        list.Add(c);
        return Task.FromResult((list, (string)null));
    }

    public Task<(bool Ok, string Body, string Error)> GetManifestTextAsync(
        HttpClient http, AumNetPolicy policy, AumReleaseCandidate c, CancellationToken token)
    {
        Requests.Add(c.ManifestUrl);
        if (_cfg.ManifestOverride != null) return Task.FromResult((true, _cfg.ManifestOverride, (string)null));
        return AumNet.GetStringAsync(http, policy, c.ManifestUrl, 256 * 1024, token);
    }

    public Task<(bool Ok, string Body, string Error)> GetVersionFileAsync(
        HttpClient http, AumNetPolicy policy, AumReleaseCandidate c, CancellationToken token)
    {
        Requests.Add(c.VersionFileUrl);
        if (_cfg.OmitVersionFile) return Task.FromResult((false, (string)null, "测试配置：AUM-version.txt 缺失"));
        if (_cfg.VersionFileOverride != null) return Task.FromResult((true, _cfg.VersionFileOverride, (string)null));
        return AumNet.GetStringAsync(http, policy, c.VersionFileUrl, 4096, token);
    }

    public Task<AumNet.DownloadOutcome> DownloadAssetAsync(
        HttpClient http, AumNetPolicy policy, AumReleaseCandidate c, AumAsset asset,
        string destPath, Action<string> log, Action<long, long> onProgress, CancellationToken token)
    {
        var url = _cfg.BaseUrl + "/" + _cfg.AssetPathPrefix + asset.Name;
        Requests.Add(url);
        return AumNet.DownloadAsync(http, policy, url, destPath, asset.Size, asset.Size, asset.Sha256, log, onProgress, token);
    }

    public Task<AumNet.DownloadOutcome> DownloadSourceAsync(
        HttpClient http, AumNetPolicy policy, AumReleaseCandidate c, string destPath,
        Action<string> log, Action<long, long> onProgress, CancellationToken token)
    {
        Requests.Add(c.SourceZipUrl);
        if (_cfg.OmitSource)
        {
            return Task.FromResult(new AumNet.DownloadOutcome { Ok = false, Error = "测试配置：源码包缺失" });
        }
        return AumNet.DownloadAsync(http, policy, c.SourceZipUrl, destPath, 0, 200L * 1024 * 1024, null, log, onProgress, token);
    }
}
