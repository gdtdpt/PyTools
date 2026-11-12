# 当前任务

> **仓库剥离说明（2026-11-06）**：本仓库由旧仓库 `pytools` 的 `v2/` 子目录经 `git subtree split` 剥离而来，26 个提交历史连续（作者已统一重写为 gdtdpt）。文中提及的 `40afd24` 等提交号在剥离重写后已变化（旧号仅作历史索引）；提及 `v2/...` 的路径一律对应本仓库根。

状态：**2.0.0-rc8 预发布包构建，待双屏/单屏真机验证（2026-11-12）**，rc1 之后新增待验收提交：var 截断气泡（`f65b396`）、多行编辑区滚动条贴底 + encode/regex 自动换行（`d4a03a3`）、暗色+简洁模式整窗发白修复（`f81d005`）、呼出窗跟随鼠标所在屏（`addf62a`，混合 DPI 物理像素返工 + 旧偏移/旧坐标作废见下）、带参 lnk 启动报 193 修复（2026-11-09 真机踩到：Chrome 开始菜单快捷方式自带 `--source-shortcut-location=start-menu`，CreateProcess 直接对 .lnk 报 ERROR_BAD_EXE_FORMAT；修复为带参分支 spawn 解析后的 TargetPath + 存在性检查按真实目标判定 + SaveSummonOffset 补落盘，selftest 101 项全过，Chrome 条目探针实测启动成功）；tools 工具列表 + 内置工具图标已验收（剥离前提交 `40afd24`，selftest 96 项全过；含验收反馈修复：列表行图标-文字间距）；明暗主题已验收提交（`fab53a0`，2026-11-05，selftest 94 项全过）；code-naming 已验收提交（`3f5483e`，2026-11-05，M4 AI 批第四消费者收官，selftest 93 项全过）；ai-translate 已验收提交（`f08fb91`，2026-11-05，M4 AI 批第三个消费者，selftest 90 项全过）；剪贴板文件条目打磨 + 写回重试 + 滚动复位已验收提交（`e776f9f`，selftest 88）；ai-ocr 已验收提交（`46cf1ff`，M4 AI 批第二个消费者，selftest 87）；regex AI 生成 POC 已验收提交（`dfc3b9d`）；diff F3 备用键 N/P 已验收（`31794b4`）；diff 行号列水平固定已验收（`6f4cb64`）；text-diff 已验收提交（2026-11-04）；regex-tool / S10 自启清理已提交（2026-11-04）；此前 UI 控件规范化（`155e9a9`）/ cron（`e1e1ea7`）/ encoding-converter（`6af788d`）/ format 间距（`4ca3c72`）/ 剪贴板 alpha（`05daf1d`）已验收提交；dev-values 用户暂缓，env-switcher 归 M5；另有待排期需求「diff 历史记录」（见下）
更新时间：2026-11-09

## 呼出窗跟随鼠标所在屏（2026-11-09 定案，11-10 全程物理像素，11-12 旧偏移/旧坐标作废，待真机验收）

拍板：选屏依据 = **鼠标位置**（用户确认）；不加设置项。

**旧偏移作废（2026-11-12 真机反馈：两屏都能显示但都压在左下角）**：rc5 时代以错误坐标语义落盘的 `v2.summonOffsetX/Y`（且部分值来自飞屏状态的 clamp 残迹）被 rc6 直接信任——换算后超出屏高 → `ClampIntoWork` 钳到「屏底保留 120px 可见」+ 左缘，正是截图的左下角贴边形态。修法：新增 **`v2.summonOffsetVer`（当前 = 2）** 标记偏移的坐标语义版本；`Load` 时版本缺失/不符一律作废（NaN），呼出走默认位（目标屏水平居中、垂直上 1/3），首次收起重新落盘并写入版本键；`SaveSummonOffset`/`WriteBack`/`ResetBounds` 三处同步维护版本键。selftest 103 项全过（新增：无版本键偏移作废、版本不符作废、保存后版本键落盘）。

**旧绝对坐标沿用分支删除（2026-11-12 真机反馈：单屏 rc7 变左上角）**：rc7 `EnsureOnScreen` 还有「旧 windowBounds 落在本屏工作区内则按物理像素沿用」的迁移分支——但历史版本落盘的 `windowBounds` 全是 **DIP** 口径（WPF Left/Top），当物理像素用在 scale≠1 屏上坐标只有一半 → 落在左上区域，且首次收起后偏移记忆忠实固化错位。修法：删除该分支，无有效 ver=2 偏移一律走默认位；`EnsureOnScreenLegacy`（DIP×DIP 口径一致）保留为句柄/显示器取不到时的兜底。探针复现（旧 DIP windowBounds + 无版本键）：窗口落在 rel x=0.50/y=0.40（水平居中、垂直上 1/3），Esc 后新语义落盘 offset=物理位÷scale 精确。

**混合 DPI 返工原因（2026-11-10 真机反馈：高分辨率屏呼出偏左下、低分辨率屏看不到窗口）**：首版把「物理像素工作区」用**单一全局系数**折算成 DIP（`VirtualScreenWidth / SM_CXVIRTUALSCREEN`，本质是主屏/系统 DPI），再交给 WPF 的 `Left/Top`。混合 DPI 下每块屏的系数不同——副屏工作区原点被按主屏 DPI 折算后必然错位（缩放比 200% 的屏偏将近一屏宽度，100% 的屏整窗飞出）。⇒ 定位链一律改为**物理像素**。

- **`Core/ScreenLayout.cs`**：`ScreenInfo`（工作区物理像素 + 该屏 DPI/Scale）；`CursorScreen()`（GetCursorPos→MonitorFromPoint→GetMonitorInfo）/`ScreenForWindow(hwnd)`（MonitorFromWindow）/`TryGetWindowRect(hwnd)`（GetWindowRect 物理像素）；`EffectiveDpi()` 走 `Shcore.GetDpiForMonitor(MDT_EFFECTIVE_DPI)`，逐屏取值、失败按 96 兜底；`WorkAreaDip()` 只在写配置时用（物理 ÷ **该屏** Scale）；`ClampIntoWork` 纯函数不变（单位无关）。
- **`Native/NativeMethods.cs`**：+GetCursorPos/MonitorFromPoint/MonitorFromWindow/GetMonitorInfo/GetDpiForMonitor/GetWindowRect/SetWindowPos/GetSystemMetrics + POINT/RECT/MONITORINFO 结构（嵌套类内，引用处带 `NativeMethods.` 前缀）。
- **`App.xaml.cs` 定位链全部走物理像素**：
  - `EnsureOnScreen`：先 `EnsureHandle()` 拿句柄 → 取鼠标所在屏 → 目标位置 = 屏工作区原点（物理）+ 配置偏移 × **该屏 Scale**；新配置走偏移，旧绝对坐标若落在本屏工作区内按物理像素沿用（单屏 100% 时代相等），否则水平居中、垂直上 1/3；`ClampIntoWork` 用物理像素口径（minVisible 也乘 Scale）→ `PlacePhysical()` 用 `SetWindowPos(SWP_NOZORDER|SWP_NOACTIVATE)` 落位，高度传 0 交给 SizeToContent。
  - `SettlePlacement()` 两拍（立即 + 200ms）兜住 DPI 变更回调的异步重算——跨屏呼出时 WPF 会按新屏 DPI 重排，晚一拍校正防偏移。
  - `SavePosition()`：改从 `GetWindowRect` 读物理像素，按窗口所在屏 Scale 折回 DIP 后写「相对该屏工作区的偏移」+ 旧 windowBounds；三处落盘点（收起/工具还原点/退出）统一走这里。
  - `TargetRectFor` clamp 参照从虚拟桌面收窄到「窗口当前所在屏」（固定尺寸模式不再骑缝）。
  - `EnsureOnScreenLegacy()`：句柄/显示器信息取不到时的原口径兜底。
- **`Core/AppConfig.cs`**：+`SummonOffsetX/Y`（v2.summonOffsetX/Y，NaN=未迁移）；`SaveSummonOffset` **自己落盘**（此前依赖后续 SaveBounds 顺带写，selftest 单独调用即暴露）；ResetBounds 一并清除；WriteBack 只在非 NaN 时写键（JavaScriptSerializer 不支持 NaN）。
- **`Core/SelfTest.cs`**：+2（ClampIntoWork 五断言；偏移读写/NaN 不落盘/ResetBounds 回环），累计 **102**。
- **单屏 200% 探针回归**（.Probe 锁 + 临时 config，probe 自身 PerMonitorV2）：首屏落位完全在工作区内、偏移落盘 (39.5, 75) 与物理位精确对应、移鼠标后热键再呼出**位置精确复现**；cron 固定尺寸模式 200% 下尺寸/文字清晰无裁切。⚠️ 探针 exe 必须在改锁名**之后**构建（旧 exe 会被用户实例静默顶掉，两次踩中）。

**双屏真机验收清单（用户执行）**：
1. 鼠标在 A 屏呼出 → A 屏记忆位；鼠标移到 B 屏呼出 → B 屏对应位（同一相对位置）
2. 拖窗到 B 屏收起 → B 屏呼出位置 = 拖动后位置
3. cron/regex/translate/naming 固定尺寸模式同样跟随、不骑缝
4. 混合 DPI：高分辨率屏呼出居中/记忆位准确，低分辨率屏可见且完全在工作区内
5. 两屏来回呼出多次不漂移（偏移是相对各自屏工作区的，不是相对光标或全局原点）

### tools 工具列表 + 工具图标已实现清单（2026-11-06，待真机验收）

拍板结论：别名 `tools`/`工具`；图标走 C 混合（应用图片 → 工具矢量图标 → 首字头像三层回退）；打开工具后 Esc 不回列表；不加类型徽标。

- **`Themes/Icons.xaml`（新）**：11 枚 24×24 线框 `StreamGeometry`（`Icon.<Id>`，stroke 由模板 Path 提供 `Text.Secondary` + 圆头线帽，深浅主题自动适配）；「工具列表」入口无图标走首字回退。
- **`Core/BuiltinCommands.cs`**：新条目 `tools`（Kind="tool-list"，置于首位）；各条目 +`IconKey`；`All()`（全部工具条目，不含 tools 自身）与 `FilterToolList(query)`（空查询列全部；别名/标题/标题拼音首字母 Rank 或副标题子串命中）。
- **`Core/AppEntry.cs`**：+`IconKey`。
- **`Views/SummonView.xaml`**：图标列三层叠放（Image / Path / 首字 Border）；提示条左侧加「工具列表」Accent 徽标（ModeBadge，模式时可见）。
- **`Views/SummonView.xaml.cs`**：`ItemVM` +`IconGeometry`（按 IconKey 查资源）/+`FallbackVisible`（Icon setter 同步通知，应用图标异步加载后首字自动让位）/+`FallbackLetter`；`_toolListMode` 状态：`EnterToolListMode`（清框、placeholder「过滤工具…」、全量列表）/`TryExitToolListMode`/`ExitToolListModeInternal`（清框收列表回普通空态——首版漏了这步，Esc 后残留半截列表，已修）/`RefreshToolList`（空态「无匹配工具」）；ResetForSummon 重置模式；键位完全沿用全局约定。
- **`App.xaml.cs`**：ApplyTheme 合并 Icons.xaml（主题清空重挂后补回，Controls 仍最后）；OnLaunchRequested 加 `tools` 分支（调视图 EnterToolListMode）；Esc 分层在动作面板后加 TryExitToolListMode。
- **`Core/SelfTest.cs`**：+2（tools 入口与 FilterToolList 口径；图标资源键全覆盖校验），累计 96。
- **自验**（`--debug --query=tools`，临时截图已删）：tools 结果首条「工具列表」带「工」首字头像；点击进入全量 11 行矢量图标（深浅两主题均清晰）；左下「工具列表」徽标 + placeholder 换口径；`z` 过滤收窄；Esc 退层（窗口 1259→242 回空态）；无图标应用（cmd 快捷方式）首字「X」回退正常。
- ⚠️ selftest 瞬态失败一次（连跑三次均 ALL PASS）：恰逢调试实例刚被停止，疑剪贴板/热键用例与残留进程竞态，非本次改动引入；后续如复现再查。

## 设计讨论：tools 命令 + 内置工具图标（2026-11-06）

**v1 口径盘点**（`src/renderer/renderer.js`）：

| 维度 | v1 实现 |
|---|---|
| 入口 | 搜索 `tools` 命中一条固定条目「工具列表」（描述「浏览全部工具，按名称、唤醒词或命令查找」，徽标「入口」）；v1 无其它别名 |
| 模式 | 回车进入 **tool-list 模式**：搜索框清空、placeholder 改「过滤工具、唤醒词或命令...」、右上角显示模式徽标「工具列表」、列表 = 全部工具（按声明序） |
| 过滤 | 对 id / name / description / keywords / 各命令 id·title·description·aliases 做 `includes` 子串匹配 |
| 退出 | Esc 退出模式，回到普通搜索（搜索框清空） |
| 图标 | **非应用条目一律是「首字头像」**：32×32 圆角 6 方框 + 标题首字（工具取 `title[0]`，如「正」「剪」），边框 `--line`、底 `--surface-raised`、字 14/600；只有应用（`entry.type==='app' && app.icon`）才用图片 |

**v2 现状**：`BuiltinCommands` 共 11 条（10 个视图工具 + 1 个动作 `format-clipboard`），转成 `AppEntry` 后 `IconPath` 为空；列表行模板（`SummonView.xaml` 32px 图标列）只绑 `Image`，命令条目 Icon 为 null → **图标列留空**（不是错位，是空白）。v2 无 tool-list 模式。

### 需求 1：`tools` 命令 → 工具列表模式

| 维度 | 定案（待拍板） |
|---|---|
| 入口条目 | `BuiltinCommands` 新增 `tools`（Kind="tool-list"，标题「工具列表」，副标题「浏览全部工具，按名称或唤醒词查找」，别名 `tools` / `工具`） |
| 进入 | 回车 → SummonView 进 `_toolListMode`：搜索框清空、placeholder 改「过滤工具…」、提示条左侧显示「工具列表 · Esc 退出」、列表 = 全部内置条目（声明序，含动作条目）、选中首条并滚动归零 |
| 过滤 | 空查询列全部；有查询时复用 `BuiltinCommands` 的匹配口径（别名/标题/标题拼音首字母），另加副标题子串匹配（v1 也搜 description） |
| 键位 | 完全沿用全局约定：↓/↑、Ctrl+J/K、Ctrl+N/P、Enter 打开工具、Ctrl+Enter 动作面板、Esc 分层退出（**先退工具列表模式 → 再退空态**） |
| 打开工具后 | **不记忆返回工具列表**：Esc 仍回空态（与现有全部入口一致，避免多一层隐式状态）；要再进就再输一次 `tools` |
| 空态 | 「无匹配工具」 |
| 角标 | 复用现有数字角标（1-0 = 当前可视区第 N 条），不额外加「工具」类型徽标 |

### 需求 2：内置工具图标

三选一：

| 方案 | 说明 | 代价 |
|---|---|---|
| A 首字头像（v1 口径） | 32×32 圆角方框 + 标题首字（「正」「剪」「命」…），主题令牌配色 | 最小；但「剪贴板记录/格式化」这类靠字面区分，辨识度一般 |
| B 矢量线框图标 | 每工具一枚 24×24 Path 线框图标（stroke `Text.Secondary`、圆头线帽，与翻译互换按钮同风格），存 `Themes/Icons.xaml` 的 `Geometry` 资源 | 画 11 枚图标；视觉统一精致，深浅主题自动适配 |
| **C 混合（推荐）** | 应用图片 → 工具矢量图标 → 都没有回退首字头像（三层回退，v1 的 fallback 语义 + 矢量图标） | 与 B 同工作量，另需模板加一层回退 |

推荐 C。实现要点：
- `Themes/Icons.xaml`（新）：11 枚 `Geometry`（`Icon.<Id>`），由 `App.ApplyTheme` 之后合并（与主题字典同生命周期）；图标为几何形状，颜色由 `Path.Stroke` 走 `DynamicResource`，深浅主题无需两套。
- `BuiltinCommand` 增 `IconKey`；`AppEntry` 增 `IconKey`（应用留空）；`ItemVM` 暴露 `IconGeometry`（按 key 从资源取）。
- `SummonView.xaml` 图标列改三层叠放：`Image`（应用，NotNullToVisible）/ `Path`（几何，NotNullToVisible）/ `Border+TextBlock` 首字回退（两者皆空时可见，含「工具列表」条目——它本身没有工具图标，用「T」或「工」）。

### 改动面与验证

- 文件：`Core/BuiltinCommands.cs`（+tools 条目、IconKey）、`Core/AppEntry.cs`、`Views/SummonView.xaml(.cs)`（tool-list 模式 + 图标三态）、`Themes/Icons.xaml`（新）、`App.xaml.cs`（图标字典合并）、`Core/SelfTest.cs`（+2：tools 条目与过滤口径、图标资源键覆盖全部工具）、`PyTools.csproj`。
- selftest 加项：① 每个内置条目都有可用图标键（键必须在 Icons.xaml 中存在，防漏画）；② tool-list 过滤口径（空查询全量、别名/标题/首字母/副标题命中）。
- 用户验收：输 `tools` 回车看列表与过滤；Esc 分层；各工具行图标在浅/深色下的观感。

### 待拍板（4 点）

| # | 问题 | 推荐 |
|---|---|---|
| 1 | 入口别名 `tools` / `工具`，还是也挂 `tool` | `tools` + `工具`（v1 只有 tools，`tool` 是 `tools` 的前缀天然命中） |
| 2 | 图标方案 A / B / C | **C 混合**（矢量图标 + 首字回退） |
| 3 | 从工具列表打开工具后 Esc 是否回工具列表 | **不回**，直接回空态 |
| 4 | 工具列表是否显示类型徽标（v1 有「工具/命令/应用」） | **不加**，v2 行内已是标题+副标题，角标只留数字 |

## 设计讨论：明暗主题（2026-11-05）

**现状盘点**：

