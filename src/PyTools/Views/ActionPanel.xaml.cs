using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using PyTools.Core;

namespace PyTools.Views
{
    /// <summary>
    /// 动作面板组件（Raycast 式，M4 各工具共用）：
    /// 独立 Popup 浮层（可伸出窗外，高度与宿主列表解耦，上限 5 行）+ 自带过滤输入框 +
    /// 行尾单键提示 + 贴宿主右下角定位。
    /// 宿主职责：只声明动作表并执行 Invoked(id)；Esc/Tab 收起时收到 Dismissed，自行决定焦点去向。
    /// 键位策略集中在本类 HandleKey：宿主视图在 PreviewKeyDown 里转交，避免「焦点在 Popup 里
    /// 收不到键」与「宿主与面板各写一份策略」两种老问题。
    /// </summary>
    public partial class ActionPanel : UserControl
    {
        private readonly List<ActionItem> _all = new List<ActionItem>();   // 本次呼出的全部可用动作
        private readonly List<ActionItem> _rows = new List<ActionItem>();  // 过滤后可见行
        private FrameworkElement _anchor;
        private int _sel = -1;

        /// <summary>选中动作执行：参数为动作 id（面板此时已自行收起）。</summary>
        public event Action<string> Invoked;

        /// <summary>用户按 Esc/Tab 收起面板（宿主据此退出动作态）。</summary>
        public event Action Dismissed;

        /// <summary>Ctrl+N/P 步进条数（与列表一致，取 v2.pageStep）。</summary>
        public int PageStep { get; set; }

        public bool IsOpen { get { return PART_Popup.IsOpen; } }

        public ActionPanel()
        {
            InitializeComponent();
            PageStep = 5;
            PART_ActionFilter.TextChanged += delegate { if (IsOpen) Rebuild(); };
            PART_Actions.PreviewMouseLeftButtonUp += OnActionClick;
            // 面板内按键：焦点在过滤框里时，事件从 Popup 根隧道到卡片
            Card.PreviewKeyDown += delegate(object sender, KeyEventArgs e) { if (HandleKey(e)) e.Handled = true; };
            Card.SizeChanged += delegate { UpdateOffsets(); }; // 过滤增减行时保持底边锚定
        }

        /// <summary>呼出面板。anchor = 宿主根元素（定位基准，面板贴其右下角留 12px）。</summary>
        public void Show(FrameworkElement anchor, IList<ActionItem> items)
        {
            _anchor = anchor;
            _all.Clear();
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null || !item.Available || item.Title.Length == 0) continue;
                _all.Add(item);
            }

