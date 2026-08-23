/*
 * ==================================================================
 * MainForm 页面构建部分 (partial class)
 * 包含: 开始页 / 存档管理页 / 设置与关于页 / 使用说明页 / 日志条
 *       以及页面辅助方法 (LinkRow / ShowHelp / 赛利亚房间修复)
 * 拆分目的: 页面 UI 与业务逻辑分离, 修改单个页面不影响其他部分
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
    // ================================================================
    // ★ P1 概览页 ★ — 状态卡片 + 快捷控制 + 快速更新 + 底部链接
    // ================================================================
    void BuildDash()
    {
        var d = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1, RowCount = 2,
            BackColor = Color.Transparent,
            Padding = new Padding(10)
        };
        d.RowStyles.Add(new RowStyle(SizeType.Absolute, 92F));    // 状态卡片
        d.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));    // 快捷控制 + 快速更新
        pgDash.Controls.Add(d);

        // ============================================================
        // ★ 状态卡片 ×4 ★ — 服务端 / PVF / 版本 / SDK
        // ============================================================
        var stats = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4, RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 6)
        };
        for (int i = 0; i < 4; i++)
            stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
        stats.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var c1 = StatCard("服务端状态", out lbSt);
        var c2 = StatCard("PVF 状态", out lbPv);
        var c3 = StatCard("服务端版本", out lbVe);
        var c4 = StatCard(".NET SDK", out lbSd);
        stats.Controls.Add(c1, 0, 0);
        stats.Controls.Add(c2, 1, 0);
        stats.Controls.Add(c3, 2, 0);
        stats.Controls.Add(c4, 3, 0);
        d.Controls.Add(stats, 0, 0);

        // ============================================================
        // ★ 中部两栏 ★ — 快捷控制 (左) + 快速更新 (右)
        // ============================================================
        var mid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2, RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 6, 0, 6)
        };
        mid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58F));
        mid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42F));
        mid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        d.Controls.Add(mid, 0, 1);

        // ============================================================
        // ★ 快捷控制卡片 ★
        // ============================================================
        var gc = Card(8, 2);
        gc.Padding = new Padding(16, 10, 16, 12);
        var gg = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3, RowCount = 6,
            BackColor = Color.Transparent
        };
        gg.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34F));
        gg.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33F));
        gg.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33F));
        gg.RowStyles.Add(new RowStyle(SizeType.Absolute, 58F));   // 开始游戏
        gg.RowStyles.Add(new RowStyle(SizeType.Absolute, 56F));   // 停止/重启
        gg.RowStyles.Add(new RowStyle(SizeType.Absolute, 52F));   // 可选运行方式 + DX 分段选择器
        gg.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));   // 去除水印开关行
        gg.RowStyles.Add(new RowStyle(SizeType.Absolute, 58F));   // PVF/GM/主存档
        gg.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));   // 占位填充行

        // [开始游戏] — 绿色、加粗、最大最醒目（主操作）
        btPlay = B("开始游戏", TTypeMini.Success, "PlayCircleOutlined");
        btPlay.Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
        btPlay.Click += async (s, e) =>
        {
            Lg(">>> 点击了开始游戏", Color.CornflowerBlue);
            await Play();
        };

        // [停止服务端] — 红色、加粗（危险操作）
        btStop = B("停止服务端", TTypeMini.Error, "StopOutlined");
        btStop.Click += (s, e) =>
        {
            Lg(">>> 点击了停止服务端", Color.Gold);
            System.Threading.Tasks.Task.Run(() =>
            {
                _sv.Stop();
                Invoke(new Action(() =>
                    Lg(">>> 已终止服务端进程树", Color.Gold)));
            });
        };

        // [重启服务端] — 橙色、加粗（中间状态操作）
        btRe = B("重启服务端", TTypeMini.Warn, "ReloadOutlined");
        btRe.Click += async (s, e) =>
        {
            Lg(">>> 点击了重启服务端", Color.CornflowerBlue);
            await System.Threading.Tasks.Task.Run(() => _sv.Stop());
            await System.Threading.Tasks.Task.Delay(1200);
            Go();
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(10000);
                Invoke(new Action(() =>
                { try { _sv.HideConsoleWindow(); } catch { } }));
            });
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(10000);
                Invoke(new Action(() =>
                { try { ServerService.HideDfoServerWindow(); } catch { } }));
            });
        };

        gg.Controls.Add(btPlay, 0, 0);
        gg.SetColumnSpan(btPlay, 3);
        gg.Controls.Add(btStop, 0, 1);
        // [重启服务端] 移到最右列 — 右边缘与上方"开始游戏"按钮右边缘对齐
        gg.Controls.Add(btRe, 2, 1);

        // ============================================================
        // ★ 运行方式行 ★ — 可选运行方式(标签) + 分段选择器
        // 不勾选 DX11/DX12 时 = 默认 DX9 运行(无补丁)
        // 再点击已勾选的项 = 取消选择, 回到默认 DX9
        // ============================================================
        var sgRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2, RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 8, 0, 6)
        };
        sgRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110F));
        sgRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        sgRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        // 纯文字标签: 可选运行方式
        var sgCap = new AntdUI.Label
        {
            Text = "可选运行方式",
            Font = new Font("Microsoft YaHei UI", 9f),
            ForeColor = Color.FromArgb(140, 140, 148),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        sgRow.Controls.Add(sgCap, 0, 0);

        sgDx = new AntdUI.Segmented
        {
            Dock = DockStyle.Fill,
            Cursor = Cursors.Hand
        };
        sgDx.Items.Add(new AntdUI.SegmentedItem { Text = "以DX11方式运行游戏" });
        sgDx.Items.Add(new AntdUI.SegmentedItem { Text = "以DX12方式运行游戏" });
        sgDx.SelectIndexChanged += (s, e) => ApplyDx();
        // 点击已勾选的项 → 取消选择(回到默认DX9)
        sgDx.SelectIndexChanging += (s, e) =>
        {
            if (e.Value == sgDx.SelectIndex)
            {
                sgDx.SelectIndex = -1;
                return false;
            }
            return true;
        };
        sgRow.Controls.Add(sgDx, 1, 0);
        gg.Controls.Add(sgRow, 0, 2);
        gg.SetColumnSpan(sgRow, 3);

        // ============================================================
        // ★ 水印开关行 ★ — 占满整行, 左/右边缘与上方"开始游戏"对齐
        // ============================================================
        cbDw = new AntdUI.Switch { Checked = false, Cursor = Cursors.Hand };
        cbDw.CheckedChanged += (s, e) => ApplyDx();
        var wmRow = SwRow("去除dgVoodooCpl水印", cbDw);
        wmRow.Margin = new Padding(4, 2, 4, 4);
        gg.Controls.Add(wmRow, 0, 3);
        gg.SetColumnSpan(wmRow, 3);

        // ---- 打开目录 / GM 工具 ----
        // [打开PVF目录] — 与"停止服务端"同风格 (红色 Error)
        btPv = B("打开PVF目录", TTypeMini.Error, "FolderOpenOutlined");
        btPv.Click += (s, e) =>
        {
            Lg(">>> 打开PVF目录", Color.CornflowerBlue);
            var dir = Path.Combine(_ad, "ServerS4A21-AUM", "dist",
                "win-x64", "Data", "Pvf");
            if (Directory.Exists(dir))
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = dir, UseShellExecute = true });
            else
                Lg("PVF目录不存在", Color.Gold);
        };

        // [GM工具] — 与"安装NET.10 SDK"同风格 (橙色 Warn), 启动 DfoGmTool 网页管理后台
        btGm = B("GM工具", TTypeMini.Warn, "ToolOutlined");
        btGm.Click += (s, e) => LaunchGmTool();

        btMD = B("打开主存档", TTypeMini.Default, "DatabaseOutlined");
        btMD.Click += (s, e) =>
        {
            var d = Path.Combine(_ad, "ServerS4A21-AUM",
                "dist", "win-x64", "Data");
            if (Directory.Exists(d))
            {
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = d, UseShellExecute = true });
                Lg(">>> 打开了主存档目录", Color.CornflowerBlue);
            }
            else Lg("主存档目录不存在", Color.Gold);
        };

        gg.Controls.Add(btPv, 0, 4);
        gg.Controls.Add(btGm, 1, 4);
        gg.Controls.Add(btMD, 2, 4);
        gc.Controls.Add(gg);
        mid.Controls.Add(gc, 0, 0);

        // ============================================================
        // ★ 快速更新卡片 ★
        // ============================================================
        var gu = Card(8, 2);
        gu.Padding = new Padding(16, 10, 16, 12);
        var ug = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2, RowCount = 5,
            BackColor = Color.Transparent
        };
        ug.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        ug.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        ug.RowStyles.Add(new RowStyle(SizeType.Absolute, 56F));   // 增量/全量
        ug.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));   // 查看更新日志
        ug.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));   // 上次更新
        ug.RowStyles.Add(new RowStyle(SizeType.Absolute, 52F));   // 更新AUM / 安装SDK
        ug.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));   // 占位填充行

        // [增量更新] — 蓝色（推荐，常规操作）— v2.1 起隐藏按钮, 由"开始更新"统一入口
        btIn = B("增量更新", TTypeMini.Primary, "CloudDownloadOutlined");
        btIn.Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold);
        btIn.Visible = false;
        btIn.Click += async (s, e) =>
        {
            Lg(">>> 点击了增量更新", Color.CornflowerBlue);
            await RI();
        };

        // [全量更新] — 橙色（耗时较长，需注意）— v2.1 起隐藏按钮, 逻辑保留(RF 可随时恢复)
        btFu = B("全量更新", TTypeMini.Warn, "CloudSyncOutlined");
        btFu.Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold);
        btFu.Visible = false;
        btFu.Click += async (s, e) =>
        {
            Lg(">>> 点击了全量更新", Color.CornflowerBlue);
            await RF();
        };

        // [开始更新] — 长条主按钮, 日常更新入口(执行增量更新)
        var btStartUpd = B("开始更新", TTypeMini.Primary, "ThunderboltOutlined");
        btStartUpd.Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
        btStartUpd.Click += async (s, e) =>
        {
            Lg(">>> 点击了开始更新", Color.CornflowerBlue);
            await RI();
        };
        ug.Controls.Add(btStartUpd, 1, 0);

        // [查看更新日志] — 中性色
        btVL = B("查看更新日志", TTypeMini.Default, "FileTextOutlined");
        btVL.Click += (s, e) =>
        {
            Lg(">>> 查看更新日志", Color.CornflowerBlue);
            SL();
        };

        // 上次更新信息
        lbLu = L("上次更新: 尚未有log日志无法识别版本，请进行更新", 8.5f, Or);
        lbLu.TextAlign = ContentAlignment.MiddleLeft;
        lbLu.TextMultiLine = true;

        // [更新AUM] — 红色（与打开PVF目录一致）
        btAu = B("更新AUM", TTypeMini.Error, "SyncOutlined");
        btAu.Click += async (s, e) =>
        {
            Lg(">>> 正在检测 AUM 管理器更新...", Color.CornflowerBlue);
            await CheckAndUpdateAUM();
        };

        // [安装SDK] — 橙色（需要用户注意的操作）
        btSdk = B("安装NET.10 SDK", TTypeMini.Warn, "DownloadOutlined");
        btSdk.Click += async (s, e) =>
        {
            Lg(">>> 开始安装 .NET 10 SDK...", Color.CornflowerBlue);
            await IS();
        };

        ug.Controls.Add(btStartUpd, 0, 0);
        ug.SetColumnSpan(btStartUpd, 2);   // 长条按钮横跨整行
        ug.Controls.Add(btVL, 0, 1);
        ug.SetColumnSpan(btVL, 2);
        ug.Controls.Add(lbLu, 0, 2);
        ug.SetColumnSpan(lbLu, 2);
        ug.Controls.Add(btAu, 0, 3);
        ug.Controls.Add(btSdk, 1, 3);
        gu.Controls.Add(ug);
        mid.Controls.Add(gu, 1, 0);
    }

    /*
     * 启动 GM 工具 (LaunchGmTool) — 供主界面与极简模式共用
     */
    internal void LaunchGmTool()
    {
        Lg(">>> 点击了GM工具", Color.Gold);

        var gmp = Path.Combine(_ad, "dfogmtool", "publish",
            "DfoGmTool.exe");
        if (!File.Exists(gmp))
        {
            Lg("GM工具尚未编译, 请先执行一次增量/全量更新", Or);
            return;
        }

        var sb = Path.Combine(_ad, "ServerS4A21-AUM",
            "dist", "win-x64");
        if (!File.Exists(Path.Combine(sb, "Data", "inventory.db"))
            || !File.Exists(Path.Combine(sb, "Data", "Pvf",
                "Script.pvf")))
        {
            Lg("GM工具启动失败: 服务端数据目录(" + sb
                + ")不完整, 请先执行一次更新", Or);
            return;
        }

        try
        {
            foreach (var p in Process.GetProcessesByName(
                "DfoGmTool"))
            { try { p.Kill(); } catch { } }
        }
        catch { }

        var psi = new ProcessStartInfo
        {
            FileName = gmp,
            Arguments = "--server-bin \"" + sb + "\"",
            WorkingDirectory = Path.GetDirectoryName(gmp),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.Environment["DFO_GM_SERVER_BIN"] = sb;
        Process.Start(psi);
        Lg("GM工具已启动 -- 服务端目录: " + sb, Gn);

        System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(3000);
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "http://localhost:5050",
                    UseShellExecute = true
                });
            }
            catch
            {
                Lg("浏览器未能自动打开, 请手动访问"
                    + " http://localhost:5050", Or);
            }
        });
    }

    // ================================================================
    // ★ P2 存档管理页 ★ — 信息栏 / 常用操作 / 目录访问 / 表格 / 拖拽区
    // ================================================================
    void BuildArchive()
    {
        pgArc.Padding = new Padding(10);
        var ag = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1, RowCount = 5,
            BackColor = Color.Transparent
        };
        ag.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));   // 信息栏
        ag.RowStyles.Add(new RowStyle(SizeType.Absolute, 60F));   // 常用操作
        ag.RowStyles.Add(new RowStyle(SizeType.Absolute, 54F));   // 目录访问
        ag.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));   // 存档表格
        ag.RowStyles.Add(new RowStyle(SizeType.Absolute, 62F));   // 拖拽区
        pgArc.Controls.Add(ag);

        // ============================================================
        // ★ 信息栏 ★ — 当前存档 + 备份数 + 清理冗余DB + 刷新
        // ============================================================
        var ib = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5, RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(4, 2, 4, 0)
        };
        ib.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240F));
        ib.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140F));
        ib.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        ib.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170F));
        ib.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120F));
        ib.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        lbCu = L("当前: --", 9f);
        lbBk = L("备份数: 0", 9f);

        // [清理冗余DB] — 勾选后自动清理 Data 目录下的冗余 inventory 文件
        cbCl = new AntdUI.Switch { Checked = false, Cursor = Cursors.Hand };
        cbCl.CheckedChanged += (s, e) =>
        {
            Lg(">>> [清理冗余DB] "
                + (cbCl.Checked ? "已启用" : "已关闭"),
                cbCl.Checked ? Gn : Txt2);
            if (cbCl.Checked) CleanRedundantDb();
        };

        // [刷新存档] — 刷新存档列表
        btRf = B("刷新存档", TTypeMini.Default, "ReloadOutlined");
        btRf.Click += (s, e) =>
        {
            Lg(">>> 刷新存档列表", Color.CornflowerBlue);
            RA();
        };

        ib.Controls.Add(lbCu, 0, 0);
        ib.Controls.Add(lbBk, 1, 0);
        ib.Controls.Add(SwRow("清理冗余DB", cbCl), 3, 0);
        ib.Controls.Add(btRf, 4, 0);
        ag.Controls.Add(ib, 0, 0);

        // ============================================================
        // ★ 常用操作 ★ — 储存当前 / 导入 / 导出 / 撤销
        // 高频操作一行排开，其中"储存当前"用蓝色强调
        // ============================================================
        var row1 = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4, RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(4, 2, 4, 4)
        };
        for (int i = 0; i < 4; i++)
            row1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
        row1.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        btSC = B("储存当前", TTypeMini.Primary, "SaveOutlined");  // 主操作，蓝色强调
        btIm = B("导入存档", TTypeMini.Default, "UploadOutlined");
        btEx = B("导出当前", TTypeMini.Default, "DownloadOutlined");
        btUd = B("撤销换挡", TTypeMini.Default, "UndoOutlined");

        btSC.Click += (s, e) =>
        {
            Lg(">>> 点击了储存当前存档", Color.CornflowerBlue);
            DoArchiveOp(() => { SC(); return true; });
        };
        btIm.Click += (s, e) =>
        {
            Lg(">>> 点击了导入存档", Color.CornflowerBlue);
            DoArchiveOp(() => { IA(); return true; });
        };
        btEx.Click += (s, e) =>
        {
            Lg(">>> 点击了导出当前", Color.CornflowerBlue);
            DoArchiveOp(() => { EC(); return true; });
        };
        btUd.Click += (s, e) =>
        {
            Lg(">>> 点击了撤销换挡", Color.CornflowerBlue);
            DoArchiveOp(() =>
            {
                if (!_ar.UndoSwap(_ad))
                {
                    Lg("无备份", Color.Gold);
                    return false;
                }
                LS("已撤销");
                RA();
                if (cbCl != null && cbCl.Checked) CleanRedundantDb();
                return true;
            });
        };

        row1.Controls.Add(btSC, 0, 0);
        row1.Controls.Add(btIm, 1, 0);
        row1.Controls.Add(btEx, 2, 0);
        row1.Controls.Add(btUd, 3, 0);
        ag.Controls.Add(row1, 0, 1);

        // ============================================================
        // ★ 目录访问 ★ — 打开切换库 / 备份库 / 主存档
        // ============================================================
        var row2 = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3, RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(4, 2, 4, 4)
        };
        for (int i = 0; i < 3; i++)
            row2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / 3F));
        row2.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        btOD = B("打开切换库", TTypeMini.Default, "FolderOpenOutlined");
        btOB = B("打开备份库", TTypeMini.Default, "FolderOpenOutlined");

        // 打开切换存档目录
        btOD.Click += (s, e) =>
        {
            var dir = Path.Combine(_ad, "存档管理", "切换库");
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = dir, UseShellExecute = true });
            Lg(">>> 打开了切换存档目录", Color.CornflowerBlue);
        };
        // 打开备份存档目录
        btOB.Click += (s, e) =>
        {
            var dir = Path.Combine(_ad, "存档管理", "备份存档");
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = dir, UseShellExecute = true });
            Lg(">>> 打开了备份存档目录", Color.CornflowerBlue);
        };

        row2.Controls.Add(btOD, 0, 0);
        row2.Controls.Add(btOB, 1, 0);

        // 存档页使用独立的"打开主存档"按钮 (与概览页的 btMD 互不干扰)
        var btMD2 = B("打开主存档", TTypeMini.Default, "DatabaseOutlined");
        btMD2.Click += (s, e) =>
        {
            var d = Path.Combine(_ad, "ServerS4A21-AUM",
                "dist", "win-x64", "Data");
            if (Directory.Exists(d))
            {
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = d, UseShellExecute = true });
                Lg(">>> 打开了主存档目录", Color.CornflowerBlue);
            }
            else Lg("主存档目录不存在", Color.Gold);
        };
        row2.Controls.Add(btMD2, 2, 0);
        ag.Controls.Add(row2, 0, 2);

        // ============================================================
        // ★ 存档表格 ★ — 序号 / 存档名称 / 大小 / 修改时间
        // 左键双击切换存档 · 右键菜单: 切换/重命名 · 点"修改时间"列头排序
        // ============================================================
        lv = new AntdUI.Table
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(4, 2, 4, 4),
            FixedHeader = true,
            AutoSizeColumnsMode = AntdUI.ColumnsMode.Fill,
            EmptyHeader = false
        };
        lv.Columns = new ColumnCollection
        {
            new Column("Index", "#") { Width = "56", Align = ColumnAlign.Center },
            new Column("Name", "存档名称 (双击切换 · 右键更多)") { Ellipsis = true },
            new Column("Size", "大小") { Width = "90", Align = ColumnAlign.Right },
            new Column("Modified", "修改时间 (点此排序)") { Width = "150", Align = ColumnAlign.Center },
        };
        // ★ 关键: AntdUI 的 Table 双击只会触发 CellDoubleClick 事件,
        //    不会触发 CellClick (双击时 CellClick 被 AntdUI 内部吞掉),
        //    因此切换存档必须挂在 CellDoubleClick 上
        lv.CellDoubleClick += Lv_CellDoubleClick;
        lv.CellClickBegin += Lv_CellClickBegin;
        ag.Controls.Add(lv, 0, 3);

        // ============================================================
        // ★ 拖拽区 ★ — 拖 .db 文件到此处快速换挡; 双击=储存当前
        // ============================================================
        dz = new AntdUI.Panel
        {
            Dock = DockStyle.Fill,
            Radius = 8,
            BorderWidth = 1F,
            Margin = new Padding(4, 2, 4, 0),
            Cursor = Cursors.Hand
        };
        dz.DoubleClick += (s, e) =>
        {
            Lg(">>> 双击拖拽区，储存当前存档", Color.CornflowerBlue);
            DoArchiveOp(() => { SC(); return true; });
        };
        dz.DragEnter += (s, e) =>
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var fs = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (fs.Length == 1 && fs[0].EndsWith(".db",
                    StringComparison.OrdinalIgnoreCase))
                    e.Effect = DragDropEffects.Copy;
            }
        };
        dz.DragDrop += (s, e) =>
        {
            var fs = (string[])e.Data.GetData(DataFormats.FileDrop);
            Lg(">>> 拖拽换挡: " + Path.GetFileName(fs[0]),
                Color.CornflowerBlue);
            DoArchiveOp(() =>
            {
                DoSwapCore(fs[0], "拖拽换挡完成");
                return true;
            });
        };
        dz.AllowDrop = true;
        lbDr = new AntdUI.Label
        {
            Text = "📁 拖拽 .db 文件到此处 = 快速替换存档 (双击 = 储存当前)",
            Font = new Font("Microsoft YaHei UI", 9f),
            ForeColor = Color.FromArgb(130, 130, 130),
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Fill
        };
        dz.Controls.Add(lbDr);
        ag.Controls.Add(dz, 0, 4);
    }

    // ================================================================
    // ★ P3 设置与关于页 ★ — 关于 / 仓库链接 / 实用工具
    // ================================================================
    void BuildAbout()
    {
        var u = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1, RowCount = 4,
            BackColor = Color.Transparent,
            Padding = new Padding(10)
        };
        u.RowStyles.Add(new RowStyle(SizeType.Absolute, 102F));   // 关于
        u.RowStyles.Add(new RowStyle(SizeType.Absolute, 154F));   // 仓库链接 (4 行)
        u.RowStyles.Add(new RowStyle(SizeType.Absolute, 182F));   // 实用工具 (3 个按钮: 编译/赛利亚/安全DLL)
        u.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));    // 占位填充行
        pgUpd.Controls.Add(u);

        // ============================================================
        // ★ 关于 ★
        // ============================================================
        var c1 = Card(8, 2);
        c1.Padding = new Padding(20, 12, 20, 12);
        var g1 = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2, RowCount = 3,
            BackColor = Color.Transparent
        };
        g1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22F));
        g1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 78F));
        g1.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));
        g1.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
        g1.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var appName = new AntdUI.Label
        {
            Text = "ServerS4A21 管理器",
            Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        var verLb = new AntdUI.Label
        {
            Text = "v" + VER + " · ServerS4A21-AUM",
            Font = new Font("Microsoft YaHei UI", 9.5f),
            ForeColor = Color.FromArgb(130, 130, 130),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        var aboutText = new AntdUI.Label
        {
            Text = "基于 Ant Design 设计语言构建的单机服务端管理工具：一键启停游戏服务、存档管理、增量更新、GM 工具等。",
            Font = new Font("Microsoft YaHei UI", 9f),
            ForeColor = Color.FromArgb(140, 140, 148),
            TextAlign = ContentAlignment.TopLeft,
            TextMultiLine = true,
            Dock = DockStyle.Fill
        };
        g1.Controls.Add(appName, 0, 0);
        g1.SetColumnSpan(appName, 2);
        g1.Controls.Add(verLb, 0, 1);
        g1.SetColumnSpan(verLb, 2);
        g1.Controls.Add(aboutText, 0, 2);
        g1.SetColumnSpan(aboutText, 2);
        c1.Controls.Add(g1);
        u.Controls.Add(c1, 0, 0);

        // ============================================================
        // ★ 仓库与链接 ★ — 点击在系统默认浏览器中打开
        // ============================================================
        var c2 = Card(8, 2);
        c2.Padding = new Padding(20, 10, 20, 12);
        var g2 = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2, RowCount = 5,
            BackColor = Color.Transparent
        };
        g2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30F));
        g2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70F));
        g2.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
        g2.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));
        g2.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));
        g2.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));
        g2.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var cap2 = Cap("仓库与链接");
        g2.Controls.Add(cap2, 0, 0);
        g2.SetColumnSpan(cap2, 2);

        // ServerUI-仓库
        var link1 = LinkRow("ServerUI-仓库",
            "github.com/118coder/ServerUI-AUM-S4A21",
            "https://github.com/118coder/ServerUI-AUM-S4A21");
        g2.Controls.Add(link1, 0, 1);
        g2.SetColumnSpan(link1, 2);

        // 86JP-主源仓库
        var link2 = LinkRow("ServerS4A21-主源仓库",
            "gitgud.io/rewio/ServerS4A21",
            "https://gitgud.io/rewio/ServerS4A21");
        g2.Controls.Add(link2, 0, 2);
        g2.SetColumnSpan(link2, 2);

        // GM工具-主源仓库
        var link4 = LinkRow("GM工具-主源仓库",
            "gitgud.io/rewio/S4A21GmTool",
            "https://gitgud.io/rewio/S4A21GmTool");
        g2.Controls.Add(link4, 0, 3);
        g2.SetColumnSpan(link4, 2);

        // GM 工具 (本地)
        var link3 = LinkRow("GM 工具",
            "http://localhost:5050",
            "http://localhost:5050");
        g2.Controls.Add(link3, 0, 4);
        g2.SetColumnSpan(link3, 2);
        c2.Controls.Add(g2);
        u.Controls.Add(c2, 0, 1);

        // ============================================================
        // ★ 实用工具 ★
        // ============================================================
        var c3 = Card(8, 2);
        c3.Padding = new Padding(20, 10, 20, 12);
        var g3 = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3, RowCount = 5,
            BackColor = Color.Transparent
        };
        g3.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190F));
        g3.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        g3.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40F));
        g3.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));   // 分组标题
        g3.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));   // 进行本地编译
        g3.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));   // 修复赛丽亚房间问题
        g3.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));   // 安装新安全DLL
        g3.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));   // 占位填充行

        g3.Controls.Add(Cap("实用工具"), 0, 0);
        g3.SetColumnSpan(Cap("实用工具"), 3);

        // [进行本地编译] — 调用 AUM管理组件\进行本地编译.bat
        var btBuild = B("进行本地编译", TTypeMini.Primary, "BuildOutlined");
        btBuild.Click += (s, e) =>
        {
            var bat = Path.Combine(_ad, "进行本地编译.bat");
            if (!File.Exists(bat))
            {
                Lg("未找到 进行本地编译.bat: " + bat, Rd);
                return;
            }
            Lg(">>> 正在启动本地编译脚本...", Color.CornflowerBlue);
            Process.Start(new ProcessStartInfo
            {
                FileName = bat,
                WorkingDirectory = _ad,
                UseShellExecute = true
            });
        };
        var buildTip = L("调用【AUM管理组件\\进行本地编译.bat】，用【AUM管理组件\\latest\\】的源码包重新编译【服务端】与【GM工具】", 8.5f);
        buildTip.TextAlign = ContentAlignment.MiddleLeft;
        buildTip.TextMultiLine = true;
        g3.Controls.Add(btBuild, 0, 1);
        g3.Controls.Add(buildTip, 1, 1);

        // [修复赛丽亚房间问题] — 样式与"进行本地编译"完全一致(尺寸/图标/字体),
        // 仅文字与颜色不同 (红色 Error)
        var btFix = B("修复赛丽亚房间问题", TTypeMini.Error, "BuildOutlined");
        btFix.Click += (s, e) => FixSeriaRoomConfirm();
        var fixTip = L("如果遇到卡赛丽亚房间问题，请点击该选项，将会替换86JP.dll", 8.5f);
        fixTip.TextAlign = ContentAlignment.MiddleLeft;
        fixTip.TextMultiLine = true;
        g3.Controls.Add(btFix, 0, 2);
        g3.Controls.Add(fixTip, 1, 2);

        // [安装新安全DLL] — 样式与"修复赛丽亚房间问题"一致, 颜色为绿色 (Success)
        // 从 实用工具包\DLL覆盖\ 解压 86JP-DLL安全性补丁.zip 覆盖到游戏根目录;
        // 已存在"已安装"标记文件时跳过 (更新时自动执行)
        var btSec = B("安装新安全DLL", TTypeMini.Success, "SafetyCertificateOutlined");
        btSec.Click += (s, e) => InstallSecurityDll();
        var secTip = L("安装 86JP 安全 DLL 补丁到游戏根目录，防止私服 DLL 泄露隐私", 8.5f);
        secTip.TextAlign = ContentAlignment.MiddleLeft;
        secTip.TextMultiLine = true;
        g3.Controls.Add(btSec, 0, 3);
        g3.Controls.Add(secTip, 1, 3);
        c3.Controls.Add(g3);
        u.Controls.Add(c3, 0, 2);
    }

    /*
     * 扫描 AUM管理组件 根目录残留的 .ps1 文件
     * 历史版本曾在根目录散落 .ps1, 现核心脚本统一在 ps1核心\;
     * 残留文件可能被旧 bat 引用, 干扰核心更新功能的正确性
     */
    List<string> FindLeftoverPs1()
    {
        var list = new List<string>();
        try
        {
            foreach (var f in Directory.GetFiles(_ad, "*.ps1"))
                list.Add(Path.GetFileName(f));
            list.Sort(StringComparer.OrdinalIgnoreCase);
        }
        catch { }
        return list;
    }

    /*
     * 清理残留 .ps1 — 移入 旧版ps1\ 备份 (ps1核心\ 为权威副本, 不直接删除)
     */
    void CleanLeftoverPs1(List<string> files)
    {
        var bak = Path.Combine(_ad, "旧版ps1");
        int moved = 0;
        foreach (var name in files)
        {
            var src = Path.Combine(_ad, name);
            if (!File.Exists(src)) continue;
            try
            {
                Directory.CreateDirectory(bak);
                var dst = Path.Combine(bak, name);
                if (File.Exists(dst)) File.Delete(dst);
                File.Move(src, dst);
                moved++;
                Lg(">>> 已清理残留 PS1: " + name, Gn);
            }
            catch (Exception ex)
            {
                Lg(">>> 清理失败: " + name + " - " + ex.Message, Or);
            }
        }
        Lg(">>> 残留 PS1 清理完成: " + moved + " 个文件已移入 旧版ps1\\", Gn);
    }

    /*
     * 检测并提示清理残留 PS1 (更新/更新AUM 前调用; 无残留则不弹窗)
     */
    void CheckLeftoverPs1Prompt()
    {
        var leftovers = FindLeftoverPs1();
        if (leftovers.Count == 0) return;

        var r = MessageBox.Show(
            "检测到 AUM管理组件 目录下残留 " + leftovers.Count + " 个 .ps1 文件：\n\n"
            + string.Join("\n", leftovers) + "\n\n"
            + "核心脚本已统一整理到【ps1核心】目录，残留文件可能干扰核心更新功能的正确性。\n"
            + "是否将这些残留文件移入【旧版ps1】备份并清理？",
            "检测到残留 PS1 文件",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r == DialogResult.Yes)
            CleanLeftoverPs1(leftovers);
    }

    /*
     * 安装新安全DLL (InstallSecurityDll) — 从 实用工具包\DLL覆盖\ 解压
     * 86JP-DLL安全性补丁.zip 并覆盖到游戏根目录 (DNF.exe 所在目录)
     * 更新时自动调用; 检测到标记文件 86JP-DLL安全性补丁-已安装.txt 则跳过
     */
    internal void InstallSecurityDll()
    {
        var dllDir = Path.Combine(_ad, "实用工具包", "DLL覆盖");
        var zip = Path.Combine(dllDir, "86JP-DLL安全性补丁.zip");
        var marker = Path.Combine(dllDir, "86JP-DLL安全性补丁-已安装.txt");

        if (!File.Exists(zip))
        {
            Lg(">>> [安全DLL] 未找到补丁包: " + zip, Or);
            return;
        }
        if (File.Exists(marker))
        {
            Lg(">>> [安全DLL] 已安装过（检测到标记文件），跳过", Txt2);
            return;
        }

        Lg(">>> [安全DLL] 正在安装新安全 DLL 补丁...", Color.CornflowerBlue);
        try
        {
            int count = 0;
            using (var za = System.IO.Compression.ZipFile.OpenRead(zip))
            {
                foreach (var entry in za.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)
                        || entry.FullName.EndsWith("/")
                        || entry.FullName.EndsWith("\\"))
                        continue;
                    var dst = Path.Combine(_gr, entry.FullName);
                    var dir = Path.GetDirectoryName(dst);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);
                    using var src = entry.Open();
                    using var fs = new FileStream(dst, FileMode.Create, FileAccess.Write);
                    src.CopyTo(fs);
                    count++;
                    Lg(">>> [安全DLL] 已覆盖: " + entry.FullName, Gn);
                }
            }
            File.WriteAllText(marker,
                "安装时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Encoding.UTF8);
            Lg(">>> [安全DLL] 安装完成: 覆盖 " + count + " 个文件, 已生成安装标记", Gn);
        }
        catch (Exception ex)
        {
            Lg(">>> [安全DLL] 安装失败: " + ex.Message, Rd);
        }
    }

    /*
     * 修复赛利亚房间问题 — 确认弹窗 (FixSeriaRoomConfirm)
     */
    void FixSeriaRoomConfirm()
    {
        var src = Path.Combine(_ad, "实用工具包", "赛利亚房间修复");
        if (!Directory.Exists(src))
        {
            Lg("赛利亚房间修复目录不存在: " + src, Rd);
            return;
        }
        var files = Directory.GetFiles(src, "*", SearchOption.AllDirectories);
        if (files.Length == 0)
        {
            Lg("赛利亚房间修复目录为空: " + src, Or);
            return;
        }

        var r = MessageBox.Show(
            "将用 AUM管理组件\\实用工具包\\赛利亚房间修复\\ 内的文件（" + files.Length
            + " 个）替换游戏根目录（" + _gr + "）中的对应文件，其中包含 86JP.dll。\n\n继续？",
            "修复赛利亚房间问题",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r != DialogResult.Yes) return;

        DoFixSeriaRoom(src, files);
    }

    /*
     * 修复赛利亚房间问题 — 执行复制 (DoFixSeriaRoom)
     * 将 赛利亚房间修复\ 内所有文件递归覆盖到游戏根目录 (DNF.exe 所在目录)
     */
    void DoFixSeriaRoom(string src, string[] files)
    {
        int copied = 0;
        try
        {
            foreach (var f in files)
            {
                var rel = Compat.GetRelativePath(src, f);
                var dst = Path.Combine(_gr, rel);
                var dir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.Copy(f, dst, true);
                copied++;
            }
            Lg(">>> 赛利亚房间修复完成: 已覆盖 " + copied + " 个文件到 " + _gr, Gn);
        }
        catch (Exception ex)
        {
            Lg(">>> 赛利亚房间修复失败: " + ex.Message, Rd);
        }
    }
    /*
     * 可点击链接行 (LinkRow) — 名称 + 地址, 点击在系统默认浏览器打开
     */
    Control LinkRow(string name, string urlText, string url)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2, RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 2, 0, 2)
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140F));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var nameLb = new AntdUI.Label
        {
            Text = name,
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(140, 140, 148),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Cursor = Cursors.Hand
        };
        var urlLb = new AntdUI.Label
        {
            Text = urlText,
            Font = new Font("Microsoft YaHei UI", 9f),
            ForeColor = Ac,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Cursor = Cursors.Hand
        };
        var open = () =>
        {
            Lg(">>> 打开: " + url, Color.CornflowerBlue);
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Lg("打开链接失败: " + ex.Message, Rd);
            }
        };
        nameLb.Click += (s, e) => open();
        urlLb.Click += (s, e) => open();
        row.Controls.Add(nameLb, 0, 0);
        row.Controls.Add(urlLb, 1, 0);
        return row;
    }

    // ================================================================
    // ★ P4 使用说明页 ★ — 左侧功能按钮网格, 点击右侧显示对应说明
    // ================================================================
    void BuildHelp()
    {
        // 说明数据: 分组 / 按钮名 / 图标 / 标题 / 正文
        (string group, string name, string icon, string title, string body)[] helps =
        {
            ("快速开始",
             "快速开始", "RocketOutlined", "快速开始",
             "使用本软件快速进入游戏。\n\n第一步 更新（首次使用必做）：\n  1. 点击【开始页】的「开始更新」按钮，拉取并编译服务端\n  2. 等待进度条完成（约 5~15 分钟，视网速而定）\n  3. 更新完成后无需重启，直接进入下一步\n\n第二步 启动游戏：\n  1. 回到【开始页】\n  2. 点击绿色大按钮「开始游戏」\n  3. 程序自动启动服务端并等待就绪（约 5~15 秒）\n  4. 服务端存活后自动打开游戏客户端\n\n第三步：\n  1. 创建或选择角色，直接开始游玩。\n\n如果遇到疑难杂症，请看使用说明~\n\n日常管理：\n· 停止服务端 — 【开始页】红色「停止服务端」\n· 存档备份 — 【存档管理】页，见「存档操作」说明\n· 管理员工具 — 【开始页】「GM工具」按钮"),

            ("服务端控制",
             "开始游戏的方式", "PlayCircleOutlined", "开始游戏",
             "一键启动服务端并进入游戏。\n\n执行流程：\n  1. 启动 start-server.bat 服务端脚本\n  2. 10 秒后自动隐藏脚本窗口\n  3. 检测服务端进程是否存活\n  4. 存活后自动打开游戏客户端\n  5. 15 秒后自动隐藏 DfoServer 窗口\n\n提示：\n· 首次启动较慢，请耐心等待约 10~20 秒\n· 若未自动打开游戏，请确认「本地游戏S4.bat」或「单机游戏启动.bat」存在"),
            ("服务端控制",
             "停止服务端的方式", "StopOutlined", "停止服务端",
             "终止正在运行的服务端进程树。\n\n执行内容：\n· 强制结束 DfoServer 及其全部子进程\n\n提示：\n· 停止后再进行存档操作，避免数据库损坏\n· 停止后可随时点击「开始游戏」重新启动"),
            ("服务端控制",
             "重启服务端的方式", "ReloadOutlined", "重启服务端",
             "先停止服务端，稍候片刻再重新启动。\n\n执行流程：\n  1. 停止服务端进程树\n  2. 等待约 1.2 秒\n  3. 重新启动 start-server.bat\n  4. 10 秒后自动隐藏窗口\n\n适用场景：修改了配置或补丁后需要让服务端重新加载"),

            ("运行方式",
             "可选运行方式", "ThunderboltOutlined", "可选运行方式（DX11 / DX12）",
             "为兼容不同显卡，选择游戏的渲染运行方式。\n\n使用方法：\n  1. 点击「以DX11方式运行游戏」或「以DX12方式运行游戏」\n  2. 程序自动把对应补丁文件（D3D9.dll / dgVoodoo.conf 等）复制到游戏目录\n  3. 点击「开始游戏」即可生效\n\n提示：\n· 两种方式只能二选一\n· 如果游戏无法正常游玩，请取消选择相关可选运行方式\n· 补丁目录为 AUM管理组件\\DX11补丁 与 DX12补丁\n\n切换运行方式方式，对游戏性能提升有限，建议使用原生运行方式（DX9）进行游戏"),
            ("运行方式",
             "去除水印的方式", "WatermarkOutlined", "去除dgVoodooCpl水印",
             "使用无水印版本的 dgVoodoo 补丁。\n\n使用方法：\n  1. 先选择 DX11 或 DX12 运行方式\n  2. 打开本开关\n  3. 程序自动改用「无水印」目录下的补丁文件\n\n提示：必须搭配 DX11/DX12 使用，单独开启无效"),

            ("目录与工具",
             "打开PVF目录的方式", "FolderOpenOutlined", "打开PVF目录",
             "打开服务端的 Pvf 文件夹。\n\n位置：AUM管理组件\\ServerS4A21-AUM\\dist\\win-x64\\Data\\Pvf\n\n用途：\n· 查看或替换游戏资源脚本（Script.pvf）\n· 排查资源加载问题"),
            ("目录与工具",
             "打开主存档的方式", "DatabaseOutlined", "打开主存档",
             "打开服务端实际读取的存档数据目录。\n\n位置：AUM管理组件\\ServerS4A21-AUM\\dist\\win-x64\\Data\n\n用途：\n· 直接查看/备份 inventory.db 数据库文件\n· 手动干预存档数据"),
            ("目录与工具",
             "GM工具的使用方式", "ToolOutlined", "GM工具",
             "启动 DfoGmTool 网页管理后台。\n\n使用方法：\n  1. 点击后自动启动 GM 工具服务\n  2. 自动打开浏览器访问 http://localhost:5050\n\n提示：\n· GM 工具需先执行一次「开始更新」完成编译\n· 服务端数据目录（Data 与 Pvf）必须完整\n· 重复点击会自动重启 GM 工具"),

            ("更新管理",
             "开始更新的方式", "ThunderboltOutlined", "开始更新（增量更新）",
             "执行日常增量更新，拉取服务端增量改动并重新编译。\n\n执行流程：\n  1. 检查仓库与镜像源连通性\n  2. 自动停止正在运行的服务端\n  3. 拉取增量源码并同步文件\n  4. 编译 DfoServer.exe 与 DfoGmTool.exe\n  5. 获取提交日志写入「更新日志.txt」\n\n提示：\n· 更新前请勾选「清理冗余DB」可顺带清理杂项数据库\n· 若更新无效果，可再次点击或联系开发者使用全量更新"),
            ("更新管理",
             "查看更新日志的方式", "FileTextOutlined", "查看更新日志",
             "用记事本打开「更新日志.txt」查看历史更新记录。\n\n日志包含：\n· 版本号与更新时间\n· 服务器与 GM 工具编译状态\n· 按日期分组的提交记录"),
            ("更新管理",
             "更新AUM的方式", "SyncOutlined", "更新AUM（管理器自更新）",
             "从 GitHub 拉取 AUM 管理器最新源码并自动重新编译升级。\n\n执行流程：\n  1. 检测远程版本（与本地 AUM-version.txt 对比）\n  2. 有更新时自动下载源码\n  3. 编译并替换 有依赖版/无依赖版 两个 exe\n  4. 自动重启管理器\n\n提示：\n· 需要本机已安装 .NET 10 SDK\n· 当前为开发版时也可选择强制重新编译"),
            ("更新管理",
             "安装SDK的方式", "DownloadOutlined", "安装NET.10 SDK",
             "打开微软官方 .NET 10 SDK 安装程序。\n\n安装包位置：AUM管理组件\\dotnet-sdk\\dotnet-sdk-10.0.302-win-x64.exe\n\n提示：\n· 若目录下没有安装包，请先手动下载放入该目录\n· 安装完成后重启管理器再执行更新"),

            ("存档管理",
             "存档操作", "DatabaseOutlined", "存档管理",
             "管理切换库中的所有存档。\n\n常用操作：\n· 储存当前 — 把当前存档存入切换库（双击拖拽区同样生效）\n· 导入存档 — 选择 ZIP 包还原存档\n· 导出当前 — 把当前存档打包为 ZIP\n· 撤销换挡 — 从最近一次备份恢复\n· 刷新存档 — 重新扫描切换库\n· 清理冗余DB — 自动删除 Data 目录中多余的 inventory* 文件\n\n存档列表操作：\n· 左键双击存档行 = 切换到该存档\n· 右键存档行 = 弹出菜单（切换 / 重命名）\n· 点击「修改时间」列头 = 切换排序方向\n· 拖拽 .db 文件到窗口任意位置 = 快速换挡\n\n提示：服务端运行中会阻止存档操作"),
            ("存档管理",
             "备份机制", "SafetyOutlined", "备份机制",
             "每次切换存档时自动备份当前存档。\n\n备份位置：AUM管理组件\\存档管理\\备份存档\\backup_日期_时间\n\n规则：\n· 最多保留最近 10 个备份（更早的自动删除）\n· 「撤销换挡」从最新备份恢复\n· 建议定期手动导出重要存档到安全位置"),

            ("日志区",
             "日志工具", "ConsoleSqlOutlined", "运行日志",
             "窗口底部常驻的运行日志条。\n\n功能：\n· 复制日志 — 把日志全文复制到剪贴板\n· 清空日志 — 清空当前显示与缓存\n· 折叠按钮（右下角）— 收起/展开日志区\n· 跳过更新日志 — 更新时不拉取提交记录（橙色）\n· 镜像下载 — 跳过 GitGud 直接使用镜像源（橙色）\n\n提示：更新期间底部会实时显示进度条与百分比"),
        };

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2, RowCount = 1,
            BackColor = Color.Transparent,
            Padding = new Padding(10)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 312F));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        pgHelp.Controls.Add(root);

        // ============================================================
        // ★ 左侧: 功能按钮列表 (分组标题 + 单列全宽按钮, 可滚动)
        // 用 In.Panel(AutoScroll) 包裹 FlowLayoutPanel(AutoSize),
        // 按钮数量任意增长都不会被裁剪, 支持无限扩展
        // ============================================================
        var leftCard = Card(8, 2);
        var leftScroll = new AntdUI.In.Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true
        };
        // 按钮宽度计算:
        //   左卡片客户区 = 304, 右侧滚动条占 ~17px, 可视区 = 287
        //   左侧 padding 12 → 按钮右缘 ≤ 287 - 间隔 17 ≈ 270 → 按钮宽 252
        // 保证按钮与滚动条保持约 17px 间隔, 视觉舒适
        const int btnW = 252;
        var leftFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            // 不透明背景(与卡片同色): 滚动时像素整体移动, 避免透明背景
            // 与移动像素叠加产生残影/文字重叠("一坨")
            BackColor = Style.Get(Colour.BgContainer),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Padding = new Padding(12, 8, 12, 8)
        };
        _helpFlow = leftFlow;
        leftScroll.Controls.Add(leftFlow);
        leftCard.Controls.Add(leftScroll);
        root.Controls.Add(leftCard, 0, 0);

        string lastGroup = null;
        foreach (var h in helps)
        {
            if (h.group != lastGroup)
            {
                lastGroup = h.group;
                var gCap = new AntdUI.Label
                {
                    Text = "—— " + h.group + " ——",
                    Font = new Font("Microsoft YaHei UI", 8.5f),
                    ForeColor = Color.FromArgb(120, 120, 128),
                    AutoSize = false,
                    Size = new Size(btnW, 24),
                    TextAlign = ContentAlignment.MiddleLeft,
                    Margin = new Padding(0, 6, 0, 2)
                };
                leftFlow.Controls.Add(gCap);
            }

            var btn = new AntdUI.Button
            {
                Text = h.name,
                Type = TTypeMini.Default,
                IconSvg = h.icon,
                Size = new Size(btnW, 44),
                Margin = new Padding(0, 2, 0, 2),
                WaveSize = 0,   // 关闭水波动画: 滚动时减少重绘开销, 更流畅
                Cursor = Cursors.Hand
            };
            btn.BorderWidth = 1.5F;
            btn.DefaultBorderColor = Color.FromArgb(100, 102, 110);
            var item = h; // 闭包捕获
            btn.Click += (s, e) => ShowHelp(item.title, item.body);
            leftFlow.Controls.Add(btn);
        }

        // ============================================================
        // ★ 右侧: 说明卡片
        // ============================================================
        var rightCard = Card(8, 2);
        rightCard.Padding = new Padding(28, 20, 28, 20);
        var rightInner = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1, RowCount = 3,
            BackColor = Color.Transparent
        };
        rightInner.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
        rightInner.RowStyles.Add(new RowStyle(SizeType.Absolute, 2F));
        rightInner.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        lbHelpTitle = new AntdUI.Label
        {
            Text = "点击左侧按钮查看使用说明",
            Font = new Font("Microsoft YaHei UI", 16f, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        var divider = new AntdUI.Divider
        {
            Dock = DockStyle.Fill
        };
        lbHelpBody = new AntdUI.Label
        {
            Text = "左侧列出了本程序所有功能按钮，点击任意一个即可查看对应的详细使用说明。\n\n包括：服务端控制、运行方式、目录与工具、更新管理、存档管理、日志区 共 6 大类功能。",
            Font = new Font("Microsoft YaHei UI", 10f),
            ForeColor = Color.FromArgb(150, 150, 158),
            TextAlign = ContentAlignment.TopLeft,
            TextMultiLine = true,
            Dock = DockStyle.Fill
        };
        rightInner.Controls.Add(lbHelpTitle, 0, 0);
        rightInner.Controls.Add(divider, 0, 1);
        rightInner.Controls.Add(lbHelpBody, 0, 2);
        rightCard.Controls.Add(rightInner);
        root.Controls.Add(rightCard, 1, 0);

        // 默认置顶显示第一条说明 (快速开始)
        if (helps.Length > 0)
            ShowHelp(helps[0].title, helps[0].body);
    }

    /*
     * 显示使用说明 (ShowHelp) — 更新右侧说明卡片的标题与正文
     */
    void ShowHelp(string title, string body)
    {
        if (lbHelpTitle == null || lbHelpBody == null) return;
        lbHelpTitle.Text = title;
        lbHelpBody.Text = body;
        lbHelpBody.ForeColor = Color.FromArgb(150, 150, 158);
    }

    // ================================================================
    // ★ 日志条 ★ — 标题 + 选项 + 进度 + 复制/清空/折叠 + 日志正文
    // ================================================================
    void BuildLog()
    {
        var lp = new AntdUI.Panel
        {
            Dock = DockStyle.Fill,
            Radius = 8,
            Shadow = 2,
            BorderWidth = 1F,
            Margin = new Padding(12, 8, 12, 12)
        };
        lp.Padding = new Padding(0, 0, 0, 0);

        // ============================================================
        // ★ 日志工具条 ★
        // ============================================================
        var lbar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 44,
            ColumnCount = 8, RowCount = 1,
            BackColor = Color.Transparent,
            Padding = new Padding(12, 6, 12, 0)
        };
        lbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90F));   // 标题
        lbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165F));  // 跳过更新日志
        lbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140F));  // 镜像下载
        lbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));   // 进度标签
        lbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110F));  // 复制日志
        lbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110F));  // 清空日志
        lbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40F));   // 折叠按钮
        lbar.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var lbLogTitle = Cap("运行日志");
        lbLogTitle.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);

        // 进度百分比标签 (更新时显示)
        lbPg = new AntdUI.Label
        {
            Text = "",
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei UI", 8.5f),
            ForeColor = Color.FromArgb(130, 130, 130),
            TextAlign = ContentAlignment.MiddleLeft,
            Visible = false
        };

        // 复制日志按钮
        btCp = B("复制日志", TTypeMini.Default, "CopyOutlined");
        btCp.Click += (s, e) =>
        {
            Lg(">>> 复制日志", Color.CornflowerBlue);
            if (rt.Text.Length > 0)
            {
                try
                {
                    Clipboard.SetDataObject(rt.Text, copy: true, retryTimes: 3, retryDelay: 50);
                }
                catch (Exception)
                {
                    Lg("复制失败: 所请求的剪贴板操作失败，请手动进行复制——按下Ctrl+A可以全选整个运行日志", Rd);
                }
            }
        };

        // 清空日志按钮
        btCl = B("清空日志", TTypeMini.Default, "DeleteOutlined");
        btCl.Click += (s, e) =>
        {
            Lg(">>> 清空日志", Color.CornflowerBlue);
            rt.Clear();
            _logBuilder.Clear();
        };

        // 折叠/展开日志按钮
        btFold = new AntdUI.Button
        {
            Dock = DockStyle.Fill,
            Ghost = true,
            Radius = 0,
            WaveSize = 0,
            IconSvg = "DownOutlined",
            Cursor = Cursors.Hand
        };
        btFold.Click += (s, e) =>
        {
            _logCollapsed = !_logCollapsed;
            rt.Visible = !_logCollapsed;
            _root.RowStyles[2].Height = _logCollapsed ? 46 : 210;
            btFold.IconSvg = _logCollapsed ? "UpOutlined" : "DownOutlined";
        };

        // 跳过更新日志开关 — 下次更新不拉取仓库提交记录
        cbSkipLog = new AntdUI.Switch { Checked = false, Cursor = Cursors.Hand };
        cbSkipLog.CheckedChanged += (s, e) =>
        {
            Lg(">>> [跳过更新日志] " + (cbSkipLog.Checked ? "已启用 — 下次更新不拉取提交记录" : "已关闭"),
                cbSkipLog.Checked ? Or : Txt2);
        };

        // 镜像下载开关 — 跳过GitGud直接使用镜像源
        cbMirror = new AntdUI.Switch { Checked = false, Cursor = Cursors.Hand };
        cbMirror.CheckedChanged += (s, e) =>
        {
            Lg(">>> [镜像下载] " + (cbMirror.Checked ? "已启用 — 跳过GitGud直接使用镜像源" : "已关闭"),
                cbMirror.Checked ? Gn : Txt2);
        };

        lbar.Controls.Add(lbLogTitle, 0, 0);
        lbar.Controls.Add(SwRow("跳过更新日志", cbSkipLog, 8.5f, Or), 1, 0);
        lbar.Controls.Add(SwRow("镜像下载", cbMirror, 8.5f, Or), 2, 0);
        lbar.Controls.Add(lbPg, 3, 0);
        lbar.Controls.Add(btCp, 4, 0);
        lbar.Controls.Add(btCl, 5, 0);
        lbar.Controls.Add(btFold, 6, 0);

        // ============================================================
        // ★ 日志正文 ★ — 只读、等宽字体、深色背景
        // ============================================================
        rt = new RichTextBox
        {
            Dock = DockStyle.Fill,
            BackColor = LogBg,
            ForeColor = Color.FromArgb(240, 240, 245),
            ReadOnly = true,
            WordWrap = true,
            Font = new Font("Consolas", 9.5f),
            BorderStyle = BorderStyle.None
        };

        // 更新进度条 — 更新时显示，平时自动隐藏
        pb = new AntdUI.Progress
        {
            Dock = DockStyle.Bottom,
            Height = 12,
            Radius = 4,
            Value = 0,
            Visible = false,
            Margin = new Padding(12, 0, 12, 6)
        };

        // 添加顺序决定 Dock 层级: rt(填充) → lbar(顶部) → pb(底部)
        lp.Controls.Add(rt);
        lp.Controls.Add(lbar);
        lp.Controls.Add(pb);
        _root.Controls.Add(lp, 0, 2);
    }
}
