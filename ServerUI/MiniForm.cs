/*
 * ==================================================================
 * 极简模式窗口 (MiniForm.cs) — 独立于主界面源码的全新小窗口
 * ==================================================================
 *
 * 【设计说明】
 *   极简模式 = 一个独立的小窗口, 只保留最基础的高频功能:
 *     - 开始游戏 (一键启动服务端并进入游戏)
 *     - GM工具  (启动 DfoGmTool 网页管理后台)
 *     - 停止服务端 (优雅停服, 防止数据库回档)
 *     - 存档切换与管理 (列表/双击切换/储存当前/撤销换挡)
 *   关闭极简窗口即返回完整模式, 主窗口完全不受影响。
 *
 * 【与主窗口的关系】
 *   极简窗口持有 MainForm 引用, 复用其 internal 方法与服务实例,
 *   不复制任何业务逻辑, 不修改主窗口任何代码。
 * ==================================================================
 */
using System;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.VisualBasic;
using AntdUI;
using ServerUI.Models;
using ServerUI.Services;

namespace ServerUI;

public partial class MiniForm : AntdUI.Window
{
    readonly MainForm _main;

    // UI 控件
    AntdUI.Label lbStatus, lbPvf, lbInfo;
    AntdUI.Button btPlay, btGm, btUpd, btSave, btUndo, btRefresh, btBack;
    AntdUI.Table lv;

    // 配色 (与主窗口一致)
    static readonly Color Gn = Color.FromArgb(40, 167, 69);
    static readonly Color Rd = Color.FromArgb(220, 53, 69);
    static readonly Color Or = Color.FromArgb(253, 126, 20);
    static readonly Color Cy = Color.FromArgb(78, 201, 176);
    static readonly Color Txt2 = Color.FromArgb(176, 176, 184);

    public MiniForm(MainForm main)
    {
        _main = main;

        // 窗口属性 — 小窗口
        MinimumSize = new Size(600, 420);
        Size = new Size(620, 470);
        StartPosition = FormStartPosition.CenterScreen;
        ControlBox = false;
        Text = "ServerS4A12 极简模式";
        Font = new Font("Microsoft YaHei UI", 9.5f);

        FormClosing += (s, e) =>
        {
            // 关闭极简窗口(X) = 返回完整模式 (隐藏而非真正关闭, 避免递归)
            _main.BackToFullMode();
            e.Cancel = true;
        };

        // 使用与完整模式相同的窗口图标
        try { if (_main.Icon != null) Icon = _main.Icon; } catch { }

        BuildUi();

        // 状态刷新由主窗口每 2 秒广播 (OnMainTick), 不再独立轮询
        RefreshStatus();
        RefreshArchives();
    }

    /*
     * 由主窗口状态刷新广播调用 — 复用主窗口检测结果, 减少进程枚举与磁盘 IO
     */
    internal void OnMainTick() => RefreshStatus();