            PART_ActionFilter.Text = "";
            Rebuild();
            if (anchor != null) PART_Popup.PlacementTarget = anchor;
            PART_Popup.IsOpen = true;
            PART_ActionFilter.Focus();
            // SizeToContent 之外的面板要等首帧布局完成才拿得到 ActualWidth/Height
            Dispatcher.BeginInvoke(new Action(UpdateOffsets), DispatcherPriority.Loaded);
        }

        /// <summary>收起面板（宿主主动收起，不触发 Dismissed）。</summary>
        public void Hide()
        {
            if (!PART_Popup.IsOpen) return;
            PART_Popup.IsOpen = false;
            PART_Actions.ItemsSource = null;
            _all.Clear();
            _rows.Clear();
            _sel = -1;
        }

        /// <summary>键位策略（宿主 PreviewKeyDown 转交；返回 true 表示已消费）。
        /// 未命中提示键的字母返回 false，放行给过滤框正常输入。</summary>
        public bool HandleKey(KeyEventArgs e)
        {
            if (!IsOpen) return false;
            var isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

            // Ctrl+Enter：动作面板开关（开 → 关）
            if (isCtrl && e.Key == Key.Enter) { HideAndDismiss(); return true; }

            // 移动键：方向键 + Ctrl+J/K 单条加速 + Ctrl+N/P 页步进（与列表视图全局键位一致）
            if (e.Key == Key.Down || (isCtrl && e.Key == Key.J)) { Move(_sel + 1); return true; }
            if (e.Key == Key.Up || (isCtrl && e.Key == Key.K)) { Move(_sel - 1); return true; }
            if (isCtrl && e.Key == Key.N) { Move(_sel + Math.Max(1, PageStep)); return true; }
            if (isCtrl && e.Key == Key.P) { Move(_sel - Math.Max(1, PageStep)); return true; }

            if (e.Key == Key.Enter) { ExecuteSelected(); return true; }
            if (e.Key == Key.Escape || e.Key == Key.Tab) { HideAndDismiss(); return true; }

            if (!isCtrl && e.Key >= Key.A && e.Key <= Key.Z)
            {
                // 过滤为空且字母命中行尾提示键 → 直执行（Raycast quick key）；
                // 未命中的字母放行给过滤框（否则想输 "copy" 的 c 会被吞掉）
                var letter = ((char)('A' + (e.Key - Key.A))).ToString();
                if ((PART_ActionFilter.Text ?? "").Trim().Length == 0)
                {
                    var index = _rows.FindIndex(delegate(ActionItem item) { return item.Hint == letter; });
                    if (index >= 0) { ExecuteAt(index); return true; }
                }
                return false;
            }
            return false; // 数字/标点等交给过滤框
        }

        /// <summary>执行当前选中项（回车/点击统一入口）。</summary>
        public void ExecuteSelected()
        {
            ExecuteAt(_sel);
        }

        // ---- 内部 ----

        private void HideAndDismiss()
        {
            Hide();
            var handler = Dismissed;
            if (handler != null) handler();
        }

        private void ExecuteAt(int index)
        {
            if (index < 0 || index >= _rows.Count) return;
            var id = _rows[index].Id;
            Hide();
            var handler = Invoked;
            if (handler != null) handler(id);
        }

        /// <summary>按过滤词重建可见行（标题与 id 双匹配，输英文 copy 也能命中 copy-name）。</summary>
        private void Rebuild()
        {
            var query = (PART_ActionFilter.Text ?? "").Trim();
            _rows.Clear();
            for (var i = 0; i < _all.Count; i++)
            {
                var item = _all[i];
                if (query.Length > 0
                    && item.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0
                    && (item.Id ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                _rows.Add(item);
            }
            PART_Actions.ItemsSource = null;
            PART_Actions.ItemsSource = _rows;
            NoActionHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Move(0);
        }

        private void Move(int index)
        {
            if (_rows.Count == 0) { _sel = -1; PART_Actions.SelectedIndex = -1; return; }
            _sel = Math.Max(0, Math.Min(index, _rows.Count - 1));
            PART_Actions.SelectedIndex = _sel;
            PART_Actions.ScrollIntoView(_rows[_sel]);
        }

        /// <summary>贴宿主右下角（留 12px）。面板可伸出窗外，故不用宿主实际高度夹取。</summary>
        private void UpdateOffsets()
        {
            if (!PART_Popup.IsOpen || _anchor == null) return;
            PART_Popup.HorizontalOffset = Math.Max(0, _anchor.ActualWidth - 12 - Card.ActualWidth);
            PART_Popup.VerticalOffset = Math.Max(0, _anchor.ActualHeight - 12 - Card.ActualHeight);
        }

        private void OnActionClick(object sender, MouseButtonEventArgs e)
        {
            var item = e.OriginalSource as DependencyObject;
            while (item != null && !(item is ListBoxItem)) item = System.Windows.Media.VisualTreeHelper.GetParent(item);
            var container = item as ListBoxItem;
            if (container == null) return;
            var index = PART_Actions.ItemContainerGenerator.IndexFromContainer(container);
            if (index >= 0) { ExecuteAt(index); e.Handled = true; }
        }
    }
}
