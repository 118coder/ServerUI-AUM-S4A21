using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using ServerUI.Models;

namespace ServerUI.Web.Bridge;

/// <summary>
/// 存档管理（移植 MainForm.Archive.cs 的非 UI 逻辑）。
/// 服务端运行时阻止一切存档操作（旧 DoArchiveOp），确认框走前端主题化模态。
/// Phase 1 只提供 bridge 命令；存档页 UI 在 Phase 2。
/// </summary>
internal sealed class ArchiveController
{
    private const int MaxBackupFolders = 10;   // 旧 const int MB = 10

    private readonly IUi _ui;
    private readonly AppController _app;

    /// <summary>列表排序方向（true = 按修改时间正序），对应旧 _sa。</summary>
    private bool _ascending = true;

    public ArchiveController(IUi ui, AppController app)
    {
        _ui = ui;
        _app = app;
    }

    private void L(string m, string level = AppController.LDefault) => _app.L(m, level);
    private void LS(string m) => _app.L(m, AppController.LSuccess);

    // ================================================================
    // 列表
    // ================================================================

    public object List()
    {
        try
        {
            var list = _app.Archives.List(Paths.Ad) ?? new List<ArchiveEntry>();
            var ordered = (_ascending
                ? list.OrderBy(x => x.Modified)
                : list.OrderByDescending(x => x.Modified)).ToList();

            var currentInfo = "";
            var backupCount = 0;
            try { currentInfo = _app.Archives.CurrentInfo(Paths.Ad) ?? ""; } catch { }
            try { backupCount = _app.Archives.BackupCount(Paths.Ad); } catch { }

            // 「使用中」判定：比较切换库文件夹与 Data 目录的 inventory* 文件签名。
            // 旧 UI 没有这个标记，但 Phase 2 任务书要求「当前行高亮」——用内容判定，而不是记一次操作。
            var dataSig = SignatureOf(Paths.DataDir);
            var entries = ordered.Select((x, i) => new
            {
                index = i + 1,
                name = x.Name,
                size = x.Size,
                sizeDisplay = x.SizeDisplay,
                modified = x.Modified.ToString("yyyy-MM-dd HH:mm"),
                modifiedRaw = x.Modified.ToString("o"),
                current = dataSig != null && dataSig == SignatureOf(x.FullPath)
            }).ToList();

            return new
            {
                ok = true,
                current = currentInfo,
                backupCount,
                ascending = _ascending,
                entries
            };
        }
        catch (Exception ex)
        {
            L(">>> [存档管理] 读取列表失败: " + ex.Message, AppController.LError);
            return new { ok = false, error = ex.Message };
        }
    }

    /// <summary>
    /// 目录内 inventory* 文件的签名：文件名 + 长度 + 主库内容哈希。
    /// 目录不存在或没有 inventory 文件时返回 null（表示“不可比”）。
    /// </summary>
    private static string SignatureOf(string dir)
    {
        try
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
            var files = Directory.GetFiles(dir, "inventory*")
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (files.Count == 0) return null;

            var parts = new List<string>(files.Count + 1);
            foreach (var f in files) parts.Add(f.Name.ToLowerInvariant() + ":" + f.Length);

            var main = files.FirstOrDefault(f => f.Name.Equals("inventory.db", StringComparison.OrdinalIgnoreCase));
            if (main != null)
            {
                try
                {
                    using var fs = new FileStream(main.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var sha = SHA256.Create();
                    parts.Add("sha:" + Convert.ToHexString(sha.ComputeHash(fs)));
                }
                catch
                {
                    // 被服务端占用时退化为只比名字+长度
                }
            }
            return string.Join("|", parts);
        }
        catch
        {
            return null;
        }
    }

    public object SetSort(bool ascending)
    {
        _ascending = ascending;
        return List();
    }

    // ================================================================
    // 服务端运行时的统一拦截（旧 DoArchiveOp）
    // ================================================================

