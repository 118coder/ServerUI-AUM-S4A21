using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ServerUI.Web.Bridge.Aum;

namespace ServerUI.Web;

/// <summary>
/// `--updatetest`：AUM 自更新双通道的沙箱验收。
///
/// 全程只碰 %TEMP% 与本机 127.0.0.1 随机端口静态服务：
///   · 不访问 GitHub、不读真实游戏目录、不替换真实 exe、不重启进程。
///   · 目标 exe 是测试自己生成的假 PE（带 FileVersion），因此版本/PE 校验走的是真实代码路径。
///
/// 每个用例打印 `[PASS]/[FAIL] 名称 · 预期 · 实际`，末尾输出汇总；有失败则 exit 1。
/// </summary>
internal static class UpdaterTests
{
    // 夹具 exe 是真实构建产物（FileVersion 2.20.x）：
    // 目标版本必须与它 Major.Minor 一致，否则版本校验会正确地拒绝。
    private const string CurVer = "2.20";
    private const string TargetVer = "2.20.1";
    private static int _pass, _fail;
    private static readonly StringBuilder Report = new StringBuilder();
    private static string _root;

    internal static int Run(string[] args)
    {
        try
        {
            _root = Path.Combine(Path.GetTempPath(), "ServerUI-updatetest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_root);
            Console.WriteLine("updatetest root: " + _root);

            // 必须在线程池上跑：本方法由 WPF OnStartup 调用（Dispatcher 线程），
            // 直接 .GetAwaiter().GetResult() 会与 await 捕获的 DispatcherSynchronizationContext
            // 互相等待 → 永久死锁（实测卡住不发任何输出）。
            Task.Run(() => RunAllAsync()).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine("[FAIL] 测试框架异常 · 预期：正常跑完 · 实际：" + ex);
            _fail++;
        }
        finally
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine("===== 汇总：" + _pass + " 通过 / " + _fail + " 失败 =====");
        try
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "ServerUI-updatetest.txt"),
                Report.ToString(), new UTF8Encoding(false));
        }
        catch { }
        return _fail == 0 ? 0 : 1;
    }

    private static async Task RunAllAsync()
    {
        // ---------------- 1. 正常升级（fd）
        {
            var env = await Scenario.BuildAsync(_root, "normal-fd", TargetVer, v => v);
            var (plan, result) = await env.RunAsync(CurVer, force: false, forcePackage: false);
            Check("正常升级（fd）· 策略",
                "strategy=package 且 target=2.21",
                plan.Strategy + " / " + plan.TargetVersion,
                plan.Ok && plan.Strategy == "package" && plan.TargetVersion == TargetVer);
            // 夹具是真实 v2.20 产物：替换后 FileVersion 仍是 2.20.0.0（内容与原来相同哈希也不同不了），
            // 因此这里断言"替换动作真正发生"：成功 + 备份存在 + 目标文件被替换过（mtime 更新）。
            Check("正常升级（fd）· 替换成功",
                "ok=true 且目标文件被替换（mtime 更新 + 有备份）",
                "ok=" + result.Ok + " err=" + result.Error + " replaced=" + env.TargetReplaced(),
                result.Ok && env.TargetReplaced() && env.BakFiles().Count >= 1);
            Check("正常升级（fd）· 保留备份",
                "存在 .bak-2.20-* 备份",
                env.BakFiles().Count == 0 ? "无备份" : string.Join(",", env.BakFiles()),
                env.BakFiles().Count >= 1);
        }

        // ---------------- 2. sha256 错误
        {
            var env = await Scenario.BuildAsync(_root, "bad-sha", TargetVer, m => m.Replace("\"sha256\": \"", "\"sha256\": \"dead"));
            var (plan, result) = await env.RunAsync(CurVer, false, false);
            Check("sha256 错误 · 拒绝",
                "ok=false 且目标 exe 仍为 2.20",
                "ok=" + result.Ok + " ver=" + env.TargetVersionNow(),
                !result.Ok && env.TargetVersionNow().StartsWith(CurVer));
            Check("sha256 错误 · 不产生 .new/.bak",
                "无 .new、无 .bak",
                "new=" + env.NewFiles().Count + " bak=" + env.BakFiles().Count,
                env.NewFiles().Count == 0 && env.BakFiles().Count == 0);
        }

        // ---------------- 3. size 不符
        {
            var env = await Scenario.BuildAsync(_root, "bad-size", TargetVer,
                m => System.Text.RegularExpressions.Regex.Replace(m, "\"size\":\\s*\\d+", "\"size\": 123456"));
            var (plan, result) = await env.RunAsync(CurVer, false, false);
            Check("size 不符 · 拒绝",
                "ok=false（下载前即失败）",
                "ok=" + result.Ok + " err=" + Trim(result.Error),
                !result.Ok);
        }

        // ---------------- 4. 清单版本与 tag 不符
        {
            var env = await Scenario.BuildAsync(_root, "ver-mismatch", TargetVer,
                m => m.Replace("\"version\": \"" + TargetVer + "\"", "\"version\": \"2.99\""), tagOverride: TargetVer);
            var (plan, result) = await env.RunAsync(CurVer, false, false);
            Check("清单 version 与 tag 不符 · 拒绝",
                "plan.Ok=false 且提示不可信",
                "ok=" + plan.Ok + " err=" + Trim(plan.Error),
                !plan.Ok && (plan.Error ?? "").Contains("不符", StringComparison.Ordinal)
                    || (plan.Error ?? "").Contains("校验失败", StringComparison.Ordinal));
        }

        // ---------------- 5. 缺当前形态资产
        {
            var env = await Scenario.BuildAsync(_root, "missing-asset", TargetVer,
                m => RemoveAssetKind(m, "fd"));
            var (plan, result) = await env.RunAsync(CurVer, false, false);
            Check("清单缺当前形态（fd）资产 · 拒绝",
                "plan.Ok=false",
                "ok=" + plan.Ok + " err=" + Trim(plan.Error),
                !plan.Ok);
        }

        // ---------------- 6. zip 缺 inner
        {
            // 包内只有 WrongName.exe，清单仍声明 ServerUI-有依赖版.exe → 必须拒绝
            var env = await Scenario.BuildAsync(_root, "no-inner", TargetVer,
                m => m, zipInner: "WrongName.exe", manifestInner: "ServerUI-有依赖版.exe");
            var (plan, result) = await env.RunAsync(CurVer, false, false);
            Check("zip 缺清单声明的 inner · 拒绝",
                "ok=false（包内无 ServerUI-有依赖版.exe）",
                "ok=" + result.Ok + " err=" + Trim(result.Error),
                !result.Ok);
        }

        // ---------------- 7. zip-slip
        {
            var env = await Scenario.BuildAsync(_root, "zip-slip", TargetVer,
                m => m, extraEntry: "../evil.exe");
            var (plan, result) = await env.RunAsync(CurVer, false, false);
            Check("zip 含 ../evil.exe · 拒绝（zip-slip）",
                "ok=false 且未写出上级目录文件",
                "ok=" + result.Ok + " escaped=" + env.EscapedFileExists(),
                !result.Ok && !env.EscapedFileExists());
        }

        // ---------------- 8. inner 是 HTML
        {
            var env = await Scenario.BuildAsync(_root, "html-inner", TargetVer,
                m => m, innerIsHtml: true);
            var (plan, result) = await env.RunAsync(CurVer, false, false);
            Check("inner 是 HTML/非 PE · 拒绝",
                "ok=false（PE 校验失败）",
                "ok=" + result.Ok + " err=" + Trim(result.Error),
                !result.Ok);
        }

        // ---------------- 9. inner 版本与清单不符
        {
            // 清单声明 9.9，但包内 exe 是 2.20 → Major.Minor 不符，必须拒绝
            var env = await Scenario.BuildAsync(_root, "inner-ver", "9.9", m => m);
            var (plan, result) = await env.RunAsync(CurVer, false, false);
            Check("inner 版本(2.20) 与清单(9.9) 不符 · 拒绝",
                "ok=false（Major.Minor 不匹配）",
                "ok=" + result.Ok + " err=" + Trim(result.Error),
                !result.Ok);
        }

        // ---------------- 10. 降级
        {
            var env = await Scenario.BuildAsync(_root, "downgrade", "2.19", v => v);
            var (plan, result) = await env.RunAsync(CurVer, false, false);
            Check("降级（2.19 < 2.20）· 拒绝",
                "plan.Ok=false 或 upToDate=true，且版本停在 2.20",
                "ok=" + plan.Ok + " upToDate=" + plan.UpToDate + " ver=" + env.TargetVersionNow(),
                (!plan.Ok || plan.UpToDate) && env.TargetVersionNow().StartsWith(CurVer));
        }

        // ---------------- 11. 同版本非 force
        {
            var env = await Scenario.BuildAsync(_root, "same-no-force", CurVer, v => v);
            var (plan, result) = await env.RunAsync(CurVer, false, false);
            Check("同版本非 force · 提示已最新且不下载",
                "upToDate=true 且未请求资产",
                "upToDate=" + plan.UpToDate + " assetRequests=" + env.AssetRequestCount(),
                plan.UpToDate && env.AssetRequestCount() == 0);
        }

        // ---------------- 12. 同版本 force
        {
            var env = await Scenario.BuildAsync(_root, "same-force", CurVer, v => v);
            var (plan, result) = await env.RunAsync(CurVer, force: true, forcePackage: false);
            Check("同版本 force · 允许重装",
                "plan.Target=2.20 且 result.ok=true",
                "target=" + plan.TargetVersion + " ok=" + result.Ok + " err=" + Trim(result.Error),
                plan.Ok && plan.TargetVersion == CurVer && result.Ok);
        }

        // ---------------- 13. 清单缺字段 / 非法 sha
        {
            var env = await Scenario.BuildAsync(_root, "bad-manifest", TargetVer,
                m => "{ \"schema\": 1, \"channel\": \"stable\", \"version\": \"" + TargetVer + "\", \"assets\": [ { \"kind\": \"fd\" } ] }");
            var (plan, result) = await env.RunAsync(CurVer, false, false);
            Check("清单缺字段 / 非法 sha256 · 整份拒绝",
                "plan.Ok=false",
                "ok=" + plan.Ok + " err=" + Trim(plan.Error),
                !plan.Ok);
        }

        // ---------------- 14. 源码缺 ServerUI.Web.csproj → 转发行包
        {
            var env = await Scenario.BuildAsync(_root, "src-wrong", TargetVer, v => v, sourceLayout: SourceLayout.Legacy);
            var (plan, result) = await env.RunAsync(CurVer, false, false, envReady: true);
            Check("源码缺 ServerUI.Web.csproj · 判定不可编译 → 发行包",
                "strategy=package 且原因含“源码”",
                "strategy=" + plan.Strategy + " reason=" + Trim(plan.Reason),
                plan.Ok && plan.Strategy == "package"
                    && (plan.Reason ?? "").Contains("源码", StringComparison.Ordinal));
        }

        // ---------------- 15. 有构建环境 → 源码编译
        {
            var env = await Scenario.BuildAsync(_root, "src-ok", TargetVer, v => v, sourceLayout: SourceLayout.WebUi);
            var (plan, result) = await env.RunAsync(CurVer, false, false, envReady: true);
            // 注意：ExecuteAsync 在"编译失败→回退包"时会就地改写 plan.Strategy，
            // 所以决策必须用 RunAsync 里记录的 planAtDecision（执行前的快照）。
            // 只看决策：本用例注入的编译器必定失败，执行阶段会按设计回退到发行包（会下载），
            // 所以这里不能把 assetRequests 作为"决策是否走编译"的判据。
            Check("有构建环境 + 源码结构正确 · 选择源码编译",
                "决策阶段 strategy=compile",
                "planStrategy=" + env.PlanStrategyAtDecision,
                env.PlanStrategyAtDecision == "compile");
            Check("编译失败自动回退发行包",
                "结果 strategy=package 且最终 ok=true",
                "ok=" + result.Ok + " strategy=" + result.Strategy + " err=" + Trim(result.Error),
                result.Ok && result.Strategy == "package");
        }
    }

    // ================================================================
    // 断言与工具
    // ================================================================

    private static void Check(string name, string expect, string actual, bool ok)
    {
        if (ok) { _pass++; Console.WriteLine("[PASS] " + name); }
        else { _fail++; Console.WriteLine("[FAIL] " + name); }
        Console.WriteLine("       预期: " + expect);
        Console.WriteLine("       实际: " + actual);
        Report.AppendLine((ok ? "[PASS] " : "[FAIL] ") + name);
        Report.AppendLine("  预期: " + expect);
        Report.AppendLine("  实际: " + actual);
    }

    private static string Trim(string s)
    {
        if (string.IsNullOrEmpty(s)) return "(空)";
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length > 200 ? s.Substring(0, 200) + "…" : s;
    }

    private static string RemoveAssetKind(string manifest, string kind)
    {
        // 简易裁剪：删掉 kind=fd 的那个对象（测试清单结构固定，够用且有明确断言兜底）
        var idx = manifest.IndexOf("\"kind\": \"" + kind + "\"", StringComparison.Ordinal);
        if (idx < 0) return manifest;
        var start = manifest.LastIndexOf('{', idx);
        var end = manifest.IndexOf('}', idx);
        if (start < 0 || end < 0) return manifest;
        // 连同后面的逗号和空白
        var tail = end + 1;
        while (tail < manifest.Length && (manifest[tail] == ',' || char.IsWhiteSpace(manifest[tail]))) tail++;
        return manifest.Remove(start, tail - start);
    }

    private enum SourceLayout { WebUi, Legacy }

    /// <summary>一个完整沙箱：假 exe + 假 Release 资产 + 本地静态服务。</summary>
    private sealed class Scenario
    {
        public const string ZipSizePlaceholder = "@@SIZE@@";

        private string _dir;
        private string _targetExe;
        private LocalServer _server;
        private AumStaticSource _source;
        private List<string> _assetRequests = new List<string>();

        public static async Task<Scenario> BuildAsync(
            string root, string name, string version, Func<string, string> manifestEdit,
            string tagOverride = null, string zipInner = null, string extraEntry = null,
            bool innerIsHtml = false, string manifestInner = null,
            SourceLayout sourceLayout = SourceLayout.WebUi, string manifestOverride = null)
        {
            var s = new Scenario();
            s._dir = Path.Combine(root, name);
            Directory.CreateDirectory(s._dir);

            var serve = Path.Combine(s._dir, "serve");
            Directory.CreateDirectory(serve);

            // 1) 目标 exe 用**真实构建产物**（带 FileVersion 资源），不做伪造 PE：
            //    这样 PE 校验、FileVersion 读取都走真实代码路径。
            s._targetExe = Path.Combine(s._dir, "ServerUI-有依赖版.exe");
            File.Copy(FixtureExe, s._targetExe, true);

            // 2) 发行包 zip：inner 为假 PE（版本可控）
            var innerName = zipInner ?? "ServerUI-有依赖版.exe";
            var zipPath = Path.Combine(serve, "ServerUI-fd.zip");
            var innerBytes = innerIsHtml
                ? Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body>login</body></html>")
                : FixtureExeBytes;
            ZipBuilder.Build(zipPath, new[] { (innerName, innerBytes) }, extraEntry);

            var zipBytes = File.ReadAllBytes(zipPath);
            var sha = Convert.ToHexString(SHA256.HashData(zipBytes)).ToLowerInvariant();

            // 3) 清单
            var manifest = "{\n" +
                "  \"schema\": 1,\n" +
                "  \"channel\": \"stable\",\n" +
                "  \"version\": \"" + version + "\",\n" +
                "  \"assets\": [\n" +
                "    { \"kind\": \"fd\", \"name\": \"ServerUI-fd.zip\", \"sha256\": \"" + sha + "\", " +
                     "\"size\": " + zipBytes.Length + ", \"inner\": \"" + (manifestInner ?? innerName) + "\" }\n" +
                "  ],\n" +
                "  \"minSupportedVersion\": \"2.0\",\n" +
                "  \"notesUrl\": \"https://example.invalid/notes\"\n" +
                "}";
            manifest = manifestEdit(manifest);
            File.WriteAllText(Path.Combine(serve, "latest.json"), manifest, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(serve, "AUM-version.txt"), version, new UTF8Encoding(false));

            // 4) 源码 zip（可选路径）
            var srcZip = Path.Combine(serve, "source.zip");
            ZipBuilder.BuildSource(srcZip, sourceLayout == SourceLayout.WebUi);

            // 5) 本地静态服务（只监听 127.0.0.1）
            s._server = await LocalServer.StartAsync(serve);
            s._source = new AumStaticSource(new AumStaticSource.Config
            {
                BaseUrl = s._server.BaseUrl,
                TagOverride = tagOverride ?? version,
                ManifestOverride = manifestOverride
            });

            // 包大小占位替换（供 size 不符用例）
            var sizeBad = manifest.Contains(ZipSizePlaceholder);
            if (sizeBad)
            {
                var fixedManifest = manifest.Replace(ZipSizePlaceholder, zipBytes.Length.ToString());
                File.WriteAllText(Path.Combine(serve, "latest.json"), fixedManifest, new UTF8Encoding(false));
            }

            s._startUtc = DateTime.UtcNow.AddMinutes(-1);
            s._version = version;
            s._envReady = true;
            Console.WriteLine("  [scenario] " + name + " served at " + s._server.BaseUrl);
            return s;
        }

        private DateTime _startUtc;
        private string _version;
        private bool _envReady;

        public string BaseUrl => _server.BaseUrl;
        public string PlanStrategyAtDecision;
        public int AssetRequestCount() => _source.Requests.Count(r => r.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            && r.IndexOf("source.zip", StringComparison.OrdinalIgnoreCase) < 0);

        public string TargetVersionNow()
        {
            try { return FileVersionInfo.GetVersionInfo(_targetExe).FileVersion ?? "(null)"; }
            catch { return "(read-failed)"; }
        }

        public bool TargetReplaced()
        {
            try { return File.GetLastWriteTimeUtc(_targetExe) > _startUtc.AddSeconds(-1); }
            catch { return false; }
        }

        public List<string> NewFiles() => Directory.GetFiles(_dir, "*.new").ToList();
        public List<string> BakFiles() => Directory.GetFiles(_dir, "*.bak-*").ToList();
        public bool EscapedFileExists() => File.Exists(Path.Combine(Path.GetDirectoryName(_dir), "evil.exe"));

        public async Task<(AumPlan Plan, AumUpdateResult Result)> RunAsync(
            string currentVersion, bool force, bool forcePackage, bool envReady = false)
        {
            var opt = new AumUpdateOptions
            {
                CurrentVersion = currentVersion,
                ExePath = _targetExe,
                Force = force,
                ForcePackage = forcePackage,
                TempRoot = Path.Combine(_dir, "tmp"),
                Sandbox = true,
                SandboxSkipRestart = true,
                EnvProbeOverride = () => new AumBuildEnvironment
                {
                    SdkOk = envReady,
                    SdkVersion = envReady ? "10.0.100" : null,
                    DotnetPath = envReady ? "dotnet" : null,
                    PowerShellOk = envReady,
                    PowerShellVersion = envReady ? "5.1" : null,
                    DiskOk = envReady,
                    FreeBytes = envReady ? 100L * 1024 * 1024 * 1024 : 0,
                    DiskRoot = _dir,
                    SourceLayoutOk = false
                }
            };

            var ctx = new AumUpdateContext
            {
                Log = (lvl, txt) => { },
                Progress = (v, l) => { },
                Toast = (l, t) => { },
                Diag = t => { }
            };

            // 编译路径注入"必定失败"的编译器：验证「编译失败 → 回退发行包」
            var svc = new AumUpdateService(opt, ctx, _source, AumNetPolicy.ForLoopbackTests(), new FailingCompiler());
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            Console.WriteLine("  [run] plan start current=" + currentVersion);
            var plan = await svc.PlanAsync(cts.Token);
            PlanStrategyAtDecision = plan.Strategy;
            Console.WriteLine("  [run] plan done ok=" + plan.Ok + " strategy=" + plan.Strategy + " err=" + Trim(plan.Error));
            var result = await svc.ExecuteAsync(plan, cts.Token);
            Console.WriteLine("  [run] exec done ok=" + result.Ok + " err=" + Trim(result.Error));
            return (plan, result);
        }
    }

    /// <summary>编译路径用的假编译器：总是失败，用于验证回退。</summary>
    private sealed class FailingCompiler : IAumCompiler
    {
        public Task<(bool Ok, string ExePath, string Error)> CompileAsync(
            string projectDir, string outputDir, bool selfContained, Action<string> log, CancellationToken token)
        {
            log?.Invoke("[测试] 假编译器：模拟编译失败");
            return Task.FromResult((false, (string)null, "测试注入的编译失败"));
        }
    }

    /// <summary>真实构建产物（带 FileVersion 资源），作为 exe 夹具；缺失时测试直接报错而不是伪造。</summary>
    private static string _fixtureExe;
    private static byte[] _fixtureBytes;

    private static string FixtureExe
    {
        get
        {
            if (_fixtureExe != null) return _fixtureExe;
            // 优先用**单文件发布产物**：健康检查要求「单文件即可运行」，
            // bin 目录下的 apphost 依赖同目录 ServerUI.dll，直接复制会跑不起来。
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "ServerUI-有依赖版.exe"),
                Path.Combine(AppContext.BaseDirectory, "ServerUI.exe"),
                Compat.ExePath()
            };
            var self = candidates.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p)
                && new FileInfo(p).Length > 1_000_000);
            if (!string.IsNullOrEmpty(self) && File.Exists(self))
            {
                _fixtureExe = Path.Combine(_root, "fixture-ServerUI.exe");
                if (!File.Exists(_fixtureExe)) File.Copy(self, _fixtureExe, true);
                _fixtureBytes = File.ReadAllBytes(_fixtureExe);
                return _fixtureExe;
            }
            throw new InvalidOperationException("找不到可用于测试的 exe 夹具");
        }
    }

    private static byte[] FixtureExeBytes
    {
        get { var _ = FixtureExe; return _fixtureBytes; }
    }

    private static class ZipBuilder
    {
        public static void Build(string zipPath, (string Name, byte[] Data)[] entries, string extraEntry)
        {
            using var fs = File.Create(zipPath);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
            foreach (var e in entries)
            {
                var entry = zip.CreateEntry(e.Name, CompressionLevel.Optimal);
                using var os = entry.Open();
                os.Write(e.Data, 0, e.Data.Length);
            }
            if (!string.IsNullOrEmpty(extraEntry))
            {
                // 直接写原始条目名（CreateEntry 会原样保留名字，包含 ../）
                var evil = zip.CreateEntry(extraEntry, CompressionLevel.Optimal);
                using var os = evil.Open();
                var b = Encoding.ASCII.GetBytes("evil");
                os.Write(b, 0, b.Length);
            }
        }

        public static void BuildSource(string zipPath, bool webUi)
        {
            using var fs = File.Create(zipPath);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
            void Add(string name, string text)
            {
                var e = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var s = e.Open();
                var b = Encoding.UTF8.GetBytes(text);
                s.Write(b, 0, b.Length);
            }
            if (webUi)
            {
                Add("ServerUI.Web/ServerUI.Web.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
                Add("ServerUI.Web/web/index.html", "<html></html>");
            }
            else
            {
                // 只有旧 WinForms：应判定"不可编译"
                Add("ServerUI/ServerUI.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
            }
            Add("AUM-version.txt", TargetVer);
        }
    }

    /// <summary>极简静态文件服务，只监听 127.0.0.1 随机端口。</summary>
    private sealed class LocalServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly string _root;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        public string BaseUrl { get; }

        private LocalServer(HttpListener l, string root, string baseUrl)
        {
            _listener = l; _root = root; BaseUrl = baseUrl;
        }

        public static Task<LocalServer> StartAsync(string root)
        {
            // 找一个空闲端口（避免 80/8080 冲突）
            var port = FreePort();
            var prefix = "http://127.0.0.1:" + port + "/";
            var l = new HttpListener();
            l.Prefixes.Add(prefix);
            l.Start();
            var srv = new LocalServer(l, root, prefix.TrimEnd('/'));
            _ = Task.Run(srv.LoopAsync);
            return Task.FromResult(srv);
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch { return; }

                try
                {
                    var rel = Uri.UnescapeDataString(ctx.Request.Url.AbsolutePath.TrimStart('/'));
                    var file = Path.GetFullPath(Path.Combine(_root, rel));
                    if (!file.StartsWith(_root, StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
                    {
                        ctx.Response.StatusCode = 404;
                        ctx.Response.Close();
                        continue;
                    }
                    var bytes = File.ReadAllBytes(file);
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = file.EndsWith(".json") ? "application/json" : "application/octet-stream";
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                }
                catch { try { ctx.Response.Abort(); } catch { } }
                finally { try { ctx.Response.Close(); } catch { } }
            }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }
    }
}
