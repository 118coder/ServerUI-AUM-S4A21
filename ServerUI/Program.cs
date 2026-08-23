/*
 * ==================================================================
 * 程序入口点 (Program.cs)
 * ==================================================================
 * 
 * 【功能说明】
 *   整个 ServerUI 应用程序的启动入口。
 *   负责：设置 Windows 兼容性 → 注册全局异常捕获 → 显示主窗口。
 * 
 * 【执行流程】
 *   1. 注册 UnhandledException / ThreadException 全局异常兜底
 *      （避免程序崩溃时悄无声息地退出，让用户看到错误原因）
 *   2. 启动环境预检：Windows 版本检测（低于 Win10 1607 友好提示）
 *   3. SetHighDpiMode(PerMonitorV2) — 适配高分屏（与 app.manifest 一致）
 *   4. EnableVisualStyles — 启用 XP 风格控件外观
 *   5. Application.Run(new MainForm()) — 启动主窗口消息循环
 * 
 * 【新手修改指南】
 *   - 想修改窗口标题? → 去 MainForm.cs 构造函数里改 Text 属性
 *   - 想修改默认窗口大小? → 去 MainForm.cs 构造函数里改 Size
 *   - 想添加启动时的初始化逻辑? → 在 MainForm 构造函数末尾加代码
 *   - 绝对不要在 Program.cs 里加业务逻辑！
 * 
 * 【关键注意事项】
 *   [STAThread] 必须保留 — 没有它 WinForms 无法工作
 *   SetHighDpiMode 必须在 Application.Run 之前调用，顺序不能变
 * 
 * 【体积优化方案 — 已验证 — 切勿删除】
 *   实际优化方案：单文件压缩模式（EnableCompressionInSingleFile=true）
 *   —— 压缩所有托管程序集（运行时 DLL、AntdUI、应用代码）
 *   效果：无依赖版 114 MB → 50.6 MB（降幅 56%）
 *   配置位置：ServerUI.csproj（含完整说明注释，请保留）
 *   注意：Native AOT 方案不可行（WinForms 官方禁止，NETSDK1175）
 * ==================================================================
 */
using System;
using System.IO;
using System.Windows.Forms;

namespace ServerUI;

