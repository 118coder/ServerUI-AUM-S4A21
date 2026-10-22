using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ServerUI.Web.Bridge;

/// <summary>
/// 内置文件浏览器（对齐参考工程 nova-game-space-desktop\Services\FileBrowser.cs）。
///
///   fs.drives → { ok, drives:[{ name, path, free, total }] }
///   fs.browse → { ok, path, parent, drives, dirs:[{name,path,kind,size,modified}],
///                 files:[...], error }
///
/// 约束（任务书 Phase 2 · A）：
///   · 只读目录枚举，不改动任何文件；
///   · 权限/不存在等错误一律返回结构化 { ok:false, error }，不向外抛异常；
///   · 绝不调用系统文件对话框（OpenFileDialog / SaveFileDialog / IFileOpenDialog）。
/// </summary>
internal static class FileBrowser
{
    public sealed record Entry(string Name, string Path, string Kind, long Size, string Modified);

    /// <summary>可用盘符数组（fs.browse 里也直接内嵌这个数组，前端用 Array.isArray 判定）。</summary>
    private static List<object> DriveList()
    {
        var list = new List<object>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady) continue;
                    var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "本地磁盘" : drive.VolumeLabel;
                    list.Add(new
                    {
                        name = label + " (" + drive.Name.TrimEnd('\\') + ")",
                        path = drive.RootDirectory.FullName,
                        free = drive.AvailableFreeSpace,
                        total = drive.TotalSize
                    });
                }
                catch
                {
                    // 光驱 / 已断开的网络盘查询会抛异常，跳过即可
                }
            }
        }
        catch { }

        if (list.Count == 0)
            list.Add(new { name = "本地磁盘 (C:)", path = @"C:\", free = 0L, total = 0L });

        return list;
    }

    /// <summary>fs.drives → { ok, drives:[...] }</summary>
    public static object Drives() => new { ok = true, drives = DriveList() };

    /// <summary>枚举一个目录。path 为空时落到用户主目录。</summary>
    public static object Browse(string path, string filter, bool showHidden)
    {
        var fallback = path;
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(path)) path = @"C:\";

            var full = Path.GetFullPath(path);
            if (!Directory.Exists(full))
            {
                return new
                {
                    ok = false,
                    path = full,
                    parent = (string)null,
                    drives = DriveList(),
                    dirs = Array.Empty<Entry>(),
                    files = Array.Empty<Entry>(),
                    error = "文件夹不存在或无权限访问"
                };
            }

            var allowed = ExtensionsFor(filter);
            var dirs = new List<Entry>();
            var files = new List<Entry>();

            foreach (var dir in Directory.EnumerateDirectories(full))
            {
                try
                {
                    var info = new DirectoryInfo(dir);
                    if (!showHidden && (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    dirs.Add(new Entry(info.Name, info.FullName, "dir", 0, info.LastWriteTime.ToString("yyyy-MM-dd HH:mm")));
                }
                catch
                {
                    // 单个不可读目录不应中断整次枚举
                }
            }

            foreach (var file in Directory.EnumerateFiles(full))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (!showHidden && (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    var ext = info.Extension.ToLowerInvariant();
                    if (allowed != null && !allowed.Contains(ext)) continue;
                    files.Add(new Entry(info.Name, info.FullName, "file", info.Length, info.LastWriteTime.ToString("yyyy-MM-dd HH:mm")));
                }
                catch
                {
                    // 同上
                }
            }

            dirs.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            files.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));

            return new
            {
                ok = true,
                path = full,
                parent = Directory.GetParent(full)?.FullName,
                drives = DriveList(),
                dirs,
                files,
                error = (string)null
            };
        }
        catch (Exception ex)
        {
            return new
            {
                ok = false,
                path = fallback,
                parent = (string)null,
                drives = DriveList(),
                dirs = Array.Empty<Entry>(),
                files = Array.Empty<Entry>(),
                error = ex.Message
            };
        }
    }

    /// <summary>过滤器 → 允许的扩展名集合；null = 不做扩展名过滤（all / 未知值）。</summary>
    private static HashSet<string> ExtensionsFor(string filter)
    {
        switch ((filter ?? "").Trim().ToLowerInvariant())
        {
            case "dll":
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".dll", ".ini" };
            case "zip":
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".zip" };
            case "db":
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".db" };
            case "image":
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" };
            default:
                return null;
        }
    }
}
