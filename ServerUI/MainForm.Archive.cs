/*
 * ==================================================================
 * MainForm 存档管理部分 (partial class)
 * 包含: 导入/导出/储存/撤销/切换/重命名/清理冗余DB
 *       存档表格交互 (双击切换 / 右键菜单) / 拖拽换挡 / 刷新列表
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
    /*
     * 导入存档 (IA) — 选择 ZIP 压缩包，解压覆盖到主存档目录
     * v2.15: 纳入 DoArchiveOp 统一防护 (旧代码绕过检查, 服务端运行中也能导入导致
     * DB 锁冲突/损坏); 补充异常捕获与导入前自动备份
     */
    internal void IA()
    {
        using var d = new OpenFileDialog { Filter = "ZIP|*.zip" };
        if (d.ShowDialog() != DialogResult.OK) return;
        DoArchiveOp(() =>
        {
            try
            {
                _ar.ImportFromZip(_ad, d.FileName);
            }
            catch (Exception ex)
            {
                Lg(">>> [导入存档] 导入失败: " + ex.Message, Rd);
                MessageBox.Show("导入失败：" + ex.Message
                    + "\n\n请确认 ZIP 文件完整、且服务端已停止。",
                    "导入存档", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            LS("已导入: " + Path.GetFileName(d.FileName));
            RA();
            return true;
        });
    }

    /*
     * 导出当前 (EC) — 把当前 DB 存档 + 杂DB 打包为 ZIP
     * v2.15: 纳入 DoArchiveOp 防护 + 异常捕获; Data 目录缺失时不再假报"已导出"
     */
    internal void EC()
    {
        using var d = new SaveFileDialog
        {
            Filter = "ZIP|*.zip",
            FileName = "存档_" + DateTime.Now.ToString("MMdd_HHmm") + ".zip"
        };
        if (d.ShowDialog() != DialogResult.OK) return;
        DoArchiveOp(() =>
        {
            try
            {
                _ar.ExportAsZip(_ad, d.FileName);
            }
            catch (Exception ex)
            {
                Lg(">>> [导出存档] 导出失败: " + ex.Message, Rd);
                MessageBox.Show("导出失败：" + ex.Message, "导出存档",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            if (!File.Exists(d.FileName))
            {
                Lg(">>> [导出存档] 导出失败：服务端 Data 目录不存在（尚未部署服务端？）", Rd);
                MessageBox.Show("导出失败：未找到服务端 Data 目录，请先完成服务端更新。",
                    "导出存档", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            LS("已导出: " + Path.GetFileName(d.FileName));
            return true;
        });
    }

    /*
     * 储存当前存档 (SC) — 在切换库中新建文件夹，存储当前所有 inventory* 文件
     * v2.15: 纳入 DoArchiveOp 防护 + 名称合法性校验 + 异常捕获
     */
    internal void SC()
    {
        var n = Interaction.InputBox("名称:", "储存当前存档",
            DateTime.Now.ToString("MMdd_HHmm"));
        if (string.IsNullOrWhiteSpace(n)) return;
        if (n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show("存档名称包含不允许的字符（\\ / : * ? \" < > | 等），请换个名称。",
                "储存当前存档", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        DoArchiveOp(() =>
        {
            try
            {
                _ar.SaveArchive(_ad, n);
            }
            catch (Exception ex)
            {
                Lg(">>> [储存存档] 储存失败: " + ex.Message, Rd);
                MessageBox.Show("储存失败：" + ex.Message
                    + "\n\n请确认服务端已停止后再试。",
                    "储存当前存档", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            LS("已储存到切换库: " + n);
            RA();
            return true;
        });
    }

    /*
     * 存档表格双击 (Lv_CellDoubleClick) — 左键双击 → 切换存档
     * 注意: 必须挂在 Table.CellDoubleClick 事件上!
     *       AntdUI 的 Table 在双击时只触发 CellDoubleClick,
     *       不会触发 CellClick (Clicks==2 的 CellClick 永远收不到),
     *       旧代码挂在 CellClick 上判断 e.Clicks==2 导致双击无效
     */
    void Lv_CellDoubleClick(object s, TableClickEventArgs e)
    {
        if (!(e.Record is DataRow row)) return;

        var nm = Convert.ToString(row["Name"]);

        if (e.Button == MouseButtons.Left && !string.IsNullOrEmpty(nm))
        {
            var path = Path.Combine(_ad, "存档管理", "切换库", nm);
            DoArchiveOp(() =>
            {
                if (Directory.GetFiles(path, "*.db").Length == 0)
                {
                    Lg(">>> [切换存档] 没有存档", Or);
                    MessageBox.Show("没有存档", "切换存档",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                if (_ar.IsSimpleArchive(_ad, path))
                {
                    var r = MessageBox.Show(
                        "该存档文件夹内仅有一个.DB的主存档文件，是否执行一次对主目录的冗杂DB清理？",
                        "存档切换",
                        MessageBoxButtons.YesNoCancel,
                        MessageBoxIcon.Question);
                    if (r == DialogResult.Cancel) return false;
                    _ar.SwitchToArchive(_ad, path, cleanRedundantDbFirst: r == DialogResult.Yes);
                }
                else
                {
                    _ar.SwitchToArchive(_ad, path);
                }
                LS("已切换到: " + nm);
                RA();
                TB();
                if (cbCl != null && cbCl.Checked) CleanRedundantDb();
                return true;
            });
        }
    }

    /*
     * 存档表格点击前事件 (Lv_CellClickBegin)
     * 表头点击 → 切换排序方向   数据行右键 → 弹出上下文菜单 (切换/重命名)
     */
    void Lv_CellClickBegin(object s, TableClickBeginEventArgs e)
    {
        // 表头点击: "修改时间" 列切换排序方向
        if (e.RowType == RowType.Column)
        {
            if (e.Column?.Key == "Modified")
            {
                _sa = !_sa;
                RA();
            }
            return;
        }

        // 数据行右键: 弹出上下文菜单
        if (e.Button == MouseButtons.Right && e.Record is DataRow row)
        {
            var nm = Convert.ToString(row["Name"]);
            if (string.IsNullOrEmpty(nm)) return;
            e.Handled = true;
            ShowArchiveMenu(nm);
        }
    }

    /*
     * 存档右键菜单 — 切换存档 / 重命名存档
     */
    void ShowArchiveMenu(string nm)
    {
        var path = Path.Combine(_ad, "存档管理", "切换库", nm);

        var menulist = new AntdUI.IContextMenuStripItem[]
        {
            new AntdUI.ContextMenuStripItem("切换存档")
            {
                IconSvg = "SwapOutlined",
                Tag = "switch"
            },
            new AntdUI.ContextMenuStripItem("重命名存档")
            {
                IconSvg = "EditOutlined",
                Tag = "rename"
            }
        };

        AntdUI.ContextMenuStrip.open(lv, item =>
        {
            var act = (string)item.Tag;
            if (act == "rename")
            {
                var nn = Interaction.InputBox("修改存档名称:", "重命名", nm);
                if (!string.IsNullOrWhiteSpace(nn) && nn != nm)
                {
                    var np = Path.Combine(_ad, "存档管理", "切换库", nn);
                    if (Directory.Exists(path) && !Directory.Exists(np))
                    {
                        Directory.Move(path, np);
                        LS("已重命名: " + nm + " -> " + nn);
                        RA();
                    }
                    else if (Directory.Exists(np))
                        Lg("名称已存在", Color.Gold);
                    else
                        Lg("重命名失败", Color.Gold);
                }
            }
            else if (act == "switch")
            {
                DoArchiveOp(() =>
                {
                    if (Directory.GetFiles(path, "*.db").Length == 0)
                    {
                        Lg(">>> [切换存档] 没有存档", Or);
                        MessageBox.Show("没有存档", "切换存档",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return false;
                    }
                    if (_ar.IsSimpleArchive(_ad, path))
                    {
                        var r = MessageBox.Show(
                            "该存档文件夹内仅有一个.DB的主存档文件，是否执行一次对主目录的冗杂DB清理？",
                            "存档切换",
                            MessageBoxButtons.YesNoCancel,
                            MessageBoxIcon.Question);
                        if (r == DialogResult.Cancel) return false;
                        _ar.SwitchToArchive(_ad, path, cleanRedundantDbFirst: r == DialogResult.Yes);
                    }
                    else
                    {
                        _ar.SwitchToArchive(_ad, path);
                    }
                    LS("已切换到: " + nm);
                    RA();
                    TB();
                    if (cbCl != null && cbCl.Checked) CleanRedundantDb();
                    return true;
                });
            }
        }, menulist);
    }

    /*
     * 清理旧备份 (TB) — 限制备份目录最多保留 MB 个备份文件夹
     */
    internal void TB()
    {
        var bd = Path.Combine(_ad, "存档管理", "备份存档");
        if (!Directory.Exists(bd)) return;

        var dirs = new DirectoryInfo(bd)
            .GetDirectories("backup_*")
            .OrderByDescending(d => d.CreationTime)
            .ToList();

        while (dirs.Count > MB)
        {
            dirs[dirs.Count - 1].Delete(true);
            dirs.RemoveAt(dirs.Count - 1);
        }
    }

    /*
     * 获取服务端 Data 目录路径
     */
    string DataDir() => Path.Combine(_ad, "ServerS4A21-AUM",
        "dist", "win-x64", "Data");

    /*
     * 清理冗余 DB 文件 (CleanRedundantDb)
     * 扫描 Data 目录及所有子目录，删除 inventory* 但保留 inventory.db 本身
     */
    internal void CleanRedundantDb()
    {
        try
        {
            var dd = DataDir();
            if (!Directory.Exists(dd)) return;

            var cleaned = 0;
            foreach (var f in Directory.GetFiles(dd, "inventory*",
                SearchOption.AllDirectories))
            {
                var nm = Path.GetFileName(f);
                if (string.Equals(nm, "inventory.db",
                    StringComparison.OrdinalIgnoreCase))
                    continue;
                try { File.Delete(f); cleaned++; }
                catch { }
            }

            if (cleaned > 0)
                Lg(">>> [清理冗余DB] 已清理 " + cleaned
                    + " 个冗余文件", Gn);
        }
        catch (Exception ex)
        {
            Lg(">>> [清理冗余DB] 清理时出错: " + ex.Message, Or);
        }
    }

    /*
     * 存档操作包装器 (DoArchiveOp) — 服务端运行时阻止存档操作
     * v2.15: 判定改用 ServerAlive() — 手动启动的服务端 (bat 句柄未持有) 同样被拦截
     */
    internal void DoArchiveOp(Func<bool> op)
    {
        if (ServerAlive())
        {
            Lg(">>> [存档管理] 服务端运行中，操作已阻止", Or);
            MessageBox.Show(
                "目前服务端正在运行，请结束服务端后再使用存档管理相关功能。",
                "服务端运行中",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        bool ok = op();

        if (ok && !cbCl.Checked)
        {
            Lg(">>> 已切换存档。如果无法登录服务端或网络连接中断，请勾选【清理冗余DB】后重试。", Or);
        }
    }

    void DoSwapCore(string path, string msg)
    {
        if (Directory.Exists(path))
        {
            _ar.SwitchToArchive(_ad, path);
        }
        else if (File.Exists(path))
        {
            _ar.Swap(_ad, path);
        }
        else
        {
            Lg(">>> 存档路径无效: " + path, Rd);
            return;
        }
        LS(msg);
        RA();
        TB();
        if (cbCl != null && cbCl.Checked) CleanRedundantDb();
    }

    /*
     * 查看更新日志 (SL) — 用记事本打开 更新日志.txt
     */
    internal void SL()
    {
        var lf = Path.Combine(_ad, "更新日志.txt");
        if (File.Exists(lf))
            Process.Start(new ProcessStartInfo { FileName = lf, UseShellExecute = true });
        else
            MessageBox.Show(
                "暂时没有更新日志，请注意查看版本信息。",
                "更新日志", MessageBoxButtons.OK,
                MessageBoxIcon.Information);
    }

    /*
     * 拖入检测 (De) — 只接受单个 .db 文件的拖放
     */
    void De(object s, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var fs = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (fs.Length == 1 && fs[0].EndsWith(".db",
                StringComparison.OrdinalIgnoreCase))
                e.Effect = DragDropEffects.Copy;
        }
    }

    /*
     * 拖放处理 (Dd) — 拖 .db 文件到窗口任意位置均可换挡
     */
    void Dd(object s, DragEventArgs e)
    {
        var fs = (string[])e.Data.GetData(DataFormats.FileDrop);
        Lg(">>> 拖拽换挡: " + Path.GetFileName(fs[0]),
            Color.CornflowerBlue);
        DoArchiveOp(() =>
        {
            DoSwapCore(fs[0], "拖拽换挡完成");
            return true;
        });
    }

    // =================================================================
    // 系统检测 (Ck)
    // =================================================================
    void RA()
    {
        var dt = new DataTable();
        dt.Columns.Add("Index", typeof(string));
        dt.Columns.Add("Name", typeof(string));
        dt.Columns.Add("Size", typeof(string));
        dt.Columns.Add("Modified", typeof(string));

        var list = _ar.List(_ad);
        var o = _sa
            ? list.OrderBy(a => a.Modified).ToList()
            : list.OrderByDescending(a => a.Modified).ToList();

        for (int i = 0; i < o.Count; i++)
        {
            dt.Rows.Add((i + 1).ToString(), o[i].Name,
                o[i].SizeDisplay,
                o[i].Modified.ToString("yyyy-MM-dd HH:mm"));
        }

        lv.DataSource = dt;

        lbCu.Text = "当前: " + _ar.CurrentInfo(_ad);
        lbBk.Text = "备份数: " + _ar.BackupCount(_ad);

        if (cbCl != null && cbCl.Checked) CleanRedundantDb();
    }

    // =================================================================
    // 更新操作 (RI / RF / OU / OD)
    // =================================================================

}