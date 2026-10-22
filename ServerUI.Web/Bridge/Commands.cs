using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace ServerUI.Web.Bridge;

/// <summary>
/// 方法路由表。方法名一律点号命名（server.start / archive.list / dll.apply …）。
/// 只做参数解包与分发，业务逻辑在各自 controller 里。
/// </summary>
internal sealed class Commands
{
    public IUi Ui { get; }
    public SettingsStore Settings { get; }
    public AppController App { get; }
    public ArchiveController Archive { get; }
    public DllController Dll { get; }
    public TimeController Time { get; }

    /// <summary>窗口壳操作（drag/minimize/toggleMaximize/close/quit/navigated）。MainWindow 注入。</summary>
    public Func<string, object> ShellAction { get; set; }

    /// <summary>窗口模式切换（full/mini/classic）。MainWindow 注入；未注入时只回报 full。</summary>
    public Func<string, object> ModeHandler { get; set; }

    /// <summary>当前窗口模式读取（MainWindow 注入）。</summary>
    public Func<string> CurrentMode { get; set; }

    /// <summary>window.mode 支持的取值（顺序即前端按钮顺序）。</summary>
    public static readonly string[] SupportedModes = { "full", "mini", "classic" };

    public Commands(IUi ui, SettingsStore settings)
    {
        Ui = ui;
        Settings = settings;
        App = new AppController(ui, settings);
        Archive = new ArchiveController(ui, App);
        Dll = new DllController(ui);
        Time = new TimeController(ui);
    }

    /// <summary>长任务：先返回句柄，完成后再回包。</summary>
    public sealed class Deferred
    {
        private readonly Task<object> _task;
        public Deferred(Task<object> task) { _task = task; }
        public void ContinueWith(Action<Task<object>> cb) => _task.ContinueWith(cb, TaskScheduler.Default);
        public static Deferred Of(Func<Task<object>> f) => new Deferred(f());
    }

    public object Invoke(string method, JsonElement? rawArgs)
    {
        var a = new Args(rawArgs);

