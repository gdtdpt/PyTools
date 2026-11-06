# v2-M3 剪贴板历史设计

状态：**方案已确认（2026-10-30 用户拍板：热键默认留空+试注册、旧配置值原样显示不迁移、上限常量、隐私开关默认开、不做清空入口），待「开始编码」关口**
日期：2026-10-30
依据：`docs/v2-plan.md` §4-M3、`docs/v2-rewrite-matrix.md` §3（C1-C4）+ S9；v1 行为基准 = `src/main/clipboard-history.js` / `clipboard-hotkey.js` / `settings.js` / `renderer.js`（v1-final tag）。

## 0. 范围

| 项 | 内容 | 矩阵 |
|---|---|---|
| 采集 | Win32 剪贴板监听（事件驱动）、文本/图片/文件三类记录、落盘 | C1 |
| 存储 | `clipboard-history.json` + `clipboard-history/images/*.png`，格式沿用 v1（v2 首启直读 v1 数据） | C1 |
| 视图 | 直呼视图 + 过滤 + 锚定可视区角标 + 选择回填 | C2 |
| 缩略图 | 按记录 id 缓存，**呼出时不重编码**（v1 性能根因） | C3 |
| 容量 | 100 条上限 + 图片/文本单体上限 | C4 |
| 热键 | 剪贴板直呼热键默认留空（不预设）、启用时试注册 `Super+V` | S9 |

不做：云同步、图片编辑、单条删除/多选/清空历史（按 §4 上限自动循环淘汰即可）、跨设备、富文本（RTF/HTML）保留、配置字段迁移。

## 1. 默认值口径（不做任何配置迁移）

- 代码默认 `clipboardHotkey = ""`（未设置、不注册）、`clipboardHotkeyEnabled = false`。全新安装（无 `config.json`）即此状态 —— **默认不预设任何键位**。
- **已有 `config.json` 里的值原样读取、原样显示，不迁移、不改判、不清理**（2026-10-30 用户拍板）。键位属于用户数据，哪怕它来自某个旧版本的默认值，也应当让用户看到后自行判断要不要换。
- 已知历史，仅供排查参考：v1 `settings.js:6` 的默认值是 `Super+V`；v2 `AppConfig.cs` 误抄成更早版本的 `Alt+B`。所以老机器的 `config.json` 里可能是二者之一 —— **都不处理**。M3 只把代码默认值改为空串，从此新装机器不再带任何预设键位。
- 由此**本设计不含任何字段迁移逻辑**：不引入配置版本号，不写 `configVersion` 之类的闸门。
- 首版设计稿曾写"读到 `Alt+B` 判为未设置"（等价永久封杀该键位），用户否决；改为版本号闸门后用户进一步指出"根本不需要处理，显示出来让人自己判断即可" —— 现按后者执行，逻辑最简且零风险。

## 2. 采集：事件驱动（C1，`Core/ClipboardWatcher.cs`）

**不用轮询**。v1 是 500ms `setInterval`（`POLL_INTERVAL_MS`），漏配快速连续复制，且空转耗电。

- 独立隐藏顶层窗口接收消息：`new HwndSource(new HwndSourceParameters("PyTools.Clipboard"){ WindowStyle = WS_POPUP, ExtendedWindowStyle = WS_EX_TOOLWINDOW })`，创建后不 `Show`。
  **不用 message-only 窗口**（`ParentWindow = HWND_MESSAGE`）：`AddClipboardFormatListener` 对非顶层窗口的行为没有文档保证，不拿这个赌。
- `AddClipboardFormatListener(hwnd)` / `RemoveClipboardFormatListener(hwnd)`；消息 `WM_CLIPBOARDUPDATE = 0x031D`。
- 收到消息 → 投递到 UI 线程（STA）读取，**不在 WndProc 里做重活**。
- **读不到要重试**：消息到达瞬间剪贴板常被源进程锁定，`OpenClipboard` 会 `ERROR_ACCESS_DENIED`。退避 3 次（10 / 30 / 90ms），仍失败则丢弃本次（下次复制会再触发，不留队列）。
- **防自激**：写回剪贴板后把 `_lastSignature` 预置为该记录的签名，自己引发的那次 update 直接判为重复丢弃。

## 3. 读取与类型判定（C1）

WPF 的 `System.Windows.Clipboard` 就是 STA 线程可用的封装，**全程零 P/Invoke**（v1 靠 Electron 的 `clipboard` 模块）：

| 优先级 | 判定 | 记录类型 |
|---|---|---|
| 1 | `DataFormats.FileDrop` | files（取 `string[]`，规范化全路径） |
| 2 | `DataFormats.Bitmap` | image（`BitmapSource` → PNG 编码） |
| 3 | `DataFormats.UnicodeText` / `Text` | text |

