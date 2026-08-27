/*
 * ==================================================================
 * MainForm DLL 扩展页 (partial class) — 客户端补丁扩展管理
 * 对应游戏根目录 GameGaurd.ini 的 [Plugins] 插件列表（与启动器一致的 DLL/INI 扩展机制）
 * 包含: 挂载列表 / 受管插件开关 / 添加扩展 / 删除扩展 / 配置 ini 可视化编辑
 *       新DLL安装(安装 客户端补丁.zip) 与代码弹窗 DllPickForm / IniEditorForm / IniAskForm
 * 应用更改 = 直写 GameGaurd.ini（不再解压安装 zip）
 * ==================================================================
 */
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Microsoft.VisualBasic;
using AntdUI;
using ServerUI.Models;
using ServerUI.Services;

namespace ServerUI;

public partial class MainForm : AntdUI.Window
{
    // ===== DLL扩展页 字段 (MainForm.DllExt.cs) =====
    AntdUI.In.Panel pgDll;                 // P2.5 DLL扩展 (由 Build() 创建加入内容区)
    AntdUI.In.Panel pgTime;                // P2.6 调整系统时间
    AntdUI.Switch[] swDlls;                // 插件开关数组 (DLL扩展页)
    TableLayoutPanel _dllTbl;              // DLL扩展页插件行表格 (AutoSize, 可滚动)
    AntdUI.Label lbDllSt;                  // DLL扩展页状态标签
    bool _dllSyncing;                      // 开关状态同步标志 (避免 RefreshDllState 触发"未应用"提示)
    bool _patchInstalled;                  // 游戏根目录是否已安装 DLL 扩展 (挂载器文件或 [Plugins] 记录存在)
    List<string> _custRows = new();        // 自定义扩展文件名顺序（当前刷新结果, 供【应用更改】读取勾选）
    readonly Dictionary<string, AntdUI.Switch> _swCust = new(StringComparer.OrdinalIgnoreCase); // 自定义扩展行开关（文件名 → 开关）


    // 客户端补丁受管理的插件 — 对应 客户端补丁.zip 内 GameGaurd.ini [Plugins] 列表
    // 元组: (文件名, 显示名, 说明, 可编辑配置文件); Config=null 表示无配置文件
    static readonly (string File, string Name, string Desc, string Config)[] DllPlugins =
    {
        ("PreventMinimize.dll", "防止窗口最小化", "防止游戏窗口意外最小化", null),
        ("MultiInstance.dll",   "游戏多开",        "支持同时运行多个游戏客户端", null),
        ("GameNative.dll",      "游戏原生整合",    "原生层游戏功能整合补丁（必选，不可关闭）", null),
        ("AutoFire.dll",        "自动连发",        "按住攻击键自动连续攻击", "AutoFire.ini"),
        ("EquipmentSwap.dll",   "一键换装",        "快捷切换预设装备方案（含 EquipmentSwap 界面配置）", null),
        ("DpsMeter.dll",        "DPS 统计",        "战斗中实时统计输出", "DpsMeter.ini"),
        ("CombatPower.dll",     "战斗力显示",      "实时显示角色战斗力", "CombatPower.ini"),
    };

