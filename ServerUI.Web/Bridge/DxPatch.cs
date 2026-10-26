using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ServerUI.Web.Bridge;

/// <summary>
/// 运行方式补丁：把 AUM管理组件 下的补丁文件复制到游戏根目录。
///
/// 三套互斥，切换时整套替换：
///   · DX11 / DX12 → dgVoodoo：D3D9.dll + dgVoodoo.conf + dgVoodooCpl.exe
///   · Vulkan      → DXVK：DXVK补丁 目录下的全部 DLL
///   · 默认 DX9    → 两套都移除
///
/// 为什么不能只"复制"：
///   · dgVoodoo 的 D3D9.dll 与 DXVK 的 d3d9.dll 在 Windows 上是同一个文件（不区分大小写），
///     切换时必须覆盖，不能按"存在就跳过"。
///   · DXVK 会额外放 dxgi.dll / d3d11.dll / d3d8.dll；这些留在游戏目录里，
///     即使换成 dgVoodoo，游戏也会继续被导到 Vulkan。所以切走时要清掉。
/// </summary>
internal static class DxPatch
{
    public const string ModeOff = "off";
    public const string ModeDx11 = "dx11";
    public const string ModeDx12 = "dx12";
    public const string ModeVulkan = "vulkan";

    public static readonly string[] DgvFiles = { "D3D9.dll", "dgVoodoo.conf", "dgVoodooCpl.exe" };

    /// <summary>DXVK 整套；d3d9.dll 与 dgVoodoo 的 D3D9.dll 在 Windows 上是同一个文件。</summary>
    public static readonly string[] DxvkFiles = { "d3d8.dll", "d3d9.dll", "d3d10core.dll", "d3d11.dll", "dxgi.dll" };

    /// <summary>DXVK 专属文件（d3d9.dll 由复制覆盖，不在此列）。</summary>
    public static readonly string[] DxvkOnly = { "d3d8.dll", "d3d10core.dll", "d3d11.dll", "dxgi.dll" };

    public sealed class Result
    {
        public bool Ok;
        public string Error;
        public string Mode = ModeOff;
        public bool Watermark;
        public int Copied;
        public int Removed;
        /// <summary>(level, text)：level 与宿主日志级别同名（success/warning/error/secondary）。</summary>
        public readonly List<(string Level, string Text)> Messages = new List<(string, string)>();
    }

    /// <summary>归一化：非法值一律回落到 off（= 原生 DX9）。</summary>
    public static string NormalizeMode(string mode)
    {
        var m = (mode ?? "").Trim().ToLowerInvariant();
        return m == ModeDx11 || m == ModeDx12 || m == ModeVulkan ? m : ModeOff;
    }

    public static Result Apply(string mode, bool watermark, string adDir, string grDir)
    {
        var r = new Result { Mode = NormalizeMode(mode), Watermark = watermark };
        var isDgv = r.Mode == ModeDx11 || r.Mode == ModeDx12;
        var isVk = r.Mode == ModeVulkan;

        if (isDgv || isVk)
            r.Messages.Add(("warning", ">>> 提示: 如果游戏无法正常游玩，请取消选择相关可选运行方式"));

        // 水印是 dgVoodoo 的选项，DXVK 没有；先关掉再落盘，避免设置里留假状态
        if (isVk && r.Watermark)
        {
            r.Watermark = false;
            r.Messages.Add(("warning", "Vulkan 方式不使用 dgVoodoo，已忽略「去除水印」选项"));
        }

        if (!Directory.Exists(grDir))
        {
            r.Error = "游戏根目录不存在: " + grDir;
            return r;
        }

        try
        {
            if (isVk) ApplyVulkan(r, adDir, grDir);
            else if (isDgv) ApplyDgv(r, adDir, grDir);
            else ApplyOff(r, adDir, grDir);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            r.Ok = false;
            r.Error = "补丁写入失败（文件可能被游戏占用）：" + ex.Message;
            r.Messages.Add(("error", ">>> DX补丁复制失败: " + ex.Message));
            r.Messages.Add(("error", "DX补丁复制失败：文件可能正被游戏占用。请完全关闭游戏客户端后重试。"));
        }
        return r;
    }

    // ------------------------------------------------------------------ 三种模式

