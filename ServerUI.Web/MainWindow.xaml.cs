using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Web.WebView2.Core;
using ServerUI.Web.Bridge;

namespace ServerUI.Web;

/// <summary>
/// 无边框 WPF 壳：WebView2 + Splash + 标题栏拖拽 + 关闭拦截 + 文件拖入。
/// 界面全部由 web\ 前端绘制；宿主只负责窗口行为、WebView2 硬化设置与 bridge。
/// </summary>
public partial class MainWindow : Window
{
    private const string DarkBorder = "#2B2B2B";
    private const string LightBorder = "#E3E3E8";
    /// <summary>深色页面底（老版 Ant Design 深色 #141414）；浅色为纯白。</summary>
    private static readonly Color DarkBg = Color.FromRgb(0x14, 0x14, 0x14);
    private static readonly Color LightBg = Colors.White;

    // ---------------------------------------------------------------- 窗口模式（Phase 3b）
    private const string ModeFull = "full";
    private const string ModeMini = "mini";
    private const string ModeClassic = "classic";
    private static readonly string[] SupportedModes = { ModeFull, ModeMini, ModeClassic };

    private readonly SettingsStore _store = new SettingsStore();
    private NativeBridge _bridge;
    private Commands _commands;
    private string _webRoot;

    /// <summary>当前窗口模式：full / mini / classic（与 window.mode 推送保持同步）。</summary>
    private string _mode = ModeFull;
    /// <summary>进入 mini/classic 前保存的完整模式几何（含最大化状态）。</summary>
    private Rect _fullBounds;
    private WindowState _fullState = WindowState.Normal;
    private bool _hasFullBounds;

    private bool _reallyClosing;
    private bool _splashHidden;
    private bool _splashAnimStarted;
    private bool _splashHoldDone;
    private bool _controllerStarted;
    private bool _processFailedHandled;
    private bool _applyingMode;
    private IntPtr _hwnd;

