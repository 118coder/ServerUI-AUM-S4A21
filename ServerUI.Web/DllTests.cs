using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ServerUI.Web.Bridge;

namespace ServerUI.Web;

/// <summary>
/// `--dlltest`：DLL 扩展页「开关只是挂载、删除才消失」的沙箱验收。
///
/// 在 %TEMP% 造一套游戏根目录（GameGaurd.ini + 挂载器 + 一个自定义 DLL），
/// 走真实的 DllController：添加 → 取消勾选并应用 → 断言行还在（on=false）→ 再勾上 → 删除后行才消失。
/// </summary>
internal static class DllTests
{
    private static int _pass, _fail;
    private static readonly StringBuilder Report = new StringBuilder();

    internal static int Run(string[] args)
    {
        var root = Path.Combine(Path.GetTempPath(), "ServerUI-dlltest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        var savedDataRoot = Environment.GetEnvironmentVariable("AUM_DATA_ROOT");
        var savedAdRoot = Environment.GetEnvironmentVariable("AUM_AD_ROOT");
        var savedGrRoot = Environment.GetEnvironmentVariable("AUM_GR_ROOT");

        try
        {
            var ad = Path.Combine(root, "AUM管理组件");
            var gr = Path.Combine(root, "游戏根");
            Directory.CreateDirectory(ad);
            Directory.CreateDirectory(gr);
            // 三处都指向沙箱：否则 Paths 会按 exe 位置推导，写到 bin 目录里
            Environment.SetEnvironmentVariable("AUM_DATA_ROOT", ad);
            Environment.SetEnvironmentVariable("AUM_AD_ROOT", ad);
            Environment.SetEnvironmentVariable("AUM_GR_ROOT", gr);

            // 夹具：挂载器 + 初始 ini（受管插件全开，无自定义）
            File.WriteAllText(Path.Combine(gr, "GameGaurd.dll"), "");
            var ini = "[Plugins]\r\nPlugin0=GameNative.dll\r\nPlugin1=MultiInstance.dll\r\n";
            File.WriteAllText(Path.Combine(gr, "GameGaurd.ini"), ini, new UTF8Encoding(false));

            var extSrc = Path.Combine(root, "MyPlugin.dll");
            File.WriteAllText(extSrc, "fake-dll");

            var ui = new SilentUi();
            var dll = new DllController(ui);

            // 1. 添加扩展 → 出现在列表且默认勾选
            var addRes = dll.Add(extSrc);
            Check("添加扩展 · 成功", "ok=true", "ok=" + Ok(addRes) + " err=" + Err(addRes), Ok(addRes));
            var st = StateOf(dll);
            var row = Find(st, "MyPlugin.dll");
            Check("添加扩展 · 出现在列表且勾选", "存在且 on=true",
                row == null ? "(未找到)" : ("on=" + row.On + " exists=" + row.Exists),
                row != null && row.On);

            // 2. 取消勾选并应用 → 行必须还在，只是 on=false（这是本次修复的核心）
            var managedOn = st.Managed.Where(m => m.On).Select(m => m.File).ToList();
            var applyRes = dll.Apply(managedOn, new List<string>(), null, null);
            Check("取消勾选 · 应用成功", "ok=true", "ok=" + Ok(applyRes) + " err=" + Err(applyRes), Ok(applyRes));
            st = StateOf(dll);
            row = Find(st, "MyPlugin.dll");
            Check("取消勾选后 · 行仍在列表", "存在且 on=false",
                row == null ? "(行已消失——就是本次要修的 bug)" : ("on=" + row.On),
                row != null && !row.On);
            Check("取消勾选后 · ini 已移除条目", "GameGaurd.ini 不含 MyPlugin.dll",
                ReadIni(gr),
                ReadIni(gr).IndexOf("MyPlugin.dll", StringComparison.OrdinalIgnoreCase) < 0);
            Check("取消勾选后 · 文件仍保留在游戏根目录", "MyPlugin.dll 存在",
                File.Exists(Path.Combine(gr, "MyPlugin.dll")) ? "存在" : "已被删除",
                File.Exists(Path.Combine(gr, "MyPlugin.dll")));

            // 3. 重新勾选并应用 → 回到挂载状态
            managedOn = StateOf(dll).Managed.Where(m => m.On).Select(m => m.File).ToList();
            dll.Apply(managedOn, new List<string> { "MyPlugin.dll" }, null, null);
            st = StateOf(dll);
            row = Find(st, "MyPlugin.dll");
            Check("重新勾选 · 恢复挂载", "on=true 且 ini 含条目",
                "on=" + (row == null ? "(缺失)" : row.On.ToString()) + " ini=" + ReadIni(gr),
                row != null && row.On
                    && ReadIni(gr).IndexOf("MyPlugin.dll", StringComparison.OrdinalIgnoreCase) >= 0);

            // 4. 重复添加同一文件 → 提示已在列表（不应被当成新文件覆盖顺序）
            var dup = dll.Add(extSrc);
            Check("重复添加 · 提示已在列表", "ok=false 且提示「已在扩展列表」",
                "ok=" + Ok(dup) + " err=" + Err(dup),
                !Ok(dup) && (Err(dup) ?? "").Contains("已在扩展列表", StringComparison.Ordinal));

            // 5. 删除扩展 → 行才消失
            var delRes = dll.Delete("MyPlugin.dll", false);
            Check("删除扩展 · 成功", "ok=true", "ok=" + Ok(delRes) + " err=" + Err(delRes), Ok(delRes));
            st = StateOf(dll);
            Check("删除扩展后 · 行消失", "列表不含 MyPlugin.dll",
                Find(st, "MyPlugin.dll") == null ? "(已消失)" : "(仍存在)",
                Find(st, "MyPlugin.dll") == null);

            // 6. 删除后可以重新添加
            var reAdd = dll.Add(extSrc);
            Check("删除后可重新添加", "ok=true 且重新出现",
                "ok=" + Ok(reAdd) + " 出现=" + (Find(StateOf(dll), "MyPlugin.dll") != null),
                Ok(reAdd) && Find(StateOf(dll), "MyPlugin.dll") != null);

            // 7. 玩家用启动器手动挂载的条目（ini 有、清单没有）也应显示
            var iniText = IniFile.ReadText(Path.Combine(gr, "GameGaurd.ini"));
            File.WriteAllText(Path.Combine(gr, "GameGaurd.ini"),
                iniText.TrimEnd() + "\r\nPlugin99=LauncherAdded.dll\r\n", new UTF8Encoding(false));
            st = StateOf(dll);
            Check("ini 里手工挂载的条目 · 也显示", "LauncherAdded.dll 在列表中",
                Find(st, "LauncherAdded.dll") == null ? "(未显示)" : "已显示",
                Find(st, "LauncherAdded.dll") != null);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[FAIL] 测试框架异常 · 实际：" + ex);
            _fail++;
        }
        finally
        {
            Environment.SetEnvironmentVariable("AUM_DATA_ROOT", savedDataRoot);
            Environment.SetEnvironmentVariable("AUM_AD_ROOT", savedAdRoot);
            Environment.SetEnvironmentVariable("AUM_GR_ROOT", savedGrRoot);
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine("===== 汇总：" + _pass + " 通过 / " + _fail + " 失败 =====");
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "ServerUI-dlltest.txt"), Report.ToString(), new UTF8Encoding(false)); }
        catch { }
        return _fail == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ 工具

