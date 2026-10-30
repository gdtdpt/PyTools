using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Threading;
using PyTools.Core;
using PyTools.Native;
using PyTools.Tray;
using PyTools.Views;
using PyTools.Windows;

namespace PyTools
{
    public partial class App : Application
    {
        private const int HotkeyIdMain = 0x0001;
        private const int HotkeyIdClipboard = 0x0002; // M3 接管剪贴板直呼

        private static readonly IntPtr HwndMessage = new IntPtr(-3);

        private AppConfig _config;
        private SingleInstance _single;
        private TrayController _tray;
        private SummonWindowBase _window;
        private SummonView _view;
        private ClipboardView _clipView;
        private ClipboardHistory _clipHistory;
        private ClipboardWatcher _clipWatcher;
        private bool _clipboardRegistered;
        private bool _hiding; // 收起动画进行中，防重复触发
        private ClipRecord _pendingWriteBack; // 收起并恢复焦点后才写回剪贴板
        private AppIndex _index;
        private bool _bubbleNextScan; // 手动重扫后气泡反馈（首扫不气泡）
        private string _debugQuery;   // --debug 配套 --query=<词>：呼出即预填搜索（UI 截图自验用）
        private string _bootAddApp;   // 冷启动 --add-app=<路径>：静默驻留，不呼出窗口
        private IntPtr _hotkeyHwnd = IntPtr.Zero;
        private HwndSource _hotkeySource;
        private bool _capturedForeground; // 呼出前已记录前台
        private IntPtr _capturedTarget = IntPtr.Zero;
        private SynchronizationContext _uiContext;

