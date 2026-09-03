/*
 * ==================================================================
* 主窗口类 — 核心框架 (MainForm.cs) — ServerS4A21 GUI 管理器
 * ==================================================================
 *
 * 【v2.0 UI 重设计说明】
 *   本版本基于 Ant Design 设计语言 (Apache-2.0) 的开源 WinForms 控件库
 *   AntdUI v2.4.3 (https://github.com/AntdUI/AntdUI) 进行现代化改造:
 *
 *   - 无边框窗口 + 自定义标题栏 (PageHeader)：原生最小化/最大化/关闭
 *   - Ant Design 经典 Dashboard 布局：左侧导航菜单 + 卡片式内容区
 *   - 明暗双主题一键切换（跟随 AntdUI 主题系统）
 *   - 底部常驻"运行日志"条，可一键折叠/展开，更新进度条实时可见
 *
 * 【代码组织 (partial class 拆分, 各文件独立维护)】
 *   MainForm.cs             — 字段声明 / 构造函数 / 控件工厂 / Build()
 *                              / ShowPage() / 窗口拉伸过渡动画
 *   MainForm.Pages.cs       — 页面构建: 开始 / 存档管理 / DLL扩展 / 调整系统时间
 *                              / 设置与关于 / 使用说明 / 日志条 (页面 UI 改动只改这里)
 *   MainForm.Archive.cs     — 存档管理全部逻辑 (导入导出/切换/重命名/
 *                              清理冗余DB/拖拽换挡/刷新)
 *   MainForm.Server.cs      — 服务端控制 / 日志 / 系统检测 / DX补丁 /
 *                              SDK安装 / 网络与AUM自更新 / 更新编排
 *
 * 拆分目的: 避免牵一发而动全身, 修改单个页面或功能不影响其他部分。
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
    // =================================================================
    // 配色 — 仅保留主题色之外的专用状态色，其余颜色全部交给主题系统
    // =================================================================
    static readonly Color Gn   = Color.FromArgb(40, 167, 69);   // 成功绿
    static readonly Color Rd   = Color.FromArgb(220, 53, 69);   // 危险红
    static readonly Color Or   = Color.FromArgb(253, 126, 20);  // 警告橙
    static readonly Color Cy   = Color.FromArgb(78, 201, 176);  // 青色
    static readonly Color Ac   = Color.FromArgb(30, 144, 255);  // 科技蓝
    static readonly Color Gold = Color.FromArgb(218, 165, 32);  // 金色
    static readonly Color Txt2 = Color.FromArgb(176, 176, 184); // 次要文字(日志区固定底色)
    static readonly Color LogBg = Color.FromArgb(24, 24, 28);   // 日志区深色底

    // MB = 备份存档保留的最大数量 (当前 10 个)
    const int MB = 10;

    // VER = 当前工具版本号 — 显示在窗口标题和启动日志中
#if NET48
    internal const string VER = "2.13-V";   // Win7 兼容模式
#else
    internal const string VER = "2.13";
#endif

    // ===== 路径计算 =====
    readonly string _bd = AppDomain.CurrentDomain.BaseDirectory;
    internal readonly string _ad;
    internal readonly string _gr;

    // ===== 服务实例 (Single Responsibility) =====
    internal readonly ServerService _sv = new();  // 服务端进程管理
    internal readonly ArchiveService _ar = new();  // 存档文件管理
    internal readonly UpdateService _up = new();  // 更新脚本调用
    readonly SelfUpdateService  _au = new();  // AUM管理器自更新
    readonly MirrorUploadService _mu = new(); // 镜像上传服务

    // ===== UI 控件字段 =====
    // 根布局
    TableLayoutPanel _root;
    // 标题栏
    AntdUI.PageHeader hd;
    AntdUI.Label lbStHd;              // 标题栏右侧状态
    AntdUI.Button btTheme;            // 明暗主题切换
    AntdUI.Button btSize;             // 窗口尺寸切换 (标准/小一半)
    // 导航
    AntdUI.Menu mn;
    // 页面容器
    AntdUI.In.Panel pgDash;           // P1 开始
    AntdUI.Panel pgArc;               // P2 存档管理
    AntdUI.In.Panel pgUpd;            // P3 设置与关于
    AntdUI.In.Panel pgHelp;           // P4 使用说明
    // 概览页 — 状态卡片
    AntdUI.Label lbSt, lbPv, lbVe, lbSd;
    AntdUI.Label lbLu;                // 上次更新(概览)
    // 概览页 — 快捷控制
    AntdUI.Button btPlay, btStop, btRe, btPv, btPvR, btGm, btMD;
    // 概览页 — 快速更新
    AntdUI.Button btIn, btFu, btVL;
    // DX 选项
    AntdUI.Segmented sgDx;
    AntdUI.Switch cbDw;               // 去水印
    // 存档页
    AntdUI.Label lbCu, lbBk;
    AntdUI.Button btSC, btIm, btEx, btUd, btRf;
    AntdUI.Button btOD, btOB;
    AntdUI.Table lv;                  // 存档表格
    AntdUI.Switch cbCl;               // 清理冗余DB
    AntdUI.Panel dz;                  // 拖拽区
    AntdUI.Label lbDr;
    // 更新页
    AntdUI.Button btAu, btSdk;
    // 使用说明页
    AntdUI.Label lbHelpTitle, lbHelpBody;
    FlowLayoutPanel _helpFlow;        // 说明页左侧内容 (不透明背景, 主题切换时同步)
    AntdUI.Switch cbSkipLog, cbMirror;
    // 日志条
    RichTextBox rt;
    AntdUI.Progress pb;
    AntdUI.Label lbPg;
    AntdUI.Button btCp, btCl, btFold;

    // ===== 定时器 =====
    Timer _st, _pt, _ct;

    // ===== 状态变量 =====
    int _stepTarget;                      // 当前步骤的目标百分比
    float _pv;                            // 进度条当前值 (0-100, float 支持平滑蠕动)
    bool _sa = true;                      // 排序方向: true=正序, false=倒序
    bool _orphanLogged;                   // 孤儿进程告警只触发一次
    bool _hasSdk;                         // .NET 10 SDK 是否可用
    bool _mirrorOk;                       // 镜像上传令牌是否有效
    bool _logCollapsed;                   // 日志条是否折叠
    bool _dxReady;                        // DX 分段选择器是否已完成初始化
    MiniForm _miniForm;                   // 极简模式窗口 (独立小窗口)
    ClassicForm _classicForm;             // 经典模式窗口 (旧版一站式布局)
    float _fadeV = 1f;                    // 窗口拉伸过渡动画进度 (0-1)
    Timer _fadeTimer;                     // 过渡动画定时器
    AntdUI.Panel _contentCard;            // 内容卡片 (过渡动画作用对象)
    readonly StringBuilder _logBuilder = new();  // 累积全部运行日志

    // ===== 日志攒批渲染 (性能优化) =====
    // Lg() 只做线程安全的入队, 由 UI 线程的 FlushLog 定时器统一渲染:
    //   1. 跨线程调用不再逐行 Invoke 封送 (更新/镜像大量输出时不再卡顿)
    //   2. 同一批次内多次 AppendText 合并为一次绘制
    //   3. 仅在日志滚动条位于底部时才自动跟随 (用户上翻查看时不抢滚)
    readonly System.Collections.Generic.Queue<(string m, Color c, bool bold)> _logQueue = new();
    Timer _logFlush;                      // 日志刷新定时器 (30ms)

    // ===== 经典模式转发钩子 (由 ClassicForm 挂接, 不影响主窗口自身) =====
    internal Action<string, Color> LogHook;     // 日志实时转发到经典模式窗口
    internal Action<int> ProgressHook;          // 更新进度转发到经典模式窗口

    /*
     * 构造函数 — 程序启动时执行一次，完成所有初始化工作
     */
    public MainForm()
    {
        // 如果 EXE 在 AUM管理组件 的上级目录 (如 开始游戏-ServerUI.exe)，
        // 则 _ad = EXE目录\AUM管理组件，否则 _ad = EXE 所在目录
        _ad = Directory.Exists(Path.Combine(_bd, "AUM管理组件"))
            ? Path.Combine(_bd, "AUM管理组件")
            : _bd;
        _gr = Directory.GetParent(_ad)?.FullName ?? _ad;

        LoadWindowIcon();

        // 默认使用深色主题 (Dark Mode)
        Config.IsLight = false;

        // 窗口基本属性 (AntdUI.Window = 无边框但有原生特性的窗口)
        MinimumSize = new Size(1080, 700);
        Size = new Size(1280, 840);           // 默认启动大小
        StartPosition = FormStartPosition.CenterScreen;
        ControlBox = false;
        Text = "ServerS4A21 管理器 v" + VER; // 窗口标题 (任务栏显示)
        Font = new Font("Microsoft YaHei UI", 10f);

        // 拖放支持 — 让用户可以拖 .db 文件到窗口换挡
        AllowDrop = true;
        DragEnter += De;
        DragDrop += Dd;
        FormClosing += Fc;

        // 窗口拉伸过渡动画 — 拉伸过程中内容实时跟随, 拉伸结束后内容卡片平滑淡入
        ResizeEnd += (s, e) => StartResizeFade();

        // 创建界面 + 启动定时器
        Build();
        Ti();

        // 双缓冲 — 减少容器重绘闪烁/残影 (TableLayoutPanel 等默认无双缓冲)
        EnableDoubleBuffer(this);

        // 预创建经典模式窗口 — 把构建开销移到启动阶段, 点【典】时即时显示不卡顿
        try { _classicForm = new ClassicForm(this); } catch { }

        // Load 事件中执行: 系统检测 / 状态刷新 / 网络检测 / AUM 自检
        // v2.03: Ck() 已异步化(SDK探测在后台线程), 首帧立即显示不卡顿
        Load += async (s, e) => { PreLayoutClassic(); CheckDnfExists(); await Ck(); Rf(); await CheckBasicNetwork(); await CheckAUMUpdate(); };
    }

    /*
     * 窗口拉伸过渡动画 (StartResizeFade)
     * 拉伸结束后, 内容卡片背景从半透明平滑过渡到不透明 (约 150ms),
     * 掩盖拉伸瞬间的布局跳变, 提升视觉流畅性
     */
    void StartResizeFade()
    {
        if (_fadeTimer == null)
        {
            _fadeTimer = new Timer { Interval = 16 };
            _fadeTimer.Tick += (s, e) =>
            {
                _fadeV += 0.12f;
                if (_fadeV >= 1f)
                {
                    _fadeV = 1f;
                    _fadeTimer.Stop();
                    if (_contentCard != null) _contentCard.Back = null;
                    return;
                }
                if (_contentCard != null)
                    _contentCard.Back = Color.FromArgb((int)(_fadeV * 255f), Style.Get(Colour.BgContainer));
            };
        }
        _fadeV = 0.4f;
        _fadeTimer.Start();
    }

    /*
     * 主题切换后的全局同步 (OnThemeChanged)
     * 1. 说明页内容背景为固定色, 需跟随主题
     * 2. 浅色主题下彩色按钮使用自定义明确色板 (Ant Design 标准色,
     *    白字, 清晰明显但不刺眼, 各颜色互相区分); 深色主题恢复默认
     * 完整模式与极简模式一并更新
     */
    internal void OnThemeChanged()
    {
        try
        {
            if (_helpFlow != null)
                _helpFlow.BackColor = Style.Get(Colour.BgContainer);
            TshThemeSync();
            ApplyButtonsColor(this);
            if (_miniForm != null && !_miniForm.IsDisposed)
                ApplyButtonsColor(_miniForm);
            if (_classicForm != null && !_classicForm.IsDisposed)
                ApplyButtonsColor(_classicForm);
        }
        catch { }
    }

    /*
     * 递归应用按钮颜色 (ApplyButtonsColor)
     */
    internal static void ApplyButtonsColor(Control c)
    {
        foreach (Control child in c.Controls)
        {
            if (child is AntdUI.Button b && b.Type != TTypeMini.Default)
                b.BackColor = Config.IsLight ? LightColorOf(b.Type) : null;
            ApplyButtonsColor(child);
        }
    }

    /*
     * 浅色主题按钮色板 — Material Design 经典 500 色系
     * (白底+多色最经典好看的搭配, 2014 年至今的 UI 教科书配色;
     * 与深色模式偏深的 蓝/绿/红/橙 相比更亮更鲜艳, 一眼可辨主题)
     * 白字加粗按钮为 Material 官方标准搭配
     */
    static Color? LightColorOf(TTypeMini t) => t switch
    {
        TTypeMini.Primary => Color.FromArgb(33, 150, 243),     // 蓝 Blue-500    #2196F3
        TTypeMini.Success => Color.FromArgb(76, 175, 80),      // 绿 Green-500   #4CAF50
        TTypeMini.Error   => Color.FromArgb(244, 67, 54),      // 红 Red-500     #F44336
        TTypeMini.Warn    => Color.FromArgb(255, 152, 0),      // 橙 Orange-500  #FF9800
        TTypeMini.Info    => Color.FromArgb(0, 188, 212),      // 青 Cyan-500    #00BCD4
        _ => null
    };

    /*
     * 经典模式预布局 (PreLayoutClassic) — 启动时强制创建句柄并完成全部布局,
     * 点击【典】切换时窗口已完全就绪, 即时显示不卡顿
     */
    void PreLayoutClassic()
    {
        try
        {
            if (_classicForm != null && !_classicForm.IsDisposed)
                _classicForm.CreateControl();
        }
        catch { }
    }

    /*
     * 打开极简模式 (OpenMiniMode)
     * 极简模式 = 独立的小窗口 (MiniForm.cs), 只保留最基础功能:
     * 开始游戏 / GM工具 / 开始更新 / 存档切换与管理
     * 关闭极简窗口即返回完整模式, 主窗口保持不变, 互不冲突
     */
    void OpenMiniMode()
    {
        if (_miniForm == null || _miniForm.IsDisposed)
            _miniForm = new MiniForm(this);
        Hide();
        _miniForm.Show();
    }

    /*
     * 打开经典模式 (OpenClassicMode)
     * 经典模式 = 独立窗口 (ClassicForm.cs), 采用旧版一站式布局:
     * 左侧服务控制/更新管理 + 右侧存档管理 + 底部运行日志
     * 日志与更新进度通过 LogHook / ProgressHook 实时转发到经典窗口
     */
    void OpenClassicMode()
    {
        if (_classicForm == null || _classicForm.IsDisposed)
            _classicForm = new ClassicForm(this);
        Hide();
        _classicForm.Show();
    }

    /*
     * 返回完整模式 (BackToFullMode) — 由极简/经典窗口调用
     * 只隐藏子窗口, 不 Close (避免 FormClosing 递归)
     */
    internal void BackToFullMode()
    {
        Show();
        if (_miniForm != null && !_miniForm.IsDisposed)
            _miniForm.Hide();
        if (_classicForm != null && !_classicForm.IsDisposed)
            _classicForm.Hide();
    }

    /*
     * 窗口级合成 (WS_EX_COMPOSITED) — 整窗单次合成
     * v2.03 实测: 开启后 AntdUI 按钮的悬停过渡动画明显变慢(视觉卡顿),
     * 默认关闭; 如需要可改为 true 再测试
     */
    const bool UseComposited = false;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            if (UseComposited) cp.ExStyle |= 0x02000000;   // WS_EX_COMPOSITED
            return cp;
        }
    }

    /*
     * 递归启用双缓冲 (TableLayoutPanel / Panel / FlowLayoutPanel 防闪烁、防切换页面残影)
     * v2.03: 扩展到 AntdUI 自绘容器 (Panel/In.Panel/Table/Menu/PageHeader/Progress)
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

    // =================================================================
    // 控件工厂方法
    // =================================================================

    /*
     * 加载窗口图标
     * 优先级: 内嵌资源 (app.ico) → EXE 关联图标
     */
    void LoadWindowIcon()
    {
        try
        {
            using var s = System.Reflection.Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("app.ico");
            if (s != null) { Icon = new Icon(s); return; }
        }
        catch { }
        try
        {
            var exe = Compat.ExePath();
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                var ic = Icon.ExtractAssociatedIcon(exe);
                if (ic != null) Icon = ic;
            }
        }
        catch { }
    }

    /*
     * 按钮工厂 — AntdUI 主题按钮
     *   t   — 按钮文字
     *   ty  — 类型 (Primary/Success/Error/Warn/Default)
     *   svg — 可选图标 (Ant Design 图标名, 如 "PlayCircleOutlined")
     * 所有按钮统一加边框, 提升深色主题下的可点击识别度:
     *   Default 按钮: 细边框 + 中性灰描边 (区分于背景卡片)
     *   彩色按钮: 1px 同色系描边
     */
    AntdUI.Button B(string t, TTypeMini ty = TTypeMini.Default, string svg = null)
    {
        var b = new AntdUI.Button
        {
            Text = t,
            Type = ty,
            Dock = DockStyle.Fill,
            Margin = new Padding(4),
            Cursor = Cursors.Hand,
            WaveSize = 0        // v2.03: 关闭水波动画, 减少点击/滚动时的重绘开销
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
     * 标签工厂 — 主题标签 (自动配色)
     */
    AntdUI.Label L(string t, float fs = 9.5f, Color? c = null)
    {
        var lb = new AntdUI.Label
        {
            Text = t,
            Font = new Font("Microsoft YaHei UI", fs),
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill
        };
        if (c != null) lb.ForeColor = c.Value;
        return lb;
    }

    /*
     * 卡片工厂 — 圆角卡片 (主题自适应背景 + 阴影 + 1px 主题边框)
     * BorderColor 不指定 → 使用主题 BorderColor, 深浅模式下自动适配且清晰可见
     */
    AntdUI.Panel Card(int radius = 8, int shadow = 1)
    {
        return new AntdUI.Panel
        {
            Dock = DockStyle.Fill,
            Radius = radius,
            Shadow = shadow,
            BorderWidth = 1F,
            Margin = new Padding(4)
        };
    }

    /*
     * 标题文字 — 卡片内的小节标题 (加粗 + 主题色)
     */
    AntdUI.Label Cap(string t)
    {
        return new AntdUI.Label
        {
            Text = t,
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
    }

    /*
     * 开关行 — 开关 + 说明文字的紧凑组合
     *   t   — 说明文字
     *   sw  — 开关控件
     *   fs  — 字号
     *   fg  — 文字颜色 (null = 默认灰色)
     * 布局: 开关在左, 文字紧挨其右 — "文字挨着按钮"
     * 注意: 1) AntdUI.Label 的 AutoSize 测量偏窄(约-8px), 会导致文字被裁剪
     *       2) AutoSize=false 后必须同时指定宽高, 否则默认高度100px
     *          会把文字顶出容器可视区(表现为"文字没显示")
     */
    Control SwRow(string t, AntdUI.Switch sw, float fs = 9f, Color? fg = null)
    {
        var f = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 7, 0, 0)
        };
        var font = new Font("Microsoft YaHei UI", fs);
        var sz = TextRenderer.MeasureText(t, font);
        var lb = new AntdUI.Label
        {
            Text = t,
            Font = font,
            ForeColor = fg ?? Color.FromArgb(140, 140, 148),
            AutoSize = false,
            Size = new Size(sz.Width + 6, sz.Height + 2),
            Margin = new Padding(6, 1, 0, 0)
        };
        sw.Size = new Size(40, 22);
        sw.Margin = new Padding(0, 1, 0, 0);
        f.Controls.Add(sw);
        f.Controls.Add(lb);
        return f;
    }

    /*
     * 状态卡片 — 概览页顶部的 4 张统计卡 (标题 + 大号状态值)
     */
    AntdUI.Panel StatCard(string caption, out AntdUI.Label val)
    {
        var card = Card(8, 2);
        card.Padding = new Padding(14, 8, 14, 8);
        var g = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1, RowCount = 2,
            BackColor = Color.Transparent
        };
        g.RowStyles.Add(new RowStyle(SizeType.Percent, 40F));
        g.RowStyles.Add(new RowStyle(SizeType.Percent, 60F));
        var cap = new AntdUI.Label
        {
            Text = caption,
            Font = new Font("Microsoft YaHei UI", 8.5f),
            ForeColor = Color.FromArgb(130, 130, 130),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        val = new AntdUI.Label
        {
            Text = "--",
            Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        g.Controls.Add(cap, 0, 0);
        g.Controls.Add(val, 0, 1);
        card.Controls.Add(g);
        return card;
    }

    // =================================================================
    // UI 布局 (Build)
    // =================================================================

    void Build()
    {
        // ============================================================
        // ★ root 根布局 ★ — 3 行: 标题栏 / 主区域 / 日志条
        // ============================================================
        _root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1, RowCount = 3,
            BackColor = Color.Transparent
        };
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));   // 标题栏
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));   // 主区域
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 210F));  // 日志条
        Controls.Add(_root);

        // ============================================================
        // ★ r0 标题栏 ★ — AntdUI PageHeader (自带最小化/最大化/关闭)
        // ============================================================
        hd = new AntdUI.PageHeader
        {
            Dock = DockStyle.Fill,
            Text = "ServerS4A21 管理器",
            SubText = "v" + VER + " · ServerS4A21-AUM",
            ShowIcon = true,
            ShowButton = true,
            DividerShow = true
        };

        // 状态标签 (标题栏右侧) — 第一个添加 = 靠左; 右边距 3px 与"简"按钮隔开
        lbStHd = new AntdUI.Label
        {
            Dock = DockStyle.Right,
            Width = 150,
            Margin = new Padding(0, 0, 3, 0),
            Text = "● 未运行",
            ForeColor = Rd,
            Font = new Font("Microsoft YaHei UI", 9f),
            TextAlign = ContentAlignment.MiddleRight
        };

        // 明暗主题切换按钮 — 最后一个添加 = 最右 (紧邻窗口按钮)
        btTheme = new AntdUI.Button
        {
            Dock = DockStyle.Right,
            Width = 44,
            Ghost = true,
            Radius = 0,
            WaveSize = 0,
            IconSvg = "SunOutlined",
            ToggleIconSvg = "MoonOutlined",
            Toggle = false,
            Cursor = Cursors.Hand
        };
        btTheme.Click += (s, e) =>
        {
            Config.IsLight = !Config.IsLight;
            OnThemeChanged();
        };

        // 经典模式切换按钮 (旧版一站式布局) — 位于【简】左边
        var btCla = new AntdUI.Button
        {
            Dock = DockStyle.Right,
            Width = 44,
            Ghost = true,
            Radius = 0,
            WaveSize = 0,
            Text = "典",
            Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btCla.Click += (s, e) => OpenClassicMode();

        // 极简模式切换按钮 (打开独立的小窗口) — 位于明暗主题切换按钮左边
        btSize = new AntdUI.Button
        {
            Dock = DockStyle.Right,
            Width = 44,
            Ghost = true,
            Radius = 0,
            WaveSize = 0,
            Text = "简",
            Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btSize.Click += (s, e) => OpenMiniMode();

        hd.Controls.Add(lbStHd);
        // 3px 间距占位 — 让状态标签与"简"按钮保持间隔 (PageHeader 忽略 Margin)
        hd.Controls.Add(new System.Windows.Forms.Panel
        {
            Dock = DockStyle.Right,
            Width = 3,
            BackColor = Color.Transparent
        });
        hd.Controls.Add(btCla);
        hd.Controls.Add(btSize);
        hd.Controls.Add(btTheme);
        _root.Controls.Add(hd, 0, 0);

        // ============================================================
        // ★ r1 主区域 ★ — 左侧导航 + 右侧内容卡片
        // ============================================================
        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2, RowCount = 1,
            BackColor = Color.Transparent,
            Padding = new Padding(12, 12, 12, 0)
        };
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180F));
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _root.Controls.Add(main, 0, 1);

        // ---- 左侧导航 ----
        var sideCard = Card(8, 2);
        sideCard.Margin = new Padding(0, 4, 4, 4);
        mn = new AntdUI.Menu
        {
            Dock = DockStyle.Fill,
            Indent = true,
            Padding = new Padding(6),
            Unique = true
        };
        mn.Items.Add(new AntdUI.MenuItem { Text = "开始",      IconSvg = "DashboardOutlined",      Tag = "dash" });
        mn.Items.Add(new AntdUI.MenuItem { Text = "存档管理",  IconSvg = "DatabaseOutlined",       Tag = "archive" });
        mn.Items.Add(new AntdUI.MenuItem { Text = "DLL扩展",   IconSvg = "ApiOutlined",            Tag = "dll" });
        mn.Items.Add(new AntdUI.MenuItem { Text = "调整系统时间", IconSvg = "ClockCircleOutlined", Tag = "time" });
        mn.Items.Add(new AntdUI.MenuItem { Text = "设置与关于", IconSvg = "SettingOutlined",        Tag = "about" });
        mn.Items.Add(new AntdUI.MenuItem { Text = "使用说明",   IconSvg = "QuestionCircleOutlined", Tag = "help" });
        mn.Items.Add(new AntdUI.MenuItem { Text = "疑难杂症解惑", IconSvg = "BulbOutlined",         Tag = "tsh" });
        mn.SelectChanged += (s, e) =>
        {
            var tag = (e.Value?.Tag as string) ?? "";
            ShowPage(tag);
        };
        sideCard.Controls.Add(mn);
        main.Controls.Add(sideCard, 0, 0);

        // ---- 右侧内容卡片 ----
        var contentCard = Card(8, 2);
        contentCard.Margin = new Padding(4);
        _contentCard = contentCard;
        pgDash = new AntdUI.In.Panel { Dock = DockStyle.Fill, AutoScroll = true };
        pgArc  = new AntdUI.Panel   { Dock = DockStyle.Fill, Visible = false };
        pgUpd  = new AntdUI.In.Panel { Dock = DockStyle.Fill, AutoScroll = true, Visible = false };
        pgHelp = new AntdUI.In.Panel { Dock = DockStyle.Fill, AutoScroll = true, Visible = false };
        pgTsh  = new AntdUI.In.Panel { Dock = DockStyle.Fill, AutoScroll = true, Visible = false };
        pgDll  = new AntdUI.In.Panel { Dock = DockStyle.Fill, AutoScroll = true, Visible = false };
        pgTime = new AntdUI.In.Panel { Dock = DockStyle.Fill, AutoScroll = true, Visible = false };
        contentCard.Controls.Add(pgDash);
        contentCard.Controls.Add(pgArc);
        contentCard.Controls.Add(pgDll);
        contentCard.Controls.Add(pgTime);
        contentCard.Controls.Add(pgUpd);
        contentCard.Controls.Add(pgHelp);
        contentCard.Controls.Add(pgTsh);
        main.Controls.Add(contentCard, 1, 0);

        BuildDash();
        BuildArchive();
        BuildAbout();
        BuildHelp();
        BuildTroubleshoot();
        BuildDllPage();
        BuildTimePage();
        BuildLog();

        // 运行方式默认不勾选任何 DX 选项 (无选中 = 默认 DX9 运行)
        // (SelectIndex 赋值会触发 ApplyDx, 用 _dxReady 标志避免启动时
        //  自动删除/复制补丁文件, 一切由用户点击决定)
        try
        {
            sgDx.SelectIndex = -1;
            _dxReady = true;
        }
        catch { }

        // 默认选中"概览"
        try { mn.SelectIndex(0); } catch { }

        // 预布局隐藏页面 — WinForms 不会自动布局不可见控件,
        // 首次切换时才布局会导致明显卡顿 (表格等重控件需计算行高)。
        // 这里先把隐藏页面的尺寸设为目标尺寸并强制布局, 切换时即开即用。
        try
        {
            var card = (AntdUI.Panel)main.Controls[1];
            var cs = card.ClientSize;
            pgArc.Size = cs;
            pgUpd.Size = cs;
            pgDash.Size = cs;
            pgTsh.Size = cs;
            pgDll.Size = cs;
            pgTime.Size = cs;
            pgArc.PerformLayout();
            pgUpd.PerformLayout();
            pgDash.PerformLayout();
            pgTsh.PerformLayout();
            pgDll.PerformLayout();
            pgTime.PerformLayout();
        }
        catch { }
    }

    /*
     * 页面切换 (ShowPage) — 双缓冲 + 布局控制, 避免卡顿与残影
     * 先挂起布局, 切换可见性后立即对目标页强制布局, 再恢复布局并重绘内容区
     */
    void ShowPage(string tag)
    {
        Control target = tag switch
        {
            "archive" => (Control)pgArc,
            "dll" => (Control)pgDll,
            "time" => (Control)pgTime,
            "about" => (Control)pgUpd,
            "help" => (Control)pgHelp,
            "tsh" => (Control)pgTsh,
            _ => (Control)pgDash
        };

        SuspendLayout();
        foreach (var c in new Control[] { pgDash, pgArc, pgDll, pgTime, pgUpd, pgHelp, pgTsh })
        {
            bool show = c == target;
            if (c.Visible != show)
            {
                c.Visible = show;
                if (show) c.PerformLayout();   // 强制完成目标页布局, 避免显示时的延迟布局卡顿
            }
        }
        ResumeLayout(true);
        // 只重绘内容区, 避免全窗口无效化造成的卡顿 (侧边栏/标题栏/日志条无需重绘)
        if (target.Parent != null) target.Parent.Invalidate(true);
    }
}