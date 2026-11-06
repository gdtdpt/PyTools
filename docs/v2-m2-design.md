# v2-M2 启动器设计

状态：**方案已确认（2026-10-30 用户拍板拆批/图标缓存/拼音方案），待「开始编码」关口**
日期：2026-10-30
依据：`docs/v2-plan.md` §4-M2、`docs/v2-rewrite-matrix.md` §2（L1-L10）+ S10；v1 行为基准 = `src/main/app-loader.js` / `custom-apps.js` / `context-menu.js` / `result-actions.js`（v1-final tag）。

## 0. 范围与分批（已确认）

| 批次 | 内容 | 矩阵条目 |
|---|---|---|
| **M2a** | 扫描、lnk 解析、图标提取+落盘缓存、搜索（含拼音首字母）、连续滚动列表、动态高度、启动、死条目 | L1 L2 L3 L4 L6 L7 |
| **M2b** | Tab 操作菜单、右键登记、静默添加、设置窗口、开机自启 | L5 L8 L9 L10 S10 |

M2a 先交付验收，通过后再进 M2b——两批各自走完整闭环（编码→自验→真机验收→提交）。

## 1. 数据模型与扫描（L1，`Core/AppScanner.cs`）

- **AppEntry**（内存模型）：`Name / Path（.lnk 或 exe 原路径）/ TargetPath / TargetDir / Args（仅登记条目）/ Keywords[] / Initials[] / IconRef / Source（Scan|Custom）`。
- **扫描根**：`%APPDATA%\Microsoft\Windows\Start Menu\Programs` + `%PROGRAMDATA%\...\Programs` + `config.appDirs`（相对 `configDir` 解析），去重；递归深度 8；只收 `.lnk/.exe`。
- **身份键** `target|args`（小写），自定义清单同键覆盖扫描条目（显式登记优先）——对齐 v1。
- **关键词** = 去重小写的 [显示名, 目标文件名, 目标所在文件夹名]；排序 `zh-Hans-CN` localeCompare。
- **时序**：启动后台线程扫描（不阻塞呼出；扫描完成前列表区显示"正在扫描…"）；托盘"重新扫描"；登记增删后自动重扫。扫描结果一次性替换内存索引，UI 增量刷新。

## 2. lnk 解析（`Native/ShellLink.cs`）

- IShellLink + IPersistFile COM P/Invoke（零依赖标准做法），取 `target / args / iconPath / iconIndex / workDir`。
- 双层嵌套 lnk 最多解析 2 层（对齐 v1 `resolveTarget` 的 hops 限制）；解析失败按普通文件处理。
- 同时服务：扫描（M2a）、登记路径解析（M2b）。

## 3. 图标提取与落盘缓存（L1，`Core/IconCache.cs`，**v1 没有的改进，已确认**）

- **进程内** `System.Drawing.Icon.ExtractAssociatedIcon(iconPath)`（BCL 合法），不再像 v1 那样 spawn PowerShell 子进程。
- **落盘缓存**：exe 旁 `icons/` 目录，PNG 32×32；文件名 = iconPath 的 SHA1 前 16 位 + `-` + mtime 刻度（源文件变了自动失效重提取）。
- 启动/扫描时异步填充：命中缓存直接读 PNG（毫秒级），未命中才提取；提取失败用内置占位图。WPF 侧按 `IconRef` 缓存 `BitmapImage`，列表虚拟化下内存可控。
- 冷启动收益：二次启动不再重提取任何图标（弱机 <1s 指标的直接支撑）。

## 4. 拼音首字母（L7，`Core/Pinyin.cs`，**已确认方案**）

- 内嵌「汉字→首字母」映射表（EmbeddedResource，常用汉字集，约 50-100KB），纯本地零网络。
- **扫描时**为每个条目的 Name 与每个 Keyword 各生成一条首字母串，缓存进 AppEntry（搜索时纯字符串前缀比对，零开销）。
- 匹配 = 原关键词前缀 **或** 首字母串前缀；**只做首字母不做全拼**（`tx` 中「腾讯会议」，`tengxun` 不中）。
- 多音字取常用音（重庆→cq、银行→yh），极少数取不到目标音属已知取舍。
- 开关 `v2.pinyinSearch`（默认 true）；关闭后行为与 v1 完全一致。
- 不做视觉提示（不显示首字母串），保持界面干净。

