/*
 * ==================================================================
 * MainForm 疑难杂症解惑页 (partial class) — 独立文件, 便于单独维护
 * ==================================================================
 *
 * 【说明】
 *   本页面与使用说明页结构相同: 左侧问题分类按钮列表 + 右侧解答卡片。
 *   所有页面 UI 与内容均在本文件内维护, 修改不影响其他页面。
 *
 * 【代码组织】
 *   MainForm.cs               — 菜单项 / 页面容器创建 / 页面切换
 *   MainForm.Troubleshoot.cs  — 疑难杂症解惑页的全部 UI 与内容 (本文件)
 * ==================================================================
 */
using System;
using System.Drawing;
using System.Windows.Forms;
using AntdUI;

namespace ServerUI;

public partial class MainForm : AntdUI.Window
{
    // ===== 疑难杂症解惑页 控件 =====
    AntdUI.In.Panel pgTsh;              // 页面容器 (由 Build() 创建并加入内容区)
    AntdUI.Label lbTshTitle, lbTshBody; // 右侧标题 + 正文
    FlowLayoutPanel _tshFlow;           // 左侧按钮流 (不透明背景, 主题切换时同步)

    // ================================================================
    // ★ 疑难杂症解惑页 ★ — 左侧问题分类按钮, 点击右侧显示对应解答
    // ================================================================
    void BuildTroubleshoot()
    {
        // 解答数据: 分组 / 按钮名 / 图标 / 标题 / 正文
        (string group, string name, string icon, string title, string body)[] items =
        {
            ("关于",
             "关于\"疑难杂症\"", "InfoCircleOutlined", "关于\"疑难杂症\"",
             "本菜单旨在记录游戏过程中出现的各类常见问题及其解决方法，内容来源于个人经验总结及用户反馈收集。"),

            ("第一类：存档与数据库相关",
             "回档或登录中断怎么办？", "UndoOutlined", "回档或登录中断怎么办？",
             "回档问题通常由新版本种子库与旧存档之间的不兼容冲突引起。\n目前项目仍处于持续开发阶段，相关道具、背包等数据库系统可能会经历多次重构。\n建议使用我配置的【新生种子存档】进行游戏，存档位于【切换库】中，可直接选用。\n\n在DB存档数据目录下，可能会生成【inventory.db-shm】与【inventory.db-wal】等额外文件，这些是由新种子库自动生成的辅助文件。\n\n若出现以下情况：\n- 点开服务端后窗口秒关闭\n- 无法进入角色选择界面\n- 提示\"网络连接中断\"\n- 频道列表无法正常显示\n\n请尝试手动删除上述两个文件，然后重新登录游戏，通常可恢复正常。\n\n此外，您也可以在存档切换界面点击【清理冗余DB】功能（详见上一条说明），该功能用于清除因数据库变动产生的异常文件。\n勾选后，系统将自动删除如【inventory.db-shm】与【inventory.db-wal】等新生成的数据库附属文件，以确保存档切换顺畅。\n\n（删除后，服务端会自动重建适配当前版本的新数据库文件，无需额外操作。）"),

            ("第一类：存档与数据库相关",
             "多台电脑如何同步存档？", "LaptopOutlined", "多台电脑如何同步存档？",
             "将db存档放入【切换库】中，并做好命名标识。之后只需拷贝整个切换库到另一台电脑即可。\n请注意：服务端运行时请勿拷贝存档文件。如不熟悉操作，建议使用AUM中的【储存当前存档】功能，全程在AUM内完成，可避免误操作。\n点击【储存当前存档】后，系统会自动将存档存入切换库中。\n更多详细说明，请参考存档功能相关介绍。"),

            ("第二类：客户端与启动环境",
             "全屏后闪退怎么办？", "FullscreenOutlined", "全屏后闪退怎么办？",
             "该问题通常与游戏客户端本身的exe文件或运行环境配置有关，而非服务端问题。不同硬件或系统环境下表现可能不同。\n\n解决方法如下：\n打开路径 【C:\\Users\\用户名\\AppData\\LocalLow\\DNF】，删除该文件夹下的所有内容。\n（该文件夹为隐藏目录，请先在系统设置中开启\"显示隐藏文件\"选项，方可查看并操作。）"),

            ("第二类：客户端与启动环境",
             "卡在赛利亚房间怎么办？", "HomeOutlined", "卡在赛利亚房间怎么办？",
             "该问题原因暂不明确，可能与DLL文件与当前系统不兼容有关。\n（S4A21 版本服务端与游戏本体已无此问题，原修复功能已暂时移除，按钮【待定】保留。）"),

            ("第二类：客户端与启动环境",
             "更新失败或日志截断怎么办？", "CloudDownloadOutlined", "更新失败或日志截断怎么办？",
             "更新失败或日志截断怎么办？\n\n在没有网络代理的情况下，建议优先选择镜像下载方式，可解决大部分更新失败的问题。\n更新过程中会显示进度条和日志信息，完成后会弹出提示：\n【>>>更新完成!如果更新没有效果，请尝试再次点击更新或使用全量更新。<<<】\n\n不过，由于网络波动，更新有时仍可能失败，失败请使用镜像下载，或者本地编译。\n~日志输出截断通常与网络状况有关，若想提升更新速度，可勾选【跳过更新日志】选项。"),

            ("第二类：客户端与启动环境",
             "DNF.exe找不到，出现报错", "BugOutlined", "DNF.exe找不到，出现报错",
             "该问题通常由杀毒软件误删DNF.exe所致，包括系统自带的Windows Defender在内。\n请尝试关闭杀毒软件或将游戏目录加入白名单。\n若追求安全性且希望一劳永逸，建议安装【火绒安全杀毒软件】，可有效避免此类问题。"),

            ("第二类：客户端与启动环境",
             "打死怪后会卡一下怎么办？", "HourglassOutlined", "打死怪后会卡一下怎么办？",
             "打死怪后卡顿，因为杀毒软件检测，扫盘，使服务端通讯受阻导致的。\n解决方法如下：\n请尝试关闭杀毒软件或将游戏目录加入白名单。\n若追求安全性且希望一劳永逸，建议安装【火绒安全杀毒软件】，可有效避免此类问题。"),

("第二类：客户端与启动环境",
              "角色选择/创建界面闪退怎么办？", "FullscreenOutlined", "角色选择/创建界面闪退怎么办？",
              "经追踪，该问题主要与高帧率有关。请尝试以下方案：\n方案一：切换至核显运行（适用于同时拥有独显和核显的电脑）\nWindows设置 → 系统 → 屏幕 → 显示卡 → 添加DNF.exe → 选项 → 选择\"节能\"（核显）→ 保存。\n方案二：限制帧率（适用于无核显或不愿切换显卡的电脑）\nNVIDIA控制面板 → 管理3D设置 → 程序设置 → 添加DNF.exe → 开启\"最大帧速率\" → 设为120 FPS → 应用。\n两种方法均实测有效，建议优先尝试方案二。若已使用角色界面补丁，建议先还原官方资源。\n————————\n原理说明\n2.033端使用老版32位DX9客户端，较新NVIDIA显卡搭配高刷显示器时，帧率可飙升至800+ FPS。DX9在角色界面需频繁加载贴图，过高帧率导致渲染与资源加载线程同步紊乱，触发崩溃。\n方案一利用核显性能有限、无法输出极端帧率的特点，自然规避问题；方案二通过驱动直接锁帧，从源头控制。\n另有日志报错CharacterCreate/CharacterCreate.img (48)，核查该IMG实际仅45帧，但补齐后闪退依旧，证明帧率才是核心原因。\n如有其他环境差异，请补充反馈，我们会继续协助排查。"),
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
        pgTsh.Controls.Add(root);

        // ============================================================
        // ★ 左侧: 问题分类按钮列表 (分组标题 + 单列全宽按钮, 可滚动)
        // 与使用说明页相同的布局方案: In.Panel(AutoScroll) 包裹
        // FlowLayoutPanel(AutoSize), 按钮数量任意增长都不会被裁剪
        // ============================================================
        var leftCard = Card(8, 2);
        var leftScroll = new AntdUI.In.Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true
        };
        const int btnW = 252;
        var leftFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            // 不透明背景(与卡片同色): 滚动时像素整体移动, 避免透明背景
            // 与移动像素叠加产生残影/文字重叠
            BackColor = Style.Get(Colour.BgContainer),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Padding = new Padding(12, 8, 12, 8)
        };
        _tshFlow = leftFlow;
        leftScroll.Controls.Add(leftFlow);
        leftCard.Controls.Add(leftScroll);
        root.Controls.Add(leftCard, 0, 0);

