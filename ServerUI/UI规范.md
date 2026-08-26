# ServerUI（AUM 管理器）UI 规范记录

> 目标：所有页面视觉 / 交互 / 代码写法一致。新页面、新按钮、新弹窗一律按本规范实现。
> 基础：WinForms + AntdUI 2.4.3，`MainForm : AntdUI.Window`（圆角窗口、深色主题、抗拉伸、DPI 缩放）。

---

## 一、文件与代码组织

| 项 | 规范 |
|----|------|
| 页面拆分 | 每页独立 `partial class MainForm` 文件：`MainForm.Pages.cs`（开始/存档/设置/使用说明/日志）、`MainForm.DllExt.cs`（DLL 扩展 + 弹窗类）、`MainForm.Time.cs`（时间页）、`MainForm.Archive.cs`（存档逻辑）、`MainForm.Server.cs`（服务端/更新）、`MainForm.Troubleshoot.cs`（疑难解答） |
| 文件头注释 | 每文件顶部 `/* ==== 用途说明 ==== */`，注明包含哪些页面/方法 |
| 编码 | .cs / .md 为 UTF-8（新文件无 BOM）；**ps1 脚本必须 UTF-8 带 BOM** —— Windows PowerShell 5.1 对无 BOM 文件按 ANSI(GBK) 解码，中文注释会被误读为引号/括号等语法字符（曾致 update.ps1 整串解析错误、更新流程直接失败）；纯 ASCII 脚本（dnf_monitor/get_pid）除外 |
| 脚本校验 | 改完任何 ps1 后：① 确认 BOM=`EF BB BF`；② 用 **PowerShell 5.1 引擎** `Parser.ParseFile` 验证 0 错误（不能只用 pwsh7 验证，两者读法不同） |
| 命名 | 私有方法 `动词+名词`（`BuildDllPage`/`RefreshDllState`），页面字段带页前缀（`lbDllSt`/`_dllTbl`/`dtpTime`/`lbTimeNow`） |
| 构建 | 顺序编译：`dotnet build ServerUI.csproj -c Release` 成功后再编 `ServerUI-Win7.csproj`（共享 obj/，并行会 NETSDK1047） |

## 二、页面布局骨架

```
pgXXX.Padding = new Padding(10);                       // 页根（AntdUI.In.Panel，由 Build() 创建并加入内容区）
外层 TableLayoutPanel（Dock=Fill, BackColor=Transparent）
├─ 信息栏   行高 Absolute 52F   （左标签 + 右按钮）
├─ 主体卡片  行高 Percent 100    （Card(8,2) 包裹）
├─ 操作行   行高 Absolute 56F   （主按钮右对齐）
└─ 状态行   行高 Absolute 26F   （lbXXXSt，左对齐小字）
```

- 卡片：`Card(8, 2)`（圆角 8、边距 2 的 AntdUI 面板封装）；
- 可滚动列表：`AntdUI.In.Panel { Dock=Fill, AutoScroll=true }` 内放 `TableLayoutPanel { Dock=Top, AutoSize=GrowAndShrink, BackColor=Style.Get(Colour.BgContainer) }` —— 不透明背景防滚动残影；行高用 `RowStyle(SizeType.Absolute, h)`；
- 主题取色：`Style.Get(Colour.BgContainer)` 等，不写死深色/浅色值；
- 控件坐标一律用 `TableLayoutPanel` 网格（`.Add(ctrl, col, row)`），不手工摆坐标。

## 三、控件写法

| 控件 | 规范要点 |
|------|----------|
| 按钮 `AntdUI.Button` | 辅助方法 `B(text, TTypeMini.*, "IconSvg")`；`WaveSize = 0`；无边框时 `BorderWidth = 1F`；`Cursor = Cursors.Hand`；主要操作用 `TTypeMini.Primary`，危险操作用 `TTypeMini.Error`，成功/安装类用 `TTypeMini.Success` |
| 标签 `AntdUI.Label` | 辅助方法 `L(text, 字号)`；正文 8.5~9F、名称 9.5F Bold、标题 10F Bold；次要文字 `ForeColor` 灰阶 `120~150` | 
| 开关 `AntdUI.Switch` | `Size(40,22)`；禁用原因明确时 `Enabled=false` + `Cursor.Default`；状态变更事件统一走 `_dllSyncing` 防抖标志 |
| 数字 `AntdUI.InputNumber` | 取值用 `ctl.Num.Value`（可空 decimal）转 int；区间校验后写回 |
| 日期 `AntdUI.DatePicker` | `Format="yyyy-MM-dd HH:mm:ss"` 可含时间；`Placement` 下拉；修改不回传用户输入事件（自动格式化） |
| 滚动条 | 依赖 `In.Panel.AutoScroll`，不显式加 ScrollBar 控件 |