| 维度 | 现状 |
|---|---|
| 令牌体系 | `Theme.xaml` = 基础令牌层（颜色 24 + 交互态 7 + diff 11 + 字号/圆角/动效/布局），全部视图经 `DynamicResource` 引用；`Theme.Lite.xaml` 只覆盖窗口底/阴影/动效三类；`Controls.xaml` 隐式样式后挂 |
| 换肤机制 | `App.ApplyTheme(liteMode)` 启动时清空并重挂三本字典；`DynamicResource` 天然支持运行时重挂刷新 |
| 既有伏笔 | diff 配色注释已写明「暗色主题只需换本组」；视图层零局部 Background/Foreground（2026-11-03 定型），硬编码色仅 3 处且都不随主题（OCR 棋盘格白底×2、DiffWindow 字宽度量用黑刷） |
| v1 | 无暗色语义，无从参照，按通用暗色对比度做 |
| 配置 | `v2.liteMode` 布尔（简洁模式，重启生效，窗口类随它决定）；无主题配置项 |

**设计方案**：

| 维度 | 定案（待拍板） |
|---|---|
| 结构 | **不拆 Theme.xaml**：新增 `Themes/Theme.Dark.xaml` 只覆盖全部**颜色类**键（Bg/Border/Text/Accent 组/交互态/diff 组/Scrollbar，约 42 键）；字号/圆角/动效/布局继承 Theme.xaml。挂载顺序 Theme → Dark → Lite → Controls（Lite 只碰窗口底/阴影/动效，与 Dark 正交可叠加） |
| 设置项 | 设置窗口外观区新增「主题」胶囊单选三态：**浅色 / 深色 / 跟随系统**（默认浅色 = 现状不变）；config 新键 `v2.theme`（`light`/`dark`/`auto`），与 `v2.liteMode` 独立 |
| 生效时机 | **热切换**：设置里选中即 `ApplyTheme` 重挂字典，呼出壳 + 已开的 diff/OCR/设置窗口全部即时变色（DynamicResource 刷新）；LiteMode 仍重启生效不动（窗口类不随主题变） |
| 跟随系统 | 启动时读一次注册表 `AppsUseLightTheme`（HKCU Personalize）决定 light/dark；**不监听实时变化**（系统切主题后重启 PyTools 或手动改设置即可，避免常驻监听的复杂度） |
| Accent 暗色 | 深色下 Accent 提亮一档（`#5B8AF5`，Hover/Pressed/Soft/OnAccentDisabled 相应调整），保证蓝底白字对比度 |
| diff 暗色 | 深色增/删底改暗色系（绿 `#1F3A26` 底 / 红 `#43262B` 底一类的低饱和深底 + 行内片段相应加深），行号列/命中黄底同步换暗值 |
| 棋盘格/度量 | OCR 棋盘格维持白灰（截图多为白底，暗底反而干扰判断）；DiffWindow 量宽黑刷只是度量不入渲染，不动 |
| selftest | +1 项：`Theme.Dark.xaml` 与 `Theme.xaml` 的**颜色键集合必须一致**（ResourceDictionary 直接加载比对，防漏键白屏） |

**改动面**：`Themes/Theme.Dark.xaml`（新）、`App.xaml.cs`（ApplyTheme 加 theme 参数 + auto 解析）、`Core/AppConfig.cs`（`v2.theme`）、`Windows/SettingsWindow.xaml(.cs)`（外观区单选 + 热切换调用）、`Core/SelfTest.cs`（+1）、`PyTools.csproj`。

**待拍板（4 点）**：

| # | 问题 | 推荐 |
|---|---|---|
| 1 | 结构：Dark 只覆盖颜色键 vs 拆中立 Core 层 | **只覆盖颜色键**（改动最小，Lite 现有约束不破坏） |
| 2 | 设置项：三态（含跟随系统） vs 二态 | **三态**（Win10 用户系统主题常切，auto 免手动） |
| 3 | 生效时机：热切换 vs 重启生效 | **热切换**（DynamicResource 重挂即刷； LiteMode 维持重启） |
| 4 | 跟随系统读取时机：启动读一次 vs SystemEvents 实时监听 | **启动读一次**（简单可预期，后续可加监听） |

用户确认（2026-11-05）：「需要在配置可选深浅随系统选项」→ 三态方案成立，按推荐四点全部落定，随即编码。

### 明暗主题已实现清单（2026-11-05，待真机验收）

- **`Themes/Theme.Dark.xaml`（新）**：只覆盖 34 个颜色键（基础 15 + 交互态 7 + diff 12）+ 阴影加重一档（Opacity 0.32→0.55）；字号/圆角/布局/动效不覆盖。diff 用低饱和深色系；Accent 提亮为 `#5B8AF5` 系保证对比度。
- **`App.xaml.cs`**：`ApplyTheme(bool)` → 无参 `ApplyTheme()`（public，设置窗口热切换入口）；挂载顺序 **Theme → Dark → Lite → Controls**（Dark 与 Lite 正交可叠加）；`ResolveDark()`：`v2.theme=dark` 或 `system` 且注册表 `AppsUseLightTheme=0`（读不到按浅色）。
- **`Core/AppConfig.cs`**：新键 `v2.theme`（light/dark/system，默认 light；未知值宽容回 light）；`SaveTheme()` 即时落盘。
- **`Windows/SettingsWindow`**：常规页新增「主题」行，胶囊单选 浅色/深色/跟随系统；Checked/Unchecked 都进 `OnThemeChanged`、按当前勾选态落值（幂等），`_loaded` 守卫防初始化误触发；点击即 `SaveTheme` + `App.ApplyTheme()` 热切换。
- **`Core/SelfTest.cs`**：+1 项「Dark 覆盖层与浅色颜色键集合一致（防漏键白屏）」——双字典 SolidColorBrush 键集合双向比对，**累计 94 项**。
- **`PyTools.csproj`**：+`Themes\Theme.Dark.xaml`。
- **自验**（`--debug --settings`，临时截图已删）：深色下设置窗全量换肤正常（导航/输入/复选/胶囊单选/按钮）、主题行三态与选中态正确；深色呼出主界面搜索框正常（外圈纯黑为 PrintWindow 不合成透明的伪影）；**热切换实测通过**：深色态点「浅色」→ 全窗即时变浅、config 落盘 `v2.theme=light`、进程稳定无崩溃。
- ⚠️ 自验探针坑（记入 memory）：① 设置窗口标题是「PyTools 设置」不是「PyTools」；② 探针 python 必须先 `SetProcessDpiAwareness(2)`，否则 GetWindowRect/SetCursorPos 走 DPI 虚拟化坐标（本机 200% 缩放差一半），点击全偏；③ `(cmd &)` 后台实例可能被 shell 生命周期连带终止，长观察窗口应在单条命令内原子完成。

## 设计讨论：code-naming 接入（M4 AI 批第四项，2026-11-05）→ 已按此编码完成并验收

**v1 语义盘点**（`tools/code-naming` + `src/main/ai-adapter.js` 的 `naming()`）：**v1 语义盘点**（`tools/code-naming` + `src/main/ai-adapter.js` 的 `naming()`）：

| 维度 | v1 行为 |
|---|---|
| 输入 | 单个输入框，可填名词或业务描述；trim 后为空拒绝，长度上限 **200 字**；Enter = 生成 |
| 请求 | `{input, exclude}` 作为 user content；`exclude` = 已出现候选，上限 40 条，换一批时必避开 |
| 提示词 | 先提炼 `core`（核心概念）；`note` 一句话说明取词依据；`items[{group, style, value, note?}]`；只输出 JSON |
| 分组 | 固定八组 `变量 / 布尔变量 / 类 / 方法 / 常量 / 表名 / 字段 / 文件名`；**必给四组**：变量(camelCase)、类(PascalCase)、表名(snake_case)、字段(snake_case)；布尔/方法/常量/文件名**仅在输入适用时给** |
| 风格 | 白名单 `camelCase / PascalCase / snake_case / UPPER_SNAKE_CASE / kebab-case` |
| 取值 | 正则 `^[A-Za-z_][A-Za-z0-9_-]*$`，不加引号不加说明；**每组只给 1 条** |
| 解析 | 组/风格白名单过滤、非法 value 过滤、同值去重；空列表 → `AI 返回内容缺少可用命名`；服务端 error 优先透出 |
| 展示 | 摘要行（`核心概念 xxx` + note）→ 按 `GROUP_ORDER` 固定排序的扁平列表（组标签 / 等宽 value / 风格 tag / note）→ 状态栏 |
| 交互 | 行点击即复制（含按钮冒泡）；「换一批」结果**整体替换**；AI 不可用显示 tag 与原因，点击时才补测一次配置 |

**v2 设计定案**：

| 维度 | 定案 |
|---|---|
| 形态 | **呼出壳视图**，固定尺寸 **780×560**（与 cron 同量级；命名是「输入一句 → 挑一个 → 复制走」的短流程，比 translate 的双栏窄，不需要 900 宽） |
| 布局 | 顶部输入栏（`描述 [单行输入 ______] [生成] [换一批]`）→ 摘要行（`核心概念 xxx` + 依据，无内容时整行隐藏）→ 结果列表（按批次分节，批内八组固定顺序，滚动）→ 底部状态栏（左生命周期文案 / 右「操作 Ctrl+Enter」键帽） |
| 结果行 | `组标签(宽 56) + value(等宽字体 13, 主色) + 风格 tag(次要色小字) + note(次要色, 省略号) `；行 hover = `Bg.ItemHover`，选中 = `Bg.ItemSelected`；**单击行 = 复制**（v1 口径）+ ToolTip「复制 xxx」 |
| 键位 | 视图级 `PreviewKeyDown` 照 SummonView：↓/↑ 移动一条、Ctrl+J/K 加速、Ctrl+N/P 按页步进、**Enter = 复制选中行**、Ctrl+Enter = 动作面板、Esc 分层退出。**输入框内 Enter = 生成**（单行框，视图级 Enter 判断 `InputBox.IsKeyboardFocusWithin` 时不拦，与 translate 源栏同理） |
| 焦点 | 打开即焦点在输入框（全选已有文本）；生成完成后焦点留输入框，↓ 直接进列表（视图级拦截，无需手动搬焦点）；键入可打印字符继续落在输入框（列表不抢焦点） |
| 打开行为 | **不读剪贴板、不预填**（用户拍板：命名需要人自己想描述，自动带入的文本多半是别的内容）；输入框空态，焦点就位，用户直接键入 |
| 换一批 | **追加为新批次**（用户拍板）：列表按「第 1 批 / 第 2 批 / 第 3 批」小标题分节，批内组序不变，可上下对比挑最贴切的一条；**最多保留 3 批**（≤24 行，超出丢弃最早一批） |
| 批次实现 | 单 `ListBox` + 扁平行集合：行分 `Header`/`Item` 两类，`Header` 不参与选中（`Focusable=false`、模板不响应 hover），↓/↑ 步进时跳过 header；换一批后自动滚到新批次首行并选中它 |
| 避重 | `exclude` **跨批次累积**全部已出现 value（上限 40，v1 口径）；解析层再与本次已有值比对去重，保证同一 value 不跨批重复 |
| 复制 | 写剪贴板 + 状态栏「已复制 orderTime」；复制后**不改列表与选中项**（便于连续复制多条）；实现复用现有剪贴板写回重试路径 |
| 空/错态 | 无结果 → 列表区居中提示「没有可用命名，换个说法再试」；超长 → 状态栏红字「输入过长，请控制在 200 字以内」；AI 未配置 → 状态栏「AI 未配置（未启用/模型缺失/密钥缺失）」，生成按钮点击时再校验一次；请求失败 → 红字 + `（可在设置中更换模型）`（与 translate 同口径） |
| 动作面板 | 生成 G / 换一批 R / 复制选中项 C / 清空 X（**无「从剪贴板填入」**，与不预填一致） |
| 技术要求 | `AiClient.NamingAsync`（60s 超时、temperature=0、无 tools、`ParseNamingResponse` 五级解析：直返 JSON → 服务端 error → content JSON → 围栏 → 纯文本兜底）；`BuildNamingPayload` 里 `exclude` 用 JSON 序列化；调试开关 `--naming` / `--naming-text=` / `--naming-demo`（预置两批结果，供无 AI 环境的截图自验） |
| 改动面 | `Core/AiClient.cs`（扩充）、`Views/NamingView.xaml(.cs)`（新建）、`Core/BuiltinCommands.cs`（`naming`，别名 `name`/`命名`）、`App.xaml.cs`（`ShowNaming` 壳视图 780×560 + 调试链 + 命令分发）、`Core/SelfTest.cs`（+3 项）、`PyTools.csproj` |

**已拍板（2026-11-05）**：

| # | 问题 | 结论 |
|---|---|---|
| 1 | 尺寸 | **780×560**（列表单列，900 宽会留大片空白） |
| 2 | 打开时是否带入剪贴板文本 | **不带入、不预填**，输入框空态 |
| 3 | 换一批语义 | **追加为新批次**（最多 3 批，批次小标题分节，exclude 跨批累积） |
| 4 | 每组条数 | **1 条**（v1 口径，token 与解析最稳，扩展靠换一批） |
| 5 | 是否加「语境」下拉 | **不加**（v1 没有，提示词已覆盖各语言通用命名） |

### code-naming 已实现清单（2026-11-05，已验收）

- **`Core/AiClient.cs`**：`NamingPrompt`（v1 原文照搬）、`NamingMaxInput=200`、`NamingMaxExclude=40`、`NamingGroupOrder`（八组固定顺序兼白名单）、`AiNamingItem/AiNamingResult`、`Naming/NamingAsync`（60s、temperature=0、无 tools）、`BuildNamingPayload`（`{input, exclude}` 作 user content）、`ParseNamingResponse(raw, existing)`（直返 items → 服务端 error → content JSON → 围栏 → 纯文本兜底；组/风格白名单、取值正则 `^[A-Za-z_][A-Za-z0-9_-]*$`、批内+跨批去重、按组序重排）、`ValidateNamingInput` / `NormalizeNamingExclude`（离线可测）。
- **`Views/NamingView.xaml(.cs)`**（新）：输入栏（描述 + 生成 AccentButton + 换一批）→ 摘要行（核心概念/依据，空则隐藏）→ 结果列表（批次小标题「第 N 批 · M 条」不可选中，批内组序固定）→ 状态栏。换一批**追加新批次**（最多 3 批，超出丢最早），exclude 跨批累积；描述修改后 RerollButton 自动禁用、换一批降级为重新生成。行 hover/选中用 `Bg.ItemHover/ItemSelected`，单击行 = 复制，Enter 复制选中行。键位照全局约定（↓/↑、Ctrl+J/K、Ctrl+N/P、Ctrl+Enter 面板、Esc 走 App 路由），输入框内 Enter = 生成。动作面板：生成 G / 换一批 R / 复制选中 C / 清空 X。watermark 聚焦让位（与 translate 同口径）。
- **🔴 实现踩坑**：`NamingRow` 首版用公开字段，WPF 绑定不认字段 → 列表整列空白且无异常（`--naming-demo` 截图自验抓出），改属性后修复。**列表/模板类行对象一律用属性**。
- **`App.xaml.cs`**：`_namingView`（`PageStep=_config.PageStep`）、Esc 路由、`ShowNaming`（780×560）、命令分发 `naming`；调试链 `--naming` + `--naming-text=`（等价 `--query=`）+ `--naming-demo`（铺两批示例结果，不请求 AI，截图自验用）。
- **`BuiltinCommands`**：`naming`（别名 `var`/`命名`；`var` 是 v1 keywords 首位、最高频呼出词，验收反馈补挂）。
- **`SelfTest.cs`**：+3 项（请求体序列化 + 输入校验 + exclude 规范化；白名单/非法取值/批内去重/组序重排；围栏/服务端 error/跨批去重/空候选/非 JSON），累计 **93 项全过**；exe 476KB。
- **自验截图**（`--naming-demo`，已删临时文件）：两批 8 条渲染、批次小标题、选中态（新批首行）、hover 背景正常；空态按钮禁用与居中提示正常。



## 设计讨论：ai-translate 接入（M4 AI 批第三个消费者，2026-11-05）→ 已按此编码完成

**v1 语义盘点**（`tools/ai-translate` + `src/main/ai-adapter.js`）：

| 维度 | v1 行为 |
|---|---|
| 语言集 | 源 `自动/中文/英文/日文`；目标 `中文/英文/日文`（无自动） |
| 自动检测 | 源=自动时**先单独发一次 detectLanguage 请求**，再发 translate（两次往返） |
| 互换 | 源=自动时先检测再对调语言选择；无文本时按钮禁用 |
| 结果 | 只读 + 浮动复制按钮；Ctrl+Enter 翻译；`{translation,error}` JSON |
| 提示词 | 禁用 Markdown/解释，只输出 JSON |

**v2 设计草案**：

| 维度 | 定案 |
|---|---|
| 形态 | **呼出壳视图**，固定尺寸 900×620（与 regex 860×620 同构）。翻译是「呼出→翻→复制→走」的短流程，不像 OCR 需要拖拽与长时间整理，不引入第 4 个常驻窗口 |
| 布局 | 顶部语言条（`源语言 [自动▾] ⇄ 目标语言 [中文▾]`）→ 主体左右双栏（左原文可编辑 / 右译文可编辑）→ 底部状态栏（`翻译中…` / `已翻译 · 模型 · 用时 · 字数` / 红色失败原因，同 regex 口径，不另建信息卡） |
| 语言策略 | 源：自动/中文/英文/日文；目标：中文/英文/日文；**源=自动时不再预先发包**——prompt 里 sourceLang 传空由模型自判，JSON 回 `detectedLang` 显示在状态栏（省一次往返与额度） |
| 打开行为 | 剪贴板有文本 → 自动填入源栏（同 OCR 自动填图惯例），状态栏提示「已从剪贴板填入」 |
| 主操作 | 「翻译」按钮 + 动作面板「翻译」；源栏 **Enter = 换行**（多行输入），Ctrl+Enter = 动作面板 |
| 互换 | 语言对调；右栏有译文时**译文搬进源栏并清空译文**（回译场景）；源=自动时用上次识别结果作新源 |
| 动作面板 | 翻译 / 复制译文 / 互换语言 / 从剪贴板填入 / 清空 |
| 技术要求 | `AiClient.TranslateAsync`（60s 超时、temperature=0、`ParseTranslateResponse` 多级解析）；调试开关 `--translate` / `--translate-text=`；selftest 覆盖解析 6 例 + 载荷 1 例 |
| 改动面 | `Core/AiClient.cs`（扩充）、`Views/TranslateView.xaml(.cs)`（新建）、`Core/BuiltinCommands.cs`、`App.xaml.cs`、`Core/SelfTest.cs`、`PyTools.csproj` |

