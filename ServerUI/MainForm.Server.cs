/*
 * ==================================================================
 * MainForm 服务端控制与更新部分 (partial class)
 * 包含: 定时器 / 窗口关闭清理 / 启动停止 / 日志输出
 *       系统检测 / DX补丁 / SDK安装 / 网络检测 / AUM自更新
 *       状态刷新 / 增量全量更新编排
 * ==================================================================
 */
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Microsoft.VisualBasic;
using AntdUI;
using ServerUI.Models;
using ServerUI.Services;

namespace ServerUI;

public partial class MainForm : AntdUI.Window
{
    // =================================================================
    // 定时器初始化 (Ti)
    // =================================================================
    void Ti()
    {
        _pt = new Timer { Interval = 500 };
        _pt.Tick += (s, e) =>
        {
            // ===== 伪进度算法: 进度条永不卡死 =====
            // 1. 收到真实阶段标记(_stepTarget 前进) → 大步追赶, 制造"刚完成一个阶段"的跳跃感
            // 2. 追平目标但更新未结束 → 缓慢蠕动逼近 90 (剩余量按比例递减, 永不完成)
            // 3. 超过 90 → 极慢蠕动 + 在 94~95 区间轻微"呼吸"波动, 让用户知道仍在工作
            // 真实进度标记(##PROGRESS##/[N/5])仍会驱动 _stepTarget, 完成时 OD 直接跳 100%
            if (_pv < _stepTarget)
            {
                var gap = _stepTarget - _pv;
                _pv += Math.Max(1.5f, gap * 0.35f);
                if (_pv > _stepTarget) _pv = _stepTarget;
            }
            else if (_pv < 90)
            {
                var rem = 90 - _pv;
                _pv += Math.Max(0.3f, rem * 0.02f);
            }
            else
            {
                var rem = 95 - _pv;
                if (rem > 1f)
                    _pv += Math.Max(0.15f, rem * 0.025f);   // 90→94: 约15秒到达, 持续可见推进
                else
                    _pv = 94.2f + (float)(Math.Sin(Environment.TickCount / 400.0) + 1) * 0.4f; // 94.2~95.0 呼吸
            }
            pb.Value = Math.Min(_pv, 100f) / 100f;   // AntdUI Progress.Value 为 0-1 比率
            lbPg.Text = "更新进度: " + (int)_pv + "%";
        };

        _ct = new Timer { Interval = 3000 };
        _ct.Tick += (s, e) =>
        {
            if (_sv.IsRunning)
            {
                Lg(">>> 确认服务端进程已启动", Gn);
                _ct.Stop();
            }
        };

        _st = new Timer { Interval = 2000 };
        _st.Tick += (s, e) => Rs();
        _st.Start();

        // 日志攒批渲染定时器 (30ms): UI 线程统一刷新日志, 避免逐行封送/重绘
        _logFlush = new Timer { Interval = 30 };
        _logFlush.Tick += (s, e) => FlushLog();
        _logFlush.Start();
    }

    // =================================================================
    // 核心事件处理
    // =================================================================

    /*
     * 窗口关闭事件 (Fc) — 确认后清理所有相关进程并保存日志
     */
    void Fc(object s, FormClosingEventArgs e)
    {
        var r = MessageBox.Show(
            "退出本程序之后会自动关闭正在运行的服务端和GM工具，是否确认？",
            "确认退出", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (r == DialogResult.Yes)
        {
            _up.CancelUpdate();
            Lg(">>> 正在关闭所有相关进程...", Color.Gold);
            _sv.Stop();
            try
            {
                foreach (var p in Process.GetProcessesByName("DfoGmTool"))
                { try { p.Kill(); } catch { } }
            }
            catch { }
            _st.Stop(); _pt.Stop(); _ct.Stop();
            if (_logFlush != null) { _logFlush.Stop(); FlushLog(); }
            Lg(">>> 已清理所有进程", Gn);

            SaveRunningLog();
            CleanCompileCache();
            CleanUpdateTemp();
        }
        else e.Cancel = true;
    }

    void SaveRunningLog()
    {
        try
        {
            var logPath = Path.Combine(_ad, "运行日志.txt");
            File.WriteAllText(logPath, _logBuilder.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Lg(">>> 保存运行日志失败: " + ex.Message, Rd);
        }
    }

    void CleanCompileCache()
    {
        var cacheDirs = new[]
        {
            Path.Combine(_ad, "ServerS4A21-AUM", "Server", "DfoServer", "obj"),
            Path.Combine(_ad, "ServerS4A21-AUM", "Server", "DfoServer", "bin"),
            Path.Combine(_ad, "dfogmtool", "obj"),
            Path.Combine(_ad, "dfogmtool", "bin")
        };

        foreach (var dir in cacheDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                    Lg(">>> 已清理编译缓存: " + Path.GetFileName(dir), Gn);
                }
            }
            catch (Exception ex)
            {
                Lg(">>> 清理缓存失败 (" + dir + "): " + ex.Message, Or);
            }
        }
    }

