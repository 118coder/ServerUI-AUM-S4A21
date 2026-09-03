/*
 * ==================================================================
 * AUM管理器自更新服务 (SelfUpdateService) — v1.917
 * ==================================================================
 *
 * 【功能说明】
 *   检测 GitHub 仓库中是否有新版本，有则下载源码 → 本地编译 → 替换 EXE。
 *   整个自更新过程完全自动化，利用用户已有的 .NET 10 SDK 编译。
 *
 * 【工作流程】
 *   1. 读取 GitHub Raw 上的 MainForm.cs，正则提取 VER 版本号
 *   2. 对比本地 VER，相同则跳过，不同则进入更新
 *   3. 下载 GitHub 仓库 ZIP → 解压到临时目录
 *   4. 找 ServerUI 子目录 → dotnet restore → dotnet publish
 *   5. 编译成功后生成替换脚本 → 退出旧进程 → 脚本覆盖 EXE → 启动新 EXE
 *
 * 【多轮判定 / 任何一步失败均可安全回滚】
 *   R1-R3: 网络/ZIP 异常 → 重试，不碰本地文件
 *   R4-R7: 编译相关 → 旧 EXE 持续运行，不影响用户
 *   R8:    替换 EXE → 临时脚本保证原子操作
 * ==================================================================
 */
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ServerUI.Services;

public class SelfUpdateService
{
    const string GitHubRaw = "https://raw.githubusercontent.com/118coder/ServerUI-AUM-S4A21/main/";
    const string GitHubApi = "https://api.github.com/repos/118coder/ServerUI-AUM-S4A21/contents/";
    const string RepoZipUrl = "https://github.com/118coder/ServerUI-AUM-S4A21/archive/refs/heads/main.zip";
    const string VerFile = "AUM-version.txt";

    public string RemoteVersion { get; private set; }

    public event Action<string> OutputReceived;
    public event Action<bool> Completed;

    public async Task<bool> CheckForUpdateAsync(string localVer)
    {
        try
        {
            var ver = await FetchRemoteVersion();
            if (ver == null)
            {
                RemoteVersion = null;
                OutputReceived?.Invoke("[AUM自检] 无法连接 GitHub，跳过版本检测");
                return false;
            }

            RemoteVersion = ver;
            var cmp = CompareVersion(ver, localVer);
            return cmp > 0;
        }
        catch
        {
            RemoteVersion = null;
            return false;
        }
    }

