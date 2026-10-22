using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ServerUI.Web.Bridge;

/// <summary>
/// 宿主给前端推送事件的最小接口。controller 只依赖它，不依赖 WebView2。
/// </summary>
internal interface IUi
{
    void Push(string type, object payload);
    void Log(string level, string text);
    void Progress(double value, string label, bool visible);
    void Toast(string level, string text);
    void Status(object payload);
    void ArchiveChanged();
    void DllChanged();
    void ThemeChanged(string theme);
    void FlushNow();
}

/// <summary>
/// 消息总线。协议（与任务书 §5.3 一致）：
///   JS → host : postMessage(JSON.stringify({ id, method, args }))
///   host → JS : { id, ok:true,  result:{...} }
///               { id, ok:false, error:"中文错误" }
///               { type:"log"|"progress"|..., payload:{...} }
/// </summary>
internal sealed class NativeBridge : IUi
{
    public static readonly JsonSerializerOptions Json = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal const string VirtualHost = "app.serverui";
    internal const string StartUrl = "https://app.serverui/index.html";

    private const int LogBatchMs = 40;
    private const int MaxBacklog = 600;

    private readonly WebView2 _web;
    private readonly Dispatcher _dispatcher;
    private readonly string _virtualHostRoot;

    private readonly object _logLock = new object();
    private readonly List<LogItem> _pending = new List<LogItem>();
    private readonly Timer _flushTimer;
    private int _flushScheduled;
    private volatile bool _ready;
    private bool _attached;

    private sealed class LogItem
    {
        public string level { get; set; }
        public string text { get; set; }
        public LogItem(string l, string t) { level = l; text = t; }
    }

    /// <summary>方法路由表；由 MainWindow 在构造控制器后注入。</summary>
    public Commands Router { get; set; }

    /// <summary>窗口壳回调（拖拽/最小化/最大化/关闭）由 MainWindow 注入。</summary>
    public Func<ShellRequest, object> ShellHandler { get; set; }

    public bool IsReady => _ready;

    public NativeBridge(WebView2 web, string virtualHostRoot)
    {
        _web = web ?? throw new ArgumentNullException(nameof(web));
        _dispatcher = web.Dispatcher;
        _virtualHostRoot = virtualHostRoot;
        _flushTimer = new Timer(_ => FlushLogs(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public CoreWebView2 Core => _web?.CoreWebView2;

    // ---------------------------------------------------------------- 生命周期

    /// <summary>应用 §5.2 的 WebView2 设置，挂事件，映射虚拟主机目录。</summary>
    public void Attach(CoreWebView2 core)
    {
        if (core == null || _attached) return;
        _attached = true;

        var s = core.Settings;
        s.AreDevToolsEnabled = Environment.GetEnvironmentVariable("AUM_DEVTOOLS") == "1";
        s.IsStatusBarEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.AreDefaultContextMenusEnabled = false;
        s.IsZoomControlEnabled = false;
        s.IsPinchZoomEnabled = false;
        s.IsSwipeNavigationEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsBuiltInErrorPageEnabled = false;
        s.IsWebMessageEnabled = true;

        core.SetVirtualHostNameToFolderMapping(
            VirtualHost, _virtualHostRoot, CoreWebView2HostResourceAccessKind.Allow);

        core.WebMessageReceived += OnWebMessageReceived;

        // 只允许本地虚拟主机；外部 http(s) 交给系统浏览器。
        core.NavigationStarting += (_, e) =>
        {
            var url = e.Uri ?? "";
            if (url.StartsWith("https://" + VirtualHost + "/", StringComparison.OrdinalIgnoreCase)) return;
            if (url.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase)) return;
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                ShellOpen(url);
            }
            else
            {
                e.Cancel = true;
            }
        };

        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            var url = e.Uri ?? "";
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                ShellOpen(url);
        };

        core.PermissionRequested += (_, e) =>
        {
            e.State = CoreWebView2PermissionState.Deny;
            e.Handled = true;
        };

        core.NavigationCompleted += (_, e) =>
        {
            _ready = true;
            Dispatcher(() => ShellHandler?.Invoke(new ShellRequest { kind = "navigated", value = e.IsSuccess ? "1" : "0" }));
            FlushNow();
        };
    }

    /// <summary>
    /// §5.2 的按键拦截。
    /// 说明：WebView2 的 AcceleratorKeyPressed 事件属于 CoreWebView2Controller，
    /// WPF 的 WebView2 控件未公开该 controller，因此宿主侧改用
    /// Settings.AreBrowserAcceleratorKeysEnabled = false（同一组快捷键：Ctrl+R/F5、
    /// Ctrl+Shift+I/F12、Ctrl+P、Ctrl+U、Ctrl+F、Alt+←/→），并在前端 JS 侧
    /// 对 contextmenu / Ctrl+wheel / 上述组合再兜底 preventDefault。
    /// </summary>
    internal static readonly string[] BlockedAccelerators =
    {
        "Ctrl+R", "F5", "Ctrl+Shift+I", "F12", "Ctrl+P", "Ctrl+U", "Ctrl+F", "Alt+Left", "Alt+Right"
    };

    private static void ShellOpen(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch { }
    }

    // ---------------------------------------------------------------- 收

    private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string body;
        try { body = e.WebMessageAsJson; }
        catch { return; }
        if (string.IsNullOrWhiteSpace(body)) return;

