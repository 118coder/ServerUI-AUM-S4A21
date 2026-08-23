/*
 * ==================================================================
 * 经典模式窗口 (ClassicForm.cs) — 独立于新界面的旧版一站式布局
 * ==================================================================
 *
 * 【设计说明】
 *   新版本(2.01)采用 左侧菜单 + 多页面 布局; 经典模式还原旧版本
 *   的一站式仪表盘布局:
 *     顶部标题栏 → 左侧 状态/游戏控制/更新管理/快捷工具
 *                → 右侧 存档管理(表格/拖拽)
 *                → 底部 运行日志(深色, 带进度条)
 *   所有服务端/存档/更新操作仍复用主窗口 MainForm 的 internal 方法与
 *   服务实例, 不复制任何业务逻辑; 日志与更新进度通过主窗口的
 *   LogHook / ProgressHook 转发到本窗口实时显示。
 *
 * 【与主窗口的关系】
 *   点击主窗口标题栏【典】进入本模式 (主窗口隐藏), 点【返回新界面】
 *   或关闭本窗口即回到新界面, 两者状态互不干扰。
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
using ServerUI.Services;

namespace ServerUI;

public partial class ClassicForm : AntdUI.Window
{
    readonly MainForm _main;

    // 日志攒批渲染: 入队由 30ms 定时器统一渲染, 跨线程不逐行封送
    readonly System.Collections.Generic.Queue<(string m, Color c)> _logQueue = new();
    Timer _logFlush;

    // 配色 (与主窗口一致)
    static readonly Color Gn   = Color.FromArgb(40, 167, 69);
    static readonly Color Rd   = Color.FromArgb(220, 53, 69);
    static readonly Color Or   = Color.FromArgb(253, 126, 20);
    static readonly Color Cy   = Color.FromArgb(78, 201, 176);
    static readonly Color Gold = Color.FromArgb(218, 165, 32);
    static readonly Color Txt2 = Color.FromArgb(176, 176, 184);
    static readonly Color LogBg = Color.FromArgb(24, 24, 28);

    // UI 控件
    AntdUI.Label lbStatus, lbPvf, lbVe, lbLu, lbCu, lbBk;
    AntdUI.Button btPlay, btStop, btRe, btGm, btPv, btMD, btOD, btOB;
    AntdUI.Button btIn, btVL, btBack, btCp, btCl;
    AntdUI.Button btSC, btIm, btEx, btUd, btRf;
    AntdUI.Switch cbCl;
    AntdUI.Table lv;
    AntdUI.Panel dz;
    AntdUI.Progress pb;
    AntdUI.Label lbPg;
    RichTextBox rt;

    const int LogMaxChars = 200_000;
    const int LogKeepChars = 150_000;

    public ClassicForm(MainForm main)
    {
        _main = main;

        // 窗口属性 — 与旧版一致的宽大仪表盘窗口
        MinimumSize = new Size(1080, 700);
        Size = new Size(1280, 840);
        StartPosition = FormStartPosition.CenterScreen;
        ControlBox = false;
        Text = "ServerS4A12 管理器 v" + MainForm.VER;   // 经典模式
        Font = new Font("Microsoft YaHei UI", 10f);

        // 拖放支持 — 拖 .db 文件到窗口任意位置换挡
        AllowDrop = true;
        DragEnter += OnDragEnter;
        DragDrop += OnFileDrop;

        FormClosing += (s, e) =>
        {
            // 关闭经典窗口(X) = 返回新界面 (隐藏而非真正关闭, 避免递归)
            _main.BackToFullMode();
            e.Cancel = true;
        };

        // 可见性变化时挂接/摘除日志转发钩子
        VisibleChanged += (s, e) => UpdateHooks();

        try { if (_main.Icon != null) Icon = _main.Icon; } catch { }

        // 构建期间挂起布局, 减少重复布局开销; 双缓冲减少重绘闪烁
        SuspendLayout();
        BuildUi();
        ResumeLayout(true);
        EnableDoubleBuffer(this);

        // 日志攒批渲染定时器 (30ms)
        _logFlush = new Timer { Interval = 30 };
        _logFlush.Tick += (s, e) => FlushLog();
        _logFlush.Start();

        RefreshStatus();
        RefreshArchives();
        Lg(">>> 已进入经典模式(旧版一站式布局)。点右上角【返回新界面】可回到新版界面。", Gold);
    }

    /*
     * 由主窗口状态刷新广播调用 (每 2 秒) — 复用主窗口的检测结果,
     * 不再独立轮询进程/文件, 减少磁盘 IO 与进程枚举
     */
    internal void OnMainTick() => RefreshStatus();

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

    /*
     * 钩子挂接 — 显示时转发主窗口日志/进度, 隐藏时摘除
     */
    void UpdateHooks()
    {
        if (Visible)
        {
            _main.LogHook = (m, c) => Lg(m, c);
            _main.ProgressHook = pct => SafeProg(pct);
        }
        else
        {
            _main.LogHook = null;
            _main.ProgressHook = null;
        }
    }

    /*
     * 进度更新 (跨线程安全)
     */
    void SafeProg(int pct)
    {
        try
        {
            if (rt.InvokeRequired)
            {
                rt.Invoke(new Action(() => SetProg(pct)));
                return;
            }
            SetProg(pct);
        }
        catch { }
    }

    void SetProg(int pct)
    {
        if (pb == null || lbPg == null) return;
        pb.Value = Math.Min(Math.Max(pct, 0), 100) / 100f;
        lbPg.Text = pct >= 100 ? "更新进度: 100%" : "更新进度: " + pct + "%";
    }

    // =================================================================
    // UI 构建
    // =================================================================
    void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1, RowCount = 3,
            BackColor = Color.Transparent,
            Padding = new Padding(12)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));    // 标题栏
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));   // 主区域
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 192F));  // 日志区
        Controls.Add(root);

        // ===== 标题栏 (可拖动) =====
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
            Text = "经典模式 · ServerS4A12 管理器 v" + MainForm.VER,
            Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Cursor = Cursors.Hand
        };
        title.MouseDown += DragWindow;
        bar.MouseDown += DragWindow;
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
            _main.OnThemeChanged();
        };
        btBack = new AntdUI.Button
        {
            Text = "返回新界面",
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

        // ===== 主区域: 左列(服务控制) + 右列(存档管理) =====
        var mid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2, RowCount = 1,
            BackColor = Color.Transparent
        };
        mid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 470F));
        mid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        mid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        mid.Controls.Add(BuildLeft(), 0, 0);
        mid.Controls.Add(BuildRight(), 1, 0);
        root.Controls.Add(mid, 0, 1);

        BuildLog();
        root.Controls.Add(_logPanel, 0, 2);
    }

    // ---------- 左列: 状态 / 开始游戏 / 服务控制 / 快速操作 / 更新管理 ----------
    TableLayoutPanel BuildLeft()
    {
        var left = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1, RowCount = 5,
            BackColor = Color.Transparent
        };
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 96F));   // 状态卡
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 68F));   // 开始游戏
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 104F));  // 服务控制
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 104F));  // 快速操作
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));   // 更新管理

        // ---- 状态卡: 左列(服务端/PVF) 右列(版本/上次更新), 上下对称对齐 ----
        var stCard = Card(8, 2);
        stCard.Padding = new Padding(18, 8, 18, 8);
        var stG = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2, RowCount = 2,
            BackColor = Color.Transparent
        };
        stG.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55F));
        stG.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45F));
        stG.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
        stG.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
        lbStatus = new AntdUI.Label
        {
            Text = "● 服务端未运行",
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold),
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
        lbVe = new AntdUI.Label
        {
            Text = "版本: --",
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold),
            ForeColor = Txt2,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight
        };
        lbLu = new AntdUI.Label
        {
            Text = "上次更新: 尚未识别",
            Font = new Font("Microsoft YaHei UI", 9f),
            ForeColor = Txt2,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight   // 文字贴到容器右边缘
        };
        stG.Controls.Add(lbStatus, 0, 0);
        stG.Controls.Add(lbVe, 1, 0);
        stG.Controls.Add(lbPvf, 0, 1);
        stG.Controls.Add(lbLu, 1, 1);
        stCard.Controls.Add(stG);
        left.Controls.Add(stCard, 0, 0);

        // ---- 开始游戏 (绿色大按钮) ----
        btPlay = B("开始游戏", TTypeMini.Success, "PlayCircleOutlined", 14, true);
        btPlay.Margin = new Padding(6, 6, 6, 6);
        btPlay.Click += async (s, e) =>
        {
            Lg(">>> 点击了开始游戏", Color.CornflowerBlue);
            await _main.Play();
        };
        left.Controls.Add(btPlay, 0, 1);

        // ---- 服务控制卡: 停止(红) / 重启(橙) ----
        var ctrlCard = Card(8, 2);
        ctrlCard.Padding = new Padding(14, 12, 14, 12);
        var ctrlG = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2, RowCount = 1,
            BackColor = Color.Transparent
        };
        ctrlG.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        ctrlG.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        ctrlG.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        btStop = B("停止服务端", TTypeMini.Error, "StopOutlined", 11, true);
        btStop.Click += (s, e) =>
        {
            Lg(">>> 点击了停止服务端", Color.Gold);
            System.Threading.Tasks.Task.Run(() =>
            {
                _main._sv.Stop();
                Invoke(new Action(() => Lg(">>> 已终止服务端进程树", Color.Gold)));
            });
        };
        btRe = B("重启服务端", TTypeMini.Warn, "ReloadOutlined", 11, true);
        btRe.Click += async (s, e) =>
        {
            Lg(">>> 点击了重启服务端", Color.CornflowerBlue);
            await System.Threading.Tasks.Task.Run(() => _main._sv.Stop());
            await System.Threading.Tasks.Task.Delay(1200);
            _main._sv.Start(Path.Combine(_main._ad, "ServerS4A12-AUM"));
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(10000);
                try { _main._sv.HideConsoleWindow(); } catch { }
                try { ServerService.HideDfoServerWindow(); } catch { }
            });
            Lg(">>> 服务端已重新启动", Gn);
        };
        ctrlG.Controls.Add(btStop, 0, 0);
        ctrlG.Controls.Add(btRe, 1, 0);
        ctrlCard.Controls.Add(ctrlG);
        left.Controls.Add(ctrlCard, 0, 2);

        // ---- 快速操作卡: GM工具(橙) / 打开PVF目录(红) — 与完整版一致 ----
        var toolCard = Card(8, 2);
        toolCard.Padding = new Padding(14, 12, 14, 12);
        var toolG = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2, RowCount = 1,
            BackColor = Color.Transparent
        };
        toolG.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        toolG.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        toolG.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        btGm = B("GM工具", TTypeMini.Warn, "ToolOutlined", 11, true);
        btGm.Click += (s, e) => _main.LaunchGmTool();
        btPv = B("打开PVF目录", TTypeMini.Error, "FolderOpenOutlined", 10, true);
        btPv.Click += (s, e) => OpenDir(Path.Combine(_main._ad, "ServerS4A12-AUM", "dist", "win-x64", "Data", "Pvf"), "PVF目录");
        toolG.Controls.Add(btGm, 0, 0);
        toolG.Controls.Add(btPv, 1, 0);
        toolCard.Controls.Add(toolG);
        left.Controls.Add(toolCard, 0, 3);

        // ---- 更新管理卡: 开始更新 / 查看更新日志 (v2.03: 移除 更新AUM/安装SDK) ----
        var updCard = Card(8, 2);
        updCard.Padding = new Padding(14, 12, 14, 14);
        var updG = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1, RowCount = 3,
            BackColor = Color.Transparent
        };
        updG.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        updG.RowStyles.Add(new RowStyle(SizeType.Absolute, 56F));   // 开始更新
        updG.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));   // 查看更新日志
        updG.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));   // 占位填充行

        btIn = B("开始更新", TTypeMini.Primary, "ThunderboltOutlined", 12, true);
        btIn.Click += async (s, e) =>
        {
            Lg(">>> 点击了开始更新", Color.CornflowerBlue);
            await _main.RI();
        };
        btVL = B("查看更新日志", TTypeMini.Default, "FileTextOutlined", 9, false);
        btVL.Click += (s, e) =>
        {
            Lg(">>> 查看更新日志", Color.CornflowerBlue);
            _main.SL();
        };
        updG.Controls.Add(btIn, 0, 0);
        updG.Controls.Add(btVL, 0, 1);
        updCard.Controls.Add(updG);
        left.Controls.Add(updCard, 0, 4);

        return left;
    }

    void OpenDir(string dir, string tag)
    {
        try { Directory.CreateDirectory(dir); } catch { }
        if (Directory.Exists(dir))
        {
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = dir, UseShellExecute = true });
            Lg(">>> 打开了" + tag, Color.CornflowerBlue);
        }
        else Lg(tag + "不存在", Color.Gold);
    }

    // ---------- 右列: 存档管理 (完整版风格: 整个页面套一个大卡片框) ----------
    AntdUI.Panel BuildRight()
    {
        // 外层大卡片框 — 参考完整版: 存档管理页 pgArc 整体作为一帧, 内容内缩 10px
        var archCard = Card(8, 2);
        archCard.Padding = new Padding(10);
        archCard.Margin = new Padding(2, 2, 2, 2);

        var ag = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1, RowCount = 4,
            BackColor = Color.Transparent
        };
        ag.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));   // 信息栏
        ag.RowStyles.Add(new RowStyle(SizeType.Absolute, 98F));   // 操作网格 (2 行)
        ag.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));   // 存档表格
        ag.RowStyles.Add(new RowStyle(SizeType.Absolute, 54F));   // 拖拽区

        // ---- 信息栏: 当前 / 备份数 / 清理冗余DB / 刷新存档 ----
        var ib = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5, RowCount = 1,
            BackColor = Color.Transparent
        };
        ib.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28F));
        ib.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 16F));
        ib.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        ib.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150F));
        ib.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116F));
        ib.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        lbCu = new AntdUI.Label
        {
            Text = "当前: --",
            Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        lbBk = new AntdUI.Label
        {
            Text = "备份数: 0",
            Font = new Font("Microsoft YaHei UI", 9f),
            ForeColor = Txt2,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        cbCl = new AntdUI.Switch { Checked = false, Cursor = Cursors.Hand };
        cbCl.CheckedChanged += (s, e) =>
        {
            Lg(">>> [清理冗余DB] " + (cbCl.Checked ? "已启用" : "已关闭"), cbCl.Checked ? Gn : Txt2);
            if (cbCl.Checked) _main.CleanRedundantDb();
        };
        btRf = AB("刷新存档", TTypeMini.Default, "ReloadOutlined", 8.5f, false, 26, 88);
        btRf.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;   // 与清理冗余DB 底对齐
        btRf.Click += (s, e) =>
        {
            Lg(">>> 刷新存档列表", Color.CornflowerBlue);
            RefreshArchives();
        };
        var swRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 14, 0, 0)   // 开关顶部 y=14 (原17再上移3px)
        };
        cbCl.Size = new Size(40, 22);
        swRow.Controls.Add(cbCl);
        var swLb = new AntdUI.Label
        {
            Text = "清理冗余DB",
            Font = new Font("Microsoft YaHei UI", 9f),
            ForeColor = Txt2,
            AutoSize = false,
            Size = new Size(96, 26),
            Margin = new Padding(6, 0, 0, 0)
        };
        swRow.Controls.Add(swLb);
        ib.Controls.Add(lbCu, 0, 0);
        ib.Controls.Add(lbBk, 1, 0);
        ib.Controls.Add(swRow, 3, 0);
        ib.Controls.Add(btRf, 4, 0);
        ag.Controls.Add(ib, 0, 0);

        // ---- 操作网格: 第 1 行 目录访问 / 第 2 行 常用操作 ----
        var opG = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1, RowCount = 2,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 2, 0, 2)
        };
        opG.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
        opG.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));

        // 目录访问行: 打开切换库 / 打开备份库 / 打开主存档
        var dirRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3, RowCount = 1,
            BackColor = Color.Transparent
        };
        for (int i = 0; i < 3; i++)
            dirRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / 3F));
        dirRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        btOD = B("打开切换库", TTypeMini.Default, "FolderOpenOutlined", 9.5f, false);
        btOD.Click += (s, e) => OpenDir(Path.Combine(_main._ad, "存档管理", "切换库"), "切换存档目录");
        btOB = B("打开备份库", TTypeMini.Default, "FolderOpenOutlined", 9.5f, false);
        btOB.Click += (s, e) => OpenDir(Path.Combine(_main._ad, "存档管理", "备份存档"), "备份存档目录");
        btMD = B("打开主存档", TTypeMini.Default, "DatabaseOutlined", 9.5f, false);
        btMD.Click += (s, e) => OpenDir(Path.Combine(_main._ad, "ServerS4A12-AUM", "dist", "win-x64", "Data"), "主存档目录");
        dirRow.Controls.Add(btOD, 0, 0);
        dirRow.Controls.Add(btOB, 1, 0);
        dirRow.Controls.Add(btMD, 2, 0);
        opG.Controls.Add(dirRow, 0, 0);

        // 常用操作行: 储存当前 / 导入存档 / 导出当前 / 撤销换挡
        var actRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4, RowCount = 1,
            BackColor = Color.Transparent
        };
        for (int i = 0; i < 4; i++)
            actRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
        actRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        btSC = B("储存当前", TTypeMini.Primary, "SaveOutlined", 9.5f, true);
        btSC.Click += (s, e) =>
        {
            Lg(">>> 点击了储存当前存档", Color.CornflowerBlue);
            _main.DoArchiveOp(() => { _main.SC(); RefreshArchives(); return true; });
        };
        btIm = B("导入存档", TTypeMini.Default, "UploadOutlined", 9.5f, false);
        btIm.Click += (s, e) =>
        {
            Lg(">>> 点击了导入存档", Color.CornflowerBlue);
            _main.DoArchiveOp(() => { _main.IA(); RefreshArchives(); return true; });
        };
        btEx = B("导出当前", TTypeMini.Default, "DownloadOutlined", 9.5f, false);
        btEx.Click += (s, e) =>
        {
            Lg(">>> 点击了导出当前", Color.CornflowerBlue);
            _main.DoArchiveOp(() => { _main.EC(); return true; });
        };
        btUd = B("撤销换挡", TTypeMini.Default, "UndoOutlined", 9.5f, false);
        btUd.Click += (s, e) =>
        {
            Lg(">>> 点击了撤销换挡", Color.CornflowerBlue);
            _main.DoArchiveOp(() =>
            {
                if (!_main._ar.UndoSwap(_main._ad))
                {
                    Lg("无备份", Color.Gold);
                    return false;
                }
                Lg(">>> 已撤销换挡", Gn);
                RefreshArchives();
                if (cbCl.Checked) _main.CleanRedundantDb();
                return true;
            });
        };
        actRow.Controls.Add(btSC, 0, 0);
        actRow.Controls.Add(btIm, 1, 0);
        actRow.Controls.Add(btEx, 2, 0);
        actRow.Controls.Add(btUd, 3, 0);
        opG.Controls.Add(actRow, 0, 1);
        ag.Controls.Add(opG, 0, 1);

        // ---- 存档表格 (双击切换 · 右键菜单) ----
        lv = new AntdUI.Table
        {
            Dock = DockStyle.Fill,
            FixedHeader = true,
            AutoSizeColumnsMode = AntdUI.ColumnsMode.Fill
        };
        lv.Columns = new ColumnCollection
        {
            new Column("Name", "存档名称 (双击切换 · 右键更多)") { Ellipsis = true },
            new Column("Size", "大小") { Width = "90", Align = ColumnAlign.Right },
            new Column("Modified", "修改时间") { Width = "150", Align = ColumnAlign.Center },
        };
        // ★ AntdUI 双击只触发 CellDoubleClick, 不会触发 CellClick
        lv.CellDoubleClick += Lv_CellDoubleClick;
        lv.CellClickBegin += Lv_CellClickBegin;
        ag.Controls.Add(lv, 0, 2);

        // ---- 拖拽区 ----
        dz = new AntdUI.Panel
        {
            Dock = DockStyle.Fill,
            Radius = 8,
            BorderWidth = 1.2F,
            BorderColor = CardBorder,
            Margin = new Padding(0, 4, 0, 0),
            Cursor = Cursors.Hand
        };
        dz.DoubleClick += (s, e) =>
        {
            Lg(">>> 双击拖拽区，储存当前存档", Color.CornflowerBlue);
            _main.DoArchiveOp(() => { _main.SC(); RefreshArchives(); return true; });
        };
        dz.DragEnter += OnDragEnter;
        dz.DragDrop += OnFileDrop;
        dz.AllowDrop = true;
        var lbDr = new AntdUI.Label
        {
            Text = "📁 拖拽 .db 文件到此处 = 快速替换存档 (双击 = 储存当前)",
            Font = new Font("Microsoft YaHei UI", 9f),
            ForeColor = Txt2,
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Fill
        };
        dz.Controls.Add(lbDr);
        ag.Controls.Add(dz, 0, 3);

        archCard.Controls.Add(ag);
        return archCard;
    }

    // ---------- 底部日志区 ----------
    AntdUI.Panel _logPanel;
    void BuildLog()
    {
        var lp = new AntdUI.Panel
        {
            Dock = DockStyle.Fill,
            Radius = 8,
            Shadow = 2,
            BorderWidth = 1.2F,
            BorderColor = CardBorder,
            Margin = new Padding(0, 8, 0, 0)
        };
        lp.Padding = new Padding(0, 0, 0, 0);

        // 工具条: 进度 + 复制/清空
        var lbar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 40,
            ColumnCount = 4, RowCount = 1,
            BackColor = Color.Transparent,
            Padding = new Padding(12, 6, 12, 0)
        };
        lbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90F));   // 标题
        lbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));   // 进度
        lbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110F));  // 复制
        lbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110F));  // 清空
        lbar.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        var lbTitle = new AntdUI.Label
        {
            Text = "运行日志",
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        lbPg = new AntdUI.Label
        {
            Text = "",
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei UI", 8.5f),
            ForeColor = Txt2,
            TextAlign = ContentAlignment.MiddleLeft
        };
        pb = new AntdUI.Progress
        {
            Dock = DockStyle.Fill,
            Value = 0F,
            Visible = false
        };
        btCp = B("复制日志", TTypeMini.Default, "CopyOutlined", 9, false);
        btCp.Click += (s, e) =>
        {
            try
            {
                Clipboard.SetText(rt.Text);
                Lg(">>> 日志已复制到剪贴板", Gn);
            }
            catch { }
        };
        btCl = B("清空日志", TTypeMini.Default, "ClearOutlined", 9, false);
        btCl.Click += (s, e) => { rt.Clear(); };
        var progWrap = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1, RowCount = 2,
            BackColor = Color.Transparent
        };
        progWrap.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
        progWrap.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
        progWrap.Controls.Add(pb, 0, 0);
        progWrap.Controls.Add(lbPg, 0, 1);
        lbar.Controls.Add(lbTitle, 0, 0);
        lbar.Controls.Add(progWrap, 1, 0);
        lbar.Controls.Add(btCp, 2, 0);
        lbar.Controls.Add(btCl, 3, 0);
        lp.Controls.Add(lbar);

        rt = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = LogBg,
            ForeColor = Color.FromArgb(240, 240, 245),
            BorderStyle = BorderStyle.None,
            Font = new Font("Consolas", 9f),
            DetectUrls = false,
            Margin = new Padding(12, 0, 12, 0)
        };
        lp.Controls.Add(rt);
        _logPanel = lp;
    }

    // =================================================================
    // 日志输出 (攒批渲染, 线程安全)
    // =================================================================
    void Lg(string m) => Lg(m, Color.FromArgb(240, 240, 245));
    void Lg(string m, Color c)
    {
        if (rt == null || rt.IsDisposed) return;
        lock (_logQueue) _logQueue.Enqueue((m, c));
    }

    /*
     * 日志批量渲染 (FlushLog) — 由 30ms 定时器在 UI 线程执行
     * 仅底部时自动跟随; 超限按字符裁剪 (保留最近部分, 不丢格式)
     * v2.03: 单批最多 200 行, 超量自动分片到下一次
     */
    const int LogFlushMaxLines = 200;

    void FlushLog()
    {
        if (rt == null || rt.IsDisposed) return;
        List<(string m, Color c)> batch;
        lock (_logQueue)
        {
            if (_logQueue.Count == 0) return;
            var n = Math.Min(LogFlushMaxLines, _logQueue.Count);
            batch = new List<(string, Color)>(n);
            for (int i = 0; i < n; i++) batch.Add(_logQueue.Dequeue());
        }

        bool follow = IsLogAtBottom();
        try
        {
            foreach (var (m, c) in batch)
            {
                var ts = "[" + DateTime.Now.ToString("HH:mm:ss") + "] ";
                rt.SelectionStart = rt.TextLength;
                rt.SelectionLength = 0;
                rt.SelectionColor = Color.FromArgb(240, 240, 245);
                rt.AppendText(ts);
                rt.SelectionColor = c;
                rt.AppendText(m + "\n");
            }
            if (rt.TextLength > LogMaxChars)
            {
                var cut = rt.TextLength - LogKeepChars;
                if (cut > 0)
                {
                    rt.ReadOnly = false;
                    rt.Select(0, cut);
                    rt.SelectedText = "";
                    rt.ReadOnly = true;
                }
            }
            if (follow) rt.ScrollToCaret();
        }
        catch { }
    }

    /*
     * 判断日志滚动条是否位于底部 — 用户上翻查看历史时不抢滚
     */
    bool IsLogAtBottom()
    {
        if (rt == null || rt.TextLength == 0) return true;
        try
        {
            var pos = rt.GetPositionFromCharIndex(rt.TextLength - 1);
            return pos.Y >= 0 && pos.Y < rt.ClientSize.Height - 8;
        }
        catch { return true; }
    }

    // =================================================================
    // 状态刷新 (每 2 秒)
    // =================================================================
    void RefreshStatus()
    {
        try
        {
            var distDir = Path.Combine(_main._ad, "ServerS4A12-AUM", "dist", "win-x64");
            bool running = _main._sv.IsBatRunning && ServerService.IsDfoServerRunning(distDir);
            lbStatus.Text = running ? "● 服务端运行中" : "● 服务端未运行";
            lbStatus.ForeColor = running ? Gn : Rd;

            var pvfOk = _main._sv.PvfExists(Path.Combine(_main._ad, "ServerS4A12-AUM"));
            lbPvf.Text = pvfOk ? "PVF: ● 已加载" : "PVF: ● 未找到";
            lbPvf.ForeColor = pvfOk ? Gn : Rd;

            lbVe.Text = "版本: v" + _main._up.GetVersion(_main._ad);

            // 走缓存读取 (主窗口与经典窗口共用, 避免重复 IO)
            var tx = _main._up.GetLogText(_main._ad);
            if (tx.Length > 0)
            {
                var ix = tx.LastIndexOf("版本:");
                if (ix >= 0)
                {
                    var en = tx.IndexOf('\n', ix);
                    if (en < 0) en = Math.Min(ix + 20, tx.Length);
                    lbLu.Text = "上次更新: " + tx.Substring(ix, en - ix).Trim().Replace("版本:", "").Trim();
                }
                else lbLu.Text = "上次更新: 尚未识别";
            }
            else lbLu.Text = "上次更新: 尚未更新";
        }
        catch { }
    }

    // =================================================================
    // 存档列表
    // =================================================================
    void RefreshArchives()
    {
        try
        {
            var dt = new DataTable();
            dt.Columns.Add("Name", typeof(string));
            dt.Columns.Add("Size", typeof(string));
            dt.Columns.Add("Modified", typeof(string));
            foreach (var a in _main._ar.List(_main._ad).OrderByDescending(x => x.Modified))
                dt.Rows.Add(a.Name, a.SizeDisplay, a.Modified.ToString("yyyy-MM-dd HH:mm"));
            lv.DataSource = dt;

            lbCu.Text = "当前: " + _main._ar.CurrentInfo(_main._ad);
            lbBk.Text = "备份数: " + _main._ar.BackupCount(_main._ad);
        }
        catch { }
    }

    // ---------- 存档表格: 双击切换 ----------
    void Lv_CellDoubleClick(object s, TableClickEventArgs e)
    {
        if (!(e.Record is DataRow row)) return;
        if (e.Button != MouseButtons.Left) return;
        var nm = Convert.ToString(row["Name"]);
        if (string.IsNullOrEmpty(nm)) return;
        SwitchToArchive(nm);
    }

    // ---------- 切换存档 (双击 / 右键菜单共用) ----------
    void SwitchToArchive(string nm)
    {
        var path = Path.Combine(_main._ad, "存档管理", "切换库", nm);
        _main.DoArchiveOp(() =>
        {
            if (Directory.GetFiles(path, "*.db").Length == 0)
            {
                Lg(">>> [切换存档] 没有存档", Or);
                MessageBox.Show("没有存档", "切换存档",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            if (_main._ar.IsSimpleArchive(_main._ad, path))
            {
                var r = MessageBox.Show(
                    "该存档文件夹内仅有一个.DB的主存档文件，是否执行一次对主目录的冗杂DB清理？",
                    "存档切换",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Question);
                if (r == DialogResult.Cancel) return false;
                _main._ar.SwitchToArchive(_main._ad, path, cleanRedundantDbFirst: r == DialogResult.Yes);
            }
            else
            {
                _main._ar.SwitchToArchive(_main._ad, path);
            }
            Lg(">>> 已切换到: " + nm, Gn);
            RefreshArchives();
            _main.TB();
            if (cbCl.Checked) _main.CleanRedundantDb();
            return true;
        });
    }

    // ---------- 存档表格: 右键菜单 (切换 / 重命名) ----------
    void Lv_CellClickBegin(object s, TableClickBeginEventArgs e)
    {
        if (e.RowType == RowType.Column) return;
        if (e.Button != MouseButtons.Right || !(e.Record is DataRow row)) return;

        var nm = Convert.ToString(row["Name"]);
        if (string.IsNullOrEmpty(nm)) return;
        e.Handled = true;

        var path = Path.Combine(_main._ad, "存档管理", "切换库", nm);
        var menulist = new AntdUI.IContextMenuStripItem[]
        {
            new AntdUI.ContextMenuStripItem("切换存档") { IconSvg = "SwapOutlined", Tag = "switch" },
            new AntdUI.ContextMenuStripItem("重命名存档") { IconSvg = "EditOutlined", Tag = "rename" }
        };
        AntdUI.ContextMenuStrip.open(lv, item =>
        {
            var act = (string)item.Tag;
            if (act == "rename")
            {
                var nn = Interaction.InputBox("修改存档名称:", "重命名", nm);
                if (!string.IsNullOrWhiteSpace(nn) && nn != nm)
                {
                    var np = Path.Combine(_main._ad, "存档管理", "切换库", nn);
                    if (Directory.Exists(path) && !Directory.Exists(np))
                    {
                        Directory.Move(path, np);
                        Lg(">>> 已重命名: " + nm + " -> " + nn, Gn);
                        RefreshArchives();
                    }
                    else if (Directory.Exists(np))
                        Lg("名称已存在", Color.Gold);
                    else
                        Lg("重命名失败", Color.Gold);
                }
            }
            else if (act == "switch")
            {
                SwitchToArchive(nm);
            }
        }, menulist);
    }

    // =================================================================
    // 拖拽换挡
    // =================================================================
    void OnDragEnter(object s, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var fs = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (fs.Length == 1 && fs[0].EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                e.Effect = DragDropEffects.Copy;
        }
    }

    void OnFileDrop(object s, DragEventArgs e)
    {
        var fs = (string[])e.Data.GetData(DataFormats.FileDrop);
        Lg(">>> 拖拽换挡: " + Path.GetFileName(fs[0]), Color.CornflowerBlue);
        _main.DoArchiveOp(() =>
        {
            var p = fs[0];
            if (Directory.Exists(p))
                _main._ar.SwitchToArchive(_main._ad, p);
            else if (File.Exists(p))
                _main._ar.Swap(_main._ad, p);
            else
            {
                Lg(">>> 存档路径无效: " + p, Rd);
                return false;
            }
            Lg(">>> 拖拽换挡完成", Gn);
            RefreshArchives();
            _main.TB();
            if (cbCl.Checked) _main.CleanRedundantDb();
            return true;
        });
    }

    // =================================================================
    // 控件工厂
    // =================================================================
    // 卡片统一显式描边 (同 Default 按钮的可见灰), 明暗主题下均清晰可辨
    static readonly Color CardBorder = Color.FromArgb(100, 102, 110);

    AntdUI.Panel Card(int radius = 8, int shadow = 1)
    {
        return new AntdUI.Panel
        {
            Dock = DockStyle.Fill,
            Radius = radius,
            Shadow = shadow,
            BorderWidth = 1.2F,
            BorderColor = CardBorder,
            Margin = new Padding(2)
        };
    }

    /*
     * 完整版风格按钮工厂 (B) — Dock 填充式, 用于网格布局
     */
    AntdUI.Button B(string t, TTypeMini ty = TTypeMini.Default, string svg = null, float fs = 10, bool bold = false)
    {
        var b = new AntdUI.Button
        {
            Text = t,
            Type = ty,
            Dock = DockStyle.Fill,
            Margin = new Padding(6),
            Cursor = Cursors.Hand,
            WaveSize = 0        // v2.03: 关闭水波动画, 减少重绘开销
        };
        if (!string.IsNullOrEmpty(svg)) b.IconSvg = svg;
        if (ty == TTypeMini.Default)
        {
            b.BorderWidth = 1.5F;
            b.DefaultBorderColor = Color.FromArgb(100, 102, 110);
        }
        else b.BorderWidth = 1F;
        return b;
    }

    /*
     * 流式按钮工厂 (AB) — 按文字测量宽度 + 图标留白, 用于信息栏流式排布
     * w > 0 时使用指定宽度 (紧凑按钮)
     */
    AntdUI.Button AB(string t, TTypeMini ty = TTypeMini.Default, string svg = null, float fs = 9, bool bold = false, int h = 34, int w = 0)
    {
        var font = new Font("Microsoft YaHei UI", fs, bold ? FontStyle.Bold : FontStyle.Regular);
        var b = new AntdUI.Button
        {
            Text = t,
            Type = ty,
            Radius = 6,
            WaveSize = 0,
            Font = font,
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 10, 0)
        };
        if (!string.IsNullOrEmpty(svg)) b.IconSvg = svg;
        if (w > 0) b.Size = new Size(w, h);
        else
        {
            var sz = TextRenderer.MeasureText(t, font);
            b.Size = new Size(sz.Width + (string.IsNullOrEmpty(svg) ? 48 : 66), h);
        }
        return b;
    }

    /*
     * 窗口拖动 — 无边框窗口需手动实现标题栏拖动
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
}
