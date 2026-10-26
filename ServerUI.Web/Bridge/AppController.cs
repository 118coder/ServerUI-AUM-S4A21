using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ServerUI.Services;
using ServerUI.Web.Bridge.Aum;

namespace ServerUI.Web.Bridge;

/// <summary>
/// 服务端 / 更新 / DX / 目录 / GM / PVF / SDK / 关闭清理 的宿主侧编排。
/// 行为逐条移植自旧 MainForm.Server.cs、MainForm.Pages.cs（日志文案保持一致），
/// 但所有确认/提示框改为前端主题化模态（IUi + AskAsync），不再使用系统 MessageBox。
/// </summary>
internal sealed class AppController
{
    internal const string Version = "2.22";

    // ---- 日志等级（对应旧 Lg 的 Color 语义；前端按 §6.4 配色） ----
    internal const string LDefault = "default";     // (240,240,245)
    internal const string LSuccess = "success";     // Gn
    internal const string LWarning = "warning";     // Or / Gold
    internal const string LError = "error";       // Rd
    internal const string LInfo = "info";        // CornflowerBlue / Cy
    internal const string LSecondary = "secondary";   // Txt2 / (130,130,130)

    private const int LogMaxChars = 200_000;
    private const int LogKeepChars = 150_000;
    private const string UpdateBusyText = "已有更新任务正在进行中，请等待其完成后再试。\n（更新耗时较长，期间请勿重复点击更新按钮）";

    private readonly IUi _ui;
    private readonly SettingsStore _store;

    internal readonly ServerService Server = new ServerService();
    internal readonly UpdateService Updater = new UpdateService();
    internal readonly MirrorUploadService MirrorUploader = new MirrorUploadService();
    internal readonly ArchiveService Archives = new ArchiveService();

    private readonly object _logLock = new object();
    private readonly StringBuilder _logBuilder = new StringBuilder();

    private readonly object _asksLock = new object();
    private readonly Dictionary<string, TaskCompletionSource<string>> _asks = new Dictionary<string, TaskCompletionSource<string>>();
    private int _askSeq;

    private readonly object _pollLock = new object();
    private Timer _pollTimer;
    private string _lastStatusJson;
    private bool _orphanLogged;
    private bool _startFailLogged;

    private string _sdkText = "未安装";
    private string _sdkLevel = LError;
    private bool _hasSdk;
    private volatile bool _sdkProbed;

    private bool _updateBusy;
    private Task _mirrorTask;
    private bool _mirrorOk;
    private double _progressValue;
    private int _stepTarget;

    // 事件订阅用委托（不用方法组：L 有可选参数，方法组无法转换到 Action<string>）
    private readonly Action<string> MirrorLog;
    private readonly Action<string> UpdateLog;

    /// <summary>退出宿主窗口（由 Commands 注入 Shell("quit")）。AUM 更新成功后调用。</summary>
    internal Action QuitHook { get; set; }

    public AppController(IUi ui, SettingsStore store)
    {
        _ui = ui;
        _store = store;
        MirrorLog = m => L(m);
        UpdateLog = OnUpdateOutput;
    }

    // ================================================================
    // 日志
    // ================================================================

    public void L(string message, string level = LDefault)
    {
        if (message == null) return;
        lock (_logLock)
        {
            _logBuilder.Append('[').Append(DateTime.Now.ToString("HH:mm:ss")).Append("] ").AppendLine(message);
            if (_logBuilder.Length > LogMaxChars)
                _logBuilder.Remove(0, _logBuilder.Length - LogKeepChars);
        }
        _ui.Log(level, message);
    }

    public string LogText { get { lock (_logLock) return _logBuilder.ToString(); } }

    /// <summary>旧 cbCl.Checked（清理冗余DB 开关）。</summary>
    internal bool SettingsCleanDbOn() => _store.Current.CleanRedundantDb;

    internal void SetSettingsCleanDb(bool on)
    {
        _store.Current.CleanRedundantDb = on;
        _store.Save();
    }

    // ================================================================
    // 主题化确认/提示（替换旧 MessageBox）
    // ================================================================

    public async Task<bool> ConfirmAsync(string title, string text, string level = "question",
        string yesLabel = "是", string noLabel = "否")
        => await AskAsync(title, text, level, new[] { "yes", "no" }, yesLabel, noLabel, "确定", null) == "yes";

    public async Task InfoAsync(string title, string text, string level = "info", string okLabel = "确定")
    {
        await AskAsync(title, text, level, new[] { "ok" }, null, null, okLabel, null);
    }

    /// <summary>三选一确认（是 / 否 / 取消），返回 "yes" | "no" | "cancel"。</summary>
    public Task<string> AskYesNoCancelAsync(string title, string text, string level = "question")
        => AskAsync(title, text, level, new[] { "yes", "no", "cancel" }, "是", "否", "确定", "取消");

    private Task<string> AskAsync(string title, string text, string level, string[] buttons,
        string yes, string no, string ok, string cancel)
    {
        if (!_uiIsReady()) return Task.FromResult(buttons.Length == 1 ? "ok" : "no");

        var id = "ask" + Interlocked.Increment(ref _askSeq);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_asksLock) _asks[id] = tcs;

        _ui.Push("ask", new
        {
            id,
            title,
            text,
            level,
            buttons,
            labels = new { yes, no, ok, cancel }
        });

