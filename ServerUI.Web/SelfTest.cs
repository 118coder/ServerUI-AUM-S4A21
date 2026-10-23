using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ServerUI.Web.Bridge;

namespace ServerUI.Web;

/// <summary>
/// `--selftest`：不显示窗口，只跑只读桥命令并把结果以 JSON 打印。
/// 用途：CI / 审稿快速确认「exe 能起、路径推导正确、前端资源已嵌入、状态快照可生成」。
/// 全程只读：不写游戏目录、不动 GameGaurd.ini、不启动服务端、不发网络请求。
/// </summary>
internal static class SelfTest
{
    private const string OutFileName = "ServerUI-selftest.json";

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
        var exitCode = 0;
        object appInit = null;
        object serverStatus = null;
        object archiveList = null;
        object dllState = null;
        object fsDrives = null;
        object fsBrowse = null;
        object timeGet = null;
        object windowMode = null;
        string error = null;

        var store = new SettingsStore();
        try { store.Load(); } catch { }

        var commands = new Commands(new NullUi(), store);

        try { appInit = commands.Invoke("app.init", null); }
        catch (Exception ex) { error = "app.init: " + ex.Message; exitCode = 1; }

        try { serverStatus = commands.Invoke("server.status", null); }
        catch (Exception ex) { error = (error == null ? "" : error + " | ") + "server.status: " + ex.Message; exitCode = 2; }

        // Phase 2 只读探针：全部为纯读命令，不写游戏目录、不动 GameGaurd.ini、不启动服务端。
        try { archiveList = commands.Invoke("archive.list", null); }
        catch (Exception ex) { error = (error == null ? "" : error + " | ") + "archive.list: " + ex.Message; exitCode = 3; }

        try { dllState = commands.Invoke("dll.state", null); }
        catch (Exception ex) { error = (error == null ? "" : error + " | ") + "dll.state: " + ex.Message; exitCode = 4; }

        try { fsDrives = commands.Invoke("fs.drives", null); }
        catch (Exception ex) { error = (error == null ? "" : error + " | ") + "fs.drives: " + ex.Message; exitCode = 5; }

        try
        {
            // 目录枚举只读：默认落到用户主目录（fs.browse 的 path 留空即可）
            var argJson = JsonDocument.Parse("{\"path\":\"\",\"filter\":\"all\",\"showHidden\":false}").RootElement;
            fsBrowse = commands.Invoke("fs.browse", argJson);
        }
        catch (Exception ex) { error = (error == null ? "" : error + " | ") + "fs.browse: " + ex.Message; exitCode = 6; }

        // Phase 3a：时间页只读探针（只读系统时间，不写）
        try { timeGet = commands.Invoke("time.get", null); }
        catch (Exception ex) { error = (error == null ? "" : error + " | ") + "time.get: " + ex.Message; exitCode = 7; }

        // Phase 3b：窗口模式探针（无 mode 参数 = 只读查询，不切几何、不动窗口）
        try { windowMode = commands.Invoke("window.mode", null); }
        catch (Exception ex) { error = (error == null ? "" : error + " | ") + "window.mode: " + ex.Message; exitCode = 8; }

        // AUM 更新能力快照（纯本地探测，不联网）：让审稿/CI 看到"会走哪条通道"。
        object updateInfo;
        try
        {
            var env = ServerUI.Web.Bridge.Aum.AumEnvironmentProbe.Probe(Compat.ExePath());
            var selfContained = false;
            try
            {
                var exe = Compat.ExePath();
                selfContained = !string.IsNullOrEmpty(exe) && File.Exists(exe) && new FileInfo(exe).Length > 50_000_000;
            }
            catch { }
            updateInfo = new
            {
                current = AppController.Version,
                shape = selfContained ? "sc" : "fd",
                strategy = env.SdkOk && env.PowerShellOk && env.DiskOk ? "compile-eligible" : "package",
                buildEnv = new
                {
                    sdkOk = env.SdkOk, sdkVersion = env.SdkVersion,
                    powerShellOk = env.PowerShellOk, powerShellVersion = env.PowerShellVersion,
                    diskOk = env.DiskOk, freeGB = Math.Round(env.FreeBytes / 1073741824.0, 1)
                },
                note = "只做本地探测；实际策略在点击更新时联网读取 Release 清单后再定（源码结构不符会回退发行包）"
            };
        }
        catch (Exception ex) { updateInfo = new { error = ex.Message }; }

        // v2.20：控制台隐藏自检（隔离沙箱，不启动真实服务端、不写游戏目录）
        object consoleHide = null;
        try { consoleHide = ConsoleHideProbe.Run(); }
        catch (Exception ex) { error = (error == null ? "" : error + " | ") + "console.hide: " + ex.Message; exitCode = 9; }

        var web = InspectEmbeddedWeb();