**待拍板（5 点）**：

| # | 问题 | 推荐 |
|---|---|---|
| 1 | 形态：壳视图 vs 独立窗口 | **壳视图**（翻译是短流程；Esc 分层退出、失焦即收可接受） |
| 2 | 打开自动填剪贴板文本 | **填**（复制外文即呼出翻译是主路径），需要空白时动作面板「清空」 |
| 3 | 源语言自动时是否单次请求 | **单次**（v1 两次往返，多花一次额度；模型自判后回 detectedLang） |
| 4 | 互换行为 | **语言对调 + 译文搬回源栏**（回译场景一步到位） |
| 5 | 语言集 | **继续中英日三语**（内网场景够用，后续按需扩展） |

### ai-translate 已实现清单（2026-11-05，已验收）

- **`Core/AiClient.cs`**：`TranslatePrompt`（v1 口径 + 保留换行段落 / 代码·URL·专有名词原样 / 源语言空时自判回 detectedLang）、`BuildTranslatePayload`（纯文本 content、无 tools、temperature=0）、`ParseTranslateResponse`（直返 translation → 服务端 error → content JSON → 围栏 → 纯文本兜底）、`Translate/TranslateAsync`（60s 超时、WebException 双路径 `RequestFailed` 指路设置）。
- **`Views/TranslateView.xaml(.cs)`**（新）：语言条（源 自动/中/英/日 + 互换 + 目标 中/英/日 + AccentButton 翻译）→ 左右双栏（原文/译文均可编辑，watermark 无内容且未聚焦才显示）→ 状态栏（`翻译中…` / `英文 → 中文 · 模型 · 1.6s · 22 字` / 红字失败 + RequestFailed 指路设置）。互换：语言对调 + 译文搬回原文栏（回译）；源=自动时用 `_lastDetected` 作新源。动作面板：翻译 T / 复制译文 C / 互换 S / 从剪贴板填入 V / 清空 X。打开时剪贴板有文本且与原文不同 → 自动填入。
- **互换按钮图形**：语言条中间用矢量 Path 画的双箭头（16×14，圆头线帽 1.5px），**不用 `⇄` 字符**——该字形在常规字体里撑满字面，30×26 按钮内上下被裁且观感差（验收反馈后改）；描边 `Text.Secondary`，hover 时经 DataTrigger 加深为 `Text.Primary`。
- **`App.xaml.cs`**：`ShowTranslate`（壳视图固定 900×620）、Esc 路由、`--translate` + `--query=` 预填调试链、命令分发 translate。
- **`BuiltinCommands`**：`translate`（别名 `fanyi`/`翻译`）。
- **`SelfTest.cs`**：+2 项（请求体转义断言、响应解析七路），累计 **90 项全过**；exe 452KB。
- **探针实测**（本机 deepseek-flash 真实请求 1 次 + 互换，无 AI 消耗大头）：预填态/空态布局正常；点「翻译」→ 真实译文 + 状态栏 `英文 → 中文 · deepseek-flash · 1.6s · 22 字`；点 ⇄ → 源=中文/目标=英文 + 译文搬回原文栏 + 状态栏「已互换语言，译文已移到原文栏」。
- ⚠️ 探针经验：合成 Ctrl 组合键（keybd_event）在此应用内不被识别（剪贴板/翻译两次探针一致），键盘驱动 UI 走鼠标点击坐标（注意截图缩放比：1800×1240 物理图上按钮 y 比例 0.11 而非按 700 高预览算的 0.056）。

## 剪贴板文件条目打磨（2026-11-05，已验收提交 `e776f9f`）

### 追加两项验收反馈修复（同日 15:45 段）

1. **写回剪贴板失败 `CLIPBRD_E_CANT_OPEN`（用户截图 toast）**：截图工具/剪贴板管理器会较长时间握住剪贴板句柄，`Clipboard.SetDataObject(data, true)` 内置重试仅约 200ms 不够。修复：`ClipboardHistory.WriteBack` → `SetClipboardWithRetry`（外层 5 次 × 120ms，仅对 `0x800401D0` 重试）；全部失败后错误文案改为「剪贴板被其他程序占用（已重试仍失败），请稍后重试」。
2. **列表滚动位置跨打开保留**：`ResetForClipboard` 重建 ItemsSource 后虚拟化面板保留上次垂直偏移。修复：布局完成后（Loaded 优先级）`_scroll.ScrollToHome()`，每次打开都从最近记录开始。
3. ⚠️ **探针教训（定型）**：单实例架构下 `--debug --clipboard` 探针若用户实例正在运行，锁获取失败 → 信号路由到用户实例，`FindWindowW` 找到的也是用户实例窗口——按键/滚轮会打进用户实盘工具。判据：截图内容与本地 json 对不上（`{ 1113 字` 不存在于 bin/Release 数据）。滚动复位类 UI 行为不再用第二进程探针验证，交真机。

**背景**：用户反馈「看不到文件记录」。排查结论：文件条目捕获/去重/落盘/写回 v2 早已完整支持，本机 json 41 条里 7 条 files（diff-a.txt 等，位于列表 14+ 位，被更近的图片/文本挤到可视区外）——非 bug，是可发现性差。顺带打磨四点 + 列表间距：

| # | 打磨项 | 实现 |
|---|---|---|
| 1 | 文件图标 | `Core/FileIcons.cs`（新）：SHGetFileInfo + SHGFI_USEFILEATTRIBUTES 按扩展名取 shell 小图标（文件删除也出图标），内存按扩展名缓存，取不到退通用文件图标；替换原「件」字符块 |
| 2 | 副标题显示路径 | `ClipRecord.FilesSubtitle`：单文件=所在目录；多文件同目录=「共 N 个文件 · 目录」；跨目录=「共 N 个文件」 |
| 3 | 失效检测 | `ClipRecord.IsMissing/AnyFileExists`（按记录缓存 stat）：文件全不存在 → 标题灰显（Text.Placeholder）+ 副标题「文件已不存在」；`WriteBack` 过滤失效路径（`ExistingPaths` internal 供 selftest），全失效返回「文件已不存在，无法写回剪贴板」 |
| 4 | selftest | +1 项覆盖：DataObject FileDrop 捕获 → 小写路径签名 → 判重 skip=duplicate → json 往返 → 删文件后 IsMissing/副标题/ExistingPaths，累计 **88 项全过** |
| 5 | 列表间距（用户点名） | 图标列与文字列间距 0 → 8px；标题/副标题行距 1 → 3px |

**验证**：`--debug --clipboard --query=diff` 截图——文件行显示 txt/ps1 各自 shell 图标 + 目录路径，间距舒适；普通列表间距复检通过。待真机验收：复制文件后是否置顶出现、图标/路径/失效显示。

## ai-ocr 已实现清单（2026-11-05 编码完成，待真机验收）

- **`Core/AiClient.cs` 扩充**：`OcrPrompt`（只回 JSON {text,error}，纯文本也接）／`AiOcrResult`／`Ocr·OcrAsync`（60s 超时，temperature=0，无 tools）／`BuildOcrPayload`（图文 content 数组）／`ParseOcrResponse`（直返 text → 服务端 error → content JSON → ```json 围栏 → **纯文本兜底**，拍板 #1）。
- **`Core/OcrImage.cs`（新）**：`FromClipboard`（**优先 "PNG" 格式流**保原始字节，退化 `GetImage()`+PngEncoder）/`FromFile`（png/jpg/jpeg/bmp 白名单 + ≤10MB + BitmapDecoder 解码校验）/`OcrImageRecord.DataUrl`（**保留原格式 MIME**，拍板 #2）+ `ToBitmap()`（OnLoad+Freeze 预览）。
- **`Views/OcrWindow.xaml(.cs)`（新）**：独立常驻窗口（拍板 #11，拖拽/编辑需要窗口不失焦）。布局：左 `ImageCard`（Auto 列 + 改 Width 即布局动画 220ms CubicEase）+ 下方 `InfoCard`（AI 生命周期唯一呈现处）｜右 `ResultBox` 可编辑 + 占位提示。状态机：空态 → 展开双栏（42% 宽图片卡撑满高）→ 识别中（缩略 120×90 + 「识别中…」动态省略号）→ 已识别（信息卡 `模型 · 用时 · 字数`，字数实时跟随编辑，拍板 #4；失败红字 + 可重试，保持缩略态）。缩略态 hover=ToolTip 原图气泡（InitialShowDelay 150）+「单击展开」角标，单击=展开回双栏（拍板 #8）。图片四入口：拖拽（整窗 Drop）/ Ctrl+V（剪贴板有图才拦截，文本放行给结果框）/ 按钮 / 点击空图卡选文件；换图一律清结果 + 恢复双栏。开窗即探测剪贴板自动填入（无图静默）。键位：Ctrl+Enter 动作面板（读剪贴板 C/选文件 F/识别 R/复制结果 Y/展开图片 E/清空 X，AI 未配置不列识别项）/ Ctrl+R 识别 / Esc 分层（面板→关窗）。
- **`App.xaml.cs`**：`_ocrWindow` 单例 + `ShowOcr`（Show+Activate → HideSummon(restoreFocus:false)，再进只 Activate 保留状态）+ `CascadeSteps()`（diff/设置窗口已开时右移下移各 32px，最多 2 档）+ `--ocr` / `--ocr-image=` / `--ocr-demo` 调试开关 + 托盘退出一并关闭。`BuiltinCommands` 加 `ocr`（别名 ocr/识别）。
- **`Core/SelfTest.cs`**：+4 项（OCR 请求体/响应解析四路/OcrImage 白名单·尺寸·解码/**DIB 死 alpha 展平**），累计 **87 项全过**；exe 433KB。
- **截图自验**（探针 + PrintWindow）：空态（按钮禁用态正确、等待提示）/ 展开态（42% 图片卡 + 信息卡引导）/ 缩略态（120×90 + 单击展开角标 + 信息卡 识别完成·模型·用时·字数）三态均正常。
- **验收修复 ①（焦点与可见性）**：初始焦点移到 Root（光标不再压占位提示）；图片卡加 16px 棋盘格底衬（挂 CardHost——**WPF Image 无 Background 属性**，不能挂 Image 本身）；开窗自动探测剪贴板无图时静默（不报「剪贴板没有图片」警告）。
- **验收修复 ②（🔴 载入成功但卡片空白，2026-11-05 真因）**：用户截图 1541×202 载入后整卡空白。排除白底误判后，用调试开关 `--ocr-clip-dump=<路径>`（App 新增，走 FromClipboard 同链路落盘字节后退出）拿到剪贴板真实格式：**无 "PNG" 格式，只有 DIB 系（DeviceIndependentBitmap/Bitmap/Format17）**，走 GetImage() 兜底；导出字节 PIL 分析 **alpha 通道 100% = 0**（GDI 系截图工具放 32bpp DIB 不写 alpha 字节），重编码 PNG 解码一切正常（尺寸/KB 都对）但渲染全透明。修复：`FromBytes` 统一入口检测 Bgra32 且采样 256 行 alpha 全 0 → `FormatConvertedBitmap` 展平为无 alpha 格式（Bgr32/Bgr24，丢 alpha 保 RGB）重编码；Pbgra32 预乘后无从恢复不动；正常带 alpha 的 PNG 不重编码。端到端复验：同一份死 alpha 字节修复后渲染正常。
- **验收修复 ③（光标压占位提示）**：占位提示原来只判有无文字，聚焦空框时光标压在提示首字上。改为 watermark 惯例「无文字 **且** 未聚焦」才显示（`UpdateResultPlaceholder`，GotKeyboardFocus/LostKeyboardFocus 触发）。探针模拟点击验证两态。
- **待真机验收清单**：① 启动器输 `ocr`/`识别` → 呼出壳收起、OCR 窗口居中弹出；diff 开着时再进 → 两窗并存 + OCR 右下偏移 32px；OCR 已开再进 → 激活不新建；② 截图后开窗 → 自动载入 + 提示「已从剪贴板自动载入」；③ 配置 AI 后识别真实图片 → 图片让位动画 + 信息卡进度 + 结果可编辑、字数实时变；④ 识别失败（如断网）→ 红字原因 + 保持缩略态可重试；⑤ hover 缩略卡看原图气泡、单击恢复双栏；⑥ 拖拽/Ctrl+V/选文件换图 → 结果清空 + 双栏；⑦ Esc 关窗、托盘退出全关。

## 设计讨论：ai-ocr 接入（M4 AI 批第二个消费者，2026-11-05）

**定位**：AI 批第二个消费者，验证 `AiClient` 共享层的可复用性——OCR 需要的是「图文混合 content 数组」，而 regex 是「纯文本 + tools」，如果 AiClient 抽象站得住，翻译 / code-naming 就只是换 prompt。

**v1 语义盘点（`tools/ai-ocr/view.js` + `src/main/ai-adapter.js` + `toolkit-api.js`）**：

| 环节 | v1 做法 |
|---|---|
| 图片来源 | 剪贴板 `clipboard.readImage()` / 本地文件（png/jpg/jpeg/bmp，≤10MB）；两者都 `nativeImage` 解码后**统一转 PNG** dataUrl |
| 请求 | `POST /chat/completions`，system=OCR_PROMPT（只输出 JSON `{text,error}`），user content 数组 = `[{type:text}, {type:image_url, image_url:{url:dataUrl}}]`，**超时 60s**（比生成的 30s 长） |
| 解析 | 剥 ``` 围栏 + 提取首尾大括号 → `text` 字段；无 text 则报错 |
| UI | 左预览 / 右结果只读文本域 / 状态栏 / AI 徽标（🔴 假绿问题：只判配置不判可达，v2 已废） |

**v2 落点**：

1. **`Core/AiClient.cs` 扩充**（不新建类，保持单一共享层）：`BuildOcrPayload`（图文 content 数组，可离线测）、`ParseOcrResponse`（可离线测）、`Ocr/OcrAsync`（60s 超时，`RequestFailed` 标记同 regex）、`AiOcrResult { Ok, Text, Model, Error, RequestFailed }`。
2. **`Core/OcrImage.cs`（新建）**：`FromClipboard()` / `FromFile(path)` → `OcrImageRecord { Ok, DataUrl, Width, Height, Bytes, Error }`。
   - 剪贴板：**优先取 "PNG" 格式流**（`Clipboard.GetDataObject()` 里 `PNG` 的 MemoryStream，保原始字节），退化才 `Clipboard.GetImage()` 再 PngEncoder 编码——WPF 已知坑：`GetImage()` 走 DIB 转换可能丢 alpha / 丢原始压缩。
   - 文件：扩展名白名单 + ≤10MB + 解码校验（沿用 v1 三条错误文案）。
3. **`Views/OcrWindow.xaml(.cs)`（新建，2026-11-05 用户提问后拍板改形态）**：**独立常驻窗口**（与 diff 同一家族，不寄居呼出壳）。理由：① 拖拽文件必须先激活资源管理器，呼出壳会 `Deactivated` 收起，拖不进来；② 「编辑结果时点一下别处就整窗消失」与「常驻」语义互斥。布局对齐 v1 但改造为 v2 双栏（左图预览 / 右可编辑结果）。
4. **入口**：`BuiltinCommands` 加 `ocr`（标题「AI 识别文字」，别名 `ocr` / `识别`）；`App` 加 `ShowOcr()` + Esc 路由 + `--ocr` 调试开关。

**UI 布局**：

- 顶部按钮行：读取剪贴板图片 / 选择本地图片 / 识别 / 清空；
- 主体双栏：左 `Image`（Uniform 缩放 + 空态「等待图片」）｜右只读文本域（等宽、可换行、可滚）；
- 状态栏：左状态（含 ToolTip 全文，沿用气泡约定）｜右「操作 Ctrl+Enter」键帽。

**动作面板**：读剪贴板图片 `C` / 选择本地图片 `F` / 开始识别 `R` / 复制结果 `Y` / 清空 `X`。
（未配置 AI 时「开始识别」不列出；其余项恒列。）

**第一轮拍板（2026-11-05 用户确认）**：

| # | 问题 | 定稿 |
|---|---|---|
| 1 | 解析兜底 | **加纯文本兜底**（非 JSON 且非空 → 整段当识别结果）——v1 只认 JSON `{text}`，模型直接吐文本就白跑一次请求 |
| 2 | 图片字节 | **保留原格式**（jpg 发 `image/jpeg` 原字节），仅剪贴板转 PNG——4000×3000 照片转 PNG 可能 20MB+ |
| 3 | 粘贴 / 拖入 | 都做：`Ctrl+V` 读剪贴板图片、图片文件拖进预览区（DiffWindow 已有两个先例） |
| 4 | 大图 | 不缩放，>10MB 直接拒绝（隐式降质不如让用户知情） |
| 5 | 窗口尺寸 | 900×620（比 regex 860×620 略宽，图片预览要空间） |
| 6 | 未配置 AI | 视图照进（读图/预览可用）、状态栏报原因、识别项不列出（不做徽标，v1 假绿教训） |

### 布局与动效（第二轮，2026-11-05 用户提议）

**问题**：原始「左图右结果」双栏在识别时图片占着半个窗口却无信息量，结果文本被挤在右半。

**方案**：识别开始时图片**缩成缩略卡并让位**，结果区占满；hover 缩略卡用气泡看原图。

- **布局骨架**：主体 = `Grid` 两列，图片列 `Width="Auto"`，卡片自身 Width 做 `DoubleAnimation`（宽栏 → 120px）。**Auto 列按子元素宽度测量，所以改子元素 Width 就等于布局动画**，不需要自定义 `GridLengthAnimation`（本方案的关键技巧，省掉一类容易写错的自定义动画类）。
- 卡片高度：识别前 `Stretch` 撑满行高，识别后动画到 90 且 `VerticalAlignment=Top` → 自然变成左列顶部缩略卡。
- 时长 220ms `CubicEase(EaseOut)`；请求发出即播（用户能感知「图片让位」）。
- **hover 看原图**：卡片 `ToolTip` 装一个 Image（最大 900×700 等比缩放）+ `ToolTipService.InitialShowDelay=150`。**用 ToolTip 而不是自定义 Popup**：天然处理 hover 进出、无闪烁、无需管 StaysOpen。
- **恢复双栏**：单击缩略卡，或动作面板「展开图片」；换图（粘贴/拖入/选文件）自动恢复双栏。
- **识别失败保持缩略态**：用户可直接重试；状态栏报原因。

