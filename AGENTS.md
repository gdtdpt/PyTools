# PyTools v2 协作约定（AGENTS）

AI 代理在 `v2/` 下动手前必须读完本文件，同时读根目录 `AGENTS.md`（其硬性不变量**仅适用 v1/Electron 代码**）与 `docs/v2-plan.md`、`docs/v2-rewrite-matrix.md`。

## 项目一句话

PyTools v2：v1（Electron 启动器）的 .NET Framework 4.6.2 / WPF 重写版，单 exe 零部署，目标机为内网脱机 Win10 1709 x64。v1 冻结于 tag `v1-final`，仅修致命缺陷。

## 硬性不变量（违反即返工）

1. **.NET Framework 4.6.2 锁定**，传统格式 csproj（非 SDK 风格），目标机无任何运行时安装。
2. **零 NuGet / 零第三方依赖**：引用仅 BCL（WPF 三件套 + System.Windows.Forms + System.Web.Extensions）。
3. **单 exe 拷贝即用**；asInvoker 免管理员；数据文件（config.json / custom-apps.json / clipboard-history）与 exe 同目录。
4. **含中文的源文件必须 UTF-8 with BOM**（.cs / .xaml 一律），否则 csc 按 ANSI 解码、中文字符串全乱。
5. **config.json 键名与 v1 兼容**：沿用 v1 键名；v2 新增键加 `v2.` 前缀；写回保留未知键（`Core/AppConfig.cs` 负责，不许绕过它直写配置文件）。
6. **样式只用令牌**：颜色/字号/间距/圆角/阴影/动效全部走 `Themes/Theme.xaml`；**简洁模式（Theme.Lite.xaml）只允许覆盖「窗口底视觉/阴影/动效」三类令牌**，其余令牌两形态强制同值——保证风格统一的机制，不许在控件里写死颜色绕过。
7. **前台恢复必须走 `Native/Foreground.cs`**（采样验证 + 升档重试 + 落日志），不许裸调 SetForegroundWindow。
8. **关键路径强制打日志**（热键注册、呼出/收起耗时、恢复尝试），日志初始化失败必须静默降级、绝不崩溃。
9. PowerShell 交付脚本 UTF-8 with BOM + `[Console]::OutputEncoding = UTF8`；param 不给依赖 `$PSScriptRoot` 的默认值。
10. 版本号纪律沿用全局约定：`2.0.0-devN` 为中间包，正式 `2.0.0` 只留给真机验证通过的那次构建。

## 目录职责

| 路径 | 职责 |
|---|---|
| `src/PyTools/Native/` | Win32 P/Invoke（热键、前台、控制台）；纯静态，无 WPF 依赖 |
| `src/PyTools/Core/` | 配置、日志、单实例、自测 |
| `src/PyTools/Themes/` | 令牌层（基础 + 简洁覆盖） |
| `src/PyTools/Views/` | 共用视图（被多种窗壳复用） |
| `src/PyTools/Windows/` | 窗壳（透明/不透明），不含业务逻辑 |
| `src/PyTools/Tray/` | 托盘与气泡 |
| `tools/build.ps1` | 构建（vswhere 定位 MSBuild） |

## 构建与自测

```powershell
.\v2\tools\build.ps1 -Configuration Debug        # 构建
.\v2\src\PyTools\bin\Debug\PyTools.exe --selftest # 逻辑自测（exit 0 = 全过）
.\v2\src\PyTools\bin\Debug\PyTools.exe --debug    # 调试：启动即显示、禁失焦隐藏
```

同机测试 v2 前必须退出 v1（全局热键 / 单实例 / 注册表右键三处冲突）。