        string lastGroup = null;
        foreach (var it in items)
        {
            if (it.group != lastGroup)
            {
                lastGroup = it.group;
                var gCap = new AntdUI.Label
                {
                    Text = "—— " + it.group + " ——",
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
                Text = it.name,
                Type = TTypeMini.Default,
                IconSvg = it.icon,
                Size = new Size(btnW, 44),
                Margin = new Padding(0, 2, 0, 2),
                WaveSize = 0,   // 关闭水波动画: 滚动时减少重绘开销, 更流畅
                Cursor = Cursors.Hand
            };
            btn.BorderWidth = 1.5F;
            btn.DefaultBorderColor = Color.FromArgb(100, 102, 110);
            var item = it; // 闭包捕获
            btn.Click += (s, e) => ShowTroubleshoot(item.title, item.body);
            leftFlow.Controls.Add(btn);
        }

        // ============================================================
        // ★ 右侧: 解答卡片
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

        lbTshTitle = new AntdUI.Label
        {
            Text = "点击左侧问题查看解答",
            Font = new Font("Microsoft YaHei UI", 16f, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        var divider = new AntdUI.Divider
        {
            Dock = DockStyle.Fill
        };
        lbTshBody = new AntdUI.Label
        {
            Text = "左侧列出了游戏过程中常见的各类问题，点击任意一个即可查看对应的解决方法。\n\n包括：存档与数据库相关、客户端与启动环境 共 2 大类问题。",
            Font = new Font("Microsoft YaHei UI", 10f),
            ForeColor = Color.FromArgb(150, 150, 158),
            TextAlign = ContentAlignment.TopLeft,
            TextMultiLine = true,
            Dock = DockStyle.Fill
        };
        rightInner.Controls.Add(lbTshTitle, 0, 0);
        rightInner.Controls.Add(divider, 0, 1);
        rightInner.Controls.Add(lbTshBody, 0, 2);
        rightCard.Controls.Add(rightInner);
        root.Controls.Add(rightCard, 1, 0);

        // 默认置顶显示第一条 (关于"疑难杂症")
        if (items.Length > 0)
            ShowTroubleshoot(items[0].title, items[0].body);
    }

    /*
     * 显示解答 (ShowTroubleshoot) — 更新右侧卡片的标题与正文
     */
    void ShowTroubleshoot(string title, string body)
    {
        if (lbTshTitle == null || lbTshBody == null) return;
        lbTshTitle.Text = title;
        lbTshBody.Text = body;
        lbTshBody.ForeColor = Color.FromArgb(150, 150, 158);
    }

    /*
     * 主题切换同步 (TshThemeSync) — 由 MainForm.OnThemeChanged 调用
     * 左侧按钮流为不透明背景, 需跟随明暗主题
     */
    internal void TshThemeSync()
    {
        try
        {
            if (_tshFlow != null)
                _tshFlow.BackColor = Style.Get(Colour.BgContainer);
        }
        catch { }
    }
}
