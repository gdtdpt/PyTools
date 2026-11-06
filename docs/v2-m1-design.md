# v2-M1 壳骨架设计

状态：**待方案确认**——确认前不动源码。
日期：2026-10-30
依据：`docs/v2-plan.md`（技术栈定稿）、`docs/v2-rewrite-matrix.md`（S1-S12 条目）。

## 范围（对照矩阵）

本期交付：S1 热键呼出/收起、S2 失焦隐藏、S3 置顶浮层+位置记忆、S4 托盘、S5 单实例、S6 Esc、S8 前台 capture/restore、S11 调试模式+落盘日志、S12 令牌层起步。
不含：S7 图钉（M5）、S9/S10/L*（后续里程碑）。呼出窗本期为**空壳**（搜索框 + 空列表占位），列表与启动器进 M2。

## 1. 项目骨架

```
v2/
├── src/
│   ├── PyTools.sln
│   └── PyTools/
│       ├── PyTools.csproj      # 传统格式，.NET Framework 4.6.2，WinExe，x64
│       ├── app.manifest        # asInvoker + PerMonitorV2 DPI 声明
│       ├── App.xaml / App.xaml.cs
│       ├── Windows/SummonWindow.xaml(.cs)   # 呼出窗
│       ├── Native/             # Win32 P/Invoke（热键、前台、DWM）
│       ├── Core/               # 配置、单实例、日志
│       └── Themes/Theme.xaml   # 令牌层（S12 起步）
└── tools/build.ps1             # vswhere 定位 MSBuild → Release 单 exe；支持 --selftest
```

- 引用仅 BCL：PresentationFramework + System.Windows.Forms（托盘 NotifyIcon 用，BCL 合法）。
- 版本从 `2.0.0-dev1` 起步，正式 `2.0.0` 留给真机验证通过（沿用版本号纪律）。

## 2. 呼出窗形态：双形态令牌化（2026-10-30 用户提议「简洁模式」，定案）

不再二选一——**默认形态与简洁形态共存，令牌层一键切换**：

| | 默认形态 | 简洁形态（`v2.liteMode: true`） |
|---|---|---|
| 窗口 | AllowsTransparency，真圆角 + 半透明阴影 | 不透明 + WindowChrome/DWM 阴影，方角（Win10 不透明窗无法真圆角，Region 锯齿方案不采用） |
| 动效 | 呼出/收起 120-150ms ease-out | 去动画或 ≤60ms 直切 |
| 内部控件（输入框/列表项/按钮） | 圆角、强调色 | **完全一致**（圆角保留在控件上，不在窗体上） |
| 布局/间距/字号 | 完全一致 | 完全一致 |

- **实现**：`Themes/Theme.xaml`（基础令牌）+ `Themes/Theme.Lite.xaml`（覆盖令牌：窗口底色、阴影、动效三类退化）；启动时按 `v2.liteMode` 合并字典。切换后重启生效（M2 设置页做好后可改热切）。
- **风格统一原则**：只有「窗口底、阴影、动效」三类令牌允许退化，其余令牌两形态强制同值——这就是「退化成纯色但风格保持统一」的机制保证。
- **基线护栏仍在**：M1 对**两种形态各测一组**基线（呼出耗时、滚动帧率），数据写回本文档；目标机部署文档建议开启简洁模式。

## 3. 配置兼容

- 位置：**exe 旁 config.json**（开发态即 `v2/bin/...`，与部署形态一致，不做 v1 的双模式）。
- 键名全沿用（`hotkey`、`autoStart`、`clipboardHotkey*`、`appDirs`、`addAppContextMenu`、`windowBounds`）；v2 新增键加 `v2.` 前缀；v1 读到未知键会忽略，**同一个 config.json 两版都能用**。
- 默认值沿用：主热键 `Ctrl+Shift+Space`、剪贴板 `Alt+B`（默认关）。

## 4. 全局热键（S1）

- `RegisterHotKey` + `HwndSource.AddHook` 收 `WM_HOTKEY`；配置串解析（"Ctrl+Shift+Space" → MOD_ALT 等）。
- 注册失败（与 v1 冲突/被占）→ 日志 + 托盘气泡提示，不静默。
- 同机测试 v2 必须退出 v1（热键/单实例/注册表三处冲突，已入记忆）。

## 5. 前台 capture/restore（S8，v1 痛点的根治点）

- **呼出时序**：`GetForegroundWindow()` 记录 hwnd（进程内同步调用，零开销）→ `Show + Activate`。
- **恢复时序**（收起后）：主路径 `AttachThreadInput(当前, 目标线程, true)` → `SetForegroundWindow(target)` → detach；**采样验证**：100ms × 5 次读 `GetForegroundWindow()`，不符则重试，最多 3 次，逐级升档（重试 → ALT 键辅助 → 放弃 + 日志记录失败原因）。
- 与 v1 helper 行为等价，但进程内完成——无 spawn、无 1s 超时静默放弃，每次尝试与结果都落日志。

## 6. 窗口行为（S2/S3/S6）

- 失焦隐藏：`Deactivated` → `Hide()`（`--debug` 禁用，给 M1 自验留检查窗口）。
- Esc → `Hide()`；呼出热键再按 → `Hide()`（toggle）。
- 位置记忆：沿用 `windowBounds`（x/y/w/h），收起时落盘。
- 隐藏逻辑留 hook，M5 图钉（钉住时不响应 Deactivated）直接挂上。

## 7. 托盘（S4）

- WinForms `NotifyIcon`：菜单 = 打开、重新扫描（M2 占位）、退出；`BalloonTip*` 气泡能力本期就位（M2 静默添加反馈直接接）。

## 8. 单实例（S5）

- `Local\PyTools.V2.SingleInstance` 命名 Mutex；第二实例 `EventWaitHandle` 通知后退出；首实例收到信号 → 呼出窗 show（与 v1 二次启动唤起语义一致）。

## 9. 日志（S11，v1 教训的根治点）

- exe 旁 `logs\pytools-YYYYMMDD.log`，INFO 级；`--debug` 升 DEBUG + 耗时打点；保留 7 天自动清理。
- 强制打点：热键注册成败、呼出/收起耗时、capture 记录值、恢复每级尝试与结果、单实例信号。

## 10. 令牌层起步（S12）

- `Themes/Theme.xaml`：颜色/字号/间距/圆角/阴影/动效曲线六类令牌，映射 v1 `design-system.md` 的语义；空壳窗先只落底色/边框/选中三类，后续里程碑按需增补。

## 11. 自验方式

- `--selftest`：config 解析、热键串解析、capture/restore 干跑（无窗口）、Mutex 双启检测。
- UI 截图自验：`win-ui-selfscreenshot` 流程（PrintWindow 对 WPF 同样有效）。
- 基线实测：冷启动（双击 exe 到托盘就绪）、呼出耗时、滚动占位帧率——数据写回本文档。

## 验收清单（用户真机）

1. 退出 v1 → 运行 v2 exe → 托盘图标出现，`Ctrl+Shift+Space` 呼出空壳窗
2. 再按热键 / Esc / 点击别处 → 三种方式都能收起
3. 呼出前焦点在记事本 → 呼出 → Esc → 光标回到记事本可继续输入
4. 重启后窗口位置不变
5. 双击 exe 二次启动 → 不起新进程，原实例窗口呼出
6. `--debug` 启动 → 失焦不隐藏；`logs\` 有当日日志且含呼出耗时
7. 恢复 v1 使用（启动 v1）一切正常——两版并存验证