    // ================================================================
    // ★ P2.5 DLL扩展页 ★ — 客户端补丁挂载 (ClientPatch)
    // 挂载方式参考 https://gitgud.io/rewio/S4A21ClientPatch:
    //   1. 补丁文件 (插件 DLL / 配置 ini / 挂载器 GameGaurd.dll、az.dll
    //      / EquipmentSwap 界面等) 复制到游戏根目录
    //   2. 勾选的插件写入游戏根目录 GameGaurd.ini 的 [Plugins] 列表
    //      (合并且保留原有插件, 如 S4A21MemOpt.dll)
    // ================================================================
    void BuildDllPage()
    {
        pgDll.Padding = new Padding(10);
        var d = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1, RowCount = 4,
            BackColor = Color.Transparent
        };
        d.RowStyles.Add(new RowStyle(SizeType.Absolute, 52F));   // 信息栏
        d.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));   // 插件列表 (可滚动)
        d.RowStyles.Add(new RowStyle(SizeType.Absolute, 56F));   // 操作行
        d.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));   // 状态行
        pgDll.Controls.Add(d);

        // ============================================================
        // ★ 信息栏 ★ (参考存档管理页信息栏: 标签 + 右侧按钮)
        // ============================================================
        var ib = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3, RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(4, 2, 4, 0)
        };
        ib.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        ib.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140F));
        ib.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130F));
        ib.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var lbInfo = L("客户端补丁挂载 · 参考 S4A21ClientPatch（勾选插件 = 写入 GameGaurd.ini 挂载）", 9f);
        lbInfo.ForeColor = Color.FromArgb(140, 140, 148);

        var btOpenZip = B("打开补丁包目录", TTypeMini.Default, "FolderOpenOutlined");
        btOpenZip.Click += (s, e) =>
        {
            var zdir = Path.Combine(_ad, "实用工具包", "DLL覆盖");
            Directory.CreateDirectory(zdir);
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = zdir, UseShellExecute = true });
            Lg(">>> 打开了 DLL覆盖 目录", Color.CornflowerBlue);
        };

        var btRf = B("刷新状态", TTypeMini.Default, "ReloadOutlined");
        btRf.Click += (s, e) => RefreshDllState();

        ib.Controls.Add(lbInfo, 0, 0);
        ib.Controls.Add(btOpenZip, 1, 0);
        ib.Controls.Add(btRf, 2, 0);
        d.Controls.Add(ib, 0, 0);

        // ============================================================
        // ★ 插件列表卡片 ★ — 可滚动 (参考使用说明页滚动结构)
        // 内部 AutoSize 表格: 超出可视区时出现滚动条, 支持滚轮滑动
        // ============================================================
        var cList = Card(8, 2);
        var scroll = new AntdUI.In.Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true
        };
        var tbl = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3, RowCount = 0,
            BackColor = Style.Get(Colour.BgContainer),   // 不透明背景, 滚动无残影
            Padding = new Padding(14, 8, 14, 8)
        };
        tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56F));    // 开关/图标
        tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 205F));   // 名称（DLL 文件名较长, 加宽避免换行/截断）
        tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));    // 说明/操作
        _dllTbl = tbl;
        scroll.Controls.Add(tbl);
        cList.Controls.Add(scroll);
        d.Controls.Add(cList, 0, 1);

        swDlls = new AntdUI.Switch[DllPlugins.Length];
        for (int i = 0; i < DllPlugins.Length; i++)
        {
            var isNative = IsGameNative(DllPlugins[i].File);
            var sw = new AntdUI.Switch
            {
                // 原生整合补丁（GameNative）为必选插件: 默认开启且锁定, 不可手动关闭
                Checked = isNative,
                Enabled = !isNative,
                Cursor = isNative ? Cursors.Default : Cursors.Hand,
                Size = new Size(40, 22),
                Margin = new Padding(6, 7, 0, 0),
                Anchor = AnchorStyles.Left
            };
            sw.CheckedChanged += (s, e) =>
            {
                if (_dllSyncing) return;
                if (lbDllSt != null)
                    lbDllSt.Text = "已修改（未应用）：点击【应用更改】写入 GameGaurd.ini 生效";
            };
            swDlls[i] = sw;
        }

        // ============================================================
        // ★ 操作行 ★ — 应用 / 添加扩展 / 删除扩展 + 提示文字
        // ============================================================
        var op = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4, RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(4, 4, 4, 0)
        };
        op.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150F));
        op.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150F));
        op.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150F));
        op.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        op.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var btApply = B("应用更改", TTypeMini.Primary, "CheckOutlined");
        btApply.Click += (s, e) => ApplyDllPatch();

        var btAdd = B("添加扩展", TTypeMini.Default, "PlusOutlined");
        btAdd.Click += (s, e) => AddCustomDll();

        var btDel = B("删除扩展", TTypeMini.Error, "DeleteOutlined");
        btDel.Click += (s, e) => PickAndDeleteDll();

        var lbOp = new AntdUI.Label
        {
            Text = "添加扩展 = 选择自己的插件挂载（复制到游戏根目录 + 写入 GameGaurd.ini）；删除扩展 = 移除已挂载的自定义插件；自定义扩展行有开关，取消勾选后应用 = 从列表移除（文件保留）",
            Font = new Font("Microsoft YaHei UI", 8.5f),
            ForeColor = Color.FromArgb(130, 130, 138),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        op.Controls.Add(btApply, 0, 0);
        op.Controls.Add(btAdd, 1, 0);
        op.Controls.Add(btDel, 2, 0);
        op.Controls.Add(lbOp, 3, 0);
        d.Controls.Add(op, 0, 2);

        // ============================================================
        // ★ 状态行 ★
        // ============================================================
        lbDllSt = L("", 8.5f);
        lbDllSt.ForeColor = Color.FromArgb(120, 120, 128);
        lbDllSt.TextAlign = ContentAlignment.MiddleLeft;
        d.Controls.Add(lbDllSt, 0, 3);

        RefreshDllState();
    }

    /*
     * 刷新 DLL扩展页 — 读取 GameGaurd.ini [Plugins] 勾选状态并重建插件列表
     * (受管插件开关 + 自定义扩展 行, 列表可滚动)
     * 未安装 DLL 扩展（游戏根目录无挂载器文件且无 [Plugins] 记录）时:
     *   不显示任何"已安装插件" — 列表只显示空态提示, 开关全部置灰
     */
    void RefreshDllState()
    {
        _dllSyncing = true;
        try
        {
            var iniPath = Path.Combine(_gr, "GameGaurd.ini");
            var iniSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var custom = new List<string>();
            bool hasPlugins = false;
            if (File.Exists(iniPath))
            {
                foreach (var raw in File.ReadAllLines(iniPath))
                {
                    var t = raw.Trim();
                    int eq = t.IndexOf('=');
                    if (eq > 0 && t.StartsWith("Plugin", StringComparison.OrdinalIgnoreCase))
                    {
                        var v = t.Substring(eq + 1).Trim();
                        if (v.Length == 0) continue;
                        hasPlugins = true;
                        iniSet.Add(v);
                        if (!IsManagedDll(v))
                            custom.Add(v);   // 自定义扩展 = 非受管插件 (S4A21MemOpt.dll 等第三方插件同样归入, 自由管理)
                    }
                }
            }

            // 是否已安装 DLL 扩展: 挂载器文件存在 或 GameGaurd.ini 已有插件记录
            _patchInstalled = File.Exists(Path.Combine(_gr, "GameGaurd.dll"))
                || File.Exists(Path.Combine(_gr, "az.dll"))
                || hasPlugins;

            for (int i = 0; i < DllPlugins.Length; i++)
            {
                // GameNative 原生整合补丁为必选: 恒为开启状态且锁定
                var isNative = IsGameNative(DllPlugins[i].File);
                if (!_patchInstalled)
                {
                    // 未安装 → 开关全部置灰（不误导"已安装"）, 安装后由【新DLL安装】重建状态
                    swDlls[i].Checked = false;
                    swDlls[i].Enabled = false;
                }
                else
                {
                    swDlls[i].Checked = isNative || iniSet.Contains(DllPlugins[i].File);
                    swDlls[i].Enabled = !isNative;
                }
            }

            _custRows.Clear();
            _custRows.AddRange(custom);
            RebuildDllRows(custom);

            int on = 0;
            for (int i = 0; i < DllPlugins.Length; i++)
                if (swDlls[i].Checked) on++;
            int onCust = 0;
            foreach (var s in _swCust.Values)
                if (s.Checked) onCust++;
            if (lbDllSt != null)
                lbDllSt.Text = !_patchInstalled
                    ? "未安装 DLL 扩展：游戏根目录未发现补丁文件，请先点击【新DLL安装】"
                    : "受管插件已启用 " + on + " / " + DllPlugins.Length
                        + " · 自定义扩展 已勾选 " + onCust + " / " + custom.Count + " → GameGaurd.ini";
            Lg(!_patchInstalled
                ? ">>> [DLL扩展] 未安装：游戏根目录未发现补丁文件（挂载器/插件列表均无）"
                : ">>> [DLL扩展] 已刷新: 受管插件 " + on + " 个已启用, 自定义扩展 " + onCust + " 个已勾选", Txt2);
        }
        catch (Exception ex)
        {
            Lg(">>> [DLL扩展] 读取 GameGaurd.ini 失败: " + ex.Message, Rd);
            if (lbDllSt != null) lbDllSt.Text = "读取 GameGaurd.ini 失败";
        }
        finally { _dllSyncing = false; }
    }

    /* 是否内置受管插件 (客户端补丁 7 个) */
    bool IsManagedDll(string file)
    {
        foreach (var p in DllPlugins)
            if (p.File.Equals(file, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // 第三方插件不设"内置/自带"限制：S4A21MemOpt.dll 等非受管插件均按普通扩展自由管理

    /* 原生整合补丁（GameNative）— 必选插件判定 */
    bool IsGameNative(string file) =>
        file.Equals("GameNative.dll", StringComparison.OrdinalIgnoreCase);

    /*
     * 打开插件配置 ini 编辑器 (AutoFire / DpsMeter / CombatPower 等)
     * 独立窗口编辑, 保存回游戏根目录; 文件不存在时从补丁包提取模板
     */
    void OpenIniEditor(string iniName, string pluginName)
    {
        var dstPath = Path.Combine(_gr, iniName);
        string text;
        Encoding enc = null;
        if (File.Exists(dstPath))
        {
            text = ReadConfigText(dstPath, out enc);
        }
        else
        {
            var tpl = ExtractPatchZipEntry(iniName);
            text = tpl ?? "（未找到 " + iniName + " 模板，将新建配置文件）\n";
        }

        var r = IniEditorForm.Edit(this, pluginName + " · " + iniName, text, dstPath, enc != null);
        if (r == null) return;   // 取消

        try
        {
            var dir = Path.GetDirectoryName(dstPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (enc != null)
                File.WriteAllText(dstPath, r, enc);
            else
                File.WriteAllText(dstPath, r, new UTF8Encoding(false));   // 新文件按 UTF-8 无 BOM
            Lg(">>> [DLL扩展] 已保存配置: " + dstPath, Gn);
            if (lbDllSt != null)
                lbDllSt.Text = "已保存配置：" + iniName + " → 游戏根目录";
        }
        catch (Exception ex)
        {
            Lg(">>> [DLL扩展] 保存配置失败: " + ex.Message, Rd);
        }
    }

    /* 读取配置文件文本: 优先 UTF-8, 解析失败回退 GBK(936), 再回退系统默认编码 */
    string ReadConfigText(string path, out Encoding enc)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            var s = new UTF8Encoding(false, true).GetString(bytes);
            enc = new UTF8Encoding(false);
            return s;
        }
        catch (DecoderFallbackException) { }
        try
        {
            var gbk = Encoding.GetEncoding(936);
            enc = gbk;
            return gbk.GetString(bytes);
        }
        catch { }
        enc = Encoding.Default;
        return enc.GetString(bytes);
    }

    /* 从 客户端补丁.zip 提取模板文件内容 (不存在返回 null) */
    string ExtractPatchZipEntry(string entry)
    {
        var zip = Path.Combine(_ad, "实用工具包", "DLL覆盖", "客户端补丁.zip");
        if (!File.Exists(zip)) return null;
        try
        {
            using var zf = System.IO.Compression.ZipFile.OpenRead(zip);
            foreach (var en in zf.Entries)
            {
                if (!en.FullName.Equals(entry, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (en.Length > 0)
                    using (var sr = new StreamReader(en.Open(), new UTF8Encoding(false), true))
                        return sr.ReadToEnd();
            }
        }
        catch { }
        return null;
    }

    /*
     * 重建插件列表 (可滚动表格)
     * 结构: 表头 → 受管插件行 (开关) → 自定义扩展 分组 (图标 + 名称 + 删除按钮)
     */
    void RebuildDllRows(List<string> custom)
    {
        var tbl = _dllTbl;
        if (tbl == null) return;
        _swCust.Clear();
        // 运行期重建表格时按窗体 AutoScale 因子补偿字体/控件尺寸:
        // AutoScaleMode.Font 只缩放首次创建的控件, 重建的新控件若不补偿会整体变小（界面/字体缩小）
        float k = 1f;
        if (IsHandleCreated)
        {
            // 用窗口当前 DPI 相对 96 的比值作缩放因子:
            // 窗体首次 AutoScale（Font 模式 ≈ DPI 比例）只缩放首批控件, 运行期重建控件需手动补偿
            int dpi = DeviceDpi;
            if (dpi > 0) k = dpi / 96f;
        }
        tbl.SuspendLayout();
        // 释放旧行控件（保留受管开关复用），避免多次刷新产生内存垃圾
        foreach (Control c in tbl.Controls)
        {
            bool isSw = false;
            if (swDlls != null)
                foreach (var s in swDlls)
                    if (ReferenceEquals(s, c)) { isSw = true; break; }
            if (!isSw) c.Dispose();
        }
        tbl.Controls.Clear();
        tbl.RowStyles.Clear();
        tbl.RowCount = 0;

        void AddRow(float h)
        {
            tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
            tbl.RowCount++;
        }

        // ---- 未安装 DLL 扩展: 只显示空态提示, 不渲染"已安装插件"列表 ----
        if (!_patchInstalled)
        {
            AddRow(44F);
            var nt = new AntdUI.Label
            {
                Text = "—— 未安装 DLL 扩展 ——",
                Font = new Font("Microsoft YaHei UI", 10f * k, FontStyle.Bold),
                ForeColor = Color.FromArgb(150, 150, 158),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            };
            tbl.Controls.Add(nt, 0, 0);
            tbl.SetColumnSpan(nt, 3);

            AddRow(64F);
            var tip = new AntdUI.Label
            {
                Text = "游戏根目录尚未安装客户端补丁（未发现挂载器 GameGaurd.dll / az.dll，且 GameGaurd.ini 无插件记录）。\n\n"
                    + "请到【设置与关于】页点击【新DLL安装】，把 客户端补丁.zip 完整安装到游戏根目录；\n"
                    + "安装完成后本页会自动显示插件列表。",
                Font = new Font("Microsoft YaHei UI", 8.5f * k),
                ForeColor = Color.FromArgb(120, 120, 128),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                TextMultiLine = true
            };
            tbl.Controls.Add(tip, 0, 1);
            tbl.SetColumnSpan(tip, 3);

            tbl.ResumeLayout(true);
            tbl.PerformLayout();
            // v2.1-4 修复: 重建后强制整表与滚动容器重绘（同下方常规分支）
            tbl.Invalidate();
            (tbl.Parent as AntdUI.In.Panel)?.Invalidate(true);
            return;
        }

        // ---- 表头 ----
        AddRow(28F);
        var hCap = new AntdUI.Label
        {
            Text = "已安装插件（勾选 = 挂载；受管插件与自定义扩展均以开关控制，底部为玩家自定义扩展）",
            Font = new Font("Microsoft YaHei UI", 8.5f * k),
            ForeColor = Color.FromArgb(120, 120, 128),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        tbl.Controls.Add(hCap, 0, 0);
        tbl.SetColumnSpan(hCap, 3);

        // ---- 受管插件行 ----
        for (int i = 0; i < DllPlugins.Length; i++)
        {
            AddRow(38F);
            int row = tbl.RowCount - 1;
            var p = DllPlugins[i];
            var nm = new AntdUI.Label
            {
                Text = IsGameNative(p.File) ? p.Name + "（必选）" : p.Name,
                Font = new Font("Microsoft YaHei UI", 9.5f * k, FontStyle.Bold),
                ForeColor = Style.Get(Colour.Text),   // 显式主题前景色：刷新重建后不依赖 Label 默认取色（避免文字失踪）
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            };
            var de = new AntdUI.Label
            {
                Text = p.Desc + "（" + p.File + "）",
                Font = new Font("Microsoft YaHei UI", 8.5f * k),
                ForeColor = Color.FromArgb(130, 130, 138),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            };
            tbl.Controls.Add(swDlls[i], 0, row);
            tbl.Controls.Add(nm, 1, row);

            // 有配置文件的插件 (AutoFire / DpsMeter / CombatPower): 行尾提供「编辑」按钮
            if (!string.IsNullOrEmpty(p.Config))
            {
                var cell3 = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 2, RowCount = 1,
                    BackColor = Color.Transparent
                };
                cell3.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
                cell3.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
                cell3.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

                var ed = new AntdUI.Button
                {
                    Text = "编辑",
                    Type = TTypeMini.Default,
                    IconSvg = "EditOutlined",
                    Dock = DockStyle.Fill,
                    Margin = new Padding(4, 3, 0, 3),
                    Cursor = Cursors.Hand,
                    WaveSize = 0,
                    BorderWidth = 1F
                };
                var cfg = p.Config;   // 闭包捕获
                var pName = p.Name;
                ed.Click += (s, e) => OpenIniEditor(cfg, pName);
                cell3.Controls.Add(de, 0, 0);
                cell3.Controls.Add(ed, 1, 0);
                tbl.Controls.Add(cell3, 2, row);
            }
            else
            {
                tbl.Controls.Add(de, 2, row);
            }
        }

        // ---- 自定义扩展 分组 ----（开关 = 挂载; 取消勾选后应用 = 从 [Plugins] 移除, 文件保留）
        if (custom.Count > 0)
        {
            AddRow(28F);   // 与表头行高等高
            int gro = tbl.RowCount - 1;
            var gCap = new AntdUI.Label
            {
                Text = "—— 自定义扩展（开关 = 挂载，行尾可删除）——",
                Font = new Font("Microsoft YaHei UI", 8.5f * k),
                ForeColor = Color.FromArgb(140, 140, 148),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            };
            tbl.Controls.Add(gCap, 0, gro);
            tbl.SetColumnSpan(gCap, 3);

            foreach (var f in custom)
            {
                AddRow(38F);
                int row = tbl.RowCount - 1;
                var exists = File.Exists(Path.Combine(_gr, f));

                // 开关 = 当前挂载（条目在 GameGaurd.ini [Plugins] 中即勾选）
                var sw = new AntdUI.Switch
                {
                    Checked = true,
                    Cursor = Cursors.Hand,
                    Size = new Size((int)(40f * k), (int)(22f * k)),
                    Margin = new Padding(6, 7, 0, 0),
                    Anchor = AnchorStyles.Left
                };
                sw.CheckedChanged += (s, e) =>
                {
                    if (_dllSyncing) return;
                    if (lbDllSt != null)
                        lbDllSt.Text = "已修改（未应用）：点击【应用更改】写入 GameGaurd.ini 生效";
                };
                _swCust[f] = sw;

                // 直接显示 DLL / INI 文件名（ForeColor 用主题色, 不用透明色以免文字不可见）
                var nm = new AntdUI.Label
                {
                    Text = f,
                    Font = new Font("Microsoft YaHei UI", 9.5f * k, FontStyle.Bold),   // 与受管行名称同字号（按窗体缩放因子补偿）
                    ForeColor = exists ? Style.Get(Colour.Text) : Rd,
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleLeft
                };
                var st = new AntdUI.Label
                {
                    Text = exists ? "已挂载（文件在游戏根目录）" : "文件缺失（仅剩 GameGaurd.ini 条目）",
                    Font = new Font("Microsoft YaHei UI", 8.5f * k),
                    ForeColor = exists ? Color.FromArgb(130, 130, 138) : Rd,
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleLeft
                };
                var del = new AntdUI.Button
                {
                    Text = "删除",
                    Type = TTypeMini.Error,
                    Dock = DockStyle.Fill,
                    Margin = new Padding(4, 3, 0, 3),
                    Cursor = Cursors.Hand,
                    WaveSize = 0,
                    BorderWidth = 1F
                };
                var ff = f;   // 闭包捕获
                del.Click += (s, e) => DeleteCustomDll(ff);
                var cell3 = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 2, RowCount = 1,
                    BackColor = Color.Transparent
                };
                cell3.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
                cell3.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));   // 与受管行编辑按钮等宽
                cell3.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
                cell3.Controls.Add(st, 0, 0);
                cell3.Controls.Add(del, 1, 0);
                tbl.Controls.Add(sw, 0, row);
                tbl.Controls.Add(nm, 1, row);
                tbl.Controls.Add(cell3, 2, row);
            }
        }
        else
        {
            AddRow(30F);
            int row = tbl.RowCount - 1;
            var empty = new AntdUI.Label
            {
                Text = "—— 暂无自定义扩展（点击下方【添加扩展】挂载自己的插件）——",
                Font = new Font("Microsoft YaHei UI", 8.5f * k),
                ForeColor = Color.FromArgb(120, 120, 128),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            };
            tbl.Controls.Add(empty, 0, row);
            tbl.SetColumnSpan(empty, 3);
        }

        tbl.ResumeLayout(true);
        tbl.PerformLayout();
        // v2.1-4 修复: 重建后强制整表与滚动容器重绘
        // （AntdUI 自绘控件刷新重建后偶发不重绘 → 文字不显示但按钮/开关仍在）
        tbl.Invalidate();
        (tbl.Parent as AntdUI.In.Panel)?.Invalidate(true);
    }

    /*
     * 读取 GameGaurd.ini 中当前的"非受管"插件条目
     * (含 S4A21MemOpt.dll 等非受管插件与玩家自定义扩展)
     */
    List<string> GetNonManagedPlugins()
    {
        var list = new List<string>();
        var iniPath = Path.Combine(_gr, "GameGaurd.ini");
        if (!File.Exists(iniPath)) return list;
        foreach (var raw in File.ReadAllLines(iniPath))
        {
            var t = raw.Trim();
            int eq = t.IndexOf('=');
            if (eq > 0 && t.StartsWith("Plugin", StringComparison.OrdinalIgnoreCase))
            {
                var v = t.Substring(eq + 1).Trim();
                if (v.Length > 0 && !IsManagedDll(v))
                    list.Add(v);
            }
        }
        return list;
    }

    /*
     * 添加扩展 — 玩家选择自己的扩展文件挂载 (与启动器一致: 可选 DLL 或 INI)
     * 1. OpenFileDialog 选择插件文件 (DLL / INI)
     * 2. 若不在游戏根目录 → 询问并复制 (加载器按文件名从游戏根目录加载)
     * 3. 追加写入 GameGaurd.ini [Plugins] 并刷新
     */
    void AddCustomDll()
    {
        // 未安装 DLL 扩展时不允许添加（列表直写无意义, 优先引导安装）
        if (!_patchInstalled)
        {
            MessageBox.Show("游戏根目录尚未安装 DLL 扩展。\n\n"
                + "请先到【设置与关于】页点击【新DLL安装】安装 客户端补丁.zip，再添加自定义扩展。",
                "添加扩展", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var ofd = new OpenFileDialog();
        ofd.Title = "选择要挂载的扩展文件（DLL / INI，与启动器一致）";
        ofd.Filter = "插件文件 (*.dll;*.ini)|*.dll;*.ini|所有文件 (*.*)|*.*";
        ofd.InitialDirectory = _gr;
        if (ofd.ShowDialog(this) != DialogResult.OK) return;

        var file = Path.GetFileName(ofd.FileName);
        if (IsManagedDll(file))
        {
            MessageBox.Show("「" + file + "」是内置受管插件，请直接用列表里的开关勾选挂载。",
                "添加扩展", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var existing = GetNonManagedPlugins();
        foreach (var e in existing)
            if (e.Equals(file, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("「" + file + "」已在挂载列表中。", "添加扩展",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

        var dst = Path.Combine(_gr, file);
        var srcPath = ofd.FileName;
        if (!dst.Equals(srcPath, StringComparison.OrdinalIgnoreCase))
        {
            var r = MessageBox.Show(
                "选中的扩展文件位于游戏根目录之外：\n" + srcPath + "\n\n"
                + "GameGaurd 加载器按文件名从游戏根目录（" + _gr + "）加载扩展，"
                + "需要先把文件复制过去。\n\n是否复制到游戏根目录并挂载？",
                "添加扩展", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;
            try
            {
                File.Copy(srcPath, dst, true);
                Lg(">>> [DLL扩展] 已复制自定义扩展 → " + dst, Gn);
            }
            catch (Exception ex)
            {
                MessageBox.Show("复制扩展文件失败：" + ex.Message, "添加扩展",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
        }

        try
        {
            var iniPath = Path.Combine(_gr, "GameGaurd.ini");
            existing.Add(file);
            var enabled = new List<string>();
            for (int i = 0; i < DllPlugins.Length; i++)
                if (swDlls[i].Checked) enabled.Add(DllPlugins[i].File);
            File.WriteAllText(iniPath, BuildPatchIni(iniPath, enabled, existing), Encoding.UTF8);
            Lg(">>> [DLL扩展] 已挂载自定义扩展: " + file + " → GameGaurd.ini", Gn);
            RefreshDllState();
        }
        catch (Exception ex)
        {
            Lg(">>> [DLL扩展] 写入 GameGaurd.ini 失败: " + ex.Message, Rd);
        }
    }

    /*
     * 删除扩展 — 弹窗列表选择要移除的自定义扩展 (内置插件用开关管理)
     */
    void PickAndDeleteDll()
    {
        if (!_patchInstalled)
        {
            MessageBox.Show("游戏根目录尚未安装 DLL 扩展，当前没有可删除的自定义扩展。\n\n"
                + "请先到【设置与关于】页点击【新DLL安装】安装 客户端补丁.zip。",
                "删除扩展", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var custom = new List<string>();
        var iniPath = Path.Combine(_gr, "GameGaurd.ini");
        if (File.Exists(iniPath))
        {
            foreach (var raw in File.ReadAllLines(iniPath))
            {
                var t = raw.Trim();
                int eq = t.IndexOf('=');
                if (eq > 0 && t.StartsWith("Plugin", StringComparison.OrdinalIgnoreCase))
                {
                    var v = t.Substring(eq + 1).Trim();
                    if (v.Length > 0 && !IsManagedDll(v))
                        custom.Add(v);
                }
            }
        }
        if (custom.Count == 0)
        {
            MessageBox.Show("当前没有自定义扩展 可删除。\n\n内置插件请取消勾选后点击【应用更改】即可移除。",
                "删除扩展", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var pick = DllPickForm.Pick(this, "选择要删除的扩展", custom.ToArray());
        if (string.IsNullOrEmpty(pick)) return;
        DeleteCustomDll(pick);
    }

    /*
     * 删除指定自定义扩展 — 从 GameGaurd.ini 移除条目, 可选删除游戏根目录文件
     */
    void DeleteCustomDll(string file)
    {
        if (IsManagedDll(file)) return;
        var iniPath = Path.Combine(_gr, "GameGaurd.ini");
        try
        {
            RemovePluginFromIni(iniPath, file);
            Lg(">>> [DLL扩展] 已从 GameGaurd.ini 移除: " + file, Gn);
        }
        catch (Exception ex)
        {
            Lg(">>> [DLL扩展] 移除失败: " + ex.Message, Rd);
            return;
        }

        var dst = Path.Combine(_gr, file);
        if (File.Exists(dst))
        {
            var r = MessageBox.Show(
                "已从 GameGaurd.ini 移除「" + file + "」。\n\n"
                + "是否同时删除游戏根目录中的该扩展文件？",
                "删除扩展", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r == DialogResult.Yes)
            {
                try
                {
                    File.Delete(dst);
                    Lg(">>> [DLL扩展] 已删除文件: " + dst, Gn);
                }
                catch (Exception ex)
                {
                    Lg(">>> [DLL扩展] 删除文件失败: " + ex.Message, Or);
                }
            }
        }
        RefreshDllState();
    }

    /*
     * 从 GameGaurd.ini 移除指定插件条目 (其余条目/注释/其他节完整保留并重新编号)
     */
    void RemovePluginFromIni(string iniPath, string file)
    {
        var keep = new List<string>();
        var sectionComments = new List<string>();
        var plugins = new List<string>();
        bool inP = false;
        foreach (var raw in File.ReadAllLines(iniPath))
        {
            var t = raw.Trim();
            if (t.StartsWith("["))
            {
                inP = t.Equals("[Plugins]", StringComparison.OrdinalIgnoreCase);
                keep.Add(raw);
                continue;
            }
            if (inP)
            {
                int eq = t.IndexOf('=');
                if (eq > 0 && t.Substring(0, eq).Trim().StartsWith("Plugin", StringComparison.OrdinalIgnoreCase))
                {
                    var v = t.Substring(eq + 1).Trim();
                    if (v.Equals(file, StringComparison.OrdinalIgnoreCase))
                        continue;   // 目标条目丢弃
                    if (v.Length > 0) plugins.Add(v);
                    continue;
                }
                sectionComments.Add(raw);   // [Plugins] 内的注释保留
                continue;
            }
            keep.Add(raw);
        }

        var sb = new StringBuilder();
        bool wrote = false;
        foreach (var line in keep)
        {
            sb.AppendLine(line);
            var t = line.Trim();
            if (!wrote && t.Equals("[Plugins]", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var c in sectionComments) sb.AppendLine(c);
                int idx = 0;
                foreach (var v in plugins) sb.AppendLine("Plugin" + (idx++) + "=" + v);
                wrote = true;
            }
        }
        if (!wrote)
        {
            if (sb.Length > 0 && !sb.ToString().EndsWith("\r\n", StringComparison.Ordinal))
                sb.AppendLine();
            sb.AppendLine("[Plugins]");
            int idx = 0;
            foreach (var v in plugins) sb.AppendLine("Plugin" + (idx++) + "=" + v);
        }
        File.WriteAllText(iniPath, sb.ToString().TrimEnd() + "\r\n", Encoding.UTF8);
    }

    /*
     * 应用更改 — 只把当前勾选状态直写游戏根目录 GameGaurd.ini 的 [Plugins] 插件列表
     * 不再解压 客户端补丁.zip（补丁文件安装请使用 设置与关于页 的【新DLL安装】）
     * 插件文件已安装的前提下, 本页 = 纯列表管理（勾选/取消勾选 → 应用 → 重启游戏生效）
     */
    void ApplyDllPatch()
    {
        // 原生整合补丁（GameNative）为必选插件 — 未开启时禁止应用修改
        bool nativeOn = false;
        for (int i = 0; i < DllPlugins.Length; i++)
            if (IsGameNative(DllPlugins[i].File) && swDlls[i].Checked) { nativeOn = true; break; }
        if (!nativeOn)
        {
            Lg(">>> [DLL扩展] 原生整合补丁（GameNative）为必选插件，取消勾选无法应用修改", Rd);
            if (lbDllSt != null)
                lbDllSt.Text = "无法应用：原生整合补丁（GameNative）为必选插件，必须保持开启";
            MessageBox.Show(
                "原生整合补丁（GameNative）必须保持开启，否则无法应用修改。\n\n"
                + "该插件是客户端补丁的核心基础，已锁定为必选状态（列表中不可手动关闭）。",
                "DLL扩展 - 应用更改", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // 收集启用的受管插件; GameNative 无条件包含
        var enabled = new List<string>();
        for (int i = 0; i < DllPlugins.Length; i++)
        {
            var f = DllPlugins[i].File;
            if (IsGameNative(f) || swDlls[i].Checked)
                enabled.Add(f);
        }

        // 收集勾选的自定义扩展（行内开关）— 取消勾选 = 应用后从 [Plugins] 移除条目（文件保留）
        var customOn = new List<string>();
        foreach (var f in _custRows)
            if (_swCust.TryGetValue(f, out var cs) && cs.Checked)
                customOn.Add(f);

        // 未检测到挂载器时提醒先执行【新DLL安装】；但列表仍可直写（文件缺失仅影响加载器加载）
        bool loaderInstalled = File.Exists(Path.Combine(_gr, "GameGaurd.dll"))
            || File.Exists(Path.Combine(_gr, "az.dll"));
        var r = MessageBox.Show(
            "将直写游戏根目录 GameGaurd.ini 的 [Plugins] 插件列表：\n"
            + "  启用受管插件 " + enabled.Count + "（未勾选的受管条目会从列表移除，文件保留；\n"
            + "  自定义扩展 " + customOn.Count + " 个按开关勾选写入（取消勾选 = 从 [Plugins] 移除，文件保留）"
            + (loaderInstalled ? "" : "\n\n提示：游戏根目录尚未发现挂载器 GameGaurd.dll / az.dll，"
                + "插件 DLL 文件可能未安装——请先到【设置与关于】页点击【新DLL安装】安装补丁包")
            + "\n\n继续？",
            "DLL扩展 - 应用更改",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r != DialogResult.Yes) return;

        // 直写 GameGaurd.ini（BuildPatchIni 保留注释, 受管条目按勾选重编号, 自定义扩展按开关勾选写入）
        try
        {
            var iniPath = Path.Combine(_gr, "GameGaurd.ini");
            File.WriteAllText(iniPath, BuildPatchIni(iniPath, enabled, customOn), Encoding.UTF8);
            Lg(">>> [DLL扩展] 已直写 GameGaurd.ini: 受管插件 " + enabled.Count + " 个, 自定义扩展 " + customOn.Count + " 个", Gn);
            if (lbDllSt != null)
                lbDllSt.Text = "已应用：受管插件 " + enabled.Count + " · 自定义扩展 " + customOn.Count + "（直写 GameGaurd.ini）";
            RefreshDllState();
        }
        catch (Exception ex)
        {
            Lg(">>> [DLL扩展] 写入 GameGaurd.ini 失败: " + ex.Message, Rd);
            if (lbDllSt != null) lbDllSt.Text = "写入 GameGaurd.ini 失败";
        }
    }

    /*
     * 新DLL安装 — 把 实用工具包\DLL覆盖\客户端补丁.zip 完整安装到游戏根目录
     * (设置与关于页【新DLL安装】按钮调用)
     * 1. 解压全部补丁文件到游戏根目录（已编辑过的插件配置 ini 保留不覆盖）
     * 2. 合并 GameGaurd.ini [Plugins] 列表：受管插件默认全部启用
     * 之后【DLL扩展】页只需直写 GameGaurd.ini 管理列表，无需再次解压
     */
    void InstallPatchZip()
    {
        var zip = Path.Combine(_ad, "实用工具包", "DLL覆盖", "客户端补丁.zip");
        if (!File.Exists(zip))
        {
            Lg("未找到客户端补丁包: " + zip, Rd);
            MessageBox.Show("未找到客户端补丁包：\n" + zip, "新DLL安装",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // 收集全部受管插件（安装即默认启用, 与补丁包 GameGaurd.ini 模板一致）
        var enabled = new List<string>();
        foreach (var p in DllPlugins) enabled.Add(p.File);

        var r = MessageBox.Show(
            "将把 客户端补丁.zip 完整安装到游戏根目录（" + _gr + "）：\n"
            + "  1. 复制补丁文件（GameGaurd.dll / az.dll / 插件 DLL / 配置 ini / EquipmentSwap 界面等）\n"
            + "     （已编辑过的插件配置 ini 会被保留，不会被补丁包模板覆盖）\n"
            + "  2. 合并更新 GameGaurd.ini 的 [Plugins] 列表（受管插件默认全部启用）\n\n"
            + "安装后可在【DLL扩展】页直接勾选/取消插件并应用（只更新 GameGaurd.ini）。\n\n继续？",
            "新DLL安装", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r != DialogResult.Yes) return;

        // 1) 解压补丁包到临时目录
        var tmp = Path.Combine(Path.GetTempPath(), "ServerUI_ClientPatch");
        try
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            System.IO.Compression.ZipFile.ExtractToDirectory(zip, tmp);
        }
        catch (Exception ex)
        {
            Lg(">>> [新DLL安装] 解压客户端补丁失败: " + ex.Message, Rd);
            MessageBox.Show("解压客户端补丁失败：" + ex.Message, "新DLL安装",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // 2) 复制全部补丁文件到游戏根目录（跳过 GameGaurd.ini, 由合并逻辑单独写入）
        int copied = 0;
        foreach (var f in Directory.GetFiles(tmp, "*", SearchOption.AllDirectories))
        {
            var rel = Compat.GetRelativePath(tmp, f);
            if (rel.Equals("GameGaurd.ini", StringComparison.OrdinalIgnoreCase))
                continue;
            var dst = Path.Combine(_gr, rel);
            // 插件配置 ini (AutoFire.ini 等) 已在游戏根目录 → 保留用户已编辑内容, 不覆盖
            // （需要恢复补丁包默认模板时, 删除游戏根目录中对应 ini 后重新应用即可）
            if (rel.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) && File.Exists(dst))
                continue;
            try
            {
                var dir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.Copy(f, dst, true);
                copied++;
            }
            catch (Exception ex)
            {
                Lg(">>> [新DLL安装] 复制补丁文件失败: " + rel + " - " + ex.Message, Or);
            }
        }

        // 3) 合并写入 GameGaurd.ini
        try
        {
            var iniPath = Path.Combine(_gr, "GameGaurd.ini");
            // 合并保留现有非受管条目（玩家自定义扩展 / S4A21MemOpt.dll 等不因安装而丢失）
            var keepCustom = GetNonManagedPlugins();
            File.WriteAllText(iniPath, BuildPatchIni(iniPath, enabled, keepCustom), Encoding.UTF8);
            Lg(">>> [新DLL安装] 客户端补丁已安装: 复制 " + copied + " 个文件, 启用插件 "
                + enabled.Count + " 个 → GameGaurd.ini", Gn);
            MessageBox.Show(
                "客户端补丁已安装到游戏根目录：\n"
                + "复制 " + copied + " 个文件，[Plugins] 列表启用 " + enabled.Count + " 个插件。\n\n"
                + "之后可在【DLL扩展】页直接管理插件列表（勾选/添加/删除扩展并应用，只更新 GameGaurd.ini）。",
                "新DLL安装", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _patchInstalled = true;   // 安装成功 → 本页立即恢复显示插件列表
            RefreshDllState();
        }
        catch (Exception ex)
        {
            Lg(">>> [新DLL安装] 写入 GameGaurd.ini 失败: " + ex.Message, Rd);
            MessageBox.Show("写入 GameGaurd.ini 失败：" + ex.Message, "新DLL安装",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /*
     * 合并生成 GameGaurd.ini
     * 保留原有 [Plugins] 中的非受管插件(如 S4A21MemOpt.dll)与全部注释,
     * 丢弃受管插件旧条目, 按勾选列表统一重新编号写入
     */
    string BuildPatchIni(string iniPath, List<string> enabled, List<string> extra = null)
    {
        var managed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in DllPlugins) managed.Add(p.File);

        var keep = new List<string>();
        var existing = new List<string>();
        var sectionComments = new List<string>();
        bool inP = false;

        if (File.Exists(iniPath))
        {
            foreach (var raw in File.ReadAllLines(iniPath))
            {
                var t = raw.Trim();
                if (t.StartsWith("["))
                {
                    inP = t.Equals("[Plugins]", StringComparison.OrdinalIgnoreCase);
                    keep.Add(raw);
                    continue;
                }
                if (inP)
                {
                    int eq = t.IndexOf('=');
                    if (eq > 0 && t.Substring(0, eq).Trim().StartsWith("Plugin", StringComparison.OrdinalIgnoreCase))
                    {
                        var v = t.Substring(eq + 1).Trim();
                        if (v.Length > 0 && !managed.Contains(v))
                            existing.Add(v);
                        continue;   // 受管插件旧条目丢弃, 将由新列表重新编号写入
                    }
                    sectionComments.Add(raw);   // [Plugins] 内的注释保留
                    continue;
                }
                keep.Add(raw);
            }
        }

        // 输出集合 = extra（调用方传入）: 添加扩展时 = 全部非受管条目; 【应用更改】时 = 开关勾选的条目
        // 取消勾选的自定义扩展不再写入（文件保留, 仅移除 [Plugins] 条目）
        var ext = new List<string>();
        if (extra != null)
        {
            foreach (var e in extra)
            {
                bool has = false;
                foreach (var x in ext)
                    if (x.Equals(e, StringComparison.OrdinalIgnoreCase)) { has = true; break; }
                if (!has) ext.Add(e);
            }
        }

        var sb = new StringBuilder();
        bool wrote = false;
        foreach (var line in keep)
        {
            sb.AppendLine(line);
            var t = line.Trim();
            if (!wrote && t.Equals("[Plugins]", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var c in sectionComments) sb.AppendLine(c);
                int idx = 0;
                foreach (var v in ext) sb.AppendLine("Plugin" + (idx++) + "=" + v);
                foreach (var v in enabled) sb.AppendLine("Plugin" + (idx++) + "=" + v);
                wrote = true;
            }
        }
        if (!wrote)
        {
            if (sb.Length > 0 && !sb.ToString().EndsWith("\r\n", StringComparison.Ordinal))
                sb.AppendLine();
            sb.AppendLine("[Plugins]");
            sb.AppendLine("; 由 ServerUI【DLL扩展】页面管理");
            int idx = 0;
            foreach (var v in ext) sb.AppendLine("Plugin" + (idx++) + "=" + v);
            foreach (var v in enabled) sb.AppendLine("Plugin" + (idx++) + "=" + v);
        }
        return sb.ToString().TrimEnd() + "\r\n";
    }

}
/*
 * ==================================================================
 * 简易选择弹窗 (DllPickForm) — 供【删除扩展】选择要移除的自定义插件
 * AntdUI.Window 风格 (与 ServerUI 主界面一致): 圆角窗口 + 主题色 + AntdUI 按钮
 * ==================================================================
 */
internal class DllPickForm : AntdUI.Window
{
    private readonly ListBox _lb;
    private string _result;

    public string Result => _result;

    public DllPickForm(string title, string[] items)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Font = new Font("Microsoft YaHei UI", 9F);
        ClientSize = new Size(480, 400);

        // 插件列表 — 主题色跟随 ServerUI 深浅色主题
        _lb = new ListBox
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(18, 14, 18, 12),
            IntegralHeight = false,
            HorizontalScrollbar = true,
            Font = new Font("Microsoft YaHei UI", 9.5F),
            BackColor = Style.Get(Colour.BgContainer),
            ForeColor = Style.Get(Colour.Text),
            BorderStyle = BorderStyle.None
        };
        foreach (var it in items) _lb.Items.Add(it);
        if (_lb.Items.Count > 0) _lb.SelectedIndex = 0;
        _lb.DoubleClick += (s, e) =>
        {
            _result = _lb.SelectedItem == null ? null : (string)_lb.SelectedItem;
            DialogResult = DialogResult.OK;
        };

        // ---- 底部工具条: 提示 + 删除/取消 (AntdUI 风格按钮) ----
        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            ColumnCount = 3, RowCount = 1,
            Padding = new Padding(18, 12, 18, 12)
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104F));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88F));
        bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var lbTip = new AntdUI.Label
        {
            Text = "提示：双击列表项，或选中后点击【删除】",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft YaHei UI", 8.5F),
            ForeColor = Style.Get(Colour.TextSecondary)
        };

        var btOk = new AntdUI.Button
        {
            Text = "删除",
            Type = TTypeMini.Error,
            IconSvg = "DeleteOutlined",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            WaveSize = 0,
            BorderWidth = 1F
        };
        btOk.Click += (s, e) =>
        {
            _result = _lb.SelectedItem == null ? null : (string)_lb.SelectedItem;
            DialogResult = DialogResult.OK;
        };

        var btCancel = new AntdUI.Button
        {
            Text = "取消",
            Type = TTypeMini.Default,
            IconSvg = "CloseOutlined",
            Dock = DockStyle.Fill,
            Cursor = Cursors.Hand,
            WaveSize = 0,
            BorderWidth = 1F
        };
        btCancel.Click += (s, e) => DialogResult = DialogResult.Cancel;

        bar.Controls.Add(lbTip, 0, 0);
        bar.Controls.Add(btOk, 1, 0);
        bar.Controls.Add(btCancel, 2, 0);

        Controls.Add(_lb);
        Controls.Add(bar);

        KeyPreview = true;
        KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
        };
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // 简单 DPI 缩放: 保持窗口在父窗体中央 (与时间工具 PromptForm 一致)
        if (Owner != null)
        {
            var midX = Owner.Left + Owner.Width / 2;
            var midY = Owner.Top + Owner.Height / 2;
            Left = midX - Width / 2;
            Top = midY - Height / 2;
        }
    }

    /// <summary>弹出选择框, 返回选中的项; 取消返回 null</summary>
    public static string Pick(Form owner, string title, string[] items)
    {
        if (items == null || items.Length == 0) return null;
        using var f = new DllPickForm(title, items);
        if (owner != null && owner.IsHandleCreated) f.ShowDialog(owner);
        else f.ShowDialog();
        return f.Result;
    }
}

/*
 * ==================================================================
 * Ini 编辑器 (IniEditorForm) — 插件配置可视化编辑窗口
 * 不直接编辑 ini 文本, 而是按规则解析后渲染成表单:
 *   布尔值 → 开关; 数字 → 上下箭头微调; 其余 → 文本框; 注释行 → 灰色说明
 * 分组管理 (参考启动器 AddSection/AddKeyInSection/RemoveSection 机制):
 *   [分组] 标题行 → 「+ 添加键」「删除分组」; 键行行尾 → 「删除」
 *   顶部「添加分组」追加新分组到文件末尾; 新键规则: 键名不能含 '=', 分组内键名不可重复
 * 用户直观调节, 保存时自动转义回 ini 格式 (保留节/注释/键顺序与原值写法)
 * 快捷键: Esc 取消 / Ctrl+S 保存; 编码保持原文件 (UTF-8 / GBK), 新建按 UTF-8 无 BOM
 * ==================================================================
 */
internal class IniEditorForm : AntdUI.Window
{
    // ================= 数据模型 =================
    // ini 解析后的一行: 注释/空行/节头按原文保留; key=value 渲染为可调节控件
    private sealed class IniLine
    {
        public string Raw;        // 原始行文本
        public bool IsKey;        // 是否为 key=value
        public string Key;        // 键名
        public string Value;      // 原始值 (Trim 后)
        public bool BoolStyle;    // 布尔风格: 1/0/true/false/on/off/yes/no/enable/disable
        public bool NumStyle;     // 数字风格
        public int Digits;        // 小数位数 (NumStyle 时, 用于回写保留精度)
        public RowCtl Ctl;        // 对应的行控件 (保存时回读)
    }

    private sealed class RowCtl
    {
        public AntdUI.Switch Sw;
        public AntdUI.InputNumber Num;
        public AntdUI.Input Txt;
    }

    private readonly List<IniLine> _lines = new List<IniLine>();
    private readonly string _path;
    private TableLayoutPanel _tbl;         // 表单表格 (重建时替换内容)
    private bool _dirty;                   // 是否有结构修改 (添加/删除分组/键)
    private readonly string _title;        // 原始标题 (结构修改后显示"已修改"状态)

    // ================= 构造: 解析 + 渲染表单 =================
    public IniEditorForm(string title, string initText, string path, bool exists)
    {
        Text = title;
        _title = title;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Font = new Font("Microsoft YaHei UI", 9F);
        ClientSize = new Size(940, 620);
        _path = path;

        ParseIni(initText ?? "");

        // ---- 顶部提示条 + 添加分组按钮 ----
        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Height = 44,
            ColumnCount = 2, RowCount = 1,
            Padding = new Padding(16, 0, 16, 0)
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118F));
        top.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        var lbTip = new AntdUI.Label
        {
            Text = "直接调节：开关 = 启用/关闭 · 数字 = 上下箭头微调 · 文本框 = 填写内容 · 灰色小字是原配置注释。分组标题行可「添加键 / 删除分组」，键行行尾可「删除」；保存后自动转回 ini 格式",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft YaHei UI", 8.5F),
            ForeColor = Style.Get(Colour.TextSecondary)
        };
        var btAddGroup = new AntdUI.Button
        {
            Text = "添加分组",
            Type = TTypeMini.Default,
            IconSvg = "PlusOutlined",
            Dock = DockStyle.Fill,
            Margin = new Padding(10, 6, 0, 6),
            Cursor = Cursors.Hand,
            WaveSize = 0,
            BorderWidth = 1F
        };
        btAddGroup.Click += (s, e) => AddGroup();
        top.Controls.Add(lbTip, 0, 0);
        top.Controls.Add(btAddGroup, 1, 0);

        // ---- 可滚动表单区 (参考 DLL扩展页滚动结构) ----
        var scroll = new AntdUI.In.Panel { Dock = DockStyle.Fill, AutoScroll = true };
        _tbl = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3, RowCount = 0,
            BackColor = Style.Get(Colour.BgContainer),
            Padding = new Padding(16, 8, 16, 8)
        };
        _tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200F));   // 键名列
        _tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));    // 控件列
        _tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92F));    // 行操作列
        scroll.Controls.Add(_tbl);

        BuildForm(_tbl);

        // ---- 底部工具条: 路径说明 + 保存/取消 ----
        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 58,
            ColumnCount = 2, RowCount = 1,
            Padding = new Padding(14, 11, 14, 11)
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200F));
        bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var lbPath = new AntdUI.Label
        {
            Text = "保存位置：游戏根目录 → " + path + (exists ? "" : "（新文件，不存在时将自动创建）"),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft YaHei UI", 8.5F),
            ForeColor = Style.Get(Colour.TextSecondary)
        };

        var btBox = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        btBox.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
        btBox.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
        btBox.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var btSave = new AntdUI.Button
        {
            Text = "保存", Type = TTypeMini.Primary, IconSvg = "SaveOutlined",
            Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0),
            Cursor = Cursors.Hand, WaveSize = 0, BorderWidth = 1F
        };
        btSave.Click += (s, e) => DialogResult = DialogResult.OK;

        var btCancel = new AntdUI.Button
        {
            Text = "取消", Type = TTypeMini.Default, IconSvg = "CloseOutlined",
            Dock = DockStyle.Fill, Cursor = Cursors.Hand, WaveSize = 0, BorderWidth = 1F
        };
        btCancel.Click += (s, e) => DialogResult = DialogResult.Cancel;

        btBox.Controls.Add(btSave, 0, 0);
        btBox.Controls.Add(btCancel, 1, 0);
        bar.Controls.Add(lbPath, 0, 0);
        bar.Controls.Add(btBox, 1, 0);

        Controls.Add(top);
        Controls.Add(bar);
        Controls.Add(scroll);

        // 快捷键: Esc 取消, Ctrl+S 保存
        KeyPreview = true;
        KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
            else if (e.Control && e.KeyCode == Keys.S)
            {
                e.SuppressKeyPress = true;
                DialogResult = DialogResult.OK;
            }
        };
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (Owner != null)
        {
            var midX = Owner.Left + Owner.Width / 2;
            var midY = Owner.Top + Owner.Height / 2;
            Left = midX - Width / 2;
            Top = midY - Height / 2;
        }
    }

    // ================= 解析 =================
    void ParseIni(string text)
    {
        _lines.Clear();
        using var sr = new StringReader(text);
        string ln;
        while ((ln = sr.ReadLine()) != null)
        {
            var t = ln.Trim();
            if (t.Length == 0 || t.StartsWith(";") || t.StartsWith("#") || t.StartsWith("["))
            {
                _lines.Add(new IniLine { Raw = ln });
                continue;
            }
            int eq = t.IndexOf('=');
            if (eq > 0)
            {
                var v = t.Substring(eq + 1).Trim();
                var il = new IniLine
                {
                    IsKey = true,
                    Raw = ln,
                    Key = t.Substring(0, eq).Trim(),
                    Value = v
                };
                il.BoolStyle = IsBoolText(v);
                il.NumStyle = !il.BoolStyle && IsNumText(v, out il.Digits);
                _lines.Add(il);
            }
            else
            {
                _lines.Add(new IniLine { Raw = ln });
            }
        }
    }

    // ================= 渲染表单 (含分组管理操作) =================
    void BuildForm(TableLayoutPanel tbl)
    {
        void AddRow(float h)
        {
            tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
            tbl.RowCount++;
        }

        bool sectionSeen = false;   // 是否已遇到 [节头]
        bool mainCap = false;       // 是否已渲染 "(主配置)" 标题行

        foreach (var il in _lines)
        {
            if (il.IsKey)
            {
                // 文件开头 (任意分组之前) 的键 → 归入 "(主配置)", 先渲染一个标题行
                if (!sectionSeen && !mainCap)
                {
                    AddRow(40F);
                    int r0 = tbl.RowCount - 1;
                    var cap0 = MakeSectionCap(tbl, "(主配置)", null, false);
                    tbl.Controls.Add(cap0, 0, r0);
                    tbl.SetColumnSpan(cap0, 3);
                    mainCap = true;
                }

                AddRow(38F);
                int row = tbl.RowCount - 1;

                var lbKey = new AntdUI.Label
                {
                    Text = il.Key,
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
                    ForeColor = Style.Get(Colour.Text),
                    Margin = new Padding(18, 0, 0, 0)
                };

                // 控件按值类型: 布尔 → 开关; 数字 → 上下箭头微调; 其余 → 文本框
                if (il.BoolStyle)
                {
                    var sw = new AntdUI.Switch
                    {
                        Checked = BoolParse(il.Value),
                        Cursor = Cursors.Hand,
                        Size = new Size(40, 22),
                        Anchor = AnchorStyles.Left,
                        Margin = new Padding(8, 8, 0, 0)
                    };
                    il.Ctl = new RowCtl { Sw = sw };
                    tbl.Controls.Add(lbKey, 0, row);
                    tbl.Controls.Add(sw, 1, row);
                }
                else if (il.NumStyle)
                {
                    var num = new AntdUI.InputNumber
                    {
                        Value = NumParse(il.Value),
                        ShowControl = true,
                        DecimalPlaces = il.Digits > 0 ? il.Digits : 0,
                        Width = 220,
                        Height = 30,
                        Anchor = AnchorStyles.Left,
                        Margin = new Padding(8, 4, 0, 0),
                        Font = new Font("Microsoft YaHei UI", 9.5F)
                    };
                    il.Ctl = new RowCtl { Num = num };
                    tbl.Controls.Add(lbKey, 0, row);
                    tbl.Controls.Add(num, 1, row);
                }
                else
                {
                    var txt = new AntdUI.Input
                    {
                        Text = il.Value,
                        Dock = DockStyle.Fill,
                        Radius = 6,
                        Font = new Font("Microsoft YaHei UI", 9.5F),
                        Margin = new Padding(8, 3, 0, 3)
                    };
                    il.Ctl = new RowCtl { Txt = txt };
                    tbl.Controls.Add(lbKey, 0, row);
                    tbl.Controls.Add(txt, 1, row);
                }

                // 键行行尾「删除」按钮 — 移除该键 (启动器 RemoveKeyInSection)
                var il2 = il;
                var del = new AntdUI.Button
                {
                    Text = "删除",
                    Type = TTypeMini.Default,
                    IconSvg = "CloseOutlined",
                    Dock = DockStyle.Fill,
                    Margin = new Padding(4, 5, 0, 5),
                    Cursor = Cursors.Hand,
                    WaveSize = 0,
                    BorderWidth = 1F
                };
                del.Click += (s, e) => RemoveKey(il2);
                tbl.Controls.Add(del, 2, row);
            }
            else
            {
                var t = il.Raw.Trim();
                if (t.StartsWith("[") && t.EndsWith("]"))
                {
                    // 分组标题行: 节名 + 「添加键」+「删除分组」(启动器 AddSection/RemoveSection)
                    sectionSeen = true;
                    var sec = t.Substring(1, t.Length - 2).Trim();
                    AddRow(40F);
                    int row = tbl.RowCount - 1;
                    var cap = MakeSectionCap(tbl, sec, sec, true);
                    tbl.Controls.Add(cap, 0, row);
                    tbl.SetColumnSpan(cap, 3);
                }
                else if (t.StartsWith(";") || t.StartsWith("#"))
                {
                    // 注释行 → 灰色说明文字 (跨三列), 帮助理解每个键的含义
                    AddRow(24F);
                    int row = tbl.RowCount - 1;
                    var lbCm = new AntdUI.Label
                    {
                        Text = t,
                        Font = new Font("Microsoft YaHei UI", 8.5F),
                        ForeColor = Style.Get(Colour.TextTertiary),
                        Dock = DockStyle.Fill,
                        TextAlign = ContentAlignment.MiddleLeft,
                        Margin = new Padding(24, 0, 0, 0)
                    };
                    tbl.Controls.Add(lbCm, 0, row);
                    tbl.SetColumnSpan(lbCm, 3);
                }
                // 空行: 直接跳过
            }
        }

        // 表单行全部添加后强制重排 (AutoSize 表格)
        tbl.ResumeLayout(true);
        tbl.PerformLayout();
    }

    /* 分组标题行: [分组名] + 「添加键」(Default) + 「删除分组」(Error); 主配置标题无删除按钮 */
    TableLayoutPanel MakeSectionCap(TableLayoutPanel tbl, string capName, string secKey, bool canRemove)
    {
        var cap = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3, RowCount = 1,
            BackColor = Color.Transparent
        };
        cap.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        cap.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100F));
        cap.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108F));
        cap.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var lb = new AntdUI.Label
        {
            Text = "▍ " + capName,
            Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold),
            ForeColor = Style.Get(Colour.Primary),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(6, 0, 0, 0)
        };
        var sec = secKey;
        var btAdd = new AntdUI.Button
        {
            Text = "添加键",
            Type = TTypeMini.Default,
            IconSvg = "PlusOutlined",
            Dock = DockStyle.Fill,
            Margin = new Padding(4, 5, 4, 5),
            Cursor = Cursors.Hand,
            WaveSize = 0,
            BorderWidth = 1F
        };
        btAdd.Click += (s, e) => AddKey(sec);
        cap.Controls.Add(lb, 0, 0);
        cap.Controls.Add(btAdd, 1, 0);

        if (canRemove)
        {
            var btRm = new AntdUI.Button
            {
                Text = "删除分组",
                Type = TTypeMini.Error,
                IconSvg = "DeleteOutlined",
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 5, 0, 5),
                Cursor = Cursors.Hand,
                WaveSize = 0,
                BorderWidth = 1F
            };
            btRm.Click += (s, e) => RemoveGroup(sec);
            cap.Controls.Add(btRm, 2, 0);
        }
        else
        {
            cap.Controls.Add(new System.Windows.Forms.Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent }, 2, 0);
        }
        return cap;
    }

    // ============ 分组管理 (参考启动器 AddSection/AddKeyInSection/RemoveSection) ============
    /* 重建整个表单 — 分组结构变化后调用 (释放旧控件防泄漏) */
    void RebuildForm()
    {
        if (_tbl == null) return;
        _tbl.SuspendLayout();
        foreach (Control c in _tbl.Controls) c.Dispose();
        _tbl.Controls.Clear();
        _tbl.RowStyles.Clear();
        _tbl.RowCount = 0;
        BuildForm(_tbl);
        _tbl.ResumeLayout(true);
        _tbl.PerformLayout();
    }

    /* 结构修改后刷新窗口标题 — 提示"已修改" (保存后写回, 取消则丢弃) */
    void RefreshTitle()
    {
        Text = _dirty ? _title + "（已修改，保存后写入）" : _title;
    }

    /* 定位分组 [sec] 的起止索引: start=节头行, end=下一节头或文件末尾; 找不到返回 (-1,-1) */
    (int start, int end) SectionRange(string sec)
    {
        int start = -1, end = _lines.Count;
        for (int i = 0; i < _lines.Count; i++)
        {
            if (_lines[i].IsKey) continue;
            var t = _lines[i].Raw.Trim();
            if (!(t.StartsWith("[") && t.EndsWith("]"))) continue;
            var name = t.Substring(1, t.Length - 2).Trim();
            if (start >= 0) { end = i; break; }
            if (name.Equals(sec, StringComparison.OrdinalIgnoreCase)) start = i;
        }
        return (start, end);
    }

    /* 生成新键行 — 值类型推断规则与 ParseIni 一致 (布尔/数字/文本) */
    IniLine MakeKeyLine(string key, string val)
    {
        var il = new IniLine { IsKey = true, Key = key, Value = val, Raw = key + "=" + val };
        il.BoolStyle = IsBoolText(val);
        il.NumStyle = !il.BoolStyle && IsNumText(val, out il.Digits);
        return il;
    }

    /* 添加分组 — 输入节名, 追加到文件末尾 (规则: 名称不能含 [ ] 且不可与已有分组重名) */
    void AddGroup()
    {
        var r = IniAskForm.Ask(this, "添加分组",
            "分组名称（如 Config；规则：不能包含 [ ]，且不能与已有分组重名）", null);
        if (r == null) return;
        var name = r[0].Trim();
        if (name.Length == 0) return;
        if (name.IndexOf('[') >= 0 || name.IndexOf(']') >= 0)
        {
            MessageBox.Show("分组名称不能包含 [ 或 ] 字符。", "添加分组",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        foreach (var il in _lines)
        {
            if (il.IsKey) continue;
            var t = il.Raw.Trim();
            if (t.StartsWith("[") && t.EndsWith("]")
                && t.Substring(1, t.Length - 2).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("分组 [" + name + "] 已存在。", "添加分组",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
        }
        _lines.Add(new IniLine { Raw = "[" + name + "]" });
        _dirty = true;
        RefreshTitle();
        RebuildForm();
    }

    /* 删除分组 — 整节移除 (含节内键与注释; 节前的注释保留) */
    void RemoveGroup(string sec)
    {
        var (start, end) = SectionRange(sec);
        if (start < 0) return;
        if (MessageBox.Show("确定删除分组 [" + sec + "]？\n\n该分组下的键与配置将一并移除。", "删除分组",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _lines.RemoveRange(start, end - start);
        _dirty = true;
        RefreshTitle();
        RebuildForm();
    }

    /* 添加键 — 输入键名与值, 插入到分组末尾 (规则: 键名不能含 =, 分组内不可重名) */
    void AddKey(string sec)
    {
        var r = IniAskForm.Ask(this, sec == null ? "添加键（主配置）" : "添加键 — 分组 [" + sec + "]",
            "键名（规则：不能包含 =，分组内不可重名）", "值（可留空）");
        if (r == null) return;
        var key = r[0].Trim();
        if (key.Length == 0 || key.IndexOf('=') >= 0)
        {
            MessageBox.Show("键名不能为空，且不能包含 = 字符。", "添加键",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        int start, end;
        if (sec == null)
        {
            start = 0;
            end = _lines.Count;
            for (int i = 0; i < _lines.Count; i++)
            {
                if (_lines[i].IsKey) continue;
                var t = _lines[i].Raw.Trim();
                if (t.StartsWith("[") && t.EndsWith("]")) { end = i; break; }
            }
        }
        else
        {
            (start, end) = SectionRange(sec);
            if (start < 0)
            {
                MessageBox.Show("分组 [" + sec + "] 不存在。", "添加键",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
        }
        // 分组内键名去重
        for (int i = start + 1; i < end; i++)
        {
            if (_lines[i].IsKey && _lines[i].Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("该分组下已存在同名键「" + key + "」。", "添加键",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
        }
        // 插入到分组内最后一个键行之后 (无键时插在节头后)
        int insert = end;
        for (int i = start + 1; i < end; i++)
            if (_lines[i].IsKey) insert = i + 1;
        var val = r.Length > 1 ? r[1].Trim() : "";
        _lines.Insert(insert, MakeKeyLine(key, val));
        _dirty = true;
        RefreshTitle();
        RebuildForm();
    }

    /* 删除键 — 移除单个键行 */
    void RemoveKey(IniLine il)
    {
        if (MessageBox.Show("确定删除键「" + il.Key + "」？", "删除键",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _lines.Remove(il);
        _dirty = true;
        RefreshTitle();
        RebuildForm();
    }

    // ================= 判定与转换 =================
    static bool IsBoolText(string v)
    {
        var s = v.ToLowerInvariant();
        return s == "1" || s == "0" || s == "true" || s == "false"
            || s == "on" || s == "off" || s == "yes" || s == "no"
            || s == "enable" || s == "enabled" || s == "disable" || s == "disabled";
    }

    static bool IsNumText(string v, out int digits)
    {
        digits = 0;
        if (double.TryParse(v, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            var i = v.IndexOf('.');
            if (i >= 0) digits = v.Length - i - 1;
            return true;
        }
        return false;
    }

    static bool BoolParse(string v)
    {
        var s = v.ToLowerInvariant();
        return s == "1" || s == "true" || s == "yes" || s == "on"
            || s == "enable" || s == "enabled";
    }

    static decimal NumParse(string v)
    {
        decimal.TryParse(v, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d);
        return d;
    }

    // ================= 回写转义 (ini 文本) =================
    string RebuildIniText()
    {
        var sb = new StringBuilder();
        foreach (var il in _lines)
        {
            if (!il.IsKey)
            {
                sb.Append(il.Raw).Append("\r\n");
                continue;
            }
            var ctl = il.Ctl;
            string v;
            if (ctl == null) v = il.Value;
            else if (ctl.Sw != null) v = BoolToOrig(ctl.Sw.Checked, il.Value);
            else if (ctl.Num != null)
            {
                var dv = ctl.Num.Value;
                v = il.Digits > 0
                    ? dv.ToString("0." + new string('0', il.Digits), System.Globalization.CultureInfo.InvariantCulture)
                    : dv.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
            }
            else v = ctl.Txt != null ? ctl.Txt.Text.Trim() : il.Value;
            sb.Append(il.Key).Append('=').Append(v).Append("\r\n");
        }
        return sb.ToString().TrimEnd() + "\r\n";
    }

    /* 布尔回写 — 保持原配置文件的值写法 (1/0、true/false、on/off…) */
    static string BoolToOrig(bool b, string orig)
    {
        var s = orig.ToLowerInvariant();
        if (s == "1" || s == "0") return b ? "1" : "0";
        if (s == "on" || s == "off") return b ? "on" : "off";
        if (s == "yes" || s == "no") return b ? "yes" : "no";
        if (s == "enable" || s == "disable") return b ? "enable" : "disable";
        if (s == "enabled" || s == "disabled") return b ? "enabled" : "disabled";
        return b ? "true" : "false";
    }

    // ================= 入口 =================
    /// <summary>打开可视化编辑器; 返回转义后的 ini 完整文本, 取消返回 null</summary>
    public static string Edit(Form owner, string title, string initText, string path, bool exists)
    {
        using var f = new IniEditorForm(title, initText, path, exists);
        if (owner != null && owner.IsHandleCreated) f.ShowDialog(owner);
        else f.ShowDialog();
        if (f.DialogResult != DialogResult.OK) return null;
        return f.RebuildIniText();
    }
}

/*
 * ==================================================================
 * 输入弹窗 (IniAskForm) — 供 IniEditorForm 添加分组/添加键时输入名称与值
 * AntdUI.Window 风格, 可带 1~2 个输入框; 快捷键: Esc 取消 / Enter 确定
 * ==================================================================
 */
internal class IniAskForm : AntdUI.Window
{
    private readonly AntdUI.Input _in1;
    private readonly AntdUI.Input _in2;

    public string[] Result { get; private set; }

    public IniAskForm(string title, string label1, string label2)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Font = new Font("Microsoft YaHei UI", 9F);
        bool two = !string.IsNullOrEmpty(label2);
        ClientSize = new Size(440, two ? 240 : 190);

        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = two ? 4 : 2,
            Padding = new Padding(16, 10, 16, 8)
        };
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));

        var lb1 = new AntdUI.Label
        {
            Text = label1,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft YaHei UI", 9F),
            ForeColor = Style.Get(Colour.Text)
        };
        _in1 = new AntdUI.Input
        {
            Dock = DockStyle.Fill,
            Radius = 6,
            Font = new Font("Microsoft YaHei UI", 9.5F),
            Margin = new Padding(8, 2, 0, 2)
        };
        main.Controls.Add(lb1, 0, 0);
        main.Controls.Add(_in1, 1, 0);

        if (two)
        {
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            var lb2 = new AntdUI.Label
            {
                Text = label2,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Microsoft YaHei UI", 9F),
                ForeColor = Style.Get(Colour.Text)
            };
            _in2 = new AntdUI.Input
            {
                Dock = DockStyle.Fill,
                Radius = 6,
                Font = new Font("Microsoft YaHei UI", 9.5F),
                Margin = new Padding(8, 2, 0, 2)
            };
            main.Controls.Add(lb2, 0, 2);
            main.Controls.Add(_in2, 1, 2);
        }
        else
        {
            _in2 = null;
        }

        // ---- 底部按钮条: 确定/取消 (AntdUI 风格) ----
        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 54,
            ColumnCount = 3, RowCount = 1,
            Padding = new Padding(16, 8, 16, 8)
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92F));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92F));
        bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var btOk = new AntdUI.Button
        {
            Text = "确定",
            Type = TTypeMini.Primary,
            IconSvg = "CheckOutlined",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            WaveSize = 0,
            BorderWidth = 1F
        };
        btOk.Click += (s, e) => Apply();
        var btCancel = new AntdUI.Button
        {
            Text = "取消",
            Type = TTypeMini.Default,
            IconSvg = "CloseOutlined",
            Dock = DockStyle.Fill,
            Cursor = Cursors.Hand,
            WaveSize = 0,
            BorderWidth = 1F
        };
        btCancel.Click += (s, e) => DialogResult = DialogResult.Cancel;

        bar.Controls.Add(new System.Windows.Forms.Panel { Dock = DockStyle.Fill }, 0, 0);
        bar.Controls.Add(btOk, 1, 0);
        bar.Controls.Add(btCancel, 2, 0);

        Controls.Add(main);
        Controls.Add(bar);

        // 快捷键: Esc 取消, Enter 确定
        KeyPreview = true;
        KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
            else if (e.KeyCode == Keys.Enter && !e.Control)
            {
                e.SuppressKeyPress = true;
                Apply();
            }
        };
    }

    void Apply()
    {
        Result = _in2 == null
            ? new[] { _in1.Text.Trim() }
            : new[] { _in1.Text.Trim(), _in2.Text.Trim() };
        DialogResult = DialogResult.OK;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (Owner != null)
        {
            var midX = Owner.Left + Owner.Width / 2;
            var midY = Owner.Top + Owner.Height / 2;
            Left = midX - Width / 2;
            Top = midY - Height / 2;
        }
        _in1.Focus();
    }

    /// <summary>弹出输入框; 返回输入文本数组 (1~2 项), 取消返回 null</summary>
    public static string[] Ask(Form owner, string title, string label1, string label2)
    {
        using var f = new IniAskForm(title, label1, label2);
        if (owner != null && owner.IsHandleCreated) f.ShowDialog(owner);
        else f.ShowDialog();
        return f.Result;
    }
}