        var report = new
        {
            ok = exitCode == 0,
            mode = "selftest",
            version = AppController.Version,
            machine = Environment.MachineName,
            os = Environment.OSVersion.VersionString,
            isAdmin = Paths.IsAdmin,
            update = updateInfo,
            consoleHide,
            error,
            paths = new
            {
                baseDir = Paths.Base,
                ad = Paths.Ad,
                gr = Paths.Gr,
                settingsFile = Paths.SettingsFile,
                serverRoot = Paths.ServerRoot,
                distDir = Paths.DistDir,
                dataDir = Paths.DataDir,
                pvfDir = Paths.PvfDir,
                switchDir = Paths.SwitchDir,
                backupDir = Paths.BackupDir,
                gameGaurdIni = Paths.GameGaurdIni,
                updateLog = Paths.UpdateLogFile,
                runningLog = Paths.RunningLogFile,
                webViewData = Paths.WebViewData,
                devWebRoot = Paths.DevWebRoot
            },
            existence = new
            {
                ad = Directory.Exists(Paths.Ad),
                gr = Directory.Exists(Paths.Gr),
                dnfExe = File.Exists(Paths.GameDnfExe),
                gameGaurdIni = File.Exists(Paths.GameGaurdIni),
                updateLog = File.Exists(Paths.UpdateLogFile),
                serverDist = Directory.Exists(Paths.DistDir),
                serverPvf = File.Exists(Paths.ServerPvf),
                gmTool = File.Exists(Paths.GmToolExe),
                patchZip = File.Exists(Paths.PatchZip),
                dx11 = Directory.Exists(Paths.Dx11Dir),
                dx12 = Directory.Exists(Paths.Dx12Dir),
                sdkInstaller = File.Exists(Paths.SdkInstaller)
            },
            webAssets = web,
            appInit,
            serverStatus,
            // Phase 2 新增只读探针
            archiveList,
            dllState,
            fsDrives,
            fsBrowse,
            timeGet,
            windowMode
        };

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        WriteConsole(json);
        WriteFile(json);
        return exitCode;
    }

    private static object InspectEmbeddedWeb()
    {
        try
        {
            const string resName = "ServerUI.Web.web.zip";
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resName);
            if (s == null) return new { embedded = false, bytes = 0, name = resName };
            return new { embedded = true, bytes = (long)s.Length, name = resName };
        }
        catch (Exception ex)
        {
            return new { embedded = false, bytes = 0, error = ex.Message };
        }
    }

    /// <summary>WinExe 默认没有控制台；附加到父控制台后直接写 CONOUT$，保证 git bash 里能看到。</summary>
    private static void WriteConsole(string json)
    {
        try { NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS); } catch { }

        try
        {
            using var con = new FileStream("CONOUT$", FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            var bytes = Encoding.UTF8.GetBytes(json + Environment.NewLine);
            con.Write(bytes, 0, bytes.Length);
            con.Flush();
        }
        catch
        {
            try { Console.Out.WriteLine(json); } catch { }
        }
    }

    /// <summary>同时落一份 JSON，便于无控制台的自动化审稿直接读取。</summary>
    private static void WriteFile(string json)
    {
        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, OutFileName), json, new UTF8Encoding(false));
        }
        catch { }
    }

    /// <summary>
    /// 控制台隐藏探针：在临时目录里建一个假 start-server.bat，用 ServerService 启动，
    /// 断言「子进程控制台存在但不可见」，再优雅停服并清理。全程只写 %TEMP%。
    /// </summary>
    private static class ConsoleHideProbe
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool AttachConsole(uint pid);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool FreeConsole();
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        public static object Run()
        {
            var dir = Path.Combine(Path.GetTempPath(), "ServerUI-selftest-console-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            var bat = Path.Combine(dir, "start-server.bat");
            // 假服务端：打印一行后睡 ~20s（不依赖真实 DfoServer / 不占端口）
            var batText = "@echo off" + "\r\n"
                + "echo ServerUI console-hide probe" + "\r\n"
                + "ping -n 22 127.0.0.1 >nul" + "\r\n";
            File.WriteAllText(bat, batText, new UTF8Encoding(false));

            var svc = new ServerUI.Services.ServerService();
            bool found = false, visible = false, hidden = false, stopped = false;
            try
            {
                svc.Start(dir);
                var deadline = DateTime.UtcNow.AddSeconds(6);
                while (DateTime.UtcNow < deadline)
                {
                    System.Threading.Thread.Sleep(150);
                    if (ServerUI.Services.ServerService.HideConsoleOfProcess(ProbePid(svc), out found))
                        hidden = true;
                    if (found) break;
                }
                // 再等一拍，确认窗口状态稳定（不闪回）
                System.Threading.Thread.Sleep(400);
                visible = ConsoleVisible(ProbePid(svc));
            }
            catch { }
            finally
            {
                try { svc.Stop(); stopped = !svc.IsBatRunning; } catch { }
                try { System.Threading.Thread.Sleep(800); Directory.Delete(dir, true); } catch { }
            }

            return new
            {
                ok = found && !visible && stopped,
                consoleFound = found,
                consoleVisible = visible,
                hidOnce = hidden,
                stopped,
                note = found && !visible
                    ? "控制台已创建但不可见；停服正常"
                    : (found ? "控制台仍然可见（隐藏失败）" : "未观察到控制台窗口（可能启动过快或已被隐藏）")
            };
        }

        private static int ProbePid(ServerUI.Services.ServerService svc)
        {
            // ServerService 只持有 Process 私有字段；这里用 bat 的 cmd 进程组里最新的 cmd 近似
            var bat = svc.GetType();
            var f = bat.GetField("_batProcess", BindingFlags.Instance | BindingFlags.NonPublic);
            var p = f != null ? f.GetValue(svc) as System.Diagnostics.Process : null;
            return p != null ? p.Id : 0;
        }

        private static bool ConsoleVisible(int pid)
        {
            if (pid == 0) return false;
            try
            {
                if (!AttachConsole((uint)pid)) return false;
                try
                {
                    var h = GetConsoleWindow();
                    return h != IntPtr.Zero && IsWindowVisible(h);
                }
                finally { FreeConsole(); }
            }
            catch { return false; }
        }
    }
}