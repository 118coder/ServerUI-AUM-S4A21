/*
 * ==================================================================
 * 存档管理服务 (ArchiveService)
 * ==================================================================
 * 
 * 【功能说明】
 *   提供全部存档操作的核心逻辑：列出、备份、切换、导入、导出、撤销。
 *   所有操作围绕 inventory.db 这个文件，它是服务端的玩家数据库。
 *   v1.918: 切换库改为文件夹结构，支持ZIP导入导出，杂DB与主DB协同管理。
 * 
 * 【目录结构 v1.918】
 *   AUM管理组件\
 *     ├── 存档管理\
 *     │   ├── 切换库\           ← SwitchDir — 用户保存的存档文件夹(每个存档一个文件夹)
 *     │   │   ├── 存档名称1\
 *     │   │   │   ├── inventory.db       ← 主存档 (或任意名称.db)
 *     │   │   │   ├── inventory.db-shm   ← 杂DB
 *     │   │   │   └── inventory.db-wal   ← 杂DB
 *     │   │   └── 存档名称2\
 *     │   └── 备份存档\          ← BackupDir — 换挡时自动备份旧档(每个备份一个文件夹)
 *     │       └── backup_20260715_143052\
 *     │           ├── inventory.db
 *     │           ├── inventory.db-shm
 *     │           └── inventory.db-wal
 *     └── ServerS4A12-AUM\
 *         └── dist\win-x64\
 *             └── Data\
 *                 ├── inventory.db       ← DbPath — 服务端实际读取的主存档
 *                 ├── inventory.db-shm
 *                 └── inventory.db-wal
 * 
 * 【新手修改指南】
 *   - 想改目录名称? 修改 SwitchDir / BackupDir / DbPath 中的路径字符串
 *   - 想改成 MySQL/SQL Server 存储? 需要大幅重构，不建议新手尝试
 *   - 想加新的存档操作? 参考 SwitchToArchive() 的模式：备份 → 操作 → 确认
 * 
 * 【重要警告】
 *   操作 inventory.db 前必须先停止服务端，否则可能损坏数据库！
 *   MainForm 中的 DoArchiveOp() 已经处理了自动启停逻辑。
 * ==================================================================
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using ServerUI.Models;

namespace ServerUI.Services;

public class ArchiveService
{
    // ===== 路径计算辅助方法 =====

    private string SwitchDir(string baseDir) =>
        Path.Combine(baseDir, "存档管理", "切换库");

    private string BackupDir(string baseDir) =>
        Path.Combine(baseDir, "存档管理", "备份存档");

    private string DbPath(string baseDir) =>
        Path.Combine(baseDir, "ServerS4A12-AUM", "dist", "win-x64", "Data", "inventory.db");

    private string DataDir(string baseDir) =>
        Path.GetDirectoryName(DbPath(baseDir))!;

    /*
     * 获取 Data 目录下所有 inventory* 文件（主DB + 杂DB）
     */
    private string[] GetDataInventoryFiles(string baseDir)
    {
        var dd = DataDir(baseDir);
        if (!Directory.Exists(dd)) return Array.Empty<string>();
        return Directory.GetFiles(dd, "inventory*");
    }

    /*
     * 判断存档文件夹是否为"简单存档"：
     *   仅有一个 .db 文件，且没有任何杂DB文件（inventory.db-shm / inventory.db-wal 等）
     */
    public bool IsSimpleArchive(string baseDir, string archiveFolder)
    {
        if (!Directory.Exists(archiveFolder)) return false;
        var dbCount = Directory.GetFiles(archiveFolder, "*.db").Length;
        var miscCount = Directory.GetFiles(archiveFolder, "inventory.*")
            .Count(f => !f.EndsWith(".db", StringComparison.OrdinalIgnoreCase));
        return dbCount == 1 && miscCount == 0;
    }

    /*
     * 获取最新备份文件夹的路径（用于撤销换挡时检查）
     */
    public string GetLatestBackupFolder(string baseDir)
    {
        var bak = BackupDir(baseDir);
        if (!Directory.Exists(bak)) return null;
        var dirs = new DirectoryInfo(bak).GetDirectories("backup_*");
        if (dirs.Length == 0) return null;
        return dirs.OrderByDescending(d => d.CreationTime).First().FullName;
    }

    /*
     * 列出切换库中的所有存档文件夹
     * 返回: ArchiveEntry 列表，按修改时间从新到旧排序
     * v1.918: 扫描文件夹内是否有任意 .db 文件，无 .db 则不显示
     */
    public List<ArchiveEntry> List(string baseDir)
    {
        var dir = SwitchDir(baseDir);
        var list = new List<ArchiveEntry>();
        if (!Directory.Exists(dir)) return list;

        foreach (var d in new DirectoryInfo(dir).GetDirectories())
        {
            // 检查文件夹内是否有任意 .db 文件
            var dbFiles = d.GetFiles("*.db");
            if (dbFiles.Length == 0) continue;

            var allFiles = d.GetFiles();
            list.Add(new ArchiveEntry
            {
                Name = d.Name,
                FullPath = d.FullName,
                Size = allFiles.Sum(f => f.Length),
                Modified = d.LastWriteTime
            });
        }

        list.Sort((a, b) => b.Modified.CompareTo(a.Modified));
        return list;
    }

    /*
     * 统计备份存档数量（按文件夹计数）
     */
    public int BackupCount(string baseDir)
    {
        var dir = BackupDir(baseDir);
        if (!Directory.Exists(dir)) return 0;
        return new DirectoryInfo(dir).GetDirectories("backup_*").Length;
    }

    /*
     * 获取当前 inventory.db 的信息
     */
    public string CurrentInfo(string baseDir)
    {
        var db = DbPath(baseDir);
        if (!File.Exists(db)) return "N/A";

        var fi = new FileInfo(db);
        if (fi.Length >= 1048576)
            return $"{fi.Name} ({(fi.Length / 1048576.0):F1} MB)";
        else
            return $"{fi.Name} ({(fi.Length / 1024.0):F1} KB)";
    }

    /*
     * 储存当前存档 — 在切换库中创建文件夹，存储所有 inventory* 文件
     */
    public void SaveArchive(string baseDir, string name)
    {
        var destDir = Path.Combine(SwitchDir(baseDir), name);
        Directory.CreateDirectory(destDir);

        foreach (var f in GetDataInventoryFiles(baseDir))
        {
            File.Copy(f, Path.Combine(destDir, Path.GetFileName(f)), true);
        }
    }

    /*
     * 切换存档 — 从存档文件夹恢复到 Data 目录
     * 自动将主 .db 文件重命名为 inventory.db，杂DB文件保持原名
     * cleanRedundantDbFirst: 切换前是否先清理主目录的杂DB
     * 返回: true=切换成功, false=目标文件夹没有 .db 文件
     */
    public bool SwitchToArchive(string baseDir, string archiveFolder, bool cleanRedundantDbFirst = false)
    {
        var dbDir = DataDir(baseDir);
        if (!Directory.Exists(dbDir)) return false;

        // 检查是否有任意 .db 文件
        var dbFiles = Directory.GetFiles(archiveFolder, "*.db");
        if (dbFiles.Length == 0) return false;

        // 如果要求先清理主目录的杂DB
        if (cleanRedundantDbFirst)
        {
            foreach (var f in Directory.GetFiles(dbDir, "inventory*"))
            {
                var nm = Path.GetFileName(f);
                if (!string.Equals(nm, "inventory.db", StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(f); } catch { }
                }
            }
        }

        // 备份当前所有 inventory* 文件
        var bakDir = BackupDir(baseDir);
        var backupFolder = Path.Combine(bakDir, $"backup_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(backupFolder);
        foreach (var f in GetDataInventoryFiles(baseDir))
        {
            try { File.Copy(f, Path.Combine(backupFolder, Path.GetFileName(f)), true); } catch { }
        }

        // 确定主DB文件：优先用 inventory.db，否则用第一个 .db
        var mainDb = dbFiles.FirstOrDefault(f =>
            string.Equals(Path.GetFileName(f), "inventory.db", StringComparison.OrdinalIgnoreCase))
            ?? dbFiles[0];

        // 复制主DB并重命名为 inventory.db
        File.Copy(mainDb, Path.Combine(dbDir, "inventory.db"), true);

        // 复制其他杂DB文件（inventory.db-shm, inventory.db-wal 等）
        // 跳过已被复制为主DB的文件
        var mainDbName = Path.GetFileName(mainDb);
        foreach (var f in Directory.GetFiles(archiveFolder))
        {
            var nm = Path.GetFileName(f);
            if (string.Equals(nm, mainDbName, StringComparison.OrdinalIgnoreCase))
                continue;
            // 只复制 inventory 开头的文件（杂DB）
            if (nm.StartsWith("inventory.", StringComparison.OrdinalIgnoreCase)
                || nm.StartsWith("inventory-", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(f, Path.Combine(dbDir, nm), true);
            }
        }

        return true;
    }

    /*
     * 换挡操作 — 兼容旧的单文件切换（拖拽 .db 使用）
     * 备份所有 inventory* 文件，然后复制来源 .db 为主存档
     */
    public void Swap(string baseDir, string srcPath)
    {
        var bakDir = BackupDir(baseDir);
        Directory.CreateDirectory(bakDir);

        var dbDir = DataDir(baseDir);

        // 备份所有 inventory* 文件
        if (Directory.Exists(dbDir))
        {
            var backupFolder = Path.Combine(bakDir, $"backup_{DateTime.Now:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(backupFolder);
            foreach (var f in Directory.GetFiles(dbDir, "inventory*"))
            {
                try { File.Copy(f, Path.Combine(backupFolder, Path.GetFileName(f)), true); } catch { }
            }
        }

        // 复制来源存档为主存档
        var db = DbPath(baseDir);
        File.Copy(srcPath, db, true);
    }

    /*
     * 撤销换挡 — 从最新的备份文件夹恢复所有 inventory* 文件
     * cleanRedundantDbFirst: 恢复前是否先清理主目录的杂DB
     * 返回: true=撤销成功, false=没有可用的备份
     */
    public bool UndoSwap(string baseDir, bool cleanRedundantDbFirst = false)
    {
        var bak = BackupDir(baseDir);
        if (!Directory.Exists(bak)) return false;

        var dirs = new DirectoryInfo(bak).GetDirectories("backup_*");
        if (dirs.Length == 0) return false;

        var latest = dirs.OrderByDescending(d => d.CreationTime).First();
        var dbDir = DataDir(baseDir);

        if (cleanRedundantDbFirst)
        {
            foreach (var f in Directory.GetFiles(dbDir, "inventory*"))
            {
                var nm = Path.GetFileName(f);
                if (!string.Equals(nm, "inventory.db", StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(f); } catch { }
                }
            }
        }

        foreach (var f in latest.GetFiles())
        {
            var nm = f.Name;
            // 只恢复 inventory 相关文件
            if (nm.StartsWith("inventory", StringComparison.OrdinalIgnoreCase))
            {
                f.CopyTo(Path.Combine(dbDir, nm), true);
            }
        }
        return true;
    }

    /*
     * 导出当前存档为 ZIP — 包含所有 inventory* 文件
     */
    public void ExportAsZip(string baseDir, string zipPath)
    {
        var dbDir = DataDir(baseDir);
        if (!Directory.Exists(dbDir)) return;

        var tempDir = Path.Combine(Path.GetTempPath(), "AUM_Export_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            foreach (var f in GetDataInventoryFiles(baseDir))
            {
                File.Copy(f, Path.Combine(tempDir, Path.GetFileName(f)), true);
            }

            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(tempDir, zipPath);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /*
     * 导入 ZIP 存档 — 解压覆盖到 Data 目录，同时在切换库创建同名存档文件夹
     */
    public void ImportFromZip(string baseDir, string zipPath)
    {
        var dbDir = DataDir(baseDir);
        if (!Directory.Exists(dbDir)) return;

        var tempDir = Path.Combine(Path.GetTempPath(), "AUM_Import_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            ZipFile.ExtractToDirectory(zipPath, tempDir);

            // 覆盖到主存档目录
            foreach (var f in Directory.GetFiles(tempDir, "inventory*"))
            {
                File.Copy(f, Path.Combine(dbDir, Path.GetFileName(f)), true);
            }

            // 在切换库中创建同名存档文件夹
            var archiveName = Path.GetFileNameWithoutExtension(zipPath);
            var destDir = Path.Combine(SwitchDir(baseDir), archiveName);
            Directory.CreateDirectory(destDir);
            foreach (var f in Directory.GetFiles(tempDir, "inventory*"))
            {
                File.Copy(f, Path.Combine(destDir, Path.GetFileName(f)), true);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /*
     * 导入外部 .db 文件到切换库（兼容旧版）
     */
    public void Import(string baseDir, string srcPath)
    {
        var dest = Path.Combine(SwitchDir(baseDir), Path.GetFileName(srcPath));
        Directory.CreateDirectory(SwitchDir(baseDir));
        File.Copy(srcPath, dest, true);
    }

    /*
     * 从切换库中删除指定存档
     */
    public void Delete(string baseDir, string name)
    {
        var path = Path.Combine(SwitchDir(baseDir), name);
        if (File.Exists(path)) File.Delete(path);
    }
}
