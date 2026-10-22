using System;
using System.IO;

namespace ServerUI.Web.Bridge;

/// <summary>
/// 路径解析 — 对齐旧 MainForm 构造器里的 _bd / _ad / _gr 推导：
///   _bd = 管理器 exe 所在目录 (AppContext.BaseDirectory)
///   _ad = _bd 下有 "AUM管理组件" 时取该子目录，否则就是 _bd
///   _gr = _ad 的父目录（游戏根目录，DNF.exe / GameGaurd.ini 所在）
/// </summary>
internal static class Paths
{
    /// <summary>_bd</summary>
    public static string Base => AppContext.BaseDirectory.TrimEnd('\\', '/');

    /// <summary>_ad</summary>
    public static string Ad
    {
        get
        {
            var b = Base;
            var sub = Path.Combine(b, "AUM管理组件");
            return Directory.Exists(sub) ? sub : b;
        }
    }

    /// <summary>_gr</summary>
    public static string Gr
    {
        get
        {
            var ad = Ad;
            var p = Directory.GetParent(ad);
            return p != null ? p.FullName : ad;
        }
    }

    /// <summary>设置 / 日志等可写数据的根目录（测试用 AUM_DATA_ROOT 覆盖）</summary>
    public static string DataRoot
    {
        get
        {
            var over = Environment.GetEnvironmentVariable("AUM_DATA_ROOT");
            if (!string.IsNullOrWhiteSpace(over)) return over.TrimEnd('\\', '/');
            return Ad;
        }
    }

    public static string SettingsFile => Path.Combine(DataRoot, "设置.json");

    // ---------------- 旧工程使用到的固定路径 ----------------
    public static string ServerRoot => Path.Combine(Ad, "ServerS4A21-AUM");
    public static string DistDir => Path.Combine(ServerRoot, "dist", "win-x64");
    public static string DataDir => Path.Combine(DistDir, "Data");
    public static string PvfDir => Path.Combine(DataDir, "Pvf");
    public static string ServerPvf => Path.Combine(PvfDir, "Script.pvf");
    public static string InventoryDb => Path.Combine(DataDir, "inventory.db");

    public static string SwitchDir => Path.Combine(Ad, "存档管理", "切换库");
    public static string BackupDir => Path.Combine(Ad, "存档管理", "备份存档");

    public static string Dx11Dir => Path.Combine(Ad, "DX11补丁");
    public static string Dx12Dir => Path.Combine(Ad, "DX12补丁");
    public static string ToolPackDir => Path.Combine(Ad, "实用工具包");
    public static string PatchDir => Path.Combine(ToolPackDir, "DLL覆盖");
    public static string PatchZip => Path.Combine(PatchDir, "客户端补丁.zip");
    public static string OriginalPvfSource => Path.Combine(ToolPackDir, "PVF原版", "Script.pvf");
    public static string GmToolDir => Path.Combine(Ad, "dfogmtool");
    public static string GmToolExe => Path.Combine(GmToolDir, "publish", "DfoGmTool.exe");
    public static string SdkDir => Path.Combine(Ad, "dotnet-sdk");
    public static string SdkInstaller => Path.Combine(SdkDir, "dotnet-sdk-10.0.302-win-x64.exe");

    public static string UpdateLogFile => Path.Combine(Ad, "更新日志.txt");
    public static string RunningLogFile => Path.Combine(Ad, "运行日志.txt");
    public static string GameGaurdIni => Path.Combine(Gr, "GameGaurd.ini");
    public static string GameDnfExe => Path.Combine(Gr, "DNF.exe");

    /// <summary>游戏根目录的 Script.pvf（还原-原版PVF 的客户端目标）</summary>
    public static string ClientPvf => Path.Combine(Gr, "Script.pvf");

    /// <summary>游戏启动脚本：游戏根（_gr）下的「本地游戏S4.bat」。
    /// exe 放在游戏根或 AUM管理组件 下都成立（旧逻辑按 exe 父目录推导，exe 在根目录时会指到上一层）。</summary>
    public static string LocalGameBat
    {
        get
        {
            var grBat = Path.Combine(Gr, "本地游戏S4.bat");
            if (File.Exists(grBat)) return grBat;
            var p = Directory.GetParent(Base);
            return p != null ? Path.Combine(p.FullName, "本地游戏S4.bat") : grBat;
        }
    }

    public static string SingleGameBat => Path.Combine(Ad, "单机游戏启动.bat");

    /// <summary>WebView2 用户数据目录（绝不放在游戏目录；AUM_WEBVIEW_DATA 可覆盖）</summary>
    public static string WebViewData
    {
        get
        {
            var over = Environment.GetEnvironmentVariable("AUM_WEBVIEW_DATA");
            if (!string.IsNullOrWhiteSpace(over)) return over.TrimEnd('\\', '/');
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ServerUI.Web", "WebView2");
        }
    }

    /// <summary>前端解压根目录（%LOCALAPPDATA%\ServerUI.Web\web）</summary>
    public static string WebCacheRoot
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ServerUI.Web", "web");
        }
    }

    /// <summary>AUM_WEB_ROOT 开发覆盖：直接映射该目录（热改 CSS/JS 不用重编译）</summary>
    public static string DevWebRoot
    {
        get
        {
            var over = Environment.GetEnvironmentVariable("AUM_WEB_ROOT");
            if (!string.IsNullOrWhiteSpace(over) && Directory.Exists(over)) return over;
            return null;
        }
    }

    public static bool IsAdmin
    {
        get { try { return ServerUI.TimeTool.IsAdmin(); } catch { return false; } }
    }
}
