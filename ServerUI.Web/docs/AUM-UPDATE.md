# 更新AUM：发行包直替 + 源码编译（长期方案）

> 适用版本：v2.20+ · 实现位置：`Bridge/Aum/`、`--healthcheck`、`tools/make-release.ps1`、`.github/workflows/release.yml`

---

## 1. 为什么改成双通道

旧实现只有一条路：下载源码 zip → `dotnet publish` → 替换 exe。问题有三个：

1. **对用户要求太高**：必须装 .NET 10 SDK，且编译要几分钟到十几分钟。
2. **仓库没有 Release**：用户拿不到"官方编译好的包"，只能自己编。
3. **编译对象曾经错配**：旧代码编的是 `ServerUI/ServerUI.csproj`（WinForms），在 WebView2 版本里执行会**静默回退界面**。

新方案：

```
有构建环境 → 源码编译（编译失败自动回退发行包）
否则       → 直接下载 GitHub Release 的发行包，校验后原子替换
```

---

## 2. 策略判定（严格）

```
hasBuildEnvironment =
      .NET SDK >= 10.0
   && PowerShell >= 5.1
   && 目标盘可用空间 >= 2GB
   && 下载到的源码包含 ServerUI.Web/ServerUI.Web.csproj
   && 下载到的源码包含 ServerUI.Web/web/index.html
```

最后两条不是"探测环境"，而是**防止把旧 WinForms 源码当成可编译源码**——只有同时具备新工程结构与构建工具，才允许走编译。

| 条件 | 策略 | 说明 |
|---|---|---|
| 上述全部满足且未强制发行包 | `compile` | 源码编译；失败自动回退 `package` 并记录原因 |
| 缺任一条件 | `package` | 直下发行包 |
| `forcePackage: true`（设置页入口） | `package` | 跳过探测与编译 |
| 目标版本 <= 当前版本 | 已是最新 | 除非 `force`（同版本重装）；**永不降级** |

`force` 允许同版本重装（修复），仍拒绝降级。测试可通过 `AUM_ALLOW_AUM_DOWNGRADE=1` 放开降级，生产不设该变量。

---

## 3. 发行包协议（长期契约）

每次发版固定上传 4 个资产：

| 资产 | 内容 |
|---|---|
| `latest.json` | 清单（见下） |
| `ServerUI-fd.zip` | 内含 `ServerUI-有依赖版.exe`（框架依赖，需 .NET 10） |
| `ServerUI-sc.zip` | 内含 `ServerUI-无依赖版.exe`（自包含） |
| `ServerUI-compat.zip` | 可选，内含 `ServerUI-兼容模式.exe` + `.config` |

```json
{
  "schema": 1,
  "version": "2.21",
  "channel": "stable",
  "minSupportedVersion": "2.0",
  "notesUrl": "https://github.com/118coder/ServerUI-AUM-S4A21/releases/tag/v2.21",
  "assets": [
    { "kind": "fd", "name": "ServerUI-fd.zip", "sha256": "<64hex>", "size": 1900000, "inner": "ServerUI-有依赖版.exe" },
    { "kind": "sc", "name": "ServerUI-sc.zip", "sha256": "<64hex>", "size": 70000000, "inner": "ServerUI-无依赖版.exe" }
  ]
}
```

**清单校验（任一不满足即整份拒绝，不会"跳过坏项继续"）**

- `schema == 1`、`channel == "stable"`
- 清单 `version` == 该 Release tag 版本（`v` 前缀去掉）== `AUM-version.txt`
- 每个资产：`sha256` 为 64 位小写十六进制、`size` 在 `1..200MB`、`kind ∈ {fd,sc,compat}`
- `name` / `inner`：允许中文（发布物本来叫 `ServerUI-有依赖版.exe`），但禁止 `/ \ : * ? " < > |`、控制字符、`..`、前导点、结尾点/空格；长度 ≤ 120
- 必须存在**当前形态**对应的资产：自身 exe > 50MB（自包含）要 `sc`，否则要 `fd`
- `minSupportedVersion` 若高于当前版本 → 拒绝，提示手动下载完整包

---

## 4. 下载与包校验

**网络**
- 仅 HTTPS；主机白名单 `github.com` / `api.github.com` / `objects.githubusercontent.com` / `raw.githubusercontent.com`
- 重定向最多 5 跳，**最终主机也必须在白名单内**
- 边下边算 SHA256；`Content-Length` 存在且与清单 `size` 不符 → 立即失败
- 拒绝 HTML 响应（门户劫持/错误页）：首 512 字节含 `<!DOCTYPE` / `<html` 即失败
- 连接超时 15s、整体 180s；临时目录 `%TEMP%\ServerUI-aum-update\<guid>`，结束必删