## 四、弹窗规范（独立 `internal class ... : AntdUI.Window`）

| 项 | 规范 |
|----|------|
| 窗口 | `StartPosition = FormStartPosition.CenterParent`、`ShowInTaskbar = false`、`Font = new Font("Microsoft YaHei UI", 9F)` |
| 快捷键 | `KeyPreview = true`；`Esc` 取消；确认键不吞回车（列表内输入时回车=输入框确认） |
| 按钮 | AntdUI 按钮，底部右对齐 + 弹性 spacer；不用 `MessageBox` 风格按钮 |
| 返回 | 公开静态工厂：`static string[] Ask(Form owner, ...)`，取消返回 `null`，`using var` 释放 |
| 确认/拦截 | 功能级确认统一 `MessageBox.Show(..., "标题（功能名）", YesNo, Question)`；错误 `Error`，信息 `Information` |

现有弹窗：`IniEditorForm`（分组管理：添加分组/添加键/删除键/删除分组）、`IniAskForm`（1~2 输入框通用询问）、`DllPickForm`（删除扩展选择列表）、`AlertDialog`（AntdUI 醒目弹窗）。

## 五、文案与配色约定

- 页面标题行：`★ Px.x 页面名 ★ — 一句话说明` 注释 + 页根首标签同步；
- 按钮文案：动词开头（应用更改 / 新DLL安装 / 添加扩展 / 删除扩展 / 刷新状态 / 打开补丁包目录）；
- 状态行三段式：`结果 + 数量/位置 + 去向`，如 `受管插件已启用 5 / 7 · 自定义扩展 2 个 → GameGaurd.ini`；未安装态单独给引导文案；
- 日志 `Lg(msg, color)`：成功 `Gn`、警告 `Or`、错误 `Rd`、常规 `Txt2`；前缀 `>>> [模块] 动作: 结果`；
- GameNative：必选插件锁定开关、取消勾选时应用被拦截（提示语固定）；
- 与启动器对齐的机制说明（`[Plugins]` 顺序加载、DLL/INI 均可、路径=游戏根目录文件名）在 使用说明 中保留。

## 六、DLL 扩展页行为状态机

```
_patchInstalled = 存在 GameGaurd.dll / az.dll 或 GameGaurd.ini 有 PluginN= 记录
├─ 未安装：空态提示「—— 未安装 DLL 扩展 ——」+ 引导【新DLL安装】；开关全灰；添加/删除扩展拦截
└─ 已安装：渲染 表头「已安装插件」→ 受管插件行（开关+名称+说明±编辑按钮）→ 自定义扩展分组（图标+名称+删除）
```

- `RefreshDllState()`：读 ini → 计算 iniSet/custom → 同步开关（GameNative 恒开锁定）→ `RebuildDllRows(custom)` → 刷新状态行与日志；
- `ApplyDllPatch()`：仅直写 ini（`Encoding.UTF8` 写入），`BuildPatchIni` 保留注释/非受管条目/自定义条目；
- `InstallPatchZip()`（设置页）：解压 `实用工具包\DLL覆盖\客户端补丁.zip` → 复制到游戏根（已存在配置 ini 不覆盖）→ 合并 ini（受管全启用）→ `_patchInstalled=true` + `RefreshDllState()`；
- 受管插件清单唯一维护点：`DllPlugins` 常量（7 项，元组：File/Name/Desc/Config）。

## 七、使用说明 / 疑难解答维护

- 条目结构：`(分组, 标题, 图标Svg, 副标题, 正文含\n与·换行)`；图标用 AntdUI 内置 `*Outlined` 系列；
- 更新文档同步：功能行为变化后，同步改「使用说明」对应条目 + 本文件 + `更新记录.md`。