## 5. 呼出窗：动态高度 + 连续滚动列表（L2/L6，改造 `Views/SummonView`）

- **动态高度（2026-10-30 用户提出）**：呼出空态**仅搜索框一行高**（v1 同款），M1 的常驻占位区取消；有结果时窗口向下动态展开。实现：`SizeToContent=Height` + 列表行空态折叠；默认形态高度变化走 120ms 动效，简洁模式直接跳变（动效令牌退化约定）；**顶边锚定不动**，`windowBounds` 位置记忆记顶边。
- **列表**：ListBox + VirtualizingStackPanel；条目 = 图标 + 名称 + 路径副标题；结果上限 10 条可视，超出连续滚动（`MaxHeight` 封顶）。
- **空查询**：列表隐藏，仅搜索框（**v1 同款**，已核对 v1 renderer.js `resultsEl.hidden = … && !query`；2026-10-30 设计修正——原稿"空查询显示全部应用"作废）；**有查询**：关键词/首字母过滤，空结果显示"无匹配应用"占位。
- **键位**（全量对齐矩阵 L6）：
  - ↑/↓ 逐条移动并滚动跟随；
  - Ctrl+J / Ctrl+K 逐条下/上；
  - Ctrl+N / Ctrl+P 步进 N 条（`v2.pageStep`，默认 5）；
  - 滚轮 / 滚动条全量可达；
  - 数字键 1-0 映射**当前可视区**第 1-10 条，角标锚定可视区：滚动后对可见行重编号（2026-10-30 用户验收定案，任意滚动位置每个可见条目都有号可按）；首可见行按真实容器索引取「露出 ≥60%」的第一行，不按行高估算；
  - 回车启动选中项；Esc 收起（M1 语义不变）。

## 6. 应用启动（L3，`Core/AppLauncher.cs`）

- **授权**：`isKnownApp` = 在自定义清单 **或** 位于任一扫描根之下（路径前缀判定），语义对齐 v1。
- **无参条目**：`Process.Start(UseShellExecute=true)` 打开原路径（.lnk 自带参数由 shell 处理；exe 直启）。
- **带参条目**（登记条目留存 args）：`UseShellExecute=false` + quote-aware 分词（v1 `splitArgs` 语义逐字符移植）+ `WorkingDirectory=目标目录`；`ComponentManager Win32Exception`/同步异常必须 try/catch 包住 `Process.Start` 本身（Win32 教训：非 PE 文件同步抛）。
- **两条路径统一**：回车与 Tab 菜单的"打开应用"都走 `AppLauncher.Open` 单一入口（v1 教训）。

## 7. 死条目（L4，对齐 v1.8.1）

- 启动前 `File.Exists(TargetPath)` 校验：不存在 → **若是登记条目则移除登记 + 重扫 + 列表刷新**，toast「目标不存在，已从列表移除：xxx」；扫描条目只提示不移除。
- 其他启动失败保留记录，toast 显示失败原因（含"文件可能不是有效的可执行程序"的可读化文案）。
- 失败后退出 Tab 模式（若处于其中），列表回到已清理状态。

## 8. Tab 操作菜单（L5，M2b）

- Popup 式菜单（非系统 ContextMenu，走令牌层），条目动作集对齐 v1 `createAppActions` 七项：打开应用 / 打开所在目录 / 复制应用名 / 复制应用路径 / 复制应用目录路径 / 在 CMD 打开目录 / 在 PowerShell 打开目录。
- **第 8 项「移除登记」（2026-10-30 用户确认新增，仅 `IsCustom` 条目显示）**：从 `custom-apps.json` 删除 + toast + 列表刷新。v1 缺手动移除出口（只有目标被删的自动移除），此项补体验缺口。
- 进入：Tab；↑↓ 选择 + 回车执行（可输入过滤动作，v1 同款）；Esc/Tab 退出；执行后收起呼出窗（"打开应用"走 §6 统一入口）；**执行失败 toast 原因并退出操作模式回已刷新列表**（v1 1.8.1 语义）。
- 终端打开走 conhost 包装（v1 等价）；VSCode 项目/script 类动作属 M4（vsc 内嵌工具），本期不含。

## 9. 右键登记（L8，M2b，`Core/ContextMenuRegistry.cs`）

