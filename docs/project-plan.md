# PyTools 阶段计划与验收

本文件是项目推进的单一事实来源。每完成一个阶段，先执行该阶段验证，再把状态与验收记录更新回本文件；未通过时不进入下一阶段。

## 推进规则

1. 一个阶段只做该阶段列出的范围；发现新需求先记录，不随手扩大改动。
2. 每个阶段必须同时给出命令验证和人工验收；只跑命令不算完成。
3. 用户确认通过后，才更新阶段状态为“已完成”。
4. 阶段状态只允许：`未开始`、`进行中`、`已实现待验收`、`已完成`、`已阻塞`。

## 阶段总览

| 阶段 | 范围 | 状态 | 结果 |
|---|---|---|---|
| 0. 项目基线 | 目标、技术栈、编码与安全约定 | 已完成 | 2026-09-04 用户确认通过 |
| 1. Electron 壳 | 托盘、热键、搜索窗、失焦隐藏、单实例 | 已完成 | 2026-09-04 用户 GUI 验收通过 |
| 2. 插件系统 | 工具扫描、iframe 桥、toolkit API、热加载 | 已完成 | 2026-09-04 用户 GUI 验收通过 |
| 3. 工具与主题 | `hello`、`json-format`、`ui-demo`、共享主题 | 已完成 | 2026-09-04 用户 GUI 验收通过 |
| 4. 启动器体验 | 空输入无列表、应用搜索、工具命令动作、动态高度 | 已实现待验收 | 2026-09-04 冒烟启动通过，待人工验收 |
| 4A. 环境版本切换工具 | Node 版本扫描、当前用户 Path 切换、专用 CMD | 已实现待验收 | 2026-09-10 已完成静态检查，待人工验收 |
| 5. 打包部署 | electron-packager 产物、离线拷贝、目标机安装位置 | 未开始 | 未记录 |
| 6. 目标机验收 | 自启、热键、插件增删、异常兜底 | 未开始 | 未记录 |

## 阶段 0：项目基线

**范围**

- 明确项目目标、技术栈和不可破坏的安全约束。
- 移除 Python 配置与脚本；项目不依赖 Python。
- 固化 `.editorconfig`、`.gitattributes`、`AGENTS.md` 与 `docs/conventions.md`。

**命令验证**

```powershell
Get-ChildItem -Path src,tools -Recurse -Filter *.js | ForEach-Object { node --check $_.FullName }
node -e "const fs=require('fs');for(const f of ['package.json','tools/hello/tool.json','tools/json-format/tool.json','tools/ui-demo/tool.json'])JSON.parse(fs.readFileSync(f,'utf8'))"
rg --files -g "pyproject.toml" -g "scripts/*.py"
git diff --check
```

预期：JS 语法检查和 JSON 解析无报错；`rg` 没有输出；`git diff --check` 没有空白错误。

**人工验收**

1. `package.json` 的 `dependencies` 不存在或为空，`devDependencies` 只有 `electron` 与 `electron-packager`。
2. 项目根目录没有 `pyproject.toml`，`scripts/` 下没有 Python 脚本。
3. 三个工具面板都引用共享主题与 toolkit。

**通过标准**

命令验证全部通过，且人工检查没有发现 Python 配置、构建脚本或运行要求。

## 阶段 1：Electron 壳

**范围**

- 单实例锁；第二个实例只唤起第一个实例。
- 托盘常驻：显示/隐藏、重新扫描、自启开关、退出。
- 默认全局热键 `Ctrl+Shift+Space`，注册失败时弹系统通知。
- 无边框置顶搜索窗，不显示在任务栏；失焦 150ms 后自动隐藏。
- 中文 IME：组合输入期间不触发搜索过滤，也不执行 Enter。

**命令验证**

```powershell
pnpm exec electron --version
pnpm start -- --debug
```

`--debug` 会打开 DevTools 并关闭失焦自动隐藏，适合排查窗口行为；最终还要用 `pnpm start` 做一次非调试验收。

**人工验收**

1. 启动后窗口不自动弹出，托盘出现 PyTools 图标。
2. 在记事本或其他窗口中按 `Ctrl+Shift+Space`，搜索窗呼出并置顶。
3. 输入中文时，拼音组合期间列表不闪烁，按 Enter 不会在组合中途误执行。
4. 点击窗口外部，窗口自动隐藏；托盘“显示/隐藏”能唤回。
5. 再启动一个实例，不会出现两个托盘进程，只会唤起已有窗口。
6. 修改 exe 同目录 `config.json` 的 `hotkey` 后重启，新热键生效；原热键失效。

