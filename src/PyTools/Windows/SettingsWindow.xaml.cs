using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using PyTools.Core;
using PyTools.Native;

namespace PyTools.Windows
{
    /// <summary>
    /// 设置窗口（M2b，L10+S10）。复用令牌层；开关点击即生效（注册表类动作回读实况，
    /// 失败回滚勾选态并提示——意图与实况分离，Mino 教训）。
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly AppConfig _config;
        private readonly AppIndex _index;
        private readonly Func<string, string> _hotkeyApplier; // 应用热键：null=成功，否则错误文案

        private bool _loaded;          // Loaded 之前不响应控件事件
        private bool _capturingHotkey; // 热键捕获中

        public SettingsWindow(AppConfig config, AppIndex index, Func<string, string> hotkeyApplier)
        {
            InitializeComponent();
            _config = config;
            _index = index;
            _hotkeyApplier = hotkeyApplier;
            SourceInitialized += (s, e) => SetDarkTitleBar();
        }

        private void SetDarkTitleBar()
        {
            // v2 令牌层为深色视觉，标题栏跟随（DWMWA_USE_IMMERSIVE_DARK_MODE）
            var source = (System.Windows.Interop.HwndSource)PresentationSource.FromVisual(this);
            if (source == null) return;
            var attribute = 1;
            NativeMethods.DwmSetWindowAttribute(source.Handle, 20, ref attribute, sizeof(int));
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            HotkeyBox.Text = _config.Hotkey;
            AutoStartCheck.IsChecked = _config.AutoStart;
            PinyinCheck.IsChecked = _config.PinyinSearch;
            PageStepBox.Text = _config.PageStep.ToString();
            LiteModeCheck.IsChecked = _config.LiteMode;
            // 右键菜单与自启以系统实况为准（意图 ≠ 实况）
            ContextMenuCheck.IsChecked = ContextMenuRegistry.Configured();
            AutoStartCheck.IsChecked = Startup.IsApplied();
            SetHint(HotkeyHint, HotkeyIdleHint);
            SetHint(ContextMenuHint, "更改即时写入注册表，点击即生效");
            RefreshCustomList();
            _loaded = true;
        }

        /// <summary>提示行统一出口。提示行常驻占位（不折叠）——状态文字出现/消失不推挤下方表单布局。</summary>
        private static void SetHint(System.Windows.Controls.TextBlock target, string text)
        {
            target.Text = text;
        }

        private const string HotkeyIdleHint = "点击后按下新的组合键（如 Ctrl+Alt+J），Esc 取消";

        // ---- 主热键（点击进入捕获态，按下组合即完成）----

