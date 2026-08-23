/*
 * ==================================================================
 * 更新编排服务 (UpdateService)
 * ==================================================================
 * 
 * 【功能说明】
 *   调用外部 update.ps1 PowerShell 脚本，执行服务端的增量或全量更新。
 *   负责：启动 PowerShell 进程 → 实时回传输出日志 → 通知完成状态。
 * 
 * 【v1.911 更新】
 *   - 新增镜像源可达性检测 (CheckMirrorSourcesAsync)
 *   - 新增镜像仓库 URL 常量，供 MainForm 显示
 *   - update.ps1 已集成智能源切换（GitGud 不可达 → GitHub 优先 → Codeberg）
 * 
 * 【工作流程】
 *   1. MainForm 调用 RunIncremental() 或 RunFull()
 *   2. 本服务启动 powershell.exe 子进程，执行 AUM管理组件\update.ps1
 *   3. 逐行读取 PowerShell 输出，通过 OutputReceived 事件回传给界面
 *   4. 脚本执行完毕，通过 Completed 事件通知成功/失败
 * 
 * 【新手修改指南】
 *   - 想修改更新脚本路径? 改 RunPowerShell 中的 scriptPath 参数
 *   - 想修改日志读取行数? 改 ReadLogTail 的默认参数
 *   - 想禁用更新功能? 在 MainForm 中隐藏 btIn/btFu 按钮即可
 *   - 想更换更新仓库? 去改 update.ps1 中的仓库 URL
 * 
 * 【事件说明】
 *   OutputReceived — 每收到一行 PowerShell 输出时触发（用于日志区域实时显示）
 *   Completed       — 脚本执行完毕后触发，参数 true=成功, false=失败
 * ==================================================================
 */
using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace ServerUI.Services;

public class UpdateService
{
    const string RepositoryApi = "https://gitgud.io/api/v4/projects/rewio%2F86JP";
    const string ApiTokenB64 = "WjJkcGIxOUZkbUpmUmtScFpqRnNWVlJXUVZGcmR6QjZTMWRIT0RaTlVYQXhUMnBLYWxvelowc3VNREV1TVRBeFozVXhhMnBq";
    static string ApiToken
    {
        get
        {
            var once = Encoding.UTF8.GetString(Convert.FromBase64String(ApiTokenB64));
            return Encoding.UTF8.GetString(Convert.FromBase64String(once));
        }
    }

    // v1.911: 镜像仓库 URL（当 GitGud 不可达时自动切换）
    // update.ps1 已集成智能源切换：GitGud 不可达 → Gitee(国内) → GitHub → Codeberg → 本地缓存
    public const string MirrorGiteeRaw     = "https://gitee.com/c118oder/ServerS4A12.86JP/raw/main";
    public const string MirrorGitHubRaw    = "https://raw.githubusercontent.com/118coder/ServerS4A12.86JP/main";
    public const string MirrorCodebergRaw  = "https://codeberg.org/118coder/ServerS4A12.86JP/raw/branch/main";
    public const string MirrorGiteePage    = "https://gitee.com/c118oder/ServerS4A12.86JP";
    public const string MirrorGitHubPage   = "https://github.com/118coder/ServerS4A12.86JP";
    public const string MirrorCodebergPage = "https://codeberg.org/118coder/ServerS4A12.86JP";

    // v1.922: Gitee 镜像检测令牌 (与 GiteeAdapter 一致)
    const string GiteeTokenB64 = "WlRsbVpXWmlPRE0zWWpsaU5UVTBaamRpTVdaak4yRXdZbVprTlRKaFpUaz0=";
    static string DecodeToken(string b64)
    {
        var once = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        return Encoding.UTF8.GetString(Convert.FromBase64String(once));
    }

    Process _runningProc;

    public void CancelUpdate()
    {
        try { _runningProc?.Kill(); } catch { }
        try { _runningProc?.Dispose(); } catch { }
        _runningProc = null;
    }