**通过标准**

上述操作全部符合预期；注册冲突时至少出现系统通知，不能静默失败。

## 阶段 2：插件系统

**范围**

- `tools/<id>/tool.json` 声明工具；重复 id 跳过并输出日志。
- 工具面板通过 iframe 加载；壳用 `event.source` 校验消息来源。
- 主进程只接受顶层 frame 的 toolkit 调用；子 frame 不能直连 IPC。
- `shell.run` 只接受数组参数，禁止字符串拼 shell。

**命令验证**

```powershell
node -e "const fs=require('fs');for(const f of ['tools/hello/tool.json','tools/json-format/tool.json','tools/ui-demo/tool.json'])JSON.parse(fs.readFileSync(f,'utf8'))"
pnpm start -- --debug
```

**人工验收**

1. 搜索 `hello`，回车打开 Hello 面板。
2. 点击“运行 echo hi”，输出对象中包含 `stdout`，且内容有 `hi`。
3. 在 Hello 面板按 Esc，先回到结果列表；再按 Esc，才隐藏窗口。
4. 搜索 `json`，打开 JSON 格式化工具；粘贴合法 JSON 后能格式化，粘贴非法 JSON 时状态条显示错误且原文不变。
5. 托盘点“重新扫描工具”，结果列表刷新；新增一个临时 `tool.json` 工具后重扫能出现，删除后重扫消失。

**通过标准**

三个现有工具都能从搜索入口打开；toolkit 调用有结果；Esc 状态机正确；热加载不需要重启应用。

## 阶段 3：工具与主题

**范围**

- `tools/_shared/theme.css` 提供令牌、基础组件与栅格。
- `ui-demo` 展示并验收共享主题。
- `hello` 与 `json-format` 移除重复组件样式，只保留工具私有布局。

**命令验证**

```powershell
rg -n "\.btn|\.input|\.toolbar|\.status|\.radio-group" tools/json-format/style.css
pnpm start -- --debug
```

`rg` 不应输出；如果输出，说明 JSON 工具仍重复定义了主题组件。

**人工验收**

1. 搜索 `ui`，打开“UI 组件演示”。
2. 对照 `docs/design-system.md` 检查栅格、按钮、输入、单选组、下拉、标签、状态条、卡片与列表。
3. 在 DevTools 中临时修改 `:root` 的 `--accent`，按钮、active 列表和焦点描边同步变化；不要把临时修改写入文件。
4. 检查 `hello` 与 `json-format`：按钮高度一致，输入框焦点不改变布局，文本不溢出或遮挡。
5. JSON 工具六组操作回归：格式化剪贴板、格式化输入、压缩、复制、撤销、错误输入保护。

**通过标准**

主题组件只维护在 `theme.css`；现有工具视觉一致；`ui-demo` 与设计规范一致。

## 阶段 4：启动器体验

**范围**

- 空输入只保留搜索框；输入后混合展示工具、工具命令和本机应用。
- 扫描当前用户与系统“开始菜单/程序”下的 `.lnk` 快捷方式；只允许打开白名单内的快捷方式。
- 应用项展示系统图标；从 `.lnk` 目标提取英文文件名，支持中文名应用的英文匹配。
- `config.json` 的 `appDirs` 可指定绿色软件目录；只扫描其中的 `.lnk` 与 `.exe`。
- `tool.json` 支持 `commands`；命令可带别名与参数，选中后由对应工具执行。
- `json-format` 支持 `fc`：读取剪贴板、格式化、写回，并隐藏主窗口。
- 搜索壳结果数量驱动窗口高度，避免少量结果时出现大块空白。
- 搜索区是一个整体圆角卡片；空输入时四边留白一致，出现列表后圆角只在卡片底部。

**命令验证**

```powershell
Get-ChildItem -Path src,tools -Recurse -Filter *.js | ForEach-Object { node --check $_.FullName }
node -e "const fs=require('fs');for(const f of ['tools/hello/tool.json','tools/json-format/tool.json','tools/ui-demo/tool.json'])JSON.parse(fs.readFileSync(f,'utf8'))"
pnpm start -- --debug
```

**人工验收**

