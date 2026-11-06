# PyTools 编码约定

本文件是给人的编码与协作约定；AI 代理的额外操作约束见根目录 `AGENTS.md`；需求受理与上下文续接规则见 `docs/task-workflow.md`；工具面板视觉规范见 `docs/design-system.md`；原生能力与 helper/sidecar 架构判定见 `docs/architecture.md`。与 README 冲突时以 README 为准；发现两份约定不一致时同步修正两处。

## 格式基线

`.editorconfig` 管底座，编辑器自动遵守：

- UTF-8；换行统一 LF；文件末尾一个换行；行尾不留空格（Markdown 除外——硬换行依赖行尾双空格）；`.bat` 例外用 CRLF。
- 缩进一律空格：JS / CSS / HTML / JSON / YAML / Markdown 2 空格。
- 存量文件不批量重排；新代码与顺手改到的文件按本约定写，避免无意义 diff。

## JS 风格

- 单引号、无分号、`===`；优先 `const`，确需重绑定才用 `let`，不用 `var`。
- 模块体系：CommonJS（`require/module.exports`）。不加构建步骤、不引入 ESM/TypeScript。
- 工具面板脚本直接 `<script>` 引入，不用模块系统；异步倾向 `async/await`（与 `tools/_shared/toolkit.js` 一致）。

## 命名

- 文件与目录：kebab-case（`window-manager.js`、`json-format/`）。
- 变量与函数 camelCase；模块级常量 UPPER_SNAKE_CASE。
- 工具 id：ASCII kebab-case，与目录名一致。
- CSS：`theme.css` 提供语义组件类（`.btn`、`.input`、`.status` 等）；工具私有类名加工具前缀（如 `.jf-editor`），且不得重新定义 theme 组件类的样式。

## 工具面板样式

- 保留 `tools/_shared/theme.css` 作为共享样式表：令牌 + 基础组件 + 栅格，到此为止，不做框架（无 JS、无构建、无主题切换引擎）。
- 每个面板必须引 `../_shared/theme.css`；颜色、间距、字号、圆角只用令牌变量；缺令牌先在 theme.css 补，再使用。
- 工具特有布局写 `tools/<id>/style.css`（简单工具可少量内联），但按钮/输入框/状态条等组件外观一律用 theme 类，不手写。
- 理由：工具是高频新增的插件，共享表让任何作者（人或 AI）免费拿到一致外观，全局微调（如 `--accent`）一处生效；各工具独立手写样式必然随数量漂移。

## 工具清单

- `tool.json` 顶层字段 `devOnly:true` 表示仅开发环境加载：`pnpm start` 可见，打包产物运行时跳过；用于示例与演示工具，目录仍随产物存在。

## 版本号

- `package.json` 的 `version` 是唯一版本号来源，托盘与打包目录等其他位置都从这里读取。
- 版本号使用语义化版本：
  - `MAJOR`：破坏兼容，例如部署方式变化、配置不再兼容、工具面板通信协议变化或工具能力删除。
  - `MINOR`：新增用户可见能力或新增工具。
  - `PATCH`：缺陷修复、兼容性修复或不改变用户能力语义的小幅交互修正。
- 文档更新、纯重构和只影响内部组织但运行行为不变的变化不触发版本号修改。
- 一次需求最多 bump 一次；同一个需求内的多次修复不额外 bump。
- 新需求进入方案确认时先确定目标版本号；用户验收通过后，把版本号修改合并进最终提交。
- 打包产物的主程序文件名固定为 `pytools.exe`；版本号体现在打包目录名、托盘展示或文件元数据中，不拼进主程序文件名。

## 内嵌工具

- 只有需要继续使用壳输入框、结果列表和窗口状态机的工具才声明 `embedded:true`；内嵌工具不打开 iframe，也不引入 toolkit postMessage。
- `badgeText` 是输入框左侧的模式标识；工具 id 仍必须与目录一致且使用 ASCII kebab-case。
- 内嵌工具通过主进程 IPC 取得数据，preload 只暴露语义化方法；主进程必须校验顶层 frame，渲染端不得直接访问 Node 能力。

## 面板可选能力