        public static string AppDirectory
        {
            get
            {
                // exe 旁（部署形态与开发形态一致），v1 的双模式不再保留
                return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var args = new System.Collections.Generic.List<string>(e.Args);
            var debug = args.Contains("--debug");
            foreach (var arg in args)
            {
                if (arg.StartsWith("--query=", StringComparison.Ordinal))
                {
                    _debugQuery = arg.Substring("--query=".Length);
                }
            }
            _bootAddApp = ExtractAddAppArg(args); // 空串 = 带参数但值无效；null = 未带参数

            if (args.Contains("--selftest"))
            {
                Environment.Exit(SelfTest.Run());
            }

            _single = new SingleInstance();
            if (!_single.TryAcquire())
            {
                Logger.Info("second instance -> signal existing");
                if (_bootAddApp != null) SingleInstance.SignalAddApp(_bootAddApp);
                else SingleInstance.SignalExisting();
                Shutdown();
                return;
            }

            Logger.Init(AppDirectory, debug);

            try
            {
                _config = AppConfig.Load(Path.Combine(AppDirectory, "config.json"));
                ApplyTheme(_config.LiteMode);
                _uiContext = SynchronizationContext.Current;

                // 启动器索引（M2a）：后台扫描开始，完成后结果上线
                _index = new AppIndex(AppDirectory, _config);
                _index.AppsUpdated += OnAppsUpdated;
                _index.ScanFinished += OnScanFinished;
                _index.RescanAsync();

                _tray = new TrayController();
                _tray.Init();
                _tray.OpenRequested += ToggleSummon;
                _tray.RescanRequested += delegate
                {
                    _bubbleNextScan = true;
                    _index.RescanAsync();
                };
                _tray.ExitRequested += ExitApplication;
                _tray.SettingsRequested += OpenSettings;

                _window = _config.LiteMode ? (SummonWindowBase)new SummonOpaqueWindow() : new SummonWindow();
                _window.Width = _config.BoundsWidth; // 高度交给 SizeToContent（空态仅搜索框）
                _window.EscapePressed += HideSummon;
                _view = (SummonView)_window.Content;
                _view.Index = _index;
                _view.PageStep = _config.PageStep;
                _view.LaunchRequested += OnLaunchRequested;
                _view.ActionRequested += OnActionRequested;

                // 剪贴板历史（M3）：历史数据 + 监听 + 直呼视图。视图与启动器共用同一呼出壳（切 Content），
                // 焦点恢复/动效/位置记忆全部复用，不新建窗口。
                _clipHistory = new ClipboardHistory(AppDirectory) { PrivacyFilter = _config.ClipboardPrivacyFilter };
                _clipView = new ClipboardView { History = _clipHistory, PageStep = _config.PageStep };
                // 壳的 Margin 是阴影呼吸区（DropShadowEffect 画在 Border 之外，不留边距会被窗口边界裁切），
                // 切 Content 时必须沿用同一份边距，否则剪贴板视图的圆角处会出现尖刺伪影。
                if (_view != null) _clipView.Margin = _view.Margin;
                _clipView.SelectRequested += OnClipboardSelectRequested;
                _clipWatcher = new ClipboardWatcher(OnClipboardUpdate);
                if (!_clipWatcher.Start()) Logger.Error("clipboard watcher failed to start");

                _window.Deactivated += (sender, eventArgs) =>
                {
                    // M5 图钉在此挂钩：钉住时不响应失焦
                    if (!debug) HideSummon();
                };
                _window.EscapePressed += delegate
                {
                    // 操作模式下 Esc 先退回结果列表（v1 语义），再藏窗
                    if (_view.TryExitActionMode()) return;
                    HideSummon();
                };

                if (!RegisterHotkey(HotkeyIdMain, _config.Hotkey))
                {
                    _tray.ShowBubble("PyTools", "主热键注册失败：" + _config.Hotkey + "，可能已被占用");
                }
                if (_config.ClipboardHotkeyEnabled && !string.IsNullOrEmpty(_config.ClipboardHotkey))
                {
                    int errorCode;
                    if (RegisterHotkey(HotkeyIdClipboard, _config.ClipboardHotkey, out errorCode))
                    {
                        _clipboardRegistered = true;
                    }
                    else
                    {
                        _tray.ShowBubble("PyTools", "剪贴板热键注册失败：" + DescribeHotkeyFailure(_config.ClipboardHotkey, errorCode));
                    }
                }

                _single.ListenShow(_uiContext, ToggleSummon);
                _single.ListenAddApp(_uiContext, HandleAddAppRequest);

                // 右键菜单自愈：开关开启但注册表与期望不符（挪目录/旧格式）时幂等重写（v1 repair 语义）
                if (_config.AddAppContextMenu)
                {
                    var repairError = ContextMenuRegistry.Repair();
                    if (repairError != null) Logger.Error("context-menu repair failed: " + repairError);
                }

                Logger.Info("started, liteMode=" + _config.LiteMode + ", debug=" + debug);

                if (debug)
                {
                    // 调试模式：启动即显示且禁用失焦隐藏（与 v1 --debug 语义一致）
                    if (args.Contains("--clipboard")) ShowClipboard();
                    else ShowSummon();
                    if (_debugQuery != null)
                    {
                        // --query 配套当前视图：截图自验用
                        if (args.Contains("--clipboard")) _clipView.SearchBox.Text = _debugQuery;
                        else _view.SearchBox.Text = _debugQuery;
                    }
                }
                else if (_bootAddApp != null)
                {
                    // 右键「添加到 PyTools」冷启动：静默登记，不呼出窗口（v1 语义）
                    HandleAddAppRequest(_bootAddApp);
                }
                else if (args.Contains("--settings"))
                {
                    OpenSettings(); // 调试/自验开关：启动即开设置窗口
                }
            }
            catch (Exception ex)
            {
                Logger.Error("startup failed: " + ex);
                throw;
            }
        }

        private void ApplyTheme(bool liteMode)
        {
            var baseUri = new Uri("pack://application:,,,/Themes/Theme.xaml");
            Resources.MergedDictionaries.Clear();
            Resources.MergedDictionaries.Add(new ResourceDictionary { Source = baseUri });
            if (liteMode)
            {
                Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/Themes/Theme.Lite.xaml")
                });
            }
        }