        string id = null;
        string method = null;
        JsonElement args = default;
        var hasArgs = false;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // 参考工程兼容：body 可能是被字符串化的 JSON
            if (root.ValueKind == JsonValueKind.String)
            {
                var inner = root.GetString();
                if (string.IsNullOrWhiteSpace(inner)) return;
                using var doc2 = JsonDocument.Parse(inner);
                root = doc2.RootElement.Clone();
            }

            if (root.ValueKind != JsonValueKind.Object) return;

            if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                id = idEl.GetString();
            else if (root.TryGetProperty("id", out var idNum) && idNum.ValueKind == JsonValueKind.Number)
                id = idNum.GetRawText();

            if (root.TryGetProperty("method", out var mEl) && mEl.ValueKind == JsonValueKind.String)
                method = mEl.GetString();

            if (root.TryGetProperty("args", out var aEl) && aEl.ValueKind == JsonValueKind.Object)
            {
                args = aEl.Clone();
                hasArgs = true;
            }
        }
        catch
        {
            Reply(id, false, null, "请求格式错误（不是合法 JSON）");
            return;
        }

        if (string.IsNullOrEmpty(method))
        {
            Reply(id, false, null, "请求缺少 method");
            return;
        }

        if (Router == null)
        {
            Reply(id, false, null, "宿主未就绪");
            return;
        }

        // 异步执行，UI 线程不阻塞
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (Environment.GetEnvironmentVariable("AUM_OFFSCREEN") == "1")
                App.Diag("cmd " + method + " begin");
            object result;
            try
            {
                result = Router.Invoke(method, hasArgs ? args : (JsonElement?)null);
                if (Environment.GetEnvironmentVariable("AUM_OFFSCREEN") == "1")
                    App.Diag("cmd " + method + " end " + sw.ElapsedMilliseconds + "ms");
            }
            catch (Exception ex)
            {
                Reply(id, false, null, ex.Message);
                return;
            }

            if (result is Commands.Deferred d)
                d.ContinueWith(t =>
                {
                    if (t.IsFaulted) Reply(id, false, null, t.Exception?.GetBaseException().Message ?? "未知错误");
                    else Reply(id, true, t.Result, null);
                });
            else
                Reply(id, true, result, null);
        });
    }

    // ---------------------------------------------------------------- 发

    private void Reply(string id, bool ok, object result, string error)
    {
        if (string.IsNullOrEmpty(id)) return; // 没 id 无法对应 Promise，丢弃
        if (ok) PostRaw(JsonSerializer.Serialize(new { id, ok = true, result }, Json));
        else PostRaw(JsonSerializer.Serialize(new { id, ok = false, error }, Json));
    }

    public void Push(string type, object payload)
    {
        if (string.IsNullOrEmpty(type)) return;
        PostRaw(JsonSerializer.Serialize(new { type, payload }, Json));
    }

    public void Status(object payload) => Push("status", payload);

    public void Toast(string level, string text) => Push("toast", new { level, text });

    public void Progress(double value, string label, bool visible)
        => Push("progress", new { value = Math.Round(value, 1), label, visible });

    public void ArchiveChanged() => Push("archive.changed", new { });
    public void DllChanged() => Push("dll.changed", new { });
    public void ThemeChanged(string theme)
    {
        Push("theme.changed", new { theme });
        try { ThemeChangedEvt?.Invoke(theme); } catch { }
    }

    /// <summary>宿主窗口也要跟随主题（WPF 底色 / 1px 边框 / WebView2 背景色）。</summary>
    public event Action<string> ThemeChangedEvt;

    public void Log(string level, string text)
    {
        if (text == null) return;
        lock (_logLock)
        {
            if (_pending.Count >= MaxBacklog) _pending.RemoveRange(0, _pending.Count - MaxBacklog + 1);
            _pending.Add(new LogItem(level ?? "default", text));
        }
        if (_ready && Interlocked.CompareExchange(ref _flushScheduled, 1, 0) == 0)
            _flushTimer.Change(LogBatchMs, Timeout.Infinite);
    }

    private void FlushLogs()
    {
        Interlocked.Exchange(ref _flushScheduled, 0);
        LogItem[] batch;
        lock (_logLock)
        {
            if (_pending.Count == 0) return;
            batch = _pending.ToArray();
            _pending.Clear();
        }
        if (batch.Length == 1) Push("log", new { level = batch[0].level, text = batch[0].text });
        else Push("log", new { items = batch });
    }

    /// <summary>把积压日志立刻送出（导航完成 / 退出前调用）。</summary>
    public void FlushNow()
    {
        Interlocked.Exchange(ref _flushScheduled, 0);
        FlushLogs();
    }

    private void PostRaw(string json)
    {
        if (string.IsNullOrEmpty(json)) return;
        Dispatcher(() =>
        {
            try
            {
                var core = _web?.CoreWebView2;
                if (core != null) core.PostWebMessageAsJson(json);
            }
            catch { }
        });
    }

    private void Dispatcher(Action action)
    {
        try
        {
            if (_dispatcher == null || _dispatcher.CheckAccess()) action();
            else _dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
        }
        catch { }
    }
}

/// <summary>MainWindow 收到的壳请求（导航完成 / 拖拽 / 关闭等回调）。</summary>
internal sealed class ShellRequest
{
    public string kind { get; set; }
    public string value { get; set; }
}