        switch (method)
        {
            // ---------------------------------------------------- 应用 / 窗口
            case "app.init":
                return new
                {
                    version = AppController.Version,
                    theme = Settings.Current.Theme,
                    paths = new { ad = Paths.Ad, gr = Paths.Gr },
                    isAdmin = Paths.IsAdmin,
                    page = "dash",
                    logHeight = Settings.Current.LogHeight,
                    logCollapsed = Settings.Current.LogCollapsed,
                    mode = CurrentMode != null ? CurrentMode() : "full",
                    modes = SupportedModes
                };

            case "app.ready":
                return Shell("ready");

            case "app.quit":
                return Shell("quit");

            case "window.minimize":
                return Shell("minimize");

            case "window.toggleMaximize":
                return Shell("toggleMaximize");

            case "window.close":
                return Shell("close");

            case "window.drag":
                return Shell("drag");

            case "window.isMaximized":
                return Shell("isMaximized");

            // Phase 3b：极简 / 经典模式 —— 几何切换全部在宿主 MainWindow 里做，JS 不改窗口大小
            case "window.mode":
                return WindowMode(a);

            case "settings.get":
                return Settings.Current;

            case "settings.set":
                return SettingsSet(a);

            case "clipboard.write":
                return ClipboardWrite(a);

            // 主题化模态回话（宿主推送 ask 后由前端回传）
            case "dialog.answer":
                return App.AnswerDialog(a.Str("id"), a.Str("result"));

            // ---------------------------------------------------- 服务端 / 状态
            case "server.start":
                return Deferred.Of(() => App.ServerStartAsync());
            case "server.stop":
                return Deferred.Of(() => App.ServerStopAsync());
            case "server.restart":
                return Deferred.Of(() => App.ServerRestartAsync());
            case "server.status":
                App.RefreshStatus(force: true);
                return App.StatusSnapshot();
            case "server.dnfCheck":
                return App.CheckDnf();

            case "dx.set":
                return App.DxSet(a.Str("mode", "off"), a.Bool("watermark"));

            case "shell.open":
                return App.ShellOpen(a.Str("kind"), a.Str("value"));

            // Phase 3a：设置页【进行本地编译】→ 实用工具包脚本
            case "build.local":
                return App.BuildLocal();

            case "gm.launch":
                return Deferred.Of(() => App.LaunchGmToolAsync());

            case "pvf.restore":
                return Deferred.Of(() => App.RestoreOriginalPvfAsync());

            // ---------------------------------------------------- 更新
            case "update.start":
                return Deferred.Of(() => App.UpdateStartAsync(a.Str("mode", "incremental"), a.Bool("skipLog"), a.Bool("mirror"), a.Bool("cleanDb")));
            case "update.cancel":
                return App.UpdateCancel();
            case "update.openLog":
                return App.OpenUpdateLog();
            case "update.check":
                return Deferred.Of(() => App.UpdateCheckAsync());
            case "aum.update":
                return Deferred.Of(() => App.CheckAndUpdateAumAsync(a.Bool("force")));
            case "sdk.install":
                return Deferred.Of(() => App.InstallSdkAsync());

            // ---------------------------------------------------- 存档
            case "archive.list":
                return Archive.List();
            case "archive.store":
                return Archive.Store(a.Str("name"));
            case "archive.import":
                return Archive.Import(a.Str("path"));
            case "archive.importZip":
                return Archive.ImportZip(a.Str("path"));
            case "archive.export":
                return Archive.Export(a.Str("name"), a.Str("path"));
            case "archive.undo":
                return Archive.Undo();
            case "archive.switch":
                return Deferred.Of(() => Archive.SwitchAsync(a.Str("name")));
            case "archive.delete":
                return Archive.Delete(a.Str("name"));
            case "archive.rename":
                return Archive.Rename(a.Str("oldName"), a.Str("newName"));
            case "archive.setSort":
                return Archive.SetSort(a.Bool("ascending", true));
            case "archive.dropFile":
                return Archive.DropFile(a.Str("path"));
            case "archive.cleanRedundant":
                return Archive.CleanRedundant(a.Bool("on"));

            // ---------------------------------------------------- DLL 扩展
            case "dll.state":
                return Dll.State();
            case "dll.previewApply":
                return Dll.PreviewApply(a.StrList("managedOn"), a.StrList("customOn"));
            case "dll.apply":
                return Dll.Apply(a.StrList("managedOn"), a.StrList("customOn"), a.StrList("managedOrder"), a.StrList("customOrder"));
            case "dll.add":
                return Dll.Add(a.Str("path"));
            case "dll.delete":
                return Dll.Delete(a.Str("file"), a.Bool("deleteFile"));
            case "dll.readConfig":
                return Dll.ReadConfig(a.Str("file"));
            case "dll.saveConfig":
                return Dll.SaveConfig(a.Str("file"), a.Str("text"));
            case "dll.installPatch":
                return Deferred.Of(() => Dll.InstallPatchAsync());
            case "dll.refresh":
                return Dll.State();
            case "dll.setOrder":
                return Dll.SetOrder(a.StrList("managedOrder"), a.StrList("customOrder"));

            // ---------------------------------------------------- 时间
            case "time.get":
                return Time.Get();
            case "time.apply":
                return Deferred.Of(() => Time.ApplyAsync(a.Str("iso")));
            case "time.quick":
                return Deferred.Of(() => Time.QuickAsync(a.Int("year", 0)));
            case "time.sync":
                return Deferred.Of(() => Time.SyncAsync());

            // ---------------------------------------------------- 内置文件浏览器（Phase 2）
            // 只读目录枚举，替代系统文件对话框；错误以结构化 { ok:false, error } 返回。
            case "fs.drives":
                return FileBrowser.Drives();
            case "fs.browse":
                return FileBrowser.Browse(a.Str("path"), a.Str("filter", "all"), a.Bool("showHidden"));

            default:
                throw new NotSupportedException("未知方法: " + method);
        }
    }

