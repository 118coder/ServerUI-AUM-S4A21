using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ServerUI.Web.Bridge.Aum;

/// <summary>AUM 更新运行参数（生产路径由 AppController 填充）。</summary>
internal sealed class AumUpdateOptions
{
    public string CurrentVersion;
    public string ExePath;
    public bool Force;
    public bool ForcePackage;

    /// <summary>临时目录根；null → %TEMP%\ServerUI-aum-update。</summary>
    public string TempRoot;

    /// <summary>
    /// 沙箱模式（--updatetest）：不启动新进程、不退出管理器、允许指定任意目标 exe。
    /// 生产路径永远为 false。
    /// </summary>
    public bool Sandbox;

    /// <summary>沙箱下跳过的替换后重启（仍生成脚本/结果）。</summary>
    public bool SandboxSkipRestart = true;

    /// <summary>测试注入：替换真实的 SDK/PowerShell/磁盘探测（生产为 null）。</summary>
    public Func<AumBuildEnvironment> EnvProbeOverride;
}

internal sealed class AumPlan
{
    public bool Ok;
    public string Error;
    public bool UpToDate;
    public string CurrentVersion;
    public string TargetVersion;
    public string Strategy;              // "compile" | "package"
    public string StrategyLabel;         // 中文标签
    public string Reason;
    public AumBuildEnvironment Env;
    public AumManifest Manifest;
    public AumAsset Asset;
    public AumReleaseCandidate Release;
    public long PackageBytes;
    public string NotesUrl;
    public bool Force;
    public string SourceZipPath;
    public string SourceProjectDir;
    public string SourceRoot;
    public string TempDir;              // 规划阶段创建的临时目录（源码探测）；执行结束后删除

    public object ToJson() => new
    {
        ok = Ok,
        error = Error,
        upToDate = UpToDate,
        current = CurrentVersion,
        target = TargetVersion,
        strategy = Strategy,
        strategyLabel = StrategyLabel,
        reason = Reason,
        packageBytes = PackageBytes,
        notesUrl = NotesUrl,
        force = Force,
        env = Env?.ToJson(),
        asset = Asset == null ? null : new { Asset.Kind, Asset.Name, Asset.Inner, Asset.Size, Asset.Sha256 }
    };
}

internal sealed class AumUpdateResult
{
    public bool Ok;
    public bool Cancelled;
    public bool Deferred;                // 走了 PowerShell 兜底脚本，替换由脚本完成
    public bool UpToDate;
    public string Error;
    public string CurrentVersion;
    public string TargetVersion;
    public string Strategy;
    public string StrategyReason;
    public long PackageBytes;
    public string BackupPath;
    public string NewExePath;
    public string RestartExe;
    public string ResultFile;
    public string TempDir;
    public string Log;
    public string PlanError;

    public object ToJson() => new
    {
        ok = Ok,
        cancelled = Cancelled,
        deferred = Deferred,
        upToDate = UpToDate,
        error = Error,
        current = CurrentVersion,
        target = TargetVersion,
        strategy = Strategy,
        strategyReason = StrategyReason,
        packageBytes = PackageBytes,
        packageSize = PackageBytes > 0 ? AumNet.FormatSize(PackageBytes) : null,
        backup = BackupPath,
        newExe = NewExePath,
        restartExe = RestartExe,
        resultFile = ResultFile,
        tempDir = TempDir,
        log = Log
    };
}

/// <summary>更新进度/日志回调（AppController 注入；测试注入收集器）。</summary>
internal sealed class AumUpdateContext
{
    public Action<string, string> Log;                 // (level, text)
    public Action<double, string> Progress;             // (0..100, label)
    public Action<string, string> Toast;                // (level, text)
    public Action<string> Diag;
}

/// <summary>编译路径抽象（生产为 dotnet publish，测试可替换为固定产物）。</summary>
internal interface IAumCompiler
{
    Task<(bool Ok, string ExePath, string Error)> CompileAsync(
        string projectDir, string outputDir, bool selfContained,
        Action<string> log, CancellationToken token);
}

internal sealed class AumDotnetCompiler : IAumCompiler
{
    public async Task<(bool Ok, string ExePath, string Error)> CompileAsync(
        string projectDir, string outputDir, bool selfContained,
        Action<string> log, CancellationToken token)
    {
        var csproj = Path.Combine(projectDir, "ServerUI.Web.csproj");
        if (!File.Exists(csproj)) return (false, null, "找不到 ServerUI.Web.csproj");

        Directory.CreateDirectory(outputDir);
        var args = new StringBuilder();
        args.Append("publish \"").Append(csproj).Append("\" -c Release -r win-x64 ");
        args.Append(selfContained ? "--self-contained true " : "--self-contained false ");
        args.Append("-p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ");
        if (selfContained) args.Append("-p:EnableCompressionInSingleFile=true ");
        args.Append("-o \"").Append(outputDir).Append("\"");

        var exit = await RunAsync("dotnet", args.ToString(), projectDir, log, token).ConfigureAwait(false);
        if (exit != 0) return (false, null, "dotnet publish 退出码 " + exit);

        var exe = Path.Combine(outputDir, "ServerUI.exe");
        if (!File.Exists(exe)) return (false, null, "编译产物缺失：ServerUI.exe");
        return (true, exe, null);
    }

    private static Task<int> RunAsync(string file, string args, string cwd, Action<string> log, CancellationToken token)
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                WorkingDirectory = cwd,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.Environment["DOTNET_EnableCET"] = "0";
            psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            psi.Environment["DOTNET_NOLOGO"] = "1";