        // ---- 热键 ----

        private bool RegisterHotkey(int id, string text)
        {
            int errorCode;
            return RegisterHotkey(id, text, out errorCode);
        }

        private bool RegisterHotkey(int id, string text, out int errorCode)
        {
            errorCode = 0;
            uint mods, vk; string err;
            if (!HotkeyParser.TryParse(text, out mods, out vk, out err))
            {
                Logger.Error("hotkey parse failed: " + text + " -> " + err);
                errorCode = -1;
                return false;
            }
            EnsureHotkeyWindow();
            // MOD_NOREPEAT：长按不连发（主热键与剪贴板热键统一带）
            var ok = NativeMethods.RegisterHotKey(_hotkeyHwnd, id, mods | NativeMethods.ModNoRepeat, vk);
            if (!ok) errorCode = Marshal.GetLastWin32Error();
            Logger.Info("register hotkey id=" + id + " \"" + text + "\" -> " + ok + (ok ? "" : " err=" + errorCode));
            return ok;
        }

        private void EnsureHotkeyWindow()
        {
            if (_hotkeySource != null) return;
            var parameters = new HwndSourceParameters("PyTools.Hotkey")
            {
                ParentWindow = HwndMessage, // message-only 窗口，不进任务栏不抢焦点
                HwndSourceHook = WndProc
            };
            _hotkeySource = new HwndSource(parameters);
            _hotkeyHwnd = _hotkeySource.Handle;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WmHotkey)
            {
                var id = wParam.ToInt32();
                if (id == HotkeyIdMain) Dispatcher.Invoke(ToggleSummon);
                else if (id == HotkeyIdClipboard) Dispatcher.Invoke(ToggleClipboard);
                handled = true;
            }
            return IntPtr.Zero;
        }

        // ---- 呼出 / 收起 ----

        /// <summary>主热键：显示应用列表；已显示剪贴板视图时原地切回应用列表。</summary>
        private void ToggleSummon()
        {
            if (_window == null) return;
            if (_window.IsVisible && !_hiding)
            {
                if (ReferenceEquals(_window.Content, _view)) HideSummon();
                else ShowSummon();
                return;
            }
            ShowSummon();
        }

        /// <summary>剪贴板热键：显示剪贴板视图；已显示应用列表时原地切换。</summary>
        private void ToggleClipboard()
        {
            if (_window == null) return;
            if (_window.IsVisible && !_hiding)
            {
                if (ReferenceEquals(_window.Content, _clipView)) HideSummon();
                else ShowClipboard();
                return;
            }
            ShowClipboard();
        }

        private void ShowSummon()
        {
            ShowShell(_view, _view.ResetForSummon, "apps");
        }

        private void ShowClipboard()
        {
            ShowShell(_clipView, _clipView.ResetForClipboard, "clipboard");
        }

        /// <summary>
        /// 呼出壳统一入口：切换内容视图 → （首次显示时）记录前台、定位、显示、淡入。
        /// 两个视图共用同一份 windowBounds，所以切换视图时位置不变，只变高度（顶边锚定）。
        /// </summary>
        private void ShowShell(object content, Action reset, string tag)
        {
            if (_window == null || _hiding) return;
            var sw = Stopwatch.StartNew();
            var alreadyVisible = _window.IsVisible;

            if (!alreadyVisible)
            {
                // 呼出前记录前台（进程内同步调用，零开销）——焦点恢复的依据
                // 已在显示中切视图时**不能**重抓：此刻前台就是自己，重抓会导致收起时无处可恢复
                _capturedTarget = Foreground.Capture();
                _capturedForeground = true;
                Logger.Debug("captured foreground=0x" + _capturedTarget.ToString("X"));
                _hiding = false;
            }

            _window.Content = content;
            reset();

            if (!alreadyVisible)
            {
                // 先置零透明度再显示：Show() 若以满透明度渲染首帧，随后的淡入会造成视觉闪跳
                _window.Opacity = 0;
                EnsureOnScreen();
                _window.Show();
                _window.Activate();
            }
            _window.FocusSearch();

            if (!alreadyVisible)
            {
                var ms = GetTokenDouble("Motion.SummonMs");
                if (ms > 0)
                {
                    var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new QuadraticEase() };
                    _window.BeginAnimation(UIElement.OpacityProperty, anim);
                }
                else
                {
                    _window.Opacity = 1;
                }
            }

            sw.Stop();
            Logger.Info("summon show [" + tag + "] elapsed=" + sw.ElapsedMilliseconds + "ms");
        }

        private void HideSummon()
        {
            if (_window == null || _hiding || !_window.IsVisible) return;
            var sw = Stopwatch.StartNew();
            _hiding = true;

            // 位置记忆（沿用 v1 的 windowBounds 键）
            _config.SaveBounds(_window.Left, _window.Top, _window.Width, _window.Height);

            var ms = GetTokenDouble("Motion.HideMs");
            if (ms > 0)
            {
                // 淡出完成后才真正隐藏（BeginAnimation 不阻塞，直接 Hide 会让动画形同虚设）
                var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new QuadraticEase() };
                anim.Completed += (sender, e) => CompleteHide(sw);
                _window.BeginAnimation(UIElement.OpacityProperty, anim);
                return;
            }
            CompleteHide(sw);
        }

        private void CompleteHide(Stopwatch sw)
        {
            _window.BeginAnimation(UIElement.OpacityProperty, null);
            _window.Opacity = 1;
            _window.Hide();
            _hiding = false;

            // 焦点恢复（验证 + 重试 + 全程打点）
            if (_capturedForeground)
            {
                _capturedForeground = false;
                Foreground.Restore(_capturedTarget);
                _capturedTarget = IntPtr.Zero;
            }

            sw.Stop();
            Logger.Info("summon hide elapsed=" + sw.ElapsedMilliseconds + "ms");

            // 剪贴板回填：必须在焦点恢复之后才写——否则用户按 Ctrl+V 会粘回自己
            FlushPendingWriteBack();
        }

        private void EnsureOnScreen()
        {
            var left = _config.BoundsX;
            var top = _config.BoundsY;
            var virtualLeft = SystemParameters.VirtualScreenLeft;
            var virtualTop = SystemParameters.VirtualScreenTop;
            var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
            var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;
            if (double.IsNaN(left) || left < virtualLeft - 8 || left > virtualRight - 80)
                left = (SystemParameters.WorkArea.Width - _window.Width) / 2 + virtualLeft;
            if (double.IsNaN(top) || top < virtualTop - 8 || top > virtualBottom - 80)
                top = SystemParameters.WorkArea.Height / 3 + virtualTop; // 高度动态化后默认位只按上三分之一锚定
            _window.Left = left;
            _window.Top = top;
        }

        // ---- 剪贴板历史（M3，C1-C4 + S9）----

        /// <summary>列表里选中一条：先收起（含焦点恢复），再由 CompleteHide 触发写回。</summary>
        private void OnClipboardSelectRequested(ClipRecord record)
        {
            if (record == null) return;
            _pendingWriteBack = record;
            if (_window != null && _window.IsVisible) HideSummon();
            else FlushPendingWriteBack(); // 兜底：窗口已收起（如 --debug 场景）
        }

        /// <summary>把待回填记录写入剪贴板。时序硬性要求：必须在焦点恢复之后（见 §6）。</summary>
        private void FlushPendingWriteBack()
        {
            var record = _pendingWriteBack;
            _pendingWriteBack = null;
            if (record == null || _clipHistory == null) return;
            var error = _clipHistory.WriteBack(record);
            if (error != null)
            {
                Logger.Error("clipboard writeback failed: " + error);
                _tray.ShowBubble("PyTools", error);
                return;
            }
            Logger.Info("clipboard writeback ok: " + record.KindLabel);
            if (_clipView != null) _clipView.OnHistoryChanged();
        }

        /// <summary>剪贴板监听回调（UI 线程）：隐私过滤 → 去重 → 落盘 → 刷新视图。</summary>
        private void OnClipboardUpdate(IDataObject data)
        {
            if (_clipHistory == null) return;
            if (_clipHistory.InSelfWriteWindow)
            {
                Logger.Debug("clipboard update ignored (self write)");
                return;
            }
            _clipHistory.PrivacyFilter = _config.ClipboardPrivacyFilter;
            string skipReason;
            var record = _clipHistory.Capture(data, out skipReason);
            if (record == null)
            {
                if (skipReason != null && skipReason != "duplicate") Logger.Debug("clipboard skipped: " + skipReason);
                return;
            }
            Logger.Info("clipboard captured: " + record.KindLabel);
            if (_clipView != null) _clipView.OnHistoryChanged();
        }

        /// <summary>
        /// 应用剪贴板直呼热键（设置窗口调用）。返回 null = 成功，否则错误文案。
        /// key 由程序生成或用户录制；Win 组合只能由程序生成（WPF 录制不到 Win+V 的 V 键）。
        /// </summary>
        private string ApplyClipboardHotkey(string newHotkey, bool enabled)
        {
            var key = (newHotkey ?? "").Trim();
            if (enabled && key.Length == 0) return "请先按下一个组合键，或点右侧「Win+V」";

            uint mods, vk; string parseError;
            if (enabled && !HotkeyParser.TryParse(key, out mods, out vk, out parseError)) return parseError;

            // 与主热键相同：先判定，不交给系统报 1409（文案更有用）
            if (enabled && string.Equals(key, _config.Hotkey, StringComparison.OrdinalIgnoreCase))
                return "不能与主热键相同";

            if (_clipboardRegistered && _hotkeyHwnd != IntPtr.Zero)
            {
                NativeMethods.UnregisterHotKey(_hotkeyHwnd, HotkeyIdClipboard);
                _clipboardRegistered = false;
            }

            if (enabled)
            {
                int errorCode;
                if (!RegisterHotkey(HotkeyIdClipboard, key, out errorCode))
                {
                    // 回滚：恢复原已注册的热键
                    if (_config.ClipboardHotkeyEnabled && !string.IsNullOrEmpty(_config.ClipboardHotkey))
                        _clipboardRegistered = RegisterHotkey(HotkeyIdClipboard, _config.ClipboardHotkey);
                    return DescribeHotkeyFailure(key, errorCode);
                }
                _clipboardRegistered = true;
            }

            _config.ClipboardHotkey = key;
            _config.ClipboardHotkeyEnabled = enabled;
            _config.Save();
            return null;
        }

        private static string DescribeHotkeyFailure(string key, int errorCode)
        {
            if (errorCode == NativeMethods.ErrorHotkeyAlreadyRegistered)
            {
                if (key.IndexOf("Win+", StringComparison.OrdinalIgnoreCase) == 0)
                {
                    return "已被系统或其他程序占用：Win10 1809+ 的系统剪贴板历史会占 Win+V，"
                         + "可在「设置 → 系统 → 剪贴板」关闭后重试，或改用其他组合键";
                }
                return "已被系统或其他程序占用，请换一个组合键";
            }
            if (errorCode == -1) return "组合键格式不受支持";
            return "注册失败（错误码 " + errorCode + "）";
        }

        // ---- 启动器（M2a）----

        private void OnAppsUpdated()
        {
            // 重扫完成后，呼出中的列表按当前查询刷新
            if (_view != null) _view.OnAppsReplaced();
        }

        private void OnScanFinished()
        {
            var count = _index.Apps.Count;
            Logger.Info("scan finished, apps=" + count);
            if (_bubbleNextScan)
            {
                _bubbleNextScan = false;
                _tray.ShowBubble("PyTools", "重新扫描完成：" + count + " 个应用");
            }
        }

        private void OnLaunchRequested(AppEntry entry)
        {
            var result = AppLauncher.Open(entry, _index.Roots, _index.Custom);
            if (result == null)
            {
                // 启动成功：收起并恢复焦点
                HideSummon();
                return;
            }
            if (result.RemovedDeadEntry) _index.RescanAsync(); // 死条目已移除，列表刷新
            if (!string.IsNullOrEmpty(result.Message)) _view.ShowToast(result.Message);
            // 失败或移除场景都保持窗口展开：用户停留在已清理/已提示的列表上
        }

        // ---- Tab 操作菜单（M2b，L5）----

        private void OnActionRequested(AppEntry entry, string actionId)
        {
            switch (actionId)
            {
                case "open":
                    OnLaunchRequested(entry); // 统一入口：含死条目检查与失败提示
                    return;
                case "show-folder":
                {
                    var error = OpenInExplorer(TargetFileOf(entry));
                    FinishAction(error);
                    return;
                }
                case "copy-name":
                    FinishAction(CopyText(entry.Name));
                    return;
                case "copy-path":
                    FinishAction(CopyText(TargetFileOf(entry)));
                    return;
                case "copy-dir":
                    FinishAction(CopyText(TargetDirOf(entry)));
                    return;
                case "cmd":
                    FinishAction(OpenTerminal("cmd", TargetDirOf(entry)));
                    return;
                case "powershell":
                    FinishAction(OpenTerminal("powershell", TargetDirOf(entry)));
                    return;
                case "remove-custom":
                    // v1 无手动移除出口（仅目标被删自动移除），本项补体验缺口（2026-10-30 用户确认）
                    if (_index.Custom.Remove(entry.Path))
                    {
                        _index.RescanAsync();
                        _view.ExitActionMode();
                        HideSummon();
                        _tray.ShowBubble("PyTools", "已移除登记：" + entry.Name);
                    }
                    else
                    {
                        _view.ShowToast("移除登记失败：清单中没有该条目");
                    }
                    return;
                default:
                    _view.ShowToast("未知操作：" + actionId);
                    return;
            }
        }

        /// <summary>动作收口：成功收起呼出窗（v1 语义）；失败 toast 原因并退出操作模式回列表。</summary>
        private void FinishAction(string error)
        {
            if (error == null)
            {
                HideSummon();
                return;
            }
            _view.ExitActionMode();
            _view.ShowToast(error);
        }

        private static string TargetFileOf(AppEntry entry)
        {
            return string.IsNullOrEmpty(entry.TargetPath) ? entry.Path : entry.TargetPath;
        }

        private static string TargetDirOf(AppEntry entry)
        {
            if (!string.IsNullOrEmpty(entry.TargetDir)) return entry.TargetDir;
            try { return Path.GetDirectoryName(TargetFileOf(entry)) ?? ""; }
            catch { return ""; }
        }

        private string OpenInExplorer(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "目标不存在：" + path;
            try
            {
                Process.Start("explorer.exe", "/select,\"" + path + "\"");
                return null;
            }
            catch (Exception ex) { return "打开目录失败：" + ex.Message; }
        }

        private static string CopyText(string text)
        {
            try { Clipboard.SetText(text ?? ""); return null; }
            catch (Exception ex) { return "复制失败：" + ex.Message; }
        }

        private string OpenTerminal(string shell, string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return "目录不存在：" + dir;
            try
            {
                if (shell == "cmd")
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/k cd /d \"" + dir + "\"",
                        UseShellExecute = true
                    });
                }
                else
                {
                    var escaped = dir.Replace("'", "''");
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = "-NoExit -Command \"Set-Location -LiteralPath '" + escaped + "'\"",
                        UseShellExecute = true
                    });
                }
                return null;
            }
            catch (Exception ex) { return "打开终端失败：" + ex.Message; }
        }

        // ---- 静默添加（M2b，L9）----

        /// <summary>提取 --add-app= 自包含 token（空串 = 带参但值无效，null = 未带参）。</summary>
        private static string ExtractAddAppArg(System.Collections.Generic.List<string> args)
        {
            foreach (var arg in args)
            {
                if (!arg.StartsWith("--add-app=", StringComparison.Ordinal)) continue;
                return arg.Substring("--add-app=".Length).Trim('"', ' ');
            }
            return null;
        }

        private void HandleAddAppRequest(object raw)
        {
            var target = raw as string;
            if (string.IsNullOrEmpty(target)) return;
            Logger.Info("add-app request: " + target);
            var result = _index.Custom.Add(target);
            if (result.Ok)
            {
                _index.RescanAsync();
                _tray.ShowBubble("PyTools", "已添加：" + result.Name);
            }
            else
            {
                _tray.ShowBubble("PyTools", AddAppFailureText(result.Reason));
            }
        }

        private static string AddAppFailureText(string reason)
        {
            switch (reason)
            {
                case "invalid": return "路径无效，未添加";
                case "not-found": return "文件不存在或快捷方式指向失效，未添加";
                case "not-file": return "目标不是文件，未添加";
                case "not-app": return "仅支持可执行应用（exe/lnk），未添加";
                case "shortcut-broken": return "快捷方式已损坏，未添加";
                case "duplicate": return "该应用已在应用列表中";
                default: return "添加失败：" + reason;
            }
        }

        private static double GetTokenDouble(string key)
        {
            object value;
            if (Application.Current != null && Application.Current.Resources.Contains(key))
            {
                value = Application.Current.Resources[key];
                if (value is double) return (double)value;
            }
            return 0;
        }

        // ---- 设置窗口（M2b，L10）----

        private SettingsWindow _settingsWindow;

        private void OpenSettings()
        {
            if (_settingsWindow != null)
            {
                _settingsWindow.Activate();
                return;
            }
            _settingsWindow = new SettingsWindow(_config, _index, ApplyHotkey, ApplyClipboardHotkey);
            _settingsWindow.Closed += (sender, eventArgs) => _settingsWindow = null;
            _settingsWindow.Show();
        }

        /// <summary>热键重录（设置窗口调用）：null = 成功，否则错误文案。失败回滚原热键。</summary>
        private string ApplyHotkey(string newHotkey)
        {
            NativeMethods.UnregisterHotKey(_hotkeyHwnd, HotkeyIdMain);
            if (!RegisterHotkey(HotkeyIdMain, newHotkey))
            {
                RegisterHotkey(HotkeyIdMain, _config.Hotkey); // 回滚原热键
                return "注册失败：" + newHotkey + "（可能已被占用），已保留原热键";
            }
            _config.Hotkey = newHotkey;
            _config.Save();
            return null;
        }

        // ---- 退出 ----

        private void ExitApplication()
        {
            try
            {
                if (_hotkeyHwnd != IntPtr.Zero)
                {
                    NativeMethods.UnregisterHotKey(_hotkeyHwnd, HotkeyIdMain);
                    NativeMethods.UnregisterHotKey(_hotkeyHwnd, HotkeyIdClipboard);
                }
                if (_hotkeySource != null) _hotkeySource.Dispose();
                if (_clipWatcher != null) _clipWatcher.Dispose();
                if (_window != null && _window.IsVisible)
                    _config.SaveBounds(_window.Left, _window.Top, _window.Width, _window.Height);
                if (_tray != null) _tray.Dispose();
                if (_single != null) _single.Dispose();
                Logger.Info("exit");
            }
            catch { /* 退出路径尽力而为 */ }
            Shutdown();
        }
    }
}