    /// <summary>离屏验收模式（AUM_OFFSCREEN=1）：窗口放到屏幕外、1% 不透明度、不抢焦点，仅用于 CDP 测试。</summary>
    private readonly bool _offscreen = Environment.GetEnvironmentVariable("AUM_OFFSCREEN") == "1";

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, __) =>
        {
            StartSplashAnimation();
            CaptureSplashSequence();
        };
        if (_offscreen)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -6000;
            Top = -6000;
            ShowInTaskbar = false;
            ShowActivated = false;
            Opacity = 0.01;
        }
        Loaded += async (_, __) => await InitAsync();
    }

    // ================================================================
    // 窗口外观
    // ================================================================

    private void Window_SourceInitialized(object sender, EventArgs e)
    {
        try
        {
            _hwnd = new WindowInteropHelper(this).Handle;
        }
        catch { _hwnd = IntPtr.Zero; }

        // Win11 DWM 圆角 8px；不支持则保持直角（失败静默）
        if (_hwnd != IntPtr.Zero) NativeMethods.TrySetRoundedCorners(_hwnd);

        if (_offscreen && _hwnd != IntPtr.Zero)
        {
            try
            {
                var ex = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
                NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE,
                    ex | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);
                NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, -6000, -6000, 0, 0,
                    NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            }
            catch { }
        }
    }

    private void ApplyTheme(string theme)
    {
        var light = theme == "light";
        var bg = light ? LightBg : DarkBg;
        var border = light ? LightBorder : DarkBorder;

        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(border));
            brush.Freeze();
            RootBorder.BorderBrush = brush;
            RootBorder.Background = new SolidColorBrush(bg);
            RootGrid.Background = new SolidColorBrush(bg);
            Background = new SolidColorBrush(bg);
            // 启动页 / 兜底页与页面底同色，避免主题切换后闪出旧底色
            if (Splash != null) Splash.Background = new SolidColorBrush(bg);
            if (Fallback != null) Fallback.Background = new SolidColorBrush(bg);
            ApplySplashPalette(light);
            Web.DefaultBackgroundColor = light
                ? System.Drawing.Color.White
                : System.Drawing.Color.FromArgb(0x14, 0x14, 0x14);
        }
        catch { }

        if (_hwnd != IntPtr.Zero)
            NativeMethods.TrySetBorderColor(_hwnd, light ? NativeMethods.ColorRef(0xE8, 0xE3, 0xE3) : NativeMethods.ColorRef(0x2B, 0x2B, 0x2B));
    }

    // ================================================================
    // 初始化
    // ================================================================

    private async System.Threading.Tasks.Task InitAsync()
    {
        try
        {
            _store.Load();
            ApplyTheme(_store.Current.Theme);
            RestoreGeometry();
            if (_offscreen) ApplyOffscreenPosition();

            _webRoot = ResolveWebRoot();

            _bridge = new NativeBridge(Web, _webRoot);
            _commands = new Commands(_bridge, _store)
            {
                ShellAction = Shell,
                ModeHandler = SetMode,
                CurrentMode = () => _mode
            };
            _bridge.Router = _commands;
            _bridge.ShellHandler = req => Shell(req.kind);
            _bridge.ThemeChangedEvt += ApplyTheme;

            // 可预期的依赖缺失：用主题化兜底页，不弹系统 MessageBox
            if (Environment.GetEnvironmentVariable("AUM_FAKE_NO_WEBVIEW2") == "1")
            {
                ShowFallback("未检测到 Microsoft Edge WebView2 运行时。" + Environment.NewLine + Environment.NewLine
                    + "请先安装 WebView2 Runtime（Windows 10/11 通常已自带），然后重新启动管理器。");
                return;
            }
            string runtimeVersion = null;
            try { runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
            catch { }
            if (string.IsNullOrWhiteSpace(runtimeVersion))
            {
                ShowFallback("未检测到 Microsoft Edge WebView2 运行时。" + Environment.NewLine + Environment.NewLine
                    + "请点击下方按钮下载安装 WebView2 Runtime，安装完成后重新启动管理器。");
                return;
            }

            var splashDelay = 0;
            if (_offscreen) { try { int.TryParse(Environment.GetEnvironmentVariable("AUM_SPLASH_DELAY_MS"), out splashDelay); } catch { } }
            if (splashDelay > 0) await System.Threading.Tasks.Task.Delay(splashDelay);

            var env = await CreateEnvironmentAsync();
            await Web.EnsureCoreWebView2Async(env);

            // AllowExternalDrop=false：文件拖入交给 WPF 的 Drop（页面拿不到真实路径）
            try { Web.AllowExternalDrop = false; } catch { }

            var core = Web.CoreWebView2;
            if (core == null)
            {
                App.Report(new InvalidOperationException("WebView2 初始化失败（CoreWebView2 为空）"), "WebView2 init");
                return;
            }

            core.ProcessFailed += OnProcessFailed;
            _bridge.Attach(core);

            core.Navigate(NativeBridge.StartUrl);
        }
        catch (Exception ex)
        {
            App.Diag("MainWindow init failed: " + ex);
            ShowFallback("界面初始化失败：" + ex.Message + Environment.NewLine + Environment.NewLine
                + "可尝试重新启动；若仍失败，请查看程序目录下的 ServerUI-崩溃日志.txt。");
        }
    }

    /// <summary>主题化启动失败页（WebView2 缺失 / 初始化异常）。</summary>
    private void ShowFallback(string message)
    {
        try
        {
            FallbackText.Text = message;
            Fallback.Visibility = Visibility.Visible;
            Splash.Visibility = Visibility.Collapsed;
        }
        catch { }
    }

    private void FallbackDownload_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://go.microsoft.com/fwlink/p/?LinkId=2124703",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void FallbackQuit_Click(object sender, RoutedEventArgs e)
    {
        try { QuitNow(); } catch { }
    }

    private async System.Threading.Tasks.Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        var args = new List<string>();

        var cdp = Environment.GetEnvironmentVariable("AUM_CDP_PORT");
        if (!string.IsNullOrWhiteSpace(cdp))
        {
            args.Add("--remote-debugging-port=" + cdp.Trim());
            args.Add("--remote-allow-origins=*");
        }

        if (_offscreen)
        {
            // 离屏窗口被视为被遮挡：阻止 Chromium 节流，保证 CDP 截图有帧
            args.Add("--disable-backgrounding-occluded-windows");
            args.Add("--disable-renderer-backgrounding");
            args.Add("--disable-background-timer-throttling");
        }

        var extra = Environment.GetEnvironmentVariable("AUM_WEBVIEW_ARGS");
        if (!string.IsNullOrWhiteSpace(extra))
        {
            foreach (var part in extra.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                args.Add(part);
        }

        var options = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = args.Count > 0 ? string.Join(" ", args) : null
        };

        var userData = Paths.WebViewData;
        Directory.CreateDirectory(userData);

        // WebView2 用户数据目录固定在 %LOCALAPPDATA%（AUM_WEBVIEW_DATA 可覆盖），绝不放进游戏目录
        return await CoreWebView2Environment.CreateAsync(null, userData, options);
    }

    /// <summary>
    /// 前端资源根目录：AUM_WEB_ROOT 开发覆盖优先；否则从嵌入资源 ServerUI.Web.web.zip
    /// 解压到 %LOCALAPPDATA%\ServerUI.Web\web\&lt;zip 哈希&gt;（内容变化自动换目录并清理旧目录）。
    /// </summary>
    private static string ResolveWebRoot()
    {
        var dev = Paths.DevWebRoot;
        if (!string.IsNullOrEmpty(dev))
        {
            App.Diag("web root (dev override): " + dev);
            return dev;
        }

        const string resName = "ServerUI.Web.web.zip";
        var asm = Assembly.GetExecutingAssembly();
        byte[] data;
        using (var s = asm.GetManifestResourceStream(resName))
        {
            if (s == null) throw new InvalidOperationException("缺少嵌入资源: " + resName);
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            data = ms.ToArray();
        }

        var hash = Convert.ToHexString(SHA256.HashData(data)).Substring(0, 12).ToLowerInvariant();
        var root = Path.Combine(Paths.WebCacheRoot, hash);

        if (!File.Exists(Path.Combine(root, "index.html")))
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            Directory.CreateDirectory(root);

            using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;                    // 目录条目
                var rel = entry.FullName.Replace('\\', '/');
                if (rel.StartsWith("/", StringComparison.Ordinal) || rel.Contains("../", StringComparison.Ordinal)) continue;

                var dst = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                var dir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                entry.ExtractToFile(dst, true);
            }

            if (!File.Exists(Path.Combine(root, "index.html")))
                throw new InvalidOperationException("前端资源解压失败：index.html 缺失");
        }

        // 版本/内容变化 → 清理解压目录（不动 WebView2 用户数据）
        try
        {
            foreach (var d in Directory.GetDirectories(Paths.WebCacheRoot))
            {
                if (string.Equals(d, root, StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(d, true); } catch { }
            }
        }
        catch { }

        App.Diag("web root: " + root);
        return root;
    }

    // ================================================================
    // 窗口几何
    // ================================================================

    private void RestoreGeometry()
    {
        var s = _store.Current;
        try
        {
            if (s.WinW is > 1080 and < 20000 && s.WinH is > 700 and < 20000)
            {
                Width = s.WinW.Value;
                Height = s.WinH.Value;
            }
            if (s.WinX.HasValue && s.WinY.HasValue)
            {
                var vx = SystemParameters.VirtualScreenLeft;
                var vy = SystemParameters.VirtualScreenTop;
                var vw = SystemParameters.VirtualScreenWidth;
                var vh = SystemParameters.VirtualScreenHeight;
                var x = s.WinX.Value;
                var y = s.WinY.Value;
                if (x + 100 > vx && y + 60 > vy && x < vx + vw - 100 && y < vy + vh - 60)
                {
                    Left = x;
                    Top = y;
                    WindowStartupLocation = WindowStartupLocation.Manual;
                }
            }
            if (s.WinMaximized) WindowState = WindowState.Maximized;
        }
        catch { }
    }

    private void SaveGeometry()
    {
        try
        {
            var s = _store.Current;
            if (_mode == ModeFull)
            {
                s.WinMaximized = WindowState == WindowState.Maximized;
                if (WindowState == WindowState.Normal)
                {
                    s.WinX = Left;
                    s.WinY = Top;
                    s.WinW = Width;
                    s.WinH = Height;
                }
            }
            else if (_hasFullBounds)
            {
                // mini/classic 下退出：持久化的是完整模式几何，不是小窗口尺寸
                s.WinMaximized = _fullState == WindowState.Maximized;
                s.WinX = _fullBounds.Left;
                s.WinY = _fullBounds.Top;
                s.WinW = _fullBounds.Width;
                s.WinH = _fullBounds.Height;
            }
            _store.Save();
        }
        catch { }
    }

    // ================================================================
    // 窗口模式（Phase 3b：window.mode → full / mini / classic）
    // 始终是同一个无边框窗口，不新建 Window；缩放/最大化/关闭逻辑不变。
    // ================================================================

    /// <summary>规范化模式名；非法值返回 null（由调用方回落 full）。</summary>
    private static string NormalizeMode(string mode)
    {
        var m = (mode ?? "").Trim().ToLowerInvariant();
        switch (m)
        {
            case "": return null;
            case ModeFull: return ModeFull;
            case ModeMini: return ModeMini;
            case ModeClassic: return ModeClassic;
            default: return null;
        }
    }

    /// <summary>
    /// 由 Commands 的 window.mode 调用。
    /// 返回 { ok, mode, maximized, supported }；未知模式 ok:false 并回落 full。
    /// </summary>
    private object SetMode(string mode)
    {
        return Invoke(() =>
        {
            var target = NormalizeMode(mode);
            if (target == null)
            {
                if (_mode != ModeFull) ApplyMode(ModeFull);
                return (object)new
                {
                    ok = false,
                    mode = _mode,
                    maximized = WindowState == WindowState.Maximized,
                    supported = SupportedModes,
                    error = "未知窗口模式：" + (string.IsNullOrEmpty(mode) ? "(空)" : mode)
                            + "；支持 full / mini / classic，已回到完整模式"
                };
            }

            if (target != _mode) ApplyMode(target);

            return new
            {
                ok = true,
                mode = _mode,
                maximized = WindowState == WindowState.Maximized,
                supported = SupportedModes
            };
        });
    }

    /// <summary>真正切换几何 + 通知前端。</summary>
    private void ApplyMode(string mode)
    {
        _applyingMode = true;
        try
        {
            // 1) 离开 full 前记录几何（最大化时用 RestoreBounds，避免把最大化尺寸当成普通尺寸）
            if (_mode == ModeFull) SaveFullBounds();

            // 2) 先退出最大化/最小化，否则最小尺寸与尺寸设置不会生效
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

            // 3) 目标模式几何
            switch (mode)
            {
                case ModeMini:
                    // 先降最小尺寸再设尺寸，避免被 1080×700 夹住
                    MinWidth = 600;
                    MinHeight = 420;
                    Width = 620;
                    Height = 470;
                    ClampToVirtualScreen();
                    break;

                case ModeClassic:
                    MinWidth = 1080;
                    MinHeight = 700;
                    if (_hasFullBounds && _fullBounds.Width >= 1080 && _fullBounds.Height >= 700
                        && _fullBounds.Width < 20000 && _fullBounds.Height < 20000)
                    {
                        Width = _fullBounds.Width;
                        Height = _fullBounds.Height;
                        Left = _fullBounds.Left;
                        Top = _fullBounds.Top;
                    }
                    else
                    {
                        Width = 1280;
                        Height = 840;
                    }
                    ClampToVirtualScreen();
                    break;

                default: // full
                    MinWidth = 1080;
                    MinHeight = 700;
                    RestoreFullBounds();
                    break;
            }

            _mode = mode;
        }
        catch (Exception ex)
        {
            App.Report(ex, "SetMode(" + mode + ")");
        }
        finally
        {
            _applyingMode = false;
        }

        // 4) 推送 { mode, maximized }，与前端 data-mode 保持同步
        try { _bridge?.Push("window.mode", new { mode = _mode, maximized = WindowState == WindowState.Maximized }); } catch { }

        App.Diag("window mode=" + _mode + " " + Width + "x" + Height + " min=" + MinWidth + "x" + MinHeight);
    }

    private void SaveFullBounds()
    {
        _fullState = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
        var b = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        if (b.Width < 100 || b.Height < 100 || double.IsNaN(b.Width) || double.IsNaN(b.Height))
            b = new Rect(Left, Top, 1280, 840);

        _fullBounds = b;
        _hasFullBounds = true;
    }

    private void RestoreFullBounds()
    {
        if (!_hasFullBounds) return;

        var b = _fullBounds;
        if (b.Left > -30000 && b.Top > -30000)
        {
            Left = b.Left;
            Top = b.Top;
        }
        if (b.Width >= 1080 && b.Height >= 700 && b.Width < 20000 && b.Height < 20000)
        {
            Width = b.Width;
            Height = b.Height;
        }
        else
        {
            Width = 1280;
            Height = 840;
        }

        ClampToVirtualScreen();
        if (_fullState == WindowState.Maximized) WindowState = WindowState.Maximized;
    }

    /// <summary>离屏验收时保持屏幕外位置，且不参与屏幕钳制。</summary>
    private void ApplyOffscreenPosition()
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -6000;
        Top = -6000;
        ShowInTaskbar = false;
        Opacity = 0.01;
        if (_hwnd != IntPtr.Zero)
        {
            try
            {
                NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, -6000, -6000, 0, 0,
                    NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            }
            catch { }
        }
    }

    /// <summary>保证普通态窗口不会整个跑到屏幕外（最大化时不处理）。</summary>
    private void ClampToVirtualScreen()
    {
        if (_offscreen) { Left = -6000; Top = -6000; return; }
        if (WindowState != WindowState.Normal) return;
        try
        {
            var vx = SystemParameters.VirtualScreenLeft;
            var vy = SystemParameters.VirtualScreenTop;
            var vw = SystemParameters.VirtualScreenWidth;
            var vh = SystemParameters.VirtualScreenHeight;

            if (double.IsNaN(Left) || double.IsNaN(Top)) return;

            var minLeft = vx;
            var maxLeft = Math.Max(vx, vx + vw - Width);
            var minTop = vy;
            var maxTop = Math.Max(vy, vy + vh - Height);

            Left = Math.Min(Math.Max(Left, minLeft), maxLeft);
            Top = Math.Min(Math.Max(Top, minTop), maxTop);
        }
        catch { }
    }

    // ================================================================
    // 壳操作（由 Commands 调用，全部回到 UI 线程执行）
    // ================================================================

    private object Shell(string kind)
    {
        return Invoke(() =>
        {
            switch (kind)
            {
                case "drag":
                    BeginNativeDrag();
                    return (object)new { ok = true };

                case "minimize":
                    WindowState = WindowState.Minimized;
                    return new { ok = true };

                case "toggleMaximize":
                    WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                    return new { ok = true, maximized = WindowState == WindowState.Maximized };

                case "isMaximized":
                    return new { ok = true, maximized = WindowState == WindowState.Maximized };

                case "close":
                    RequestClose();
                    return new { ok = true };

                case "quit":
                    QuitNow();
                    return new { ok = true };

                case "ready":
                case "navigated":
                    HideSplash();
                    // 前端就绪后同步一次模式（页面刷新/重连后 data-mode 不会丢）
                    try { _bridge?.Push("window.mode", new { mode = _mode, maximized = WindowState == WindowState.Maximized }); } catch { }
                    _ = StartControllerAsync();
                    return new { ok = true };

                default:
                    return new { ok = false, error = "未知壳操作: " + kind };
            }
        });
    }

    private object Invoke(Func<object> action)
    {
        try
        {
            if (Dispatcher.CheckAccess()) return action();
            return Dispatcher.Invoke(action);
        }
        catch { return new { ok = false }; }
    }

    private System.Threading.Tasks.Task StartControllerAsync()
    {
        if (_controllerStarted || _commands == null) return System.Threading.Tasks.Task.CompletedTask;
        _controllerStarted = true;
        return System.Threading.Tasks.Task.Run(async () =>
        {
            try { await _commands.App.StartAsync(); } catch { }
        });
    }

    private void BeginNativeDrag()
    {
        try
        {
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(new WindowInteropHelper(this).Handle,
                NativeMethods.WM_NCLBUTTONDOWN, (IntPtr)NativeMethods.HTCAPTION, IntPtr.Zero);
        }
        catch { }
    }

    /// <summary>
    /// 关闭按钮 / Alt+F4 → 通知前端弹主题化确认框（旧 Fc 的确认环节）。
    /// 兜底：WebView 没就绪 / 已崩溃 / 前端没响应时，不能让窗口关不掉。
    /// 只在「宿主通道不可用」时启用 1.5s 后强制退出；正常时仍走主题化确认。
    /// </summary>
    private void RequestClose()
    {
        if (_reallyClosing) return;

        var bridgeOk = false;
        try { bridgeOk = _bridge != null && _bridge.IsReady; } catch { }

        if (bridgeOk)
        {
            try { _bridge.Push("ask-close", new { }); } catch { bridgeOk = false; }
        }

        if (bridgeOk) return;

        // 通道不可用：1.5s 后仍未进入退出流程就直接退出，避免无响应窗口
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_reallyClosing) QuitNow();
        };
        timer.Start();
    }

    /// <summary>前端确认后调用（app.quit）：执行旧 Fc 的清理序列并退出。</summary>
    private void QuitNow()
    {
        if (_reallyClosing) return;
        _reallyClosing = true;
        try
        {
            SaveGeometry();
            _commands?.App.CancelPendingDialogs();
            _commands?.App.CleanupAndExit();
        }
        catch { }

        try { Web?.Dispose(); } catch { }
        try { Close(); } catch { }
        try { Application.Current?.Shutdown(); } catch { }
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_reallyClosing) return;
        e.Cancel = true;
        RequestClose();
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        try
        {
            var maximized = WindowState == WindowState.Maximized;
            _bridge?.Push("window.state", new { maximized });
            // 模式推送同时带上最大化状态，保持 { mode, maximized } 与窗口一致
            // （ApplyMode 自身会推送一次，这里跳过避免重复）
            if (!_applyingMode) _bridge?.Push("window.mode", new { mode = _mode, maximized });
        }
        catch { }
    }

    // ================================================================
    // 文件拖入（页面拿不到真实路径，必须由宿主取）
    // ================================================================

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        try
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths == null || paths.Length == 0) return;
            _bridge?.Push("drop", new { paths });
        }
        catch { }
    }

    // ================================================================
    // 渲染进程失败 → 自动 Reload 一次（§5.2）
    // ================================================================

    private void OnProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (e.ProcessFailedKind != CoreWebView2ProcessFailedKind.RenderProcessExited) return;
        if (_processFailedHandled) return;
        _processFailedHandled = true;

        try
        {
            Dispatcher.Invoke(() =>
            {
                try { _bridge?.Toast("warning", "界面渲染进程异常退出，正在自动重载…"); } catch { }
                try { Web.Reload(); } catch { }
                System.Threading.Tasks.Task.Delay(8000).ContinueWith(_ => _processFailedHandled = false);
            });
        }
        catch { }
    }

    // ================================================================
    // Splash
    // ================================================================

    /// <summary>
    /// 测试钩子（仅 AUM_SPLASH_SNAPSHOT=1）：把启动页在若干时间点渲染成 PNG，
    /// 供离屏验收检查动画形状。RenderTargetBitmap 直接渲染视觉树，与窗口 1% 不透明度无关。
    /// </summary>
    private void CaptureSplashSequence()
    {
        var timingOnly = Environment.GetEnvironmentVariable("AUM_SPLASH_TIMING") == "1";
        if (Environment.GetEnvironmentVariable("AUM_SPLASH_SNAPSHOT") != "1" && !timingOnly) return;
        var dir = Environment.GetEnvironmentVariable("AUM_SPLASH_SNAPSHOT_DIR");
        if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(Path.GetTempPath(), "serverui-splash");
        try { Directory.CreateDirectory(dir); } catch { return; }

        // 用渲染帧回调采样：既保证截图落在真实帧上，也能记录帧间隔证明流畅度。
        var marks = new[] { 1, 2, 3, 5, 8, 12, 18, 26, 40, 60, 90, 130 };   // 帧序号
        var frame = 0;
        var stamps = new List<double>();
        var start = DateTime.UtcNow;
        var finished = false;

        void Save(int n, int idx)
        {
            try
            {
                var w = (int)Math.Max(1, ActualWidth);
                var h = (int)Math.Max(1, ActualHeight);
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(Splash.Visibility == Visibility.Visible ? (Visual)Splash : RootBorder);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                using var fs = File.Create(Path.Combine(dir, "splash-frame" + n.ToString("000") + ".png"));
                enc.Save(fs);
            }
            catch { }
        }

        EventHandler onRender = null;
        onRender = (_, __) =>
        {
            if (finished) return;
            var ms = (DateTime.UtcNow - start).TotalMilliseconds;
            stamps.Add(ms);
            frame++;
            var k = Array.IndexOf(marks, frame);
            if (k >= 0 && !timingOnly) Save(frame, k);
            if (ms > 1400)
            {
                finished = true;
                System.Windows.Media.CompositionTarget.Rendering -= onRender;
                try
                {
                    var gaps = new List<double>();
                    for (int i = 1; i < stamps.Count; i++) gaps.Add(stamps[i] - stamps[i - 1]);
                    gaps.Sort();
                    var p95 = gaps.Count > 0 ? gaps[(int)Math.Min(gaps.Count - 1, Math.Round(gaps.Count * 0.95))] : 0;
                    var max = gaps.Count > 0 ? gaps[gaps.Count - 1] : 0;
                    var avg = gaps.Count > 0 ? gaps.Average() : 0;
                    File.WriteAllText(Path.Combine(dir, "splash-timing.txt"),
                        "frames=" + stamps.Count + Environment.NewLine
                        + "avgMs=" + avg.ToString("F2") + Environment.NewLine
                        + "p95Ms=" + p95.ToString("F2") + Environment.NewLine
                        + "maxMs=" + max.ToString("F2") + Environment.NewLine
                        + "over25ms=" + gaps.Where(g => g > 25).Count() + Environment.NewLine,
                        new System.Text.UTF8Encoding(false));
                }
                catch { }
            }
        };
        System.Windows.Media.CompositionTarget.Rendering += onRender;
    }

    /// <summary>启动页配色跟随主题（环 / 标记 / 文字 / 进度条）。</summary>
    private void ApplySplashPalette(bool light)
    {
        try
        {
            var primary = light ? Color.FromRgb(0x21, 0x96, 0xF3) : Color.FromRgb(0x16, 0x68, 0xDC);
            var ring = light ? Color.FromRgb(0xE3, 0xE3, 0xE8) : Color.FromRgb(0x2B, 0x2B, 0x2B);
            var markBg = light ? Colors.White : Color.FromRgb(0x1C, 0x1C, 0x1C);
            var text = light ? Color.FromRgb(0x1F, 0x1F, 0x23) : Color.FromRgb(0xDC, 0xDC, 0xDC);
            var faint = light ? Color.FromRgb(0x6E, 0x6E, 0x78) : Color.FromRgb(0x8C, 0x8C, 0x94);

            var p = new SolidColorBrush(primary); p.Freeze();
            var r = new SolidColorBrush(ring); r.Freeze();
            var mb = new SolidColorBrush(markBg); mb.Freeze();
            var tx = new SolidColorBrush(text); tx.Freeze();
            var ft = new SolidColorBrush(faint); ft.Freeze();

            SplashRing.Stroke = r;
            SplashArc.Stroke = p;
            SplashBar.Background = p;
            SplashTrack.Background = r;
            SplashMark.Background = mb;
            SplashMark.BorderBrush = r;
            SplashMarkText.Foreground = p;
            SplashTitle.Foreground = tx;
            SplashSub.Foreground = ft;
        }
        catch { }
    }

    /// <summary>
    /// 启动动画（约 900ms）：标记浮起 → 标题/副标题字移入 → 进度条展开 → 扫光一圈。
    /// 全部用 Opacity / TranslateTransform / RotateTransform / ScaleTransform（合成层），
    /// 不触发 WPF 布局与重绘；WebView2 初始化期间也不会卡帧。
    /// 已离屏时跳过（避免无意义 GPU 占用）。
    /// </summary>
    private void StartSplashAnimation()
    {
        if (_splashAnimStarted || _splashHidden) return;
        _splashAnimStarted = true;
        if (_offscreen && Environment.GetEnvironmentVariable("AUM_SPLASH_SNAPSHOT") != "1")
        {
            // 离屏测试只需终态，避免动画影响截图时机
            // （截图模式下例外：必须真实播放动画，才能逐帧验收）
            SplashRing.Opacity = 0.75;
            SplashArc.Opacity = 0.9;
            SplashMark.Opacity = 1;
            SplashTitle.Opacity = 1;
            SplashSub.Opacity = 1;
            SplashTrack.Opacity = 1;
            SplashTitleShift.Y = 0;
            SplashSubShift.Y = 0;
            SplashBarScale.ScaleX = 0.12;
            return;
        }

        try
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var soft = new SineEase { EasingMode = EasingMode.EaseInOut };

            // 0) 首帧即有形（环/标记/扫光已在 XAML 里可见），这里只做轻微"落定"，
            //    避免出现先空后有的突兀感。
            var ringFade = new DoubleAnimation(0.7, 0.85, TimeSpan.FromMilliseconds(420))
            { EasingFunction = ease };
            SplashRing.BeginAnimation(OpacityProperty, ringFade);

            // 1) 标记：从 1.06 微缩到 1（落定感）
            var markFade = new DoubleAnimation(0.85, 1, TimeSpan.FromMilliseconds(260))
            { EasingFunction = ease };
            var markScale = new DoubleAnimation(1.06, 1, TimeSpan.FromMilliseconds(460))
            { EasingFunction = ease };
            SplashMark.BeginAnimation(OpacityProperty, markFade);
            SplashMarkScale.BeginAnimation(ScaleTransform.ScaleXProperty, markScale);
            SplashMarkScale.BeginAnimation(ScaleTransform.ScaleYProperty, markScale);

            // 2) 环上扫光：淡入后匀速转一圈（旋转只走合成层）
            var arcFade = new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(220))
            { BeginTime = TimeSpan.FromMilliseconds(120), EasingFunction = ease };
            SplashArc.BeginAnimation(OpacityProperty, arcFade);
            // 扫光：先转一圈进场；若 WebView2 还没就绪（冷启动可能数秒），
            // 继续以固定周期匀速旋转 —— 只动 RotateTransform，合成层动画，用来表示"仍在工作"。
            var spin = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(1200))
            {
                BeginTime = TimeSpan.FromMilliseconds(120),
                EasingFunction = soft,
                RepeatBehavior = RepeatBehavior.Forever
            };
            SplashArcRotate.BeginAnimation(RotateTransform.AngleProperty, spin);

            // 4) 标题 / 副标题：向上位移 + 淡入（8px → 0）
            var titleFade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300))
            { BeginTime = TimeSpan.FromMilliseconds(170), EasingFunction = ease };
            var titleMove = new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(420))
            { BeginTime = TimeSpan.FromMilliseconds(170), EasingFunction = ease };
            SplashTitle.BeginAnimation(OpacityProperty, titleFade);
            SplashTitleShift.BeginAnimation(TranslateTransform.YProperty, titleMove);

            var subFade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300))
            { BeginTime = TimeSpan.FromMilliseconds(250), EasingFunction = ease };
            var subMove = new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(420))
            { BeginTime = TimeSpan.FromMilliseconds(250), EasingFunction = ease };
            SplashSub.BeginAnimation(OpacityProperty, subFade);
            SplashSubShift.BeginAnimation(TranslateTransform.YProperty, subMove);

            // 5) 进度条：轨道淡入 + 蓝色条从 0 拉到 12%（真实进度仍由后续阶段推进）
            var trackFade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240))
            { BeginTime = TimeSpan.FromMilliseconds(320), EasingFunction = ease };
            SplashTrack.BeginAnimation(OpacityProperty, trackFade);
            var barGrow = new DoubleAnimation(0, 0.12, TimeSpan.FromMilliseconds(620))
            { BeginTime = TimeSpan.FromMilliseconds(360), EasingFunction = soft };
            SplashBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, barGrow);
        }
        catch { }
    }

    /// <summary>前端就绪：进度条补到 100% 再整体淡出，避免"跳一下消失"。</summary>
    private void FinishSplash()
    {
        if (_splashHidden) return;
        try
        {
            var barDone = new DoubleAnimation(SplashBarScale.ScaleX, 1, TimeSpan.FromMilliseconds(180))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            SplashBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, barDone);
        }
        catch { }
    }

    private void HideSplash()
    {
        if (_splashHidden) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(HideSplash)); return; }

        // 首次就绪：先把进度条补满，再淡出（整段仍只动合成层属性）
        if (!_splashAnimStarted) { StartSplashAnimation(); }
        FinishSplash();

        // 测试钩子：离屏验收时把启动页多留一会儿，便于逐帧截图（不影响正常启动）
        var hold = 0;
        if (_offscreen)
        {
            try { int.TryParse(Environment.GetEnvironmentVariable("AUM_SPLASH_HOLD_MS"), out hold); } catch { }
        }
        if (hold > 0 && !_splashHoldDone)
        {
            _splashHoldDone = true;
            var t = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(hold)
            };
            t.Tick += (_, _) => { t.Stop(); HideSplash(); };
            t.Start();
            return;
        }

        _splashHidden = true;

        try
        {
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180))
            { BeginTime = TimeSpan.FromMilliseconds(120), EasingFunction = new SineEase { EasingMode = EasingMode.EaseIn } };
            fade.Completed += (_, __) =>
            {
                try
                {
                    Splash.Visibility = Visibility.Collapsed;
                    // 释放动画时钟，避免隐藏层继续持有渲染资源
                    Splash.BeginAnimation(OpacityProperty, null);
                    SplashArcRotate.BeginAnimation(RotateTransform.AngleProperty, null);
                }
                catch { }
            };
            Splash.BeginAnimation(OpacityProperty, fade);
        }
        catch { }
    }
}