    void CleanUpdateTemp()
    {
        var pattern = "ServerS4A21-*";
        try
        {
            foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), pattern))
            {
                try { Directory.Delete(dir, true); } catch { }
            }
            foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), "ServerUI-AUM-update*"))
            {
                try { Directory.Delete(dir, true); } catch { }
            }
            Lg(">>> 已清理更新缓存目录", Gn);
        }
        catch (Exception ex)
        {
            Lg(">>> 清理更新缓存失败: " + ex.Message, Or);
        }
    }

    /*
     * 启动服务端 (Go) — 定位 start-server.bat 并启动，同时开始 3 秒后的存活检测
     */
    void Go()
    {
        _sv.Start(Path.Combine(_ad, "ServerS4A21-AUM"));
        _ct.Start();
    }

    /*
     * 开始游戏完整流程 (Play)
     * 步骤: 启动 start-server.bat → 10秒后隐藏bat窗口 → 5秒后确认DfoServer
     *       → 15秒后隐藏DfoServer窗口 → 启动游戏客户端
     */
    internal async System.Threading.Tasks.Task Play()
    {
        Lg(">>> 正在启动 start-server.bat...", Color.CornflowerBlue);
        Go();

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(10000);
            Invoke(new Action(() =>
            { try { _sv.HideConsoleWindow(); } catch { } }));
        });

        await System.Threading.Tasks.Task.Delay(5000);

        if (_sv.IsRunning)
        {
            Lg(">>> 服务端进程存活，正在启动游戏...", Gn);
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(15000);
                Invoke(new Action(() =>
                { try { ServerService.HideDfoServerWindow(); } catch { } }));
            });
        }
        else
            Lg(">>> 警告: 服务端可能未成功启动", Or);

        var p = Directory.GetParent(_bd);
        var bat = p != null
            ? Path.Combine(p.FullName, "本地游戏S4.bat")
            : "";
        if (File.Exists(bat))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = bat,
                WorkingDirectory = p.FullName,
                UseShellExecute = true
            });
            Lg(">>> 已打开本地游戏S4.bat", Gn);
        }
        else
        {
            var fb = Path.Combine(_ad, "单机游戏启动.bat");
            if (File.Exists(fb))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = fb,
                    WorkingDirectory = _ad,
                    UseShellExecute = true
                });
                Lg(">>> 已打开单机游戏启动.bat", Gn);
            }
            else
                Lg(">>> 本地游戏S4.bat / 单机游戏启动.bat 未找到!",
                    Rd);
        }
    }

    /*
     * 日志输出 (Lg) — [HH:mm:ss] 消息内容，线程安全
     * v2.02 优化: 只做线程安全入队, 渲染交给 UI 线程的 FlushLog 定时器,
     * 跨线程调用不再逐行 Invoke 封送, 更新/镜像大量输出时窗口不卡顿
     */
    const int LogMaxChars = 200_000;
    const int LogKeepChars = 150_000;

    internal void Lg(string m) => Lg(m, Color.FromArgb(240, 240, 245));
    internal void Lg(string m, Color c) => Lg(m, c, false);

    /*
     * 日志输出 (Lg) — 线程安全入队, 不做任何 UI 操作
     * 经典模式(ClassicForm)激活时日志实时转发到经典窗口 (其内部自行封送)
     */
    internal void Lg(string m, Color c, bool bold)
    {
        lock (_logQueue) _logQueue.Enqueue((m, c, bold));
        if (LogHook != null)
        {
            try { LogHook(m, c); } catch { }
        }
    }

    /*
     * 日志批量渲染 (FlushLog) — 由 30ms 定时器在 UI 线程执行
     * 一次批处理内连续 AppendText 合并为单次绘制; 仅底部时自动跟随
     * v2.03: 单批最多 200 行, 超量自动分片到下一次, 避免单次大批量 RTF 解析卡顿
     */
    const int LogFlushMaxLines = 200;

    void FlushLog()
    {
        if (rt == null || rt.IsDisposed) return;
        List<(string m, Color c, bool bold)> batch;
        lock (_logQueue)
        {
            if (_logQueue.Count == 0) return;
            var n = Math.Min(LogFlushMaxLines, _logQueue.Count);
            batch = new List<(string, Color, bool)>(n);
            for (int i = 0; i < n; i++) batch.Add(_logQueue.Dequeue());
        }

        bool follow = IsLogAtBottom();
        try
        {
            foreach (var (m, c, bold) in batch)
            {
                var ts = "[" + DateTime.Now.ToString("HH:mm:ss") + "] ";
                _logBuilder.AppendLine(ts + m);
                rt.SelectionStart = rt.TextLength;
                rt.SelectionLength = 0;
                rt.SelectionFont = rt.Font;
                rt.SelectionColor = Color.FromArgb(240, 240, 245);
                rt.AppendText(ts);
                if (bold) rt.SelectionFont = new Font(rt.Font, FontStyle.Bold);
                rt.SelectionColor = c;
                rt.AppendText(m + "\n");
            }
            if (rt.TextLength > LogMaxChars) TrimLog(LogKeepChars);
            if (follow) rt.ScrollToCaret();
        }
        catch { }
    }

    /*
     * 判断日志滚动条是否位于底部 (IsLogAtBottom)
     * 最后一行字符在可视区内 → 位于底部, 可自动跟随
     */
    bool IsLogAtBottom()
    {
        if (rt == null || rt.TextLength == 0) return true;
        try
        {
            var pos = rt.GetPositionFromCharIndex(rt.TextLength - 1);
            return pos.Y >= 0 && pos.Y < rt.ClientSize.Height - 8;
        }
        catch { return true; }
    }

    /*
     * 截断日志 (TrimLog) — 丢弃最早的日志, 只保留最近 keep 个字符
     * RichTextBox 只读模式下 SelectedText 赋值无效, 需临时解除只读
     */
    void TrimLog(int keep)
    {
        var cut = rt.TextLength - keep;
        if (cut <= 0) return;
        try
        {
            rt.ReadOnly = false;
            rt.Select(0, cut);
            rt.SelectedText = "";
            rt.ReadOnly = true;
        }
        catch { }
    }
    void LS(string m) => Lg(m, Gn);

    // =================================================================
    // 存档操作
    // =================================================================

    /*
     * 导入存档 (IA) — 选择 ZIP 压缩包，解压覆盖到主存档目录
     */
    /*
     * 启动时执行系统环境检测: Windows 版本 / 便携版 / .NET SDK 三级检测
     * v2.03: SDK 三级探测(多次启动进程)移到后台线程, 首帧立即显示不卡顿
     */
    async System.Threading.Tasks.Task Ck()
    {
        Lg("ServerUI 版本: " + VER, Color.DarkOrange);

        // ---- 检测便携版/有依赖版 ----
        var exePath = Compat.ExePath();
        bool isPortable = !string.IsNullOrEmpty(exePath)
            && File.Exists(exePath)
            && new FileInfo(exePath).Length > 50_000_000;

        // ---- 检测 Windows 版本 ----
        var osVer = Environment.OSVersion;
        bool isWin10Plus = osVer.Platform == PlatformID.Win32NT
            && osVer.Version.Major >= 10;
        if (!isWin10Plus)
            Lg("系统版本低于 Windows 10，可能会出现兼容性问题，"
                + "建议升级到 Win10 或更高版本", Or);
        else
        {
            var winVer = osVer.Version.Build >= 22000 ? "11" : "10";
            Lg("系统版本: Windows " + winVer
                + " (Build " + osVer.Version.Build + ")", Color.FromArgb(176, 176, 184));
        }

        if (isPortable)
            Lg("本版本为便携版（无依赖版），已内置 .NET 10 运行环境",
                Gn);
        else
            Lg("本版本为有依赖版，需要系统安装 .NET 10 运行环境才能运行",
                Color.FromArgb(240, 240, 245));

        // ---- 三级 .NET SDK 检测 (后台线程执行, 避免首帧卡顿) ----
        var r = await System.Threading.Tasks.Task.Run(() => ProbeDotNetSdk());
        lbSd.Text = ".NET SDK: " + r.sdk;
        lbSd.ForeColor = r.color;
        _hasSdk = r.sysOk || r.pfOk || r.localOk;

        if (r.sysOk || r.pfOk)
            Lg("检测到系统已安装 .NET 10 SDK，可用于编译服务端更新",
                Gn);
        else if (r.localOk)
            Lg("检测到本地便携 .NET SDK (dotnet-sdk)，可用于编译服务端更新",
                Gn);
        else if (isPortable)
        {
            Lg("未检测到 .NET 10 SDK，虽然本程序可运行，"
                + "但更新时无法编译服务端！", Rd);
            Lg("请将 dotnet-sdk 目录放入 AUM管理组件，"
                + "或手动安装 .NET 10 SDK", Rd);
        }
        else
        {
            Lg("未检测到 .NET 10 运行环境，本程序可能无法正常工作！",
                Rd);
            Lg("请安装 .NET 10.0 或改用便携版"
                + " (ServerUI-无依赖版.exe) 后重试", Rd);
        }
    }

    /*
     * .NET SDK 三级检测 (后台线程执行, Lg 线程安全可直接调用)
     */
    (string sdk, Color color, bool sysOk, bool pfOk, bool localOk) ProbeDotNetSdk()
    {
        string sdk = "未安装";
        Color c = Rd;
        bool sysOk = false, pfOk = false, localOk = false;

        // 第一级: 系统 PATH 中的 dotnet
        try
        {
            var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            p.Start();
            var v = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            if (p.ExitCode == 0 && !string.IsNullOrEmpty(v))
            {
                if (v.StartsWith("10."))
                {
                    sdk = "已就绪 v" + v; c = Gn; sysOk = true;
                }
                else
                    Lg("系统已安装 .NET v" + v
                        + "，但需要 ≥10.0 版本", Or);
            }
        }
        catch { }

        // 第二级: Program Files\dotnet (常见安装位置, x64)
        if (!sysOk)
        {
            var pfPaths = new[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet", "dotnet.exe")
            };
            foreach (var pfPath in pfPaths)
            {
                if (!File.Exists(pfPath)) continue;
                try
                {
                    var p = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = pfPath,
                            Arguments = "--version",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            CreateNoWindow = true
                        }
                    };
                    p.Start();
                    var v = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit();
                    if (p.ExitCode == 0 && !string.IsNullOrEmpty(v)
                        && v.StartsWith("10."))
                    {
                        sdk = "已就绪 v" + v + " (Program Files)";
                        c = Gn; pfOk = true; break;
                    }
                }
                catch { }
            }
        }

        // 第三级: 本地 dotnet-sdk 目录 (便携 SDK)
        if (!sysOk && !pfOk)
        {
            var localPath = Path.Combine(_ad, "dotnet-sdk",
                "dotnet.exe");
            if (File.Exists(localPath))
            {
                try
                {
                    var p = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = localPath,
                            Arguments = "--version",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            CreateNoWindow = true
                        }
                    };
                    p.Start();
                    var v = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit();
                    if (p.ExitCode == 0 && !string.IsNullOrEmpty(v)
                        && v.StartsWith("10."))
                    {
                        sdk = "便携SDK v" + v;
                        c = Or; localOk = true;
                    }
                }
                catch { }
                if (!localOk)
                {
                    sdk = "便携SDK (版本异常)";
                    c = Or; localOk = true;
                }
            }
        }

        return (sdk, c, sysOk, pfOk, localOk);
    }

    // =================================================================
    // DX 补丁处理 (ApplyDx)
    // =================================================================
    /*
     * 处理运行方式选择 (默认DX9 / DX11 / DX12) 与去水印选项
     *   - 未勾选 (SelectIndex=-1): 默认DX9运行, 将 DX 补丁从游戏目录移除
     *   - DX11/DX12: 直接覆盖复制对应补丁 (切换时总是覆盖, 不检查已存在)
     */
    void ApplyDx()
    {
        if (!_dxReady) return;   // 初始化阶段的 SelectIndex 赋值不触发补丁操作

        var sel = sgDx.SelectIndex;
        var dx11 = sel == 0;
        var dx12 = sel == 1;
        var dx9 = sel == -1;     // 无勾选 = 默认 DX9

        // 提示: 可选运行方式仅为兼容性选项, 如游戏无法正常游玩请取消
        if (dx11 || dx12)
            Lg(">>> 提示: 如果游戏无法正常游玩，请取消选择相关可选运行方式", Or);

        var files = new[] { "D3D9.dll", "dgVoodoo.conf",
            "dgVoodooCpl.exe" };
        string srcDir = null;
        string tag = "";

        // 确定补丁来源目录
        if (dx12)
        {
            srcDir = Path.Combine(_ad, "DX12补丁");
            tag = " (DX12)";
            if (!Directory.Exists(srcDir))
            {
                Lg("DX12补丁目录不存在: " + srcDir, Or);
                return;
            }
            if (cbDw.Checked)
            {
                var wm = Path.Combine(srcDir, "无水印");
                if (Directory.Exists(wm))
                { srcDir = wm; tag = " (DX12无水印版)"; }
                else
                    Lg("DX12无水印目录不存在", Or);
            }
        }
        else if (dx11)
        {
            srcDir = Path.Combine(_ad, "DX11补丁");
            tag = " (DX11)";
            if (!Directory.Exists(srcDir))
            {
                Lg("DX11补丁目录不存在: " + srcDir, Or);
                return;
            }
            if (cbDw.Checked)
            {
                var wm = Path.Combine(srcDir, "无水印");
                if (Directory.Exists(wm))
                { srcDir = wm; tag = " (DX11无水印版)"; }
                else
                    Lg("DX11无水印目录不存在", Or);
            }
        }
        else if (cbDw.Checked)
        {
            Lg("请先选择 DX11 或 DX12 运行模式再启用水印", Or);
            return;
        }

        // 复制补丁文件到游戏目录 — 总是直接覆盖, 切换 DX11/DX12 时立即生效
        if (srcDir != null)
        {
            int copied = 0;
            foreach (var fn in files)
            {
                var src = Path.Combine(srcDir, fn);
                var dst = Path.Combine(_gr, fn);
                if (File.Exists(src))
                {
                    File.Copy(src, dst, true);
                    copied++;
                }
            }
            Lg("DX补丁已复制到游戏目录" + tag + " (" + copied + " 个文件)", Gn);
        }
        else
        {
            // 默认DX9 → 从游戏目录删除补丁文件
            int removed = 0;
            foreach (var fn in files)
            {
                var dst = Path.Combine(_gr, fn);
                if (File.Exists(dst))
                { try { File.Delete(dst); removed++; } catch { } }
            }
            Lg("已切换为默认DX9运行，DX补丁已从游戏目录移除 (" + removed + " 个文件)", Txt2);
        }
    }

    // =================================================================
    // .NET SDK 安装 (IS)
    // =================================================================
    /*
     * 通过 dotnet-sdk 安装程序自动下载安装 .NET 10 SDK
     * 安装位置: AUM管理组件\dotnet-sdk\
     */
    internal async System.Threading.Tasks.Task IS()
    {
        btSdk.Enabled = false;
        btSdk.Text = "检测中...";

        var installer = Path.Combine(_ad, "dotnet-sdk",
            "dotnet-sdk-10.0.302-win-x64.exe");

        if (!File.Exists(installer))
        {
            if (_hasSdk)
                Lg(".NET 10 SDK 已就绪，但未找到安装包: " + installer, Or);
            else
                Lg("未找到 .NET 10 SDK 安装程序: " + installer, Rd);
            Lg("请自行下载 .NET 10.0 SDK (x64) 安装包放入 dotnet-sdk 目录，或运行 dotnet-install.ps1。", Rd);
            btSdk.Enabled = true;
            btSdk.Text = "安装NET.10 SDK";
            return;
        }

        if (_hasSdk)
            Lg(".NET 10 SDK 已就绪，仍将打开安装包供你手动修复/覆盖安装。", Or);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = installer,
                WorkingDirectory = Path.GetDirectoryName(installer),
                UseShellExecute = true
            };
            Process.Start(psi);
            Lg("已打开微软 .NET 10 SDK 安装程序。安装完成后请重启管理器，"
                + "再执行更新。", Gn);
        }
        catch (Exception ex)
        {
            Lg("无法启动 .NET 10 SDK 安装程序: " + ex.Message, Rd);
        }

        btSdk.Enabled = true;
        btSdk.Text = "安装NET.10 SDK";
        await System.Threading.Tasks.Task.CompletedTask;
    }

    void CheckDnfExists()
    {
        var dnfPath = Path.Combine(_gr, "DNF.exe");
        if (!File.Exists(dnfPath))
            // 极其醒目的亮红 + 加粗 (深色日志底上 #FF0000 最醒目)
            Lg("[警告] 本目录下并不存在 DNF.exe，请确认解压位置是否正确。当前目录: " + _gr,
                Color.FromArgb(255, 0, 0), bold: true);
        else
            Lg("[检查] DNF.exe 已找到: " + dnfPath, Gn);
    }

    async System.Threading.Tasks.Task CheckBasicNetwork()
    {
        Lg(">>> 正在检测网络可达性 (网页检测, 无API调用)...", Color.CornflowerBlue);
        try
        {
            var basic = await _up.CheckBasicConnectivityAsync();
            foreach (var kv in basic)
            {
                var name = kv.Key;
                var ms = kv.Value.LatencyMs;
                var reachable = kv.Value.Reachable;
                string tier, msg; Color color;
                if (!reachable)
                {
                    tier = "不可达"; msg = name + " " + tier + " (超时)";
                    color = Rd;
                }
                else if (ms <= 800)
                {
                    tier = "正常"; msg = name + " " + tier + " (延迟 " + ms + " ms)";
                    color = Gn;
                }
                else if (ms <= 3000)
                {
                    tier = "较慢"; msg = name + " " + tier + " (延迟 " + ms + " ms), 建议开启科学上网";
                    color = Or;
                }
                else
                {
                    tier = "极慢"; msg = name + " " + tier + " (延迟 " + ms + " ms), 更新可能失败";
                    color = Rd;
                }
                Lg("[网络] " + msg, color);
            }
        }
        catch { Lg("[网络] 检测异常，不影响正常使用。", Color.FromArgb(176, 176, 184)); }
    }

    async System.Threading.Tasks.Task<bool> CanUpdate()
    {
        Lg(">>> 更新前检查仓库连接...", Color.CornflowerBlue);
        var status = await _up.CheckRepositoryAsync();
        if (!status.Available || status.LatencyMs > 3000)
        {
            var reason = status.Available
                ? "连接延迟极高（" + status.LatencyMs + " ms）"
                : "无法连接（" + status.Detail + "）";
            Lg("[网络降级] 仓库" + reason
                + "。将自动重试并改用源码包同步；建议开启科学上网（梯子）提高成功率。", Or);
        }

        _ = ValidateMirrorTokens();
        Lg(">>> 更新前检测镜像源: Gitee / GitHub / Codeberg ...", Color.CornflowerBlue);
        try
        {
            var mirrors = await _up.CheckMirrorSourcesAsync();
            foreach (var kv in mirrors)
            {
                var ok = kv.Value.Available;
                var ms = kv.Value.LatencyMs;
                var color = ok ? Gn : Or;
                var tag = ok ? "可访问" : "不可达";
                Lg("[镜像] " + kv.Key + " " + tag + " (延迟 " + ms + " ms)", color);
            }
        }
        catch { Lg("[镜像] 检测异常，不影响正常使用。", Color.FromArgb(176, 176, 184)); }

        return true;
    }

    async System.Threading.Tasks.Task CheckAUMUpdate()
    {
        Lg(">>> 正在检测 AUM 管理器更新...", Color.CornflowerBlue);
        _au.OutputReceived += Lg;
        try
        {
            var hasUpdate = await _au.CheckForUpdateAsync(VER);
            if (_au.RemoteVersion == null)
            {
                // 网络失败，OutputReceived 已经输出了日志
            }
            else if (hasUpdate)
            {
                Lg("[AUM自检] 发现新版本 v" + _au.RemoteVersion + "！当前版本 v" + VER + "，请点击顶栏【更新AUM】升级。", Gn);
            }
            else if (_au.CompareVersion(_au.RemoteVersion, VER) < 0)
            {
                Lg("[AUM自检] 当前为开发版 v" + VER + "（高于仓库 v" + _au.RemoteVersion + "），无需更新。", Color.FromArgb(176, 176, 184));
            }
            else
            {
                Lg("[AUM自检] 已是最新版本 v" + VER, Color.FromArgb(176, 176, 184));
            }
        }
        finally { _au.OutputReceived -= Lg; }
    }

    internal async System.Threading.Tasks.Task CheckAndUpdateAUM()
    {
        // v2.03: 更新AUM 前检测残留 PS1 (无残留不弹窗)
        CheckLeftoverPs1Prompt();
        if (!_hasSdk)
        {
            Lg("[AUM更新] 需要 .NET 10 SDK 才能自更新，请先点击【安装NET.10 SDK】安装。", Rd);
            return;
        }

        Lg(">>> 正在连接 GitHub 检测 AUM 管理器更新...", Color.CornflowerBlue);
        var hasUpdate = await _au.CheckForUpdateAsync(VER);

        if (!hasUpdate)
        {
            if (_au.RemoteVersion != null)
            {
                if (_au.CompareVersion(_au.RemoteVersion, VER) < 0)
                {
                    Lg("[AUM更新] 当前已是开发版 v" + VER + "（高于仓库 v" + _au.RemoteVersion + "）。", Color.FromArgb(176, 176, 184));
                    var devR = MessageBox.Show(
                        "当前版本 v" + VER + " 高于仓库版本 v" + _au.RemoteVersion + "（开发版）。\n\n是否强制从仓库拉取源码重新编译？",
                        "AUM自更新", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (devR != DialogResult.Yes) return;
                }
                else
                {
                    Lg("[AUM更新] 已是最新版本 v" + VER + "，是否强制重新编译？", Or);
                    var sameR = MessageBox.Show(
                        "当前已是最新版本 v" + VER + "。\n\n点击【是】强制从仓库拉取源码重新编译（同步最新改动）。\n点击【否】跳过。",
                        "AUM自更新", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (sameR != DialogResult.Yes) return;
                }
            }
            else
            {
                var netR = MessageBox.Show(
                    "无法连接 GitHub 检测版本。\n\n点击【是】仍然尝试从仓库拉取源码编译。\n点击【否】取消。",
                    "AUM自更新", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (netR != DialogResult.Yes) return;
            }
        }
        else
        {
            Lg("[AUM更新] 发现新版本 v" + _au.RemoteVersion + "，当前 v" + VER, Gn);
        }

        Lg("[AUM更新] 开始自动下载源码并编译...", Color.CornflowerBlue);

        _au.OutputReceived += Lg;
        _au.Completed += (ok) =>
        {
            if (ok) Lg("[AUM更新] 编译成功，即将自动重启...", Gn);
            else Lg("[AUM更新] 更新流程中断，可稍后重试。", Or);
        };

        try
        {
            await _au.RunUpdateAsync(Path.Combine(_ad,
                Directory.Exists(Path.Combine(_ad, "ServerUI"))
                    ? "ServerUI"
                    : "."));
        }
        finally
        {
            _au.OutputReceived -= Lg;
        }
    }

    async System.Threading.Tasks.Task TryMirrorUpload()
    {
        if (!_mirrorOk)
        {
            Lg("[镜像] API令牌无法生效，请更新AUM版本。", Rd);
            return;
        }

        await System.Threading.Tasks.Task.Delay(5000);
        if (await _mu.CanReachGitGud())
        {
            Lg("[镜像] 检测到可访问 GitGud，尝试同步镜像...", Color.FromArgb(176, 176, 184));
            Lg("您已进入上传者模式，系统将通过众包镜像将最新包体分发至仓库，以缓解主源仓库的访问压力，帮助网络不佳的用户~您的等待将直接缩短他人的下载时长——愿这份互助，传递温暖。", Gold);
            _mu.OutputReceived += Lg;
            try
            {
                await _mu.RunUploaderAsync(VER, Environment.MachineName);
            }
            catch (Exception ex)
            {
                Lg("[镜像] 同步异常: " + ex.Message, Or);
            }
            finally { _mu.OutputReceived -= Lg; }
        }
        else
        {
            Lg("[镜像] 无法访问 GitGud，跳过镜像上传。", Color.FromArgb(176, 176, 184));
        }
    }

    async System.Threading.Tasks.Task ValidateMirrorTokens()
    {
        try
        {
            var ok = await _mu.ValidateTokensAsync();
            _mirrorOk = ok;
            if (!ok)
                Lg("[令牌检测] API令牌无法生效，请更新AUM版本。已禁用镜像上传。", Rd);
            else
                Lg("[令牌检测] API令牌正常，众包镜像可用。", Color.FromArgb(176, 176, 184));
        }
        catch
        {
            _mirrorOk = false;
            Lg("[令牌检测] API令牌无法生效，请更新AUM版本。", Rd);
        }
    }

    // =================================================================
    // 状态刷新 (Rs / Rf / RA)
    // =================================================================

    /*
     * 状态刷新 (Rs) — 每 2 秒执行一次
     * 检测 bat 进程存活 + DfoServer 进程存活，更新状态卡片/PVF/版本
     */
    void Rs()
    {
        var distDir = Path.Combine(_ad, "ServerS4A21-AUM",
            "dist", "win-x64");
        bool bat = _sv.IsBatRunning;
        bool dfo = ServerService.IsDfoServerRunning(distDir);

        string stText;
        Color stColor;
        if (bat && dfo)
        {
            stText = "● 运行中";
            stColor = Gn;
            _orphanLogged = false;
        }
        else if (!bat && dfo)
        {
            stText = "● 未运行";
            stColor = Rd;
            if (!_orphanLogged)
            {
                _orphanLogged = true;
                System.Threading.Tasks.Task.Run(() =>
                {
                    Lg(">>> 检测到DfoServer残留进程,"
                        + " 正在自动清理...", Or);
                    ServerService.CleanOrphans();
                });
            }
        }
        else
        {
            stText = "● 未运行";
            stColor = Rd;
            _orphanLogged = false;
        }

        lbSt.Text = stText;
        lbSt.ForeColor = stColor;
        lbStHd.Text = stText;
        lbStHd.ForeColor = stColor;

        // 更新 PVF 状态
        var pvfOk = _sv.PvfExists(
            Path.Combine(_ad, "ServerS4A21-AUM"));
        lbPv.Text = pvfOk ? "● 已加载" : "● 未找到";
        lbPv.ForeColor = pvfOk ? Gn : Rd;

        // 更新版本信息 (从更新日志.txt 读取最新版本, 走缓存避免重复 IO)
        var vf = Path.Combine(_ad, "更新日志.txt");
        var tx = _up.GetLogText(_ad);
        if (tx.Length > 0)
        {
            var ix = tx.LastIndexOf("版本:");
            if (ix >= 0)
            {
                var en = tx.IndexOf('\n', ix);
                if (en < 0) en = Math.Min(ix + 20, tx.Length);
                var verText = "上次更新: "
                    + tx.Substring(ix, en - ix).Trim()
                        .Replace("版本:", "").Trim();
                lbLu.Text = verText;
                lbLu.ForeColor = Color.FromArgb(130, 130, 130);
            }
            else
                SetLuDefault();
        }
        else
            SetLuDefault();

        lbVe.Text = "v" + _up.GetVersion(_ad);

        // 广播刷新 — 极简/经典模式窗口复用本次状态检测结果, 不再各自轮询
        try
        {
            if (_miniForm != null && _miniForm.Visible) _miniForm.OnMainTick();
            if (_classicForm != null && _classicForm.Visible) _classicForm.OnMainTick();
        }
        catch { }
    }

    void SetLuDefault()
    {
        var t = "上次更新: 尚未有log日志无法识别版本，请进行更新";
        lbLu.Text = t;
        lbLu.ForeColor = Or;
    }

    /*
     * 刷新一切 (Rf) — 调用 Rs() + RA() 刷新状态和存档列表
     */
    void Rf() { Rs(); RA(); }

    /*
     * 刷新存档列表 (RA)
     * 从切换库加载所有存档文件夹，按修改时间排序 (正序/倒序由 _sa 控制)
     */
    /*
     * 增量更新 (RI) — 停止服务端 → 启动进度条 → 调用 UpdateService.RunIncremental()
     */
    internal async System.Threading.Tasks.Task RI()
    {
        // v2.03: 更新前检测残留 PS1 (无残留不弹窗) + 自动安装安全DLL (已安装则跳过)
        CheckLeftoverPs1Prompt();
        InstallSecurityDll();
        if (!await CanUpdate()) return;
        _ = TryMirrorUpload();
        if (_sv.IsRunning)
        {
            Lg(">>> 检测到服务端正在运行，"
                + "正在自动停止以执行增量更新...", Color.Gold);
            _sv.Stop();
            System.Threading.Thread.Sleep(2000);
            Lg(">>> 服务端已停止，开始更新", Gn);
        }

        if (cbCl.Checked)
        {
            Lg(">>> 更新前清理冗余DB...", Gn);
            CleanRedundantDb();
        }

        pb.Visible = true; lbPg.Visible = true;
        pb.Value = 0; _pv = 0; _stepTarget = 5;
        if (cbSkipLog.Checked)
            Lg(">>> [跳过更新日志] 已启用，本次不拉取仓库提交记录", Or);
        Lg(">>> 开始增量更新 <<<", Color.CornflowerBlue);
        _pt.Start();

        _up.OutputReceived += OU;
        _up.Completed += OD;
        try
        {
            await _up.RunIncremental(
                Path.Combine(_ad, "ServerS4A21-AUM"), _ad, cbSkipLog.Checked, cbMirror.Checked);
        }
        finally
        {
            _up.OutputReceived -= OU;
            _up.Completed -= OD;
            _pt.Stop();
        }
    }

    /*
     * 全量更新 (RF) — 与增量更新流程相同，加上 -FullSync 参数
     */
    internal     async System.Threading.Tasks.Task RF()
    {
        // v2.03: 更新前检测残留 PS1 (无残留不弹窗) + 自动安装安全DLL (已安装则跳过)
        CheckLeftoverPs1Prompt();
        InstallSecurityDll();
        if (!await CanUpdate()) return;
        if (_sv.IsRunning)
        {
            Lg(">>> 检测到服务端正在运行，"
                + "正在自动停止以执行全量更新...", Color.Gold);
            _sv.Stop();
            System.Threading.Thread.Sleep(2000);
            Lg(">>> 服务端已停止，开始更新", Gn);
        }

        if (cbCl.Checked)
        {
            Lg(">>> 更新前清理冗余DB...", Gn);
            CleanRedundantDb();
        }

        pb.Visible = true; lbPg.Visible = true;
        pb.Value = 0; _pv = 0; _stepTarget = 5;
        if (cbSkipLog.Checked)
            Lg(">>> [跳过更新日志] 已启用，本次不拉取仓库提交记录", Or);
        Lg(">>> 开始全量更新 <<<", Color.CornflowerBlue);
        _pt.Start();

        _up.OutputReceived += OU;
        _up.Completed += OD;
        try
        {
            await _up.RunFull(
                Path.Combine(_ad, "ServerS4A21-AUM"), _ad, cbSkipLog.Checked, cbMirror.Checked);
        }
        finally
        {
            _up.OutputReceived -= OU;
            _up.Completed -= OD;
            _pt.Stop();
        }
    }

    /*
     * 更新输出回调 (OU) — 每收到一行 PowerShell 输出时调用
     * 处理 ##PROGRESS## 进度标记 / [FILE:CS] / [FILE:SUM] / 日期行 / [N/5] 步骤
     */
    void OU(string m)
    {
        if (m.StartsWith("##PROGRESS##"))
        {
            var val = m.Substring("##PROGRESS##".Length);
            if (int.TryParse(val, out var pct))
            {
                if (pct > _stepTarget && pct <= 95)
                {
                    _stepTarget = pct;
                    _pv = Math.Max(_pv, pct - 3);   // 贴近真实进度, 剩余交给蠕动
                }
                pb.Value = Math.Min(_pv, 95f) / 100f;
                lbPg.Text = "更新进度: " + (int)_pv + "%";
                ProgressHook?.Invoke((int)_pv);     // 转发到经典模式窗口
            }
            return;
        }

        if (m.StartsWith("[FILE:CS]"))
        {
            Lg(m.Substring("[FILE:CS]".Length).TrimStart(), Gn);
            return;
        }
        if (m.StartsWith("[FILE:SUM]"))
        {
            Lg(m.Substring("[FILE:SUM]".Length).TrimStart(), Or);
            return;
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(m,
            @"^--- \d{4}-\d{2}-\d{2}"))
            Lg(m, Cy);
        else Lg(m);

        var sm = System.Text.RegularExpressions.Regex.Match(m, @"\[(\d)/5\]");
        if (sm.Success && int.TryParse(sm.Groups[1].Value, out var step))
        {
            _stepTarget = step switch { 1 => 5, 2 => 25, 3 => 55, 4 => 85, 5 => 93, _ => _stepTarget };
            if (_pv < _stepTarget - 8) _pv = _stepTarget - 8;   // 大步跳到阶段附近, 剩余交给蠕动
            pb.Value = Math.Min(_pv, 95f) / 100f;
            lbPg.Text = "更新进度: " + (int)_pv + "%";
            ProgressHook?.Invoke((int)_pv);     // 转发到经典模式窗口
        }
    }

    /*
     * 更新完成回调 (OD) — 进度条 100% → 显示结果 → 隐藏进度条 → 刷新
     */
    void OD(bool ok)
    {
        pb.Value = 1f;   // 100%
        lbPg.Text = "100%";
        ProgressHook?.Invoke(100);     // 转发到经典模式窗口
        if (ok)
        {
            LS(">>> 更新完成！如果更新没有效果，"
                + "请尝试再次点击更新或者全量更新。<<<");
            Lg("========================================", Cy);
            Lg("  更新已完成，将在目录【\\AUM管理组件】生成一份运行日志", Color.Gold);
            Lg("========================================", Cy);
        }
        else
            Lg(">>> 更新失败，请检查网络连接或查看上方日志。<<<",
                Color.Orange);

        System.Threading.Thread.Sleep(1500);
        pb.Visible = false;
        lbPg.Visible = false;
        Rf();
    }
}