    private sealed class Row
    {
        public string File;
        public bool On;
        public bool Exists;
    }

    private sealed class DllState
    {
        public List<Row> Managed = new List<Row>();
        public List<Row> Custom = new List<Row>();
    }

    /// <summary>用反射读匿名对象：DllController.State() 返回匿名类型，测试里不便强转。</summary>
    private static DllState StateOf(DllController dll) => Parse(dll.State());

    private static DllState Parse(object state)
    {
        var s = new DllState();
        var t = state.GetType();
        ReadRows(t.GetProperty("managed").GetValue(state), s.Managed);
        ReadRows(t.GetProperty("custom").GetValue(state), s.Custom);
        return s;
    }

    private static void ReadRows(object listObj, List<Row> into)
    {
        if (listObj is System.Collections.IEnumerable en)
        {
            foreach (var item in en)
            {
                var t = item.GetType();
                into.Add(new Row
                {
                    File = (string)t.GetProperty("file").GetValue(item),
                    On = (bool)t.GetProperty("on").GetValue(item),
                    Exists = (bool)t.GetProperty("exists").GetValue(item),
                });
            }
        }
    }

    private static Row Find(DllState st, string file)
        => st.Custom.FirstOrDefault(r => r.File.Equals(file, StringComparison.OrdinalIgnoreCase))
        ?? st.Managed.FirstOrDefault(r => r.File.Equals(file, StringComparison.OrdinalIgnoreCase));

    private static bool Ok(object res) => res != null && (bool)res.GetType().GetProperty("ok").GetValue(res);

    private static string Err(object res)
    {
        var p = res?.GetType().GetProperty("error");
        return p == null ? "" : (string)p.GetValue(res) ?? "";
    }

    private static string ReadIni(string gr)
    {
        try { return File.ReadAllText(Path.Combine(gr, "GameGaurd.ini")); }
        catch { return "(读取失败)"; }
    }

    private sealed class SilentUi : IUi
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

    private static void Check(string name, string expect, string actual, bool ok)
    {
        if (ok) { _pass++; Console.WriteLine("[PASS] " + name); }
        else { _fail++; Console.WriteLine("[FAIL] " + name); }
        Console.WriteLine("       预期: " + expect);
        Console.WriteLine("       实际: " + Trunc(actual));
        Report.AppendLine((ok ? "[PASS] " : "[FAIL] ") + name);
        Report.AppendLine("  预期: " + expect);
        Report.AppendLine("  实际: " + Trunc(actual));
    }

    private static string Trunc(string s)
    {
        if (string.IsNullOrEmpty(s)) return "(空)";
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length > 240 ? s.Substring(0, 240) + "…" : s;
    }
}