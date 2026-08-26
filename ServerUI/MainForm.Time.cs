/*
 * ==================================================================
 * MainForm 调整系统时间页 (partial class)
 * 手动设置 / 快速调整(2015/2017/2006) / 同步网络时间 (TimeTool)
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
    // ===== 调整系统时间页 字段 (MainForm.Time.cs) =====
    AntdUI.DatePicker dtpTime;             // 目标时间选择器 (时间页, AntdUI 风格)
    AntdUI.Label lbTimeNow, lbTimeSt;      // 当前时间 / 操作结果 (时间页)
    Timer _tt;                             // 时间页每秒刷新定时器

    // ================================================================
    // ★ P2.6 调整系统时间页 ★ — 手动设置时间 / 快速调整 / 同步网络时间
    // 功能参考: E:\网页小工具\时间调节工具
    // 写入系统时间需要管理员权限: 主进程为 asInvoker,
    // 需要提权时通过 TimeTool.RunElevated 以命令行参数重启自身执行
    // ================================================================
    void BuildTimePage()
    {
        pgTime.Padding = new Padding(10);
        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1, RowCount = 4,
            BackColor = Color.Transparent
        };
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 62F));   // 信息栏 (目标时间 + 选择器 + 读取)
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 58F));   // 常用操作行 (应用 + 快速调整)
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));   // 网络同步卡片
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));   // 状态行
        pgTime.Controls.Add(t);

        // ============================================================
        // ★ 信息栏 ★ (参考存档管理信息栏: 标签 + 控件 + 按钮一行排开)
        // ============================================================
        var ib = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3, RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(4, 2, 4, 0)
        };
        ib.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
        ib.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        ib.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150F));
        ib.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var lbTarget = new AntdUI.Label
        {
            Text = "目标时间：",
            Font = new Font("Microsoft YaHei UI", 9.5f),
            ForeColor = Color.FromArgb(140, 140, 148),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        // AntdUI.DatePicker — 与主界面同款的下拉日期/时间选择框 (圆角边框, 跟随主题)
        dtpTime = new AntdUI.DatePicker
        {
            Format = "yyyy-MM-dd HH:mm:ss",
            Value = DateTime.Now,
            WaveSize = 0,
            Placement = AntdUI.TAlignFrom.Bottom,
            Height = 28,
            Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
            Margin = new Padding(2, 19, 2, 0),   // 向下微移, 与「目标时间」标签对齐
            Font = new Font("Microsoft YaHei UI", 9.5f)
        };
        var btRead = B("读取系统时间", TTypeMini.Default, "FieldTimeOutlined");
        btRead.Click += (s, e) =>
        {
            dtpTime.Value = DateTime.Now;
            Lg(">>> 已读取当前系统时间", Color.CornflowerBlue);
        };
        ib.Controls.Add(lbTarget, 0, 0);
        ib.Controls.Add(dtpTime, 1, 0);
        ib.Controls.Add(btRead, 2, 0);
        t.Controls.Add(ib, 0, 0);

        // ============================================================
        // ★ 常用操作行 ★ — 应用此时间 + 快速调整 2006/2015/2017
        // (参考存档管理常用操作: 主按钮 + 一排快捷按钮)
        // ============================================================
        var op = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4, RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(4, 4, 4, 0)
        };
        op.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170F));
        op.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / 3F));
        op.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / 3F));
        op.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / 3F));
        op.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var btApply = B("应用此时间", TTypeMini.Primary, "CheckOutlined");
        btApply.Click += (s, e) => ApplySystemTime();

        var bt2006 = B("快速调整 2006-01-01", TTypeMini.Default, "HistoryOutlined");
        bt2006.Click += (s, e) => QuickApplyTime(new DateTime(2006, 1, 1, 0, 0, 0));
        var bt2015 = B("快速调整 2015-01-01", TTypeMini.Default, "HistoryOutlined");
        bt2015.Click += (s, e) => QuickApplyTime(new DateTime(2015, 1, 1, 0, 0, 0));
        var bt2017 = B("快速调整 2017-01-01", TTypeMini.Default, "HistoryOutlined");
        bt2017.Click += (s, e) => QuickApplyTime(new DateTime(2017, 1, 1, 0, 0, 0));

        op.Controls.Add(btApply, 0, 0);
        op.Controls.Add(bt2006, 1, 0);
        op.Controls.Add(bt2015, 2, 0);
        op.Controls.Add(bt2017, 3, 0);
        t.Controls.Add(op, 0, 1);

        // ============================================================
        // ★ 网络时间同步卡片 ★ — 大按钮 + 说明 + 实时当前时间
        // ============================================================
        var c3 = Card(8, 2);
        c3.Padding = new Padding(16, 10, 16, 10);
        var g3 = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1, RowCount = 3,
            BackColor = Color.Transparent
        };
        g3.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));
        g3.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        g3.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));

        var btSync = B("同步网络时间（校准为标准时间）", TTypeMini.Success, "CloudSyncOutlined");
        btSync.Click += (s, e) => SyncNetworkTime();

        var lbSync = new AntdUI.Label
        {
            Text = "自动从 NTP 服务器（ntp.aliyun.com / cn.pool.ntp.org / time.windows.com 等）获取标准时间并写入系统，无需手动查找时间源。写入需要管理员权限（自动弹 UAC 确认）。",
            Font = new Font("Microsoft YaHei UI", 8.5f),
            ForeColor = Color.FromArgb(130, 130, 138),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft,
            TextMultiLine = true
        };
        lbTimeNow = L("当前系统时间：--", 9.5f, Ac);

        g3.Controls.Add(btSync, 0, 0);
        g3.Controls.Add(lbSync, 0, 1);
        g3.Controls.Add(lbTimeNow, 0, 2);
        c3.Controls.Add(g3);
        t.Controls.Add(c3, 0, 2);

        // ============================================================
        // ★ 状态行 ★
        // ============================================================
        lbTimeSt = L("就绪", 9f);
        lbTimeSt.ForeColor = Color.FromArgb(130, 130, 138);
        t.Controls.Add(lbTimeSt, 0, 3);

        // 每秒刷新"当前系统时间"
        _tt = new Timer { Interval = 1000 };
        _tt.Tick += (s, e) =>
        {
            if (lbTimeNow == null || lbTimeNow.IsDisposed) return;
            lbTimeNow.Text = "当前系统时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        };
        _tt.Start();
    }

    void QuickApplyTime(DateTime t)
    {
        dtpTime.Value = t;
        ApplySystemTime();
    }

    /*
     * 应用目标时间 — 直接写入 (管理员) 或提权执行 --settime
     */
    void ApplySystemTime()
    {
        var ts = (dtpTime.Value ?? DateTime.Now).ToString("yyyy-MM-dd HH:mm:ss");
        Lg(">>> 设置系统时间: " + ts, Color.CornflowerBlue);
        if (!TimeTool.IsAdmin())
        {
            if (TimeTool.RunElevated("--settime \"" + ts + "\""))
                ShowTimeArgResult("设置结果");
            else
                Lg("需要管理员权限才能修改系统时间（用户取消了提权）", Rd);
            return;
        }
        try
        {
            var err = TimeTool.SetLocalTime(dtpTime.Value ?? DateTime.Now);
            if (err == null)
            {
                Lg(">>> 已设置系统时间: " + ts, Gn);
                lbTimeSt.Text = "已设置：" + ts;
            }
            else
            {
                Lg(">>> 设置系统时间失败: " + err, Rd);
                lbTimeSt.Text = "设置失败：" + err;
            }
        }
        catch (Exception ex)
        {
            Lg(">>> 设置系统时间异常: " + ex.Message, Rd);
        }
    }

    /*
     * 同步网络时间 — 本进程查询 NTP (无需管理员); 写入需提权
     */
    void SyncNetworkTime()
    {
        Lg(">>> 开始同步网络时间...", Color.CornflowerBlue);
        Cursor = Cursors.WaitCursor;
        try
        {
            var nt = TimeTool.QueryNtp();
            if (!nt.HasValue)
            {
                var err2 = TimeTool.ResyncViaW32tm();
                if (err2 == null)
                {
                    Lg(">>> 已通过 Windows 时间服务（w32tm）同步", Gn);
                    lbTimeSt.Text = "已通过 w32tm 同步";
                }
                else
                {
                    Lg(">>> 同步失败（无法连接时间服务器）: " + err2, Rd);
                    lbTimeSt.Text = "同步失败：无法连接时间服务器";
                }
                return;
            }
            var netTime = nt.Value;
            Lg(">>> 已获取网络标准时间: " + netTime.ToString("yyyy-MM-dd HH:mm:ss"), Color.CornflowerBlue);

            if (!TimeTool.IsAdmin())
            {
                if (TimeTool.RunElevated("--synctime"))
                    ShowTimeArgResult("同步结果");
                else
                    Lg("需要管理员权限才能写入系统时间（用户取消了提权）", Rd);
                return;
            }
            var err = TimeTool.SetLocalTime(netTime);
            if (err == null)
            {
                Lg(">>> 网络时间已同步并写入: " + netTime.ToString("yyyy-MM-dd HH:mm:ss"), Gn);
                lbTimeSt.Text = "已同步：" + netTime.ToString("yyyy-MM-dd HH:mm:ss");
            }
            else
            {
                Lg(">>> 写入系统时间失败: " + err, Rd);
                lbTimeSt.Text = "同步失败：" + err;
            }
        }
        finally { Cursor = Cursors.Default; }
    }

    /*
     * 读取提权实例写入的结果文件并显示
     */
    void ShowTimeArgResult(string tag)
    {
        var res = TimeTool.ReadResult();
        TimeTool.ClearResult();
        if (string.IsNullOrEmpty(res))
        {
            Lg(">>> " + tag + "：未获取到操作结果", Rd);
            lbTimeSt.Text = "未获取到操作结果";
            return;
        }
        if (res.StartsWith("OK"))
        {
            Lg(">>> " + tag + "成功：" + res, Gn);
            lbTimeSt.Text = tag + "成功：" + res;
        }
        else
        {
            Lg(">>> " + tag + "失败：" + res, Rd);
            lbTimeSt.Text = tag + "失败：" + res;
        }
    }

}