**剪贴板自动填入**：进入视图时检测剪贴板是否含图片 → 有则后台读入并提示「已从剪贴板载入图片」（同时清空旧结果）；无图则保留当前图片不动。典型链路 = 截图工具截完 → 呼出 → 直接按 R 识别。

**第三轮待拍板**：

| # | 问题 | 推荐 |
|---|---|---|
| 7 | 缩略卡尺寸 | **120×90**（结果区仍余约 740px，够一屏正文） |
| 8 | 单击缩略卡 | **恢复双栏对照**（hover=气泡看原图，单击=展开，两个动作不冲突） |
| 9 | 识别结果是否可编辑 | **可编辑**（v1 只读；OCR 结果常有换行/错字要小改，只读会逼用户复制到别处） |
| 10 | 缩略卡下方左列空间 | **AI 结果信息卡**（2026-11-05 用户提出）：红框区域随识别状态填充——未识别「按 R 或点识别开始」淡色提示 / 识别中「识别中…」+ 动画 / 成功「模型 · 用时 · 字数」/ 失败红色原因 + 可重试提示。生命周期信息**只放这张卡**，状态栏 AI 文案精简，两处不重复；不做预置「AI 可用」标记（v1 假绿教训） |
| 11 | 与 diff 同时开着怎么办 | **OCR 改为独立常驻窗口（同 diff 家族）**，三窗口（diff / ocr / 设置）并存互不干扰：App 各持引用、各自 `Closed` 清引用；从启动器再次进入 ocr → **Activate 已有窗口并保留状态**（不新建、不重置）；「清空」= 开新会话。打开顺序复用 `ShowDiff`：先 Show+Activate 再 `HideSummon(restoreFocus:false)`；托盘退出一并关闭 |

**改动面**：`Core/AiClient.cs`（扩充）、`Core/OcrImage.cs`（新建）、`Views/OcrWindow.xaml(.cs)`（新建）、`Core/BuiltinCommands.cs`、`App.xaml.cs`、`Core/SelfTest.cs`、`PyTools.csproj`。

**离线可测项**：payload 结构（content 数组 shape / image_url 字段 / 无 tools）、JSON 解析（正常 / 围栏 / 纯文本兜底 / error 字段）、扩展名与体积校验、dataUrl MIME 前缀。

## 已验收：regex AI 生成 POC（M4 AI 批试验田，2026-11-05，提交 `dfc3b9d`）

**定位**：v2 首个 AI 链路，真正产出是共享的 `Core/AiClient.cs`——后续 ai-ocr / 翻译 / code-naming 的 AI 批全部复用它；regex 生成只是第一个消费者。

**实现落点**（超出设计稿的补充）：
- `Core/AiClient.cs`（新建，~470 行）：配置合并（config.json ai.enabled || ai-local.json enabled）、Status 五因、GET /models 清单（`ParseModelList` 离线可测）、SelectModel 写回（未知键保留、只读报错）、`BuildChatPayload`（tools/tool_choice 可关）、`ParseResponseBody` 四层兜底（直接 pattern → tool_calls → content JSON → 围栏/行内）、tool_choice 降级重试、HttpWebRequest + TLS 1.2 显式开启、超时 30s/拉取 15s、WebException→「连接超时（Ns）/无法解析服务地址/无法连接服务」。
- 🔴 **新坑入库：JavaScriptSerializer 反序列化 JSON 数组给 `ArrayList`**（非 object[]/List<object>），`as object[]` 静默得 null——必须 `ArrayList` 兜底转换（`AsArray`）。
- `Views/RegexView.xaml(.cs)`：动作面板「AI 生成正则」（hint=A，未配置不列出、生成中不可重入）；空文本守卫「请先把目标或样例写到测试文本里」；成功填表达式+按返回设 4 标志+Update 兜底（Text 不变时 TextChanged 不触发）；状态栏「AI 已生成 · model · 置信度 N · reason」（新增 ok 态 Accent 色）；失败分请求类（追加「可在设置中更换模型」）与格式类。
- `Windows/SettingsWindow.xaml(.cs)`：AI 分组（模型 ComboBox + 刷新 + 常驻提示行）；Loaded 后台拉取（序号防竞态）；当前 model 不在清单时兜底保留首项；选中即写回（写失败回滚显示——意图≠实况）；未配置态禁用下拉+原因提示。
- `App.xaml.cs`：`AiClient.Initialize(AppDirectory)`；debug 分支补 `--settings`（原在非 debug 链，`--debug --settings` 只会 ShowSummon，自验踩到）。
- `Core/SelfTest.cs`：AI 用例 8 项（Status 六态合并语义、payload 构建、tool_calls/content JSON/围栏/行内四路解析、error 提取、/models 清单解析、SelectModel 写回+只读失败），累计 **82 项全过**。

**探针自验（已清理）**：
- 设置窗口两态：未配置（禁用+原因）；已配置真机 key 对官方 API 无效 → **失败路径符合设计**（兜底项 deepseek-flash + 「获取模型列表失败: …（可点刷新重试；不影响 AI 生成）」）。
- 本地 mock OpenAI 服务验证成功路径：设置「共 3 个模型，选择即生效」；regex 视图粘贴测试文本 → Ctrl+Enter → A → 表达式 `\d+` 填入、g/i 标志按返回设置、匹配/高亮/替换三块刷新、状态栏「AI 已生成 · deepseek-flash · 置信度 90 · 提取数字序列」。
- 修两个探针期真 bug：① `RefreshAiModels` 在 `_loaded=true` 前调用被入口守卫拦掉（下拉空、提示行空）；② `--settings` 不在 debug 分支。

**待真机验收清单**（内网 deepseek 真实 key）：① 设置拉到真实清单、切换模型写回、重启保持；② 真实生成（tool_choice 路径）+ 思考模型降级路径；③ 断网/超时 UI 不冻结、文案可读；④ AI 未配置时动作面板不出现 AI 项。

**设计讨论记录（2026-11-05 三轮拍板）**：

**一、共享层 `Core/AiClient.cs`（新建）**

- **配置**：读 `AppDirectory/ai-local.json`（与 v1 同名同字段：`enabled/provider/baseUrl/model/apiKey/temperature/maxTokens`；内网部署时手工放置，**不进包**，v1 pack 规则同款——v2 打包机制出现时须列入禁带清单）。
- **多模型（2026-11-05 用户提出，三次修正定稿：选择放设置窗口，列表来自服务端）**：`GET {baseUrl}/models`（Bearer，OpenAI 兼容端点）拉取 `data[].id`；配置**不维护模型列表**，顶层 `model` 字段即当前选中项。**切换入口 = 设置窗口新增「AI」分组**：模型 ComboBox（后台拉取填充，带「刷新」按钮）+ 选中即写回 `model` 字段（点击即生效约定）；拉取失败 → 提示行给原因，ComboBox 保留当前 model 兜底项可选，**不影响生成功能**。请求全部按当前 `model` 发。
- **Status()**：enabled → provider 白名单（`internal-openai-compatible`）→ baseUrl → model → apiKey，逐项给出不可用原因（v1 语义照搬）。
- **请求**：HttpClient（BCL，零第三方）POST `{baseUrl}/chat/completions`，Bearer 认证，30s 超时 + CancellationToken，async/await 不卡 UI。⚠️ .NET 4.6.2 默认不含 TLS 1.2，须 `ServicePointManager.SecurityProtocol |= Tls12`（经典坑）。
- **JSON**：JavaScriptSerializer（v2 已在用、csproj 已引用，悬念消除）。
- **响应解析（从 v1 ai-adapter.js 移植语义，离线可测）**：三层兜底——① tool_calls 的 regex_generate 参数；② content 内 JSON（剥 ``` 围栏 + 提取首尾大括号）；③ content 内 ```regex 围栏 / 行内正则。flags 过滤 g/i/m/s。
- **tool_choice 兼容重试**：首请求带 tools+tool_choice 强制函数调用；服务端报 tool_choice/thinking mode 不支持时自动降级为纯 JSON 输出重试一次（DeepSeek 思考模型实测路径）。

**二、RegexView 接入（对齐 v1 regex-tool）**

- **prompt 来源 = 测试文本全文**（trim，v1 同语义：把目标/样例写到测试文本里；为空时提示不发起请求）。
- **动作面板**：仅「AI 生成正则」（hint=A，未配置不列出）；模型切换在设置窗口（拍板点 5），不进面板。
- 生成中：状态栏「AI 生成中…」+ 请求不可重入。
- 成功：填表达式框 + 设 4 个标志位（g/i/m/s）+ 实时刷新三块结果；状态栏「AI 已生成 · <model> · 置信度 N · <reason>」。
- 失败：状态栏错误文案（AI 不可用：<原因> / AI 请求失败：<原因> / AI 返回格式无效）；请求类失败（超时/网络/HTTP 错误）文案追加「可在设置中更换模型」。

**三、拍板点**

| # | 问题 | 推荐 |
|---|---|---|
| 1 | 入口形态：动作面板加「AI 生成正则」项 vs 标志位行尾加按钮 | **动作面板**（v2 键位约定 Ctrl+Enter；hint=A，AI 不可用时该项不列出） |
| 2 | 结果展示：状态栏一句话 vs 独立结果块 | **状态栏一句话**（POC 简化；model/置信度/reason 够用） |
| 3 | 是否常驻「AI 可用性」徽标 | **不要**（v1 教训：绿徽标只反映配置齐全，不反映服务可达，超时后仍绿——见下「AI 状态展示原则」） |
| 4 | 模型清单来源 | **服务端拉取**（GET /models，2026-11-05 用户定稿）——配置不维护列表，供应商侧增删自动跟随 |
| 5 | 切换入口 | **设置窗口「AI」分组**（2026-11-05 用户定稿：换模型是低频配置动作，不进动作面板）——ComboBox + 刷新按钮；设置打开时后台拉取，不卡 UI |
| 6 | 拉取失败的 ComboBox 行为 | 提示行给原因；保留当前 model 兜底项，生成不受影响；「刷新」可重试 |
| 7 | 切换持久化 | 选中即写回 ai-local.json `model` 字段（点击即生效，与设置窗口既有约定一致；写失败提示并回滚显示） |
| 8 | 失败后是否自动切下一个模型重试 | **不自动**（重试耗时与计费不可控，用户知情选择）；请求失败时状态栏文案追加「可在设置中更换模型」 |

**AI 状态展示原则（2026-11-05 用户提出 v1 徽标失真问题后新增）**：

v1 病根：`renderAiStatus()` 的绿色徽标（「AI 配置可用」）判据只是配置文件检查（available = 文件存在 + 字段齐全），从不触网；服务超时/不可达只在请求时报错，徽标永远绿——「配置齐全」被用户读成「服务可用」。

v2 原则：**配置有效性与服务可达性是两个状态，绝不合并展示**。
- 配置级状态：**不展示徽标**——唯一的 UI 体现是动作面板里 AI 项是否列出（未配置 → 不列出，用户需要时无成本发现）。
- 服务可达性：**只认真实请求的结果**，状态栏按请求生命周期呈现——生成中（「AI 生成中…」）→ 成功（「AI 已生成 · <model> · 置信度 N · <reason>」）→ 失败（具体原因，超时单独成文如「AI 请求失败：连接超时（30s）」）。失败信息留在状态栏直到下一次动作覆盖。
- **不做开窗网络预检**：预检既不准确（结果可能过期）又浪费请求；状态栏真实结果即最准的「徽标」。

**四、SelfTest（全部离线）**

Status 六态判断、/models 响应解析（data[].id 提取/空清单/畸形响应）、ai-local.json model 字段写回与写失败回滚、payload 构建（含 tools/tool_choice 字段）、响应解析四层兜底、flags 过滤、围栏剥离。网络请求不进 selftest，真机联调单独验。

**五、真机联调**

内网 deepseek-flash 实测两轮：tool_choice 成功路径 + 思考模型降级路径；断网/超时验证 UI 不冻结、文案可读；设置窗口拉模型清单、切换后立即用新模型生成。

**改动面**：新增 `Core/AiClient.cs`；`Views/RegexView.xaml(.cs)` 接入；`Windows/SettingsWindow.xaml(.cs)` 加「AI」分组（模型 ComboBox + 刷新 + 原因提示行）；`Core/SelfTest.cs` 补用例；csproj 挂新文件。

**实现后状态（2026-11-05）**：已实现并自验（mock 全链路成功路径 + 真机失败路径），**待用户真机验收**。真机验收中发现两个问题，已修复：

1. **思考模型 tool_choice 降级未触发**（用户截图：DeepSeek 报「Thinking mode does not support this tool_choice」直达状态栏）——病根：HTTP 400 错误体在 `Request` 里被包进 `WebException.Message` 走 catch 分支，而降级判断只在 `parsed.Error`（响应体路径）上。修复：请求异常路径同样匹配关键词后降级重试（`IsToolChoiceError` 两条路径共用，selftest 补真机错误文本用例，83 项全过）。
2. **设置窗口超高**（加 AI 分组后 `SizeToContent="Height"` 随内容无限长高、超出屏幕）——修复：构造函数 `MaxHeight = 工作区高度 - 40`，内部 ScrollViewer 接管滚动。探针实测窗口 1720px（工作区 1760），AI 分组完整可见，且真实 GET /models 拉取成功（「共 2 个模型」）。

**用户复验反馈（2026-11-05 第二轮，均已实现待验收）**：

3. **思考模型降级真机验证通过**（身份证正则生成成功，置信度 82）。
4. **状态栏长文本截断**——`SetStatus` 时把全文挂 `ToolTip`（RegexView + DiffWindow 两处出口统一），悬停出气泡给全文。探针实测气泡出现。
5. **设置窗口分栏**——单页滚动只是兜底，内容本身没变短；改为「左侧分类导航（5 项：常规/剪贴板/右键菜单/AI/应用登记）+ 右侧当前分类内容」，窗口定高 600、底栏固定放「关闭」。各页控件与「点击即生效」逻辑零改动，纯布局重组；`ShowPage(tag)` 只切可见性。探针 5 页截图全部对位。
6. **导航样式扁平化**（用户反馈分类项「像按钮」→ 原型三案讨论 → 定稿方案 A）——导航项不再用主题胶囊模板，改为局部 `NavItemStyle`：透明底、无边框、方角，悬停 `Bg.ItemHover`，**选中 = 左缘 3px Accent 竖条 + `Accent.Soft` 淡蓝底 + 蓝字**（VS Code / Win11 设置风）。探针截图选中态与切换对位确认。

## 已验收：diff F3 备用键 N/P（2026-11-05，提交 `31794b4`）

**需求来源**：行号列冻结验收通过时用户发现——其他应用用 RegisterHotkey 全局注册 F3 后按键被系统截走，DiffWindow 的 PreviewKeyDown 收不到，跳转失效（系统机制，焦点应用侧无法抢回）。PyTools 自身未注册 F3，冲突来自第三方常驻软件。

**拍板（方案 A）**：保留 F3/Shift+F3，加备用键 **N（下一处）/ P（上一处）**——裸字母键几乎不会被全局注册；diff 窗口内无输入框，单键无误触面。仅排除 Ctrl/Alt（Ctrl+N/P 保留给全局键位约定），Shift 忽略（方向与大小写无关）。动作面板键帽同步 `F3/N`、`Shift+F3/P`；状态栏提示「F3/N 下一处 · Shift+F3/P 上一处 · Ctrl+Enter 操作 · Esc 关闭」；比较完成文案提示两组键。N/P 在 diff 窗口内不另作他用。v2 尚无独立用户手册，键位说明以窗口内状态栏/动作面板为准。

**自验**：构建通过；selftest 74 全过；探针实测 N → 差异 1/6、P → 「已到最前的差异，回绕到末尾 · 差异 6 / 6」（探针已删）。

**改动**：`Views/DiffWindow.xaml.cs`（OnPreviewKeyDown 备用键分支 + 动作面板键帽 + 完成文案）、`DiffWindow.xaml`（状态栏提示文案）。

## 已归档：diff 行号列水平固定（2026-11-05 验收通过，提交 `6f4cb64`）

**实现（仅 `Views/DiffWindow.xaml` + `.xaml.cs` 两个文件）**：

- XAML：每侧结果区拆成「46px 行号冻结列 + 内容列」双 ListBox；新增 `LeftGutterTemplate`/`RightGutterTemplate`（3px 状态条 + 行号 + 1px 分隔，Kind/IsFocused 触发器原样迁移）；行模板删掉行号列只剩内容。行号 ListBox：垂直滚动条 Hidden（仍可滚供同步）、水平 Disabled、`Focusable=False`、虚拟化参数照抄、`DiffItemStyle` 复用。行号模板 Border 加 `MinWidth=46`——ItemContainerStyle 的 HorizontalContentAlignment=Left 会把 ContentPresenter 收缩到内容宽，靠 MinWidth 铺满列（与内容行 MinWidth 绑定同性质）。
- 代码后置：4 个 ScrollViewer（左行号/左内容/右内容/右行号）；`OnAnyScrollChanged` 统一同步——**垂直**任一来源写其余三个，**水平**只在两个内容列之间互写；`SetVerticalAll` 统一写四个（各自按 ScrollableHeight 钳制，行号列视口略高多出的部分被钳掉不漂）；`CenterRow`/`Compare` 归零/`DebugPreload` 像素直滚全部走 `SetVerticalAll`；`HorizontalReveal` 不变（只动内容列）。
- **探针抓出真 bug 一枚**：`Compare()`/`ClearResult()` 最初只给内容列挂 `ItemsSource`，行号列整列空白——已补挂四个。

**自验（探针截图闭环，截图即删）**：

- selftest 74 项全过（引擎未动）。
- 1000 行夹具（diff-a/b.txt）：预载滚到首个差异后行号与内容逐行对位（垂直四列表同步正确）；F3 跳 row.50（value_50↔value_350）、row.70，整行淡黄 + 行号格黄底 + 状态条 Accent 两侧同步，状态栏「差异 x / 43」。
- top 夹具（diff-top-a/b.txt，612 字符长行）：F3 连跳至差异 6/6（row.20 中段 MID-A/MID-B），HorizontalReveal 触发横滚后**行号窄列仍完整钉在窗口最左缘**（行首已滚出视口、水平滚动条滑块居中），行号与内容对位不漂；零宽空格差异（row.18）竖条标识 + 状态栏提示正常。
- 附带发现（环境教训，已记 working memory）：Git Bash 直启 exe 时 `--debug` 等参数会被 MSYS 转换吞掉（日志实证 debug=False），自验探针必须由 python `subprocess.Popen` 直拉进程传参。

