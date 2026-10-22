using System;
using System.Threading.Tasks;
using ServerUI;

namespace ServerUI.Web.Bridge;

/// <summary>
/// 系统时间页（移植 MainForm.Time.cs 的非 UI 逻辑）。
/// 写系统时间需要管理员权限：非管理员时通过 TimeTool.RunElevated 自提权重启自身
/// （--settime / --synctime 由 App.xaml.cs 直接路由到 TimeTool，不显示窗口）。
/// Phase 1 只提供 bridge 命令；时间页 UI（自绘日期时间选择器）在 Phase 3。
/// </summary>
internal sealed class TimeController
{
    private readonly IUi _ui;
    private bool _syncBusy;

    public TimeController(IUi ui) { _ui = ui; }

    private void L(string m, string level = AppController.LDefault) => _ui.Log(level, m);

    private static string Fmt(DateTime t) => t.ToString("yyyy-MM-dd HH:mm:ss");

    // ================================================================
    // 读取
    // ================================================================

    public object Get()
    {
        var now = DateTime.Now;
        return new
        {
            ok = true,
            now = Fmt(now),
            iso = now.ToString("o"),
            admin = SafeIsAdmin(),
            readOnlyHint = "写入系统时间需要管理员权限"
        };
    }

    private static bool SafeIsAdmin()
    {
        try { return TimeTool.IsAdmin(); } catch { return false; }
    }

    // ================================================================
    // 应用 / 快速调整
    // ================================================================

    public async Task<object> ApplyAsync(string iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return new { ok = false, error = "缺少时间" };
        if (!DateTime.TryParse(iso, out var t)) return new { ok = false, error = "时间格式无法解析: " + iso };
        return await ApplyTimeAsync(t).ConfigureAwait(false);
    }

    public async Task<object> QuickAsync(int year)
    {
        if (year < 1970 || year > 2100) return new { ok = false, error = "年份超出范围" };
        var t = new DateTime(year, 1, 1, 0, 0, 0);
        L(">>> 快速调整: " + Fmt(t), AppController.LInfo);
        return await ApplyTimeAsync(t).ConfigureAwait(false);
    }

    private async Task<object> ApplyTimeAsync(DateTime t)
    {
        var ts = Fmt(t);
        L(">>> 设置系统时间: " + ts, AppController.LInfo);

        if (!SafeIsAdmin())
        {
            if (TimeTool.RunElevated("--settime \"" + ts + "\""))
            {
                await Task.Delay(800).ConfigureAwait(false);
                var r = ShowTimeArgResult("设置结果");
                return new { ok = r.ok, elevated = true, result = r.text };
            }
            L("需要管理员权限才能修改系统时间（用户取消了提权）", AppController.LError);
            return new { ok = false, error = "需要管理员权限（用户取消了提权）" };
        }

        try
        {
            var err = TimeTool.SetLocalTime(t);
            if (string.IsNullOrEmpty(err))
            {
                L(">>> 已设置系统时间: " + ts, AppController.LSuccess);
                return new { ok = true, now = ts };
            }
            L(">>> 设置系统时间失败: " + err, AppController.LError);
            return new { ok = false, error = err };
        }
        catch (Exception ex)
        {
            L(">>> 设置系统时间失败: " + ex.Message, AppController.LError);
            return new { ok = false, error = ex.Message };
        }
    }

    // ================================================================
    // 网络时间同步
    // ================================================================

    public async Task<object> SyncAsync()
    {
        if (_syncBusy)
        {
            L(">>> 网络时间同步正在进行中，请稍候。", AppController.LWarning);
            return new { ok = false, busy = true };
        }

        _syncBusy = true;
        try
        {
            L(">>> 开始同步网络时间...", AppController.LInfo);

            DateTime? ntp = null;
            try { ntp = TimeTool.QueryNtp(); } catch { }

            if (ntp == null)
            {
                var err = TimeTool.ResyncViaW32tm();
                if (string.IsNullOrEmpty(err))
                {
                    L(">>> 已通过 Windows 时间服务（w32tm）同步", AppController.LSuccess);
                    return new { ok = true, via = "w32tm" };
                }
                L(">>> 同步失败（无法连接时间服务器）: " + err, AppController.LError);
                return new { ok = false, error = err };
            }

            var t = ntp.Value;
            L(">>> 已获取网络标准时间: " + Fmt(t), AppController.LInfo);

            if (!SafeIsAdmin())
            {
                if (TimeTool.RunElevated("--synctime"))
                {
                    await Task.Delay(800).ConfigureAwait(false);
                    var r = ShowTimeArgResult("同步结果");
                    return new { ok = r.ok, elevated = true, via = "ntp", result = r.text };
                }
                L("需要管理员权限才能写入系统时间（用户取消了提权）", AppController.LError);
                return new { ok = false, error = "需要管理员权限（用户取消了提权）" };
            }

            try
            {
                var err = TimeTool.SetLocalTime(t);
                if (string.IsNullOrEmpty(err))
                {
                    L(">>> 网络时间已同步并写入: " + Fmt(t), AppController.LSuccess);
                    return new { ok = true, via = "ntp", now = Fmt(t) };
                }
                L(">>> 写入系统时间失败: " + err, AppController.LError);
                return new { ok = false, error = err };
            }
            catch (Exception ex)
            {
                L(">>> 写入系统时间失败: " + ex.Message, AppController.LError);
                return new { ok = false, error = ex.Message };
            }
        }
        finally { _syncBusy = false; }
    }

    /// <summary>旧 ShowTimeArgResult：读提权子进程写回的结果文件并分类日志。</summary>
    private (bool ok, string text) ShowTimeArgResult(string tag)
    {
        string res = null;
        try { res = TimeTool.ReadResult(); } catch { }
        try { TimeTool.ClearResult(); } catch { }

        if (string.IsNullOrEmpty(res))
        {
            L(">>> " + tag + "：未获取到操作结果", AppController.LError);
            return (false, null);
        }
        if (res.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
        {
            L(">>> " + tag + "成功：" + res, AppController.LSuccess);
            return (true, res);
        }
        L(">>> " + tag + "失败：" + res, AppController.LError);
        return (false, res);
    }
}