    private static void ApplyVulkan(Result r, string adDir, string grDir)
    {
        var src = Path.Combine(adDir, "DXVK补丁");
        if (!Directory.Exists(src)) { r.Error = "DXVK补丁目录不存在: " + src; return; }

        var files = Directory.GetFiles(src).Select(Path.GetFileName).ToArray();
        if (files.Length == 0) { r.Error = "DXVK补丁目录是空的: " + src; return; }

        // 没有 Vulkan 运行库时 DXVK 起不来：提示但不拦，玩家可自行取消
        if (VulkanLoader() == null)
            r.Messages.Add(("warning", "未检测到 Vulkan 运行库（vulkan-1.dll）。Vulkan 方式需要显卡驱动支持，若游戏无法启动请取消选择。"));

        // dgVoodoo 的配置/调参程序对 DXVK 无用
        r.Removed += Delete(grDir, new[] { "dgVoodoo.conf", "dgVoodooCpl.exe" });

        r.Copied = Copy(src, grDir, files);
        if (r.Copied == 0) { r.Error = "DXVK补丁没有复制成功"; return; }

        r.Ok = true;
        r.Messages.Add(("success", "DXVK (Vulkan) 补丁已复制到游戏目录 (" + r.Copied + " 个文件)"));
    }

    private static void ApplyDgv(Result r, string adDir, string grDir)
    {
        var dx12 = r.Mode == ModeDx12;
        var src = Path.Combine(adDir, dx12 ? "DX12补丁" : "DX11补丁");
        var tag = dx12 ? " (DX12)" : " (DX11)";

        if (!Directory.Exists(src)) { r.Error = (dx12 ? "DX12补丁" : "DX11补丁") + "目录不存在: " + src; return; }

        if (r.Watermark)
        {
            var wm = Path.Combine(src, "无水印");
            if (Directory.Exists(wm)) { src = wm; tag = dx12 ? " (DX12无水印版)" : " (DX11无水印版)"; }
            else r.Messages.Add(("warning", (dx12 ? "DX12" : "DX11") + "无水印目录不存在"));
        }

        // 先清掉 DXVK 专属文件，否则 dxgi/d3d11/d3d8 仍会把游戏往 Vulkan 上导
        r.Removed += Delete(grDir, DxvkOnly);

        r.Copied = Copy(src, grDir, DgvFiles);
        if (r.Copied == 0) { r.Error = "dgVoodoo 补丁没有复制成功"; return; }

        r.Ok = true;
        r.Messages.Add(("success", "DX补丁已复制到游戏目录" + tag + " (" + r.Copied + " 个文件)"));
    }

    private static void ApplyOff(Result r, string adDir, string grDir)
    {
        if (r.Watermark)
            r.Messages.Add(("warning", "请先选择 DX11 / DX12 运行模式再启用水印（Vulkan 不使用 dgVoodoo 水印）"));

        var names = new List<string>(DgvFiles);
        names.AddRange(DxvkOnly);
        names.Add("d3d9.dll");   // 与 DgvFiles 里的 D3D9.dll 是同一个文件，重复无害

        r.Removed = Delete(grDir, names);
        r.Watermark = false;
        r.Ok = true;
        r.Messages.Add(("secondary", "已切换为默认DX9运行，DX补丁已从游戏目录移除 (" + r.Removed + " 个文件)"));
    }

    // ------------------------------------------------------------------ 文件操作

    private static int Copy(string srcDir, string grDir, IEnumerable<string> files)
    {
        var copied = 0;
        foreach (var fn in files)
        {
            var src = Path.Combine(srcDir, fn);
            if (!File.Exists(src)) continue;
            File.Copy(src, Path.Combine(grDir, fn), true);
            copied++;
        }
        return copied;
    }

    private static int Delete(string grDir, IEnumerable<string> names)
    {
        var removed = 0;
        foreach (var fn in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var p = Path.Combine(grDir, fn);
            if (!File.Exists(p)) continue;
            try { File.Delete(p); removed++; }
            catch { /* 被占用：复制阶段会给出明确错误 */ }
        }
        return removed;
    }

    /// <summary>DNF 是 32 位客户端，实际加载 SysWOW64 下的 Vulkan loader（32 位系统则就是 System32）。</summary>
    internal static string VulkanLoader()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var p in new[]
        {
            Path.Combine(win, "SysWOW64", "vulkan-1.dll"),
            Path.Combine(win, "System32", "vulkan-1.dll"),
        })
            if (File.Exists(p)) return p;
        return null;
    }
}