# v2.21 任务书：更新AUM 改为「发行包直替 + 有环境时源码编译」

> 工程：`E:\Game\S4A21_CN\AUM管理组件\ServerUI.Web\`
> 只改本工程 + 仓库根新增 `.github/workflows/` 与 `tools/make-release.ps1`；不部署、不推送远端。
> 目标：长期可用、判定严格、失败可回滚。禁止"能跑就行"的宽松实现。

---

## 0. 背景与现状

- 旧 `CheckAndUpdateAumAsync` 走 `SelfUpdateService`：**下载源码 zip → dotnet publish → 替换 exe**。
  该通道编译的是 `ServerUI/ServerUI.csproj`（旧 WinForms），在重构版里执行会**回退界面**，所以当前被
  `AUM_ALLOW_LEGACY_SELFUPDATE` 粗暴封死（`blocked:true`）。本次要彻底替换这段逻辑。
- 仓库目前 **0 Release / 0 tag / 0 workflow**，没有可下载的发行包 —— 需要一并提供发版流水线。

## 1. 策略（严格判定，二选一，互斥）

```
hasBuildEnvironment = SDK>=10.0 && PowerShell>=5.1 && 磁盘可用>=2GB
                     && 下载的源码里存在 ServerUI.Web/ServerUI.Web.csproj
                     && 存在 web/index.html
if (hasBuildEnvironment && !forcePackage)  → 源码编译（primary）
else                                        → 发行包直替（fallback）
编译失败 → 自动回退到发行包（必须记录原因）；发行包也失败 → 报错并保留现场
```

规则：
- **源码编译路径必须先下载源码 zip 并校验**「含 `ServerUI.Web/ServerUI.Web.csproj` + `web/index.html`」，
  这不只是探测环境，更是防止有人把旧 WinForms 代码发上来导致界面回退。缺任一项 → 直接判定"不可编译"，
  走发行包，并在日志写明原因。
- `forcePackage` 由 UI 提供（下方 §6 的"强制使用发行包"入口）；默认 false。
- **任何路径都不允许把版本降级**：目标版本必须 > 当前版本（`force` 同版本重装除外）。

## 2. 版本与选择

- 版本来源：仓库根 `AUM-version.txt`（现有机制），格式 `MAJOR.MINOR[.PATCH]`，逐段数字比较，缺段补 0。
- 只接受 **非 draft、非 prerelease** 的 Release；tag 必须为 `v<version>`，且 `AUM-version.txt` 的版本必须与之相等
  （不等 → 该 Release 视为不可信，跳过并记录）。
- 选最高的 `releaseVer > currentVer`；没有 → "已是最新"。
- `force`：允许同版本重装/修复，但仍禁止降级（除非设置 `AUM_ALLOW_AUM_DOWNGRADE=1`，仅测试用）。

## 3. 发行包协议（长期契约，写入文档）

Release 资产（每次发版固定 3 个 + 1 份清单）：

| 资产 | 说明 |
|---|---|
| `latest.json` | 清单，见下 |
| `ServerUI-fd.zip` | 框架依赖单文件版，内含 `ServerUI-有依赖版.exe` |
| `ServerUI-sc.zip` | 自包含单文件版，内含 `ServerUI-无依赖版.exe` |
| `ServerUI-compat.zip` | 兼容模式，内含 `ServerUI-兼容模式.exe` + `.config`（可选，缺失不算错） |

`latest.json`（UTF-8，无 BOM）：

```json
{
  "schema": 1,
  "version": "2.21",
  "channel": "stable",
  "assets": [
    { "kind": "fd", "name": "ServerUI-fd.zip", "sha256": "<64hex>", "size": 1950000, "inner": "ServerUI-有依赖版.exe" },
    { "kind": "sc", "name": "ServerUI-sc.zip", "sha256": "<64hex>", "size": 70600000, "inner": "ServerUI-无依赖版.exe" }
  ],
  "minSupportedVersion": "2.0",
  "notesUrl": "https://github.com/118coder/ServerUI-AUM-S4A21/releases/tag/v2.21"
}
```

**清单严格校验**（任一不满足即整份拒绝，不得"跳过坏项继续"）：
- `schema` 必须 == 1；`channel` 必须 == `stable`；`version` 合法且 == 该 Release 的 tag 版本。
- `assets` 非空；每个 `sha256` 必须 ^[0-9a-f]{64}$（小写化后比较）、`size` 在 1..200MB、
  `kind` ∈ {fd,sc,compat}、`name` 仅允许 `[A-Za-z0-9._-]+` 且以 `.zip` 结尾、`inner` 同样字符集且以 `.exe` 结尾。
- 必须存在**当前运行形态**对应的资产：自包含版（exe > 50MB）→ 需要 `sc`；否则需要 `fd`。
  若缺 → 若存在另一形态且该形态运行时可用（见下）则改用，否则报错。
- `minSupportedVersion` 若 > 当前版本 → 拒绝并提示"版本过旧需手动升级"。

## 4. 下载与校验（严格）

来源（按顺序，前一个不可用才走下一个）：
1. `latest.json` 资产（`https://github.com/<repo>/releases/download/v<ver>/latest.json`）
2. Releases API `GET /repos/<repo>/releases/latest`（读 `assets[]` 的 `digest` 若存在）