            var p = new Process { StartInfo = psi };
            p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log?.Invoke("[编译] " + e.Data); };
            p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log?.Invoke("[编译] " + e.Data); };
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) =>
            {
                try { tcs.TrySetResult(p.ExitCode); } catch { tcs.TrySetResult(-1); }
            };
            _ = token.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } });

            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            tcs.TrySetResult(-1);
            log?.Invoke("[编译] 启动失败：" + ex.Message);
        }
        return tcs.Task;
    }
}

/// <summary>
/// AUM 自更新主体：判定 → 下载 → 校验 → 解压 → 健康检查 → 原子替换 → 重启。
/// 任何失败都不碰用户现有 exe；只有全部校验通过后才替换，且始终保留一份 .bak。
/// </summary>
internal sealed class AumUpdateService
{
    private const long MaxAssetBytes = AumManifest.MaxAssetBytes;
    private const int HealthCheckTimeoutMs = 15000;
    private const string ResultFileName = "AUM更新结果.txt";

    private readonly AumUpdateOptions _opt;
    private readonly AumUpdateContext _ctx;
    private readonly IAumReleaseSource _source;
    private readonly AumNetPolicy _policy;
    private readonly IAumCompiler _compiler;
    private readonly HttpClient _http;

    private readonly StringBuilder _logBuf = new StringBuilder();

    public AumUpdateService(
        AumUpdateOptions opt,
        AumUpdateContext ctx,
        IAumReleaseSource source,
        AumNetPolicy policy,
        IAumCompiler compiler = null,
        HttpClient http = null)
    {
        _opt = opt;
        _ctx = ctx ?? new AumUpdateContext();
        _source = source;
        _policy = policy ?? AumNetPolicy.Strict;
        _compiler = compiler ?? new AumDotnetCompiler();
        _http = http ?? AumNet.CreateClient();
    }

    public bool IsSelfContained
    {
        get
        {
            try { return !string.IsNullOrEmpty(_opt.ExePath) && File.Exists(_opt.ExePath) && new FileInfo(_opt.ExePath).Length > 50_000_000; }
            catch { return false; }
        }
    }

    private void Log(string text, string level = null)
    {
        _logBuf.AppendLine(text);
        _ctx.Log?.Invoke(level ?? "default", text);
    }

    private void Stage(double value, string label)
    {
        _ctx.Progress?.Invoke(value, label);
        _logBuf.AppendLine("[阶段] " + label);
    }

