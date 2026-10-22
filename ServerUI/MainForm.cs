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
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
    static readonly Color Mute = Color.FromArgb(140, 140, 148); // 统一次要文字 (副标题/说明)
    static readonly Color Faint = Color.FromArgb(120, 120, 128); // 更弱一级 (表头/分组)

    // =================================================================
    // 字体缓存 — 界面里 90% 的文字只用这几档字号
    // 旧实现每建一个 Label 就 new Font 一次, 运行期重建 DLL 列表 / 每条加粗日志
    // 都会再分配 GDI 字体句柄且从不释放; 统一走缓存后句柄数量恒定
    // 注意: 缓存字体是共享只读对象, 任何地方都不要 Dispose 它
    // =================================================================
    static readonly Dictionary<(float, FontStyle), Font> _fontCache = new();
    internal static Font F(float size, FontStyle style = FontStyle.Regular)
    {
        var key = (size, style);
        lock (_fontCache)
        {
            if (!_fontCache.TryGetValue(key, out var f))
            {
                f = new Font("Microsoft YaHei UI", size, style);
                _fontCache[key] = f;
            }
            return f;
        }
    }

    // ===== 页头状态胶囊 =====
    AntdUI.Panel _stPill;
    AntdUI.Label _stPillLb;
    int _stKind = -1;                 // 0=未运行 1=运行中 2=外部运行 (主题切换时重绘用)
    string _stPillText = "未运行";

    // ===== 页面切换交叉淡入 (v2.17) =====
    // 切换瞬间抓取新旧两帧截图, 在内容区叠加一层 130ms 的淡出层:
    // 动画期间是静态位图, 不触发任何布局/重排, 因此不会像滑动动画那样拖慢切换
    const int PageFadeFrames = 8;     // 8 × 16ms ≈ 130ms
    Control _pageFade;
    Timer _pageFadeTimer;
    int _pageFadeStep;
    string _curPageTag = "dash";      // 当前页 (同一页重复点击不再重排)
    Control _curPageControl;          // 当前页控件 (缓存快照用)
    bool _pageFadeReady;              // 首帧显示完成前不做截图淡入

    // 页面快照缓存 (快照优先切页) — 点击后先用"上次的画面"立刻开始交叉淡入,
    // 真正的页面切换与绘制在这 130ms 动画之后、被快照盖住时完成。
    // 用户感知: 点击即响应; 剩下的 ~60ms 绘制被动画完全遮住。
    const int PageShotCacheMax = 4;   // 最多缓存 4 页 (每页约 6MB)
    readonly Dictionary<Control, Bitmap> _pageShots = new();
    readonly List<Control> _pageShotOrder = new();   // LRU 顺序 (旧 → 新)
    Control _pendingPage;             // 动画结束后要切换到的页面
    bool _switchAtFadeEnd;            // 快照优先模式: 动画结束后再真正切页
    Size _lastShotSize;               // 上次抓帧时的内容区尺寸 (变化则缓存失效)

    // ===== 日志区高度 (可拖拽分隔条) =====
    const int LogCollapsedH = 46;
    int _logExpandedH = 170;

    // ===== 悬停提示 (AntdUI 原生 Tooltip, 与主题一致) =====
    readonly AntdUI.TooltipComponent _tips = new();
    void Tip(Control c, string text)
    {
        try { _tips.SetTip(c, text); } catch { }
    }

    // MB = 备份存档保留的最大数量 (当前 10 个)
    const int MB = 10;

    // VER = 当前工具版本号 — 显示在窗口标题和启动日志中
#if NET48
    internal const string VER = "2.16-V";   // Win7 兼容模式
#else
    internal const string VER = "2.16";