**验收结果（2026-11-05 用户真机确认通过）**：行号钉左 / 跳转变色对位 / 行号列滚轮 / 原有行为四项全部通过；验收时发现 F3 全局占用问题 → 衍生出上面的 N/P 备用键任务。

**改动面**：引擎、DiffTextBlock、Theme 令牌、动作面板全不动。

## 设计归档：diff 行号列水平固定（2026-11-04 讨论定稿）

**需求**：横滚超长行时行号列钉在左缘不滚出视口（text-diff 验收时的已知限制，原话记录见归档节末）。

**三个候选方案**：

| 方案 | 思路 | 评估 |
|---|---|---|
| A. 行内反向平移 | 行号留在行内，横滚时按 offset 反向平移 + Clip 裁剪 | 每行都要参与变换、虚拟化回收下边界残留难清，否 |
| **B. 冻结列双列表（推荐）** | 每侧拆成「46px 行号列 + 内容列」两个 ListBox，行号列不横滚，垂直滚动走 ScrollChanged 同步 | Beyond Compare / VSCode 常规做法；垂直同步机制现成（`SyncScroll` 扩展），改动集中在 DiffWindow.xaml(.cs) |
| C. 覆盖层自绘 | 行号画在列表上层的浮层里，按可见行区间动态生成 | 聚焦黄底/状态条色要再实现一遍、与虚拟化行对齐易漂，否 |

**方案 B 设计要点**：

1. **XAML**：结果区每侧 Grid 两列（46 行号列 + * 内容列）。新增 `LeftGutterTemplate` / `RightGutterTemplate`（3px 状态条 + 行号 + 1px 右分隔，Kind/IsFocused 触发器原样搬过来）；现有行模板删掉行号列，只剩 DiffTextBlock（背景触发器、MinWidth 绑定不动）。两个行号 ListBox：`VerticalScrollBarVisibility=Hidden`（要可滚供同步）、`HorizontalScrollBarVisibility=Disabled`、同样虚拟化 + Pixel + Recycling、`DiffItemStyle` 复用。
2. **滚动同步（代码后置重写核心）**：4 个 ScrollViewer（左行号/左内容/右行号/右内容）。规则：**垂直**——任一来源变化写其余三个；**水平**——只在两个内容列之间互写（行号列 Disabled 不参与）。`_syncingScroll` 防回环沿用。
3. **跳转/定位适配**：`CenterRow` 直接写 4 个（原来只写 2 个内容列，行号列靠同步跟上）；`HorizontalReveal` 只动内容列不变；`firstVisible` 等视口计算继续用内容列的 ScrollViewer；`DebugPreload` 像素直滚写 4 个。
4. **鼠标滚轮在行号列上**：行号列保持可命中（不 IsHitTestVisible=false），滚轮滚行号列 → ScrollChanged 同步带动内容列，行为一致；点击行号列无选中视觉（DiffItemStyle 模板就是裸 ContentPresenter），无副作用。
5. **已知小差异**：行号列没有横向滚动条，视口比内容列高约一条滚动条高度（~17px），垂直可滚范围略大——`ScrollToVerticalOffset` 自动钳制，同步写同一 offset 即可，无漂移（与现在左右两栏同步同性质）。

**改动面**：仅 `Views/DiffWindow.xaml`（+2 行号模板、结果区布局改双列）与 `DiffWindow.xaml.cs`（4 ScrollViewer 的发现与同步、CenterRow/DebugPreload 适配）；引擎、DiffTextBlock、Theme 令牌、动作面板全不动。预估 ~100 行。**selftest 不涉引擎，仍 74 项**；自验走 `--diff` 截图（超长行横滚后行号仍可见 + F3 跳转后行号格 Accent/黄底正常）+ 真机拖 1000 行夹具横滚验收。

## 已归档：text-diff（M4 第六项，2026-11-04 验收通过）

**形态拍板：独立常驻窗口（脱离呼出壳，v2 首个）**

- 启动器输 `diff`/`对比` → 呼出壳隐藏 → 打开/激活 diff 窗口；关闭 diff **不回呼出壳**（保持安静，下次热键再呼）；热键呼出启动器与 diff 并存互不干扰；托盘退出连带关闭 diff。
- 常驻 = 独立 HWND 天然失焦不隐藏；**任务栏显示图标**（长工作流要找得到）；可拖拽移动 + 可 resize。
- **无位置记忆**（用户拍板：关闭 = 一次工具调用工作结束，再开 = 新工作）——每次打开按 v1 口径默认：工作区 65%×75% 居中，min 900×560，max 1400×900；不做「恢复默认位置」入口。
- **Esc = 关闭窗口**（用户认同：diff 是长时间工作流，不像其它工具 Esc 分层退回）。

**引擎（`Core/DiffEngine.cs`，纯移植 v1 diff-worker.js 三层）**

- patience diff（唯一行锚点 + LIS）→ 行数 >30000 或无锚点退 Myers → 再退 anchorFallback（lookahead 80 就近配对）。行模型 equal / change（相邻 del+ins 逐行配对）/ delete / insert / empty（对面补空行）。
- **比较键 = 原行原文，零归一化**（用户拍板 2026-11-04：v1 剥 `<b>/<strong>` 是 HTML 面板误渲染打错的补丁，不是需求；针对任何文件内容不做特殊处理，原样显示原样比较——行尾空格、全半角差异都算差异）。仅读取层 CRLF/CR → LF 换行统一。
- change 行再做字符级 inline diff（v1 同款），行内片段高亮。
- `Task.Run` 后台计算（v1 是 Web Worker；WPF 无 worker，不能卡 UI 线程），带取消。

**文件装载（三入口，框上无按钮，用户拍板「不要太多按钮」）**

每侧一个文件槽（半宽、高约 72px：文件名粗体 + 路径小字；空态提示「拖入文件 / 点击选择 / Ctrl+V 粘贴」）：

| 入口 | 行为 | 规则 |
|---|---|---|
| 拖拽 | 框 AllowDrop，DragOver 高亮边框 | 校验单个文件：多文件/目录/非文件 → 边框红 + 状态栏原因；放下即读 |
| 点击 | 点框 → OpenFileDialog（常见文本扩展名 + 所有文件，同 v1） | 已有文件再点击 = 重新选择覆盖 |
| Ctrl+V | 框内读剪贴板 FileDropList | 取第一个文件（多余忽略并提示）；纯文本 → 状态栏「剪贴板为文本，仅支持文件」（**粘贴文本对比不做**，用户拍板） |

三入口共用装载链路：读文件（编码检测）→ 更新槽 → 清旧结果 → 触发比较判定。

**编码检测（GBK 需求，用户确认 2026-11-04）**：BOM（UTF-8/16LE/16BE）按 BOM；无 BOM → 严格 UTF-8 解码（DecoderExceptionFallback），失败按 GBK(936)（BCL 内置，零依赖）；不做编码选择器。selftest 加检出用例。

**比较触发（用户拍板 2026-11-04）**：**首次双侧就绪自动比较**；此后任一侧**切换**文件 → 结果清空 + 状态栏提示「文件已更新，按 Ctrl+Enter 选择开始比较」→ 动作面板手动比较。

**动作面板（Ctrl+Enter，用户拍板只有三项）**：选择左侧文件 / 选择右侧文件 / 开始比较（未就绪不显示）。复制左侧/右侧、交换左右等全不做。

**结果渲染**：双栏并排虚拟化 ListBox（上万行全量渲染不可行，UI 虚拟化），行模板 = 行号格 + Inline 集合（change 行内高亮用 Run，regex 同套路）；两 ScrollViewer `ScrollChanged` 互写 offset + 防回环（垂直+水平，v1 同款 syncing 语义）；空档行淡红底；底部状态栏常驻（「正在计算差异…」「比较完成，共 N 个差异行」「两个文件内容相同」文案照 v1）。

**配色**：Theme.xaml 新增 `Diff.*` 令牌组（增/删/空档/行内四色 + 行号列底；CSS 半透明 rgba 预混实色），不硬编码，给明暗主题留路。

**差异跳转（2026-11-04 增补拍板，已编码）**：

- **粒度 = 差异行**（change/delete/insert 各一步，空档占位行不算；不做行内片段级细分）。
- **键位**：`F3` = 下一处、`Shift+F3` = 上一处（不碰全局键位约定）。
- **提示与计数**：状态栏常驻「差异 x / y」；比较完成文案「共 N 处差异（F3/Shift+F3 跳转）」；状态栏右侧键位提示区加 `F3/Shift+F3 跳转差异`；**不加按钮**（用户拍板）。
- **回绕 + 提示**：到底再 F3 →「已到最后一个差异，回绕到开头 · 差异 1 / y」；反向「已到最前的差异，回绕到末尾」；无差异时「两个文件内容相同，无差异可跳转」。
- **命中高亮（聚焦态，2026-11-04 二次拍板定稿）**：命中行持续显示「聚焦」样式直到跳到下一处——**整行淡黄底**（`Diff.Focus.Bg` #FFF6D9，用户验收后要求调淡；Grid 模板 `IsFocused` 触发器覆盖该行增/删底色，行号格 `Background` 同步变黄——必须移进 Style，局部值会压过触发器）+ 行号格状态条 → Accent 蓝 + 行内片段底色加深（`Diff.Inline.Add/Del.Focus`）。用户明确否掉「行首黄块 + 上下蓝线」与「半宽黄带」方案：淡黄底铺满整行宽度，横滚后依然可见（行号列会随横滚滚出视口是已知限制，标记不受影响）。
- **跳转垂直居中**：`CenterRow`——命中行滚到视口垂直居中（`rowIndex × 22 − (ViewportHeight − 22)/2`，clamp 到可滚范围，两侧同步写同一像素值）。行已在视口内时 ScrollIntoView 几乎不动、用户会以为没跳，居中让每次跳转都有明确位移。
- **跳转水平复位（验收反馈修正）**：`HorizontalReveal` 从「仅 change 行定位片段」改为——change 行滚到片段 40% 处，**其余一律回行首**；否则长行差异留下的横滚 offset 会让下一个短行目标整行落在视口外（实测：黄行在屏上但文字看不见）。
- **定位基准（编码期修正）**：聚焦行可见时以聚焦行为基准（连按 F3 逐处前进），聚焦行不存在/被滚出视口才用视口第一可见行——纯视口基准在「目标行已在视口内、VerticalOffset 不变」时会反复命中同一处（实测踩过）；`_focusedSeq` 记录聚焦序号。
- **超长行水平定位**：change 行按第一个差异片段的字符偏移 × 等宽字符宽（Consolas，FormattedText 懒测一次）估算 x，两侧 ScrollViewer 分别滚到片段处于视口 40% 处（绕开同步互写）；实测 612 字符行中段差异 F3 一下正落视口中央。
- **动作面板**：+「跳到下一个差异」「跳到上一个差异」（键帽 F3/Shift+F3，仅面板内显示，面板单键快呼按单字母精确匹配不受影响），共五项。
- **零宽字符可视化**：差异片段含不可见字符（`DiffEngine.IsInvisibleChar` 白名单：U+00AD/U+061C/U+180E/U+200B-F/U+2060-4/U+FEFF/U+FFF9-B）时逐字符拆分渲染，字符原位以 `InlineUIContainer` 嵌 4px 圆角竖条（普通态 `Diff.Invisible` 灰、聚焦态 Accent 蓝）；Delete/Insert 整行差异同样处理；跳转命中含不可见差异的行 → 状态栏追加「此处差异为不可见字符（如零宽空格），以竖条标识显示」。普通空格/Tab 不可视化。
- `DiffRow` 实现 `INotifyPropertyChanged`（`IsFocused`），行模板 DataTrigger 与 `DiffTextBlock`（订阅 Row 变更，虚拟化回收换 Row 先退订旧实例）据此动态切换聚焦样式。
- **selftest +3 = 74**：跳转索引与前后查找含双向回绕边界 / 无差异返回 -1 与不可见字符白名单判定 / 不可见字符差异如实报告且片段标记 Changed。
- **DebugPreload 滚动改像素直滚**（`rowIndex * 22`）：虚拟化下 ItemsSource 挂载后同步 `ScrollIntoView` 实测滚不动（连 Dispatcher.BeginInvoke(Loaded) 也不可靠），像素滚动只依赖固定行高、确定性生效。
- **已知限制 / 待排期**：行号列在 ListBox 水平滚动内容内，横滚超长行时会滚出视口（实测确认，v1 同款）。「行号列水平固定（每侧拆行号+内容双滚动区，Beyond Compare/VSCode 常规做法）」记待排期，约 100 行改造，diff 验收后再议。

**selftest**：引擎逐语义用例（相同/change 配对/纯增/纯删/anchorFallback/30000 守卫/零归一化空白差异/字符级 inline）+ 编码检测（UTF-8 BOM/严格 UTF-8/GBK 检出）+ 唤醒词。