要求：
- **仅 HTTPS**，主机白名单：`github.com`、`api.github.com`、`objects.githubusercontent.com`、
  `raw.githubusercontent.com`。重定向最多 5 跳，**最终主机也必须在白名单内**。
- 边下边算 SHA256，落临时文件；下完校验 `sha256` 与 `size` **都**必须与清单一致，任一不符 → 删除并失败。
- 拒绝：响应 `Content-Type` 为 HTML、或首 512 字节包含 `<!DOCTYPE`/`<html`（门户劫持/错误页）。
- 若 `Content-Length` 存在且与 `size` 不符 → 立即失败，不浪费流量。
- 超时：连接 15s、整体 180s；不做断点续传（保证原子性）。
- 临时目录：`%TEMP%\ServerUI-aum-update\<guid>\`，结束后必删。

## 5. 包校验与替换

**ZIP 校验**：
- 必须包含清单声明的 `inner` 文件名；缺失 → 失败。
- 拒绝 zip-slip：任何条目名含 `..`、绝对路径、盘符、反斜杠开头；条目数 ≤ 200；解压总量 ≤ 300MB；
  压缩比 > 100:1 视为可疑 → 失败。
- 解压出的 exe 必须通过 **PE 校验**：`MZ` + `e_lfanew` 合法 + `PE\0\0` + Machine == 0x8664；
  文件长度 > 200KB。
- **版本校验（关键）**：`FileVersionInfo.GetVersionInfo(exe).FileVersion` 的 `Major.Minor` 必须等于
  清单 `version` 的 `Major.Minor`。不等 → 失败（防止资产错配/降级/挂错包）。

**替换（原子 + 可回滚）**：
1. 先把校验通过的 exe 复制到目标同目录的 `<目标名>.new`。
2. 运行 `<目标名>.new --healthcheck <tokenFile>`，要求 15s 内退出码 0，且 token 文件内容
   `{"ok":true,"version":"<version>","exe":"<path>"}`，版本必须匹配。失败 → 删除 .new，中止。
3. 备份当前 exe：`<目标名>.bak-<oldVer>-<yyyyMMddHHmmss>`（同目录，保留 1 份，旧的删掉）。
4. `File.Replace(new, target, null)` 或 `MoveFileEx(REPLACE_EXISTING|WRITE_THROUGH)`；
   **运行中的 exe 允许改名**，所以若 Replace 失败，改用"改名 current → .bak，再 move new → target"。
5. 任一步失败 → 立刻把 `.bak` 还原为 target，删除 `.new`，报错并保留日志。
6. 成功后：启动 target（正常参数），然后本进程退出（复用现有退出清理）。
7. 若连"改名"都被占用（杀软/句柄），生成一个 PowerShell 替换脚本（等旧进程退出→复制→启动→自清理）
   作为最后兜底，脚本内同样做 hash 校验与备份，并写结果到 `<AUM数据目录>\AUM更新结果.txt`。

> 替换只处理**当前运行的那个 exe**。另一个形态（例如从无依赖版更新时）不自动替换，
> 但在日志中提示"另一版本未同步，如需请手动替换"，避免误改用户没在用的文件。

## 6. UI 与命令

- `aum.update` 参数扩展：`{ force?: bool, forcePackage?: bool }`。
- 「更新AUM」点击流程：先检测 → 弹出**主题化确认框**，正文必须包含：
  - 当前版本 → 目标版本
  - 将采用的策略（"源码编译" / "发行包替换"）及原因（例如"已检测到 .NET 10 SDK 与 PowerShell 5.1"）
  - 发行包大小（走包路径时）
  - 失败会自动回退/回滚的说明
- 新增「强制使用发行包」入口：设置与关于页「实用工具」内加一行文本按钮（小号、Default），
  点击 = 以 `forcePackage:true` 调用，用于 SDK 环境但想省时间的用户。文案与按钮名自拟但要清楚。
- 进度：复用现有 progress 通道，阶段文案至少包含：检测版本 / 下载清单 / 下载包 / 校验 / 解压 / 健康检查 / 替换 / 重启。
- 失败提示必须给可操作建议（看日志 / 手动替换路径 / 网络）。

## 7. 发版流水线（长期可用）

新增 `tools/make-release.ps1`：
- 参数：`-Version <x.y[.z]>`、可选 `-OutDir`。
- 步骤：改 `AUM-version.txt` → 依次 publish fd/sc/compat → 单文件化命名 → 打 3 个 zip（内含 exe；
  compat 额外含 `.config`）→ 计算 SHA256/大小 → 生成 `latest.json`（schema/version/channel/assets/notesUrl）。
- 幂等：重复执行覆盖 `OutDir`，不污染仓库；输出 `release-files/` 清单。
- 校验：若 `AUM-version.txt` 与 `-Version` 不一致则先改文件并提示。

新增 `.github/workflows/release.yml`：
- 触发：`push: tags: ['v*']` + `workflow_dispatch`（输入 version）。
- Windows runner：`dotnet publish` 两个变体（同 §7 命令），运行 `tools/make-release.ps1`，
  用 `softprops/action-gh-release`（或 GitHub CLI）创建 Release，上传 4 个资产。
- 必须 `prerelease: false`、`draft: false`；tag 与 `AUM-version.txt` 不一致 → 失败退出。
- 不写入任何 token（用 `GITHUB_TOKEN`）。

## 8. 测试（必须全绿，缺一不可）

### 8.1 单元/集成（新增 `--updatetest`，沙箱）
在本地起一个静态 HTTP 服务（仅 127.0.0.1），提供 fixture 清单与 zip，验证：

| 用例 | 期望 |
|---|---|
| 正常升级（fd） | 校验通过、包被解压、替换后版本正确 |
| sha256 错误 | 拒绝，且不产生 `.new`/`.bak` |
| size 不符 | 拒绝（下载前即失败） |
| 清单 version 与 tag 不符 | 拒绝 |
| 清单缺当前形态资产 | 拒绝并提示 |
| zip 缺 inner | 拒绝 |
| zip 含 `../evil.exe` | 拒绝（zip-slip） |
| inner 是 HTML/非 PE | 拒绝 |
| inner 版本 9.9 与清单 2.21 不符 | 拒绝 |
| 降级（目标 2.19 < 当前 2.20） | 拒绝 |
| 同版本非 force | 提示已最新，不下载 |
| 同版本 force | 允许重装 |
| 清单元数据缺字段/非法 sha256 | 整份拒绝 |
| 编译路径：源码缺 `ServerUI.Web.csproj` | 判定不可编译 → 转发行包 |
| 编译路径：有构建环境 | 选择源码编译（不下载发行包） |

### 8.2 现有回归
- `dotnet build` 0 警告 0 错误。
- `tools/ui-shots.js` → `layout.txt` 0 个 `!!`（新增：确认框含策略与版本、强制发行包按钮存在且可点）。
- `tools/app-shots.js` 真实宿主 0 失败；`--selftest` exit 0 且新增 `update` 字段
  （当前版本、策略判定结果、是否检测到构建环境）。
- `--healthcheck <file>` 单独可用：exit 0 且 token 内容正确。

## 9. 文档
- 新增 `ServerUI.Web/docs/AUM-UPDATE.md`：清单协议、发布步骤（`make-release.ps1` + 打 tag）、
  策略判定表、失败回滚行为、常见问题（无 Release 时怎么办）。
- 同步《更新记录与安装目录结构》《ServerUI-维护参考》《ServerUI-WebView2重构说明》对应段落；
  移除"更新AUM 被护栏阻止"的旧描述（改为"已支持发行包替换/源码编译双通道"）。

## 10. 禁止事项
- 不得使用非 HTTPS、不得放宽主机白名单。
- 不得在校验失败后"继续尝试替换"。
- 不得删除用户现有 exe 后才验证（必须先验证再替换）。
- 不得把 `AUM_ALLOW_LEGACY_SELFUPDATE` 护栏逻辑保留成隐形分支（该环境变量在本功能中废弃）。
- 不得在没有任何 Release 时假装成功；要么编译（环境具备），要么给出明确提示。

汇报：改动文件、构建、每个测试用例结果、清单/流水线产物、偏差。