    public sealed class RepositoryStatus
    {
        public bool Available { get; init; }
        public long LatencyMs { get; init; }
        public string Detail { get; init; }
    }

    // 实时输出事件：每次 PowerShell 输出一行日志时触发
    // MainForm 订阅此事件，将输出显示到界面上的 RichTextBox 日志区域
    public event Action<string> OutputReceived;

    // 更新完成事件：脚本执行完毕后触发
    // MainForm 订阅此事件，用于停止进度条动画并刷新界面
    public event Action<bool> Completed;

    // Check the same gitgud.io GitLab API used by update.ps1, so the advice matches the actual update path.
    public async Task<RepositoryStatus> CheckRepositoryAsync()
    {
        var timer = Stopwatch.StartNew();
        Exception lastError = null;
        // This is only a preflight indicator. Keep it fast; update.ps1 performs the resilient work.
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
                client.DefaultRequestHeaders.Add("PRIVATE-TOKEN", ApiToken);
                using var response = await client.GetAsync(RepositoryApi,
                    HttpCompletionOption.ResponseHeadersRead);
                if (response.IsSuccessStatusCode)
                {
                    timer.Stop();
                    return new RepositoryStatus
                    {
                        Available = true,
                        LatencyMs = timer.ElapsedMilliseconds,
                        Detail = "HTTP " + (int)response.StatusCode
                    };
                }
                lastError = new HttpRequestException("HTTP " + (int)response.StatusCode);
            }
            catch (Exception ex) { lastError = ex; }

