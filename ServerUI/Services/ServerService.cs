/*
 * ==================================================================
 * 服务端进程管理服务 (ServerService) — v1.85-1
 * ==================================================================
 * 
 * 【功能说明】
 *   管理 DNF 服务端的整个生命周期：启动、停止、状态检测、孤儿进程清理。
 *   核心设计：通过持有 start-server.bat 的 Process 句柄来控制进程树。
 * 
 * 【进程树结构】
 *   ServerUI.exe (本程序)
 *     └── powershell.exe (调用 update.ps1 时)
 *     └── cmd.exe (运行 start-server.bat)
 *           └── DfoServer.exe (实际服务端程序 — 这是关键进程)
 * 
 * 【双重检测机制】
 *   仅当 BOTH 条件满足时才视为"运行中":
 *   1. start-server.bat 进程（cmd.exe）仍在运行
 *   2. DfoServer.exe 进程仍在运行（且必须是本工具目录下的，不是系统其他程序）
 * 
 * 【生命周期】
 *   启动: MainForm.Go() → Start(baseDir) → 创建 cmd.exe 进程执行 start-server.bat
 *   停止: MainForm.Stop → Stop() → taskkill /F /T /PID 杀进程树 → CleanOrphans() 兜底
 *   退出: MainForm.Fc() → Stop() + 杀 DfoGmTool 进程
 *   异常: bat 意外退出时 Exited 事件自动触发 CleanOrphans()
 * 
 * 【新手修改指南】
 *   - 想隐藏 DfoServer 窗口? 改 CreateNoWindow = true（但不建议，用户要求可见）
 *   - 想修改检测间隔? 去 MainForm.cs 改 _st 定时器的 Interval（默认 2000ms）
 *   - 想支持远程服务端? 需要大幅改造，当前仅支持本地进程
 *   - 想用不同的服务端启动脚本? 改 Start() 中的 bat 路径
 * 
 * 【P/Invoke 说明】
 *   ShowWindow(hwnd, 0) — 调用 Windows API 隐藏控制台窗口
 *   参数: hwnd=窗口句柄, 0=SW_HIDE(隐藏)
 *   仅隐藏窗口界面，进程本身不中断
 *   这是唯一一处调用 Windows 原生 API 的代码
 * ==================================================================
 */
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ServerUI.Services;

public class ServerService
{
    // ===== Windows API 导入 =====
    // ShowWindow: 显示/隐藏窗口，user32.dll 是 Windows 核心系统 DLL
    // nCmdShow 常用值: 0=SW_HIDE(隐藏), 1=SW_NORMAL(正常), 2=SW_MINIMIZE(最小化)
    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    // ===== 控制台 API (优雅停服用) =====
    // DfoServer 是控制台程序, 提示 "Press 's' for statistics, 'q' to quit",
    // 强杀进程会导致数据库未正常落盘而回档; 优雅停服 = 向它的控制台输入
    // 缓冲区写入 'q' 按键, 让其自行保存并退出
    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(uint dwProcessId);
    [DllImport("kernel32.dll")]
    static extern bool FreeConsole();
    [DllImport("kernel32.dll")]
    static extern IntPtr GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll")]
    static extern bool WriteConsoleInput(IntPtr hConsoleInput,
        INPUT_RECORD[] lpBuffer, uint nLength, out uint lpNumberOfEventsWritten);

    const int STD_INPUT_HANDLE = -10;
    const ushort KEY_EVENT = 0x0001;

    [StructLayout(LayoutKind.Explicit)]
    struct INPUT_RECORD
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEY_EVENT_RECORD
    {
        public int bKeyDown;        // BOOL = 4 字节 (不能用 C# bool, 会错位)
        public ushort wRepeatCount;
        public ushort wVirtualKeyCode;
        public ushort wVirtualScanCode;
        public char UnicodeChar;
        public uint dwControlKeyState;
    }

    // ===== 状态字段 =====
    // _batProcess: 持有 start-server.bat(cmd.exe) 的进程句柄
    // 通过这个句柄可以检测进程是否存活、杀进程、获取窗口句柄
    private Process _batProcess;

