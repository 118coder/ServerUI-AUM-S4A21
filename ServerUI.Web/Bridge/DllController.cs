using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using ServerUI;

namespace ServerUI.Web.Bridge;

/// <summary>
/// DLL 扩展（客户端补丁挂载）— 移植 MainForm.DllExt.cs 的非 UI 逻辑。
///   · 受管插件 = 客户端补丁.zip 内 GameGaurd.ini [Plugins] 的 8 个内置插件
///   · 自定义扩展 = GameGaurd.ini 中非受管的 Plugin* 条目（文件保留在游戏根目录）
/// GameGaurd.ini 一律 UTF-8 无 BOM 写回；读取顺序 UTF-8 BOM → 严格 UTF-8 → GBK(936)。
/// Phase 1 只提供 bridge 命令；DLL 页 UI（拖拽排序 / INI 分组编辑器 / 内置文件浏览器）在 Phase 2。
/// </summary>
internal sealed class DllController
{
    internal static readonly (string File, string Name, string Desc, string Config)[] DllPlugins =
    {
        ("GameNative.dll",      "游戏原生整合",      "原生层游戏功能整合补丁（必选，不可关闭）", null),
        ("PreventMinimize.dll", "防止窗口最小化",    "防止游戏窗口意外最小化", null),
        ("MultiInstance.dll",   "游戏多开",          "支持同时运行多个游戏客户端", null),
        ("AutoFire.dll",        "自动连发",          "按住攻击键自动连续攻击", "AutoFire.ini"),
        ("EquipmentSwap.dll",   "一键换装",          "快捷切换预设装备插件，在游戏中按下end呼出，(EquipmentSwap.dll)", null),
        ("DpsMeter.dll",        "DPS 统计",          "战斗中实时统计输出", "DpsMeter.ini"),
        ("CombatPower.dll",     "战斗力显示",        "实时显示角色战斗力", "CombatPower.ini"),
        ("ImeFix.dll",          "现代输入法兼容插件", "兼容现代输入法（拼音/五笔等）在游戏中正常输入文字", "ImeFix.ini"),
    };

    private readonly IUi _ui;

    // 顺序（对应旧 _managedOrder / _custRows；Phase 2 拖拽排序后由 dll.setOrder 写入）
    private readonly List<string> _managedOrder = new List<string>();
    private readonly List<string> _customOrder = new List<string>();

    public DllController(IUi ui) { _ui = ui; }