    private string NewTempDir()
    {
        var root = string.IsNullOrWhiteSpace(_opt.TempRoot)
            ? Path.Combine(Path.GetTempPath(), "ServerUI-aum-update")
            : _opt.TempRoot;
        var dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ================================================================
    // 规划：选版本 + 严格校验清单 + 决定策略
    // ================================================================

    public async Task<AumPlan> PlanAsync(CancellationToken token)
    {
        var plan = new AumPlan
        {
            CurrentVersion = _opt.CurrentVersion,
            Force = _opt.Force
        };

        Stage(2, "检测版本 · 正在查询 GitHub Release");
        var (releases, listError) = await _source.ListAsync(_http, _policy, token, t => Log(t)).ConfigureAwait(false);

        if (releases == null || releases.Count == 0)
        {
            plan.Error = "没有可用的 Release：" + (listError ?? "仓库尚未发布任何版本") +
                         "\n提示：先在仓库打 tag 并运行发布流水线（tools/make-release.ps1 + .github/workflows/release.yml）。";
            return plan;
        }

        Log("[AUM更新] 发现 " + releases.Count + " 个候选 Release，最新 v" + releases[0].Version);

        var target = releases.FirstOrDefault(r => AumVersion.Compare(r.Version, _opt.CurrentVersion) > 0);
        if (target == null)
        {
            if (!_opt.Force)
            {
                plan.Ok = true;
                plan.UpToDate = true;
                plan.TargetVersion = _opt.CurrentVersion;
                plan.Reason = "仓库最新 v" + releases[0].Version + " 不高于当前 v" + _opt.CurrentVersion;
                Log("[AUM更新] 已是最新版本 v" + _opt.CurrentVersion + "（仓库 v" + releases[0].Version + "）", "secondary");
                Stage(100, "已是最新版本 v" + _opt.CurrentVersion);
                return plan;
            }

            // force：允许同版本重装，但绝不允许降级
            target = releases.FirstOrDefault(r => AumVersion.Compare(r.Version, _opt.CurrentVersion) == 0);
            if (target == null)
            {
                if (Environment.GetEnvironmentVariable("AUM_ALLOW_AUM_DOWNGRADE") == "1")
                {
                    target = releases[0];
                    Log("[AUM更新] 已设置 AUM_ALLOW_AUM_DOWNGRADE=1，允许降级到 v" + target.Version, "warning");
                }
                else
                {
                    plan.Error = "拒绝版本降级：仓库最高 v" + releases[0].Version + " 低于当前 v" + _opt.CurrentVersion +
                                 "（如确需降级，仅测试场景可设置 AUM_ALLOW_AUM_DOWNGRADE=1）";
                    Log("[AUM更新] " + plan.Error, "error");
                    return plan;
                }
            }
        }

        plan.TargetVersion = target.Version;
        plan.Release = target;
        plan.NotesUrl = target.ReleasePageUrl;

        // 每个候选都要过 AUM-version.txt 一致性校验；不合格的 Release 跳过并记录。
        AumManifest manifest = null;
        AumReleaseCandidate chosen = null;
        var rejected = new List<string>();

        foreach (var candidate in releases)
        {
            if (AumVersion.Compare(candidate.Version, _opt.CurrentVersion) < 0 &&
                Environment.GetEnvironmentVariable("AUM_ALLOW_AUM_DOWNGRADE") != "1")
            {
                rejected.Add("v" + candidate.Version + "：低于当前版本，跳过");
                continue;
            }
            if (!_opt.Force && AumVersion.Compare(candidate.Version, _opt.CurrentVersion) <= 0) continue;

            Stage(8, "下载清单 · v" + candidate.Version);
            var (vfOk, vfText, vfErr) = await _source.GetVersionFileAsync(_http, _policy, candidate, token).ConfigureAwait(false);
            if (!vfOk)
            {
                rejected.Add("v" + candidate.Version + "：无法读取 AUM-version.txt（" + vfErr + "），视为不可信");
                Log("[AUM更新] 跳过 v" + candidate.Version + "：" + vfErr, "warning");
                continue;
            }
            var vfVer = AumVersion.Normalize(vfText);
            if (!AumVersion.IsValid(vfVer) || AumVersion.Compare(vfVer, candidate.Version) != 0)
            {
                rejected.Add("v" + candidate.Version + "：AUM-version.txt=" + vfVer + " 与 tag 不符，视为不可信");
                Log("[AUM更新] 跳过 v" + candidate.Version + "：AUM-version.txt=" + vfVer + " 与 tag 不一致", "warning");
                continue;
            }

            var (mfOk, mfText, mfErr) = await _source.GetManifestTextAsync(_http, _policy, candidate, token).ConfigureAwait(false);
            if (!mfOk)
            {
                rejected.Add("v" + candidate.Version + "：清单获取失败（" + mfErr + "）");
                Log("[AUM更新] 跳过 v" + candidate.Version + "：清单获取失败 " + mfErr, "warning");
                continue;
            }

            var parsed = AumManifest.ParseStrict(mfText, candidate.Version, IsSelfContained, out var mfParseError);
            if (parsed == null)
            {
                rejected.Add("v" + candidate.Version + "：清单校验失败（" + mfParseError + "）");
                Log("[AUM更新] 跳过 v" + candidate.Version + "：清单校验失败 " + mfParseError, "warning");
                continue;
            }

            manifest = parsed;
            chosen = candidate;
            break;
        }

        if (manifest == null)
        {
            plan.Error = "没有可用的 Release。\n" + string.Join("\n", rejected.Take(5));
            return plan;
        }

        plan.Release = chosen;
        plan.TargetVersion = manifest.Version;
        plan.Manifest = manifest;
        plan.NotesUrl = manifest.NotesUrl ?? chosen.ReleasePageUrl;

        // 任何路径都禁止降级（除测试开关）
        if (AumVersion.Compare(manifest.Version, _opt.CurrentVersion) < 0 &&
            Environment.GetEnvironmentVariable("AUM_ALLOW_AUM_DOWNGRADE") != "1")
        {
            plan.Error = "拒绝版本降级：目标 v" + manifest.Version + " 低于当前 v" + _opt.CurrentVersion;
            Log("[AUM更新] " + plan.Error, "error");
            return plan;
        }

        if (!string.IsNullOrEmpty(manifest.MinSupportedVersion) &&
            AumVersion.Compare(manifest.MinSupportedVersion, _opt.CurrentVersion) > 0)
        {
            plan.Error = "当前版本 v" + _opt.CurrentVersion + " 低于该发行包要求的最低版本 v" +
                         manifest.MinSupportedVersion + "，请手动下载完整包升级。";
            Log("[AUM更新] " + plan.Error, "error");
            return plan;
        }

        var asset = manifest.Find(AumManifest.KindForShape(IsSelfContained));
        plan.Asset = asset;
        plan.PackageBytes = asset?.Size ?? 0;

        // ---------------- 策略判定
        if (_opt.ForcePackage)
        {
            plan.Ok = true;
            plan.Strategy = "package";
            plan.StrategyLabel = "发行包替换";
            plan.Reason = "已在设置页强制使用发行包（forcePackage=true），跳过源码编译";
            Stage(100, "采用发行包替换（已强制）");
            return plan;
        }

        Stage(10, "检测版本 · 正在探测构建环境");
        var env = _opt.EnvProbeOverride != null ? _opt.EnvProbeOverride() : AumEnvironmentProbe.Probe(_opt.ExePath);
        plan.Env = env;

        if (!(env.SdkOk && env.PowerShellOk && env.DiskOk))
        {
            plan.Ok = true;
            plan.Strategy = "package";
            plan.StrategyLabel = "发行包替换";
            plan.Reason = "构建环境不完整（缺少 " + env.Missing() + "）";
            Stage(100, "采用发行包替换（" + env.Missing() + "）");
            return plan;
        }

        // 有环境还不够：必须下载源码包并确认是 ServerUI.Web 工程（防旧 WinForms 代码回退）
        var sourceDir = NewTempDir();
        plan.TempDir = sourceDir;
        Directory.CreateDirectory(sourceDir);
        var sourceZip = Path.Combine(sourceDir, "source.zip");
        Stage(14, "下载源码 · 校验工程结构");

        var src = await _source.DownloadSourceAsync(_http, _policy, chosen, sourceZip,
            m => Log(m), (done, total) => ReportDownload("源码", done, total), token).ConfigureAwait(false);
        if (!src.Ok)
        {
            plan.Ok = true;
            plan.Strategy = "package";
            plan.StrategyLabel = "发行包替换";
            plan.Reason = "源码包下载失败（" + src.Error + "），改用发行包";
            Stage(100, "采用发行包替换（源码下载失败）");
            return plan;
        }

        var extract = Path.Combine(sourceDir, "extract");
        try
        {
            Directory.CreateDirectory(extract);
            ZipFile.ExtractToDirectory(sourceZip, extract);
        }
        catch (Exception ex)
        {
            plan.Ok = true;
            plan.Strategy = "package";
            plan.StrategyLabel = "发行包替换";
            plan.Reason = "源码包解压失败（" + ex.Message + "），改用发行包";
            Stage(100, "采用发行包替换（源码包损坏）");
            return plan;
        }

        if (!AumEnvironmentProbe.CheckSourceLayout(extract, out var projectDir, out var layoutError))
        {
            env.SourceLayoutOk = false;
            env.SourceError = layoutError;
            plan.Ok = true;
            plan.Strategy = "package";
            plan.StrategyLabel = "发行包替换";
            plan.Reason = "源码不可编译：" + layoutError;
            Log("[AUM更新] 源码不可编译：" + layoutError + " → 改用发行包", "warning");
            Stage(100, "采用发行包替换（源码结构不符）");
            return plan;
        }

        env.SourceLayoutOk = true;
        env.SourceError = null;
        plan.SourceZipPath = sourceZip;
        plan.SourceRoot = extract;
        plan.SourceProjectDir = projectDir;
        plan.Ok = true;
        plan.Strategy = "compile";
        plan.StrategyLabel = "源码编译";
        plan.Reason = "构建环境完整：" + env.Describe();
        Stage(100, "采用源码编译");
        return plan;
    }

    private void ReportDownload(string what, long done, long total)
    {
        var pct = total > 0 ? (int)(done * 100 / total) : -1;
        _logBuf.AppendLine("[下载] " + what + " " + AumNet.FormatSize(done) + (total > 0 ? " / " + AumNet.FormatSize(total) + "（" + pct + "%）" : ""));
    }

    // ================================================================
    // 执行
    // ================================================================

    public async Task<AumUpdateResult> ExecuteAsync(AumPlan plan, CancellationToken token)
    {
        var result = new AumUpdateResult
        {
            CurrentVersion = _opt.CurrentVersion,
            TargetVersion = plan.TargetVersion,
            Strategy = plan.Strategy,
            StrategyReason = plan.Reason,
            PackageBytes = plan.PackageBytes,
            UpToDate = plan.UpToDate
        };

        if (!plan.Ok)
        {
            result.Error = plan.Error;
            result.PlanError = plan.Error;
            return result;
        }
        if (plan.UpToDate)
        {
            result.Ok = true;
            result.UpToDate = true;
            return result;
        }

        var tempDir = NewTempDir();
        result.TempDir = tempDir;
        var keepTemp = false;

        try
        {
            if (plan.Strategy == "compile")
            {
                result = await ExecuteCompileAsync(plan, result, tempDir, token).ConfigureAwait(false);
            }
            else
            {
                result = await ExecutePackageAsync(plan, result, tempDir, token).ConfigureAwait(false);
            }

            keepTemp = result.Deferred;
            return result;
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
            result.Error = "已取消";
            return result;
        }
        catch (Exception ex)
        {
            result.Error = "更新异常：" + ex.Message;
            Log("[AUM更新] " + result.Error, "error");
            return result;
        }
        finally
        {
            result.Log = _logBuf.ToString();
            if (!keepTemp) TryDeleteDir(tempDir);
            if (!keepTemp && !string.IsNullOrEmpty(plan.TempDir)) TryDeleteDir(plan.TempDir);
            RestoreProgress();
        }
    }

    private void RestoreProgress()
    {
        try { _ctx.Progress?.Invoke(0, ""); } catch { }
    }

    private async Task<AumUpdateResult> ExecuteCompileAsync(
        AumPlan plan, AumUpdateResult result, string tempDir, CancellationToken token)
    {
        Stage(25, "源码编译 · 正在还原依赖并发布");
        var outDir = Path.Combine(tempDir, "publish");
        var (ok, exe, err) = await _compiler.CompileAsync(
            plan.SourceProjectDir, outDir, IsSelfContained, m => Log(m), token).ConfigureAwait(false);

        if (!ok)
        {
            Log("[AUM更新] 源码编译失败（" + err + "），自动回退发行包", "warning");
            _ctx.Toast?.Invoke("warning", "源码编译失败，已自动改用发行包");
            var packagePlan = plan;
            packagePlan.Strategy = "package";
            packagePlan.StrategyLabel = "发行包替换";
            packagePlan.Reason = "源码编译失败（" + err + "），已回退发行包";
            result.Strategy = "package";
            result.StrategyReason = packagePlan.Reason;
            var fallback = await ExecutePackageInternalAsync(packagePlan, result, tempDir, token).ConfigureAwait(false);
            if (!fallback.Ok && string.IsNullOrEmpty(fallback.Error))
                fallback.Error = "源码编译与发行包替换均失败：" + err;
            return fallback;
        }

        var pe = AumPackage.CheckPe64(exe);
        if (!pe.Ok)
        {
            Log("[AUM更新] 编译产物 PE 校验失败（" + pe.Error + "），自动回退发行包", "warning");
            var packagePlan = plan;
            packagePlan.Strategy = "package";
            packagePlan.StrategyLabel = "发行包替换";
            packagePlan.Reason = "编译产物 PE 校验失败（" + pe.Error + "），已回退发行包";
            result.Strategy = "package";
            result.StrategyReason = packagePlan.Reason;
            return await ExecutePackageInternalAsync(packagePlan, result, tempDir, token).ConfigureAwait(false);
        }

        if (!AumPackage.VersionMatches(exe, plan.TargetVersion, out var actual, out var verErr))
        {
            Log("[AUM更新] 编译产物版本不符（" + verErr + "），自动回退发行包", "warning");
            var packagePlan = plan;
            packagePlan.Strategy = "package";
            packagePlan.StrategyLabel = "发行包替换";
            packagePlan.Reason = "编译产物版本不符（" + verErr + "），已回退发行包";
            result.Strategy = "package";
            result.StrategyReason = packagePlan.Reason;
            return await ExecutePackageInternalAsync(packagePlan, result, tempDir, token).ConfigureAwait(false);
        }

        result.NewExePath = exe;
        return await InstallAsync(exe, plan, result, tempDir, token).ConfigureAwait(false);
    }

    private Task<AumUpdateResult> ExecutePackageAsync(
        AumPlan plan, AumUpdateResult result, string tempDir, CancellationToken token)
        => ExecutePackageInternalAsync(plan, result, tempDir, token);

    private async Task<AumUpdateResult> ExecutePackageInternalAsync(
        AumPlan plan, AumUpdateResult result, string tempDir, CancellationToken token)
    {
        var manifest = plan.Manifest;
        if (manifest == null)
        {
            result.Error = "发行包清单不可用";
            return result;
        }

        var asset = manifest.Find(AumManifest.KindForShape(IsSelfContained)) ?? plan.Asset;
        if (asset == null)
        {
            result.Error = "清单缺少当前形态（" + AumManifest.KindForShape(IsSelfContained) + "）的资产";
            return result;
        }
        result.PackageBytes = asset.Size;

        var pkgDir = Path.Combine(tempDir, "package");
        Directory.CreateDirectory(pkgDir);
        var zipPath = Path.Combine(pkgDir, asset.Name);

        Stage(25, "下载包 · " + asset.Name + "（" + AumNet.FormatSize(asset.Size) + "）");
        Log("[AUM更新] 下载 " + asset.Name + "（" + AumNet.FormatSize(asset.Size) + "）");

        var dl = await _source.DownloadAssetAsync(_http, _policy, plan.Release, asset, zipPath,
            m => Log(m),
            (done, total) =>
            {
                var pct = total > 0 ? Math.Min(44, 25 + (int)(done * 19 / total)) : 30;
                Stage(pct, "下载包 · " + AumNet.FormatSize(done) + (total > 0 ? " / " + AumNet.FormatSize(total) : ""));
            }, token).ConfigureAwait(false);

        if (!dl.Ok)
        {
            result.Error = "下载失败：" + dl.Error;
            Log("[AUM更新] " + result.Error, "error");
            return result;
        }

        Stage(48, "校验 · SHA256 与大小");
        if (new FileInfo(zipPath).Length != asset.Size)
        {
            result.Error = "包的字节数 " + new FileInfo(zipPath).Length + " 与清单 size=" + asset.Size + " 不符";
            return result;
        }
        var actualHash = AumNet.Sha256File(zipPath);
        if (!string.Equals(actualHash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            result.Error = "SHA256 校验失败：清单 " + asset.Sha256 + "，实际 " + actualHash;
            return result;
        }
        Log("[AUM更新] SHA256 校验通过 " + actualHash, "success");

        Stage(55, "解压 · 检查 zip 结构");
        var zipCheck = AumPackage.CheckZip(zipPath, asset.Inner);
        if (!zipCheck.Ok)
        {
            result.Error = "包校验失败：" + zipCheck.Error;
            Log("[AUM更新] " + result.Error, "error");
            return result;
        }

        var extractDir = Path.Combine(tempDir, "extract");
        if (!AumPackage.ExtractInner(zipPath, asset.Inner, extractDir, out var exePath, out var exErr))
        {
            result.Error = "解压失败：" + exErr;
            return result;
        }

        Stage(62, "校验 · PE 头与文件版本");
        var pe = AumPackage.CheckPe64(exePath);
        if (!pe.Ok)
        {
            result.Error = "包内 " + asset.Inner + " 不是有效的 x64 exe：" + pe.Error;
            Log("[AUM更新] " + result.Error, "error");
            return result;
        }

        if (!AumPackage.VersionMatches(exePath, manifest.Version, out var fv, out var verErr))
        {
            result.Error = "包内 " + asset.Inner + " 版本不符：" + verErr;
            Log("[AUM更新] " + result.Error, "error");
            return result;
        }
        Log("[AUM更新] 包内 exe FileVersion=" + fv + "，与清单 v" + manifest.Version + " 匹配", "success");

        result.NewExePath = exePath;
        return await InstallAsync(exePath, plan, result, tempDir, token).ConfigureAwait(false);
    }

    // ================================================================
    // 健康检查 + 原子替换
    // ================================================================

    private async Task<AumUpdateResult> InstallAsync(
        string verifiedExe, AumPlan plan, AumUpdateResult result, string tempDir, CancellationToken token)
    {
        var target = _opt.ExePath;
        if (string.IsNullOrWhiteSpace(target) || !File.Exists(target))
        {
            result.Error = "找不到当前运行的 exe：" + target;
            return result;
        }

        var dir = Path.GetDirectoryName(target);
        var newExe = target + ".new";

        try
        {
            Stage(66, "校验 · 准备新文件");
            File.Copy(verifiedExe, newExe, true);
            var copiedHash = AumNet.Sha256File(newExe);
            var sourceHash = AumNet.Sha256File(verifiedExe);
            if (!string.Equals(copiedHash, sourceHash, StringComparison.OrdinalIgnoreCase))
            {
                result.Error = "复制到 " + Path.GetFileName(newExe) + " 后哈希不一致，已中止";
                SafeDelete(newExe);
                return result;
            }

            Stage(70, "健康检查 · 运行新的 exe");
            var tokenFile = Path.Combine(dir, "aum-health-" + Guid.NewGuid().ToString("N") + ".json");
            var hc = await RunHealthCheckAsync(newExe, tokenFile, plan.TargetVersion, token).ConfigureAwait(false);
            SafeDelete(tokenFile);

            if (!hc.Ok)
            {
                SafeDelete(newExe);
                result.Error = "健康检查失败：" + hc.Error;
                Log("[AUM更新] " + result.Error, "error");
                return result;
            }
            Log("[AUM更新] 健康检查通过（新版本 " + hc.Version + "，退出码 0）", "success");

            var oldVer = AumVersion.Safe(_opt.CurrentVersion);
            var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            var backup = target + ".bak-" + oldVer + "-" + stamp;

            Stage(82, "替换 · 备份当前版本");
            CleanupOldBackups(target, backup);
            try { File.Copy(target, backup, true); }
            catch (Exception ex)
            {
                result.Error = "无法创建备份（" + ex.Message + "），已中止，未改动当前 exe";
                SafeDelete(newExe);
                return result;
            }
            result.BackupPath = backup;

            Stage(86, "替换 · 原子替换当前 exe");
            var replaceError = TryReplace(newExe, target, backup, out var how);
            if (replaceError != null)
            {
                // 连改名都失败：交给 PowerShell 兜底脚本（等旧进程退出后完成替换）
                Log("[AUM更新] 直接替换失败（" + replaceError + "），改用 PowerShell 兜底脚本", "warning");
                var script = WriteFallbackScript(verifiedExe, target, backup, plan.TargetVersion, oldVer, tempDir, result);
                result.Deferred = true;
                result.RestartExe = target;
                result.ResultFile = Path.Combine(Paths.DataRoot, ResultFileName);
                result.Error = null;
                result.Ok = true;
                Stage(95, "重启 · 将由后台脚本在退出后完成替换");
                Log("[AUM更新] 已生成兜底脚本：" + script, "warning");
                Log("[AUM更新] 替换结果将写入：" + result.ResultFile, "warning");
                SafeDelete(newExe);
                WritePendingBackupHint(result);
                return result;
            }

            Log("[AUM更新] 替换成功（" + how + "），备份：" + Path.GetFileName(backup), "success");

            Stage(95, "重启 · 启动新版本");
            result.RestartExe = target;
            result.Ok = true;
            result.ResultFile = Path.Combine(Paths.DataRoot, ResultFileName);
            WriteSuccessResultFile(result, target);
            return result;
        }
        catch (Exception ex)
        {
            SafeDelete(newExe);
            result.Error = "替换异常：" + ex.Message;
            Log("[AUM更新] " + result.Error, "error");
            return result;
        }
        finally
        {
            SafeDelete(newExe);
        }
    }

    private sealed class HealthResult
    {
        public bool Ok;
        public string Error;
        public string Version;
        public string ReportedExe;
    }

    private async Task<HealthResult> RunHealthCheckAsync(string exe, string tokenFile, string targetVersion, CancellationToken token)
    {
        var res = new HealthResult();
        string stagedProbe = null;
        try
        {
            if (File.Exists(tokenFile)) File.Delete(tokenFile);

            // 单文件版会依赖同目录的 ServerUI.dll（自解压布局）：
            // 若把 .new 复制到临时目录执行，会报 "application to execute does not exist"。
            // 因此健康检查必须在**目标 exe 同目录**执行（替换前该目录仍是旧版本，互不干扰）。
            var probeDir = Path.GetDirectoryName(_opt.ExePath);
            var probeExe = exe;
            if (!string.IsNullOrEmpty(probeDir))
            {
                try
                {
                    // 用唯一名：与 .new 同名会撞车（.new 可能仍被上一步句柄占用）
                    var staged = Path.Combine(probeDir,
                        "aum-probe-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
                    File.Copy(exe, staged, true);
                    probeExe = staged;
                    stagedProbe = staged;
                }
                catch (Exception ex)
                {
                    res.Error = "无法在目标目录放置健康检查副本：" + ex.Message;
                    return res;
                }
            }

            var psi = new ProcessStartInfo
            {
                FileName = probeExe,
                Arguments = "--healthcheck \"" + tokenFile + "\"",
                WorkingDirectory = string.IsNullOrEmpty(probeDir) ? Path.GetDirectoryName(exe) : probeDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            var exited = await Task.Run(() => p.WaitForExit(HealthCheckTimeoutMs), token).ConfigureAwait(false);
            if (!exited)
            {
                try { p.Kill(true); } catch { }
                res.Error = "新 exe 在 " + (HealthCheckTimeoutMs / 1000) + " 秒内没有退出";
                return res;
            }
            // 等输出读完
            await Task.Delay(60, CancellationToken.None).ConfigureAwait(false);

            if (p.ExitCode != 0)
            {
                res.Error = "新 exe 退出码 " + p.ExitCode +
                            (stderr.Length > 0 ? "（" + stderr.ToString().Trim() + "）" : "");
                return res;
            }

            if (!File.Exists(tokenFile))
            {
                res.Error = "新 exe 未写出健康检查令牌";
                return res;
            }

            string json;
            try { json = File.ReadAllText(tokenFile, Encoding.UTF8); }
            catch (Exception ex) { res.Error = "读取健康检查令牌失败：" + ex.Message; return res; }

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == System.Text.Json.JsonValueKind.True;
            var ver = root.TryGetProperty("version", out var vEl) && vEl.ValueKind == System.Text.Json.JsonValueKind.String
                ? vEl.GetString() : null;
            var exeReported = root.TryGetProperty("exe", out var eEl) && eEl.ValueKind == System.Text.Json.JsonValueKind.String
                ? eEl.GetString() : null;

            res.Version = ver;
            res.ReportedExe = exeReported;

            if (!ok) { res.Error = "令牌 ok != true"; return res; }
            if (string.IsNullOrEmpty(ver)) { res.Error = "令牌缺 version"; return res; }
            if (!AumVersion.SameMajorMinor(ver, targetVersion))
            {
                res.Error = "令牌版本 " + ver + " 与目标 " + targetVersion + " 的 Major.Minor 不一致";
                return res;
            }
            if (!string.IsNullOrEmpty(exeReported))
            {
                // 必须校验「同一目录」，但**不能**要求文件名相同：
                // 健康检查是在目标目录里用临时副本（aum-probe-*.exe）跑的。
                // 校验目的：确认新 exe 真的是从目标目录启动的（防止被 PATH/其他目录的同名文件顶替）。
                try
                {
                    var reportedDir = Path.GetDirectoryName(Path.GetFullPath(exeReported));
                    var expectDir = Path.GetDirectoryName(Path.GetFullPath(_opt.ExePath));
                    if (!string.IsNullOrEmpty(reportedDir) && !string.IsNullOrEmpty(expectDir) &&
                        !string.Equals(reportedDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), expectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    {
                        res.Error = "令牌报告的 exe 不在目标目录：" + exeReported + "（期望目录 " + expectDir + "）";
                        return res;
                    }
                }
                catch { }
            }

            res.Ok = true;
            return res;
        }
        catch (OperationCanceledException)
        {
            res.Error = "已取消";
            return res;
        }
        catch (Exception ex)
        {
            res.Error = ex.Message;
            return res;
        }
        finally
        {
            // 健康检查副本必须清理，否则目标目录会残留一个同名 exe
            if (stagedProbe != null) SafeDelete(stagedProbe);
        }
    }

    private static readonly object ReplaceLock = new object();

    /// <summary>
    /// 返回 null 表示成功；否则为错误描述。
    /// 先试 File.Replace（原子），失败则「改名旧 → 临时，再 move 新 → 目标」。
    /// </summary>
    private static string TryReplace(string newExe, string target, string backup, out string how)
    {
        how = null;
        lock (ReplaceLock)
        {
            try
            {
                File.Replace(newExe, target, null, true);
                how = "File.Replace";
                return null;
            }
            catch (Exception ex1)
            {
                var prev = target + ".prev-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                try
                {
                    File.Move(target, prev);
                }
                catch (Exception ex2)
                {
                    return "File.Replace 失败（" + ex1.Message + "）；改名也被拒绝（" + ex2.Message + "）";
                }

                try
                {
                    File.Move(newExe, target);
                    how = "改名+移动";
                    try { File.Delete(prev); } catch { }
                    return null;
                }
                catch (Exception ex3)
                {
                    // 还原：把 prev 改回 target
                    try { File.Move(prev, target); }
                    catch
                    {
                        try { File.Copy(backup, target, true); } catch { }
                    }
                    return "移动新文件失败（" + ex3.Message + "），已还原";
                }
            }
        }
    }

    private static void CleanupOldBackups(string target, string keepPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(target);
            var name = Path.GetFileName(target);
            foreach (var f in Directory.GetFiles(dir, name + ".bak-*"))
            {
                if (string.Equals(f, keepPath, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    // ================================================================
    // PowerShell 兜底脚本
    // ================================================================

    private string WriteFallbackScript(string stagedExe, string target, string backup,
        string targetVersion, string oldVer, string tempDir, AumUpdateResult result)
    {
        // 把待替换文件放到独立目录，避免与「结束后删除临时目录」互相踩
        var stageDir = Path.Combine(tempDir, "stage");
        Directory.CreateDirectory(stageDir);
        var staged = Path.Combine(stageDir, Path.GetFileName(target));
        File.Copy(stagedExe, staged, true);
        var hash = AumNet.Sha256File(staged);

        var scriptPath = Path.Combine(tempDir, "replace.ps1");
        var resultFile = Path.Combine(Paths.DataRoot, ResultFileName);

        var ps = new StringBuilder();
        ps.AppendLine("$ErrorActionPreference = 'Stop'");
        ps.AppendLine("$oldPid = " + Compat.Pid);
        ps.AppendLine("$staged = '" + PsQuote(staged) + "'");
        ps.AppendLine("$target = '" + PsQuote(target) + "'");
        ps.AppendLine("$backup = '" + PsQuote(backup) + "'");
        ps.AppendLine("$expectedHash = '" + hash + "'");
        ps.AppendLine("$newVersion = '" + PsQuote(targetVersion) + "'");
        ps.AppendLine("$oldVersion = '" + PsQuote(oldVer) + "'");
        ps.AppendLine("$resultFile = '" + PsQuote(resultFile) + "'");
        ps.AppendLine("$tempDir = '" + PsQuote(tempDir) + "'");
        ps.AppendLine("$logFile = Join-Path $tempDir 'replace.log'");
        ps.AppendLine("function Write-Result([string]$status, [string]$detail) {");
        ps.AppendLine("  try {");
        ps.AppendLine("    $lines = @(");
        ps.AppendLine("      '时间: ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'),");
        ps.AppendLine("      '状态: ' + $status,");
        ps.AppendLine("      '旧版本: ' + $oldVersion,");
        ps.AppendLine("      '新版本: ' + $newVersion,");
        ps.AppendLine("      '目标: ' + $target,");
        ps.AppendLine("      '备份: ' + $backup,");
        ps.AppendLine("      '说明: ' + $detail");
        ps.AppendLine("    )");
        ps.AppendLine("    $lines | Out-File -FilePath $resultFile -Encoding UTF8");
        ps.AppendLine("  } catch { }");
        ps.AppendLine("}");
        ps.AppendLine("function Log([string]$m) { try { ('[' + (Get-Date -Format 'HH:mm:ss') + '] ' + $m) | Out-File -FilePath $logFile -Append -Encoding UTF8 } catch { } }");
        ps.AppendLine("Log '脚本启动，等待旧进程退出…'");
        ps.AppendLine("for ($i = 0; $i -lt 60; $i++) { if (-not (Get-Process -Id $oldPid -ErrorAction SilentlyContinue)) { break }; Start-Sleep -Seconds 1 }");
        ps.AppendLine("Start-Sleep -Milliseconds 500");
        ps.AppendLine("try {");
        ps.AppendLine("  if (-not (Test-Path -LiteralPath $staged)) { throw '暂存文件不存在' }");
        ps.AppendLine("  $actual = (Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash.ToLower()");
        ps.AppendLine("  if ($actual -ne $expectedHash) { throw ('哈希校验失败: ' + $actual) }");
        ps.AppendLine("  Log '哈希校验通过'");
        ps.AppendLine("  Get-ChildItem -LiteralPath (Split-Path $target -Parent) -Filter ((Split-Path $target -Leaf) + '.bak-*') -ErrorAction SilentlyContinue |");
        ps.AppendLine("    Where-Object { $_.FullName -ne $backup } | Remove-Item -Force -ErrorAction SilentlyContinue");
        ps.AppendLine("  Copy-Item -LiteralPath $target -Destination $backup -Force");
        ps.AppendLine("  Log ('已备份到 ' + $backup)");
        ps.AppendLine("  Copy-Item -LiteralPath $staged -Destination $target -Force");
        ps.AppendLine("  Log '替换完成'");
        ps.AppendLine("  Start-Process -FilePath $target -WorkingDirectory (Split-Path $target -Parent)");
        ps.AppendLine("  Log '已启动新版本'");
        ps.AppendLine("  Write-Result '成功' '兜底脚本已完成替换并启动新版本'");
        ps.AppendLine("} catch {");
        ps.AppendLine("  Log ('失败: ' + $_)");
        ps.AppendLine("  if ((Test-Path -LiteralPath $backup) -and -not (Test-Path -LiteralPath $target)) {");
        ps.AppendLine("    try { Copy-Item -LiteralPath $backup -Destination $target -Force; Log '已还原备份' } catch { Log ('还原失败: ' + $_) }");
        ps.AppendLine("  }");
        ps.AppendLine("  Write-Result '失败' ($_.ToString())");
        ps.AppendLine("}");
        ps.AppendLine("Start-Sleep -Seconds 1");
        ps.AppendLine("Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue");

        File.WriteAllText(scriptPath, ps.ToString(), new UTF8Encoding(true));

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + scriptPath + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = tempDir
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log("[AUM更新] 启动兜底脚本失败：" + ex.Message, "error");
        }

        // 脚本需要 index：把「已生成脚本」的路径写进结果（脚本自身会清理 tempDir）
        var idx = Path.Combine(Paths.DataRoot, ResultFileName);
        try
        {
            File.WriteAllText(idx,
                "时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine +
                "状态: 等待退出后由后台脚本完成" + Environment.NewLine +
                "旧版本: " + oldVer + Environment.NewLine +
                "新版本: " + targetVersion + Environment.NewLine +
                "目标: " + target + Environment.NewLine +
                "备份: " + backup + Environment.NewLine +
                "说明: 管理器退出后由 PowerShell 完成替换；如长时间未完成，请手动将 " + staged + " 复制为 " + target + Environment.NewLine,
                new UTF8Encoding(true));
        }
        catch { }

        return scriptPath;
    }

    private static string PsQuote(string s) => (s ?? "").Replace("'", "''");

    private void WritePendingBackupHint(AumUpdateResult result)
    {
        try { Log("[AUM更新] 另一形态的 exe 未同步，如需请手动替换。", "warning"); } catch { }
    }

    private void WriteSuccessResultFile(AumUpdateResult result, string target)
    {
        try
        {
            var path = Path.Combine(Paths.DataRoot, ResultFileName);
            File.WriteAllText(path,
                "时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine +
                "状态: 成功" + Environment.NewLine +
                "旧版本: " + AumVersion.Normalize(_opt.CurrentVersion) + Environment.NewLine +
                "新版本: " + AumVersion.Normalize(result.TargetVersion) + Environment.NewLine +
                "策略: " + (result.Strategy == "compile" ? "源码编译" : "发行包替换") + Environment.NewLine +
                "目标: " + target + Environment.NewLine +
                "备份: " + (result.BackupPath ?? "(无)") + Environment.NewLine +
                "说明: 管理器将重启为新版本" + Environment.NewLine,
                new UTF8Encoding(true));
            result.ResultFile = path;
        }
        catch { }
    }

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            Directory.Delete(dir, true);
        }
        catch { }
    }
}