            if (attempt < 3)
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt));
        }

        timer.Stop();
        return new RepositoryStatus
        {
            Available = false,
            LatencyMs = timer.ElapsedMilliseconds,
            Detail = lastError?.GetBaseException().Message ?? "未知连接错误"
        };
    }

    // v1.911: 网页可达性轻量检测（启动时使用，不消耗 API 配额）
    // 仅对各源首页做 HEAD 请求，不使用 API 令牌或 raw 文件获取
    // 返回字典包含各源名称、是否可达、延迟毫秒数
    public async Task<Dictionary<string, (bool Reachable, long LatencyMs)>> CheckBasicConnectivityAsync()
    {
        var results = new Dictionary<string, (bool, long)>();
        
        async Task<(bool, long)> HeadCheck(string url, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Head, url);
                using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
                client.DefaultRequestHeaders.Add("User-Agent", "ServerUI-AUM");
                using var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                sw.Stop();
                return (true, sw.ElapsedMilliseconds);
            }
            catch
            {
                sw.Stop();
                return (false, sw.ElapsedMilliseconds);
            }
        }
        
        // 网页首页检测，不使用 API —— 防配额消耗
        var (gr, gl) = await HeadCheck("https://gitgud.io", 6000);
        results["GitGud"] = (gr, gl);
        var (geR, geL) = await HeadCheck("https://gitee.com", 6000);
        results["Gitee"] = (geR, geL);
        var (hr, hl) = await HeadCheck("https://github.com", 6000);
        results["GitHub"] = (hr, hl);
        var (cr, cl) = await HeadCheck("https://codeberg.org", 6000);
        results["Codeberg"] = (cr, cl);
        
        return results;
    }

    // v1.911: 镜像源 raw 文件可达性检测（更新前调用，验证 latest.json 可读取）
    public async Task<Dictionary<string, (bool Available, long LatencyMs)>> CheckMirrorSourcesAsync()
    {
        var results = new Dictionary<string, (bool, long)>();
        
        // GitHub raw
        var ghTimer = Stopwatch.StartNew();
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            client.DefaultRequestHeaders.Add("User-Agent", "ServerUI-AUM");
            using var response = await client.GetAsync(MirrorGitHubRaw + "/latest.json",
                HttpCompletionOption.ResponseHeadersRead);
            ghTimer.Stop();
            results["GitHub"] = (response.IsSuccessStatusCode, ghTimer.ElapsedMilliseconds);
        }
        catch { ghTimer.Stop(); results["GitHub"] = (false, ghTimer.ElapsedMilliseconds); }
        
        // Codeberg raw
        var cbTimer = Stopwatch.StartNew();
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            client.DefaultRequestHeaders.Add("User-Agent", "ServerUI-AUM");
            using var response = await client.GetAsync(MirrorCodebergRaw + "/latest.json",
                HttpCompletionOption.ResponseHeadersRead);
            cbTimer.Stop();
            results["Codeberg"] = (response.IsSuccessStatusCode, cbTimer.ElapsedMilliseconds);
        }
        catch { cbTimer.Stop(); results["Codeberg"] = (false, cbTimer.ElapsedMilliseconds); }

        // v1.922: 补上 Gitee 检测 — Gitee 为私有仓库, raw 直链需 access_token,
        // 用 API contents 端点验证 latest.json 可读取 (与 update.ps1 下载路径一致)
        var geTimer = Stopwatch.StartNew();
        try
        {
            var giteeToken = DecodeToken(GiteeTokenB64);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            client.DefaultRequestHeaders.Add("User-Agent", "ServerUI-AUM");
            using var response = await client.GetAsync(
                "https://gitee.com/api/v5/repos/c118oder/ServerS4A12.86JP/contents/latest.json?access_token="
                + giteeToken,
                HttpCompletionOption.ResponseHeadersRead);
            geTimer.Stop();
            results["Gitee"] = (response.IsSuccessStatusCode, geTimer.ElapsedMilliseconds);
        }
        catch { geTimer.Stop(); results["Gitee"] = (false, geTimer.ElapsedMilliseconds); }

        return results;
    }

    /*
     * 增量更新
     * 作用: 只下载最近 72 小时内变更的文件，速度快，适合日常更新
     * 原理: update.ps1 通过 gitgud.io GitLab API 获取最近 3 天的 commit，只同步变更的文件
     * 调用时机: 用户点击 [增量更新] 按钮
     * 
     * 参数:
     *   workDir   — PowerShell 的工作目录（ServerS4A12-AUM 目录）
     *   scriptDir — update.ps1 所在目录（AUM管理组件\）
     */
    public async Task RunIncremental(string workDir, string scriptDir, bool skipCommitLog = false, bool useMirror = false)
    {
        var args = "";
        if (skipCommitLog) args += "-SkipCommitLog ";
        if (useMirror) args += "-UseMirror ";
        await RunPowerShell(workDir, Path.Combine(scriptDir, "ps1核心", "update.ps1"), args.Trim());
    }

    /*
     * 全量更新
     * 作用: 下载所有历史变更文件，确保与仓库完全一致，适合首次部署
     * 原理: update.ps1 带上 -FullSync 参数，对比整个仓库历史
     * 调用时机: 用户点击 [全量更新] 按钮
     */
    public async Task RunFull(string workDir, string scriptDir, bool skipCommitLog = false, bool useMirror = false)
    {
        var args = "-FullSync";
        if (skipCommitLog) args += " -SkipCommitLog";
        if (useMirror) args += " -UseMirror";
        await RunPowerShell(workDir, Path.Combine(scriptDir, "ps1核心", "update.ps1"), args);
    }

    /*
     * 核心方法：启动 PowerShell 子进程执行 update.ps1
     * 
     * 执行过程:
     *   1. 检查脚本文件是否存在
     *   2. 构造 PowerShell 命令行（UTF8编码 + Bypass执行策略 + 非交互模式）
     *   3. 启动进程，注册 Output/Error 数据接收事件
     *   4. 异步读取输出，逐行触发 OutputReceived
     *   5. 等待进程退出，触发 Completed
     * 
     * 修改建议:
     *   - 想修改超时时间? 在 ProcessStartInfo 中设置 Timeout
     *   - 想隐藏控制台? 已经通过 CreateNoWindow=true 隐藏了
     *   - 想改用 cmd.exe 而不是 PowerShell? 改 FileName 和 Arguments
     */
    private async Task RunPowerShell(string workDir, string scriptPath, string args)
    {
        // 脚本不存在时的处理：通知界面并标记失败
        if (!File.Exists(scriptPath))
        {
            OutputReceived?.Invoke("[ERROR] Script not found: " + scriptPath);
            Completed?.Invoke(false);
            return;
        }

        // 构造 PowerShell 完整命令行
        // -NoProfile: 不加载用户配置（加快启动）
        // -ExecutionPolicy Bypass: 绕过脚本执行限制
        // -Command: 设置 UTF8 输出编码 + 执行脚本
        var fullArgs = "-NoProfile -ExecutionPolicy Bypass -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; & '"
                       + scriptPath + "' -NonInteractive"
                       + (string.IsNullOrEmpty(args) ? "" : " " + args) + "\"";

        var before = CaptureFiles(workDir);
        OutputReceived?.Invoke("[CHECK] 已记录更新前文件状态: " + before.Count + " 个文件");

        // 在后台线程中运行，不阻塞 UI
        await Task.Run(() =>
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = fullArgs,
                WorkingDirectory = workDir,   // PowerShell 的起始目录
                UseShellExecute = false,       // 必须为 false 才能重定向输出
                RedirectStandardOutput = true, // 捕获标准输出
                RedirectStandardError = true,  // 捕获错误输出
                CreateNoWindow = true,         // 不显示 PowerShell 黑窗口
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            // 【v1.85-1 修复】部分旧 CPU/Windows 不支持 CET (Control-flow Enforcement)
            // dotnet build 时会报错 "Your Windows doesn't fully support CET"
            // 设置环境变量 DOTNET_EnableCET=0 可禁用 CET 检查, 不影响编译结果
            psi.Environment["DOTNET_EnableCET"] = "0";

            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };

            var commitLogBuffer = new System.Collections.Generic.List<string>();
            var inCommitLog = false;

            p.OutputDataReceived += (s, a) =>
            {
                if (string.IsNullOrEmpty(a.Data)) return;
                if (a.Data.Contains(">>> [5/5]")) { inCommitLog = true; }

                if (a.Data.StartsWith("##PROGRESS##"))
                {
                    // 进度标记: 不显示在日志中，由 OU 解析
                    OutputReceived?.Invoke(a.Data);
                    return;
                }

                if (inCommitLog)
                    commitLogBuffer.Add(a.Data);
                else
                    OutputReceived?.Invoke(a.Data);
            };
            p.ErrorDataReceived += (s, a) =>
            {
                if (!string.IsNullOrEmpty(a.Data))
                    OutputReceived?.Invoke("[ERR] " + a.Data);
            };

            p.Start();
            _runningProc = p;
            p.BeginOutputReadLine();   // 开始异步读取标准输出
            p.BeginErrorReadLine();    // 开始异步读取错误输出
            p.WaitForExit();           // 等待 PowerShell 进程结束

            _runningProc = null;

            // 先输出文件变更列表 [FILES]
            var changed = DescribeFileChanges(workDir, before);
            if (changed.Count == 0)
                OutputReceived?.Invoke("[FILES] 本次未检测到服务端源码文件变更。");
            else
            {
                OutputReceived?.Invoke("[FILES] 本次检测到 " + changed.Count + " 个文件变化:");
                foreach (var line in changed)
                    OutputReceived?.Invoke("[FILES] " + line);
            }

            // 再输出缓存的提交日志 (排在 [FILES] 之后)
            foreach (var line in commitLogBuffer)
                OutputReceived?.Invoke(line);

            // 通过 ExitCode 判断成功或失败
            // 0 = 成功, 非0 = 失败（脚本中 exit 1 时）
            Completed?.Invoke(p.ExitCode == 0);
        });
    }

    private static Dictionary<string, (long Length, DateTime Modified)> CaptureFiles(string root)
    {
        var files = new Dictionary<string, (long, DateTime)>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return files;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            // Build outputs are regenerated and do not describe repository updates.
            var relative = Compat.GetRelativePath(root, path);
            if (relative.StartsWith(".git" + Path.DirectorySeparatorChar)
                || relative.StartsWith("dist" + Path.DirectorySeparatorChar))
                continue;
            var info = new FileInfo(path);
            files[relative] = (info.Length, info.LastWriteTime);
        }
        return files;
    }

    private static List<string> DescribeFileChanges(string root,
        Dictionary<string, (long Length, DateTime Modified)> before)
    {
        var after = CaptureFiles(root);
        var changes = new List<string>();
        foreach (var item in after)
        {
            if (!before.TryGetValue(item.Key, out var previous))
                changes.Add("新增 " + item.Key + " | 更新日期 " + item.Value.Modified.ToString("yyyy-MM-dd HH:mm"));
            else if (previous.Length != item.Value.Length || previous.Modified != item.Value.Modified)
                changes.Add("更新 " + item.Key + " | " + previous.Modified.ToString("yyyy-MM-dd HH:mm")
                    + " -> " + item.Value.Modified.ToString("yyyy-MM-dd HH:mm"));
        }
        foreach (var item in before)
            if (!after.ContainsKey(item.Key))
                changes.Add("删除 " + item.Key + " | 原日期 " + item.Value.Modified.ToString("yyyy-MM-dd HH:mm"));
        changes.Sort(StringComparer.OrdinalIgnoreCase);
        return changes;
    }

    /*
     * 读取更新日志末尾 N 行
     * 用于界面上快速查看最近的变更记录（暂未直接使用，保留备用）
     * 
     * 参数:
     *   baseDir — AUM管理组件目录
     *   lines   — 读取行数（默认 40 行）
     */
    public string ReadLogTail(string baseDir, int lines = 40)
    {
        var text = GetLogText(baseDir);
        if (string.IsNullOrEmpty(text)) return "";
        var all = text.Split('\n');
        // 从后往前取 lines 行（如果文件不够 lines 行，从头开始取）
        var start = Math.Max(0, all.Length - lines);
        var tail = new System.Collections.Generic.List<string>();
        for (int i = start; i < all.Length; i++) tail.Add(all[i]);
        return string.Join("\n", tail.ToArray()).TrimEnd('\r');
    }

    /*
     * 从更新日志中提取最新版本号
     * 搜索逻辑: 在 更新日志.txt 中找到最后一个 "版本:" 标记，截取后面的日期字符串
     * 示例: "版本: 2026-07-15" → 返回 "2026-07-15"
     * 返回 "--" 表示日志文件不存在或未找到版本标记
     */
    public string GetVersion(string baseDir)
    {
        var text = GetLogText(baseDir);
        if (string.IsNullOrEmpty(text)) return "--";

        // 查找最后一个 "版本:" 位置
        var idx = text.LastIndexOf("版本:");
        if (idx >= 0)
        {
            // 截取从 "版本:" 到行尾的内容
            var end = text.IndexOf('\n', idx);
            if (end < 0) end = Math.Min(idx + 20, text.Length);
            return text.Substring(idx, end - idx).Trim().Replace("版本:", "").Trim();
        }

        return "--";
    }

    // =================================================================
    // 更新日志读取缓存 (性能优化)
    // 三个窗口每 2 秒都会读取 更新日志.txt, 直接用 File.ReadAllText
    // 会产生持续磁盘 IO; 这里按 LastWriteTimeUtc 缓存, 文件未变化时
    // 只做一次廉价的文件元数据查询
    // =================================================================
    static string _logCachePath = "";
    static DateTime _logCacheWrite = DateTime.MinValue;
    static string _logCacheText = "";

    public string GetLogText(string baseDir)
    {
        var log = Path.Combine(baseDir, "更新日志.txt");
        if (!File.Exists(log)) return "";
        try
        {
            var lw = File.GetLastWriteTimeUtc(log);
            if (lw != _logCacheWrite || _logCachePath != log)
            {
                _logCacheWrite = lw;
                _logCachePath = log;
                _logCacheText = File.ReadAllText(log, Encoding.UTF8);
            }
            return _logCacheText;
        }
        catch { return ""; }
    }
}