    /*
     * 构建极简界面 — 紧凑的单列布局
     */
    void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1, RowCount = 7,
            BackColor = Color.Transparent,
            Padding = new Padding(12)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));   // 标题栏
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));   // 状态行
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56F));   // 开始游戏
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));   // GM / 更新
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));   // 存档操作
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));   // 存档列表
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));   // 提示行
        Controls.Add(root);

        // ===== 标题栏 (可拖动窗口) =====
        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3, RowCount = 1,
            BackColor = Color.Transparent
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44F));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130F));
        var title = new AntdUI.Label
        {
            Text = "极简模式 · ServerS4A12",
            Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Cursor = Cursors.Hand
        };
        title.MouseDown += DragWindow;
        bar.MouseDown += DragWindow;
        // 浅色/深色主题切换 (与完整模式一致)
        var btTheme = new AntdUI.Button
        {
            Dock = DockStyle.Fill,
            Ghost = true,
            Radius = 0,
            WaveSize = 0,
            IconSvg = "SunOutlined",
            ToggleIconSvg = "MoonOutlined",
            Toggle = false,
            Cursor = Cursors.Hand,
            Margin = new Padding(2)
        };
        btTheme.Click += (s, e) =>
        {
            Config.IsLight = !Config.IsLight;
            _main.OnThemeChanged();   // 同步主窗口固定色背景与按钮色板
        };
        btBack = new AntdUI.Button
        {
            Text = "返回完整模式",
            Type = TTypeMini.Default,
            Dock = DockStyle.Fill,
            Cursor = Cursors.Hand,
            Margin = new Padding(4)
        };
        btBack.BorderWidth = 1.5F;
        btBack.DefaultBorderColor = Color.FromArgb(100, 102, 110);
        btBack.Click += (s, e) => _main.BackToFullMode();
        bar.Controls.Add(title, 0, 0);
        bar.Controls.Add(btTheme, 1, 0);
        bar.Controls.Add(btBack, 2, 0);
        root.Controls.Add(bar, 0, 0);

        // ===== 状态行 =====
        var stRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3, RowCount = 1,
            BackColor = Color.Transparent
        };
        stRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34F));
        stRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33F));
        stRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33F));
        lbStatus = new AntdUI.Label
        {
            Text = "● 未运行",
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
            ForeColor = Rd,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        lbPvf = new AntdUI.Label
        {
            Text = "PVF: ● 检测中",
            Font = new Font("Microsoft YaHei UI", 9f),
            ForeColor = Txt2,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        lbInfo = new AntdUI.Label
        {
            Text = "版本: --",
            Font = new Font("Microsoft YaHei UI", 9f),
            ForeColor = Txt2,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight
        };
        stRow.Controls.Add(lbStatus, 0, 0);
        stRow.Controls.Add(lbPvf, 1, 0);
        stRow.Controls.Add(lbInfo, 2, 0);
        root.Controls.Add(stRow, 0, 1);

        // ===== 开始游戏 =====
        btPlay = new AntdUI.Button
        {
            Text = "开始游戏",
            Type = TTypeMini.Success,
            IconSvg = "PlayCircleOutlined",
            Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold),
            Dock = DockStyle.Fill,
            Cursor = Cursors.Hand,
            Margin = new Padding(4)
        };
        btPlay.Click += async (s, e) => await _main.Play();
        root.Controls.Add(btPlay, 0, 2);

        // ===== GM工具 / 停止服务端 =====
        var row2 = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2, RowCount = 1,
            BackColor = Color.Transparent
        };
        row2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        row2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        btGm = new AntdUI.Button
        {
            Text = "GM工具",
            Type = TTypeMini.Warn,
            IconSvg = "ToolOutlined",
            Dock = DockStyle.Fill,
            Cursor = Cursors.Hand,
            Margin = new Padding(4)
        };
        btGm.Click += (s, e) => _main.LaunchGmTool();
        btUpd = new AntdUI.Button
        {
            Text = "停止服务端",
            Type = TTypeMini.Error,
            IconSvg = "StopOutlined",
            Dock = DockStyle.Fill,
            Cursor = Cursors.Hand,
            Margin = new Padding(4)
        };
        btUpd.Click += (s, e) =>
        {
            _main.Lg(">>> 极简模式: 停止服务端", Color.Gold);
            System.Threading.Tasks.Task.Run(() =>
            {
                _main._sv.Stop();
                try { _main.Invoke(new Action(() =>
                    _main.Lg(">>> 已停止服务端进程树", Color.Gold))); } catch { }
            });
        };
        row2.Controls.Add(btGm, 0, 0);
        row2.Controls.Add(btUpd, 1, 0);
        root.Controls.Add(row2, 0, 3);

        // ===== 存档操作 =====
        var row3 = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4, RowCount = 1,
            BackColor = Color.Transparent
        };
        for (int i = 0; i < 4; i++)
            row3.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
        btSave = new AntdUI.Button
        {
            Text = "储存当前",
            Type = TTypeMini.Primary,
            IconSvg = "SaveOutlined",
            Dock = DockStyle.Fill,
            Cursor = Cursors.Hand,
            Margin = new Padding(4)
        };
        btSave.Click += (s, e) =>
        {
            if (!CheckServer()) return;
            var n = Interaction.InputBox("名称:", "储存当前存档",
                DateTime.Now.ToString("MMdd_HHmm"));
            if (!string.IsNullOrWhiteSpace(n))
            {
                _main._ar.SaveArchive(_main._ad, n);
                RefreshArchives();
            }
        };
        btUndo = new AntdUI.Button
        {
            Text = "撤销换挡",
            Type = TTypeMini.Default,
            IconSvg = "UndoOutlined",
            Dock = DockStyle.Fill,
            Cursor = Cursors.Hand,
            Margin = new Padding(4)
        };
        btUndo.BorderWidth = 1.5F;
        btUndo.DefaultBorderColor = Color.FromArgb(100, 102, 110);
        btUndo.Click += (s, e) =>
        {
            if (!CheckServer()) return;
            if (!_main._ar.UndoSwap(_main._ad))
            {
                MessageBox.Show("没有可用的备份", "撤销换挡",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            MessageBox.Show("已从最近一次备份恢复", "撤销换挡",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            RefreshArchives();
        };
        btRefresh = new AntdUI.Button
        {
            Text = "刷新",
            Type = TTypeMini.Default,
            IconSvg = "ReloadOutlined",
            Dock = DockStyle.Fill,
            Cursor = Cursors.Hand,
            Margin = new Padding(4)
        };
        btRefresh.BorderWidth = 1.5F;
        btRefresh.DefaultBorderColor = Color.FromArgb(100, 102, 110);
        btRefresh.Click += (s, e) => RefreshArchives();
        var lbHint = new AntdUI.Label
        {
            Text = "双击存档 = 切换",
            Font = new Font("Microsoft YaHei UI", 8.5f),
            ForeColor = Txt2,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter
        };
        row3.Controls.Add(btSave, 0, 0);
        row3.Controls.Add(btUndo, 1, 0);
        row3.Controls.Add(btRefresh, 2, 0);
        row3.Controls.Add(lbHint, 3, 0);
        root.Controls.Add(row3, 0, 4);

        // ===== 存档列表 =====
        lv = new AntdUI.Table
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(4, 2, 4, 2),
            FixedHeader = true,
            AutoSizeColumnsMode = AntdUI.ColumnsMode.Fill
        };
        lv.Columns = new ColumnCollection
        {
            new Column("Name", "存档名称") { Ellipsis = true },
            new Column("Modified", "修改时间") { Width = "120", Align = ColumnAlign.Center },
        };
        // ★ AntdUI 双击只会触发 CellDoubleClick, 不会触发 CellClick
        lv.CellDoubleClick += Lv_CellClick;
        root.Controls.Add(lv, 0, 5);

        // ===== 提示行 =====
        var tip = new AntdUI.Label
        {
            Text = "提示: 服务端运行中无法操作存档; 停服请点【停止服务端】(优雅停服防止回档)",
            Font = new Font("Microsoft YaHei UI", 8f),
            ForeColor = Txt2,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter
        };
        root.Controls.Add(tip, 0, 6);

        // 双缓冲
        EnableDoubleBuffer(this);

        // 当前为浅色主题时, 立即应用自定义按钮色板 (与完整模式一致)
        if (Config.IsLight)
            MainForm.ApplyButtonsColor(this);
    }

    /*
     * 状态刷新 — 服务端 / PVF / 版本
     */
    void RefreshStatus()
    {
        try
        {
            var distDir = Path.Combine(_main._ad, "ServerS4A12-AUM",
                "dist", "win-x64");
            bool running = _main._sv.IsBatRunning
                && ServerService.IsDfoServerRunning(distDir);
            lbStatus.Text = running ? "● 运行中" : "● 未运行";
            lbStatus.ForeColor = running ? Gn : Rd;

            var pvfOk = _main._sv.PvfExists(
                Path.Combine(_main._ad, "ServerS4A12-AUM"));
            lbPvf.Text = pvfOk ? "PVF: ● 已加载" : "PVF: ● 未找到";
            lbPvf.ForeColor = pvfOk ? Gn : Rd;

            lbInfo.Text = "版本: v" + _main._up.GetVersion(_main._ad);
        }
        catch { }
    }

    /*
     * 刷新存档列表
     */
    void RefreshArchives()
    {
        var dt = new DataTable();
        dt.Columns.Add("Name", typeof(string));
        dt.Columns.Add("Modified", typeof(string));
        foreach (var a in _main._ar.List(_main._ad)
                     .OrderByDescending(x => x.Modified))
        {
            dt.Rows.Add(a.Name, a.Modified.ToString("yyyy-MM-dd HH:mm"));
        }
        lv.DataSource = dt;
    }

    /*
     * 存档列表双击 — 左键双击切换存档
     * 挂在 CellDoubleClick 上 (AntdUI 双击不触发 CellClick)
     */
    void Lv_CellClick(object s, TableClickEventArgs e)
    {
        if (!(e.Record is DataRow row)) return;
        if (e.Button != MouseButtons.Left) return;
        if (!CheckServer()) return;

        var nm = Convert.ToString(row["Name"]);
        if (string.IsNullOrEmpty(nm)) return;

        var path = Path.Combine(_main._ad, "存档管理", "切换库", nm);
        if (Directory.GetFiles(path, "*.db").Length == 0)
        {
            MessageBox.Show("该存档没有 .db 文件", "切换存档",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (_main._ar.IsSimpleArchive(_main._ad, path))
        {
            var r = MessageBox.Show(
                "该存档文件夹内仅有一个.DB的主存档文件，是否执行一次对主目录的冗杂DB清理？",
                "存档切换",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return;
            _main._ar.SwitchToArchive(_main._ad, path,
                cleanRedundantDbFirst: r == DialogResult.Yes);
        }
        else
        {
            _main._ar.SwitchToArchive(_main._ad, path);
        }

        _main.Lg(">>> 极简模式: 已切换到存档 " + nm, Gn);
        RefreshArchives();
    }

    /*
     * 存档操作前置检查 — 服务端运行中阻止
     */
    bool CheckServer()
    {
        if (_main._sv.IsRunning)
        {
            MessageBox.Show(
                "目前服务端正在运行，请结束服务端后再使用存档管理相关功能。",
                "服务端运行中",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }
        return true;
    }

    /*
     * 窗口拖动 (DragWindow) — 无边框窗口需手动实现标题栏拖动
     */
    void DragWindow(object s, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    const uint WM_NCLBUTTONDOWN = 0x00A1;
    const int HTCAPTION = 0x0002;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool ReleaseCapture();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    /*
     * 递归启用双缓冲 (v2.03: 扩展到 AntdUI 自绘容器)
     */
    static void EnableDoubleBuffer(Control root)
    {
        var prop = typeof(Control).GetProperty("DoubleBuffered",
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic);
        foreach (Control c in root.Controls)
        {
            if (c is TableLayoutPanel || c is System.Windows.Forms.Panel || c is FlowLayoutPanel
                || c is AntdUI.Panel || c is AntdUI.In.Panel || c is AntdUI.Table
                || c is AntdUI.Menu || c is AntdUI.PageHeader || c is AntdUI.Progress)
                try { prop?.SetValue(c, true); } catch { }
            EnableDoubleBuffer(c);
        }
    }
}