    /// <summary>
    /// 服务端运行时的统一拦截（旧 DoArchiveOp）。
    /// 注意：**必须完全非阻塞**。存档命令在 WebView 的 WebMessageReceived（UI 线程）上执行，
    /// 一旦在这里同步等待前端确认框（dialog.answer 也要回 UI 线程），整窗会死锁。
    /// 因此这里只回一个 blocked 结果，由前端弹主题化提示。
    /// </summary>
    private bool Blocked(out object result)
    {
        result = null;
        if (!_app.ServerAlive()) return false;
        L(">>> [存档管理] 服务端运行中，操作已阻止", AppController.LWarning);
        result = new
        {
            ok = false,
            blocked = true,
            error = "目前服务端正在运行，请结束服务端后再使用存档管理相关功能。"
        };
        return true;
    }

    private void AfterOpHint()
    {
        if (!_app.SettingsCleanDbOn())
            L(">>> 已切换存档。如果无法登录服务端或网络连接中断，请勾选【清理冗余DB】后重试。", AppController.LWarning);
    }

    private static string SwitchDirOf(string name) => Path.Combine(Paths.SwitchDir, name);

    // ================================================================
    // 储存 / 切换 / 撤销 / 导入 / 导出 / 删除
    // ================================================================

    public object Store(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return new { ok = false, error = "缺少存档名称" };
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return new { ok = false, error = "存档名称包含不允许的字符（\\ / : * ? \" < > | 等），请换个名称。" };

        if (Blocked(out var blocked)) return blocked;

        try
        {
            _app.Archives.SaveArchive(Paths.Ad, name);
            LS("已储存到切换库: " + name);
            _ui.ArchiveChanged();
            AfterOpHint();
            return new { ok = true, name };
        }
        catch (Exception ex)
        {
            L(">>> [储存存档] 储存失败: " + ex.Message, AppController.LError);
            _ = _app.InfoAsync("储存当前存档", "储存失败：" + ex.Message + "\n\n请确认服务端已停止后再试。", "error");
            return new { ok = false, error = ex.Message };
        }
    }

    /// <summary>
    /// 切换存档。必须 async：简单存档要先弹「是否清理冗余DB」三选一，
    /// 同步等待该确认会与 UI 线程上的 dialog.answer 互锁。
    /// </summary>
    public async System.Threading.Tasks.Task<object> SwitchAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return new { ok = false, error = "缺少存档名称" };
        if (Blocked(out var blocked)) return blocked;

        var path = SwitchDirOf(name);
        if (!Directory.Exists(path)) return new { ok = false, error = "存档路径无效: " + path };