        private void HotkeyBox_OnGotFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!_loaded) return;
            if (_capturingHotkey) return;
            _capturingHotkey = true;
            // 视觉反馈要落在「点击」这一刻：边框高亮 + 提示，让用户知道现在就能按
            HotkeyBox.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "Accent");
            HotkeyBox.BorderThickness = new Thickness(1.5);
            SetHint(HotkeyHint, "请按下新的组合键（如 Ctrl+Alt+J）…Esc 取消");
        }

        private void HotkeyBox_OnLostFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!_capturingHotkey) return;
            _capturingHotkey = false;
            RestoreHotkeyBoxVisual();
            SetHint(HotkeyHint, HotkeyIdleHint); // 回到常驻引导文案，不改变行高
        }

        private void RestoreHotkeyBoxVisual()
        {
            HotkeyBox.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "Border.Input");
            HotkeyBox.BorderThickness = new Thickness(1);
        }

        private void HotkeyBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!_loaded) return;
            if (e.Key == Key.Escape)
            {
                if (_capturingHotkey)
                {
                    _capturingHotkey = false;
                    RestoreHotkeyBoxVisual();
                    HotkeyBox.Text = _config.Hotkey;
                    SetHint(HotkeyHint, "已取消捕获");
                    // 焦点移出只读框：否则焦点仍在框内、再按键无响应且无提示，用户会再次困惑
                    HotkeyBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                }
                e.Handled = true;
                return;
            }
            if (!_capturingHotkey) return;

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var text = ToHotkeyString(Keyboard.Modifiers, key);
            if (text == null) { e.Handled = true; return; } // 纯修饰键，继续等

            e.Handled = true;
            uint mods, vk; string parseError;
            if (!HotkeyParser.TryParse(text, out mods, out vk, out parseError))
            {
                SetHint(HotkeyHint, parseError);
                return;
            }

            var error = _hotkeyApplier(text);
            if (error != null)
            {
                SetHint(HotkeyHint, error);
                return;
            }
            _capturingHotkey = false;
            RestoreHotkeyBoxVisual();
            HotkeyBox.Text = _config.Hotkey;
            SetHint(HotkeyHint, "热键已更新：" + _config.Hotkey);
            HotkeyBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }

        /// <summary>WPF Key → 配置串（HotkeyParser 语义）；纯修饰键返回 null。</summary>
        private static string ToHotkeyString(ModifierKeys modifiers, Key key)
        {
            var parts = new List<string>();
            if ((modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
            if ((modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
            if ((modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
            if ((modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");

            string name;
            if (key >= Key.A && key <= Key.Z) name = key.ToString();
            else if (key >= Key.D0 && key <= Key.D9) name = ((int)(key - Key.D0)).ToString();
            else if (key >= Key.F1 && key <= Key.F24) name = key.ToString();
            else
            {
                switch (key)
                {
                    case Key.Space: name = "Space"; break;
                    case Key.Tab: name = "Tab"; break;
                    case Key.Enter: name = "Enter"; break;
                    case Key.Back: name = "Backspace"; break;
                    case Key.Insert: name = "Insert"; break;
                    case Key.Delete: name = "Delete"; break;
                    case Key.Home: name = "Home"; break;
                    case Key.End: name = "End"; break;
                    case Key.PageUp: name = "PgUp"; break;
                    case Key.PageDown: name = "PgDn"; break;
                    case Key.Left: name = "Left"; break;
                    case Key.Up: name = "Up"; break;
                    case Key.Right: name = "Right"; break;
                    case Key.Down: name = "Down"; break;
                    default: return null; // 其余键不作为主键
                }
            }
            if (parts.Count == 0) return null; // 必须带修饰符
            parts.Add(name);
            return string.Join("+", parts.ToArray());
        }

        // ---- 开关类（点击即生效 + 实况回读）----

        private void OnAutoStartChanged(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            var enable = AutoStartCheck.IsChecked == true;
            var error = Startup.Apply(enable);
            if (error != null)
            {
                AutoStartCheck.IsChecked = Startup.IsApplied(); // 回滚到实况
                MessageBox.Show(this, error, "PyTools", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _config.AutoStart = enable;
            _config.Save();
        }

        private void OnPinyinChanged(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            _config.PinyinSearch = PinyinCheck.IsChecked == true;
            _config.Save();
        }

        private void OnLiteModeChanged(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            _config.SaveLiteMode(LiteModeCheck.IsChecked == true); // 重启生效
        }

        private void OnContextMenuChanged(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            var enable = ContextMenuCheck.IsChecked == true;
            var error = enable ? ContextMenuRegistry.Enable() : ContextMenuRegistry.Disable();
            var live = ContextMenuRegistry.Configured();
            if (error != null || live != enable)
            {
                ContextMenuCheck.IsChecked = live; // 实况回滚
                SetHint(ContextMenuHint, (error ?? "注册表状态与预期不符") + "（当前实况：" + (live ? "已开启" : "已关闭") + "）");
                return;
            }
            SetHint(ContextMenuHint, enable ? "已开启（点击即生效）" : "已关闭");
            _config.AddAppContextMenu = live;
            _config.Save();
        }

        // ---- 翻页步长 ----

        private void OnPageStepKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { CommitPageStep(); e.Handled = true; }
        }

        private void OnPageStepCommitted(object sender, RoutedEventArgs e) { CommitPageStep(); }

        private void CommitPageStep()
        {
            if (!_loaded) return;
            int step;
            if (!int.TryParse(PageStepBox.Text.Trim(), out step) || step < 1 || step > 20)
            {
                PageStepBox.Text = _config.PageStep.ToString();
                return;
            }
            if (step == _config.PageStep) return;
            _config.PageStep = step;
            _config.Save();
        }

        // ---- 应用登记清单 ----

        public sealed class CustomItem
        {
            public string Key;   // 显示名
            public string Value; // 路径
        }

        private void RefreshCustomList()
        {
            var items = new ObservableCollection<CustomItem>();
            foreach (var app in _index.Custom.Load())
            {
                items.Add(new CustomItem { Key = app.Name, Value = app.Path });
            }
            CustomList.ItemsSource = items;
            CustomEmptyHint.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SetHint(CustomHint, "共 " + items.Count + " 条");
        }

        private void OnRemoveCustom(object sender, RoutedEventArgs e)
        {
            var item = CustomList.SelectedItem as CustomItem;
            if (item == null)
            {
                SetHint(CustomHint, "先在列表中选中一条登记");
                return;
            }
            if (_index.Custom.Remove(item.Value))
            {
                _index.RescanAsync();
                RefreshCustomList();
                SetHint(CustomHint, "已移除：" + item.Key);
            }
            else
            {
                SetHint(CustomHint, "移除失败（可能已被移除）");
            }
        }

        private void OnOpenCustomFolder(object sender, RoutedEventArgs e)
        {
            var item = CustomList.SelectedItem as CustomItem;
            var path = item == null ? null : item.Value;
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                SetHint(CustomHint, "目标不存在");
                return;
            }
            try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\""); }
            catch (Exception ex) { SetHint(CustomHint, "打开失败：" + ex.Message); }
        }

        private void OnClose(object sender, RoutedEventArgs e) { Close(); }
    }
}