1. 刚呼出且输入为空时，没有结果列表；窗口高度紧凑。
2. 输入 `fc`，出现「格式化剪贴板」命令项。
3. 输入 `notepad`、`cmd` 或其他已安装应用名，出现“应用”候选。
4. 选中应用并回车，应用打开；主窗口消失，再次呼出时输入框为空。
5. 复制一段合法 JSON 后输入 `fc`，回车后剪贴板变成格式化 JSON，主窗口自动消失。
6. 输入 `fc 0`、`fc 2`、`fc 4`、`fc tab`，参数生效。
7. 剪贴板内容不是 JSON 时，原文不写回，可用 Esc 回到结果列表。
8. 应用候选显示真实应用图标；读取失败时显示首字母兜底。
9. 对一个中文名应用，用其目标 exe 名称或英文目录名搜索能命中。
10. 在 `config.json` 的 `appDirs` 加入一个绿色软件目录，托盘重扫后目录内 `.exe` 或 `.lnk` 能搜索并启动。
11. 空输入时搜索卡四边留白一致；出现列表后整体卡片只在底部有圆角。
12. 托盘“重新扫描工具”后，新增或删除的应用快捷方式与工具命令能更新。

**通过标准**

空输入无列表、应用可直接启动、命令动作可执行并按需隐藏主窗；非法输入不会破坏剪贴板或原数据。应用发现只使用开始菜单和 `appDirs` 白名单，不做全盘扫描。

## 阶段 5：打包部署

**范围**

- 增加 `pack` 脚本，使用 electron-packager 生成 Windows x64 目录。
- 固定 `--no-asar --no-prune --ignore=node_modules`，保留工具目录可编辑。
- 交付包排除开发机运行期数据与凭据（`clipboard-history.json`、`clipboard-history/`、`custom-apps.json`、`ai-local.json`）与打包脚本自身；忽略规则用正则锚定到应用根，避免误伤 `tools/clipboard-history` 与 `src/main/clipboard-history.js`。`ai-local.json` 含明文密钥，架构约定为部署时手工放置（见 `architecture.md`），绝不可随包分发。
- 打包后自校验必需项与禁止项，任一项不符即非零退出。
- 产物部署到目标机 `C:\Apps\pytools`。

**命令验证**

```powershell
pnpm run pack
Get-ChildItem -Recurse -File dist\pytools-win32-x64 | Measure-Object
dist\pytools-win32-x64\pytools.exe --version
```

`--version` 只用于确认 exe 能启动；若该 Electron 版本不输出版本号但应用正常启动，也不视为失败。

**人工验收**

1. 确认 `dist/pytools-win32-x64/resources/app/tools/` 中存在 `_shared`、`hello`、`json-format`、`ui-demo`；`hello` 与 `ui-demo` 为 `devOnly`，运行时不显示，属预期。
2. 记录打包目录文件数；拷贝到目标机后再次统计，数量一致。
3. 从 `C:\Apps\pytools\pytools.exe` 启动，托盘、热键和三个工具可用。
4. 在目标机 `resources/app/tools/` 新增一个临时工具文件夹，托盘重扫后能出现；删除后重扫消失。

**通过标准**

打包产物不依赖 `node_modules`，目标机无需 Node 或 Python；部署后热加载仍可用。

## 阶段 6：目标机验收

**范围**

- 开机自启、重启驻留、离线运行、异常兜底。
- 目标机是内网脱机 Windows 10 1709 x64。

**命令验证**

目标机没有开发环境，固定使用人工验收；构建机只在打包时执行命令。

**人工验收**

1. 固定部署路径 `C:\Apps\pytools`，路径不含空格。
2. 启动后托盘开启“开机自启”，重启系统后 PyTools 自动驻留托盘。
3. 重启后热键、搜索、三个工具全部可用。
4. 拔掉网络或保持内网无外网状态，功能不受影响。
5. 若黑屏、花屏或窗口不刷新，用 `C:\Apps\pytools\pytools.exe --disable-gpu` 重试，并记录结果。

**通过标准**

目标机冷启动、重启后均可使用；异常兜底方案有明确记录。

## 浏览器与截图验收

Codex 内置浏览器受 URL 策略限制，不能打开 `file:///C:/.../panel.html`。你这边可以做三件事：

1. **首选真实应用**：运行 `pnpm start`，从搜索窗打开 `ui-demo`、`hello`、`json-format` 验收。这才是最终运行形态。
2. **粗看静态布局**：在系统浏览器里直接打开 `panel.html` 可以检查大体排版，但 toolkit 按钮和窗口高度同步不会工作，不能作为通过依据。
3. **交给 Codex 复核**：把截图发到当前任务里，我按设计规范检查视觉问题；如果需要我启动 GUI，再明确授权。

不要为了绕过浏览器限制改变安全模型或新增本地 HTTP 服务；本项目当前没有 Web 服务器，也不需要为验收引入一个。
