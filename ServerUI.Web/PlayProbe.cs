using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ServerUI.Web.Bridge;

namespace ServerUI.Web;

/// <summary>
/// `--playtest`：验证「服务端已在运行时点开始游戏 = 只拉起客户端」（多开路径）。
///
/// 只在沙箱里跑：exe 所在目录必须存在 playtest.marker，且结构为
///   &lt;sandbox&gt;\ServerUI.exe
///   &lt;sandbox&gt;\playtest.marker
///   &lt;sandbox&gt;\AUM管理组件\ServerS4A21-AUM\start-server.bat   （假服务端，只 sleep）
///   &lt;sandbox&gt;\本地游戏S4.bat                                  （假客户端，只写计数文件）
/// 真实安装目录没有 marker，因此绝不会启动真实服务端 / 真实游戏。
/// </summary>
internal static class PlayProbe
{
    private const string MarkerName = "playtest.marker";
    private const string ClientCountName = "playtest-client.log";

    private sealed class NullUi : IUi
    {
        public void Push(string type, object payload) { }
        public void Log(string level, string text) { }
        public void Progress(double value, string label, bool visible) { }
        public void Toast(string level, string text) { }
        public void Status(object payload) { }
        public void ArchiveChanged() { }
        public void DllChanged() { }
        public void ThemeChanged(string theme) { }
        public void FlushNow() { }
    }

    internal static int Run()
    {
        var result = new StringBuilder();
        var baseDir = Paths.Base;
        if (!File.Exists(Path.Combine(baseDir, MarkerName)))
        {
            Console.WriteLine("{\"ok\":false,\"error\":\"拒绝执行：缺少 " + MarkerName + "（非沙箱目录）\"}");
            return 3;
        }

        var exit = 1;
        var store = new SettingsStore();
        store.Load();
        var app = new AppController(new NullUi(), store);
        var clientCount = Path.Combine(Paths.Gr, ClientCountName);
        try { if (File.Exists(clientCount)) File.Delete(clientCount); } catch { }

        try
        {
            // 第一次：服务端未运行 → 正常启动（假 bat）
            var first = Wait(app.ServerStartAsync(), 30000);
            result.AppendLine("first: " + Json(first));

            var batRunning = app.Server.IsBatRunning;
            result.AppendLine("batRunningAfterFirst: " + batRunning);

            // 第二次：服务端仍在运行 → 必须复用，不再启动第二个服务端
            var second = Wait(app.ServerStartAsync(), 30000);
            result.AppendLine("second: " + Json(second));

            var reused = Json(second).IndexOf("\"reused\":true", StringComparison.Ordinal) >= 0;
            var gameSecond = Json(second).IndexOf("\"game\":\"local\"", StringComparison.Ordinal) >= 0;

            // 客户端 bat 由 Process.Start 异步拉起，稍等一下再数（否则必然读到 0）
            Thread.Sleep(2000);
            var clients = 0;
            try { clients = File.Exists(clientCount) ? File.ReadAllLines(clientCount).Length : 0; } catch { }
            result.AppendLine("clientLaunches: " + clients);

            var ok = reused && gameSecond && clients >= 2;
            result.AppendLine("ok: " + ok);
            exit = ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            result.AppendLine("error: " + ex);
            exit = 1;
        }
        finally
        {
            try { app.Server.Stop(); } catch { }
        }

        Console.WriteLine(result.ToString());
        return exit;
    }

    private static object Wait(Task<object> task, int timeoutMs)
    {
        if (!task.Wait(timeoutMs)) throw new TimeoutException("测点超时");
        return task.Result;
    }

    private static string Json(object o)
    {
        try { return System.Text.Json.JsonSerializer.Serialize(o, NativeBridge.Json); }
        catch { return "{}"; }
    }
}
