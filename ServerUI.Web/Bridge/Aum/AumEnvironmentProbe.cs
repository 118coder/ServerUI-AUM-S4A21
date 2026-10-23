using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ServerUI.Web.Bridge.Aum;

/// <summary>
/// 环境判定（任务书 §1）：SDK ≥ 10.0、PowerShell ≥ 5.1、磁盘可用 ≥ 2GB，
/// 外加「下载到的源码里同时存在 ServerUI.Web/ServerUI.Web.csproj 与 web/index.html」。
/// 前三项可以立刻探测；后两项必须等源码包下载并校验后才成立，所以拆成
/// <see cref="ProbeEnvironment"/> + <see cref="CheckSourceLayout"/> 两步。
/// </summary>
internal sealed class AumBuildEnvironment
{
    public bool SdkOk;
    public string SdkVersion;
    public string DotnetPath;
    public bool PowerShellOk;
    public string PowerShellVersion;
    public bool DiskOk;
    public long FreeBytes;
    public string DiskRoot;
    public bool SourceLayoutOk;
    public string SourceError;

    public bool Ready => SdkOk && PowerShellOk && DiskOk && SourceLayoutOk;

    public string Missing()
    {
        var miss = new List<string>();
        if (!SdkOk) miss.Add(".NET 10 SDK");
        if (!PowerShellOk) miss.Add("PowerShell 5.1");
        if (!DiskOk) miss.Add("磁盘可用空间 2GB");
        if (!SourceLayoutOk) miss.Add("源码结构（ServerUI.Web.csproj/web/index.html）");
        return miss.Count == 0 ? "无" : string.Join("、", miss);
    }

    /// <summary>可读的原因说明（写进确认框与日志）。</summary>
    public string Describe()
    {
        var parts = new List<string>();
        parts.Add(SdkOk ? "已检测到 .NET " + (SdkVersion ?? "10") + " SDK" : "未检测到 .NET 10 SDK");
        parts.Add(PowerShellOk ? "PowerShell " + (PowerShellVersion ?? "5.1") : "PowerShell 低于 5.1");
        parts.Add(DiskOk
            ? "磁盘可用 " + AumNet.FormatSize(FreeBytes) + "（" + DiskRoot + "）"
            : "磁盘可用仅 " + AumNet.FormatSize(FreeBytes) + "（需 ≥ 2GB）");
        if (!SourceLayoutOk) parts.Add("源码不可用：" + (SourceError ?? "结构不符"));
        return string.Join("；", parts);
    }

    public object ToJson() => new
    {
        ready = Ready,
        sdk = SdkOk,
        sdkVersion = SdkVersion,
        dotnet = DotnetPath,
        powershell = PowerShellOk,
        powershellVersion = PowerShellVersion,
        disk = DiskOk,
        freeBytes = FreeBytes,
        diskRoot = DiskRoot,
        sourceLayout = SourceLayoutOk,
        sourceError = SourceError,
        missing = Missing()
    };
}

/// <summary>构建环境探测（只读：跑 dotnet --version / powershell -Command，读磁盘剩余空间）。</summary>
internal static class AumEnvironmentProbe
{
    public const long MinFreeBytes = 2L * 1024 * 1024 * 1024;

    public static AumBuildEnvironment Probe(string currentExePath)
    {
        var env = new AumBuildEnvironment();

        var (sdkVer, dotnetPath) = ProbeDotnet();
        env.SdkVersion = sdkVer;
        env.DotnetPath = dotnetPath;
        env.SdkOk = !string.IsNullOrEmpty(sdkVer) && AumVersion.Major(sdkVer) >= 10;

        var (psVer, psOk) = ProbePowerShell();
        env.PowerShellVersion = psVer;
        env.PowerShellOk = psOk;

        try
        {
            var root = Path.GetPathRoot(string.IsNullOrEmpty(currentExePath) ? Paths.Base : currentExePath);
            if (string.IsNullOrEmpty(root)) root = "C:\\";
            env.DiskRoot = root;
            var drive = new DriveInfo(root);
            env.FreeBytes = drive.AvailableFreeSpace;
            env.DiskOk = env.FreeBytes >= MinFreeBytes;
        }
        catch
        {
            env.DiskRoot = "?";
            env.FreeBytes = 0;
            env.DiskOk = false;
        }

        return env;
    }

    /// <summary>按 PATH → Program Files(x64/x86) → AUM管理组件\dotnet-sdk 的顺序找 ≥10 的 SDK。</summary>
    private static (string Version, string Path) ProbeDotnet()
    {
        var candidates = new List<string> { "dotnet" };
        try
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(pf)) candidates.Add(Path.Combine(pf, "dotnet", "dotnet.exe"));
            if (!string.IsNullOrEmpty(pf86)) candidates.Add(Path.Combine(pf86, "dotnet", "dotnet.exe"));
        }
        catch { }
        try { candidates.Add(Path.Combine(Paths.Ad, "dotnet-sdk", "dotnet.exe")); } catch { }

        foreach (var c in candidates)
        {
            if (c != "dotnet" && !File.Exists(c)) continue;
            var ver = RunCapture(c, "--version", 6000);
            if (string.IsNullOrEmpty(ver)) continue;
            ver = ver.Trim();
            if (AumVersion.Major(ver) >= 10) return (ver, c);
        }
        return (null, null);
    }

    private static (string Version, bool Ok) ProbePowerShell()
    {
        var outText = RunCapture("powershell.exe",
            "-NoProfile -NonInteractive -Command \"$PSVersionTable.PSVersion.ToString()\"", 8000);
        if (string.IsNullOrWhiteSpace(outText)) return (null, false);
        var ver = outText.Trim().Split('\r', '\n')[0].Trim();
        if (!Version.TryParse(ver, out var v)) return (ver, false);
        return (ver, v >= new Version(5, 1));
    }

    private static string RunCapture(string exe, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            var text = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
                return null;
            }
            return p.ExitCode == 0 ? text : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// 源码结构校验：GitHub 的源码 zip 解压后有一层 {owner}-{repo}-{sha}/ 前缀，
    /// 只认「根 或 唯一一层子目录」下的 ServerUI.Web/ServerUI.Web.csproj 与 web/index.html。
    /// 缺任一项 = 不可编译（防止有人把旧 WinForms 代码发上来导致界面回退）。
    /// </summary>
    public static bool CheckSourceLayout(string extractRoot, out string projectDir, out string error)
    {
        projectDir = null;
        error = null;
        try
        {
            var roots = new List<string> { extractRoot };
            var subs = Directory.GetDirectories(extractRoot);
            if (subs.Length == 1) roots.Add(subs[0]);

            foreach (var root in roots)
            {
                var proj = Path.Combine(root, "ServerUI.Web");
                var csproj = Path.Combine(proj, "ServerUI.Web.csproj");
                var index = Path.Combine(proj, "web", "index.html");
                if (File.Exists(csproj) && File.Exists(index))
                {
                    projectDir = proj;
                    return true;
                }
            }

            var hasOld = File.Exists(Path.Combine(extractRoot, "ServerUI", "ServerUI.csproj"))
                         || subs.Any(d => File.Exists(Path.Combine(d, "ServerUI", "ServerUI.csproj")));
            error = hasOld
                ? "源码包内只有旧版 ServerUI/ServerUI.csproj（WinForms），缺少 ServerUI.Web/ServerUI.Web.csproj 与 web/index.html"
                : "源码包内缺少 ServerUI.Web/ServerUI.Web.csproj 或 web/index.html";
            return false;
        }
        catch (Exception ex)
        {
            error = "检查源码结构失败：" + ex.Message;
            return false;
        }
    }
}