#endif

    // ===== 路径计算 =====
    // 无头自测可用 SERVERUI_BASE_DIR 指向真实部署目录, 复现正式环境的相对路径
    // 用 AppContext.BaseDirectory: 单文件发布时它是 exe 所在目录 (文档约定),
    // 而 AppDomain.CurrentDomain.BaseDirectory 在部分单文件/自解压场景会指向临时目录
    readonly string _bd =
        HeadlessMode && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SERVERUI_BASE_DIR"))
            ? Environment.GetEnvironmentVariable("SERVERUI_BASE_DIR")
            : AppContext.BaseDirectory;
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
    bool _updateBusy;                     // v2.15: 更新防重入标志 (增量/全量共用)
    System.Threading.Tasks.Task _mirrorTask;  // v2.15: 令牌校验任务 (上传前等待其完成)
    bool _startFailLogged;                // v2.15: 服务端启动失败提示只输出一次
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
        Font = F(10f);

        // 拖放支持 — 让用户可以拖 .db 文件到窗口换挡
        AllowDrop = true;
        DragEnter += De;
        DragDrop += Dd;
        FormClosing += Fc;

        // 窗口拉伸过渡动画 — 拉伸过程中内容实时跟随, 拉伸结束后内容卡片平滑淡入
        ResizeEnd += (s, e) => { StartResizeFade(); SyncPageSizes(); };

        // 创建界面 + 启动定时器
        Build();
        Ti();

        // 首帧显示完成后再允许截图淡入 (启动阶段截图会抓到未布局的半成品);
        // 同时补一次隐藏页预布局 — Build() 时内容卡片可能还没有最终尺寸
        Shown += (s, e) =>
        {
            if (HeadlessMode) return;   // 无头自测不做截图淡入, 避免叠加层影响渲染
            _pageFadeReady = true;
            SyncPageSizes();
        };

        // v2.15: AUM 自更新完成回调只在构造时订阅一次
        // (旧逻辑每次点击【更新AUM】都 += 一个新 lambda, 多次更新后日志成对重复)
        _au.Completed += ok =>
        {
            if (ok) Lg("[AUM更新] 编译成功，即将自动重启...", Gn);
            else Lg("[AUM更新] 更新流程中断，可稍后重试。", Or);
        };

        // v2.15: 服务端启动/清理通知 → 运行日志
        _sv.NoticeReceived += m => Lg(m, Or);

        // 双缓冲 — 减少容器重绘闪烁/残影 (TableLayoutPanel 等默认无双缓冲)
        EnableDoubleBuffer(this);

        // 预创建经典模式窗口 — 把构建开销移到启动阶段, 点【典】时即时显示不卡顿
        try { _classicForm = new ClassicForm(this); } catch { }

        // Load 事件中执行: 系统检测 / 状态刷新 / 网络检测 / AUM 自检
        // v2.03: Ck() 已异步化(SDK探测在后台线程), 首帧立即显示不卡顿
        Load += async (s, e) =>
        {
            PreLayoutClassic();
            if (HeadlessMode) return;   // 无头自测: 不联网、不探测进程
            CheckDnfExists();
            await Ck();
            Rf();
            await CheckBasicNetwork();
            await CheckAUMUpdate();
        };
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
            // 主题变了 → 旧页面快照的颜色不再匹配, 立刻丢弃
            DisposePageFade(cancelPending: true);
            ClearPageShots();
            _lastShotSize = Size.Empty;   // 让下一次 SyncPageSizes 重新抓帧
            if (_helpFlow != null)
                _helpFlow.BackColor = Style.Get(Colour.BgContainer);
            TshThemeSync();
            DllThemeSync();             // 插件行名称颜色随主题重刷
            ApplyButtonsColor(this);
            ApplyHeaderStatusColor();   // 状态胶囊底色/描边随主题重算
            ApplyLogTheme();            // 日志滚动条跟随明暗主题
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
            Font = F(fs),
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill
        };
        if (c != null) lb.ForeColor = c.Value;
        return lb;
    }

    /*
     * 卡片工厂 — 圆角卡片 (主题自适应背景 + 1px 主题边框)
     * BorderColor 不指定 → 使用主题 BorderColor, 深浅模式下自动适配且清晰可见
     * 性能: 阴影是整页重绘的最大开销 (无头实测切页绘制 ~130ms → ~55ms),
     *       卡片之间改用 1px 主题描边区分层次, 视觉上依旧清晰
     */
    AntdUI.Panel Card(int radius = 8, int shadow = 1)
    {
        return new AntdUI.Panel
        {
            Dock = DockStyle.Fill,
            Radius = radius,
            Shadow = 0,
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
            Font = F(10f, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
    }

    /*
     * 统一页头 (PageHead) — 页面标题 + 一行副标题 + 右侧操作区
     * 视觉层级: 15pt 粗体标题 / 9pt 次要色副标题; 操作按钮右对齐等高
     * 所有页面第一行都使用它, 让"我在哪一页 / 这页能做什么"一眼可见
     */
    Control PageHead(string title, string sub, params Control[] actions)
    {
        int n = actions == null ? 0 : actions.Length;
        var head = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1 + n, RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(6, 2, 6, 4)
        };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        // 操作区用固定列宽 — 旧写法用 AutoSize 列测按钮首选宽度,
        // 按钮文字一长就把整行列宽撑出父容器 (表现为右侧按钮被裁掉、标题被挤没)
        for (int i = 0; i < n; i++)
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 156F));
        head.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var texts = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1, RowCount = 2,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        texts.RowStyles.Add(new RowStyle(SizeType.Percent, 56F));
        texts.RowStyles.Add(new RowStyle(SizeType.Percent, 44F));
        texts.Controls.Add(new AntdUI.Label
        {
            Text = title,
            Font = F(15f, FontStyle.Bold),
            Dock = DockStyle.Fill,
            // 关键: Label 默认 Margin=3 会吃掉 6px 高度; 15pt 粗体在高 DPI 下行高约 31px,
            // 一旦控件高度小于行高, AntdUI.Label 会直接不绘制文字 (无头截图里就是"标题消失")。
            // Margin=0 让标题占满行高, 稳定可见。
            Margin = new Padding(0),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        texts.Controls.Add(new AntdUI.Label
        {
            Text = sub,
            Font = F(9f),
            ForeColor = Mute,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            TextAlign = ContentAlignment.TopLeft
        }, 0, 1);
        head.Controls.Add(texts, 0, 0);

        for (int i = 0; i < n; i++)
        {
            actions[i].Dock = DockStyle.Fill;
            actions[i].Margin = new Padding(4, 5, 4, 5);
            head.Controls.Add(actions[i], 1 + i, 0);
        }
        return head;
    }

    /*
     * 卡片小节标题 (SectionHead) — 左侧主题色竖条 + 10pt 粗体标题
     * 比纯文字标题更容易扫读, 也把"服务端控制 / 更新与工具"这类卡片做了分组
     */
    Control SectionHead(string title)
    {
        var box = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2, RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(2, 0, 2, 0)
        };
        box.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 12F));
        box.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        box.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        box.Controls.Add(new AntdUI.Panel
        {
            Dock = DockStyle.Fill,
            Radius = 2,
            Back = Style.Get(Colour.Primary),
            Margin = new Padding(0, 7, 8, 7)
        }, 0, 0);
        box.Controls.Add(new AntdUI.Label
        {
            Text = title,
            Font = F(10f, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 1, 0);
        return box;
    }

    /*
     * 标题栏状态胶囊 (SetHeaderStatus)
     * 旧实现是一个固定宽度、纯色文字的状态标签; 改成"浅色底 + 同色描边 + 同色文字"的胶囊,
     * 状态在标题栏里更醒目, 同时用 AntdUI 主题色, 明暗主题下都保持正确对比度
     */
    void SetHeaderStatus(string text, Color kindColor)
    {
        int kind = kindColor == Gn ? 1 : (kindColor == Or ? 2 : 0);
        string label = text.Replace("●", "").Replace("(外部)", "外部").Trim();
        if (label.Contains("外部")) label = "外部运行";
        // Rs() 每 2 秒刷新一次, 状态没变就不重设颜色, 避免无意义的重绘
        if (kind == _stKind && label == _stPillText) return;
        _stKind = kind;
        _stPillText = label;
        ApplyHeaderStatusColor();
    }

    void ApplyHeaderStatusColor()
    {
        if (_stPill == null || _stPillLb == null || _stKind < 0) return;
        Colour bg, bd, fg;
        if (_stKind == 1) { bg = Colour.SuccessBg; bd = Colour.SuccessBorder; fg = Colour.Success; }
        else if (_stKind == 2) { bg = Colour.WarningBg; bd = Colour.WarningBorder; fg = Colour.Warning; }
        else { bg = Colour.ErrorBg; bd = Colour.ErrorBorder; fg = Colour.Error; }
        try
        {
            _stPill.Back = Style.Get(bg);
            _stPill.BorderColor = Style.Get(bd);
            _stPillLb.ForeColor = Style.Get(fg);
            _stPillLb.Text = _stPillText;
        }
        catch { }
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
        var font = F(fs);
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
            Font = F(9f),
            ForeColor = Mute,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        val = new AntdUI.Label
        {
            Text = "--",
            Font = F(15f, FontStyle.Bold),
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
            ColumnCount = 1, RowCount = 4,
            BackColor = Color.Transparent
        };
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));    // 标题栏
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));    // 主区域
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 6F));     // 日志区拖拽分隔条
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, _logExpandedH)); // 日志条
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

        // 状态胶囊 (标题栏右侧) — 浅色底 + 同色描边 + 同色文字
        // 旧实现是固定宽度的纯色文字标签, 在标题栏里存在感很弱
        var stWrap = new TableLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 116,
            ColumnCount = 1, RowCount = 3,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        stWrap.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        stWrap.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
        stWrap.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
        stWrap.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
        _stPill = new AntdUI.Panel
        {
            Dock = DockStyle.Fill,
            Radius = 13,
            BorderWidth = 1F,
            Margin = new Padding(0, 0, 8, 0)
        };
        _stPillLb = new AntdUI.Label
        {
            Text = "未运行",
            Font = F(9f, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter
        };
        _stPill.Controls.Add(_stPillLb);
        stWrap.Controls.Add(_stPill, 0, 1);
        hd.Controls.Add(stWrap);
        SetHeaderStatus("● 未运行", Rd);

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
            Cursor = Cursors.Hand,
            AccessibleName = "切换明暗主题"
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
            Font = F(10f, FontStyle.Bold),
            Cursor = Cursors.Hand,
            AccessibleName = "经典模式"
        };
        btCla.Click += (s, e) => OpenClassicMode();
        Tip(btCla, "经典模式 — 旧版一站式布局");

        // 极简模式切换按钮 (打开独立的小窗口) — 位于明暗主题切换按钮左边
        btSize = new AntdUI.Button
        {
            Dock = DockStyle.Right,
            Width = 44,
            Ghost = true,
            Radius = 0,
            WaveSize = 0,
            Text = "简",
            Font = F(10f, FontStyle.Bold),
            Cursor = Cursors.Hand,
            AccessibleName = "极简模式"
        };
        btSize.Click += (s, e) => OpenMiniMode();
        Tip(btSize, "极简模式 — 只保留开始游戏与存档切换");
        Tip(btTheme, "切换明暗主题");

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

        // ============================================================
        // ★ r2 日志分隔条 ★ — 上下拖动调整日志区高度 (双击复位)
        // 旧实现日志区固定 210px, 即使没有日志也长期占掉四分之一窗口;
        // 现在默认 170px, 且可拖拽/折叠, 把纵向空间还给页面内容
        // ============================================================
        var splitter = new LogSplitter { Dock = DockStyle.Fill };
        splitter.Dragged += dy =>
        {
            var h = _root.RowStyles[3].Height + dy;
            h = Math.Max(LogCollapsedH, Math.Min(420, h));
            // 拖动时合并布局: 一次 MouseMove 只跑一轮排版, 避免日志区(含 RichTextBox)反复重排
            _root.SuspendLayout();
            _root.RowStyles[3].Height = h;
            _root.ResumeLayout();
            if (!_logCollapsed) _logExpandedH = (int)h;
        };
        splitter.DoubleClick += (s, e) =>
        {
            _logCollapsed = !_logCollapsed;
            _root.RowStyles[3].Height = _logCollapsed ? LogCollapsedH : _logExpandedH;
            if (rt != null) rt.Visible = !_logCollapsed;
            if (btFold != null) btFold.IconSvg = _logCollapsed ? "UpOutlined" : "DownOutlined";
        };
        _root.Controls.Add(splitter, 0, 2);

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
        SyncPageSizes();
    }

    /*
     * 预布局全部页面 (SyncPageSizes) — 启动与窗口缩放结束时调用
     * 隐藏页不会参与窗体布局, 若不主动按内容区尺寸预排, 缩放后第一次切过去
     * 就要现场算一遍布局 (表格/流式按钮列表尤其明显) → 表现为"切页顿一下"
     */
    void SyncPageSizes()
    {
        try
        {
            if (_contentCard == null) return;
            var cs = _contentCard.ClientSize;
            if (cs.Width <= 0 || cs.Height <= 0) return;

            // 内容区尺寸变了 → 旧快照比例不再匹配, 清空缓存 (下次切页重新抓帧)
            if (_lastShotSize != cs)
            {
                _lastShotSize = cs;
                DisposePageFade(cancelPending: true);
                ClearPageShots();
            }

            var pages = new Control[] { pgDash, pgArc, pgDll, pgTime, pgUpd, pgHelp, pgTsh };
            _contentCard.SuspendLayout();
            foreach (var p in pages)
            {
                if (p == null) continue;
                if (!p.Visible)
                {
                    if (p.Size != cs) p.Size = cs;
                    p.PerformLayout();
                }
            }
            _contentCard.ResumeLayout(false);
        }
        catch { }
    }

    /*
     * 页面切换 (ShowPage) — 交叉淡入 + 零重排切换
     *
     * v2.17 视觉升级:
     *   1. 同一页重复点击直接忽略, 不再做无意义的重排
     *   2. 切换前抓一帧旧页、切换后抓一帧新页, 叠加 130ms 静态淡出层
     *      (动画期间不跑布局, 所以既有过渡感又不会像滑动动画那样拖慢)
     *   3. 不再对整个内容卡片 Invalidate(true) 递归重绘 —
     *      旧写法把侧栏之外的所有子控件全部标脏, 每次切页等于整页重画两遍
     *   4. 截图失败时自动退化为原来的硬切, 不影响功能
     */
    void ShowPage(string tag)
    {
        if (tag == _curPageTag) return;
        _curPageTag = tag;

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

        // 先摘掉上一轮还没淡完的覆盖层, 否则截图会抓到残影
        DisposePageFade(cancelPending: true);
        _contentCard?.Update();

        Bitmap shotBefore = _pageFadeReady ? CaptureContent() : null;

        // ① 快照优先: 目标页有上次的画面 → 立刻播放"旧帧 → 目标快照"的淡入
        //    (纯位图动画, 不做布局/重绘); 动画结束后再在快照背后完成真正的切页。
        //    这样点击到画面变化只有几毫秒, 页面绘制被 130ms 动画完全遮住。
        if (shotBefore != null && _pageShots.TryGetValue(target, out var cached)
            && cached != null)
        {
            try
            {
                var ov = new PageFadeOverlay(shotBefore, cached, ownsAfter: false);
                _pendingPage = target;
                _switchAtFadeEnd = true;
                _curPageControl = target;
                AddFadeOverlay(ov);
                StartFadeTimer();
                return;
            }
            catch
            {
                _pendingPage = null;
                _switchAtFadeEnd = false;
                shotBefore.Dispose();
            }
        }

        // ② 首次进入该页: 直接切换并绘制, 再对"旧帧 → 新页"做淡入
        SwitchPages(target);
        _curPageControl = target;
        if (shotBefore == null) return;

        _contentCard.Update();
        var shotAfter = CaptureContent();
        if (shotAfter == null) { shotBefore.Dispose(); return; }
        CachePageShot(target, shotAfter);   // 缓存持有该位图
        try
        {
            var ov = new PageFadeOverlay(shotBefore, shotAfter, ownsAfter: false);
            AddFadeOverlay(ov);
            StartFadeTimer();
        }
        catch { shotBefore.Dispose(); }
    }

    /* 真正的页面切换 (可见性 + 目标页布局 + 标脏) — 供直接切换/动画结束回调共用 */
    void SwitchPages(Control target)
    {
        // 只挂起内容卡片的布局 — 旧写法 SuspendLayout() 挂起整个窗体,
        // 切换后会导致标题栏/侧栏/日志条一起重排重绘
        _contentCard.SuspendLayout();
        foreach (var c in new Control[] { pgDash, pgArc, pgDll, pgTime, pgUpd, pgHelp, pgTsh })
        {
            bool show = c == target;
            if (c.Visible != show)
            {
                c.Visible = show;
                if (show) c.PerformLayout();   // 强制完成目标页布局, 避免显示时的延迟布局卡顿
            }
        }
        _contentCard.ResumeLayout(true);
        target.Invalidate();   // 只标脏目标页自身, 不递归
    }

    void AddFadeOverlay(PageFadeOverlay ov)
    {
        _pageFade = ov;
        _pageFadeStep = 0;
        _contentCard.Controls.Add(ov);
        ov.BringToFront();
        ov.Update();   // 第一帧立即上屏, 不等下一轮消息循环
    }

    void StartFadeTimer()
    {
        if (_pageFadeTimer == null)
        {
            _pageFadeTimer = new Timer { Interval = 16 };
            _pageFadeTimer.Tick += (s, e) =>
            {
                _pageFadeStep++;
                if (_pageFadeStep >= PageFadeFrames)
                {
                    // 快照优先模式: 此时画面完全由最后一帧快照覆盖, 在背后完成切页与绘制
                    if (_switchAtFadeEnd && _pendingPage != null)
                    {
                        var t = _pendingPage;
                        _pendingPage = null;
                        _switchAtFadeEnd = false;
                        try { SwitchPages(t); _contentCard.Update(); } catch { }
                    }
                    DisposePageFade();
                    SchedulePageShotRefresh();
                    return;
                }
                if (_pageFade is PageFadeOverlay o)
                {
                    float t = (float)_pageFadeStep / PageFadeFrames;
                    o.Alpha = (1f - t) * (1f - t);   // ease-out
                    o.Invalidate();
                }
            };
        }
        _pageFadeTimer.Start();
    }

    /*
     * 动画结束后刷新当前页的快照缓存
     * 晚一拍执行: 等覆盖层移除、真实页面露出后再抓帧, 避免把叠加层截进去;
     * 期间如果又开始了新过渡则放弃 (新过渡会自己抓帧)
     */
    void SchedulePageShotRefresh()
    {
        var page = _curPageControl;
        if (page == null) return;
        try
        {
            BeginInvoke(new Action(() =>
            {
                if (_pageFade != null || page != _curPageControl) return;
                try { _contentCard.Update(); } catch { }   // 确保真实页面已上屏再抓帧
                var bmp = CaptureContent();
                if (bmp != null) CachePageShot(page, bmp);
            }));
        }
        catch { }
    }

    void CachePageShot(Control page, Bitmap bmp)
    {
        if (page == null || bmp == null) return;
        if (_pageShots.TryGetValue(page, out var old) && old != null)
        {
            _pageShots.Remove(page);
            _pageShotOrder.Remove(page);
            old.Dispose();
        }
        _pageShots[page] = bmp;
        _pageShotOrder.Add(page);

        while (_pageShotOrder.Count > PageShotCacheMax)
        {
            var evict = _pageShotOrder[0];
            _pageShotOrder.RemoveAt(0);
            if (_pageShots.TryGetValue(evict, out var b))
            {
                _pageShots.Remove(evict);
                b?.Dispose();
            }
        }
    }

    /* 尺寸/主题变化后旧快照失效 — 清空并释放 */
    void ClearPageShots()
    {
        foreach (var b in _pageShots.Values) b?.Dispose();
        _pageShots.Clear();
        _pageShotOrder.Clear();
    }

    /* 抓取内容区当前画面 (屏幕像素) — 失败返回 null, 由调用方退化为硬切 */
    Bitmap CaptureContent()
    {
        try
        {
            var card = _contentCard;
            if (card == null || !card.IsHandleCreated || !card.Visible) return null;
            if (card.Width < 32 || card.Height < 32) return null;
            var bmp = new Bitmap(card.Width, card.Height, PixelFormat.Format32bppPArgb);
            if (HeadlessMode)
            {
                // 无头窗口在屏幕外, CopyFromScreen 只能抓到黑屏 → 用控件自绘抓帧
                card.DrawToBitmap(bmp, new Rectangle(0, 0, card.Width, card.Height));
                return bmp;
            }
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(card.PointToScreen(Point.Empty), Point.Empty, card.Size, CopyPixelOperation.SourceCopy);
            return bmp;
        }
        catch { return null; }
    }

    /* 移除淡入层; cancelPending = 取消尚未执行的"动画结束后切页" */
    void DisposePageFade(bool cancelPending = false)
    {
        _pageFadeTimer?.Stop();
        if (cancelPending)
        {
            _pendingPage = null;
            _switchAtFadeEnd = false;
        }
        if (_pageFade == null) return;
        var c = _pageFade;
        _pageFade = null;
        try
        {
            if (c.Parent != null) c.Parent.Controls.Remove(c);
            c.Dispose();
        }
        catch { }
        // 移除后露出真正的页面, 主动刷新一次避免残留旧像素
        try { _contentCard?.Invalidate(); } catch { }
    }

    /* 透明叠层: 先画新页位图, 再按递减 alpha 画旧页位图 = 真正的交叉淡入 */
    sealed class PageFadeOverlay : Control
    {
        readonly Bitmap _before, _after;
        bool _dead;
        public float Alpha = 1f;

        readonly bool _ownsAfter;

        public PageFadeOverlay(Bitmap before, Bitmap after, bool ownsAfter)
        {
            _before = before;
            _after = after;
            _ownsAfter = ownsAfter;
            Dock = DockStyle.Fill;
            Enabled = false;                       // 淡入期间不拦截点击
            TabStop = false;
            SetStyle(ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (_dead) return;
            if (_after != null)
                e.Graphics.DrawImageUnscaled(_after, Point.Empty);
            if (_before == null || Alpha <= 0f) return;

            using var ia = new ImageAttributes();
            ia.SetColorMatrix(new ColorMatrix
            {
                Matrix00 = 1f, Matrix11 = 1f, Matrix22 = 1f,
                Matrix33 = Math.Min(1f, Alpha), Matrix44 = 1f
            });
            e.Graphics.DrawImage(_before,
                new Rectangle(0, 0, Width, Height),
                0, 0, _before.Width, _before.Height,
                GraphicsUnit.Pixel, ia);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _dead = true;
                _before?.Dispose();
                if (_ownsAfter) _after?.Dispose();   // 缓存位图由快照缓存统一管理
            }
            base.Dispose(disposing);
        }
    }

    // =================================================================
    // 无头自测钩子 — 仅供 ServerUI.Headless 测试程序集调用, 不参与界面逻辑
    // (让 UI 布局与页面切换可以在不弹窗、不提权的情况下被验证)
    // =================================================================
    internal static bool HeadlessMode = false;

    internal void HeadlessShowPage(string tag) => ShowPage(tag);

    /* 无头自测: 打开截图淡入/快照切页 (用 DrawToBitmap 抓帧代替屏幕抓取) */
    internal void HeadlessEnableTransition() => _pageFadeReady = true;

    internal int HeadlessPageShotCount => _pageShots.Count;

    internal string HeadlessVisiblePage()
    {
        if (pgDash.Visible) return "dash";
        if (pgArc.Visible) return "archive";
        if (pgDll.Visible) return "dll";
        if (pgTime.Visible) return "time";
        if (pgUpd.Visible) return "about";
        if (pgHelp.Visible) return "help";
        if (pgTsh.Visible) return "tsh";
        return "none";
    }

    /* 无头自测: 只做本地状态刷新 (读 ini / 存档目录), 不联网 */
    internal void HeadlessRefresh()
    {
        CheckDnfExists();
        Rf();
    }

    internal Control[] HeadlessPages => new Control[]
        { pgDash, pgArc, pgDll, pgTime, pgUpd, pgHelp, pgTsh };

    internal string HeadlessDiagnostics()
    {
        var ini = Path.Combine(_gr, "GameGaurd.ini");
        var sb = new StringBuilder();
        sb.AppendLine("_ad=" + _ad);
        sb.AppendLine("_gr=" + _gr);
        sb.AppendLine("ini=" + ini);
        sb.AppendLine("ini-exists=" + File.Exists(ini));
        sb.AppendLine("patch-installed=" + _patchInstalled);
        sb.AppendLine("dll-rows-built=" + _dllRowsBuilt);
        sb.AppendLine("cust-rows=" + _custRows.Count);
        sb.AppendLine("swCust=" + _swCust.Count);
        sb.AppendLine("is-light=" + Config.IsLight);
        sb.AppendLine("theme-text=" + Style.Get(Colour.Text));
        return sb.ToString();
    }

    /*
     * 日志区拖拽分隔条 (LogSplitter)
     * 5px 高的透明条, 悬停/拖动时显示主题色短线, 双击复位 (由调用方处理)
     */
    sealed class LogSplitter : Control
    {
        bool _hot, _drag;
        int _lastY;
        public event Action<int> Dragged;

        public LogSplitter()
        {
            Cursor = Cursors.SizeNS;
            TabStop = false;
            Height = 6;
            SetStyle(ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var color = (_hot || _drag) ? Style.Get(Colour.Primary) : Style.Get(Colour.Split);
            using var pen = new Pen(color, 1f);
            int y = Height / 2;
            int w = Math.Min(56, Math.Max(16, Width / 12));
            e.Graphics.DrawLine(pen, (Width - w) / 2, y, (Width + w) / 2, y);
        }

        protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { _drag = true; _lastY = Cursor.Position.Y; Capture = true; Invalidate(); }
            base.OnMouseDown(e);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_drag)
            {
                int y = Cursor.Position.Y;
                int dy = y - _lastY;
                if (dy != 0) { _lastY = y; Dragged?.Invoke(dy); }
            }
            base.OnMouseMove(e);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_drag) { _drag = false; Capture = false; Invalidate(); }
            base.OnMouseUp(e);
        }
    }
}