高优先级命中即止（与 v1 `readClipboardSnapshot` 顺序一致）。空文本 / 空文件列表视为无效快照。

**隐私格式过滤（`v2.clipboardPrivacyFilter`，默认 `true`）**：

| 格式 | 判据 | 强度 |
|---|---|---|
| `ExcludeClipboardContentFromMonitorProcessing` | **存在即跳过** | 强语义：程序明确要求"别监控我"（浏览器隐私模式、密码管理器） |
| `CanIncludeInClipboardHistory` | 存在且解析为 `0` 才跳过 | 弱语义：只声明"不进系统历史"；值解析不出（拿不到 DWORD）按允许处理 |
| `CanUploadToCloudClipboard` | **不参与判断** | 只管云同步，与本地留存无关 |

开关关闭时三个格式全部忽略，一切复制都进历史。

## 4. 去重、容量与上限（C4，常量）

```csharp
const int  MaxRecords      = 100;
const int  MaxPreviewChars = 200;                  // 标题单行预览上限（v1 同）
const long MaxTextChars    = 1024 * 1024;          // 单条文本上限 1M 字符
const long MaxImageBytes   = 20L * 1024 * 1024;    // PNG 编码后上限 20MB
const long MaxImagePixels  = 25_000_000;           // 25MP：挡住高压缩率大图解码爆内存
const int  ThumbDecodeEdge = 64;                   // 缩略图解码边长（含 2x DPI 余量）
```

- **签名**：text = 原文；files = 小写路径以 `\0` 连接；image = PNG 字节的 SHA1。同签名视为同一条记录 → 更新时间并移到首位（v1 `upsertSnapshot` 语义）。
- **超限处理**：文本超 1M 字符 / 图片超 20MB 或 25MP → **整条不记录**，写日志。不做截断存储——截断后写回会给出不完整内容，比不记录更危险。
- **淘汰**：超过 100 条从尾部删除，同时删对应 PNG 文件（v1 `trimRecords` 语义）。
- 图片记录 ID 即 PNG 文件名（v1 同构），加载时文件名必须与 `id + ".png"` 严格匹配（v1 有此校验，保留）。

## 5. 缩略图与内存（C3）

v1 的性能根因是**每次呼出把全部图片记录重编码成 data URL**。v2 的解法：

- 落盘只存原图 PNG（`clipboard-history/images/<id>.png`），**不额外生成缩略图文件**。
- 列表用 `BitmapImage` + `DecodePixelWidth = ThumbDecodeEdge` 解码：WPF 在解码期就缩放，内存只有缩略图大小，不经过全尺寸位图。
- `Dictionary<string, BitmapSource>` 按记录 id 缓存解码结果；upsert 时该 id 失效（对应 C3 的"upsert 失效"）。
- **懒加载**：只解可视行的缩略图（`ListBox` 虚拟化 + 容器生成时触发），100 条历史不预解码。
- 验收目标：44 图历史呼出 < 100ms（`v2-plan.md` §2）。

## 6. 写回与焦点时序（C2）

**顺序不可颠倒**：先收起窗口并恢复前台焦点，再写剪贴板。

v1 `renderer.js:917-922` 正是这个顺序（`hide()` 之后才 `selectClipboardHistory`）。反过来做会让焦点停在 PyTools 上，用户按 Ctrl+V 粘到自己窗口里——这是 M1 已经趟过的坑。

写回内容按类型：text → `Clipboard.SetText`；image → `Clipboard.SetImage`（PNG 解码后的 `BitmapSource`）；files → `DataObject` + `SetFileDropList`。写入用 `Clipboard.SetDataObject(data, copy: true)` 保证进程退出后内容仍在。

写回成功后：该记录移到首位、更新 `updatedAt`、存盘、刷新视图。

## 7. 视图承载：复用呼出壳做视图切换

**不新建窗口**。`App.ShowSummon/HideSummon/EnsureOnScreen` 是已验收的主流程（焦点 capture/restore、动效、位置记忆、双形态），新窗口要么复制一份要么抽出参数化编排，两者都是对稳定代码的侵入。

做法：`SummonWindow` / `SummonOpaqueWindow` 的 `Content` 在 `SummonView` 与新增的 `Views/ClipboardView` 之间切换（两个实例预创建，切换只换引用，不重建）。

需要的极小改动：

- `SummonWindowBase.FocusSearch()` 现在写死 `Content as Views.SummonView` → 改为 `Content as ISearchFocusable`（新接口，`void FocusSearch()`），两个视图各自实现。
- `App` 增加 `ShowClipboard()`：`_window.Content = _clipboardView` → `ResetForClipboard()` → 走原有 Show 流程；`ShowSummon()` 前把 `Content` 设回 `_view`。
- 位置/宽度记忆**共用同一份 `windowBounds`** —— 两个视图在同一位置弹出，体验一致，不新增配置键。