**ZIP**
- 必须包含清单声明的 `inner`；条目数 ≤ 200；解压总量 ≤ 300MB；压缩比 > 100:1 视为可疑
- 拒绝 zip-slip：条目名含 `..`、绝对路径、盘符、反斜杠开头

**EXE**
- PE 头：`MZ` + `e_lfanew` 合法 + `PE\0\0` + `Machine == 0x8664` + 长度 > 200KB
- **版本防错配**：`FileVersion` 的 `Major.Minor` 必须等于清单版本（防止资产挂错、误降级）

---

## 5. 替换流程（原子 + 可回滚）

1. 校验通过的 exe 复制为 `<目标>.new`（复制后重新比哈希）
2. 在**目标目录**用唯一临时名（`aum-probe-*.exe`）运行 `--healthcheck <tokenFile>`
   - 15s 内退出码 0，token 内 `ok:true`、`version` 的 Major.Minor 与目标一致、exe 位于目标目录
   - 为什么必须在目标目录：单文件版依赖同目录的自解压布局，放到临时目录会报 `application to execute does not exist`
3. 备份当前 exe 为 `<目标>.bak-<旧版本>-<yyyyMMddHHmmss>`（同目录，只保留最近 1 份）
4. `File.Replace` 原子替换；失败则"改名旧 → 移动新 → 目标"
5. 任一步失败 → 立即用 `.bak` 还原、删除 `.new`，报错并保留日志
6. 成功后启动新 exe，本进程走既有退出清理
7. 极少数连改名都被占用（杀软/句柄）→ 生成 PowerShell 兜底脚本（同样做 hash 校验 + 备份 + 结果文件 `AUM更新结果.txt`）

**只替换当前运行的那个 exe**。另一个形态（例如自包含版更新时不碰有依赖版）只在日志提示"另一版本未同步，如需请手动替换"，避免误改用户没在用的文件。

---

## 6. 发版步骤

```powershell
cd <repo>
powershell -ExecutionPolicy Bypass -File tools\make-release.ps1 -Version 2.21
```

脚本会：写 `AUM-version.txt` → publish 三个变体 → 打 3 个 zip → 算 SHA256/size → 生成 `latest.json` → 输出到 `release-files/`。

然后：

```bash
git add AUM-version.txt
git commit -m "release: v2.21"
git tag v2.21
git push origin main --tags
```

`.github/workflows/release.yml` 在 tag 推送时：
- 校验 `AUM-version.txt` 与 tag 一致（不一致直接失败，防错配）
- 重新构建并校验 `latest.json` 里的 sha256/size
- 创建**非 draft、非 prerelease** 的 Release 并上传 4 个资产

也可以在 Actions 页面 `workflow_dispatch` 手工指定版本。

---

## 7. 常见问题

**Q：仓库还没有 Release，点更新会怎样？**
明确报错「没有可用的 Release：… 提示：先在仓库打 tag 并运行发布流水线」。不会假装成功，也不会回退界面。

**Q：我有 SDK，为什么还是走了发行包？**
看确认框里的「判定原因」。常见原因：源码结构不符（仓库里没有 `ServerUI.Web/`）、PowerShell < 5.1、磁盘 < 2GB、或勾选了「强制使用发行包」。

**Q：更新失败会不会把当前 exe 弄坏？**
不会。所有校验都在替换前完成；替换失败会用 `.bak` 还原。备份保留在同目录，可手工回滚。

**Q：另一形态的 exe 会一起更新吗？**
不会，只更新当前运行的那个。日志会提示另一形态未同步。

**Q：能降级吗？**
不能（生产）。仅测试可用 `AUM_ALLOW_AUM_DOWNGRADE=1`。

---

## 8. 测试

```powershell
# 15 类校验/策略用例（本地 127.0.0.1 静态服务，不联网）
ServerUI.exe --updatetest

# 单独验证健康检查
ServerUI.exe --healthcheck "%TEMP%\hc.json"

# 只读能力快照
ServerUI.exe --selftest    # 含 update.strategy / buildEnv
```

`--updatetest` 覆盖：正常升级、sha256 错、size 错、清单版本与 tag 不符、缺形态资产、zip 缺 inner、zip-slip、inner 非 PE、inner 版本不符、降级、同版本非 force、同版本 force、清单缺字段、源码结构不符→发行包、有环境→编译且编译失败自动回退。