    private object Shell(string kind) => ShellAction == null
        ? (object)new { ok = false }
        : ShellAction(kind);

    /// <summary>
    /// window.mode：宿主执行几何切换并推送 { mode, maximized }。
    /// 只读（不带 mode 参数）时返回当前模式；非法值回落 full 并给出可读错误。
    /// </summary>
    private object WindowMode(Args a)
    {
        var requested = a.Str("mode");
        var cur = CurrentMode != null ? CurrentMode() : "full";

        // 不带 mode = 只读查询当前模式（前端刷新/宿主同步用），不触发几何切换
        if (string.IsNullOrWhiteSpace(requested))
        {
            return new
            {
                ok = true,
                mode = cur,
                maximized = false,
                supported = SupportedModes
            };
        }

        // 未注入宿主（单元/自检场景）：只回报当前模式，绝不假装切换成功
        if (ModeHandler == null)
        {
            var bad0 = Array.IndexOf(SupportedModes, requested.Trim().ToLowerInvariant()) < 0;
            return new
            {
                ok = !bad0,
                mode = cur,
                maximized = false,
                supported = SupportedModes,
                error = bad0 ? "未知窗口模式：" + requested + "；支持 full / mini / classic" : null
            };
        }

        var res = ModeHandler(requested);
        if (res != null) return res;

        return new
        {
            ok = false,
            mode = cur,
            maximized = false,
            supported = SupportedModes,
            error = "窗口模式切换失败"
        };
    }

    private object SettingsSet(Args a)
    {
        var theme = a.Str("theme");
        if (theme == "dark" || theme == "light")
        {
            Settings.Current.Theme = theme;
            Settings.Save();
            Ui.ThemeChanged(theme);
        }
        if (a.Has("logHeight")) Settings.Current.LogHeight = a.Int("logHeight", 190);
        if (a.Has("logCollapsed")) Settings.Current.LogCollapsed = a.Bool("logCollapsed");
        if (a.Has("watermark")) Settings.Current.Watermark = a.Bool("watermark");
        if (a.Has("dxMode")) Settings.Current.DxMode = a.Str("dxMode", "off");
        if (a.Has("skipLog")) Settings.Current.SkipLog = a.Bool("skipLog");
        if (a.Has("mirror")) Settings.Current.Mirror = a.Bool("mirror");
        if (a.Has("cleanDb")) Settings.Current.CleanRedundantDb = a.Bool("cleanDb");
        Settings.Save();
        return Settings.Current;
    }

    private object ClipboardWrite(Args a)
    {
        var text = a.Str("text") ?? "";
        AppController.TrySetClipboard(text, out var err);
        return err == null ? (object)new { ok = true } : new { ok = false, error = err };
    }
}

/// <summary>args 解包器（容忍缺字段 / 类型不符）。</summary>
internal readonly struct Args
{
    private readonly JsonElement? _el;
    public Args(JsonElement? el) { _el = el; }

    private bool Ok => _el.HasValue && _el.Value.ValueKind == JsonValueKind.Object;

    public bool Has(string name) => Ok && _el.Value.TryGetProperty(name, out _);

    public string Str(string name, string def = null)
    {
        if (!Ok || !_el.Value.TryGetProperty(name, out var v)) return def;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Null => def,
            JsonValueKind.Undefined => def,
            _ => v.ToString()
        };
    }

    public bool Bool(string name, bool def = false)
    {
        if (!Ok || !_el.Value.TryGetProperty(name, out var v)) return def;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase),
            JsonValueKind.Number => v.GetDouble() != 0,
            _ => def
        };
    }

    public int Int(string name, int def = 0)
    {
        if (!Ok || !_el.Value.TryGetProperty(name, out var v)) return def;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return def;
    }

    public List<string> StrList(string name)
    {
        var list = new List<string>();
        if (!Ok || !_el.Value.TryGetProperty(name, out var v)) return list;
        if (v.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrEmpty(s)) list.Add(s);
            }
        }
        return list;
    }
}