        return WithTimeout(tcs, TimeSpan.FromMinutes(5), buttons.Length == 1 ? "ok" : "no");
    }

    private static async Task<string> WithTimeout(TaskCompletionSource<string> tcs, TimeSpan timeout, string fallback)
    {
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout)).ConfigureAwait(false);
        if (done == tcs.Task) return await tcs.Task.ConfigureAwait(false);
        return fallback;
    }

    private bool _uiIsReady() => (_ui as NativeBridge)?.IsReady ?? true;

    /// <summary>前端回话：dialog.answer {id,result:"yes"|"no"|"ok"|"cancel"}。</summary>
    public string AnswerDialog(string id, string result)
    {
        TaskCompletionSource<string> tcs = null;
        lock (_asksLock)
        {
            if (!string.IsNullOrEmpty(id) && _asks.TryGetValue(id, out tcs)) _asks.Remove(id);
        }
        tcs?.TrySetResult(string.IsNullOrEmpty(result) ? "cancel" : result);
        return "ok";
    }

    /// <summary>退出前把所有未决对话框按取消处理，避免后台任务悬挂。</summary>
    public void CancelPendingDialogs()
    {
        List<TaskCompletionSource<string>> list;
        lock (_asksLock) { list = _asks.Values.ToList(); _asks.Clear(); }
        foreach (var t in list) t.TrySetResult("cancel");
    }

    // ================================================================
    // 启动初始化（旧 Ck() 的日志部分）
    // ================================================================

    public async Task StartAsync()
    {
        L("ServerUI 版本: " + Version, LWarning);

        var exePath = Compat.ExePath();
        var isPortable = false;
        try { isPortable = !string.IsNullOrEmpty(exePath) && File.Exists(exePath) && new FileInfo(exePath).Length > 50_000_000; }
        catch { }

        if (isPortable)
            L("本版本为便携版（无依赖版），已内置 .NET 10 运行环境", LSuccess);
        else
            L("本版本为有依赖版，需要系统安装 .NET 10 运行环境才能运行", LDefault);

        var osVer = Environment.OSVersion;
        var isWin10Plus = osVer.Platform == PlatformID.Win32NT && osVer.Version.Major >= 10;
        if (!isWin10Plus)
            L("系统版本低于 Windows 10，可能会出现兼容性问题，建议升级到 Win10 或更高版本", LWarning);
        else
            L("系统版本: Windows " + (osVer.Version.Build >= 22000 ? "11" : "10") + " (Build " + osVer.Version.Build + ")", LSecondary);

        await Task.Run(() => ProbeDotNetSdk());

        CheckDnf();
        RefreshStatus(force: true);
        StartStatusPolling();

        // 旧版 Load 事件：检测 DNF → 状态 → 网络可达性 → AUM 自检。
        // 这两步只写运行日志（不弹窗、不阻塞界面），缺失会让用户看不到网络/版本提示。
        _ = Task.Run(async () =>
        {
            try { await CheckBasicNetworkAsync(); } catch { }
            try { await CheckAumVersionAsync(); } catch { }
        });
    }

    /// <summary>启动时的网络可达性检测（旧 CheckBasicNetwork，文案与分级一致）。</summary>
    private async Task CheckBasicNetworkAsync()
    {
        L(">>> 正在检测网络可达性 (网页检测, 无API调用)...", LInfo);
        try
        {
            var basic = await Updater.CheckBasicConnectivityAsync().ConfigureAwait(false);
            foreach (var kv in basic)
            {
                var name = kv.Key;
                var ms = kv.Value.LatencyMs;
                string msg; string level;
                if (!kv.Value.Reachable) { msg = name + " 不可达 (超时)"; level = LError; }
                else if (ms <= 800) { msg = name + " 正常 (延迟 " + ms + " ms)"; level = LSuccess; }
                else if (ms <= 3000) { msg = name + " 较慢 (延迟 " + ms + " ms), 建议开启科学上网"; level = LWarning; }
                else { msg = name + " 极慢 (延迟 " + ms + " ms), 更新可能失败"; level = LError; }
                L("[网络] " + msg, level);
            }
        }
        catch { L("[网络] 检测异常，不影响正常使用。", LSecondary); }
    }

    /// <summary>
    /// 启动时的 AUM 版本自检（旧 CheckAUMUpdate；只写日志，不触发更新）。
    /// 只读 Releases 列表：不复用旧的 SelfUpdateService（它读的是旧 WinForms 仓库结构）。
    /// </summary>
    private async Task CheckAumVersionAsync()
    {
        L(">>> 正在检测 AUM 管理器更新...", LInfo);
        try
        {
            using var http = AumNet.CreateClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var src = new AumGitHubSource();
            var (list, error) = await src.ListAsync(http, AumNetPolicy.Strict, cts.Token, m => L(m, LSecondary)).ConfigureAwait(false);
            if (list == null || list.Count == 0)
            {
                L("[AUM自检] " + (error ?? "仓库没有可用 Release") + "，跳过版本检测", LSecondary);
                return;
            }

            var remote = list[0].Version;
            var cmp = AumVersion.Compare(remote, Version);
            if (cmp > 0)
                L("[AUM自检] 发现新版本 v" + remote + "！当前版本 v" + Version + "，请点击【更新AUM】升级。", LSuccess);
            else if (cmp < 0)
                L("[AUM自检] 当前为开发版 v" + Version + "（高于仓库 v" + remote + "），无需更新。", LSecondary);
            else
                L("[AUM自检] 已是最新版本 v" + Version, LSecondary);
        }
        catch (Exception ex)
        {
            L("[AUM自检] 检测异常，不影响正常使用：" + ex.Message, LSecondary);
        }
    }

    public object CheckDnf()
    {
        var dnfPath = Path.Combine(Paths.Gr, "DNF.exe");
        if (!File.Exists(dnfPath))
            L("[警告] 本目录下并不存在 DNF.exe，请确认解压位置是否正确。当前目录: " + Paths.Gr, LError);
        else
            L("[检查] DNF.exe 已找到: " + dnfPath, LSuccess);
        return new { exists = File.Exists(dnfPath), path = dnfPath };
    }

    // ================================================================
    // .NET SDK 三级探测（旧 ProbeDotNetSdk）
    // ================================================================

    internal (string sdk, string level, bool sysOk, bool pfOk, bool localOk) ProbeDotNetSdk()
    {
        var sdk = "未安装";
        var level = LError;
        bool sysOk = false, pfOk = false, localOk = false;

        // 第一级: 系统 PATH 中的 dotnet
        var v1 = RunDotnetVersion("dotnet");
        if (v1 != null)
        {
            if (v1.StartsWith("10.")) { sdk = "已就绪 v" + v1; level = LSuccess; sysOk = true; }
            else L("系统已安装 .NET v" + v1 + "，但需要 ≥10.0 版本", LWarning);
        }

        // 第二级: Program Files\dotnet（x64 / x86 常见安装位置）
        if (!sysOk)
        {
            var pfPaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet", "dotnet.exe")
            };
            foreach (var pfPath in pfPaths)
            {
                if (!File.Exists(pfPath)) continue;
                var v = RunDotnetVersion(pfPath);
                if (v != null && v.StartsWith("10.")) { sdk = "已就绪 v" + v; level = LSuccess; pfOk = true; break; }
            }
        }

        // 第三级: 便携 SDK
        if (!sysOk && !pfOk)
        {
            var localPath = Path.Combine(Paths.Ad, "dotnet-sdk", "dotnet.exe");
            if (File.Exists(localPath))
            {
                var v = RunDotnetVersion(localPath);
                if (v != null && v.StartsWith("10.")) { sdk = "便携SDK v" + v; level = LSuccess; localOk = true; }
            }
        }

        _sdkText = sdk;
        _sdkLevel = level;
        _hasSdk = sysOk || pfOk || localOk;
        _sdkProbed = true;

        if (sysOk || pfOk) L("检测到系统已安装 .NET 10 SDK，可用于编译服务端更新", LSuccess);
        else if (localOk) L("检测到本地便携 .NET SDK (dotnet-sdk)，可用于编译服务端更新", LSuccess);
        else if (IsPortableExe())
        {
            L("未检测到 .NET 10 SDK，虽然本程序可运行，但更新时无法编译服务端！", LError);
            L("请将 dotnet-sdk 目录放入 AUM管理组件，或手动安装 .NET 10 SDK", LError);
        }
        else
        {
            L("未检测到 .NET 10 运行环境，本程序可能无法正常工作！", LError);
            L("请安装 .NET 10.0 或改用便携版 (ServerUI-无依赖版.exe) 后重试", LError);
        }

        return (sdk, level, sysOk, pfOk, localOk);
    }

    private static bool IsPortableExe()
    {
        try
        {
            var p = Compat.ExePath();
            return !string.IsNullOrEmpty(p) && File.Exists(p) && new FileInfo(p).Length > 50_000_000;
        }
        catch { return false; }
    }

    private static string RunDotnetVersion(string exe)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            p.Start();
            var v = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            if (p.ExitCode == 0 && !string.IsNullOrEmpty(v)) return v;
        }
        catch { }
        return null;
    }

    // ================================================================
    // 状态轮询 + 快照（旧 Rs）
    // ================================================================

    public void StartStatusPolling()
    {
        lock (_pollLock)
        {
            _pollTimer ??= new Timer(_ =>
            {
                try { RefreshStatus(force: false); } catch { }
            }, null, 2000, 2000);
        }
    }

    public void StopStatusPolling()
    {
        lock (_pollLock)
        {
            _pollTimer?.Dispose();
            _pollTimer = null;
        }
    }

    public void RefreshStatus(bool force)
    {
        bool bat = Server.IsBatRunning;
        bool dfo = ServerService.IsDfoServerRunning(Paths.DistDir);

        string stText, stLevel;
        if (bat && dfo)
        {
            stText = "● 运行中"; stLevel = LSuccess; _orphanLogged = false;
        }
        else if (!bat && dfo)
        {
            // v2.15: 不再自动清理 — bat 句柄未持有只说明不是本会话经 UI 启动的
            stText = "● 运行中(外部)"; stLevel = LWarning;
            if (!_orphanLogged)
            {
                _orphanLogged = true;
                L(">>> 检测到服务端进程正在运行 (可能为手动启动或上次会话遗留)，管理器不会自动清理。", LWarning);
                L("    如需停止请点击【停止服务端】；执行存档操作前请先停止服务端。", LWarning);
            }
        }
        else
        {
            stText = "● 未运行"; stLevel = LError; _orphanLogged = false;
        }

        // 启动后短时间内意外退出 → 可操作的排查提示（旧 v2.15 诊断）
        if (!_startFailLogged && !bat
            && Server.StartedAtUtc != null && Server.UnexpectedExitUtc != null
            && (DateTime.UtcNow - Server.StartedAtUtc.Value).TotalSeconds < 120
            && (DateTime.UtcNow - Server.UnexpectedExitUtc.Value).TotalSeconds < 120)
        {
            _startFailLogged = true;
            L(">>> [服务端] 启动后短时间内退出（退出代码 " + Server.LastExitCode + "）。常见原因与对策：", LError);
            L("    1) 杀毒软件拦截 DfoServer.exe → 将游戏目录加入杀软白名单", LError);
            L("    2) 端口被残留进程占用 → 管理器已在启动前自动清理残留，仍失败请重启电脑", LError);
            L("    3) CET 兼容问题 → 启动链路已自动注入 DOTNET_EnableCET=0，仍失败请看【疑难杂症解惑】页", LError);
            L("    4) DfoServer.exe 缺失（未完成更新）→ 先执行一次【开始更新】", LError);
        }

        // 上次更新信息（从 更新日志.txt 读取，走服务内缓存）
        var luText = LastUpdateText(out var luLevel);

        var version = "2.22";
        try { version = Updater.GetVersion(Paths.Ad); } catch { }

        var snapshot = BuildSnapshot(bat, dfo, stText, stLevel, luText, luLevel, version);

        var json = JsonSerializer.Serialize(snapshot, NativeBridge.Json);
        if (force || json != _lastStatusJson)
        {
            _lastStatusJson = json;
            _ui.Status(snapshot);
        }
    }

    /// <summary>统一状态快照（push 与 server.status 用同一结构，避免两边字段不一致）。</summary>
    private object BuildSnapshot(bool bat, bool dfo, string stText, string stLevel,
        string luText, string luLevel, string version)
    {
        return new
        {
            bat,
            dfo,
            external = !bat && dfo,
            pvf = Server.PvfExists(Paths.ServerRoot),
            version,
            sdk = _sdkProbed ? _sdkText : "检测中...",
            sdkOk = _hasSdk,
            sdkLevel = _sdkProbed ? _sdkLevel : LSecondary,
            serverState = stText,
            serverLevel = stLevel,
            startedAt = Server.StartedAtUtc?.ToString("o"),
            unexpectedExit = Server.UnexpectedExitUtc?.ToString("o"),
            lastExitCode = Server.LastExitCode,
            lastUpdate = luText,
            lastUpdateLevel = luLevel,
            admin = Paths.IsAdmin
        };
    }

    /// <summary>读取 更新日志.txt 推断上次更新版本（只读，带服务内缓存）。</summary>
    private string LastUpdateText(out string level)
    {
        level = LWarning;
        var luText = "上次更新: 尚未有log日志无法识别版本，请进行更新";
        try
        {
            var tx = UpdateServiceLogText();
            if (tx.Length > 0)
            {
                var ix = tx.LastIndexOf("版本:", StringComparison.Ordinal);
                if (ix >= 0)
                {
                    var en = tx.IndexOf('\n', ix);
                    if (en < 0) en = Math.Min(ix + 20, tx.Length);
                    luText = "上次更新: " + tx.Substring(ix, en - ix).Trim().Replace("版本:", "").Trim();
                    level = LSecondary;
                }
            }
        }
        catch { }
        return luText;
    }

    /// <summary>供 server.status 使用；不产生日志副作用。文案与 RefreshStatus 完全一致（旧 Rs()）。</summary>
    public object StatusSnapshot()
    {
        bool bat = Server.IsBatRunning;
        bool dfo = ServerService.IsDfoServerRunning(Paths.DistDir);
        string stText, stLevel;
        if (bat && dfo) { stText = "● 运行中"; stLevel = LSuccess; }
        else if (!bat && dfo) { stText = "● 运行中(外部)"; stLevel = LWarning; }
        else { stText = "● 未运行"; stLevel = LError; }

        var luText = LastUpdateText(out var luLevel);
        var version = "2.22";
        try { version = Updater.GetVersion(Paths.Ad); } catch { }

        return BuildSnapshot(bat, dfo, stText, stLevel, luText, luLevel, version);
    }

    private string UpdateServiceLogText()
    {
        // 旧代码走 _up.GetLogText(_ad)，这里保持同一入口（服务内部已有缓存）
        return Updater.GetLogText(Paths.Ad) ?? "";
    }

    /// <summary>bat 句柄或 DfoServer 进程任一在即视为运行中（旧 ServerAlive）。</summary>
    internal bool ServerAlive() => Server.IsBatRunning || ServerService.IsDfoServerRunning(Paths.DistDir);

    // ================================================================
    // 服务端启停（旧 Go / Play / 停止 / 重启）
    // ================================================================

    public async Task<object> ServerStartAsync()
    {
        L(">>> 点击了开始游戏", LInfo);

        // 服务端已在运行（外部启动 / 上次会话遗留 / 已装多开 DLL 想再开一个客户端）：
        // 不再启动第二个服务端，直接拉起游戏客户端。多开依赖的正是这条路径。
        if (ServerAlive())
        {
            var externalOnly = !Server.IsBatRunning;
            L(externalOnly
                ? ">>> 检测到服务端已在运行（外部启动），跳过启动步骤，直接启动游戏客户端"
                : ">>> 服务端已在运行，跳过启动步骤，直接启动游戏客户端", LWarning);
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000).ConfigureAwait(false);
                try { ServerService.HideDfoServerWindow(); } catch { }
            });
            var extra = LaunchGameClient();
            RefreshStatus(force: true);
            return new { started = false, reused = true, game = extra };
        }

        L(">>> 正在启动 start-server.bat...", LInfo);
        Server.Start(Paths.ServerRoot);

        _ = Task.Run(async () =>
        {
            await Task.Delay(10000).ConfigureAwait(false);
            try { Server.HideConsoleWindow(); } catch { }
        });

        var started = await Task.Run(async () =>
        {
            await Task.Delay(5000).ConfigureAwait(false);
            return Server.IsRunning;
        }).ConfigureAwait(false);

        if (started)
        {
            L(">>> 服务端进程存活，正在启动游戏...", LSuccess);
            _ = Task.Run(async () =>
            {
                await Task.Delay(15000).ConfigureAwait(false);
                try { ServerService.HideDfoServerWindow(); } catch { }
            });
        }
        else
            L(">>> 警告: 服务端可能未成功启动", LWarning);

        var launched = LaunchGameClient();
        RefreshStatus(force: true);
        return new { started, game = launched };
    }

    /// <summary>启动游戏客户端（旧 Play 后半段：本地游戏S4.bat → 单机游戏启动.bat）。</summary>
    private string LaunchGameClient()
    {
        if (File.Exists(Paths.LocalGameBat))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Paths.LocalGameBat,
                    WorkingDirectory = Path.GetDirectoryName(Paths.LocalGameBat),
                    UseShellExecute = true
                });
                L(">>> 已打开本地游戏S4.bat", LSuccess);
                return "local";
            }
            catch { }
        }

        if (File.Exists(Paths.SingleGameBat))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Paths.SingleGameBat,
                    WorkingDirectory = Paths.Ad,
                    UseShellExecute = true
                });
                L(">>> 已打开单机游戏启动.bat", LSuccess);
                return "single";
            }
            catch { }
        }

        L(">>> 本地游戏S4.bat / 单机游戏启动.bat 未找到!", LError);
        return "none";
    }

    public async Task<object> ServerStopAsync()
    {
        L(">>> 点击了停止服务端", LWarning);
        await Task.Run(() => Server.Stop()).ConfigureAwait(false);
        L(">>> 已终止服务端进程树", LWarning);
        RefreshStatus(force: true);
        return new { stopped = true };
    }

    public async Task<object> ServerRestartAsync()
    {
        L(">>> 点击了重启服务端", LInfo);
        await Task.Run(() => Server.Stop()).ConfigureAwait(false);
        await Task.Delay(1200).ConfigureAwait(false);

        Server.Start(Paths.ServerRoot);

        _ = Task.Run(async () =>
        {
            await Task.Delay(10000).ConfigureAwait(false);
            try { Server.HideConsoleWindow(); } catch { }
        });
        _ = Task.Run(async () =>
        {
            await Task.Delay(10000).ConfigureAwait(false);
            try { ServerService.HideDfoServerWindow(); } catch { }
        });

        await Task.Delay(600).ConfigureAwait(false);
        RefreshStatus(force: true);
        return new { restarted = true };
    }

    // ================================================================
    // DX 补丁 / 水印（旧 ApplyDx）
    // ================================================================

    public object DxSet(string mode, bool watermark)
    {
        var r = DxPatch.Apply(mode, watermark, Paths.Ad, Paths.Gr);

        // 日志按「先提示、后结果」的顺序原样输出（旧行为：切到非 DX9 先提示可取消）
        foreach (var m in r.Messages)
            L(m.Text, m.Level == "success" ? LSuccess
                : m.Level == "warning" ? LWarning
                : m.Level == "error" ? LError : LSecondary);

        if (!r.Ok)
        {
            // 补丁没有真正切换成功时不要落盘：否则界面上显示已选中，实际游戏目录没变
            return new { ok = false, error = r.Error, mode = _store.Current.DxMode, watermark = _store.Current.Watermark };
        }
        Persist(r.Mode, r.Watermark);
        return new { ok = true, mode = _store.Current.DxMode, watermark = _store.Current.Watermark, copied = r.Copied, removed = r.Removed };
    }

    private object Persist(string mode, bool watermark)
    {
        _store.Current.DxMode = DxPatch.NormalizeMode(mode);
        _store.Current.Watermark = _store.Current.DxMode == DxPatch.ModeVulkan ? false : watermark;
        _store.Save();
        return new { ok = true, mode = _store.Current.DxMode, watermark = _store.Current.Watermark };
    }

    // ================================================================
    // 目录 / 链接
    // ================================================================

    public object ShellOpen(string kind, string value)
    {
        switch (kind)
        {
            case "pvfDir":
                L(">>> 打开PVF目录", LInfo);
                if (Directory.Exists(Paths.PvfDir)) { OpenInExplorer(Paths.PvfDir); return new { ok = true, path = Paths.PvfDir }; }
                L("PVF目录不存在", LWarning);
                return new { ok = false, error = "PVF目录不存在" };

            case "saveDir":
                if (!Directory.Exists(Paths.DataDir)) { L("主存档目录不存在", LWarning); return new { ok = false, error = "主存档目录不存在" }; }
                OpenInExplorer(Paths.DataDir);
                L(">>> 打开了主存档目录", LInfo);
                return new { ok = true, path = Paths.DataDir };

            case "switchDir":
                if (!Directory.Exists(Paths.SwitchDir)) { L("切换库目录不存在", LWarning); return new { ok = false, error = "切换库目录不存在" }; }
                OpenInExplorer(Paths.SwitchDir);
                return new { ok = true, path = Paths.SwitchDir };

            case "backupDir":
                if (!Directory.Exists(Paths.BackupDir)) { L("备份存档目录不存在", LWarning); return new { ok = false, error = "备份存档目录不存在" }; }
                OpenInExplorer(Paths.BackupDir);
                return new { ok = true, path = Paths.BackupDir };

            case "gameDir":
                if (!Directory.Exists(Paths.Gr)) return new { ok = false, error = "游戏目录不存在" };
                OpenInExplorer(Paths.Gr);
                return new { ok = true, path = Paths.Gr };

            case "patchDir":
                if (!Directory.Exists(Paths.PatchDir)) { L("补丁目录不存在", LWarning); return new { ok = false, error = "补丁目录不存在" }; }
                OpenInExplorer(Paths.PatchDir);
                return new { ok = true, path = Paths.PatchDir };

            case "url":
                if (string.IsNullOrWhiteSpace(value)) return new { ok = false, error = "缺少 URL" };
                OpenWithShell(value);
                return new { ok = true };

            case "file":
                if (string.IsNullOrWhiteSpace(value)) return new { ok = false, error = "缺少路径" };
                if (!File.Exists(value)) { L("文件不存在: " + value, LWarning); return new { ok = false, error = "文件不存在" }; }
                OpenWithShell(value);
                return new { ok = true };

            default:
                return new { ok = false, error = "未知目录类型: " + kind };
        }
    }

    private static void OpenInExplorer(string dir)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "\"" + dir + "\"", UseShellExecute = true });
        }
        catch { }
    }

    // ================================================================
    // 进行本地编译（旧 MainForm.Pages.cs BuildAbout 的 [进行本地编译]）
    // ================================================================

    /// <summary>
    /// 启动 AUM管理组件\进行本地编译.bat（旧实现：UseShellExecute=true，工作目录 = _ad）。
    /// 只负责拉起脚本，不等待其结束、不读输出。
    /// </summary>
    public object BuildLocal()
    {
        var bat = Path.Combine(Paths.Ad, "进行本地编译.bat");
        if (!File.Exists(bat))
        {
            L("未找到 进行本地编译.bat: " + bat, LError);
            return new { ok = false, error = "未找到 进行本地编译.bat: " + bat };
        }

        try
        {
            L(">>> 正在启动本地编译脚本...", LInfo);
            Process.Start(new ProcessStartInfo
            {
                FileName = bat,
                WorkingDirectory = Paths.Ad,
                UseShellExecute = true
            });
            return new { ok = true, path = bat };
        }
        catch (Exception ex)
        {
            L(">>> 启动本地编译脚本失败: " + ex.Message, LError);
            return new { ok = false, error = ex.Message };
        }
    }

    private static void OpenWithShell(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
        catch { }
    }

    // ================================================================
    // GM 工具（旧 LaunchGmTool）
    // ================================================================

    public async Task<object> LaunchGmToolAsync()
    {
        L(">>> 点击了GM工具", LWarning);

        var gmp = Paths.GmToolExe;
        if (!File.Exists(gmp))
        {
            L("GM工具尚未编译, 请先执行一次增量/全量更新", LWarning);
            return new { ok = false, error = "GM工具尚未编译" };
        }

        var sb = Paths.DistDir;
        if (!File.Exists(Paths.InventoryDb) || !File.Exists(Paths.ServerPvf))
        {
            L("GM工具启动失败: 服务端数据目录(" + sb + ")不完整, 请先执行一次更新", LWarning);
            return new { ok = false, error = "服务端数据目录不完整" };
        }

        try
        {
            foreach (var p in Process.GetProcessesByName("DfoGmTool"))
            {
                try { p.Kill(); } catch { }
            }
        }
        catch { }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = gmp,
                Arguments = "--server-bin \"" + sb + "\"",
                WorkingDirectory = Path.GetDirectoryName(gmp),
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.Environment["DFO_GM_SERVER_BIN"] = sb;
            psi.Environment["DOTNET_EnableCET"] = "0";
            Process.Start(psi);
            L("GM工具已启动 -- 服务端目录: " + sb, LSuccess);
        }
        catch (Exception ex)
        {
            L("GM工具启动失败: " + ex.Message, LError);
            return new { ok = false, error = ex.Message };
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(3000).ConfigureAwait(false);
            try
            {
                Process.Start(new ProcessStartInfo { FileName = "http://localhost:5051", UseShellExecute = true });
            }
            catch
            {
                L("浏览器未能自动打开, 请手动访问 http://localhost:5051", LWarning);
            }
        });

        await Task.CompletedTask;
        return new { ok = true, url = "http://localhost:5051" };
    }

    // ================================================================
    // 还原原版 PVF（旧 RestoreOriginalPvf；确认框改前端模态）
    // ================================================================

    public async Task<object> RestoreOriginalPvfAsync()
    {
        var src = Paths.OriginalPvfSource;
        var dst = Paths.ClientPvf;
        var dstSvr = Paths.ServerPvf;

        if (!File.Exists(src))
        {
            L(">>> 未找到原版PVF: " + src, LError);
            await InfoAsync("还原-原版PVF",
                "未找到原版PVF文件：" + src + "\n\n请确认 实用工具包\\PVF原版\\Script.pvf 存在。",
                "warning").ConfigureAwait(false);
            return new { ok = false, error = "未找到原版PVF文件" };
        }

        long size;
        try { size = new FileInfo(src).Length; } catch { size = 0; }

        var yes = await ConfirmAsync("还原-原版PVF",
            "将把 实用工具包\\PVF原版\\Script.pvf 覆盖到：\n"
            + "  [客户端] " + dst + "\n"
            + "  [服务端] " + dstSvr + "\n\n"
            + "（客户端与服务端 Script.pvf 均将替换为原版，文件大小 " + size + " 字节）\n\n继续？",
            "question").ConfigureAwait(false);
        if (!yes) return new { ok = false, cancelled = true };

        try
        {
            File.Copy(src, dst, true);
            L(">>> 已还原原版PVF → [客户端] " + dst, LSuccess);

            var svrDir = Path.GetDirectoryName(dstSvr);
            if (!string.IsNullOrEmpty(svrDir) && Directory.Exists(svrDir))
            {
                File.Copy(src, dstSvr, true);
                L(">>> 已还原原版PVF → [服务端] " + dstSvr, LSuccess);
            }
            else
                L(">>> 服务端 PVF 目录不存在，跳过服务端还原（" + svrDir + "）", LWarning);

            const string tip = "已成功还原PVF，以防止BUG出现，可以在设置-进行本地编译，重新对服务端进行编译。";
            L(">>> " + tip, LSuccess);
            await InfoAsync("还原-原版PVF", tip, "info").ConfigureAwait(false);
            return new { ok = true };
        }
        catch (Exception ex)
        {
            L(">>> 还原原版PVF失败: " + ex.Message, LError);
            await InfoAsync("还原-原版PVF", "还原失败：" + ex.Message, "error").ConfigureAwait(false);
            return new { ok = false, error = ex.Message };
        }
    }

    // ================================================================
    // 更新（旧 CanUpdate / RI / RF / OU / OD）
    // ================================================================

    public async Task<object> UpdateStartAsync(string mode, bool skipLog, bool mirror, bool cleanDb)
    {
        var full = mode == "full";
        L(full ? ">>> 点击了全量更新" : ">>> 点击了开始更新", LInfo);

        if (_updateBusy)
        {
            L(">>> 已有更新任务正在进行中，本次请求已忽略。", LWarning);
            await InfoAsync("更新进行中", UpdateBusyText, "warning").ConfigureAwait(false);
            return new { ok = false, busy = true };
        }

        _updateBusy = true;
        try
        {
            _store.Current.SkipLog = skipLog;
            _store.Current.Mirror = mirror;
            _store.Current.CleanRedundantDb = cleanDb;
            _store.Save();

            await CheckLeftoverPs1PromptAsync().ConfigureAwait(false);
            // 旧逻辑：if (!await CanUpdate()) return; —— 前置检查不通过就不得进入更新流程。
            if (!await CanUpdateAsync().ConfigureAwait(false))
                return new { ok = false, cancelled = true };
            _ = TryMirrorUploadAsync();

            if (ServerAlive())
            {
                L(">>> 检测到服务端正在运行，正在自动停止以执行" + (full ? "全量" : "增量") + "更新...", LWarning);
                await Task.Run(() => Server.Stop()).ConfigureAwait(false);
                L(">>> 服务端已停止，开始更新", LSuccess);
            }

            if (cleanDb)
            {
                L(">>> 更新前清理冗余DB...", LSuccess);
                CleanRedundantDb();
            }

            SetProgress(0, true);
            _progressValue = 0;
            _stepTarget = 5;
            if (skipLog) L(">>> [跳过更新日志] 已启用，本次不拉取仓库提交记录", LWarning);
            L(full ? ">>> 开始全量更新 <<<" : ">>> 开始增量更新 <<<", LInfo);

            Updater.OutputReceived += OnUpdateOutput;
            Updater.Completed += OnUpdateCompleted;
            try
            {
                if (full) await Updater.RunFull(Paths.ServerRoot, Paths.Ad, skipLog, mirror).ConfigureAwait(false);
                else await Updater.RunIncremental(Paths.ServerRoot, Paths.Ad, skipLog, mirror).ConfigureAwait(false);
            }
            finally
            {
                Updater.OutputReceived -= OnUpdateOutput;
                Updater.Completed -= OnUpdateCompleted;
            }

            return new { ok = true };
        }
        catch (Exception ex)
        {
            L(">>> 更新异常: " + ex.Message, LError);
            SetProgress(0, false);
            return new { ok = false, error = ex.Message };
        }
        finally
        {
            _updateBusy = false;
            RefreshStatus(force: true);
        }
    }

    public object UpdateCancel()
    {
        try { Updater.CancelUpdate(); } catch { }
        L(">>> 已请求取消更新", LWarning);
        SetProgress(0, false);
        return new { ok = true };
    }

    private void SetProgress(double value, bool visible, string label = null)
    {
        _progressValue = value;
        _ui.Progress(value, label ?? ("更新进度: " + (int)value + "%"), visible);
    }

    /// <summary>旧 OU(string)：##PROGRESS## / [FILE:CS] / [FILE:SUM] / 日期行 / [N/5] 步骤。</summary>
    private void OnUpdateOutput(string m)
    {
        if (m == null) return;

        if (m.StartsWith("##PROGRESS##", StringComparison.Ordinal))
        {
            var val = m.Substring("##PROGRESS##".Length);
            if (int.TryParse(val, out var pct))
            {
                if (pct > _stepTarget && pct <= 97) _stepTarget = pct;
                _progressValue = Math.Max(_progressValue, pct - 5);
                SetProgress(Math.Min(_progressValue, 97), true);
            }
            return;
        }

        if (m.StartsWith("[FILE:CS]", StringComparison.Ordinal)) { L(m.Substring("[FILE:CS]".Length).TrimStart(), LSuccess); return; }
        if (m.StartsWith("[FILE:SUM]", StringComparison.Ordinal)) { L(m.Substring("[FILE:SUM]".Length).TrimStart(), LWarning); return; }

        if (Regex.IsMatch(m, @"^--- \d{4}-\d{2}-\d{2}")) L(m, LInfo);
        else L(m);

        var sm = Regex.Match(m, @"\[(\d)/5\]");
        if (sm.Success && int.TryParse(sm.Groups[1].Value, out var step))
        {
            var target = step switch { 1 => 5, 2 => 10, 3 => 30, 4 => 55, 5 => 85, _ => -1 };
            if (target > 0)
            {
                if (target > _stepTarget) _stepTarget = target;   // 只前进，防旧标记回拉
                if (_progressValue < _stepTarget - 5) _progressValue = _stepTarget - 5;
                SetProgress(Math.Min(_progressValue, 97), true);
            }
        }
    }

    /// <summary>旧 OD(bool)：100% → 结果日志 → 隐藏进度 → 刷新状态。</summary>
    private void OnUpdateCompleted(bool ok)
    {
        Thread.Sleep(1500);
        SetProgress(100, true, "100%");
        if (ok)
        {
            L(">>> 更新完成！如果更新没有效果，请尝试再次点击更新或者全量更新。<<<", LSuccess);
            L("========================================", LInfo);
            L("  更新已完成，将在目录【\\AUM管理组件】生成一份运行日志", LWarning);
            L("========================================", LInfo);
        }
        else
        {
            L(">>> 更新失败，请检查网络连接或查看上方日志。<<<", LWarning);
        }
        SetProgress(0, false);
        RefreshStatus(force: true);
    }

    /// <summary>旧 CanUpdate：仓库连通性 + 镜像源检测。</summary>
    /// <summary>
    /// 更新前置检查（旧 CanUpdate）。返回 false 表示不得继续更新。
    /// 当前实现与旧逻辑一致：仓库/镜像不可达只降级告警，不中止；保留 bool 返回供调用方短路。
    /// </summary>
    private async Task<bool> CanUpdateAsync()
    {
        L(">>> 更新前检查仓库连接...", LInfo);
        try
        {
            var status = await Updater.CheckRepositoryAsync().ConfigureAwait(false);
            if (!status.Available || status.LatencyMs > 3000)
            {
                var reason = status.Available
                    ? "连接延迟极高（" + status.LatencyMs + " ms）"
                    : "无法连接（" + status.Detail + "）";
                L("[网络降级] 仓库" + reason + "。将自动重试并改用源码包同步；建议开启科学上网（梯子）提高成功率。", LWarning);
            }
        }
        catch { }

        _mirrorTask = ValidateMirrorTokensAsync();
        L(">>> 更新前检测镜像源: Gitee / GitHub / Codeberg ...", LInfo);
        try
        {
            var mirrors = await Updater.CheckMirrorSourcesAsync().ConfigureAwait(false);
            foreach (var kv in mirrors)
            {
                var tag = kv.Value.Available ? "可访问" : "不可达";
                L("[镜像] " + kv.Key + " " + tag + " (延迟 " + kv.Value.LatencyMs + " ms)", kv.Value.Available ? LSuccess : LWarning);
            }
        }
        catch { L("[镜像] 检测异常，不影响正常使用。", LSecondary); }

        return true;
    }

    private async Task<bool> ValidateMirrorTokensAsync()
    {
        try
        {
            var ok = await MirrorUploader.ValidateTokensAsync().ConfigureAwait(false);
            _mirrorOk = ok;
            if (!ok) L("[令牌检测] API令牌无法生效，请更新AUM版本。已禁用镜像上传。", LError);
            else L("[令牌检测] API令牌正常，众包镜像可用。", LSecondary);
            return ok;
        }
        catch
        {
            _mirrorOk = false;
            L("[令牌检测] API令牌无法生效，请更新AUM版本。", LError);
            return false;
        }
    }

    private async Task TryMirrorUploadAsync()
    {
        await Task.Delay(5000).ConfigureAwait(false);
        // v2.15: 等待令牌校验完成，避免 GitHub API 慢于 5 秒时误判"令牌无法生效"
        if (_mirrorTask != null) { try { await _mirrorTask.ConfigureAwait(false); } catch { } }
        if (!_mirrorOk)
        {
            L("[镜像] API令牌无法生效，请更新AUM版本。", LError);
            return;
        }

        if (await MirrorUploader.CanReachGitGud().ConfigureAwait(false))
        {
            L("[镜像] 检测到可访问 GitGud，尝试同步镜像...", LSecondary);
            L("您已进入上传者模式，系统将通过众包镜像将最新包体分发至仓库，以缓解主源仓库的访问压力，帮助网络不佳的用户~您的等待将直接缩短他人的下载时长——愿这份互助，传递温暖。", LWarning);
            MirrorUploader.OutputReceived += MirrorLog;
            try { await MirrorUploader.RunUploaderAsync(Version, Environment.MachineName).ConfigureAwait(false); }
            catch (Exception ex) { L("[镜像] 同步异常: " + ex.Message, LWarning); }
            finally { MirrorUploader.OutputReceived -= MirrorLog; }
        }
        else
            L("[镜像] 无法访问 GitGud，跳过镜像上传。", LSecondary);
    }

    public object OpenUpdateLog()
    {
        L(">>> 查看更新日志", LInfo);
        if (File.Exists(Paths.UpdateLogFile))
        {
            OpenWithShell(Paths.UpdateLogFile);
            return new { ok = true };
        }
        _ = InfoAsync("更新日志", "暂时没有更新日志，请注意查看版本信息。", "info");
        return new { ok = false, error = "暂时没有更新日志" };
    }

    /// <summary>只读仓库连通性检查（update.check）。</summary>
    public async Task<object> UpdateCheckAsync()
    {
        try
        {
            var status = await Updater.CheckRepositoryAsync().ConfigureAwait(false);
            return new { ok = true, available = status.Available, latency = status.LatencyMs, detail = status.Detail };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.Message };
        }
    }

    // ================================================================
    // AUM 自更新（v2.21：发行包直替 + 有环境时源码编译）
    // ================================================================

    private CancellationTokenSource _aumCts;

    /// <summary>设置页「强制使用发行包」的最近一次结果（自检/日志可读）。</summary>
    internal string LastAumStrategy { get; private set; }

    /// <summary>
    /// 只读规划：检测版本、严格校验清单、判定策略。前端用它渲染确认框。
    /// 不下载发行包、不替换任何文件；源码探测阶段临时目录会在结束时删除。
    /// </summary>
    public async Task<object> AumPlanAsync(bool forcePackage)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var svc = new AumUpdateService(
            new AumUpdateOptions
            {
                CurrentVersion = Version,
                ExePath = Compat.ExePath(),
                Force = false,
                ForcePackage = forcePackage
            },
            NewAumContext(),
            new AumGitHubSource(),
            AumNetPolicy.Strict);

        var plan = await svc.PlanAsync(cts.Token).ConfigureAwait(false);
        LastAumStrategy = plan.Strategy;
        if (!plan.Ok) L("[AUM更新] 规划失败：" + plan.Error, LError);
        else L("[AUM更新] 策略：" + plan.StrategyLabel + "（" + plan.Reason + "）", plan.Strategy == "compile" ? LSuccess : LWarning);
        return plan.ToJson();
    }

    /// <summary>
    /// aum.update：{ force?, forcePackage? }
    /// 有完整构建环境且源码结构正确 → dotnet publish 当前 ServerUI.Web 工程；
    /// 否则下载发行包直替。编译失败自动回退发行包。
    /// </summary>
    public async Task<object> CheckAndUpdateAumAsync(bool force, bool forcePackage)
    {
        if (_aumCts != null)
        {
            L("[AUM更新] 已有 AUM 更新任务在进行中，请等待其完成。", LWarning);
            return new { ok = false, busy = true, error = "已有 AUM 更新任务在进行中" };
        }

        await CheckLeftoverPs1PromptAsync().ConfigureAwait(false);

        _aumCts = new CancellationTokenSource();
        var token = _aumCts.Token;

        try
        {
            L(">>> 正在检测 AUM 管理器更新（发行包直替 / 源码编译 双通道）...", LInfo);
            L("[AUM更新] 当前版本 v" + Version + "，运行形态：" + (IsPortableExe() ? "自包含版（无依赖）" : "框架依赖版（有依赖）"),
                LSecondary);

            var svc = new AumUpdateService(
                new AumUpdateOptions
                {
                    CurrentVersion = Version,
                    ExePath = Compat.ExePath(),
                    Force = force,
                    ForcePackage = forcePackage
                },
                NewAumContext(),
                new AumGitHubSource(),
                AumNetPolicy.Strict);

            var plan = await svc.PlanAsync(token).ConfigureAwait(false);
            LastAumStrategy = plan.Strategy;

            if (!plan.Ok)
            {
                L("[AUM更新] " + plan.Error, LError);
                return new { ok = false, error = plan.Error };
            }

            if (plan.UpToDate)
            {
                L("[AUM更新] 已是最新版本 v" + plan.CurrentVersion + "。", LSuccess);
                _ui.Toast("info", "已是最新版本 v" + plan.CurrentVersion);
                return new { ok = true, upToDate = true, current = plan.CurrentVersion, target = plan.TargetVersion };
            }

            // 目标版本必须严格大于当前（force 同版本重装除外）——再兜一道底
            var cmp = AumVersion.Compare(plan.TargetVersion, Version);
            if (cmp < 0 && Environment.GetEnvironmentVariable("AUM_ALLOW_AUM_DOWNGRADE") != "1")
            {
                var msg = "拒绝版本降级：目标 v" + plan.TargetVersion + " 低于当前 v" + Version;
                L("[AUM更新] " + msg, LError);
                return new { ok = false, error = msg };
            }

            L("[AUM更新] 发现目标版本 v" + plan.TargetVersion + "（当前 v" + Version + "）", LSuccess);
            L("[AUM更新] 采用策略：" + plan.StrategyLabel + "。原因：" + plan.Reason, LInfo);
            if (plan.PackageBytes > 0)
                L("[AUM更新] 发行包大小：" + AumNet.FormatSize(plan.PackageBytes), LSecondary);

            var result = await svc.ExecuteAsync(plan, token).ConfigureAwait(false);

            if (!result.Ok)
            {
                L("[AUM更新] 更新失败：" + result.Error, LError);
                L("    建议：1) 检查网络是否能访问 github.com；2) 查看上方日志定位失败阶段；3) 也可手动下载 " +
                  "ServerUI-fd.zip / ServerUI-sc.zip 覆盖同目录的 exe。", LWarning);
                return new { ok = false, error = result.Error, strategy = result.Strategy, staged = result.NewExePath };
            }

            if (result.UpToDate)
                return new { ok = true, upToDate = true, current = Version, target = Version };

            if (result.Deferred)
            {
                L(">>> 替换由后台脚本完成：退出后请稍候，管理器会自动以新版本启动。", LWarning);
                L("    结果写入：" + result.ResultFile, LSecondary);
                _ui.Toast("warning", "即将退出并完成后台替换");
                ScheduleRestartAndQuit(result.RestartExe);
                return new
                {
                    ok = true,
                    deferred = true,
                    strategy = result.Strategy,
                    current = Version,
                    target = result.TargetVersion,
                    resultFile = result.ResultFile
                };
            }

            L(">>> AUM 更新完成：v" + Version + " → v" + result.TargetVersion + "，正在重启...", LSuccess);
            L("[AUM更新] 备份：" + (result.BackupPath ?? "(无)"), LSecondary);
            if (result.PackageBytes > 0)
                L("[AUM更新] 另一形态的 exe 未同步，如需请手动替换。", LSecondary);
            _ui.Toast("success", "已更新到 v" + result.TargetVersion + "，正在重启");

            ScheduleRestartAndQuit(result.RestartExe);
            return new
            {
                ok = true,
                strategy = result.Strategy,
                current = Version,
                target = result.TargetVersion,
                backup = result.BackupPath,
                restarting = true
            };
        }
        catch (OperationCanceledException)
        {
            L("[AUM更新] 已取消", LWarning);
            return new { ok = false, cancelled = true, error = "已取消" };
        }
        catch (Exception ex)
        {
            L("[AUM更新] 异常：" + ex.Message, LError);
            return new { ok = false, error = ex.Message };
        }
        finally
        {
            _aumCts?.Dispose();
            _aumCts = null;
            _ui.Progress(0, "", false);
        }
    }

    /// <summary>把 AumUpdateService 的输出接回现有的 log/progress/toast 通道。</summary>
    private AumUpdateContext NewAumContext() => new AumUpdateContext
    {
        Log = (level, text) => L(text, level),
        Progress = (value, label) =>
        {
            if (string.IsNullOrEmpty(label)) _ui.Progress(0, "", false);
            else _ui.Progress(value, label, true);
        },
        Toast = (level, text) => _ui.Toast(level, text),
        Diag = m => App.Diag(m)
    };

    /// <summary>替换成功后：延迟 1.2s 启动新 exe，再走现有退出清理（QuitHook → MainWindow.QuitNow）。</summary>
    private void ScheduleRestartAndQuit(string exePath)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1200).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exePath,
                        WorkingDirectory = Path.GetDirectoryName(exePath),
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                L("[AUM更新] 启动新版本失败：" + ex.Message + "，请手动运行：" + exePath, LError);
                return;
            }

            try { await Task.Delay(700).ConfigureAwait(false); } catch { }

            // 复用现有退出清理（保存运行日志 / 停服 / 杀 GM 工具 / 清临时目录）
            try { QuitHook?.Invoke(); } catch { }
        });
    }

    // ================================================================
    // .NET SDK 安装（旧 IS）
    // ================================================================

    public async Task<object> InstallSdkAsync()
    {
        L(">>> 开始安装 .NET 10 SDK...", LInfo);

        var installer = Paths.SdkInstaller;
        if (!File.Exists(installer))
        {
            if (_hasSdk) L(".NET 10 SDK 已就绪，但未找到安装包: " + installer, LWarning);
            else L("未找到 .NET 10 SDK 安装程序: " + installer, LError);
            L("请自行下载 .NET 10.0 SDK (x64) 安装包放入 dotnet-sdk 目录，或运行 dotnet-install.ps1。", LError);
            return new { ok = false, error = "未找到安装程序" };
        }

        if (_hasSdk)
            L(".NET 10 SDK 已就绪，仍将打开安装包供你手动修复/覆盖安装。", LWarning);

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = installer,
                WorkingDirectory = Path.GetDirectoryName(installer),
                UseShellExecute = true
            });
            L("已打开微软 .NET 10 SDK 安装程序。安装完成后请重启管理器，再执行更新。", LSuccess);
            await Task.CompletedTask;
            return new { ok = true };
        }
        catch (Exception ex)
        {
            L("无法启动 .NET 10 SDK 安装程序: " + ex.Message, LError);
            return new { ok = false, error = ex.Message };
        }
    }

    // ================================================================
    // 残留 PS1（旧 CheckLeftoverPs1Prompt / CleanLeftoverPs1）
    // ================================================================

    public List<string> FindLeftoverPs1()
    {
        var list = new List<string>();
        try
        {
            foreach (var f in Directory.GetFiles(Paths.Ad, "*.ps1"))
                list.Add(Path.GetFileName(f));
            list.Sort(StringComparer.OrdinalIgnoreCase);
        }
        catch { }
        return list;
    }

    private void CleanLeftoverPs1(List<string> files)
    {
        var bak = Path.Combine(Paths.Ad, "旧版ps1");
        var moved = 0;
        foreach (var name in files)
        {
            var src = Path.Combine(Paths.Ad, name);
            if (!File.Exists(src)) continue;
            try
            {
                Directory.CreateDirectory(bak);
                var dst = Path.Combine(bak, name);
                if (File.Exists(dst)) File.Delete(dst);
                File.Move(src, dst);
                moved++;
                L(">>> 已清理残留 PS1: " + name, LSuccess);
            }
            catch (Exception ex)
            {
                L(">>> 清理失败: " + name + " - " + ex.Message, LWarning);
            }
        }
        L(">>> 残留 PS1 清理完成: " + moved + " 个文件已移入 旧版ps1\\", LSuccess);
    }

    private async Task CheckLeftoverPs1PromptAsync()
    {
        var leftovers = FindLeftoverPs1();
        if (leftovers.Count == 0) return;

        var yes = await ConfirmAsync("检测到残留 PS1 文件",
            "检测到 AUM管理组件 目录下残留 " + leftovers.Count + " 个 .ps1 文件：\n\n"
            + string.Join("\n", leftovers) + "\n\n"
            + "核心脚本已统一整理到【ps1核心】目录，残留文件可能干扰核心更新功能的正确性。\n"
            + "是否将这些残留文件移入【旧版ps1】备份并清理？",
            "question").ConfigureAwait(false);
        if (yes) CleanLeftoverPs1(leftovers);
    }

    // ================================================================
    // 清理冗余 DB（旧 CleanRedundantDb）
    // ================================================================

    public object CleanRedundantDb()
    {
        var cleaned = 0;
        try
        {
            if (!Directory.Exists(Paths.DataDir)) return new { ok = true, cleaned = 0 };

            foreach (var f in Directory.GetFiles(Paths.DataDir, "inventory*", SearchOption.AllDirectories))
            {
                var nm = Path.GetFileName(f);
                if (string.Equals(nm, "inventory.db", StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(f); cleaned++; } catch { }
            }

            if (cleaned > 0) L(">>> [清理冗余DB] 已清理 " + cleaned + " 个冗余文件", LSuccess);
        }
        catch (Exception ex)
        {
            L(">>> [清理冗余DB] 清理时出错: " + ex.Message, LWarning);
            return new { ok = false, error = ex.Message };
        }
        return new { ok = true, cleaned };
    }

    // ================================================================
    // 退出清理（旧 Fc）
    // ================================================================

    /// <summary>确认退出后的清理序列。返回 false 表示用户取消（旧 Fc 的 e.Cancel = true）。</summary>
    public async Task<bool> ConfirmQuitAsync()
    {
        var yes = await ConfirmAsync("确认退出",
            "退出本程序之后会自动关闭正在运行的服务端和GM工具，是否确认？",
            "warning", "退出", "取消").ConfigureAwait(false);
        return yes;
    }

    /// <summary>旧 Fc 的 Yes 分支：取消更新 → 停服务端 → 杀 GM 工具 → 保存运行日志 → 清缓存。</summary>
    public void CleanupAndExit()
    {
        try { Updater.CancelUpdate(); } catch { }
        L(">>> 正在关闭所有相关进程...", LWarning);

        try { Server.Stop(); } catch { }
        try
        {
            foreach (var p in Process.GetProcessesByName("DfoGmTool"))
            {
                try { p.Kill(); } catch { }
            }
        }
        catch { }

        StopStatusPolling();
        _ui.FlushNow();

        L(">>> 已清理所有进程", LSuccess);
        SaveRunningLog();
        CleanCompileCache();
        CleanUpdateTemp();
        _ui.FlushNow();
    }

    private void SaveRunningLog()
    {
        try
        {
            File.WriteAllText(Paths.RunningLogFile, LogText, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            L(">>> 保存运行日志失败: " + ex.Message, LError);
        }
    }

    private void CleanCompileCache()
    {
        var cacheDirs = new[]
        {
            Path.Combine(Paths.ServerRoot, "Server", "DfoServer", "obj"),
            Path.Combine(Paths.ServerRoot, "Server", "DfoServer", "bin"),
            Path.Combine(Paths.Ad, "dfogmtool", "obj"),
            Path.Combine(Paths.Ad, "dfogmtool", "bin")
        };

        foreach (var dir in cacheDirs)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                Directory.Delete(dir, true);
                L(">>> 已清理编译缓存: " + Path.GetFileName(dir), LSuccess);
            }
            catch (Exception ex)
            {
                L(">>> 清理缓存失败 (" + dir + "): " + ex.Message, LWarning);
            }
        }
    }

    private void CleanUpdateTemp()
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), "ServerS4A21-*"))
            {
                try { Directory.Delete(dir, true); } catch { }
            }
            foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), "ServerUI-AUM-update*"))
            {
                try { Directory.Delete(dir, true); } catch { }
            }
            L(">>> 已清理更新缓存目录", LSuccess);
        }
        catch (Exception ex)
        {
            L(">>> 清理更新缓存失败: " + ex.Message, LWarning);
        }
    }

    // ================================================================
    // 剪贴板（避免 WPF Clipboard 抛异常炸掉 bridge）
    // ================================================================

    internal static void TrySetClipboard(string text, out string error)
    {
        error = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetDataObject(text, true);
                return;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Thread.Sleep(60);
            }
        }
        error ??= "所请求的剪贴板操作失败，请手动进行复制";
    }
}