**热键切换语义**（对齐 v1 `onShown → resetToLauncher`，并补上跨视图切换）：

| 当前状态 | 按主热键 | 按剪贴板热键 |
|---|---|---|
| 窗口隐藏 | 显示应用列表 | 显示剪贴板视图 |
| 显示应用列表 | 收起（toggle） | 原地切到剪贴板视图 |
| 显示剪贴板视图 | 原地切回应用列表 | 收起（toggle） |

## 8. 剪贴板视图布局与键位（C2）

沿用主窗的令牌与行样式，不新造视觉：

- 顶部输入框：占位「搜索剪贴板…」，实时过滤；**空查询 = 显示全部记录**（注意：应用列表是"空查询不显示"，两个视图语义不同，别串）。
- 列表：最多 10 行可视，超出连续滚动；行 = 数字角标 + 缩略图/类型图标 + 标题（首行预览，200 字截断，单行省略）+ 副标题 + 右缘类型徽标（文本 / 图片 / 文件）。
- **角标锚定可视区**（M2a 已定案的同一条规则）：可见 10 行永远挂 1~0，滚动后重编号，首可见行按 `ItemContainerGenerator` + `TransformToVisual` 的"露出 ≥60%"判定，不用行高估算。
- 键位：↑/↓、Ctrl+J/K、Ctrl+N/P、数字键 1-0、滚轮/滚动条、回车（回填）、点击（回填）、Esc（收起）。
- 过滤行为对齐 v1 `filterEmbedded`：有查询时**图片记录不参与匹配**（图片没有可搜文本）；空查询才显示图片。
- 空态：无记录 → 「暂无剪贴板记录」；有查询无命中 → 「没有匹配的记录」。
- 两种形态（透明 / 简洁）一致。

## 9. 剪贴板直呼热键（S9）

**默认留空 = 不预设、不注册**，配置时才试 Win+V。

| 项 | 定稿 |
|---|---|
| 配置默认 | `clipboardHotkey = ""`、`clipboardHotkeyEnabled = false`（启用与键位分离，未配置即关闭） |
| 已有配置值 | `config.json` 里已有键位（`Super+V` / `Alt+B` / 其他）→ **原样显示在输入框、原样生效，不清理不告警**；用户想换就点「Win+V」或录制新组合（§1） |
| 候选 | 首选 `Super+V`（v1 原设计），由程序生成串，**不经过键盘录制** |
| 为什么不能录 | WPF 键盘事件拿不到 `Super+V`：按下 Win 时 shell 弹开始菜单抢焦点，后续 V 送不到窗口（2026-10-30 用户目标机实测：只在框里看到 Super，V 永不出现）；Win 组合若已被注册则更早被系统吞掉。不装低级键盘钩子（用户已否决）⇒ Win 组合只能由程序生成 |
| 试注册流程 | 点「Win+V」按钮 → 生成 `"Super+V"` → 先判是否与主热键相同（相同则提示"与主热键重复"，不交给系统报 1409）→ `UnregisterHotKey` 旧的 → `RegisterHotKey` 新的（带 `MOD_NOREPEAT`）→ 失败回滚 |
| 失败文案 | `GetLastError()==1409` → 「已被系统或其他程序占用（Win10 1809+ 的系统剪贴板历史会占它）」；其他错误码原样带出 |
| 成功 | 自动勾选启用 + 存盘 + 提示「已启用 Win+V」 |
| 手动录制 | 保留，用于非 Win 组合；录制态按到 `LWin/RWin` 立即提示「Win 组合请点右侧 Win+V 按钮」，不让用户空等 |
| 手动清空 | 输入框右侧提供「清除」把键位置空（= 关闭直呼），不强制留值 |
| 键位范围 | **任何组合键都可设置和使用**，不设白名单。`Alt+B`、`Ctrl+Alt+V`、`Shift+F9` 一律平等对待；唯一约束是与主热键不能相同。能不能真正生效取决于 `RegisterHotKey` 当次是否成功（系统保留组合如 `Win+L` 会失败——这是 Windows 的限制，不是我们的白名单） |

> ⚠️ 待实测（写进验收项）：`RegisterHotKey` 返回成功 ≠ 一定能收到 `WM_HOTKEY`。某些系统上 shell 拦 Win 组合不走 hotkey 机制，会出现"注册成功但按了没反应"。返回值不作结论，必须人肉按一次验证。

## 10. 设置窗口新增「剪贴板」分组

放在「常规」与「右键菜单」之间：

