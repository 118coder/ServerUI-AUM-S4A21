using System;
using System.IO;
using System.Linq;
using System.Text;
using ServerUI.Web.Bridge;

namespace ServerUI.Web;

/// <summary>
/// `--dxtest`：运行方式补丁切换的沙箱验收（不碰真实游戏目录）。
///
/// 在 %TEMP% 下造一套 AUM管理组件（DX11/DX12/DXVK 补丁目录）和空游戏根目录，
/// 依次切换 vulkan / dx11 / dx12 / off，检查文件落位与跨模式清理。
/// </summary>
internal static class DxTests
{
    private static int _pass, _fail;
    private static readonly StringBuilder Report = new StringBuilder();

    internal static int Run(string[] args)
    {
        var root = Path.Combine(Path.GetTempPath(), "ServerUI-dxtest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        var ad = Path.Combine(root, "AUM管理组件");
        var gr = Path.Combine(root, "游戏根");

        try
        {
            Directory.CreateDirectory(ad);
            Directory.CreateDirectory(gr);
            BuildFixtures(ad);

            // 1. 切到 Vulkan
            var r = Apply("vulkan", ad, gr);
            Check("Vulkan · 复制 5 个 DXVK 文件", "ok=true 且 5 个文件内容为 DXVK 版",
                "ok=" + r.Ok + " copied=" + r.Copied + " | " + Describe(gr, DxPatch.DxvkFiles),
                r.Ok && r.Copied == 5 && DxPatch.DxvkFiles.All(f => Content(gr, f) == "vk-" + f));
            Check("Vulkan · 不残留 dgVoodoo 配置", "无 dgVoodoo.conf / dgVoodooCpl.exe",
                Exists(gr, "dgVoodoo.conf") + "," + Exists(gr, "dgVoodooCpl.exe"),
                !Exists(gr, "dgVoodoo.conf") && !Exists(gr, "dgVoodooCpl.exe"));

            // 2. Vulkan → DX11
            r = Apply("dx11", ad, gr);
            Check("Vulkan→DX11 · 换成 dgVoodoo 三件套", "D3D9.dll=dgv11 且 conf/cpl 都在",
                "D3D9=" + Content(gr, "D3D9.dll") + " conf=" + Exists(gr, "dgVoodoo.conf"),
                r.Ok && Content(gr, "D3D9.dll") == "dgv11-D3D9.dll"
                     && Content(gr, "dgVoodoo.conf") == "dgv11-dgVoodoo.conf"
                     && Exists(gr, "dgVoodooCpl.exe"));
            Check("Vulkan→DX11 · 清掉 DXVK 专属文件", "无 d3d8/d3d10core/d3d11/dxgi",
                string.Join(",", DxPatch.DxvkOnly.Where(f => Exists(gr, f))),
                DxPatch.DxvkOnly.All(f => !Exists(gr, f)));

            // 3. DX12 + 无水印
            r = Apply("dx12", ad, gr, watermark: true);
            Check("DX12 无水印 · 取无水印目录", "D3D9.dll=dgv12wm-D3D9.dll",
                Content(gr, "D3D9.dll"),
                r.Ok && Content(gr, "D3D9.dll") == "dgv12wm-D3D9.dll");

            // 4. DX12 → Vulkan
            r = Apply("vulkan", ad, gr);
            Check("DX12→Vulkan · 清 dgVoodoo 配置并换回 DXVK", "无 conf/cpl，d3d9=vk",
                "conf=" + Exists(gr, "dgVoodoo.conf") + " cpl=" + Exists(gr, "dgVoodooCpl.exe") + " d3d9=" + Content(gr, "d3d9.dll"),
                r.Ok && !Exists(gr, "dgVoodoo.conf") && !Exists(gr, "dgVoodooCpl.exe")
                     && Content(gr, "d3d9.dll") == "vk-d3d9.dll");

            // 5. Vulkan → 默认 DX9
            r = Apply("off", ad, gr);
            var left = Directory.GetFiles(gr).Select(Path.GetFileName).ToArray();
            Check("Vulkan→DX9 · 全部移除", "游戏目录为空",
                left.Length == 0 ? "(空)" : string.Join(",", left),
                r.Ok && left.Length == 0);

            // 6. DXVK 目录缺失 → 明确失败且不动目录
            Directory.Move(Path.Combine(ad, "DXVK补丁"), Path.Combine(ad, "DXVK补丁_off"));
            r = Apply("vulkan", ad, gr);
            Check("缺 DXVK 目录 · 拒绝", "ok=false 且目录仍为空",
                "ok=" + r.Ok + " err=" + r.Error,
                !r.Ok && Directory.GetFiles(gr).Length == 0);
            Directory.Move(Path.Combine(ad, "DXVK补丁_off"), Path.Combine(ad, "DXVK补丁"));

            // 7. Vulkan + 水印 → 强制关闭
            r = Apply("vulkan", ad, gr, watermark: true);
            Check("Vulkan + 水印 · 强制关闭", "watermark=false 且有提示",
                "wm=" + r.Watermark,
                r.Ok && !r.Watermark && r.Messages.Exists(m => m.Text.Contains("已忽略", StringComparison.Ordinal)));
            Apply("off", ad, gr);

            // 8. 非法模式 = 默认 DX9
            r = Apply("bogus", ad, gr);
            Check("非法模式回落 DX9", "ok=true 且 mode=off",
                "ok=" + r.Ok + " mode=" + r.Mode,
                r.Ok && r.Mode == DxPatch.ModeOff);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[FAIL] 测试框架异常 · 实际：" + ex);
            _fail++;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine("===== 汇总：" + _pass + " 通过 / " + _fail + " 失败 =====");
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "ServerUI-dxtest.txt"), Report.ToString(), new UTF8Encoding(false)); }
        catch { }
        return _fail == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ 夹具与工具

