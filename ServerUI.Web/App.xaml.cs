using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

[assembly: ThemeInfo(ResourceDictionaryLocation.None, ResourceDictionaryLocation.SourceAssembly)]

namespace ServerUI.Web;

/// <summary>
/// 应用入口。
/// 启动参数（与旧 ServerUI 保持一致 + 新增 --selftest）：
///   --settime "yyyy-MM-dd HH:mm:ss"  仅写入系统时间（无窗口，供提权实例使用）
///   --synctime                       仅同步网络时间（无窗口，供提权实例使用）
///   --selftest                       只读桥命令自检：打印 app.init / server.status 的 JSON 并退出
/// 环境变量：AUM_CDP_PORT / AUM_WEBVIEW_ARGS / AUM_WEB_ROOT / AUM_DATA_ROOT / AUM_DEVTOOLS / AUM_WEBVIEW_DATA
/// </summary>
public sealed partial class App : Application
{
    // 旧 Program.cs 会在 exe 目录留一份启动日志；便携/无依赖场景靠它排查
    public static readonly string StartupLogName = "ServerUI-启动日志.txt";
    public static readonly string CrashLogName = "ServerUI-崩溃日志.txt";

    private static Mutex _instanceMutex;
    private static EventWaitHandle _activateEvent;

    /// <summary>进程级诊断日志（exe 目录，追加写；失败静默）。</summary>
    internal static void Diag(string message)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, StartupLogName);
            File.AppendAllText(path,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine,
                new UTF8Encoding(false));
        }
        catch { }
    }

    /// <summary>未处理异常 → 崩溃日志 + 提示框（与旧 Program.cs 的 Report 行为一致）。</summary>
    internal static void Report(Exception ex, string tag)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, CrashLogName);
            File.AppendAllText(path,
                "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + tag
                + Environment.NewLine + ex + Environment.NewLine + new string('-', 60) + Environment.NewLine,
                new UTF8Encoding(false));
        }
        catch { }

        try
        {
            MessageBox.Show("程序发生错误：" + Environment.NewLine + ex.Message
                + Environment.NewLine + Environment.NewLine
                + "详细信息已写入：" + Path.Combine(AppContext.BaseDirectory, CrashLogName),
                "ServerS4A21 管理器 - 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (s, a) =>
            Report(a.ExceptionObject as Exception ?? new Exception("未知错误"), "UnhandledException");
        DispatcherUnhandledException += (s, a) =>
        {
            Report(a.Exception, "DispatcherUnhandledException");
            a.Handled = true;
        };

        var args = e.Args ?? new string[0];

        // ---- 提权实例入口（无窗口）----
        if (args.Length > 0 && args[0] == "--settime")
        {
            string ts = args.Length > 1 ? args[1] : "";
            Diag("--settime " + ts);
            ServerUI.TimeTool.ApplyFromArg(ts);
            Shutdown(0);
            return;
        }
        if (args.Length > 0 && args[0] == "--synctime")
        {
            Diag("--synctime");
            ServerUI.TimeTool.SyncFromArg();
            Shutdown(0);
            return;
        }

        // ---- 健康检查（AUM 更新替换前验证新 exe；无窗口、无副作用）----
        if (args.Length > 0 && args[0] == "--healthcheck")
        {
            var tokenFile = args.Length > 1 ? args[1] : "";
            Diag("--healthcheck " + tokenFile);
            int code;
            try { code = HealthCheck.Run(tokenFile); }
            catch { code = 2; }
            Shutdown(code);
            return;
        }

        // ---- 只读自检（无窗口，供 CI/审稿）----
        if (args.Length > 0 && args[0] == "--selftest")
        {
            int code = 0;
            try { code = SelfTest.Run(); }
            catch (Exception ex) { Report(ex, "selftest"); code = 3; }
            Shutdown(code);
            return;
        }

        // ---- AUM 更新沙箱测试（本地 127.0.0.1 静态服务，绝不联网）----
        if (args.Length > 0 && args[0] == "--updatetest")
        {
            int code;
            try { code = UpdaterTests.Run(args); }
            catch (Exception ex)
            {
                try { Console.Error.WriteLine(ex); } catch { }
                code = 3;
            }
            Shutdown(code);
            return;
        }

        // ---- 运行方式补丁沙箱测试（临时目录，不碰真实游戏目录）----
        if (args.Length > 0 && args[0] == "--dxtest")
        {
            int code;
            try { code = DxTests.Run(args); }
            catch (Exception ex)
            {
                try { Console.Error.WriteLine(ex); } catch { }
                code = 3;
            }
            Shutdown(code);
            return;
        }

        // ---- 多开路径验证（仅沙箱：exe 目录须有 playtest.marker）----
        if (args.Length > 0 && args[0] == "--playtest")
        {
            int code;
            try { code = PlayProbe.Run(); }
            catch (Exception ex) { Console.WriteLine("{\"ok\":false,\"error\":\"" + ex.Message + "\"}"); code = 3; }
            Shutdown(code);
            return;
        }

        // ---- 单实例（第二个实例只激活第一个窗口）----
        var instanceId = @"Local\ServerUI.Web.SingleInstance" + ProfileSuffix();
        var activateId = @"Local\ServerUI.Web.Activate" + ProfileSuffix();
        bool isFirst;
        try
        {
            _instanceMutex = new Mutex(true, instanceId, out isFirst);
        }
        catch
        {
            isFirst = true;
        }
        if (!isFirst)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(activateId, out var existing))
                {
                    existing.Set();
                    existing.Dispose();
                }
            }
            catch { }
            Shutdown(0);
            return;
        }

        try
        {
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, activateId);
            StartActivationListener(_activateEvent);
        }
        catch { }

        Diag("app start");
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    /// <summary>AUM_DATA_ROOT 指向独立 profile 时，单实例锁也要独立，否则自检/测试
    /// 会把激活信号发给正在运行的真实实例而永不显示窗口。</summary>
    private static string ProfileSuffix()
    {
        var over = Environment.GetEnvironmentVariable("AUM_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(over)) return "";
        var hash = 17;
        foreach (var ch in over.Trim().ToLowerInvariant())
            unchecked { hash = hash * 31 + ch; }
        return "." + Math.Abs(hash).ToString("x8");
    }

    private static void StartActivationListener(EventWaitHandle handle)
    {
        var thread = new Thread(() =>
        {
            try
            {
                while (handle != null && handle.WaitOne())
                {
                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        var w = Application.Current?.MainWindow as MainWindow;
                        if (w == null) return;
                        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                        w.Activate();
                    });
                }
            }
            catch { }
        })
        { IsBackground = true, Name = "ServerUI single-instance listener" };
        thread.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _activateEvent?.Set();
            _activateEvent?.Dispose();
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
        }
        catch { }
        base.OnExit(e);
    }
}