**测试文件（自验用，入库 `v2/tools/testdata/`，绝对路径 `C:\Users\richard\Develop\pytools\v2\tools\testdata\`）**：生成两个 ~1000 行文件，覆盖单行超长行（数千字符）/ 短行 / 缺行 / 增行 / 行内差异在头部、尾部；真机验收直接拖拽用。清单：`diff-a.txt`（GBK 编码，编码检测验收点）/ `diff-b.txt`（UTF-8）/ `diff-top-a.txt`+`diff-top-b.txt`（30 行小夹具，四类差异行集中在首屏）/ `make-diff-fixtures.ps1`（重新生成）。

**待排期（用户提出 2026-11-04，第一版不做）**：diff 历史记录——最近 N 条（时间 + 左右路径 + 差异行数），只存路径不存内容（diff 看当下状态，文件变了重算），点击记录重载两侧重算；复用剪贴板历史模式。**剪贴板历史记录「文件复制」条目**同记待排期（用户 2026-11-04 提出确认：diff 的 Ctrl+V 走系统剪贴板原始 FileDropList，不依赖剪贴板历史）。

**实现落点（2026-11-04 编码完成）**：

- `Core/DiffEngine.cs`：三层引擎纯移植（patience 唯一行锚点 + LIS → Myers O(ND)（30000 行上限）→ anchorFallback lookahead 80）；比较键 = 原行原文零归一化；change 行字符级行内片段（相邻同标记段合并减少 Run）；行模型 equal/change/delete/insert + 对面空档。
- `Core/TextFileLoader.cs`：编码检测（BOM 三种 → 严格 UTF-8 → GBK 936），检出结果随文件槽展示。
- `Views/DiffWindow.xaml(.cs)`：独立常驻窗口；文件槽三入口（拖拽校验单文件/单击 OpenFileDialog/Ctrl+V 跟随鼠标悬停侧）；虚拟化 ListBox ×2（Pixel 滚动单元 + Recycling）+ ScrollChanged 双向同步防回环；`DiffTextBlock : TextBlock` 自定义控件（Row/IsLeft DP → Run 集合重建，虚拟化回收幂等）；动作面板三项；状态栏。
- 接线：`BuiltinCommands` 加 diff 条目（`diff`/`对比`）；`App.ShowDiff`（**先 Show+Activate diff、再收呼出壳**，且收壳不恢复前台焦点——`HideSummon(bool, Action)` + `_skipFocusRestoreOnce`）；`--diff` 调试入口 + `--diff-left=`/`--diff-right=` 预载（DebugPreload 顺带滚到首个差异行，截图自验用）；托盘退出一并关闭 diff。
- `Themes/Theme.xaml` 补 8 个 `Diff.*` 令牌（增/删/空档/行内增删/行号列底/状态条×2，v1 rgba 预混实色）。
- selftest **+7 = 71 全过**（跳转增补后累计 **74**）：零差异与 CRLF 等价 / change 配对与行内（行尾 `3→5`、行头 `max_`）/ 纯增删空档与行号归零 / 零归一化（空白、`<b>` 标签、全半角）/ 重复行不崩 / 编码检测四态 / 唤醒词 / 跳转索引回绕 / 不可见字符判定与片段标记。
- 测试夹具 `v2/tools/testdata/`：`make-diff-fixtures.ps1` 生成 `diff-a.txt`（**GBK**）/`diff-b.txt`（UTF-8）/`diff-top-a|b.txt`（30 行小夹具，全行型集中首屏）；1000 行夹具含 3200 字符超长行、超长行尾差异、短行、空行、中文行、每 50/70 行尾/行头差异、每 120/130 行删/增。

**编码期踩坑（已修，均已带注释进代码）**：

1. **Myers 回溯两处 while 漏游标递减**（v1 JS 的 `{ ops.push; cx--; cy-- }` 移植时写成只 push）→ ops 无限增长撑爆 List（"Array dimensions exceeded supported range"）。selftest 的行内差异用例抓出，对照 v1 逐行核对修复。
2. **测试夹具设计错误**：想造删除行却在两侧各写了不同内容 → Myers 把相邻删+插配对成 change（语义正确），空档行渲染验证落空。改为「单侧独有行」才产生真正的 delete/insert。
3. **`.ps1` 无 BOM 坑再次踩实**：生成脚本含中文字面量，PS 5.1 按 ANSI 解析 → GBK 回写后字节无损回环，文件实际成了 UTF-8、GBK 检测验证失效。Write 工具产出无 BOM，写完必须补 BOM（已固化进流程）。
4. SendMessage 滚轮滚不动 WPF ListBox（两截图像素全等）→ 改「小夹具首屏直出」方案验证渲染，不走滚动注入。
5. **独立窗口焦点交接（2026-11-04 用户真机反馈「diff 窗口一开就往底层跳」）**：三个叠加因素——①壳 `Topmost=true` 且淡出 100ms 内仍可见，提前开窗会被盖；②`ShowDiff` 里 `HideSummon` 的 `_skipFocusRestoreOnce` 被壳的 `Deactivated → HideSummon()` 重入路径清掉（原来的 `if (_hiding || !IsVisible) { _skip = false; return; }`），收尾时 `Foreground.Restore(呼出前窗口)` 把前台抢走；③先藏壳再开窗，前台会落到进程内隐藏的 `PyTools.Hotkey` 消息窗口，`Activate()` 抢不回来。**定论：先 `Show+Activate` 新窗口再收壳，且重入路径绝不清 skip 标记**；另给 `HideSummon` 加 `afterHide` 回调（壳彻底隐藏后才执行的挂点，复用给后续独立窗口）。探针采样前台 pid/标题验证：52ms 即 diff 前台并稳定；壳 ↔ diff 往返焦点正确。

**自验**：构建 OK；selftest 71 全过；debug `--diff` 截图三张——1000 行夹具（GBK/UTF-8 徽标正确、中文行渲染正确、change 行琥珀色标记 + 行内高亮、状态栏「比较完成，共 43 个差异行」）；30 行小夹具（删除行红底+右空档淡红无行号、插入行绿底+左空档、两栏逐行严格对齐、行头/行尾行内高亮）。

**待用户验收清单**：① 启动器输 `diff`/`对比` → 呼出壳收起、diff 窗口居中弹出（任务栏有图标、常驻不失焦隐藏）；② 拖 `v2/tools/testdata/diff-a.txt` 进左框、`diff-b.txt` 进右框 → 自动比较（注意左槽徽标应显示 **GBK** 且中文行正常——编码检测验收点）；③ 拖多文件/文件夹 → 红框 + 状态栏原因；④ 资源管理器 Ctrl+C 复制文件 → 鼠标悬停某框按 Ctrl+V → 该侧装载；悬停框外 Ctrl+V → 提示；复制纯文本后 Ctrl+V → 「仅支持文件」提示；⑤ 已有结果后再换一侧文件 → 结果清空 + 「按 Ctrl+Enter 选择开始比较」；Ctrl+Enter → 动作面板（选择左/右文件、开始比较、跳转差异）→ 回车执行；⑥ 拖动窗口位置、resize、Esc 关闭；再次 `diff` → 新窗口回默认居中；⑦ 打开 `diff-top-a.txt`/`diff-top-b.txt` 看四类行渲染；⑧ **F3/Shift+F3 跳转**：连按逐处前进、行号格变蓝 + 片段加深、跳到 `row.18`（零宽字符）看竖条与状态栏提示、跳到 `row.21`（超长行）看水平定位把 MID-A/MID-B 带进视口、末尾回绕提示；⑨ diff 开着时热键呼出启动器互不干扰；托盘退出 diff 一并关闭。

## 已归档：regex-tool（M4 第五项，2026-11-04 验收通过）

**拍板（2026-11-04 讨论）**：

1. **布局 = 方案 A（左输入右结果，三块同屏）**：左栏 = 测试文本编辑区（等宽）；右栏纵向三块——匹配列表（起始位置 + 匹配值 + 捕获组逐条展开含命名组）/ 全文匹配高亮 / 替换结果预览，各自可滚动。不用 Tab 切换（对照价值 > 单块面积）。
2. **窗口 860×620**，走 cron 同款「工具固定尺寸模式」动画（比 cron 的 760 宽，双栏需要）。
3. **标志位 4 个 CheckBox**：全局 g（默认开）/ 忽略大小写 i / 多行 m / 点号跨行 s，输入即实时计算三路结果。
4. **匹配列表点选联动（做）**：点某条 → 高亮区滚动定位到该匹配处 + 短暂闪烁标注（v1 没有的增强）。
5. **AI 生成正则（v1 有此按钮）暂缓**：本次纯本地；**AI 批启动时第一个做 regex 的 AI 生成**作为 AI 功能联调的试验田（用户拍板 2026-11-04）。
6. **唤醒词**：`regex` / `正则`（口径表既有）。
7. **动作面板**（Ctrl+Enter）：复制表达式 / 复制替换结果 / 复制测试文本。

**能力范围（v1 tools/regex-tool 全对齐）**：表达式栏（Consolas）+ 替换串栏（支持 `$1`/`${name}`）实时计算；状态栏 = 匹配 N 条 / 正则无效（带原因）；匹配上限 200 条。替换语义用 .NET `Regex.Replace`（`$1`/`${name}` 与 JS 等价；无 g 只替换第一处，与 v1 一致）。高亮区 `TextBlock` + Run 集合着色（几百 Run 无性能问题），替换结果只读文本。

**实现要点**：纯本地，.NET `Regex` 原生，零第三方依赖；`Core/BuiltinCommands.cs` 登记条目；`App.xaml.cs` 接 `ShowRegex`（EnterToolSizeMode(860,620)）+ Esc 路由 + `--regex` 调试入口；csproj 登记。

**验收方式**：selftest（解析/标志位组合/替换语义 $1·${name}/命名组/上限截断/无效正则报错原因）；截图（空态、匹配+高亮+替换三块同屏、错误态、点选联动闪烁帧）；真机链路（粘贴文本 → 输表达式 → 看列表/高亮/替换联动 → 点列表定位 → Ctrl+Enter 复制）。

**实现落点（2026-11-04 编码完成）**：

- `Core/RegexTester.cs`（纯本地引擎，零 WPF 依赖）：`Evaluate`（g 控制收集条数、上限 200 截断、命名组在前/编号组在后）+ `Replace`（无 g 只换第一处）+ 250ms `RegexMatchTimeout`（灾难性回溯防线，v1 无）；空表达式 Ok+0 匹配（视图显示等待态）。
- `Views/RegexView.xaml(.cs)`：方案 A 布局；高亮 = `TextBlock`+Run（匹配段 Accent.Soft 底 + Accent 字）；点选联动 = `Run.BringIntoView()` + 650ms Accent 深蓝闪烁（DispatcherTimer 单发还原）；三态文案照 v1（等待输入正则/正则无效: 原因/没有匹配/已显示前 200 条匹配/替换后为空）；零长匹配显示「(空匹配)」占位。
- 接线：`BuiltinCommands` 加 `regex`（别名 regex/正则）；`App` 加 `_regexView`/`ShowRegex`(860×620)/Esc 分支/两处 ExitActionMode 收口/`--regex`+`--query` 调试入口/启动器分发。
- **进入保留上次输入**（v1 面板 DOM 常驻语义；与 cron 清空拍板不同——正则调试常来回切）。
- **selftest +8 = 63 全过**：基础匹配与位置/无 g 单条/标志位 i·m·s（含无 m 反例）/组展开顺序/替换 $1·${name}·无 g/无效正则与零长匹配推进不卡死/200 截断与空表达式/唤醒词。
- **编码中抓出的语义坑（selftest 抓出 + PowerShell 探针确认）**：.NET 混合命名组时**未命名组先编号**——`(?<year>\d{4})-(\d{2})` 里 year=2、未命名组=1，GetGroupNames 纯数字名即未命名组，只展开它们则 `$n` 与 JS 语义一致（替换串 `$1` 同样指第一个未命名组）。

**自验**：构建 OK；selftest 63 全过；真机截图——空态（占位/默认勾选全局）、预填态（没有匹配三态）、实时态（键入 abc123 xy89 → 匹配 2 条位置 1/8、高亮淡蓝、状态栏）；动作面板 Ctrl+Enter 弹出（独立 HWND 枚举证实 300×128 浮层）；点选闪烁（连拍 6 帧像素取样：被点项深蓝 Accent、其余淡蓝 Soft）。

**验收**：2026-11-04 用户真机验收通过（进工具动画 / 三块实时联动 / 标志位 / 报错 / 点选闪烁 / 替换语义 / Ctrl+Enter 动作面板 / Esc 保留输入）。AI 生成正则随 AI 批启动时第一个补做。

## 已归档：S10 启动清理历史自启残留（2026-11-04 实现并自验）

v1 缺陷（2026-11-04 实测）闭环：Electron `setLoginItemSettings` 的 Run 键名随版本漂移（残留如 `electron.app.pytools-1.6.0`），取消自启删不到旧键 → 每天自启旧包。v2 实现（`Core/Startup.cs` + `App.OnStartup`）：

- `IsLegacyName(name)`：值名含 "pytools"（不分大小写）且不是本应用固定键名 `PyTools` → 残留。
- `CleanupLegacy()`：启动时（Logger.Init 之后）打开 HKCU Run（可写），逐值名判定并删除；只动 HKCU、单条失败不中断、注册表不可用静默；删除列表写运行日志。
- selftest +1 = **64 项全过**（判定口径：v1 漂移键/简式键判残留、本键名及其纯大小写变体豁免、无关键名不误判）。
- **真机链路自验**：伪造 `electron.app.pytools-1.6.0` → 冷启动 Release exe → 注册表确认残留已删、日志出现 `removed legacy autostart Run entries: electron.app.pytools-1.6.0`。
- 编码期坑：`App` 类内裸写 `Startup.xxx` 会被解析为继承事件 `Application.Startup`（CS0079）→ 须全限定 `PyTools.Core.Startup`。

## 已归档：UI 控件规范化（2026-11-03 验收通过，提交 `155e9a9`）

**拍板（2026-11-03）**：单选形态 = **淡蓝底 + 蓝字**；设置窗口一并纳入；先出 HTML 原型再编码（原型已确认：`.workbuddy/tmp/ui-controls-prototype.html`，验收后可删）。

**实现**：

- `Themes/Theme.xaml` 补令牌：`Border.Hover #C9D4EA`（输入 hover 边框）、`Accent.Hover #4A7EF0` / `Accent.Pressed #2258C7` / `Accent.Soft #E3EAFB`、`Bg.Disabled #F2F3F5`、`Text.Disabled #9EA0A4`、`Text.OnAccentDisabled #9DB4EE`、`Layout.ControlHeight 26`、`Layout.ChipPadding 12,0`。
- **`Themes/Controls.xaml`（新）**：隐式样式库——Button（白底 1px 边框 6px 圆角高 26，hover ItemHover / pressed ItemSelected / 禁用灰）、`AccentButton`（蓝底白字主操作，hover 亮一档 / pressed Accent.Pressed）、RadioButton 胶囊化（选中 = Accent.Soft 底 + Accent 描边 + 蓝字，hover 在前选中在后保证选中项悬停不变灰）、ToggleButton（数值网格，选中与单选同语义）、TextBox（focus 边框 Accent；不设 Height，搜索框透明无边框走局部值天然兼容）、CheckBox（15×15 圆角 4，选中 Accent 实底白勾）、ComboBox 全套重模板（Face=Style 置空的 ToggleButton + 浮层 Popup 白底投影圆角、item 高 24 圆角 4、选中项淡蓝底蓝字）。由 `App.ApplyTheme` 在 Theme/Lite 之后合并，**XAML 不写 Style 自动生效**；约定：视图不得再写局部 Background/BorderBrush/Foreground（局部值压过样式触发器）。
- 视图去局部值：EncodeView（8 个 radio Foreground、双 TextBox 底/边框）、FormatView（5 按钮 + 6 radio + 编辑区）、SettingsWindow（7 CheckBox + 7 按钮 + 3 TextBox）、CronView（ExprBox、MonoBox）。
- CronView 代码后置：点选网格 `Button`→`ToggleButton`（选中态改 `IsChecked`，删除手绘 Background 切换与 `FindResourceStatic`）；构造器模式下拉列宽 66→**78**（新内边距下「自定义」不截断）；复制按钮 → `AccentButton`。
- **对齐约定（2026-11-03 用户反馈补充）**：输入框一律左对齐（`Controls.xaml` TextBox 显式 `TextAlignment=Left` 固化）；只有「定宽窄值框」（cron 秒/分步长、区间起止这类三五个字符宽的数值框，`MonoBox(width)`）才局部设 Center。cron 顶部表达式框：Padding `8,5`→`6,0`（文字贴左起头、垂直居中交给 VerticalContentAlignment），FontSize 14→**15**——WPF `TextBox` 没有 `LineHeight`（那是 TextBlock 的属性），光标高度只由字号×字体行距决定，14px Consolas 行高仅 ~16.8px，在 30px 高的框里像短竖线。
- **样式波及修复（同日第二波反馈）**：① 主/vsc/剪贴板三个搜索框的占位符 `Margin=2` 按「默认模板 Padding=0」对齐，被样式默认 Padding=8 推开错位 → 三个搜索框补局部 `Padding="0"`（第一版补 2 仍差 2px——ScrollViewer Margin 与模板固有偏移叠加，光标穿进首字；占位符 `Margin=2` 对应的就是默认模板固有偏移本身）；② 样式默认 `VerticalContentAlignment=Center` 把 encode/format 多行编辑区内容少的文字推到框中部 → TextBox 样式**不设**该属性（默认 Top），单行框各自局部设 Center（3 搜索框、settings 3 框、cron ExprBox/MonoBox、ActionFilter 已全覆盖）。

**自验**：构建通过；selftest 55 项全过；真机截图（PrintWindow，自启 `--debug` 实例同回合起停）——encode（胶囊选中态/focus 蓝边框）、format（按钮统一/撤销禁用灰）、cron（下拉重模板 + 键盘注入展开浮层：白底圆角投影、选中项淡蓝底蓝字；`--query` 反解指定模式网格「9」高亮；自定义不截断；AccentButton 复制钮）、settings（复选框蓝底白勾、按钮/输入框统一）。注意：浮层是独立 HWND，PrintWindow 截主窗截不到，需 ImageGrab 抓屏区域。

**待用户验收清单**：主面板 → 逐个进 encode / format / cron 看单选胶囊、按钮、输入框 focus 态；cron 里点开下拉看浮层、切「指定」点数值网格；打开设置窗口看复选框与按钮；hover / 按下手感；简洁模式（重启后开「简洁模式」再进 cron，控件圆角两形态一致）。

## 已归档：cron 工具（M4 第四项，2026-11-03 验收通过，提交 `e1e1ea7`）

**拍板（2026-11-03 讨论）**：

1. **功能范围**：与 v1 tools/cron 完全对齐——六段式（秒 分 时 日 月 周）+ 5 段 crontab 兼容 + 宏（@daily 等 / @reboot）；表达式⇄构造器双向实时联动；四模式/字段（每N / 指定点选 / 区间 / 自定义）；中文解析摘要；最近 N 次触发（5/10/20，4 年上限）；错误段定位标红；参数没填完不算错只提示补齐。
2. **布局**：**上下堆叠 + 双栏构造器 3+3**（左秒分时 / 右日月周；HTML 原型对比后用户拍板），底部摘要 + 最近触发列表（限高滚动），窗口放大到 760×560。v1 的逐字段明细列表不移植（底部空间留给触发列表）。
3. **唤醒词**：`cron`，无中文别名（「定时」「计划」太泛，留给以后真正的定时任务工具）。
4. **不记忆上次表达式**（用户拍板：cron 使用多为临时推算，每次进干净状态）。
5. **工具固定尺寸模式**（新机制，cron 首用，参照 v1 window-manager setToolWindow）：进入时窗口从当前尺寸**中心锚定**动画放大到 760×560（180ms 三次缓出，`Motion.ToolResizeMs` 令牌），Esc 回启动器动画还原；藏窗时瞬间还原再落盘（位置记忆不被污染）；每个视图可自选启用（Format/Encode 保持现状）。用户选定「中心锚定」而非 v1 的跳工作区居中。

**实现**：

- `Core/CronExpr.cs`：cron-core.js 全量移植（parse / describe / fieldToBuilder / buildField / nextRuns，4 年上限 + 200 万步防死循环）。
- `Views/CronView.xaml(.cs)`：构造器 6 行在代码后置构建（4 模式参数面板叠放、点选网格 WrapPanel、文本框为最终数据源、正在输入的控件不回写）。
- `App.xaml.cs`：`EnterToolSizeMode` / `ExitToolSizeMode` / `AnimateWindowBounds` / `SnapRestoreBounds`；进入在 Content 切换前（SizeToContent 切 Manual + 冻结高度防瞬跳），退出在 Content 切换后（还原高度按新内容自然尺寸 Measure 实测）；ShowShell 增 4 参重载；Esc 路由 / OnLaunchRequested / debug `--cron` 已接。
- `BuiltinCommands` 加 cron 条目；Theme 加 `Motion.ToolResizeMs`。

**过程踩坑（已修）**：

- 冷启动直呼 cron 时窗口未 Show，`Left/Top` 为 NaN，还原点记成 NaN → Esc 还原动画 `BeginAnimation(NaN)` 抛 ArgumentException 崩进程（事件日志实锤）。修法：`CaptureRestoreBounds` 未显示时按 config + EnsureOnScreen 同口径取默认位；`AnimateWindowBounds` 加 NaN 防御。
- XAML 加载期 `CountBox` 的 `IsSelected="True"` 触发 SelectionChanged，后排控件未构造 → NRE（同 encode RadioButton 坑），加 null 守卫。
- 周字段 Max=7，点选网格 0-6（7 也是周日，核心层归一化）。
- **2026-11-03 退出后高度冻结（已修）**：几次切换测试后回到主面板，窗口高度冻结、列表有值也撑不开——动画退出路径上 `_toolSizeMode` 从未复位（只在 SnapRestoreBounds 藏窗路径复位），完成回调 `!_toolSizeMode` 恒不成立 → `SizeToContent` 永不恢复，Height 冻结在退出瞬间的实测值。修法：`ExitToolSizeMode` 启动动画前先置 false。往返探针验证：启动器 640×121 → cron 760×560 → Esc 640×121 → 输入过滤词高度长到 640×210（列表重新能撑开窗口）。
- **2026-11-03 藏窗闪烁反馈（已修）**：cron 放大态下点击工具外/切窗，窗口「跳变闪烁再消失」——`HideSummon` 在窗口还可见时就 `SnapRestoreBounds()`（760×560 瞬缩 640×420）再淡出，缩放跳变叠加淡出被看见。修法：藏窗时保持原样淡出，`CompleteHide` 的 `Hide()` 之后再瞬间还原（不可见时缩放无感）；落盘直接写 `_toolRestoreBounds`。退出路径本就先还原再落盘，未动。
- **2026-11-03 cron 界面被压回内容自然高（已修，真凶与最初判断不同）**：藏窗→再呼出循环后 cron 以 ~640×210（内容自然高）显示，宽/位置正常、约半数触发。回退二分证明**不是**闪烁重排、也不是高度冻结修复引入——是尺寸机制与生俱来的竞态，此前自测（单次进入 / Esc 往返）均未覆盖「藏窗→再呼出」循环。机制：`SizeToContent` 切 Manual 后，内容首次挂载仍可能异步把窗口高度压回内容自然高（`SizeToContent=Height` 行为复活），托管层显式 `Height`、DP 动画、逐帧赋值都拦不住（终值会被随后的内部 `SetWindowPos` 覆盖）。修法：**Win32 级防线**——进入动画落定后 `_window.MinHeight = 目标高`（经 WM_GETMINMAXINFO 钳制一切内部 SetWindowPos；不能在动画开始就设，会钳掉中间帧使增长动画跳变），退出动画前 / SnapRestoreBounds / 冷启动以外的还原路径清零。复现探针 6/6 轮 760×560 全过（闪烁修复同时在线），Esc 往返四步（121→560→121→210 列表撑高）正常。
- **2026-11-03 用户反馈二则（已修）**：① 秒/分的「指定」与「自定义」完全同质（秒/分 60 值做不了点选网格，无网格的指定只剩文本框）→ 秒/分下拉不再提供「指定」（只留 每…/区间/自定义），模式映射改为每行 `Modes` 数组；表达式反解遇秒/分列表降级为自定义直写（值不丢）；`ShowParam` 同步改为按模式名而非下拉下标映射面板（否则秒/分下标错位显示出错面板）。② 行尾该段表达式预览列删除——所选值已在输入框/按钮高亮中，且 Auto 宽度随内容变化挤压参数区（「UI 一直变」的来源）。