    private static bool IsManaged(string file)
    {
        foreach (var p in DllPlugins)
            if (p.File.Equals(file, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsGameNative(string file)
        => file.Equals("GameNative.dll", StringComparison.OrdinalIgnoreCase);

    private static string GameRootFile(string file) => Path.Combine(Paths.Gr, file);

    private static string ExtractPatchZipEntry(string entry)
    {
        if (!File.Exists(Paths.PatchZip)) return null;
        try
        {
            using var zf = ZipFile.OpenRead(Paths.PatchZip);
            foreach (var en in zf.Entries)
            {
                if (!en.FullName.Equals(entry, StringComparison.OrdinalIgnoreCase)) continue;
                if (en.Length <= 0) continue;
                using var sr = new StreamReader(en.Open(), new UTF8Encoding(false), true);
                return sr.ReadToEnd();
            }
        }
        catch { }
        return null;
    }

    // ================================================================
    // 状态（旧 RefreshDllState 的数据部分）
    // ================================================================

    public object State()
    {
        var iniPath = Paths.GameGaurdIni;
        var iniSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var customInFile = new List<string>();
        var managedInFile = new List<string>();
        var hasPlugins = false;

        if (File.Exists(iniPath))
        {
            foreach (var raw in IniFile.ReadLines(iniPath))
            {
                var t = raw.Trim();
                var eq = t.IndexOf('=');
                if (eq <= 0 || !t.StartsWith("Plugin", StringComparison.OrdinalIgnoreCase)) continue;
                var v = t.Substring(eq + 1).Trim();
                if (v.Length == 0) continue;
                hasPlugins = true;
                iniSet.Add(v);
                if (!IsManaged(v))
                {
                    if (!customInFile.Contains(v, StringComparer.OrdinalIgnoreCase)) customInFile.Add(v);
                }
                else if (!managedInFile.Contains(v, StringComparer.OrdinalIgnoreCase))
                    managedInFile.Add(v);
            }
        }

        // 受管顺序：GameNative 恒首位 → ini 中出现的顺序 → DllPlugins 定义顺序补齐
        if (_managedOrder.Count == 0)
        {
            foreach (var p in DllPlugins)
                if (IsGameNative(p.File)) { _managedOrder.Add(p.File); break; }
            foreach (var f in managedInFile)
                if (IsManaged(f) && !_managedOrder.Contains(f, StringComparer.OrdinalIgnoreCase))
                    _managedOrder.Add(f);
            foreach (var p in DllPlugins)
                if (!_managedOrder.Contains(p.File, StringComparer.OrdinalIgnoreCase))
                    _managedOrder.Add(p.File);
        }
        var loaderInstalled = File.Exists(Path.Combine(Paths.Gr, "GameGaurd.dll"))
            || File.Exists(Path.Combine(Paths.Gr, "az.dll"));
        var installed = loaderInstalled || hasPlugins;

        var managed = new List<object>();
        var onManaged = 0;
        foreach (var f in _managedOrder)
        {
            var def = DllPlugins.FirstOrDefault(p => p.File.Equals(f, StringComparison.OrdinalIgnoreCase));
            var native = IsGameNative(f);
            var on = installed && (native || iniSet.Contains(f));
            if (on) onManaged++;
            managed.Add(new
            {
                file = f,
                name = def.Name ?? f,
                desc = def.Desc ?? "",
                config = def.Config,
                on,
                locked = native,
                exists = File.Exists(GameRootFile(f))
            });
        }

        // 自定义扩展行 = 已添加清单 ∪ ini 里的非受管条目（兼容玩家用启动器手动挂载的）。
        // 关键：清单里的条目即使被取消勾选（已从 ini 移除）也要留在界面上，否则开关一点行就没了。
        var known = DllCustomStore.Load(Paths.DataRoot);
        foreach (var f in customInFile)
            if (!known.Contains(f, StringComparer.OrdinalIgnoreCase)) known.Add(f);

        // 显示顺序：内存里的顺序（拖拽结果）优先，其余按清单顺序补齐
        var customFiles = new List<string>();
        foreach (var f in _customOrder)
            if (known.Contains(f, StringComparer.OrdinalIgnoreCase) && !customFiles.Contains(f, StringComparer.OrdinalIgnoreCase))
                customFiles.Add(f);
        foreach (var f in known)
            if (!customFiles.Contains(f, StringComparer.OrdinalIgnoreCase)) customFiles.Add(f);

        _customOrder.Clear();
        _customOrder.AddRange(customFiles);

        var custom = new List<object>();
        var onCustom = 0;
        foreach (var f in customFiles)
        {
            var on = iniSet.Contains(f);
            if (on) onCustom++;
            custom.Add(new
            {
                file = f,
                name = Path.GetFileNameWithoutExtension(f),
                on,
                exists = File.Exists(GameRootFile(f))
            });
        }

        var status = !installed
            ? "未安装 DLL 扩展：游戏根目录未发现补丁文件，请先点击【新DLL安装】"
            : "受管插件已启用 " + onManaged + " / " + DllPlugins.Length
              + " · 自定义扩展 已勾选 " + onCustom + " / " + custom.Count + " → GameGaurd.ini";

        return new
        {
            ok = true,
            installed,
            loaderInstalled,
            iniPath,
            iniExists = File.Exists(iniPath),
            managed,
            custom,
            managedOrder = _managedOrder,
            customOrder = _customOrder,
            status
        };
    }

    public object SetOrder(List<string> managedOrder, List<string> customOrder)
    {
        if (managedOrder != null && managedOrder.Count > 0)
        {
            _managedOrder.Clear();
            foreach (var p in DllPlugins)
                if (IsGameNative(p.File)) { _managedOrder.Add(p.File); break; }
            foreach (var f in managedOrder)
                if (IsManaged(f) && !_managedOrder.Contains(f, StringComparer.OrdinalIgnoreCase))
                    _managedOrder.Add(f);
            foreach (var p in DllPlugins)
                if (!_managedOrder.Contains(p.File, StringComparer.OrdinalIgnoreCase))
                    _managedOrder.Add(p.File);
        }
        if (customOrder != null && customOrder.Count > 0) _customOrder.Clear();
        if (customOrder != null) _customOrder.AddRange(customOrder);
        return State();
    }

    // ================================================================
    // 应用更改（旧 ApplyDllPatch，直写 GameGaurd.ini）
    // ================================================================

    /// <summary>
    /// 应用前的确认正文（旧 ApplyDllPatch 的 MessageBox 文案，逐字保留）。
    /// 供 dll.previewApply（只读）与 dll.apply 共用，前端主题化确认框直接用它作正文。
    /// </summary>
    internal static string ConfirmText(int enabledCount, int customCount, bool loaderInstalled)
        => "将直写游戏根目录 GameGaurd.ini 的 [Plugins] 插件列表：\n"
            + "  启用受管插件 " + enabledCount + "（未勾选的受管条目会从列表移除，文件保留；\n"
            + "  自定义扩展 " + customCount + " 个按开关勾选写入（取消勾选 = 从 [Plugins] 移除，文件保留）"
            + (loaderInstalled ? "" : "\n\n提示：游戏根目录尚未发现挂载器 GameGaurd.dll / az.dll，插件 DLL 文件可能未安装——请先到【设置与关于】页点击【新DLL安装】安装补丁包")
            + "\n\n继续？";

    /// <summary>
    /// 只读预览：按给定勾选计算「应用后会写入什么」，不落盘。
    /// 前端在弹确认框前调用，保证确认正文与实际写入一致。
    /// </summary>
    public object PreviewApply(List<string> managedOn, List<string> customOn)
    {
        var enabled = _managedOrder.Count(f => IsGameNative(f)
            || (managedOn != null && managedOn.Contains(f, StringComparer.OrdinalIgnoreCase)));
        var custom = _customOrder.Count(f => customOn != null && customOn.Contains(f, StringComparer.OrdinalIgnoreCase));
        var loaderInstalled = File.Exists(Path.Combine(Paths.Gr, "GameGaurd.dll"))
            || File.Exists(Path.Combine(Paths.Gr, "az.dll"));
        return new
        {
            ok = true,
            managed = enabled,
            custom,
            loaderInstalled,
            confirmText = ConfirmText(enabled, custom, loaderInstalled)
        };
    }

    public object Apply(List<string> managedOn, List<string> customOn, List<string> managedOrder, List<string> customOrder)
    {
        if (managedOrder != null && managedOrder.Count > 0) SetOrder(managedOrder, customOrder);

        // 收集启用的受管插件；GameNative 无条件包含
        var enabled = new List<string>();
        foreach (var f in _managedOrder)
        {
            if (IsGameNative(f) || (managedOn != null && managedOn.Contains(f, StringComparer.OrdinalIgnoreCase)))
                enabled.Add(f);
        }

        // 自定义扩展：按界面顺序收集（勾选 = 写入 [Plugins]，取消 = 移除条目，文件保留）
        var custom = new List<string>();
        foreach (var f in _customOrder)
            if (customOn != null && customOn.Contains(f, StringComparer.OrdinalIgnoreCase))
                custom.Add(f);

        var loaderInstalled = File.Exists(Path.Combine(Paths.Gr, "GameGaurd.dll"))
            || File.Exists(Path.Combine(Paths.Gr, "az.dll"));

        var text = ConfirmText(enabled.Count, custom.Count, loaderInstalled);

        try
        {
            WritePatchIni(Paths.GameGaurdIni, enabled, custom);
            _ui.Log(AppController.LSuccess,
                ">>> [DLL扩展] 已直写 GameGaurd.ini: 受管插件 " + enabled.Count + " 个, 自定义扩展 " + custom.Count + " 个");
            _ui.DllChanged();
            return new { ok = true, applied = true, managed = enabled.Count, custom = custom.Count, confirmText = text };
        }
        catch (Exception ex)
        {
            _ui.Log(AppController.LError, ">>> [DLL扩展] 写入 GameGaurd.ini 失败: " + ex.Message);
            return new { ok = false, error = ex.Message };
        }
    }

    // ================================================================
    // 添加 / 删除自定义扩展
    // ================================================================

    public object Add(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new { ok = false, error = "缺少文件路径" };
        var installed = File.Exists(Path.Combine(Paths.Gr, "GameGaurd.dll"))
            || File.Exists(Path.Combine(Paths.Gr, "az.dll"))
            || IniFile.ReadPluginEntries(Paths.GameGaurdIni).Count > 0;
        if (!installed)
            return new { ok = false, error = "游戏根目录尚未安装 DLL 扩展，请先安装 客户端补丁.zip" };

        if (!File.Exists(path)) return new { ok = false, error = "文件不存在" };
        var file = Path.GetFileName(path);
        var ext = Path.GetExtension(file).ToLowerInvariant();
        if (ext != ".dll" && ext != ".ini") return new { ok = false, error = "只接受 .dll / .ini" };
        if (IsManaged(file)) return new { ok = false, error = "「" + file + "」是内置受管插件，请直接用列表里的开关勾选挂载。" };
        // 已添加但被取消勾选的条目也在清单里：此时重复添加应提示「已在列表中」，而不是当成新文件
        if (_customOrder.Contains(file, StringComparer.OrdinalIgnoreCase)
            || DllCustomStore.Load(Paths.DataRoot).Contains(file, StringComparer.OrdinalIgnoreCase))
            return new { ok = false, error = "「" + file + "」已在扩展列表中（勾选开关即可重新挂载）。" };

        var dst = GameRootFile(file);
        try
        {
            if (!string.Equals(dst, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(path, dst, true);
                _ui.Log(AppController.LSuccess, ">>> [DLL扩展] 已复制自定义扩展 → " + dst);
            }

            // 新加的扩展默认勾选挂载；其余已添加的按 ini 现状（未勾选的不写回，但仍留在清单里）
            var existing = ReadNonManaged();
            if (!existing.Contains(file, StringComparer.OrdinalIgnoreCase)) existing.Add(file);

            // 记进「已添加」清单：取消勾选后行要留在界面上，只有【删除扩展】才移除
            var known = DllCustomStore.Load(Paths.DataRoot);
            if (!known.Contains(file, StringComparer.OrdinalIgnoreCase)) known.Add(file);
            DllCustomStore.Save(Paths.DataRoot, known);

            _customOrder.Clear();
            _customOrder.AddRange(known);

            var enabled = new List<string>();
            var iniSet = IniSetFromIni();
            foreach (var f in _managedOrder)
                if (IsGameNative(f) || iniSet.Contains(f)) enabled.Add(f);

            WritePatchIni(Paths.GameGaurdIni, enabled, existing);
            _ui.Log(AppController.LSuccess, ">>> [DLL扩展] 已挂载自定义扩展: " + file + " → GameGaurd.ini");
            _ui.DllChanged();
            return new { ok = true, state = State() };
        }
        catch (Exception ex)
        {
            _ui.Log(AppController.LError, ">>> [DLL扩展] 写入 GameGaurd.ini 失败: " + ex.Message);
            return new { ok = false, error = ex.Message };
        }
    }

    public object Delete(string file, bool deleteFile)
    {
        if (string.IsNullOrWhiteSpace(file)) return new { ok = false, error = "缺少文件名" };
        if (IsManaged(file)) return new { ok = false, error = "内置插件请取消勾选后应用更改" };

        try
        {
            RemovePluginFromIni(Paths.GameGaurdIni, file);
            _customOrder.RemoveAll(x => x.Equals(file, StringComparison.OrdinalIgnoreCase));

            // 只有【删除扩展】才把条目从「已添加」清单里去掉（取消勾选只影响挂载）
            var known = DllCustomStore.Load(Paths.DataRoot);
            known.RemoveAll(x => x.Equals(file, StringComparison.OrdinalIgnoreCase));
            DllCustomStore.Save(Paths.DataRoot, known);

            _ui.Log(AppController.LSuccess, ">>> [DLL扩展] 已从 GameGaurd.ini 移除: " + file);
        }
        catch (Exception ex)
        {
            _ui.Log(AppController.LError, ">>> [DLL扩展] 移除失败: " + ex.Message);
            return new { ok = false, error = ex.Message };
        }

        if (deleteFile)
        {
            var dst = GameRootFile(file);
            if (File.Exists(dst))
            {
                try
                {
                    File.Delete(dst);
                    _ui.Log(AppController.LSuccess, ">>> [DLL扩展] 已删除文件: " + dst);
                }
                catch (Exception ex)
                {
                    _ui.Log(AppController.LWarning, ">>> [DLL扩展] 删除文件失败: " + ex.Message);
                }
            }
        }

        _ui.DllChanged();
        return new { ok = true, state = State() };
    }

    // ================================================================
    // 插件配置 ini（旧 OpenIniEditor 的读写部分）
    // ================================================================

    public object ReadConfig(string file)
    {
        if (string.IsNullOrWhiteSpace(file)) return new { ok = false, error = "缺少文件名" };
        var dst = GameRootFile(file);
        if (File.Exists(dst))
        {
            var (text, encLabel) = ReadConfigText(dst);
            return new { ok = true, file, path = dst, exists = true, text, encoding = encLabel };
        }

        var tpl = ExtractPatchZipEntry(file);
        if (tpl == null)
            _ui.Log(AppController.LWarning, ">>> [DLL扩展] 未找到 " + file + " 模板，已创建空配置（保存后写入游戏根目录）");
        return new { ok = true, file, path = dst, exists = false, text = tpl ?? "", encoding = "utf-8", template = tpl != null };
    }

    public object SaveConfig(string file, string text)
    {
        if (string.IsNullOrWhiteSpace(file)) return new { ok = false, error = "缺少文件名" };
        if (file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return new { ok = false, error = "文件名非法" };
        var dst = GameRootFile(file);
        try
        {
            var dir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(dst, text ?? "", new UTF8Encoding(false));
            _ui.Log(AppController.LSuccess, ">>> [DLL扩展] 已保存配置: " + dst);
            _ui.DllChanged();
            return new { ok = true, path = dst };
        }
        catch (Exception ex)
        {
            _ui.Log(AppController.LError, ">>> [DLL扩展] 保存配置失败: " + ex.Message);
            return new { ok = false, error = ex.Message };
        }
    }

    private static (string text, string encoding) ReadConfigText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3), "utf-8");
        try { return (new UTF8Encoding(false, true).GetString(bytes), "utf-8"); }
        catch (DecoderFallbackException) { }
        try { return (Encoding.GetEncoding(936).GetString(bytes), "gbk"); }
        catch { return (Encoding.Default.GetString(bytes), "default"); }
    }

    // ================================================================
    // 新DLL安装（旧 InstallPatchZip）
    // ================================================================

    public System.Threading.Tasks.Task<object> InstallPatchAsync()
    {
        if (!File.Exists(Paths.PatchZip))
        {
            _ui.Log(AppController.LError, "未找到客户端补丁包: " + Paths.PatchZip);
            return System.Threading.Tasks.Task.FromResult<object>(new { ok = false, error = "未找到客户端补丁包" });
        }

        var enabled = DllPlugins.Select(p => p.File).ToList();
        var tmp = Path.Combine(Path.GetTempPath(), "ServerUI_ClientPatch");
        try
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            ZipFile.ExtractToDirectory(Paths.PatchZip, tmp);
        }
        catch (Exception ex)
        {
            _ui.Log(AppController.LError, ">>> [新DLL安装] 解压客户端补丁失败: " + ex.Message);
            return System.Threading.Tasks.Task.FromResult<object>(new { ok = false, error = ex.Message });
        }

        var copied = 0;
        var failed = new List<string>();
        foreach (var f in Directory.GetFiles(tmp, "*", SearchOption.AllDirectories))
        {
            var rel = Compat.GetRelativePath(tmp, f);
            if (rel.Equals("GameGaurd.ini", StringComparison.OrdinalIgnoreCase)) continue;
            var dst = Path.Combine(Paths.Gr, rel);
            // 插件配置 ini 已在游戏根目录 → 保留用户已编辑内容
            if (rel.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) && File.Exists(dst)) continue;
            try
            {
                var dir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.Copy(f, dst, true);
                copied++;
            }
            catch (Exception ex)
            {
                failed.Add(rel);
                _ui.Log(AppController.LWarning, ">>> [新DLL安装] 复制补丁文件失败: " + rel + " - " + ex.Message);
            }
        }

        try
        {
            var keepCustom = ReadNonManaged();
            WritePatchIni(Paths.GameGaurdIni, enabled, keepCustom);
            if (failed.Count > 0)
                _ui.Log(AppController.LError, ">>> [新DLL安装] 有 " + failed.Count + " 个补丁文件未安装成功（可能被游戏占用）");
            else
                _ui.Log(AppController.LSuccess,
                    ">>> [新DLL安装] 客户端补丁已安装: 复制 " + copied + " 个文件, 启用插件 " + enabled.Count + " 个 → GameGaurd.ini");
            _ui.DllChanged();
            return System.Threading.Tasks.Task.FromResult<object>(new
            {
                ok = true,
                copied,
                enabled = enabled.Count,
                failed
            });
        }
        catch (Exception ex)
        {
            _ui.Log(AppController.LError, ">>> [新DLL安装] 写入 GameGaurd.ini 失败: " + ex.Message);
            return System.Threading.Tasks.Task.FromResult<object>(new { ok = false, error = ex.Message });
        }
    }