static class Program
{
    /*
     * [STAThread] 特性
     * 表示该线程使用单线程单元模型 (Single-Threaded Apartment)
     * WinForms 的剪贴板、拖放等功能依赖 STA 模型，去掉会导致程序异常
     * 这是 Windows Forms 项目的固定写法，不要修改
     */
    [STAThread]
    static void Main()
    {
        // v1.913: 启动前先写日志（捕获 DPI 初始化之前的无声崩溃）
        var startupLog = Path.Combine(AppContext.BaseDirectory, "ServerUI-启动日志.txt");
        try { File.WriteAllText(startupLog, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " 启动中...\n", System.Text.Encoding.UTF8); } catch { }

        // ===== 全局异常兜底 =====
        // 作用: 当程序发生未捕获的异常时，弹出错误提示 + 写入崩溃日志
        // 如果删除这段代码，程序崩溃时用户只会看到闪退，无法知道原因
        // 日志文件位置: EXE 同目录下的 ServerUI-崩溃日志.txt
        // 修改建议: 可以改弹窗里的文字，但不建议删除异常日志功能
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            Report(e.ExceptionObject as Exception, "UnhandledException");
        Application.ThreadException += (s, e) =>
            Report(e.Exception, "ThreadException");
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        try
        {
#if NET48
            // ===== 启动环境预检 (Win7 兼容版) =====
            // Win7 兼容版基于 .NET Framework 4.8, 支持 Win7 SP1 及以上,
            // 不做 Windows 10 版本拦截
#else
            // ===== 启动环境预检 (v2.0) =====
            // 1. Windows 版本检测: .NET 10 要求 Win10 1607+ (低于此版本直接友好提示)
            var osVer = Environment.OSVersion;
            bool win10Plus = osVer.Platform == PlatformID.Win32NT
                && osVer.Version.Major >= 10
                && (osVer.Version.Build >= 14393 || osVer.Version.Major > 10); // 14393 = Win10 1607
            if (!win10Plus)
            {
                System.Windows.Forms.MessageBox.Show(
                    "当前系统版本过低，ServerUI 需要 Windows 10 (1607) 及以上版本才能运行。\n\n"
                    + "当前系统: " + osVer.VersionString + "\n\n"
                    + "请升级系统或联系管理员。",
                    "ServerS4A21 管理器 - 系统版本不兼容",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Warning);
                return;
            }
#endif

            // 2. 判断是否为"有依赖版"(无依赖版 >50MB) — 有依赖版缺少运行时会在更早阶段失败,
            //    这里仅用于日志标记
            var exeLen = new FileInfo(Compat.ExePath()).Length;
            bool isPortable = exeLen > 50_000_000;
            File.AppendAllText(startupLog,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
#if NET48
                + (isPortable ? " 便携版(无依赖)\n" : " Win7 兼容版(需 .NET Framework 4.8)\n"),
#else
                + (isPortable ? " 便携版(无依赖)\n" : " 有依赖版(需 .NET 10 运行时)\n"),
#endif
                System.Text.Encoding.UTF8);

#if !NET48
            // ===== 高 DPI 适配 =====
            // v1.913: 改用 PerMonitorV2 与 app.manifest 的 <dpiAwareness>PerMonitorV2</dpiAwareness> 保持一致
            // SystemAware 与 manifest 的 PerMonitorV2 冲突会导致 Win10 上窗口不可见/无响应
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
#endif

            // ===== AntdUI 初始化 (v2.0 现代化界面) =====
            // 文字渲染: 抗锯齿网格对齐, 高分屏下更清晰
            AntdUI.Localization.DefaultLanguage = "zh-CN";
            AntdUI.Config.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            AntdUI.Config.TextRenderingHighQuality = true;
            AntdUI.Config.Theme().Dark("#000", "#fff").Light("#fff", "#000").FormBorderColor();

            // 启用 XP 风格的控件外观（让按钮、滚动条等看起来现代一点）
            Application.EnableVisualStyles();

            // 让控件使用系统默认字体渲染（设为 false 是 WinForms 的惯例）
            Application.SetCompatibleTextRenderingDefault(false);

            // ===== 启动主窗口 =====
            // Application.Run 会启动 Windows 消息循环（event loop）
            // 程序会一直运行直到主窗口关闭，然后 Main 方法退出
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            // 如果连主窗口都没创建成功就崩了（比如缺少 .NET 运行时），也会弹窗
            Report(ex, "StartupFatal");
        }
    }

    /*
     * 异常报告方法
     * 功能: 把异常信息写入崩溃日志文件 + 弹出错误提示框
     * 
     * 参数:
     *   ex  — 异常对象（可以为 null，为 null 则什么都不做）
     *   tag — 异常类型标签（"UnhandledException" / "ThreadException" / "StartupFatal"）
     * 
     * 修改建议:
     *   - 想改日志文件名? 改 "ServerUI-崩溃日志.txt"
     *   - 想加更多错误提示? 改 MessageBox.Show 里的文字
     *   - 想关闭弹窗只写日志? 删除 try { MessageBox.Show... } 那段
     */
    static void Report(Exception ex, string tag)
    {
        if (ex == null) return;

        // 构造日志消息: 时间 + 标签 + 异常详情
        var msg = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] "
                  + tag + "\n" + ex + "\n\n";
        try
        {
            // 写入 EXE 同目录下的崩溃日志（追加模式，不会覆盖历史记录）
            var log = Path.Combine(AppContext.BaseDirectory, "ServerUI-崩溃日志.txt");
            File.AppendAllText(log, msg, System.Text.Encoding.UTF8);
        }
        catch { }
        try
        {
            // 弹出错误提示框
            // 如果用户看不懂这些信息，让他看崩溃日志文件
            MessageBox.Show(
                "程序发生错误，已记录到 ServerUI-崩溃日志.txt。\n\n" +
                "常见原因：\n" +
                "  1. 有依赖版需先安装 .NET 10 运行环境（可改用无依赖版）\n" +
                "  2. 被杀毒软件拦截，请加入信任\n\n" +
                "错误信息：\n" + ex.GetBaseException().Message,
                "ServerS4A21 管理器 - 启动失败",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
    }
}