- 直呼热键：输入框（只读 + 空值灰字「未设置」）+ 右侧固定宽度按钮「Win+V」+ 右侧「清除」；
- 启用开关（未设键位时禁用并说明原因）；
- 隐私过滤开关 `v2.clipboardPrivacyFilter`，副标题常驻：「关闭后所有复制内容都会进历史，含密码管理器与隐私窗口内的内容」；再补一句「若某程序复制后总不进历史，可临时关闭本项排查」；
- 所有提示行走**常驻占位**，任何状态都不推挤表单（M2b 验收时定下的规则）。
- **不提供**「清空历史」「删除单条」入口（用户明确不需要清理，历史按 §4 的 100 条上限自动循环淘汰）。

## 11. 配置键与常量汇总

| 键 | 默认 | 说明 |
|---|---|---|
| `clipboardHotkey` | `""` | v1 键名沿用；`""` = 未设置。**读什么就是什么**，不迁移、不改判（§1） |
| `clipboardHotkeyEnabled` | `false` | v1 键名沿用 |
| `v2.clipboardPrivacyFilter` | `true` | 隐私格式过滤 |

100 条 / 1M 字符 / 20MB / 25MP / 96px 显示 / 200 字预览一律**编译期常量**，不进配置（用户已确认）。

## 12. 自验方式

- `--selftest` 新增：
  1. 签名去重：同文本两次 → 一条记录且 `updatedAt` 前移；
  2. 容量淘汰：101 条 → 100 条，最旧被删且其 PNG 文件不存在；
  3. 文本上限：1M+1 字符 → 不记录；
  4. 隐私格式：构造带 `ExcludeClipboardContentFromMonitorProcessing` 的 `DataObject` → 判为跳过；开关关闭 → 放行；
  5. 热键解析：`Super+V` → `MOD_WIN | 'V'`；`Alt+B` 同样可解析；
  6. 预览截断：300 字 → 200 字 + 省略号；
  7. v1 数据兼容：手写一份 v1 格式 `clipboard-history.json`（含 image 记录）→ 加载成功、坏记录跳过、孤儿 PNG 被清理；
  8. **配置值原样读取**：手写 `{clipboardHotkey:"Alt+B"}` → 读出仍是 `Alt+B`；空配置 → 读出 `""`。断言不存在任何改写逻辑。
- 落盘副作用核对（不看 stdout）：`clipboard-history.json` 内容、`images/` 文件增删、配置键写入。
- 冷启动与呼出耗时实测写回本文档基线表。
- UI 截图自验：空态 / 有记录 / 文本过滤命中 / 图片缩略图 / 简洁模式，各一张（用 `--clipboard` 调试开关直开视图，长期保留）。

## 13. 验收清单（用户真机）

1. 复制文本 → 按剪贴板热键 → 列表出现该条；回车 → 窗口收起、焦点回原窗口、Ctrl+V 粘出正确内容。
2. 复制图片 → 列表出现缩略图；回填后粘贴得到原图。
3. 复制文件（资源管理器 Ctrl+C）→ 记录为文件类型，回填后在资源管理器 Ctrl+V 可粘贴。
4. 设置里点「Win+V」→ 若目标机空闲则直接可用（真按一次验证）；若被占则提示 1409 并给出换键指引。
5. 设置成 `Alt+B` → 关掉程序重开 → 仍是 `Alt+B` 且能呼出（配置值不被任何逻辑改写）。
6. 主热键与剪贴板热键互相切换视图，不出现两个窗口、不丢失焦点。
7. 连续复制 120 条 → 只保留 100 条，`images/` 目录无孤儿文件。
8. 关掉隐私过滤开关 → 隐私窗口复制的内容会进历史；再打开 → 不再进。
9. 冷启动后历史仍在；44 图历史呼出 < 100ms。
10. `--selftest` 全过。

### 基线（2026-10-30，开发机 Release 实测）

| 项 | 数值 | 备注 |
|---|---|---|
| selftest | **28 项全 PASS** | 原 18 + M3 新增 10（热键解析/配置默认/原样读取/文本捕获去重/容量淘汰/超长文本/隐私开关/图片落盘缩略图/预览截断/v1 兼容+孤儿清理） |
| exe 体积 | 212 KB | M2b 为 190 KB |
| 剪贴板视图截图 | 4 态通过 | 全量态（7 条混合：文本/图片/文件）、过滤态（SELECT 只剩 1 条、图片正确排除）、设置窗口分组、启动器空态回归 |
| 设置窗口截图 | 剪贴板分组正常 | 「未设置」占位、Win+V/清除按钮、提示行常驻、隐私开关+副标题，布局零推挤 |
| e2e（PowerShell Set-Clipboard 实测） | 全过 | 文本/图片/文件各 +1、PNG 落盘且 sha1 写入 json、重复复制不新增、最新置顶 |
| 视图切换 | 未真机验证 | 主热键 ↔ 剪贴板热键原地切换属验收项 6 |
| 44 图呼出耗时 | 待真机实测 | 目标 <100ms（§5 缩略图懒解码是关键路径） |
