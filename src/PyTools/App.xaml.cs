using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
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
        private AppIndex _index;
        private bool _bubbleNextScan; // 手动重扫后气泡反馈（首扫不气泡）
        private string _debugQuery;   // --debug 配套 --query=<词>：呼出即预填搜索（UI 截图自验用）
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

            if (args.Contains("--selftest"))
            {
                Environment.Exit(SelfTest.Run());
            }

            _single = new SingleInstance();
            if (!_single.TryAcquire())
            {
                Logger.Info("second instance -> signal existing");
                SingleInstance.SignalExisting();
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

                _window = _config.LiteMode ? (SummonWindowBase)new SummonOpaqueWindow() : new SummonWindow();
                _window.Width = _config.BoundsWidth; // 高度交给 SizeToContent（空态仅搜索框）
                _window.EscapePressed += HideSummon;
                _view = (SummonView)_window.Content;
                _view.Index = _index;
                _view.PageStep = _config.PageStep;
                _view.LaunchRequested += OnLaunchRequested;
                _window.Deactivated += (sender, eventArgs) =>
                {
                    // M5 图钉在此挂钩：钉住时不响应失焦
                    if (!debug) HideSummon();
                };

                if (!RegisterHotkey(HotkeyIdMain, _config.Hotkey))
                {
                    _tray.ShowBubble("PyTools", "主热键注册失败：" + _config.Hotkey + "，可能已被占用");
                }
                if (_config.ClipboardHotkeyEnabled)
                {
                    // 剪贴板视图在 M3 提供；本期仅注册占位并记录，避免热键被其它程序占用
                    RegisterHotkey(HotkeyIdClipboard, _config.ClipboardHotkey);
                    Logger.Info("clipboard hotkey registered (view pending M3)");
                }

                _single.ListenShow(_uiContext, ToggleSummon);

                Logger.Info("started, liteMode=" + _config.LiteMode + ", debug=" + debug);

                if (debug)
                {
                    // 调试模式：启动即显示且禁用失焦隐藏（与 v1 --debug 语义一致）
                    ShowSummon();
                    if (_debugQuery != null)
                    {
                        _view.SearchBox.Text = _debugQuery; // 触发 RefreshResults（截图自验结果态）
                    }
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
            uint mods, vk; string err;
            if (!HotkeyParser.TryParse(text, out mods, out vk, out err))
            {
                Logger.Error("hotkey parse failed: " + text + " -> " + err);
                return false;
            }
            EnsureHotkeyWindow();
            var ok = NativeMethods.RegisterHotKey(_hotkeyHwnd, id, mods, vk);
            Logger.Info("register hotkey id=" + id + " \"" + text + "\" -> " + ok);
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
                else if (id == HotkeyIdClipboard) { /* M3：剪贴板直呼 */ }
                handled = true;
            }
            return IntPtr.Zero;
        }

        // ---- 呼出 / 收起 ----

        private void ToggleSummon()
        {
            if (_window != null && _window.IsVisible) HideSummon();
            else ShowSummon();
        }

        private bool _hiding; // 收起动画进行中，防重复触发

        private void ShowSummon()
        {
            if (_window.IsVisible || _hiding) return;
            var sw = Stopwatch.StartNew();

            // 呼出前记录前台（进程内同步调用，零开销）——焦点恢复的依据
            _capturedTarget = Foreground.Capture();
            _capturedForeground = true;
            Logger.Debug("captured foreground=0x" + _capturedTarget.ToString("X"));

            _hiding = false;
            _view.ResetForSummon(); // 空态：只有搜索框（v1 同款），随后 EnsureOnScreen 的定位基于收缩后的实际尺寸
            // 先置零透明度再显示：Show() 若以满透明度渲染首帧，随后的淡入会造成视觉闪跳
            _window.Opacity = 0;
            EnsureOnScreen();
            _window.Show();
            _window.Activate();
            _window.FocusSearch();

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

            sw.Stop();
            Logger.Info("summon show elapsed=" + sw.ElapsedMilliseconds + "ms");
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