    // ================================================================
    // GameGaurd.ini 合并写入（旧 BuildPatchIni / RemovePluginFromIni）
    // ================================================================

    private static HashSet<string> IniSetFromIni()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in IniFile.ReadPluginEntries(Paths.GameGaurdIni))
            if (kv.Value.Length > 0) set.Add(kv.Value);
        return set;
    }

    private static List<string> ReadNonManaged()
    {
        var list = new List<string>();
        foreach (var kv in IniFile.ReadPluginEntries(Paths.GameGaurdIni))
        {
            if (kv.Value.Length == 0) continue;
            if (IsManaged(kv.Value)) continue;
            if (!list.Contains(kv.Value, StringComparer.OrdinalIgnoreCase)) list.Add(kv.Value);
        }
        return list;
    }

    /// <summary>
    /// 合并生成 GameGaurd.ini：保留其他节与 [Plugins] 注释，丢弃受管旧条目，
    /// 受管插件按顺序重编号，自定义扩展追加在尾部（GameNative 恒为 Plugin0）。
    /// </summary>
    private static string BuildPatchIni(string iniPath, List<string> enabled, List<string> extra)
    {
        var gn = enabled.FindIndex(x => x.Equals("GameNative.dll", StringComparison.OrdinalIgnoreCase));
        if (gn > 0)
        {
            var g = enabled[gn];
            enabled.RemoveAt(gn);
            enabled.Insert(0, g);
        }

        var managed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in DllPlugins) managed.Add(p.File);

        var keep = new List<string>();
        var sectionComments = new List<string>();
        var inPlugins = false;

        if (File.Exists(iniPath))
        {
            foreach (var raw in IniFile.ReadLines(iniPath))
            {
                var t = raw.Trim();
                if (t.StartsWith("[", StringComparison.Ordinal))
                {
                    inPlugins = t.Equals("[Plugins]", StringComparison.OrdinalIgnoreCase);
                    keep.Add(raw);
                    continue;
                }
                if (inPlugins)
                {
                    var eq = t.IndexOf('=');
                    if (eq > 0 && t.Substring(0, eq).Trim().StartsWith("Plugin", StringComparison.OrdinalIgnoreCase))
                        continue;   // 受管/自定义旧条目丢弃，统一重编号写入
                    sectionComments.Add(raw);   // [Plugins] 内的注释保留
                    continue;
                }
                keep.Add(raw);
            }
        }

        var ext = new List<string>();
        if (extra != null)
        {
            foreach (var e in extra)
                if (!ext.Contains(e, StringComparer.OrdinalIgnoreCase)) ext.Add(e);
        }

        var sb = new StringBuilder();
        var wrote = false;
        foreach (var line in keep)
        {
            sb.AppendLine(line);
            var t = line.Trim();
            if (wrote || !t.Equals("[Plugins]", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var c in sectionComments) sb.AppendLine(c);
            var idx = 0;
            foreach (var v in enabled) sb.AppendLine("Plugin" + (idx++) + "=" + v);
            foreach (var v in ext) sb.AppendLine("Plugin" + (idx++) + "=" + v);
            wrote = true;
        }

        if (!wrote)
        {
            if (sb.Length > 0 && !sb.ToString().EndsWith("\r\n", StringComparison.Ordinal))
                sb.AppendLine();
            sb.AppendLine("[Plugins]");
            sb.AppendLine("; 由 ServerUI【DLL扩展】页面管理");
            var idx = 0;
            foreach (var v in enabled) sb.AppendLine("Plugin" + (idx++) + "=" + v);
            foreach (var v in ext) sb.AppendLine("Plugin" + (idx++) + "=" + v);
        }

        return sb.ToString().TrimEnd() + "\r\n";
    }

    private static void WritePatchIni(string iniPath, List<string> enabled, List<string> extra)
        => IniFile.WriteText(iniPath, BuildPatchIni(iniPath, enabled, extra));

    /// <summary>从 GameGaurd.ini 移除指定插件条目（其余条目/注释/其他节完整保留并重新编号）。</summary>
    private static void RemovePluginFromIni(string iniPath, string file)
    {
        var keep = new List<string>();
        var sectionComments = new List<string>();
        var plugins = new List<string>();
        var inPlugins = false;

        foreach (var raw in IniFile.ReadLines(iniPath))
        {
            var t = raw.Trim();
            if (t.StartsWith("[", StringComparison.Ordinal))
            {
                inPlugins = t.Equals("[Plugins]", StringComparison.OrdinalIgnoreCase);
                keep.Add(raw);
                continue;
            }
            if (inPlugins)
            {
                var eq = t.IndexOf('=');
                if (eq > 0 && t.Substring(0, eq).Trim().StartsWith("Plugin", StringComparison.OrdinalIgnoreCase))
                {
                    var v = t.Substring(eq + 1).Trim();
                    if (v.Equals(file, StringComparison.OrdinalIgnoreCase)) continue;
                    if (v.Length > 0) plugins.Add(v);
                    continue;
                }
                sectionComments.Add(raw);
                continue;
            }
            keep.Add(raw);
        }

        var sb = new StringBuilder();
        var wrote = false;
        foreach (var line in keep)
        {
            sb.AppendLine(line);
            var t = line.Trim();
            if (wrote || !t.Equals("[Plugins]", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var c in sectionComments) sb.AppendLine(c);
            var idx = 0;
            foreach (var v in plugins) sb.AppendLine("Plugin" + (idx++) + "=" + v);
            wrote = true;
        }

        if (!wrote)
        {
            if (sb.Length > 0 && !sb.ToString().EndsWith("\r\n", StringComparison.Ordinal))
                sb.AppendLine();
            sb.AppendLine("[Plugins]");
            var idx = 0;
            foreach (var v in plugins) sb.AppendLine("Plugin" + (idx++) + "=" + v);
        }

        IniFile.WriteText(iniPath, sb.ToString().TrimEnd() + "\r\n");
    }
}