    /*
     * 检测 bat 进程是否存活
     * 
     * 只检查通过 Start() 启动的那个特定 cmd.exe 进程
     * 不会误判系统中其他 cmd.exe 进程
     * 
     * 使用场景: UI 定时刷新状态、Start() 前检查是否已在运行
     */
    public bool IsBatRunning
    {
        get
        {
            try
            {
                return _batProcess != null && !_batProcess.HasExited;
            }
            catch { return false; }
        }
    }

    /*
     * 检测指定路径的 DfoServer.exe 是否在运行
     * 
     * 检测逻辑: 
     *   1. 先按进程名 "DfoServer" 搜索所有匹配进程
     *   2. 逐个检查进程的可执行文件路径（MainModule.FileName）
     *   3. 只有完全匹配 distDir\DfoServer.exe 的进程才算
     * 
     * 为什么需要路径匹配?
     *   防止把系统上其他也叫 DfoServer.exe 的程序（如果有的话）误判为本工具的服务端
     * 
     * 参数:
     *   distDir — dist\win-x64 目录的完整路径
     * 
     * 权限说明:
     *   MainModule.FileName 在跨位数访问时可能失败（32位程序查64位进程）
     *   catch 块会默默地跳过这些进程，不影响整体判断
     */
    public static bool IsDfoServerRunning(string distDir)
    {
        try
        {
            // 构造期望的完整 exe 路径
            var expected = Path.GetFullPath(Path.Combine(distDir, "DfoServer.exe"));
            foreach (var p in Process.GetProcessesByName("DfoServer"))
            {
                try
                {
                    // 读取进程的可执行文件路径
                    var exePath = p.MainModule?.FileName;
                    // 不区分大小写比较
                    if (exePath != null &&
                        string.Equals(exePath, expected, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { /* 跨位数/权限问题，跳过这个进程继续检查下一个 */ }
            }
            return false;
        }
        catch { return false; }
    }

    /*
     * 双重检测快捷属性
     * 
     * 仅当 bat 和 DfoServer 同时存活时返回 true
     * 
     * 为什么需要双重检测?
     *   bat 退出但 DfoServer 残留 = 异常状态，不应该显示"运行中"
     *   用户看到的"运行"意味着"可以正常玩游戏"，两个进程缺一不可
     * 
     * 使用位置:
     *   - MainForm.Rs() — 每 2 秒刷新状态显示
     *   - MainForm.Play() — 启动后确认是否成功
     *   - MainForm.RI()/RF() — 更新前检查是否需要停服
     */
    public bool IsRunning => IsBatRunning && IsDfoServerRunning(GetDistDir());

    /*
     * 启动服务端
     * 
     * 执行步骤:
     *   1. 检查 bat 是否已在运行（避免重复启动）
     *   2. 找到 baseDir 下的 start-server.bat
     *   3. 创建新的 Process 对象，设置启动参数
     *   4. 注册 Exited 事件（bat 退出时自动清理 DfoServer 孤儿进程）
     *   5. 启动进程
     * 
     * 参数:
     *   baseDir — ServerS4A21-AUM 目录（不是 dist 目录！）
     * 
     * 关键设置:
     *   UseShellExecute = false  — 必须为 false 才能持有进程句柄
     *   CreateNoWindow = true    — 【v1.85-1】启动时不创建控制台窗口
     *                              DfoServer 在 cmd 共享控制台中运行，
     *                              MainWindowHandle 归属 cmd 而非 DfoServer，
     *                              事后 ShowWindow 无法可靠隐藏，故改为启动即隐藏。
     *   EnableRaisingEvents = true — 启用 Exited 事件
     */
    public void Start(string baseDir)
    {
        if (IsBatRunning) return;  // 已在运行，不重复启动

        var bat = Path.Combine(baseDir, "start-server.bat");
        if (!File.Exists(bat)) return;  // 脚本不存在（可能是首次使用，需要先更新）

        // 【v2.1-3 修复】服务端由 AUM 启动会闪退（手动启动正常）
        // 根因（双重）:
        //   1. 权限: app.manifest 为 asInvoker —— 普通启动时 AUM 与子进程均为非管理员,
        //      DfoServer 需要管理员权限（写数据/绑定端口等）→ 启动即闪退;
        //      手动"以管理员身份运行"bat 时才有管理员权限 → 正常。
        //   2. 隐藏方式: v1.85-1 为隐藏窗口用 CreateNoWindow=true, 服务端在无真实控制台
        //      环境下启动, 与手动双击（有真实控制台）环境不一致, 加剧闪退。
        // 方案:
        //   1. app.manifest 改 requireAdministrator —— AUM 强制管理员运行,
        //      整个进程树（含 DfoServer）自动获得管理员权限 = "所有操作按管理员运行";
        //   2. CreateNoWindow = false —— 与手动双击完全一致的运行环境（真实控制台）;
        //   3. 快速隐藏 —— Start 后立即轮询 MainWindowHandle, 控制台出现即隐藏
        //      （cmd 与 DfoServer 共享同一控制台, 隐藏 cmd 主窗口即隐藏整个共享控制台）。
        _batProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = bat,
                WorkingDirectory = baseDir,
                UseShellExecute = false,
                CreateNoWindow = false   // 与手动双击一致, 保证 DfoServer 有真实控制台
            },
            EnableRaisingEvents = true
        };

        // bat 进程退出时的处理
        // 场景: 用户手动关闭了 cmd 窗口，或 DfoServer 崩溃导致 bat 退出
        // 此时需要清理可能残留的 DfoServer 孤儿进程
        _batProcess.Exited += (s, e) =>
        {
            CleanOrphans();
            try { _batProcess?.Dispose(); } catch { }
            _batProcess = null;
        };

        _batProcess.Start();

        // v2.1-3 快速隐藏: 控制台窗口出现后立即隐藏（不必等 Play 的 10 秒兜底任务）
        // cmd 与 DfoServer 共享同一控制台: 隐藏 cmd 主窗口即隐藏整个共享控制台;
        // 进程已退出时 MainWindowHandle 访问会抛异常, 由 try/catch 兜底。
        if (!_batProcess.HasExited)
        {
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var deadline = DateTime.UtcNow.AddSeconds(5);
                    while (DateTime.UtcNow < deadline)
                    {
                        System.Threading.Thread.Sleep(60);
                        var hwnd = _batProcess.MainWindowHandle;
                        if (hwnd != IntPtr.Zero)
                        {
                            ShowWindow(hwnd, 0);
                            break;
                        }
                    }
                }
                catch { }
            });
        }
    }

    /*
     * 停止服务端 (v2.031 优雅停服)
     *
     * 执行步骤（先优雅、后兜底）:
     *   1. 向 DfoServer.exe 控制台输入缓冲区写入 'q' 按键
     *      → DfoServer 自行保存数据并退出 (数据库正常落盘, 不再回档)
     *   2. 等待 bat 进程自然退出 (最多 10 秒)
     *   3. 超时未退出 → taskkill /F /T 强杀兜底
     *   4. CleanOrphans() 最终兜底 (优雅退出生效时此处无残留)
     *
     * 使用场景:
     *   - 用户点击 [停止服务端] / [重启服务端]
     *   - 程序关闭时自动清理
     */
    public void Stop()
    {
        bool graceful = SendQuitToDfoServer();

        if (graceful)
        {
            // 等待 bat 自然退出 (DfoServer 退出 → bat 结束 → Exited 事件清理)
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (_batProcess == null || _batProcess.HasExited) break;
                System.Threading.Thread.Sleep(200);
            }
            if (_batProcess != null && !_batProcess.HasExited)
            {
                // 优雅退出超时 → 强杀兜底
                KillProcessTree(_batProcess);
            }
        }
        else
        {
            // 找不到 DfoServer / 发送失败 → 直接强杀
            if (_batProcess != null && !_batProcess.HasExited)
                KillProcessTree(_batProcess);
        }

        if (_batProcess != null)
        {
            try { _batProcess.Dispose(); } catch { }
            _batProcess = null;
        }

        // 最终兜底清理 (优雅退出已生效时, 此处不会命中)
        CleanOrphans();
    }

    /*
     * 优雅停服核心: 向 DfoServer.exe 的控制台输入缓冲区写入 'q' 按键
     *
     * 原理: AttachConsole 附加到 DfoServer 的控制台 → WriteConsoleInput
     *       写入一个 KEY_EVENT('q') → DfoServer 的 Console.ReadKey 收到 'q'
     *       → 自行保存数据库并退出。写完后立即 FreeConsole 脱离, 不影响本进程。
     *
     * 返回: true = 已成功写入 (可等待优雅退出); false = 无 DfoServer 或失败
     */
    static bool SendQuitToDfoServer()
    {
        try
        {
            var distDir = GetDistDir();
            var expected = Path.GetFullPath(Path.Combine(distDir, "DfoServer.exe"));

            foreach (var p in Process.GetProcessesByName("DfoServer"))
            {
                try
                {
                    var exePath = p.MainModule?.FileName;
                    if (exePath == null) continue;
                    if (!string.Equals(exePath, expected, StringComparison.OrdinalIgnoreCase))
                        continue;

                    FreeConsole();   // 先脱离本进程可能持有的控制台 (无则返回 false, 无副作用)
                    if (!AttachConsole((uint)p.Id)) continue;   // 附加失败 → 尝试下一个
                    try
                    {
                        var hIn = GetStdHandle(STD_INPUT_HANDLE);
                        if (hIn == IntPtr.Zero) continue;
                        var rec = new INPUT_RECORD
                        {
                            EventType = KEY_EVENT,
                            KeyEvent = new KEY_EVENT_RECORD
                            {
                                bKeyDown = 1,
                                wRepeatCount = 1,
                                UnicodeChar = 'q'
                            }
                        };
                        WriteConsoleInput(hIn, new[] { rec }, 1, out _);
                        return true;
                    }
                    finally { FreeConsole(); }
                }
                catch { }
            }
        }
        catch { }
        return false;
    }

    /*
     * 清理 DfoServer 孤儿进程
     * 
     * 孤儿进程定义: bat 已退出但 DfoServer 仍残留的进程
     * 
     * 清理逻辑:
     *   1. 获取本工具 dist\win-x64 目录
     *   2. 找到所有名为 DfoServer 的进程
     *   3. 只 Kill 那些 exe 路径匹配本工具目录的进程（不杀其他同名进程）
     * 
     * 使用场景:
     *   - bat 意外退出时自动调用
     *   - 停止服务端时兜底调用
     *   - UI 检测到异常状态时主动清理
     */
    public static void CleanOrphans()
    {
        try
        {
            var distDir = GetDistDir();
            var expected = Path.GetFullPath(Path.Combine(distDir, "DfoServer.exe"));
            var orphans = Process.GetProcessesByName("DfoServer");

            foreach (var p in orphans)
            {
                try
                {
                    var exePath = p.MainModule?.FileName;
                    // 只杀本工具的 DfoServer，不杀其他同名进程
                    if (exePath != null &&
                        string.Equals(exePath, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        try { p.Kill(); } catch { }
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    /*
     * 获取服务端 dist\win-x64 目录路径
     * 
     * 路径计算逻辑:
     *   如果 EXE 所在目录下有 AUM管理组件 子目录 → _ad = AUM管理组件
     *   否则 → _ad = EXE 所在目录（便携版场景）
     *   然后 → _ad\ServerS4A21-AUM\dist\win-x64
     * 
     * 这是判定服务端位置的唯一入口，修改这里会影响所有路径相关的功能
     */
    public static string GetDistDir()
    {
        var bd = AppDomain.CurrentDomain.BaseDirectory;
        var ad = Directory.Exists(Path.Combine(bd, "AUM管理组件"))
            ? Path.Combine(bd, "AUM管理组件")
            : bd;
        return Path.Combine(ad, "ServerS4A21-AUM", "dist", "win-x64");
    }

    /*
     * 使用 taskkill 命令递归终止整个进程树
     * 
     * taskkill 参数说明:
     *   /F — 强制终止（不等待进程响应）
     *   /T — 递归终止所有子进程（包括子进程的子进程）
     *   /PID — 指定要终止的进程 ID
     * 
     * 为什么用 taskkill 而不是 Process.Kill()?
     *   Process.Kill() 只杀目标进程，不杀子进程
     *   如果只用 Kill()，DfoServer.exe 会变成孤立进程
     * 
     * taskkill 是 Windows 内置命令，无需安装任何依赖
     */
    private static void KillProcessTree(Process p)
    {
        try
        {
            using var killer = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/F /T /PID {p.Id}",
                    UseShellExecute = false,
                    CreateNoWindow = true  // 不显示 taskkill 的黑窗口
                }
            };
            killer.Start();  // 发射后不管，taskkill 会在后台完成清理
        }
        catch
        {
            // taskkill 失败时用 Process.Kill() 兜底
            // 注意: Kill() 只杀父进程，DfoServer 可能残留
            // 但外层 Stop() 还会调 CleanOrphans() 做最终清理
            try { p.Kill(); } catch { }
        }
    }

    /*
     * 隐藏 bat 进程的控制台窗口
     * 
     * 作用: 启动 10 秒后将 cmd.exe 的黑窗口隐藏
     * 效果: 窗口消失，但 cmd.exe 和 DfoServer.exe 继续在后台运行
     * 不影响: 进程运行状态、任务管理器中的可见性
     * 
     * 调用时机:
     *   点击 [开始游戏] 或 [重启服务端] 10 秒后自动调用
     *   10 秒的延迟让用户有时间看到启动日志
     * 
     * 修改建议:
     *   - 想改成最小化而不是隐藏? 把 0 改成 2 (SW_MINIMIZE)
     *   - 想完全禁用此功能? 删除 Play() 和 btRe 中的相关代码
     */
    public void HideConsoleWindow()
    {
        try
        {
            if (_batProcess != null && !_batProcess.HasExited)
            {
                var hwnd = _batProcess.MainWindowHandle;
                if (hwnd != IntPtr.Zero)
                    ShowWindow(hwnd, 0);  // 0 = SW_HIDE
            }
        }
        catch { }
    }

    /*
     * 隐藏 DfoServer.exe 的控制台窗口
     *
     * 作用: 在服务端启动完成后自动隐藏 DfoServer.exe 的窗口，仅保留后台进程
     */
    public static void HideDfoServerWindow()
    {
        try
        {
            var distDir = GetDistDir();
            var expected = Path.GetFullPath(Path.Combine(distDir, "DfoServer.exe"));
            foreach (var p in Process.GetProcessesByName("DfoServer"))
            {
                try
                {
                    var exePath = p.MainModule?.FileName;
                    if (exePath != null && string.Equals(exePath, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        var hwnd = p.MainWindowHandle;
                        if (hwnd != IntPtr.Zero)
                            ShowWindow(hwnd, 0);
                        break;
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    /*
     * 检查 PVF 资源文件是否存在
     * 
     * PVF (Package Vault File) 是 DNF 的技能/装备/任务数据文件
     * 没有 Script.pvf，服务端无法正常工作
     * 
     * 用于界面显示 "PVF: [O] 已加载" 或 "PVF: [O] 未找到"
     * 
     * 修改建议:
     *   - 想替换 PVF 文件? 直接把新的 Script.pvf 覆盖到 Data\Pvf\ 目录
     *   - 想检查 PVF 完整性? 可以在后面加 MD5/SHA256 校验
     */
    public bool PvfExists(string baseDir)
    {
        return File.Exists(Path.Combine(baseDir,
            "dist", "win-x64", "Data", "Pvf", "Script.pvf"));
    }
}
