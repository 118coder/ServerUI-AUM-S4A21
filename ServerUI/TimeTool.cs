/*
 * ==================================================================
 * 时间调节工具 (TimeTool.cs) — 供【调整系统时间】页面使用
 * ==================================================================
 * 功能: 设置系统时间 / 从 NTP 服务器同步网络时间 / 管理员提权
 * 参考: E:\网页小工具\时间调节工具 (独立的命令行小工具)
 *
 * 说明: 写入系统时间需要管理员权限; 主界面进程为 asInvoker 权限,
 *       需要提权时以命令行参数重启自身 (--settime / --synctime),
 *       提权实例完成操作后把结果写入 %TEMP%\serverui_time_result.txt,
 *       主界面读取该文件获得结果, 不弹窗不干扰主窗口。
 *       命令行入口由 Program.Main 转发 (见 Program.cs)。
 * ==================================================================
 */
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace ServerUI;

static class TimeTool
{
    // =================================================================
    // 原生 API — SetSystemTime 写入 UTC 时间 (系统内部保存 UTC)
    // =================================================================
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEMTIME
    {
        public ushort wYear;
        public ushort wMonth;
        public ushort wDayOfWeek;
        public ushort wDay;
        public ushort wHour;
        public ushort wMinute;
        public ushort wSecond;
        public ushort wMilliseconds;
    }

    [DllImport("kernel32.dll")]
    private static extern bool SetSystemTime(ref SYSTEMTIME lpSystemTime);

    // =================================================================
    // 管理员检测 / 提权
    // =================================================================
    public static bool IsAdmin()
    {
        try
        {
            var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return true; }
    }

    /*
     * 以管理员权限重新启动自身执行命令行操作
     * 参数: --settime "yyyy-MM-dd HH:mm:ss" / --synctime
     * 返回 false 表示用户取消提权或提权失败
     */
    public static bool RunElevated(string argLine)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Compat.ExePath(),
                Arguments = argLine,
                Verb = "runas",
                UseShellExecute = true,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
            };
            using var p = Process.Start(psi);
            if (p != null) p.WaitForExit();
            return true;
        }
        catch (Exception ex)
        {
            // 用户取消 UAC (错误码 1223) 或系统拒绝提权
            if (ex.Message.IndexOf("1223") >= 0
                || ex.Message.IndexOf("Cancel", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            return false;
        }
    }

    // =================================================================
    // 时间设置
    // =================================================================
    public static string SetLocalTime(DateTime local) => SetUtc(local.ToUniversalTime());

    public static string SetUtc(DateTime utc)
    {
        var st = new SYSTEMTIME
        {
            wYear = (ushort)utc.Year,
            wMonth = (ushort)utc.Month,
            wDay = (ushort)utc.Day,
            wHour = (ushort)utc.Hour,
            wMinute = (ushort)utc.Minute,
            wSecond = (ushort)utc.Second,
            wMilliseconds = 0
        };
        if (!SetSystemTime(ref st)) return "错误代码 " + Marshal.GetLastWin32Error();
        return null;
    }

    // =================================================================
    // NTP 同步
    // =================================================================
    private static readonly string[] Servers =
    {
        "ntp.aliyun.com",
        "cn.pool.ntp.org",
        "ntp.ntsc.ac.cn",
        "time.windows.com",
        "pool.ntp.org"
    };

    /* 顺序尝试各 NTP 服务器, 返回第一个成功的网络标准时间 (UTC) */
    public static DateTime? QueryNtp()
    {
        foreach (var s in Servers)
        {
            var t = QueryNtp(s);
            if (t.HasValue) return t;
        }
        return null;
    }

    public static DateTime? QueryNtp(string server)
    {
        try
        {
            IPAddress ip = null;
            foreach (var a in Dns.GetHostAddresses(server))
            {
                if (a.AddressFamily == AddressFamily.InterNetwork) { ip = a; break; }
            }
            if (ip == null) return null;

            using var uc = new UdpClient();
            uc.Client.ReceiveTimeout = 3000;
            uc.Client.SendTimeout = 3000;
            uc.Connect(ip, 123);
            var req = new byte[48];
            req[0] = 0x1B;   // NTP v4, Client 模式
            uc.Send(req, 48);
            var ep = new IPEndPoint(IPAddress.Any, 0);
            var r = uc.Receive(ref ep);
            if (r.Length < 48) return null;

            ulong sec = ((ulong)r[40] << 24) | ((ulong)r[41] << 16) | ((ulong)r[42] << 8) | r[43];
            ulong frac = ((ulong)r[44] << 24) | ((ulong)r[45] << 16) | ((ulong)r[46] << 8) | r[47];
            double total = sec + frac / 4294967296.0;
            return new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(total);
        }
        catch { return null; }
    }

    /* NTP 全失败时的回退: 调用 w32tm /resync */
    public static string ResyncViaW32tm()
    {
        var isXp = Environment.OSVersion.Platform == PlatformID.Win32NT
                    && Environment.OSVersion.Version.Major < 6;
        try
        {
            var psi = new ProcessStartInfo("w32tm.exe", isXp ? "-resync" : "/resync /force")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            var so = p.StandardOutput.ReadToEnd();
            var se = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode == 0) return null;
            return "Windows 时间服务同步未成功：" + so + se;
        }
        catch (Exception ex)
        {
            return "Windows 时间服务不可用：" + ex.Message;
        }
    }

    /* 直接同步: NTP 获取 → 写入系统时间; 返回 null 表示成功 */
    public static string DoSync()
    {
        foreach (var s in Servers)
        {
            var t = QueryNtp(s);
            if (t.HasValue)
            {
                var err = SetUtc(t.Value);
                if (err == null) return null;
                return "已获取标准时间（" + s + "），但写入失败：" + err;
            }
        }
        return ResyncViaW32tm();
    }

    // =================================================================
    // 提权实例命令行模式 (由 Program.Main 转发)
    // =================================================================
    private static string ResultPath() =>
        Path.Combine(Path.GetTempPath(), "serverui_time_result.txt");

    private static void WriteResult(string s)
    {
        try { File.WriteAllText(ResultPath(), s, Encoding.UTF8); } catch { }
    }

    /* --settime "yyyy-MM-dd HH:mm:ss" */
    public static void ApplyFromArg(string ts)
    {
        DateTime t;
        string err;
        if (DateTime.TryParseExact(ts, "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
            err = SetLocalTime(t);
        else
            err = "时间格式无效: " + ts;
        WriteResult(err == null
            ? "OK 已设置系统时间: " + t.ToString("yyyy-MM-dd HH:mm:ss")
            : "ERR " + err);
    }

    /* --synctime */
    public static void SyncFromArg()
    {
        var err = DoSync();
        WriteResult(err == null
            ? "OK 已同步为网络标准时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            : "ERR " + err);
    }

    /* 主界面读取提权实例的结果 */
    public static string ReadResult()
    {
        try { return File.ReadAllText(ResultPath(), Encoding.UTF8); }
        catch { return null; }
    }

    public static void ClearResult()
    {
        try { if (File.Exists(ResultPath())) File.Delete(ResultPath()); } catch { }
    }
}