    async Task<string> FetchRemoteVersion()
    {
        var rawTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // 第1层: 尝试 GitHub API（刷新最快，无CDN缓存）
        for (int a = 1; a <= 2; a++)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
                client.DefaultRequestHeaders.Add("User-Agent", "ServerUI-AUM");
                client.DefaultRequestHeaders.Add("Cache-Control", "no-cache, no-store");
                client.DefaultRequestHeaders.Add("Pragma", "no-cache");

                var resp = await client.GetStringAsync(GitHubApi + VerFile + "?ref=main&t=" + rawTimestamp);
                using var doc = JsonDocument.Parse(resp);
                if (doc.RootElement.TryGetProperty("content", out var contentEl))
                {
                    var bytes = Convert.FromBase64String(contentEl.GetString() ?? "");
                    var text = Encoding.UTF8.GetString(bytes).Trim();
                    text = Regex.Replace(text, @"\s+", "");
                    if (text.Length > 0) return text;
                }
            }
            catch
            {
                if (a < 2) await Task.Delay(500);
            }
        }

        // 第2层: 回退到 Raw URL（带多重缓存穿透参数）
        for (int a = 1; a <= 2; a++)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
                client.DefaultRequestHeaders.Add("User-Agent", "ServerUI-AUM");
                client.DefaultRequestHeaders.Add("Cache-Control", "no-cache, no-store, must-revalidate");
                client.DefaultRequestHeaders.Add("Pragma", "no-cache");

                var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var url = GitHubRaw + VerFile + "?r=" + ts + "&_=" + Guid.NewGuid().ToString("N").Substring(0, 8);
                var text = await client.GetStringAsync(url);
                text = text.Trim();
                text = Regex.Replace(text, @"\s+", "");
                if (text.Length > 0) return text;
            }
            catch
            {
                if (a < 2) await Task.Delay(500);
            }
        }

        return null;
    }

    public int CompareVersion(string a, string b)
    {
        var partsA = ParseVersion(a);
        var partsB = ParseVersion(b);
        int len = Math.Max(partsA.Length, partsB.Length);
        for (int i = 0; i < len; i++)
        {
            int va = i < partsA.Length ? partsA[i] : 0;
            int vb = i < partsB.Length ? partsB[i] : 0;
            if (va != vb) return va.CompareTo(vb);
        }
        return 0;
    }

    static int[] ParseVersion(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return new[] { 0 };
        var s = v.Trim();
        var parts = s.Split('.', '-', '_');
        var nums = new System.Collections.Generic.List<int>();
        foreach (var p in parts)
        {
            var cleaned = System.Text.RegularExpressions.Regex.Replace(p, @"[^0-9]", "");
            if (int.TryParse(cleaned, out var n) && n >= 0)
                nums.Add(n);
            else if (cleaned.Length > 0)
                nums.Add(0);
        }
        if (nums.Count == 0) nums.Add(0);
        return nums.ToArray();
    }

    public async Task RunUpdateAsync(string localDir)
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "ServerUI-AUM-update");
        var tmpZip = Path.Combine(tmpDir, "source.zip");
        var tmpExtract = Path.Combine(tmpDir, "extract");
        var publishDir = Path.Combine(tmpDir, "publish");
        var curExe = Compat.ExePath();

        // v2.033 修复: 计算 AUM管理组件 根目录 (同步目标)
        // 旧逻辑 GetDirectoryName(localDir) 在用户机器无 AUM管理组件\ServerUI
        // 子目录时 (绝大多数正常部署) 会误指向 AUM管理组件 的父目录,
        // 导致 ps1核心/实用工具包 等同步到了错误位置 (用户目录里"没更新")
        // 正确判定: AUM管理组件 根 = 含 ServerS4A21-AUM 子目录的目录
        var aumRoot = localDir;
        if (!Directory.Exists(Path.Combine(aumRoot, "ServerS4A21-AUM")))
        {
            var parent = Path.GetDirectoryName(localDir);
            if (parent != null && Directory.Exists(Path.Combine(parent, "ServerS4A21-AUM")))
                aumRoot = parent;
        }

        try
        {
            // R1 准备临时目录
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
            Directory.CreateDirectory(tmpDir);
            Directory.CreateDirectory(publishDir);

            // R2 下载源码 ZIP (5次重试)
            // v2.031: 流式下载 + 进度反馈 — 下载大包(GitHub 国内网速慢)时每 2 秒
            // 输出已下载大小, 用户能直观看到进度, 不再出现"长时间无反馈像卡死"的情况
            // 注意: HttpClient.Timeout 只作用于响应头, 不作用于流读取!
            // 必须用 CancellationTokenSource 给整体下载加超时, 否则传输中断时会无限挂起
            OutputReceived?.Invoke("[AUM更新] 正在从 GitHub 下载最新源码包 (约 14MB, 网络慢时请耐心等待)...");
            var ok = false;
            for (int a = 1; a <= 5; a++)
            {
                try
                {
                    using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(150));
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                    client.DefaultRequestHeaders.Add("User-Agent", "ServerUI-AUM");
                    using var resp = await client.GetAsync(RepoZipUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    resp.EnsureSuccessStatusCode();
                    long total = resp.Content.Headers.ContentLength ?? 0;
#if NET48
                    // Win7 版 (net48) 无 ReadAsStreamAsync(token) 重载
                    using var src = await resp.Content.ReadAsStreamAsync();
#else
                    using var src = await resp.Content.ReadAsStreamAsync(cts.Token);
#endif
                    using (var dst = File.Create(tmpZip))
                    {
                        var buf = new byte[81920];
                        long done = 0;
                        var lastLog = DateTime.UtcNow;
                        while (true)
                        {
                            var n = await src.ReadAsync(buf, 0, buf.Length, cts.Token);
                            if (n <= 0) break;
                            await dst.WriteAsync(buf, 0, n, cts.Token);
                            done += n;
                            if (DateTime.UtcNow - lastLog > TimeSpan.FromSeconds(2))
                            {
                                lastLog = DateTime.UtcNow;
                                var mb = (double)done / 1048576.0;
                                OutputReceived?.Invoke(total > 0
                                    ? string.Format("[AUM更新] 已下载 {0:F1} / {1:F1} MB", mb, total / 1048576.0)
                                    : string.Format("[AUM更新] 已下载 {0:F1} MB", mb));
                            }
                        }
                    }

                    if (new FileInfo(tmpZip).Length > 10240) { ok = true; break; }
                }
                catch (Exception ex)
                {
                    OutputReceived?.Invoke("[AUM更新] 第 " + a + " 次下载失败: " + ex.Message);
                }
                if (a < 5) await Task.Delay((int)Math.Pow(2, a) * 1000);
            }

            if (!ok)
            {
                OutputReceived?.Invoke("[AUM更新] 下载源码失败，请检查网络（GitHub 国内访问波动较大，可稍后重试）。");
                Completed?.Invoke(false);
                return;
            }

            // R3 解压
            OutputReceived?.Invoke("[AUM更新] 正在解压源码...");
            if (Directory.Exists(tmpExtract)) Directory.Delete(tmpExtract, true);
            ZipFile.ExtractToDirectory(tmpZip, tmpExtract);

            // GitHub zipball 解压后有个子目录 (如 118coder-ServerUI-AUM-S4A21-xxxxx)
            var rootDir = tmpExtract;
            var subDir = Directory.GetDirectories(tmpExtract).FirstOrDefault(
                d => d.Contains("ServerUI") || d.Contains("S4A21")) ?? tmpExtract;
            rootDir = subDir;

            // 找 ServerUI 源码目录
            var srcDir = Path.Combine(rootDir, "ServerUI");
            if (!Directory.Exists(srcDir))
            {
                var dirs = Directory.GetDirectories(rootDir, "ServerUI", SearchOption.AllDirectories);
                if (dirs.Length > 0) srcDir = dirs[0];
            }

            if (!Directory.Exists(srcDir) || !File.Exists(Path.Combine(srcDir, "ServerUI.csproj")))
            {
                OutputReceived?.Invoke("[AUM更新] 源码包结构异常，请手动更新。");
                Completed?.Invoke(false);
                return;
            }

            // R4 直接在下载的源码上编译（不动本地文件，确保编译源正确）
            OutputReceived?.Invoke("[AUM更新] 正在编译新版本（直接从GitHub源码）...");
            var sdk = FindDotNet();
            if (sdk == null)
            {
                OutputReceived?.Invoke("[AUM更新] 未找到 .NET 10 SDK，无法编译。请安装SDK后重试。");
                Completed?.Invoke(false);
                return;
            }

            // 清理重复文件
            CleanDuplicates(srcDir);

            // R5 dotnet restore
            OutputReceived?.Invoke("[AUM更新] 正在还原依赖...");
            var projFile = Path.Combine(srcDir, "ServerUI.csproj");
            var exit = await RunDotnet(sdk, $"restore \"{projFile}\" --ignore-failed-sources", srcDir);
            if (exit != 0)
            {
                OutputReceived?.Invoke("[AUM更新] 依赖还原失败 (exit " + exit + ")。");
                Completed?.Invoke(false);
                return;
            }

            // R6 dotnet publish (无依赖版)
            OutputReceived?.Invoke("[AUM更新] 正在编译新版本...");
            var fdExePath = Path.Combine(publishDir, "ServerUI.exe");
            exit = await RunDotnet(sdk,
                $"publish \"{projFile}\" -c Release -r win-x64 --no-self-contained -o \"{publishDir}\"",
                srcDir);

            if (exit != 0)
            {
                OutputReceived?.Invoke("[AUM更新] 编译失败 (exit " + exit + ")。");
                Completed?.Invoke(false);
                return;
            }

            if (!File.Exists(fdExePath))
            {
                OutputReceived?.Invoke("[AUM更新] 编译产物缺失，请检查源码。");
                Completed?.Invoke(false);
                return;
            }

            // R7 验证新 EXE 大小
            var newSize = new FileInfo(fdExePath).Length;
            if (newSize < 10240)
            {
                OutputReceived?.Invoke("[AUM更新] 编译产物异常 (仅 " + newSize + " 字节)。");
                Completed?.Invoke(false);
                return;
            }

            // R7.3 编译 Win7 兼容模式 exe (ServerUI-兼容模式.exe)
            // 仓库需包含与 ServerUI.csproj 同目录的 ServerUI-Win7.csproj (共享源码, net48)
            string win7ExePath = null;
            var win7Proj = Path.Combine(srcDir, "ServerUI-Win7.csproj");
            if (File.Exists(win7Proj))
            {
                OutputReceived?.Invoke("[AUM更新] 正在编译 Win7 兼容模式...");
                try
                {
                    var win7PublishDir = Path.Combine(tmpDir, "publish-win7");
                    var exit7 = await RunDotnet(sdk, $"restore \"{win7Proj}\" --ignore-failed-sources", srcDir);
                    if (exit7 == 0)
                    {
                        exit7 = await RunDotnet(sdk, $"publish \"{win7Proj}\" -c Release -o \"{win7PublishDir}\"", srcDir);
                        var win7Exe = Path.Combine(win7PublishDir, "ServerUI-Win7.exe");
                        if (exit7 == 0 && File.Exists(win7Exe)
                            && new FileInfo(win7Exe).Length > 10240)
                        {
                            win7ExePath = win7Exe;
                            OutputReceived?.Invoke("[AUM更新] Win7 兼容模式编译成功");
                        }
                        else
                            OutputReceived?.Invoke("[AUM更新] Win7 兼容模式编译失败，跳过（不影响主版本）");
                    }
                    else
                        OutputReceived?.Invoke("[AUM更新] Win7 兼容模式还原失败，跳过");
                }
                catch (Exception ex7)
                {
                    OutputReceived?.Invoke("[AUM更新] Win7 兼容模式异常: " + ex7.Message);
                }
            }
            else
                OutputReceived?.Invoke("[AUM更新] 仓库未含 ServerUI-Win7.csproj，跳过兼容模式编译");

            // R7.5 编译成功后，同步仓库内容到本地（异步，不影响替换流程）
            // v2.031: ServerUI 源码不同步进 AUM管理组件 (用户明确不要),
            // 仅同步根文件(bat/ps1/txt 由 SyncRootFiles+ReorganizeScripts 维护)
            try { SyncRootFiles(rootDir, aumRoot); ReorganizeScripts(aumRoot); } catch { }
            // v2.031: 仓库包整体覆盖 — 实用工具包/DX11运行/DX12运行/dfogmtool/latest/ps1核心 等
            // 新增文件夹随更新自动同步进 AUM管理组件 (ServerUI 源码目录除外)
            try { SyncRepoToAumDir(rootDir, aumRoot); } catch { }

            // R7.6 下载镜像中的更新日志并覆盖本地（v2.12: 始终拉取最新镜像日志, 本地日志与镜像保持一致）
            try { await DownloadChangelogFromMirror(aumRoot); } catch { }

            // R8 生成替换脚本并退出
            // 用临时 PowerShell 脚本实现: 等旧进程退出 → 覆盖 EXE → 启动 → 自清理
            OutputReceived?.Invoke("[AUM更新] 编译成功，正在准备替换...");
            var psPath = Path.Combine(tmpDir, "replace.ps1");
            var psScript = new StringBuilder();
            psScript.AppendLine("$oldPid = " + Compat.Pid + ";");
            psScript.AppendLine("$newExe = @\"\n" + fdExePath + "\n\"@;");
            psScript.AppendLine("$target = @\"\n" + curExe + "\n\"@;");
            if (win7ExePath != null)
            {
                psScript.AppendLine("$win7New = @\"\n" + win7ExePath + "\n\"@;");
                psScript.AppendLine("$win7Target = Join-Path (Split-Path $target -Parent) 'ServerUI-兼容模式.exe';");
                psScript.AppendLine("$win7Cfg = $win7New + '.config';");
            }
            psScript.AppendLine("$tmpDir = @\"\n" + tmpDir + "\n\"@;");
            psScript.AppendLine("Start-Sleep -Seconds 2;");
            psScript.AppendLine("# 等待旧进程完全退出 (最多等 30 秒)");
            psScript.AppendLine("for ($i = 0; $i -lt 30; $i++) {");
            psScript.AppendLine("    $p = Get-Process -Id $oldPid -ErrorAction SilentlyContinue;");
            psScript.AppendLine("    if (-not $p) { break };");
            psScript.AppendLine("    Start-Sleep -Seconds 1;");
            psScript.AppendLine("}");
            psScript.AppendLine("# 覆盖旧 EXE");
            psScript.AppendLine("try {");
            psScript.AppendLine("    Copy-Item -LiteralPath $newExe -Destination $target -Force;");
            psScript.AppendLine("    Write-Host '替换成功';");
            psScript.AppendLine("    Start-Process -FilePath $target;");
            psScript.AppendLine("} catch {");
            psScript.AppendLine("    Write-Host \"替换失败: $_\";");
            psScript.AppendLine("    Start-Sleep -Seconds 10;");
            psScript.AppendLine("}");
            if (win7ExePath != null)
            {
                psScript.AppendLine("# 同步 Win7 兼容模式 exe 到程序目录");
                psScript.AppendLine("try {");
                psScript.AppendLine("    Copy-Item -LiteralPath $win7New -Destination $win7Target -Force;");
                psScript.AppendLine("    if (Test-Path $win7Cfg) { Copy-Item -LiteralPath $win7Cfg -Destination ($win7Target + '.config') -Force; }");
                psScript.AppendLine("    Write-Host '兼容模式已同步';");
                psScript.AppendLine("} catch {");
                psScript.AppendLine("    Write-Host \"兼容模式同步失败: $_\";");
                psScript.AppendLine("}");
            }
            psScript.AppendLine("# 清理临时目录");
            psScript.AppendLine("Start-Sleep -Seconds 2;");
            psScript.AppendLine("Remove-Item -LiteralPath $tmpDir -Recurse -Force -ErrorAction SilentlyContinue;");
            File.WriteAllText(psPath, psScript.ToString(), Encoding.UTF8);

            OutputReceived?.Invoke("[AUM更新] 即将退出并完成替换...");
            OutputReceived?.Invoke("[AUM更新] 当前程序: " + curExe + "  →  将被新版本覆盖");
            if (win7ExePath != null)
                OutputReceived?.Invoke("[AUM更新] Win7 兼容模式: ServerUI-兼容模式.exe 将同步到程序目录");
            Completed?.Invoke(true);

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + psPath + "\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });

            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            OutputReceived?.Invoke("[AUM更新] 异常: " + ex.Message);
            Completed?.Invoke(false);
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); }
            catch { }
        }
    }

    static string FindDotNet()
    {
        // 第一级: 尝试系统 PATH 中的 dotnet
        try
        {
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (p == null) throw new Exception("Process.Start returned null");
            var v = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            var m = Regex.Match(v, @"^(\d+)\.");
            if (p.ExitCode == 0 && m.Success && int.Parse(m.Groups[1].Value) >= 10)
                return "dotnet";
        }
        catch { }

        // 第二级: 用 where.exe 查找 dotnet.exe 所在位置
        try
        {
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = "dotnet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            });
            if (p == null) throw new Exception("Process.Start returned null");
            var all = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            var lines = all.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var path = line.Trim();
                if (!File.Exists(path)) continue;
                try
                {
                    var vp = Process.Start(new ProcessStartInfo
                    {
                        FileName = path,
                        Arguments = "--version",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    });
                    if (vp == null) continue;
                    var v = vp.StandardOutput.ReadToEnd().Trim();
                    vp.WaitForExit();
                    var m = Regex.Match(v, @"^(\d+)\.");
                    if (vp.ExitCode == 0 && m.Success && int.Parse(m.Groups[1].Value) >= 10)
                        return path;
                }
                catch { }
            }
        }
        catch { }

        // 第三级: 标准安装目录 (x64 + x86)
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet", "dotnet.exe"),
        };
        foreach (var pf in candidates)
        {
            if (!File.Exists(pf)) continue;
            try
            {
                var p = Process.Start(new ProcessStartInfo
                {
                    FileName = pf,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                });
                if (p == null) continue;
                var v = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit();
                var m = Regex.Match(v, @"^(\d+)\.");
                if (p.ExitCode == 0 && m.Success && int.Parse(m.Groups[1].Value) >= 10)
                    return pf;
            }
            catch { }
        }

        return null;
    }

    async Task<int> RunDotnet(string sdk, string args, string workDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = sdk,
            Arguments = args,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.Environment["DOTNET_EnableCET"] = "0";

        using var p = new Process { StartInfo = psi };
        p.Start();

        // 必须异步读取输出，否则缓冲区满会导致进程卡死
        var act = OutputReceived;
        _ = Task.Run(() =>
        {
            while (!p.StandardOutput.EndOfStream)
            {
                var line = p.StandardOutput.ReadLine();
                if (line != null && line.Length > 0)
                    act?.Invoke("[编译] " + line);
            }
        });
        _ = Task.Run(() =>
        {
            while (!p.StandardError.EndOfStream)
            {
                var line = p.StandardError.ReadLine();
                if (line != null && line.Length > 0)
                    act?.Invoke("[编译] " + line);
            }
        });

#if NET48
        // Win7 版 (net48) 无 WaitForExitAsync, 用阻塞式等待 (自更新编译期间界面暂不可交互, 可接受)
        p.WaitForExit();
#else
        await p.WaitForExitAsync();
#endif
        return p.ExitCode;
    }

    async Task DownloadChangelogFromMirror(string destDir)
    {
        try
        {
            // v2.12 修复: 启用镜像仓库时总是拉取镜像更新日志并覆盖本地 —
            // 旧逻辑"仅本地缺失时下载"导致本地已有日志时镜像日志从不拉取（用户实测确认仍失败）;
            // 镜像日志由开发者每次更新时上传, 以镜像为准覆盖本地, 保证本地日志与镜像保持一致。
            var dest = Path.Combine(destDir, "更新日志.txt");
            var destDirInfo = new DirectoryInfo(destDir);
            if (!destDirInfo.Exists) return;

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.Add("User-Agent", "ServerUI-AUM");

            // v2.13: S4A21 镜像日志统一用 S4A21更新日志.txt，与 S4A12-AUM 上传的
            // 更新日志.txt 区分开，避免两个工具共用镜像仓库互相覆盖。
            // 拉取顺序: Gitee(国内直连优先) → GitHub → Codeberg(兜底) — 很多人连不上 GitHub，
            // 国内用户 Gitee 最快，故 Gitee 排第一。
            var urls = new[] {
                UpdateService.MirrorGiteeRaw    + "/mirrors/S4A21%E6%9B%B4%E6%96%B0%E6%97%A5%E5%BF%97.txt",
                UpdateService.MirrorGitHubRaw   + "/mirrors/S4A21%E6%9B%B4%E6%96%B0%E6%97%A5%E5%BF%97.txt",
                UpdateService.MirrorCodebergRaw + "/mirrors/S4A21%E6%9B%B4%E6%96%B0%E6%97%A5%E5%BF%97.txt"
            };

            foreach (var url in urls)
            {
                try
                {
                    var data = await client.GetByteArrayAsync(url);
                    if (data.Length > 100)
                    {
                        File.WriteAllBytes(dest, data);
                        OutputReceived?.Invoke($"[AUM更新] 镜像更新日志已拉取: {data.Length}B → {dest}");
                        return;
                    }
                }
                catch { }
            }
            OutputReceived?.Invoke("[AUM更新] 镜像更新日志拉取失败（三个镜像均不可达或日志为空）");
        }
        catch { }
    }

    static void SyncRootFiles(string repoRoot, string userRoot)
    {
        // .txt / .md → userRoot (UTF-8 文本，直接复制)
        foreach (var pattern in new[] { "*.txt", "*.md" })
        {
            foreach (var f in Directory.GetFiles(repoRoot, pattern))
            {
                var name = Path.GetFileName(f);
                // v2.03: 更新日志.txt 由服务端更新流程维护(含提交记录), 仓库根目录同步跳过,
                // 避免 AUM 更新把旧日志覆盖到本地导致版本回退
                if (name.Contains("GameLog") || name.Contains("运行日志") || name.Contains("本地游戏S4")
                    || name == "更新日志.txt") continue;
                var dest = Path.Combine(userRoot, name);
                var srcInfo = new FileInfo(f);

                if (File.Exists(dest))
                {
                    var dstInfo = new FileInfo(dest);
                    if (srcInfo.Length == dstInfo.Length
                        && srcInfo.LastWriteTimeUtc == dstInfo.LastWriteTimeUtc)
                        continue;
                    if (FileHash(f) == FileHash(dest))
                        continue;
                }
                File.Copy(f, dest, true);
            }
        }

        // .bat → userRoot (cmd 按 ANSI/GBK 或 chcp 65001 执行)
        // 源 .bat 已被 U+FFFD 损坏(锟斤拷)时绝不覆盖本地文件，等仓库修复后再同步
        foreach (var f in Directory.GetFiles(repoRoot, "*.bat"))
        {
            var name = Path.GetFileName(f);
            if (name.Contains("GameLog") || name.Contains("运行日志") || name.Contains("本地游戏S4")) continue;
            var dest = Path.Combine(userRoot, name);
            var srcInfo = new FileInfo(f);

            if (CountFFFD(File.ReadAllBytes(f)) > 0) continue;

            if (File.Exists(dest))
            {
                var dstInfo = new FileInfo(dest);
                if (srcInfo.Length == dstInfo.Length
                    && srcInfo.LastWriteTimeUtc == dstInfo.LastWriteTimeUtc)
                    continue;
                if (FileHash(f) == FileHash(dest))
                    continue;
            }
            File.Copy(f, dest, true);
        }

        // .ps1 → userRoot\ps1核心\ (全部放入 ps1核心 子目录)
        var ps1Dir = Path.Combine(userRoot, "ps1核心");
        Directory.CreateDirectory(ps1Dir);
        foreach (var f in Directory.GetFiles(repoRoot, "*.ps1"))
        {
            var name = Path.GetFileName(f);
            var dest = Path.Combine(ps1Dir, name);
            var srcInfo = new FileInfo(f);

            if (File.Exists(dest))
            {
                var dstInfo = new FileInfo(dest);
                if (srcInfo.Length == dstInfo.Length
                    && srcInfo.LastWriteTimeUtc == dstInfo.LastWriteTimeUtc)
                    continue;
                if (FileHash(f) == FileHash(dest))
                    continue;
            }
            File.Copy(f, dest, true);
        }
    }

    /// <summary>
    /// 更新AUM完成后执行文件重组：
    ///   1. 确保 ps1核心 目录存在, 将根目录下 .ps1 全部移入
    ///   2. 更新所有 .bat 中对 .ps1 的引用路径 → ps1核心\
    ///   3. 清理游戏根目录下的冗余 .bat/.ps1 文件
    /// </summary>
    static void ReorganizeScripts(string aumDir)
    {
        try
        {
            var ps1Dir = Path.Combine(aumDir, "ps1核心");
            Directory.CreateDirectory(ps1Dir);

            // 1. 将 AUM管理组件 根目录下散落的 .ps1 移入 ps1核心
            foreach (var f in Directory.GetFiles(aumDir, "*.ps1"))
            {
                var name = Path.GetFileName(f);
                var dest = Path.Combine(ps1Dir, name);
                if (!File.Exists(dest))
                {
                    File.Move(f, dest);
                }
                else
                {
                    if (FileHash(f) != FileHash(dest))
                        File.Copy(f, dest, true);
                    File.Delete(f);
                }
            }

            // 2. 更新所有 .bat 文件中 .ps1 引用路径
            foreach (var f in Directory.GetFiles(aumDir, "*.bat"))
            {
                UpdateBatReference(f);
            }

            // 3. 清理游戏根目录（AUM管理组件的父目录）下的冗余 .bat/.ps1
            var gameRoot = Directory.GetParent(aumDir)?.FullName;
            if (gameRoot != null && gameRoot != aumDir)
            {
                foreach (var f in Directory.GetFiles(gameRoot, "*.bat"))
                {
                    var name = Path.GetFileName(f);
                    if (name.Contains("GameLog") || name.Contains("运行日志") || name.Contains("DNF") || name.Contains("本地游戏S4")) continue;
                    if (File.Exists(Path.Combine(aumDir, name)))
                    {
                        try { File.Delete(f); } catch { }
                    }
                }
                foreach (var f in Directory.GetFiles(gameRoot, "*.ps1"))
                {
                    try { File.Delete(f); } catch { }
                }
            }
        }
        catch { }
    }

    // =================================================================
    // .bat 编码安全工具
    // cmd.exe 在中文 Windows 默认按 ANSI(GBK) 读取 .bat（带 chcp 65001 的按 UTF-8 读取）。
    // 任何 "按 UTF-8 解码 → 改写 → 按 UTF-8 写回" 的往返都会把 GBK 文件损坏成 "锟斤拷"，
    // 因此这里全程按字节操作，绝不进行编码转换。
    // =================================================================

    // "ps1核心\" 的 GBK 字节（中文 Windows 默认 ANSI 代码页 936）
    static readonly byte[] CoreDirGbk = { 0x70, 0x73, 0x31, 0xBA, 0xCB, 0xD0, 0xC4, 0x5C };
    // "ps1核心\" 的 UTF-8 字节（chcp 65001 的 .bat）
    static readonly byte[] CoreDirUtf8 = { 0x70, 0x73, 0x31, 0xE6, 0xA0, 0xB8, 0xE5, 0xBF, 0x83, 0x5C };

    /// <summary>统计文件中 U+FFFD 替换符(EF BF BD)的数量 — 出现即代表文件已被"锟斤拷"损坏</summary>
    static int CountFFFD(byte[] bytes)
    {
        int n = 0;
        for (int i = 0; i + 2 < bytes.Length; i++)
            if (bytes[i] == 0xEF && bytes[i + 1] == 0xBF && bytes[i + 2] == 0xBD)
                n++;
        return n;
    }

    /// <summary>在字节数组中查找 ASCII 子串（大小写不敏感）</summary>
    static bool ContainsAsciiBytes(byte[] hay, string ascii)
    {
        var needle = Encoding.ASCII.GetBytes(ascii);
        if (needle.Length == 0 || needle.Length > hay.Length) return false;
        int last = hay.Length - needle.Length;
        for (int i = 0; i <= last; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
            {
                var b = hay[i + j];
                var a = needle[j];
                if (b >= 0x41 && b <= 0x5A) b |= 0x20;
                if (a >= 0x41 && a <= 0x5A) a |= 0x20;
                if (b != a) { ok = false; break; }
            }
            if (ok) return true;
        }
        return false;
    }

    /// <summary>在字节数组中查找子串位置，找不到返回 -1</summary>
    static int IndexOfBytes(byte[] hay, byte[] needle, int start)
    {
        if (needle.Length == 0 || needle.Length > hay.Length - start) return -1;
        int last = hay.Length - needle.Length;
        for (int i = start; i <= last; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
                if (hay[i + j] != needle[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    /// <summary>字节数组是否可在严格 UTF-8 下解码（是 → chcp 65001 的 UTF-8 文件；否 → ANSI/GBK）</summary>
    static bool IsStrictUtf8(byte[] bytes)
    {
        try { new UTF8Encoding(false, true).GetString(bytes); return true; }
        catch { return false; }
    }

    /// <summary>判断 .bat 应使用哪种编码插入 "ps1核心\"：UTF-8 或 ANSI(GBK)</summary>
    static bool UseUtf8ForInsert(byte[] bytes)
    {
        // 显式声明 chcp 65001 (UTF-8 代码页) → 按 UTF-8
        if (ContainsAsciiBytes(bytes, "chcp 65001")) return true;
        // 含非 ASCII 字节且可严格按 UTF-8 解码 → UTF-8 文件
        bool hasHigh = false;
        foreach (var b in bytes)
            if (b >= 0x80) { hasHigh = true; break; }
        if (hasHigh) return IsStrictUtf8(bytes);
        // 纯 ASCII 的 .bat → 中文 Windows 上 cmd 默认按 ANSI(GBK) 执行，按 GBK 处理
        return false;
    }

    /// <summary>
    /// 将 .bat 文件中 %~dp0xxx.ps1 或 %BASE%xxx.ps1 更新为 %~dp0ps1核心\xxx.ps1
    /// v1.920: 改为纯字节操作 — 不再做 UTF-8 解码/写回，GBK 与 UTF-8 两种 .bat 都不会被损坏
    /// </summary>
    static void UpdateBatReference(string batPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(batPath);

            // 已被 U+FFFD 损坏(锟斤拷)的文件无法恢复，跳过
            if (CountFFFD(bytes) > 0) return;

            // 不含 .ps1 引用 → 无需处理
            if (!ContainsAsciiBytes(bytes, ".ps1")) return;

            // 已包含 "ps1核心" 引用(GBK 或 UTF-8 字节序) → 跳过
            if (IndexOfBytes(bytes, CoreDirGbk, 0) >= 0
                || IndexOfBytes(bytes, CoreDirUtf8, 0) >= 0)
                return;

            // 判断编码: chcp 65001 / 非 ASCII 且严格 UTF-8 → 按 UTF-8 插入; 否则按 ANSI(GBK) 插入
            var core = UseUtf8ForInsert(bytes) ? CoreDirUtf8 : CoreDirGbk;

            // 在 %~dp0 / %BASE% 标记之后插入 "ps1核心\"（处理全部匹配，与旧正则行为一致）
            var result = new System.Collections.Generic.List<byte>(bytes.Length + 32);
            result.AddRange(bytes);
            int inserted = 0;
            for (int i = 0; i + 4 < bytes.Length; i++)
            {
                bool dp0 = ContainsAsciiBytesAt(bytes, i, "%~dp0");
                bool baseMark = ContainsAsciiBytesAt(bytes, i, "%BASE%");
                if (!dp0 && !baseMark) continue;

                int markEnd = dp0 ? i + 5 : i + 6;

                // 标记之后到 .ps1 之间不得出现引号/反斜杠/空白（与旧正则 [^"'\\\s] 一致）
                int ps1 = -1;
                bool clean = true;
                for (int j = markEnd; j + 4 <= bytes.Length; j++)
                {
                    if (bytes[j] == '.' && bytes[j + 1] == 'p'
                        && bytes[j + 2] == 's' && bytes[j + 3] == '1')
                    { ps1 = j; break; }
                    var b = bytes[j];
                    if (b == '"' || b == '\'' || b == '\\' || b == ' '
                        || b == '\t' || b == '\r' || b == '\n' || b == '\f' || b == '\v')
                    { clean = false; break; }
                }
                if (!clean || ps1 < 0) continue;

                result.InsertRange(markEnd + inserted, core);
                inserted += core.Length;
            }
            File.WriteAllBytes(batPath, result.ToArray());
        }
        catch { }
    }

    /// <summary>判断字节数组指定位置是否匹配 ASCII 子串（大小写不敏感）</summary>
    static bool ContainsAsciiBytesAt(byte[] bytes, int i, string ascii)
    {
        if (i + ascii.Length > bytes.Length) return false;
        for (int j = 0; j < ascii.Length; j++)
        {
            var b = bytes[i + j];
            var a = (byte)ascii[j];
            if (b >= 0x41 && b <= 0x5A) b |= 0x20;
            if (a >= 0x41 && a <= 0x5A) a |= 0x20;
            if (b != a) return false;
        }
        return true;
    }

    static string FileHash(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var fs = File.OpenRead(path);
        var hash = sha.ComputeHash(fs);
        return BitConverter.ToString(hash);
    }

    static void CleanDuplicates(string dir)
    {
        var subDirs = new[] { "Services", "Models" };
        foreach (var sub in subDirs)
        {
            var subPath = Path.Combine(dir, sub);
            if (!Directory.Exists(subPath)) continue;
            foreach (var f in Directory.GetFiles(subPath, "*.cs"))
            {
                var name = Path.GetFileName(f);
                var rootPath = Path.Combine(dir, name);
                if (File.Exists(rootPath))
                {
                    File.Delete(rootPath);
                }
            }
        }
    }

    /// <summary>
    /// 整体覆盖同步仓库包 → AUM管理组件 (v2.031)
    /// 仓库 zip 即完整仓库: 根目录文件 + 除 ServerS4A21-AUM(服务端) 外的
    /// 所有一级子目录 (实用工具包 / DX11运行 / DX12运行 / dfogmtool / latest / ps1核心 等)
    /// 全部覆盖同步, 用户新增的资源文件夹随更新自动带下来
    /// </summary>
    static void SyncRepoToAumDir(string repoRoot, string aumDir)
    {
        // 根目录文件 (跳过更新日志, 由服务端更新流程维护, 避免版本回退)
        foreach (var f in Directory.GetFiles(repoRoot))
        {
            var name = Path.GetFileName(f);
            if (name.Contains("更新日志") || name.Contains("GameLog") || name.Contains("运行日志")) continue;
            try { File.Copy(f, Path.Combine(aumDir, name), true); } catch { }
        }

        // 一级子目录 (跳过 ServerUI 源码 / ServerS4A21-AUM 服务端 / 版本控制目录)
        foreach (var dir in Directory.GetDirectories(repoRoot))
        {
            var name = Path.GetFileName(dir);
            if (string.Equals(name, "ServerUI", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(name, "ServerS4A21-AUM", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(aumDir, name);
            try
            {
                Directory.CreateDirectory(target);
                SyncDirectory(dir, target);
            }
            catch { }
        }
    }

    static void SyncDirectory(string src, string dst)
    {
        foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = f.Substring(src.Length).TrimStart(Path.DirectorySeparatorChar);
            if (rel.StartsWith("bin" + Path.DirectorySeparatorChar) ||
                rel.StartsWith("obj" + Path.DirectorySeparatorChar) ||
                rel.EndsWith("FodyWeavers.xsd", StringComparison.OrdinalIgnoreCase))
                continue;
            var target = Path.Combine(dst, rel);
            var td = Path.GetDirectoryName(target);
            if (!Directory.Exists(td))
                Directory.CreateDirectory(td!);
            File.Copy(f, target, true);
        }
    }
}