- 框架级窗口能力统一经 `toolkit.ui` 暴露，工具不自造 IPC：`hide`、`toast`、`setPinned`。
- `toolkit.ui.setPinned(true)` 把主窗切为普通应用形态：不随失焦隐藏、移出置顶、出现在任务栏；`false` 恢复默认的「置顶 + 失焦自动隐藏 + 不占任务栏」。
- 钉住只在「同一个工具面板持续打开」期间有效：壳在打开面板（含工具间切换）与关闭面板时都会重置为未钉住，退出工具即恢复默认行为。工具面板只需在打开时把按钮显示重置为未钉住，不必查询状态。
- 能力不限工具，按需在面板上加按钮；当前仅 `ai-ocr` 提供图钉按钮。

## 自定义应用清单

- 绿色软件不靠自动识别，靠用户在资源管理器右键 exe/lnk 显式登记：设置页开关写 `HKCU\Software\Classes\{exefile,lnkfile}\shell\AddToPyTools` 静态动词，默认关闭。
- 登记动作经 `pytools.exe --add-app "<路径>"` 进入主进程（启动 argv 与 second-instance argv 双入口），lnk 经 `shell.readShortcutLink` 解析为真实路径，args 一并留存；反馈走系统通知，不弹窗抢焦点。
- 清单独立存放于应用目录 `custom-apps.json`（不进 config.json），清空/重置/转移直接操作该文件；应用列表合并时自定义条目优先于扫描结果。
- 打开权限语义不变：`isKnownApp` = 扫描根目录下或清单命中；带启动参数的条目以 detached spawn 启动，工作目录为 exe 所在目录。
- PyTools 挪目录后注册表 command 失效：启动时若开关为开，幂等重写（自愈）。

## 原生 helper

- 涉及原生能力、helper 或后台进程的需求，先读 `docs/architecture.md` 并按其判定结论选择实现形态；判定过程写入当前任务文档。
- helper 源码放 `native/<id>/`，Makefile 与 `build:<id>` script 同步建立；产物输出 `src/main/` 且不入库。
- helper 只由主进程以数组参数 spawn，子命令白名单化；缺失或失败必须静默降级，不影响启动器。

## 交互与安全底线

- 面板与壳只经 `tools/_shared/toolkit.js` 通信，不自造 postMessage 协议。
- `shell.run` 参数永远数组化；外部输入拼命令前必须过引号感知拆分，禁止字符串拼 shell。
- 主进程新增 IPC 一律经 `tool-ipc.js` 统一注册并校验 sender。
- 壳输入框处理中文 IME 的 `isComposing`（input 与 keydown 都要）。

## Git 与节奏

- master 单分支直推；提交信息首行中文单行，采用 `类型：对象/范围，关键行为或目标形态`。类型只用 `新增`、`修改`、`修复`、`重构`、`移除`、`同步`、`记录`；不把 `收尾`、`更新`、`调整`、`优化` 作为唯一语义。允许可选正文补充跨模块影响和验证结论。示例：`新增：结果操作菜单，应用和 vsc 项目支持 Tab 打开目录/复制/终端/scripts 动作`。
- 一个提交做一件事；`dist/`、`node_modules/` 不入库。
- 按 README 的分步节奏：完成一步 → 自验 → 用户确认 → 提交 → 下一步。
- 新需求先写 `docs/active-task.md`；需求确认与开始编码是两个独立阶段，用户明确确认开始编码前不改源码。缺陷反馈直接修订当前任务的设计和实现步骤后继续修复。实现过程中持续更新任务状态，完成后回填 `docs/task-log.md`。
- 不碰用户未提交的在制品；发现别人的改动先弄清再共存。

## 文档

- 项目目标见 `README.md`；阶段范围与验收见 `docs/project-plan.md`。
- 跨需求架构判定（原生能力、helper/sidecar、子进程边界）见 `docs/architecture.md`。
- 版本号升级规则见上文「版本号」。
- 当前需求与进度见 `docs/active-task.md`；历史需求见 `docs/task-log.md`；工作流见 `docs/task-workflow.md`。
- 文档全中文。新专项规范放 `docs/<主题>.md`；README 只留总纲，细节下沉到 docs/。