- `Microsoft.Win32.Registry` 写 **HKCU\Software\Classes** 下 `exefile` + `lnkfile` 的 `shell\AddToPyTools` 静态动词（免管理员），command = `"<exe路径>" "--add-app=%1"`（**自包含 token**，沿用 v1 教训）；Icon 指向自身 exe。
- **点击即生效**：设置页开关切换即写/删注册表并回读实况，不进"保存"缓冲（Mino 教训：UI 区分「意图（config）」与「实况（注册表回读）」，不一致时明示）。
- **自愈**：启动时 `configured()` 比对完整期望命令串，不符（挪目录/旧格式）幂等重写——v1 `repair()` 等价。

## 10. 静默添加（L9，M2b）

- 命令行 `--add-app=<路径>` 自包含 token；二次启动经单实例通道转发给首实例后退出（M1 SingleInstance 需**补参数转发**——首实例从命名管道/EventWaitHandle 升级为带 payload 通知）。
- 首实例处理：不弹主界面；IShellLink 解析（≤2 层）→ 校验应用扩展名 → 查重 → 写 `custom-apps.json`（格式沿用 v1：`{version:1, apps:[{name,path,args,addedAt}]}`）→ 重扫 → **托盘气泡**反馈（成功/重复/失败原因）。
- 冷启动带该参数：静默驻留，不呼出窗口。

## 11. 设置窗口（L10+S10，M2b，`Windows/SettingsWindow.xaml`）

- 独立 Window（复用令牌层，双形态一致；普通窗chrome即可，非呼出壳）。
- 项目：主热键重录（按下组合即捕获）、剪贴板热键（占位，M3 启用）、开机自启（HKCU Run 项，S10）、右键菜单开关（§9 语义）、拼音搜索开关、翻页步长 `v2.pageStep`、简洁模式开关（重启生效）、清单管理（列表/移除/清空/打开所在文件夹）。
- 托盘菜单补「设置」项。

## 12. 配置新增（`v2.` 前缀，走 AppConfig 保留未知键机制）

| 键 | 默认 | 说明 |
|---|---|---|
| `v2.pinyinSearch` | `true` | 拼音首字母搜索 |
| `v2.pageStep` | `5` | Ctrl+N/P 步进条数 |
| `v2.liteMode` | `false` | （M1 已有）设置页补开关 |

## 13. 自验方式

- `--selftest` 新增：拼音表抽检（腾→T、重(重庆)→C）、quote-aware 分词用例、扫描合并优先级（临时目录构造）、config 往返、死条目移除逻辑（临时清单）。
- UI 截图自验：空态（仅搜索框）/ 有结果展开 / 滚动角标重算 / 双形态各一组。**调试辅助开关：`--debug --query=<词>`**（呼出即预填搜索词，结果态截图用，长期保留）。
- 基线实测写回本文档：空态呼出耗时、带结果呼出耗时、过滤响应时间、二次启动图标加载耗时。

### 基线（2026-10-30，开发机 Debug 实测）

| 项 | 数值 | 备注 |
|---|---|---|
| 空态呼出耗时 | 55ms | M1 为 84-168ms，SizeToContent 后反降 |
| 后台扫描 | 138 应用，~0.4s | 不阻塞呼出；完成即上线结果 |
| 图标落盘缓存 | 39 张 / 142KB | 二次启动直接读 PNG，零重提取 |
| 拼音端到端 | `wx` → 微信/卸载微信 | 截图实测命中，多音字变体生效 |
| selftest | 14 项全 PASS | 含拼音/分词/扫描合并/死条目/授权 |
| exe 体积 | 166KB | 含 75.6KB 拼音表 |

## 14. 验收清单（用户真机）

**M2a**：
1. 呼出 → 只有搜索框一行；输入字符 → 列表向下展开，清空 → 缩回
2. 搜 `tx` → 出「腾讯会议」类中文应用（拼音开关开）；设置 `v2.pinyinSearch:false` 重启 → 不再命中
3. 回车启动应用；启动后窗口收起、焦点回原窗口
4. Ctrl+J/K/N/P、滚轮、滚动条、数字键 1-0（锚定可视区，滚动后重编号）全部可用
5. 登记一个绿色 exe，删除其目标文件后回车启动 → toast 提示且条目消失；其他启动失败保留记录
6. 托盘「重新扫描」可用；`icons/` 目录出现缓存 PNG，二次启动明显变快
7. `--selftest` 全过

**M2b**：Tab 七项动作、右键菜单注册/取消/自愈、`--add-app=` 静默添加 + 气泡、设置窗口全部项目、开机自启 Run 项。