        try
        {
            var dbCount = Directory.GetFiles(path, "*.db").Length;
            if (dbCount == 0)
            {
                L(">>> [切换存档] 没有存档", AppController.LWarning);
                _ = _app.InfoAsync("切换存档", "没有存档", "warning");
                return new { ok = false, error = "没有存档" };
            }

            if (_app.Archives.IsSimpleArchive(Paths.Ad, path))
            {
                var r = await _app.AskYesNoCancelAsync("存档切换",
                    "该存档文件夹内仅有一个.DB的主存档文件，是否执行一次对主目录的冗杂DB清理？").ConfigureAwait(false);
                if (r == "cancel") return new { ok = false, cancelled = true };
                _app.Archives.SwitchToArchive(Paths.Ad, path, cleanRedundantDbFirst: r == "yes");
            }
            else
            {
                _app.Archives.SwitchToArchive(Paths.Ad, path);
            }

            LS("已切换到: " + name);
            PruneBackups();
            if (_app.SettingsCleanDbOn()) _app.CleanRedundantDb();
            _ui.ArchiveChanged();
            AfterOpHint();
            return new { ok = true, name };
        }
        catch (Exception ex)
        {
            L(">>> [切换存档] 切换失败: " + ex.Message, AppController.LError);
            return new { ok = false, error = ex.Message };
        }
    }

    public object Undo()
    {
        if (Blocked(out var blocked)) return blocked;
        try
        {
            var ok = _app.Archives.UndoSwap(Paths.Ad, _app.SettingsCleanDbOn());
            if (ok) { LS("已撤销"); AfterOpHint(); }
            else L(">>> 无备份", AppController.LWarning);
            _ui.ArchiveChanged();
            return new { ok, undone = ok };
        }
        catch (Exception ex)
        {
            L(">>> [撤销切换] 失败: " + ex.Message, AppController.LError);
            return new { ok = false, error = ex.Message };
        }
    }

    public object Import(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
            return new { ok = false, error = "存档路径无效" };
        if (Blocked(out var blocked)) return blocked;

        try
        {
            if (Directory.Exists(path)) _app.Archives.SwitchToArchive(Paths.Ad, path);
            else _app.Archives.Import(Paths.Ad, path);
            LS("已导入: " + Path.GetFileName(path.TrimEnd('\\', '/')));
            _ui.ArchiveChanged();
            AfterOpHint();
            return new { ok = true };
        }
        catch (Exception ex)
        {
            L(">>> [导入存档] 导入失败: " + ex.Message, AppController.LError);
            _ = _app.InfoAsync("导入存档", "导入失败：" + ex.Message + "\n\n请确认 ZIP 文件完整、且服务端已停止。", "error");
            return new { ok = false, error = ex.Message };
        }
    }

    public object ImportZip(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return new { ok = false, error = "ZIP 路径无效" };
        if (Blocked(out var blocked)) return blocked;

        try
        {
            _app.Archives.ImportFromZip(Paths.Ad, path);
            LS("已导入: " + Path.GetFileName(path));
            _ui.ArchiveChanged();
            AfterOpHint();
            return new { ok = true };
        }
        catch (Exception ex)
        {
            L(">>> [导入存档] 导入失败: " + ex.Message, AppController.LError);
            _ = _app.InfoAsync("导入存档", "导入失败：" + ex.Message + "\n\n请确认 ZIP 文件完整、且服务端已停止。", "error");
            return new { ok = false, error = ex.Message };
        }
    }

    public object Export(string name, string zipPath)
    {
        if (string.IsNullOrWhiteSpace(zipPath)) return new { ok = false, error = "缺少导出路径" };
        if (Blocked(out var blocked)) return blocked;

        try
        {
            _app.Archives.ExportAsZip(Paths.Ad, zipPath);
            if (!File.Exists(zipPath))
            {
                L(">>> [导出存档] 导出失败：服务端 Data 目录不存在（尚未部署服务端？）", AppController.LError);
                _ = _app.InfoAsync("导出存档", "导出失败：未找到服务端 Data 目录，请先完成服务端更新。", "warning");
                return new { ok = false, error = "未找到服务端 Data 目录" };
            }
            LS("已导出: " + Path.GetFileName(zipPath));
            return new { ok = true, path = zipPath };
        }
        catch (Exception ex)
        {
            L(">>> [导出存档] 导出失败: " + ex.Message, AppController.LError);
            _ = _app.InfoAsync("导出存档", "导出失败：" + ex.Message, "error");
            return new { ok = false, error = ex.Message };
        }
    }

    /// <summary>Phase 2 提供主题化输入框；此处由调用方直接给新名称。</summary>
    public object Rename(string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName)) return new { ok = false, error = "缺少名称" };
        if (string.Equals(oldName, newName, StringComparison.Ordinal)) return new { ok = true, renamed = false };
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return new { ok = false, error = "名称包含不允许的字符" };

        var src = SwitchDirOf(oldName);
        var dst = SwitchDirOf(newName);
        try
        {
            if (Directory.Exists(src) && !Directory.Exists(dst))
            {
                Directory.Move(src, dst);
                LS("已重命名: " + oldName + " -> " + newName);
                _ui.ArchiveChanged();
                return new { ok = true, renamed = true };
            }
            if (Directory.Exists(dst))
            {
                L("名称已存在", AppController.LWarning);
                return new { ok = false, error = "名称已存在" };
            }
            L("重命名失败", AppController.LWarning);
            return new { ok = false, error = "存档不存在" };
        }
        catch (Exception ex)
        {
            L(">>> [重命名] 失败: " + ex.Message, AppController.LError);
            return new { ok = false, error = ex.Message };
        }
    }

    /// <summary>
    /// 删除存档（Phase 2 存档页的「删除存档」入口；旧 UI 没有这个动作）。
    /// 目录型存档不物理删除，而是整体移入「备份存档」，因此可恢复；
    /// 旧的「同名文件」语义保留，仍然交给 ArchiveService.Delete。
    /// 注意：这里不调用 PruneBackups —— 否则刚移入的备份可能被立刻轮换掉。
    /// </summary>
    public object Delete(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return new { ok = false, error = "缺少存档名称" };
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return new { ok = false, error = "名称非法" };
        if (Blocked(out var blocked)) return blocked;

        try
        {
            var dir = SwitchDirOf(name);
            if (Directory.Exists(dir))
            {
                Directory.CreateDirectory(Paths.BackupDir);
                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var dst = Path.Combine(Paths.BackupDir, "backup_" + stamp);
                var n = 1;
                while (Directory.Exists(dst))
                    dst = Path.Combine(Paths.BackupDir, "backup_" + stamp + "_" + (++n));
                Directory.Move(dir, dst);
                LS("已删除存档: " + name + "（已移入备份库，可恢复）");
                _ui.ArchiveChanged();
                return new { ok = true, movedTo = dst };
            }

            _app.Archives.Delete(Paths.Ad, name);
            LS("已删除存档: " + name);
            _ui.ArchiveChanged();
            return new { ok = true };
        }
        catch (Exception ex)
        {
            L(">>> [删除存档] 失败: " + ex.Message, AppController.LError);
            return new { ok = false, error = ex.Message };
        }
    }

    /// <summary>旧 cbCl.Checked 的读写（设置持久化）。</summary>
    public object CleanRedundant(bool on)
    {
        _app.SetSettingsCleanDb(on);
        if (on) _app.CleanRedundantDb();
        return new { ok = true, cleanRedundantDb = on };
    }

    // ================================================================
    // 备份目录轮换（旧 TB）
    // ================================================================

    internal void PruneBackups()
    {
        try
        {
            if (!Directory.Exists(Paths.BackupDir)) return;
            var dirs = new DirectoryInfo(Paths.BackupDir)
                .GetDirectories("backup_*")
                .OrderByDescending(d => d.CreationTime)
                .ToList();

            while (dirs.Count > MaxBackupFolders)
            {
                try { dirs[dirs.Count - 1].Delete(true); } catch { }
                dirs.RemoveAt(dirs.Count - 1);
            }
        }
        catch { }
    }

    /// <summary>拖入 .db 文件（旧 Dd / De）。</summary>
    public object DropFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new { ok = false, error = "无路径" };
        if (!path.EndsWith(".db", StringComparison.OrdinalIgnoreCase)) return new { ok = false, error = "只接受 .db 文件" };
        if (!File.Exists(path)) return new { ok = false, error = "文件不存在" };
        if (Blocked(out var blocked)) return blocked;

        try
        {
            _app.Archives.Swap(Paths.Ad, path);
            LS("已换挡: " + Path.GetFileName(path));
            if (_app.SettingsCleanDbOn()) _app.CleanRedundantDb();
            _ui.ArchiveChanged();
            AfterOpHint();
            return new { ok = true };
        }
        catch (Exception ex)
        {
            L(">>> [换挡] 失败: " + ex.Message, AppController.LError);
            return new { ok = false, error = ex.Message };
        }
    }
}