**自验**：构建通过；selftest **55 项全过**（新增 9 项：六段/五段解析、宏、错误定位、触发推算、月末跳过、日+周或规则、构造器往返、BuildField 校验、唤醒词）；debug `--cron` / `--query` 截图——空态 + 预填态（表达式反解、点选高亮、摘要、触发列表与 selftest 断言一致）；键击注入探针自动化：藏窗→再呼出 ×6 全部 760×560、Esc 往返四步（640×121 → 760×560 → 640×121 → 640×210 列表撑高）全过。

**待用户验收清单**：`cron` 进工具（窗口放大动画）→ 输表达式看构造器反解与摘要 → 点构造器看表达式生成与触发列表 → 错误表达式看标红定位 → Ctrl+Enter 复制表达式 → Esc 回启动器（窗口还原动画）→ 位置记忆不被放大污染（藏窗重开后位置正常）。

## 已归档：encoding-converter（M4 第三项，2026-11-03 验收通过，提交 `6af788d`）

**拍板（2026-11-02 讨论）**：

1. **视图形态**：呼出壳内非列表型视图——顶部工具栏（格式选择 + 方向选择，方向随格式联动）+ **上下堆叠双栏编辑区**（输入在上、结果只读在下；用户拍板不用 v1 的左右双栏——编码内容一行要展示更多，避免挤成柱状）+ 底部状态栏；固定高。
2. **唤醒词**：`encode` / `编码`（口径表既有）。
3. **6 种格式 × 2 方向**（与 v1 一致）：URL、Base64（↔ UTF-8）、Unicode（↔ `\uXXXX`）、HTML 实体、JSON（标准 JSON ↔ JSON 字符串字面量）、Hex（↔ UTF-8）；方向下拉的示例提示文案照 v1。
4. **实时转换**：输入即转，无按钮；失败 = 状态栏警告「无法按当前方向转换，已原样保留输入」（结果=原文）。
5. **剪贴板预填（用户提议，2026-11-02 拍板）**：**每次从启动器进入工具**都读剪贴板文本填入输入区并立即按当前格式/方向转换显示结果——呼出 → 进工具 = 「转换我刚复制的东西」动线每次成立。①初始格式/方向**记住上次选择**（持久化 config，首次默认 URL/编码 = v1 默认值），不用自动识别（纯文本极易被误判成 Base64/Hex）；②**只读不写**——预填不写回剪贴板，结果进剪贴板走「复制结果」（与 format 的双写语义区分）；③空剪贴板/图片/文件 → 输入区留空正常手动输入；④剪贴板文本原样填入（含换行），不预处理。
5. **复制结果**：补进动作面板（Ctrl+Enter），v1 无此按钮但按 v2 面板惯例顺手补上。
6. **移植技术要点**：URL 编码手写转义集对齐 JS `encodeURIComponent`（保留 `!'()*`，不用 `Uri.EscapeDataString`）；Base64/Hex 解码严格 UTF-8（`DecoderExceptionFallback`，非法字节走警告路径）；Unicode 按 UTF-16 码元（C# `char` 天然一致）；JSON stringify 保留非 ASCII 原文（复用/扩展 format 的 JSON writer，不用 `JavaScriptSerializer`）；HTML 实体解码用 `WebUtility.HtmlDecode`（BCL 零依赖，实体表全）。
7. **键位**：全局约定——Ctrl+Enter 动作面板（复制结果）、Esc 回启动器空态、输入框输入不干扰。

**验收方式**：selftest 对照 v1 输出（6 格式 × 双向 + 失败路径 + URL 保留字符 + 严格 UTF-8 报错 + JSON 非 ASCII 保留 + 剪贴板预填与记住上次选择）；截图三态；真机链路（复制内容 → 进工具直接出结果 → 切格式重转 → 复制结果；长 URL/Base64 上下栏观看体验；HTML 实体含命名实体；JSON 双向）。

**状态**：**编码完成（2026-11-03）**。落点：`Core/TextEncoders.cs`（六格式引擎，JS 语义逐条对齐）、`Core/StructuredFormats.cs`（+JsonEncodeLiteral/JsonDecodeLiteral 复用既有 writer）、`Core/AppConfig.cs`（v2.encodeFormat/encodeDirection + SaveEncodeChoice）、`Views/EncodeView.xaml(.cs)`（上下堆叠双栏 + 格式/方向单选 + 方向提示 + 剪贴板预填）、`Core/BuiltinCommands.cs`（encode 视图型条目）、`App.xaml.cs`（ShowEncode / Esc 路由 / 收面板 / --encode 调试入口）、csproj、selftest +7（共 44 全过）。**编码中抓出的 bug**：① `StrictUtf8` 调用缺 count 参数（编译期）；② Unicode 反转义 `for+continue` 双重步进吞字符（selftest 抓出，步进补偿修正）；③ XAML 加载期 `IsChecked="True"` 触发 `Checked` 时兄弟控件未构造 → `OnOptionChanged` NRE（加 null 守卫）。**自验**：debug `--encode` 截图——剪贴板预填 → URL 编码结果即时显示 → 状态「转换完成」，方向提示「编码 → %E4%B8%AD」，四角平滑无缺角。

**顶部间距调整（2026-11-03 用户反馈）**：encode 与 format 从主搜索栏进入后「顶部空间被挤、有压迫感」——根因是工具栏型视图无搜索框头部区，内层 Grid 边距 `10,8,10,8` 扣掉窗框后视觉上距顶仅 ~7px。两个视图内层边距统一改为 `12,14,12,12`（上 14 / 下 12 / 左右 12），已重建 Release 并截图自验（两视图顶部呼吸空间正常、圆角无缺角），随 encoding-converter 一并待验收。

**剪贴板图片缩略图修复（2026-11-03 用户反馈，已完成待一并验收）**：图片记录无缩略图预览。根因不在列表（模板本有 Thumb 绑定）而在**捕获**——部分程序的 32 位 DIB alpha 字节全 0（RGB 完好），`TryReadPng` 直接编码出全透明 PNG。修复：①捕获时 `CoerceBrokenAlpha` 校正（全 0 alpha 强制 255，不经过预乘换算）；②存量坏 PNG `RepairIfBroken` 惰性修复（缩略图触发 → 重写 + sha1 更新，写回同路径）；③副标题尺寸改从 PNG IHDR 读原图尺寸。`BitmapImage + StreamSource` 解码抛「键不能为 null」原因不明，改 `PngBitmapDecoder` 直解。selftest +2 = 46 全过；真机截图缩略图全部可见。

**待用户验收清单**：① 复制一段中文/URL → 启动器输 `encode`/`编码` 回车 → 输入区自动填入剪贴板内容并立即显示转换结果；② 切换格式/方向 → 结果即时刷新、方向提示变化 → 重启后记住上次选择；③ 解码非法输入（如对乱码 Base64 解码）→ 状态栏警告 + 结果=原文；④ Ctrl+Enter → 「复制结果」→ 粘贴验证；⑤ Esc 分层（面板 → 启动器空态 → 藏窗）；⑥ 长内容上下栏滚动、无横向溢出。

## 已归档：format 工具 + 剪贴板入口（M4，2026-11-02 验收通过，提交 `7fbcc61`）

**拍板（2026-11-02 讨论）**：

1. **视图形态**：呼出壳内首个非列表型视图——顶部工具栏（操作按钮 + 格式/缩进选择）+ 等宽编辑区 + 底部状态栏；**固定高**（~460px，不做内容自适应）。
2. **唤醒词**：`format` / `格式化`，**同词双条目**：主列表同时显示「格式化」（工具面板，回车进视图）与「格式化剪贴板」（动作条目，回车直接执行 + 气泡反馈）；面板条目在前。`fc` 不复活。
3. **注册表机制扩展**：`BuiltinCommand` 增加**行为类型**——视图型（切视图）/ 动作型（执行 + 气泡，不弹窗）；`BuiltinCommands.Match` 改为可返回多条，SummonView 按既有权重合并进主列表。该机制 M4 后续工具的快捷动作复用。
4. **格式**：JSON + XML，**不加 YAML**（与 v1 一致）。
5. **XML 引擎**：**移植 v1 手写保真 tokenizer 到 C#**（XML 声明/注释/CDATA/混合内容逐字符保留，行为与 v1 一致）；JSON 用 .NET 原生（缩进 2/4/Tab）。
6. **面板内「格式化剪贴板」**：读剪贴板 → 格式化 → **编辑区 + 剪贴板双写**（v1 既有行为）→ 状态栏报「已格式化并写回（JSON，N → M 字符）」。
7. **启动器动作条目「格式化剪贴板」**：**静默执行**——不弹窗，读剪贴板 → 自动识别 → 格式化（固定 2 空格）→ 写回 → **托盘气泡**报结果（含格式与字符数变化）；失败走气泡（剪贴板非 JSON/XML、读取失败），不打断当前工作。参照 v1「添加到 PyTools」静默先例。
8. **面板操作**（与 v1 对齐）：格式化剪贴板 / 格式化输入 / 压缩 / 复制结果 / 撤销（恢复上次内容）；格式选择 自动/JSON/XML；缩进 2 空格/4 空格/Tab。
9. **键位**：全局约定——`Ctrl+Enter` 动作面板（动作 = 上述 5 操作）、`Esc` 回启动器空态、编辑区输入不干扰。

**验收方式**：selftest（JSON/XML 格式化用例对照 v1 输出、压缩、撤销、动作条目命中与执行路径）；截图（启动器双条目、format 面板、动作面板、状态栏成功/错误）；真机（fc 语义：复制一段压缩 JSON → 输 `format` → 选「格式化剪贴板」回车 → 粘贴验证；XML 含注释/CDATA 用例过面板）。

**状态**：**编码完成（2026-11-02）**。落点：`Core/StructuredFormats.cs`（引擎移植）、`Core/BuiltinCommands.cs`（Kind + MatchAll 多条）、`Views/FormatView.xaml(.cs)`（面板视图）、`App.xaml.cs`（ShowFormat / 动作型静默执行 / Esc 路由 / 收面板）、`Views/SummonView.xaml.cs`（双条目头插）、`Themes/Theme.xaml`（+Text.Success/Text.Error 令牌）、selftest +4（共 37 全过）。**编码中修的移植 bug**：① `ResolveIndent` 返回 `"2"` 而非 2 空格；② JSON 缩进误用 `Append(unit,0,n)`（子串拷贝语义，非重复）；③ `CollapseTagWhitespace` 漏剥 `=` 后空格（`x= "1"`）；④ `NormalizeText` 的 while+Substring 在探针下异常退出（重写为 `TrimStart('\uFEFF')`，根因未完全解释，测试已守护）。**自验**：selftest 37 全过；debug `--format` 截图三态（面板视图布局 / Ctrl+Enter 呼出动作面板——面板为独立 Popup HWND，PrintWindow 截主窗不可见，用枚举独立 HWND + 尺寸证实）。**待用户验收清单**：① 启动器输 `format`/`格式化` → 双条目（面板在前）；② 回车进面板 → 粘贴压缩 JSON → 格式化输入 / 压缩 / 复制 / 撤销；③ 格式化剪贴板（双写验证）；④ XML 含注释/CDATA 用例；⑤ 启动器「格式化剪贴板」动作条目回车 → 粘贴验证 + 气泡；⑥ Ctrl+Enter 动作面板、Esc 分层；⑦ 剪贴板入口 `clipboard`/`剪贴板`/`jtbjl` 回车进剪贴板 → Esc 回启动器。



## 已归档：vsc 工具移植与修复（2026-11-02 验收通过，提交 `c6681ca`、`d4400e4`）

## 真机反馈修复记录（2026-11-02，已修待复验）

1. **视图切换丢焦点**（现象：进 vsc 后全部快捷键/回车失效，Esc 回启动器后主搜索框也无焦点）：根因为**窗口可见中切换 Content 时新视图 Loaded 在下一拍才触发，`ShowShell` 同步 `Focus()` 因 `IsLoaded=false` 静默失败**（焦点落到窗口根，只剩窗口级 Esc 隧道事件活着）；首呼出路径 `Window.Show()` 同步完成 Loaded 所以一直没暴露。修复：`ShowShell` 的 `FocusSearch` 延迟到 `DispatcherPriority.Loaded`。探针日志验证：修前 `ok=False` → 焦点元素 `SummonWindow`；修后 `ok=True loaded=True` → `TextBox`（三个环节：首呼出 / 进 vsc / Esc 回启动器）。
2. **vsc 键位对齐全局约定**（用户约定：Ctrl+J/K/N/P/Enter 全局统一）：vsc 列表态补齐 `↓/Ctrl+J` 下移、`↑/Ctrl+K` 上移，动作面板开关改回 **Ctrl+Enter**（此前错用 Ctrl+K）；提示条键帽同步；剪贴板视图核查本已一致。
3. **设置窗口新增「呼出位置 → 恢复默认位置」按钮**：清除 `config.json` 的 `windowBounds`（下次呼出走默认位：水平居中、垂直上 1/3）；呼出中点击立即归位。背景：Debug/Release 是两个 exe 目录、`config.json` 各自独立记忆位置，用户发现两版初始位置不同（非 bug），提供一键重置入口（`AppConfig.ResetBounds` + 设置窗口「常规」区按钮行）。

## 进行中：vsc 工具移植（M4 第一项，已编码待真机验收）

**拍板（2026-11-02）**：① 动作只要 5 个（用 VSCode 打开 / 打开项目所在目录 / 在 CMD 打开 / 在 Git Bash 打开 / 复制项目路径），PowerShell 与 npm 脚本动作本次不做；② Git Bash 用 **mintty 独立窗口**；③ **支持多根工作区**（.code-workspace 也列出）；④ Esc **逐层退回**：动作面板 → 项目列表 → 启动器空态 → 藏窗。

**实现状态（2026-11-02）**：全部落地。交付 `Core/ActionItem.cs`、`Views/ActionPanel.xaml(.cs)`（组件化，SummonView 已改为第一个消费者）、`Core/VscProjects.cs`、`Core/TerminalLauncher.cs`、`Core/BuiltinCommands.cs`（最小注册表，仅 vsc）、`Views/VscProjectView.xaml(.cs)`；App 接 `ShowVsc` + Esc 分层 + `--debug --vsc` 自验开关；selftest 28→**32**（新增 URI 解析 / workspaceStorage 夹具 / Git Bash 检测自洽 / vsc 别名匹配）。截图已验证：vsc 列表（含多根条目、footer）→ Ctrl+K 面板 5 动作 + 键帽 → Esc 恢复；启动器回归（Ctrl+Enter 面板 7 动作）；入口链路 `vsc` → 回车切视图 → Esc 回启动器空态。端到端拉起 Git Bash 成功。

**编码期实测修掉的三个坑（都已带注释进代码）**

1. **WPF 绑定不吃字段**：`ActionItem` 初版用 public 字段，面板行全部空白（chips 也在）；改属性修复。教训与旧 `ActionVM` 一致。
2. **`Uri.LocalPath` 对 `%3A` 盘符给出 `/c:/...`**：必须剥前导斜杠 + 盘符大写（v1 的 `fileURLToPath` 没这个问题）。
3. **`psi.EnvironmentVariables` 在本机会炸**：机器环境同时有 `HTTPS_PROXY` 与 `https_proxy`，大小写不敏感字典合并直接抛「已添加项」——任何 `UseShellExecute=false` + 触碰该属性的代码都会踩。改为**本进程临时注入 `CHERE_INVOKING`**（try/finally 清除），子进程按继承拿到。**此后凡是要给子进程传环境变量，一律走这条路**。
4. 另：Git Bash 检测顺序定为 **注册表 → 常见/scoop 路径 → PATH 反推**——`where git.exe` 在本机首条命中第三方 vendor 自带的 PortableGit，会把别人的 Git 当用户的（PATH 只配兜底）。

**真机验收清单（用户）**：① 启动器输 `vsc` 回车进项目列表；② 过滤；③ Ctrl+Enter 面板与键帽鼠标呼出（↑↓/Ctrl+J/K 移动、Ctrl+N/P 翻页与启动器一致）；④ 「打开项目所在目录」「在 CMD 打开项目」逐个点，确认落点目录正确；⑤ 「在 Git Bash 打开项目」确认是 mintty 外观且目录正确；⑥ Esc 逐层退回；⑦ 回车 = 用 VSCode 打开。**已验证（2026-11-02）：焦点、键位均通过。**

**与 v1 的差异（v1 `tools/vsc` 是 embedded 形态，动作表在 `main/result-actions.js`）**

| 能力 | v1 | v2 |
|---|---|---|
| 打开项目 | `cmd /c code <path>` | 同（`code` 不在 PATH 时补 `%LOCALAPPDATA%\Programs\Microsoft VS Code\bin\code.cmd` 兜底） |
| 打开目录 | shell.openPath | `explorer.exe <path>` |
| CMD | `terminal.open`（conhost 包裹） | `cmd.exe /d /k`，WorkingDirectory=项目 |
| Git Bash | 无 | **新增**：mintty + `bash -l -i`，见下 |
| 多根工作区 | 只读 `folder`，多根项目不显示 | **新增**：读 `workspace` 字段（.code-workspace） |
| 动作面板 | Tab 菜单（全局复用主搜索框） | 独立浮层 + 独立过滤框（本轮 POC 的组件） |

