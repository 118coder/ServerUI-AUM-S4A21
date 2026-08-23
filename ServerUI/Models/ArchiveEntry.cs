/*
 * ==================================================================
 * 存档条目数据模型 (ArchiveEntry)
 * ==================================================================
 * 
 * 【功能说明】
 *   描述 AUM管理组件\存档管理\切换库\ 中单个 .db 备份文件的基本信息。
 *   这个类的实例会被放到 ListView 中显示，每行代表一个存档文件。
 *   
 * 【新手修改指南】
 *   如果想在界面上多显示一列（比如"备注"），在这里加一个属性，
 *   然后在 MainForm.cs 的 RA() 方法中把新属性添加到 ListViewItem。
 * 
 * 【字段说明】
 *   Name       — 文件名（含 .db 后缀），如 "初始存档.db"
 *   FullPath   — 完整磁盘路径
 *   Size       — 文件大小（字节数）
 *   Modified   — 文件最后修改时间
 *   SizeDisplay— 智能显示文件大小：>1MB 显示 MB，>1KB 显示 KB，否则显示 B
 * ==================================================================
 */
using System;

namespace ServerUI.Models;

public class ArchiveEntry
{
    // 存档文件名（含 .db 后缀）
    // 示例: "初始存档.db", "全职业存档.db"
    public string Name { get; set; } = "";

    // 存档文件的完整磁盘路径
    // 示例: "E:\Game\DXF\AUM管理组件\存档管理\切换库\初始存档.db"
    public string FullPath { get; set; } = "";

    // 文件大小（字节数，1MB = 1048576 字节）
    // 不能用这个值直接显示，请使用 SizeDisplay 属性
    public long Size { get; set; }

    // 文件最后修改时间
    // 用于列表排序和显示
    public DateTime Modified { get; set; }

    // 智能显示文件大小
    // 逻辑: >= 1MB → 显示 "X.X MB"
    //    >= 1KB → 显示 "X.X KB"  
    //    否则   → 显示 "X B"
    // 修改建议: 可以调整小数位数（F1 → F2 显示两位小数）
    public string SizeDisplay
    {
        get
        {
            if (Size >= 1048576) return $"{(Size / 1048576.0):F1} MB";
            if (Size >= 1024) return $"{(Size / 1024.0):F1} KB";
            return $"{Size} B";
        }
    }
}