    private static DxPatch.Result Apply(string mode, string ad, string gr, bool watermark = false)
        => DxPatch.Apply(mode, watermark, ad, gr);

    private static void BuildFixtures(string ad)
    {
        foreach (var (dirName, tag) in new[] { ("DX11补丁", "dgv11"), ("DX12补丁", "dgv12") })
        {
            var d = Path.Combine(ad, dirName);
            Directory.CreateDirectory(d);
            WriteSet(d, tag);
            var wm = Path.Combine(d, "无水印");
            Directory.CreateDirectory(wm);
            WriteSet(wm, tag + "wm");
        }

        var vk = Path.Combine(ad, "DXVK补丁");
        Directory.CreateDirectory(vk);
        foreach (var f in DxPatch.DxvkFiles)
            File.WriteAllText(Path.Combine(vk, f), "vk-" + f);
    }

    private static void WriteSet(string dir, string tag)
    {
        File.WriteAllText(Path.Combine(dir, "D3D9.dll"), tag + "-D3D9.dll");
        File.WriteAllText(Path.Combine(dir, "dgVoodoo.conf"), tag + "-dgVoodoo.conf");
        File.WriteAllText(Path.Combine(dir, "dgVoodooCpl.exe"), tag + "-dgVoodooCpl.exe");
    }

    private static bool Exists(string gr, string name) => File.Exists(Path.Combine(gr, name));

    private static string Content(string gr, string name)
    {
        try
        {
            var p = Path.Combine(gr, name);
            return File.Exists(p) ? File.ReadAllText(p) : "(缺失)";
        }
        catch { return "(读取失败)"; }
    }

    private static string Describe(string gr, string[] files)
        => string.Join(" | ", files.Select(f => f + "=" + Content(gr, f)));

    private static void Check(string name, string expect, string actual, bool ok)
    {
        if (ok) { _pass++; Console.WriteLine("[PASS] " + name); }
        else { _fail++; Console.WriteLine("[FAIL] " + name); }
        Console.WriteLine("       预期: " + expect);
        Console.WriteLine("       实际: " + actual);
        Report.AppendLine((ok ? "[PASS] " : "[FAIL] ") + name);
        Report.AppendLine("  预期: " + expect);
        Report.AppendLine("  实际: " + actual);
    }
}