**交付物**

1. `Core/ActionPanel`（组件）：从 `SummonView` 抽出 Popup + 卡片 + 5 行上限 + 独立过滤框 + ↑↓/Enter/Esc/单键直执行/鼠标定位。API：`Show(FrameworkElement anchor, IList<ActionItem> items)` / `Hide()` / `IsOpen`；事件 `Invoked(id)`（宿主执行）、`Dismissed`（宿主决定焦点去向）。`ActionItem` = `{ Id, Title, Hint, Available }`（Available=false 直接不列出，用于 Git Bash 未装）。`SummonView` 改造成第一个消费者，**行为必须零变化**（回归靠现有 selftest + 截图）。
2. `Core/VscProjects.cs`：扫 `%APPDATA%\Code\User\workspaceStorage\*\workspace.json`；`folder`（file URI）→ 目录、`workspace`（file URI）→ .code-workspace 文件；`Uri.LocalPath` 解析（盘符统一大写）；按 storage 目录 mtime 降序；路径规范化去重。JSON 用 `JavaScriptSerializer`（项目已引用 System.Web.Extensions）。多根条目：标题取文件名去扩展名，`Path` 取文件所在目录（供目录/CMD/GitBash），打开时传文件。
3. `Core/TerminalLauncher.cs`：`OpenCmd(dir)` / `OpenGitBash(dir)` / `DetectGitBash()`。检测顺序：注册表 `HKLM|HKCU\SOFTWARE\GitForWindows\InstallPath` → `where git.exe` 反推安装根（`<root>\cmd\git.exe` → `<root>`）→ 常见路径（Program Files / Program Files (x86) / `%LOCALAPPDATA%\Programs\Git`）→ scoop 布局（`%USERPROFILE%\scoop\apps\git\current`）；**必须排除 `C:\Windows\System32\bash.exe`（WSL）**，判定要求同根下存在 `bin\bash.exe` 与 `usr\bin\mintty.exe`。
4. `Views/VscProjectView.xaml(.cs)`：与 `ClipboardView` 同壳同款（搜索框 + 列表 + 空态提示），过滤按标题 + 路径双匹配；行 = 首字母占位块 + 标题 + 路径副标题；底部提示条（左「打开 ↵」右「操作 Ctrl+Enter」）；Ctrl+Enter 呼出动作面板（键位与启动器全局约定一致，2026-11-02 修正）。
5. `Core/BuiltinCommands.cs`：最小注册表，只登记 vsc（别名 `vsc`、标题「VSCode 项目」、副标题「打开最近项目」），启动器搜索命中后回车切视图；结构留 M4 接其余工具。
6. `App.xaml.cs`：`ShowVsc` 走既有 `ShowShell`；记住「是否从启动器进入」以支持 Esc 逐层退回；`EscapePressed` 链路扩展为「当前视图的动作面板 → 当前视图的上一层」。

**Git Bash 启动（已实测）**：`mintty.exe -t "Git Bash" <root>\bin\bash.exe -l -i`，`WorkingDirectory = 项目目录`，`CHERE_INVOKING=1`（Git for Windows 的 `/etc/profile` 认此变量，否则 login shell 会 cd 回 home）。实测命令 `CHERE_INVOKING=1 mintty bash -lc "pwd"` 输出为指定目录，机制成立。mintty 无 `--dir` 参数，靠继承 cwd。

**动作表（项目条目，提示键互不重复）**

| 键 | id | 标题 |
|---|---|---|
| O | `open` | 用 VSCode 打开项目 |
| F | `open-folder` | 打开项目所在目录 |
| C | `cmd` | 在 CMD 打开项目 |
| G | `git-bash` | 在 Git Bash 打开项目（未装则整条不出现） |
| P | `copy-path` | 复制项目路径 |

**验收**：① 构建 + selftest（新增 URI 解析/去重排序/Git Bash 检测三项）；② 截图三态：启动器输 `vsc` 命中条目 → 项目列表 → Ctrl+Enter 动作面板（Git Bash 已装则应显示 5 条）；③ 真机执行 CMD 与 Git Bash，确认起窗目录 = 项目目录、Git Bash 是 mintty 外观；④ 未装 Git Bash 的场景用「临时把检测写坏」或路径不存在模拟，确认动作整条隐藏。

**主要风险**：`SummonView` 的动作面板抽取是本轮唯一有回归风险的改动（刚验收完的浮层行为）。对策：抽取后跑 selftest + 截图复验应用列表的动作面板（5 行上限、细滚动条、Esc 分层、鼠标呼出）四项，全部复现才算通过。

## 待排期需求：明暗主题（2026-11-02 用户提出）

**需求**：支持**亮色 / 暗色**两套主题，且**可指定也可跟随系统**（三档：亮色 / 暗色 / 跟随系统）。

**建议排期：M4 之后**。理由：M4 要移植 11 个工具面板，会引入大量新视觉面（工具视图、动作覆盖层、结果区、状态色）；主题先做等于每个工具落地后都要回改一遍暗色。反向约束写进 M4 设计：**工具视图禁止硬编码颜色，一律走令牌**（本需求的前提）。

**现状（实现基础已就位，成本主要在配色与验收）**

- 颜色/圆角/阴影全部集中在 `Themes/Theme.xaml` 令牌层，`App.ApplyTheme` 以它作基底、按需追加覆盖字典进 `Application.Resources.MergedDictionaries`——暗色即第二份「覆盖字典」，不动控件 XAML。
- ⚠️ **`Theme.Lite.xaml` 与主题正交、但当前有冲突**：它把 `Bg.Window` 硬编码为亮色 `#F7F8FA`。暗色 + 简洁模式组合时会被它盖回亮底。修法：Lite 只保留「圆角/阴影/动效」三类别覆盖，窗口底色改为主题层按 `Bg.Window` / 简洁态各自给值（或新增 `Bg.Window.Opaque` 令牌由主题负责）。⇒ 组合矩阵是 **2×2（亮/暗 × 普通/简洁）= 4 种**，验收需全覆盖。
- 暗色配色不能照抄 `docs/design-system.md` 的工具面板 tokens（那套是 v1 HTML 工具面板的深色，`#2f2f38` 底）——壳与工具视图要统一，需单独定稿一套壳级暗色令牌（背景/卡片/输入/hover/选中/边框/三级文字/滚动条 `Border.Scrollbar`/阴影）。
- 跟随系统检测：`HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize` → `AppsUseLightTheme`（DWORD，1=亮 / 0=暗，Win10 1607+ 可用，1709 满足）。**跟随策略取「每次呼出时重读注册表」**，不挂 `WM_SETTINGCHANGE` 广播——窗口长期隐藏，重读成本为零，比监听广播更省心（也避免隐藏态收不到消息导致不同步）。
- 配置：`config.json` 新增 `v2.theme`（`"light"` / `"dark"` / `"system"`），默认建议 `system`。
- 连带面：设置窗口标题栏 DWM 深色开关要跟随主题；托盘/应用图标缓存的白底图标在暗色下的可读性；呼出窗阴影与 1px 边框在暗底的对比。

**验收方式**：4 组合截图过目 + 切系统主题后下次呼出生效 + 指定档不受系统影响 + selftest 增主题解析项；暗色下主列表/动作面板/剪贴板/设置窗口四类界面分别自查。

## 待确认需求：内置命令别名（2026-11-01 用户反馈）

**反馈**：v1 里输入 `clip` 或按快捷键都能呼出剪贴板；v2 只剩热键入口，输入 `clip` 搜不到任何东西。

**定性**：v1 的别名属于工具插件体系（`tool.json` 的 `keywords` + `commands[].aliases`），v2 工具是 M4 逐个原生移植，别名机制整体尚未重做；但剪贴板已在 M3 移植完却没有别名入口，是当下真实缺口。

**方案（草案，待确认）**

1. 新增 `Core/BuiltinCommands.cs`：内置命令注册表（id / title / subtitle / keywords / 搜权重），剪贴板为第一个条目；M4 各工具沿用同一注册表。
2. 搜索合并：`AppIndex.Filter` 现在内部算分后丢弃（仅按名字排序），改为对外返回分数；`SummonView` 把命令条目与应用条目按 v1 权重合并排序（命令 id/别名 rank0、命令标题 rank1、工具关键词 rank2；应用名 rank1、应用关键词 rank3）。
3. 别名表与 v1 完全一致：`clip`、`clipboard`、`history`、`剪贴板`；不新增缩写。
4. 条目渲染：标题「剪贴板记录」+ 副标题「查看剪贴板历史」+ 灰底字符块占位（与剪贴板视图同类占位一致），不新增图标资源。
5. 回车切剪贴板视图（复用 M3 的 `ShowShell` 切 Content），不新建窗口。
6. 不做「工具列表」入口（v1 输入 `tools` 浏览全部工具）——当前只有剪贴板一个内置命令，M4 工具齐了再补。

**用户拍板（2026-11-01）**：① 归属 **M4 一起做**；② 从启动器进剪贴板后 **Esc 退回启动器空态，再 Esc 才藏窗**（热键直呼路径 Esc 仍一步藏窗）；③ 不新增缩写，别名表与 v1 一致。

**口径修正（2026-11-02 定稿，覆盖上面 ③ 与下方草案）**：唤醒词**精减**——每工具 = **英文 id 主词 + 至多一个中文别名**；工具标题的拼音首字母自动参与命中（复用应用搜索 `Pinyin.Expand`），不再多挂同义词；**v2 无命令直达**（v1 `commands[].args`/`fc`/dev-values 子命令整体废除，工具只有面板形态）。全表：剪贴板 `clipboard`/`剪贴板`（**仅此一条用完整单词**，用户拍板；前缀匹配下输 `clip` 仍可命中，其余工具维持缩写主词）、format `format`/`格式化`（json-format 改名后从 `json` 改词）、encoding-converter `encode`/`编码`、dev-values `dev`、regex-tool `regex`/`正则`、cron `cron`/`定时`、text-diff `diff`/`对比`、env-switcher `env`、ai-ocr `ocr`/`识别`、ai-translate `fanyi`/`翻译`、code-naming `var`/`命名`、vsc `vsc`。

**落地（2026-11-02）**：`BuiltinCommands` 加剪贴板条目（`clip`/`剪贴板` + 标题拼音首字母）；`OnLaunchRequested` 分发 `clipboard` → `ShowClipboard(fromLauncher:true)`；Esc 分支按 `_clipFromLauncher` 决定「回启动器空态 / 一步藏窗」。selftest 33 项全过（新增唤醒词命中项）。

**追加（2026-11-02 二）**：用户拍板**砍掉「命令形态」整体**（v1 `commands[]` + args + 精确匹配参数解析）——实际使用基本不用，徒增逻辑复杂度。后果：① dev-values 六子命令不再是搜索入口直达，改为进面板后切模式；② json-format 的 `fc` 移除，格式化走面板；③ `BuiltinCommands` 退化为纯「工具入口条目」（id/别名/标题/副标题/执行回调），无 args、无子命令、无 rank0 参数解析。**别名精选定稿**（数量不设上限、原则是有意义才留）：剪贴板 `clipboard`+`剪贴板`、ai-ocr `ocr`+`识别`、ai-translate `fanyi`+`翻译`、code-naming `var`+`命名`、cron `cron`+`定时`、dev-values `dev`+`values`、encoding-converter `encode`+`编码`、env-switcher `env`、json-format **`format` 单别名**（覆盖 XML 语义；json/中文别名均去，用户有新习惯再加）、regex-tool `regex`+`正则`、text-diff `diff`+`对比`、vsc `vsc`。匹配沿用 exact>前缀>包含打分（输 `clip` 以前缀命中 `clipboard`）。工具面板快捷操作借鉴 Raycast：右下角常驻 `Ctrl+Enter 操作` 提示 + 底部动作覆盖层（过滤/↑↓/回车/Esc），主操作按钮保留；动作表为每工具声明式注册。（键位后经用户确认全局统一：Ctrl+J/K 移动、Ctrl+N/P 翻页、Ctrl+Enter 面板开关，见「真机反馈修复记录」2）

**追加（2026-11-02 三）**：① **工具全部保留**——11 个未移植工具全部列入 M4 移植清单；② **Ctrl+K 全局动作面板定稿**：启动器操作菜单与工具动作面板统一由 Ctrl+K 触发（Raycast 模式），Tab 释放（过渡期保留 Tab 退出语义）；启动器原 Ctrl+J/K 选中移动加速器让位移除（↑↓ 方向键与 Ctrl+N/P 覆盖）。**POC 已先行**（未提交，待用户试手感）：SummonView 触发键 Tab→Ctrl+K、Ctrl+J/K 移除、占位文案改「过滤操作…（Esc 返回）」、注释同步；selftest 28 全过；Release/Debug 均已构建。用户试后决定：手感 OK 则该改动随 M4 提交，不行则回退。

**M4 范围**：11 个工具全部移植（**format**（原 json-format，无 `fc` 的面板化改造，唤醒词 `format`/`格式化`）/ dev-values 无子命令直达的模式面板），开发辅助 4 个（hello / hotkey-probe / settings / ui-demo）不移植。**M4 顺序（2026-11-02 调整）**：纯本地高频（format、encoding-converter、dev-values）→ 复杂逻辑（regex-tool、cron、text-diff、env-switcher）→ AI 三件套（先做 AI 配置功能 + AiClient 适配层，再 ai-ocr、ai-translate、code-naming）。




## M2b 归档摘要

**交付（5 块，设计稿 §9-§12）**

1. **Tab 操作菜单（L5）**：Tab 进入（搜索框转「过滤操作…」+ 右缘「操作」徽标），v1 `createAppActions` 七项 + 第 8 项「移除登记」（仅 IsCustom 显示）；动作可输入过滤；↑↓/数字键/回车/点击执行；Esc/Tab 返回结果列表；成功收起呼出窗，失败 toast 原因并退回列表（v1 1.8.1 语义）。
2. **右键登记（L8）**：`Core/ContextMenuRegistry.cs` Registry API 直写 HKCU（exefile+lnkfile\shell\AddToPyTools），command=`"<exe>" "--add-app=%1"`；启动自愈（configured 比对完整期望串，不符则幂等重写）；开关点击即生效 + 实况回读。
3. **静默添加（L9）**：第二实例把路径写入 exe 旁 `add-app.msg` + 命名信号转发（不经命令行）；首实例解析 lnk（≤2 层）→ 校验 → 查重 → 写清单 → 重扫 → 托盘气泡；冷启动带参静默驻留。目录目标返回 not-file（v1 语义）。
4. **设置窗口（L10）**：托盘「设置」（`--settings` 自验开关）；主热键重录（失败回滚）、开机自启（HKCU Run，实况回读）、右键菜单开关、拼音/步长/简洁模式、登记清单管理；深色标题栏（DWM）。
5. **selftest 18 项全 PASS**；exe 190 KB。

**验收迭代（用户真机反馈三处 UX）**

- 热键捕获引导：点击/GotFocus 即进捕获态（蓝色高亮边框 + 提示文案），Esc/成功之后 MoveFocus 移出，避免「框内按键无响应无提示」；
- 布局稳定性：提示行改**常驻占位**（热键行空闲显示引导文案、右键菜单行常驻说明），任何状态都不推挤下方表单；
- 垂直居中：`HotkeyBox` 补 `VerticalContentAlignment="Center"`（WPF TextBox 默认 Top，固定高度会贴顶）。

## M3 归档摘要（`4584315`，17 文件 +2243/-124）

交付与自验数据见 `docs/v2-m3-design.md` 基线表。要点：`AddClipboardFormatListener` 事件驱动采集、与启动器共用呼出壳切 Content、100 条自动循环淘汰（无清空入口）、`clipboardHotkey` 默认空且旧配置值原样读取、设置窗口「剪贴板」分组（Win+V 试注册按钮 + 隐私过滤开关默认开）、selftest 28 项全过、exe 212KB（Release）。用户真机验收「功能正常」（2026-10-30）。

## 下一步：M4 AI 工具集（设计讨论待启动）

范围（`docs/v2-plan.md` §M4）：ai-ocr（截图 + 调用）、翻译、正则、命名等逐个移植；HttpClient + 本地 key 文件沿用 v1 `ai-local.json` 机制（该文件含明文 key，部署时手工放置，不入打包）。设计讨论要点待定：工具入口形态（Tab 菜单扩展 vs 独立视图）、ai-ocr 的截图交互与结果呈现、逐工具还是合并交付。

**顺序约定（2026-11-04 用户拍板）**：AI 批启动时**先给 regex-tool 补「AI 生成正则」**（v1 tools/regex-tool 既有能力，本次移植暂缓）作为 AI 功能联调的试验田，再做 ai-ocr / ai-translate / code-naming。

## 历史里程碑摘要

- **M3 剪贴板历史**（`4584315`）：交付明细见 `docs/v2-m3-design.md`（定稿口径全量记录，含采集/历史/视图/回填/热键五块与 28 项 selftest）。
- **M1 壳骨架**（`b07ad96` + 视觉修复 `8cff93d`）：热键、双形态呼出窗、前台 capture/restore、托盘、单实例、日志、selftest；首显 168ms / 再次 84ms。
- **M2a 启动器**（`b0455ea`）：扫描（IShellLink P/Invoke）+ 拼音首字母（内嵌表、多音字变体 ≤8）+ 连续滚动列表（数字键 1-0 与角标**锚定可视区、滚动重编号**）+ 动态高度（空态仅搜索框）+ 启动/死条目 + 图标落盘缓存 + 托盘重扫；selftest 14 项；空态呼出 55ms、138 应用 ~0.4s、图标缓存 39 张/142KB。
- v1 冻结：同仓根目录，tag `v1-final`，最新发布 1.8.1。

## 需求与设计索引

- 重写验收矩阵：`docs/v2-rewrite-matrix.md`（S/L/C/T/D 五组）
- v2 总计划：`docs/v2-plan.md`
- M1 设计稿：`docs/v2-m1-design.md`
- M2 设计稿：`docs/v2-m2-design.md`（14 节，含 M2a/M2b 全部定案）
- M3 设计稿：`docs/v2-m3-design.md`（14 节，剪贴板历史全部定案）

新需求先在本文件记录需求、方案、步骤和验证方式；需求确认与开始编码是两个独立关口，用户明确确认后才改源码。
