using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PyTools.Core;

namespace PyTools.Views
{
    /// <summary>结果条目视图模型：图标异步填充、角标锚定可视区（滚动后对可见行重编号）。</summary>
    public sealed class ItemVM : INotifyPropertyChanged, IBadgedItem
    {
        private string _badge = "";
        private BitmapImage _icon;

        public AppEntry Entry;
        public string Name { get { return Entry.Name; } }
        public string PathText { get { return string.IsNullOrEmpty(Entry.TargetPath) ? Entry.Path : Entry.TargetPath; } }

        public BitmapImage Icon
        {
            get { return _icon; }
            set { _icon = value; if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("Icon")); }
        }

        public string Badge
        {
            get { return _badge; }
            set { if (_badge != value) { _badge = value; if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("Badge")); } }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public partial class SummonView : UserControl, ISearchFocusable
    {
        private readonly List<ItemVM> _items = new List<ItemVM>();
        private readonly DispatcherTimer _toastTimer;
        private int _generation; // 结果集换代号：图标异步回调过期即弃
        private ScrollViewer _scroll;
        private bool _actionMode;      // Ctrl+Enter 动作面板：独立浮层 + 自带过滤输入框
        private AppEntry _actionTarget; // 操作模式的目标条目

        public SummonView()
        {
            InitializeComponent();
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
            _toastTimer.Tick += delegate { Toast.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };
            PART_List.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnListScrollChanged));
            PART_List.PreviewMouseLeftButtonUp += OnListClick;
            SearchBox.TextChanged += delegate { RefreshResults(); };
            Actions.Invoked += OnActionInvoked;            // 面板选中执行 → 交给 App 层
            Actions.Dismissed += ExitActionModeInternal;    // Esc/Tab 收起 → 退出动作态
            PreviewKeyDown += OnViewPreviewKeyDown;
            PreviewMouseLeftButtonUp += OnViewPreviewMouseUp; // 底部「操作」键帽的鼠标呼出（根级 hit test）
            Loaded += delegate { _scroll = ListBadgeHelper.FindScroll(PART_List); };
        }

        /// <summary>索引（App 注入）。null 时搜索无结果。</summary>
        public AppIndex Index { get; set; }

        /// <summary>Ctrl+N/P 步进条数（v2.pageStep）。</summary>
        public int PageStep { get; set; }

        public event Action<AppEntry> LaunchRequested;

        /// <summary>操作菜单动作请求（App 层执行，成败后回调 view）。</summary>
        public event Action<AppEntry, string> ActionRequested;

        public TextBox SearchBox { get { return PART_SearchBox; } }

        public void FocusSearch()
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }

        /// <summary>呼出时重置：清空查询、收起列表、隐藏 toast（呼出即只有输入框，v1 同款）。</summary>
        public void ResetForSummon()
        {
            _toastTimer.Stop();
            Toast.Visibility = Visibility.Collapsed;
            ExitActionModeInternal();
            SearchBox.Text = "";
            SetListVisible(false);
        }

        /// <summary>Esc / Tab 由 App 转问：操作模式先退回结果列表而非藏窗（Tab 为过渡期兼容）。返回是否已消费。</summary>
        public bool TryExitActionMode()
        {
            if (!_actionMode) return false;
            ExitActionModeInternal();
            RefreshResults();
            return true;
        }

        public void ExitActionMode()
        {
            if (!_actionMode) return;
            ExitActionModeInternal();
            RefreshResults();
        }

        private void ExitActionModeInternal()
        {
            if (!_actionMode) return;
            _actionMode = false;
            _actionTarget = null;
            Actions.Hide();
            SearchBox.Focus(); // 焦点回主搜索框（面板有自己的输入框，主框全程未被复用）
            UpdateHintBar(); // 恢复提示条（公开退出路径随后还会 RefreshResults 再联动一次）
        }

        public void ShowToast(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            ToastText.Text = message;
            Toast.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        /// <summary>索引整体替换（重扫完成）时刷新当前结果。</summary>
        public void OnAppsReplaced()
        {
            if (!IsVisible) return;
            if (_actionMode) ExitActionModeInternal(); // 索引整体替换：动作目标可能已失效，先退出动作态
            RefreshResults();
        }

        // ---- 结果渲染 ----

        private void RefreshResults()
        {
            if (_actionMode) return; // 动作面板开启期间不重刷结果列表（面板内有独立过滤）
            var query = SearchBox.Text;
            var empty = string.IsNullOrWhiteSpace(query);
            if (empty)
            {
                SetListVisible(false);
                return;
            }

            var results = Index != null ? Index.Filter(query) : new List<AppEntry>();
            _generation++;
            _items.Clear();
            foreach (var entry in results)
            {
                var vm = new ItemVM { Entry = entry };
                _items.Add(vm);
            }

            // 内置命令入口（M4 工具入口）排在应用之前：输 vsc 回车即切到工具视图
            var command = BuiltinCommands.Match(query);
            if (command != null) _items.Insert(0, new ItemVM { Entry = command });

            PART_List.ItemsSource = null;
            PART_List.ItemsSource = _items;
            SetSelected(_items.Count > 0 ? 0 : -1, scrollIntoView: false);
            UpdateBadges(FirstVisibleIndex());

            EmptyHint.Text = Index != null && Index.Scanning && Index.Apps.Count == 0
                ? "正在扫描应用…"
                : "无匹配应用";
            EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SetListVisible(true);

            // 图标：内存命中即用，未命中后台加载（换代校验防止旧结果回调污染新列表）
            var generation = _generation;
            foreach (var vm in _items)
            {
                if (vm.Icon != null) continue;
                var cached = Index != null ? Index.Icons.GetCached(vm.Entry.IconPath) : null;
                if (cached != null) { vm.Icon = cached; continue; }
                if (Index == null || vm.Entry.IconPath.Length == 0) continue;
                var captured = vm;
                Index.Icons.LoadAsync(vm.Entry.IconPath, delegate(string path, BitmapImage image)
                {
                    Dispatcher.Invoke(delegate
                    {
                        if (generation == _generation && image != null) captured.Icon = image;
                    });
                });
            }
        }

        private void SetListVisible(bool visible)
        {
            ListArea.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            UpdateHintBar(); // 提示条随列表显隐联动（空态整条隐藏）
            if (!visible)
            {
                PART_List.ItemsSource = null;
                _items.Clear();
                _generation++;
                EmptyHint.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>底部提示条联动：列表可见时显示；「操作」段仅当前选中条目有可用动作时显示
        /// （M4 工具可声明零动作 → 只显示「打开 ↵」）；动作面板打开期间整条隐藏避免重叠。</summary>
        private void UpdateHintBar()
        {
            var listVisible = !_actionMode && ListArea.Visibility == Visibility.Visible;
            HintBar.Visibility = listVisible ? Visibility.Visible : Visibility.Collapsed;
            if (!listVisible) return;
            var hasActions = false;
            var sel = SelectedIndex();
            if (sel >= 0 && sel < _items.Count) hasActions = AvailableActionCount(_items[sel].Entry) > 0;
            ActionsHint.Visibility = hasActions ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>目标条目可用的动作数（无过滤查询）。动作面板与底部提示条共用同一张动作表，
        /// M4 工具接入时按条目类型裁剪即可，两处自动一致。</summary>
        private static int AvailableActionCount(AppEntry target)
        {
            return BuildAppActions(target).Count;
        }

        /// <summary>应用条目的动作表（v1 createAppActions 七项 + 移除登记，2026-10-30 用户确认）。</summary>
        private static List<ActionItem> BuildAppActions(AppEntry target)
        {
            // 内置命令条目没有应用动作（工具自带动作面板，M4）；提示条据此自动不显示「操作」段
            if (target == null || target.IsCommand) return new List<ActionItem>();

            var items = new List<ActionItem>
            {
                new ActionItem("open",           "打开应用",               "O"),
                new ActionItem("show-folder",    "打开所在目录",           "F"),
                new ActionItem("copy-name",      "复制应用名",             "N"),
                new ActionItem("copy-path",      "复制应用路径",           "P"),
                new ActionItem("copy-dir",       "复制应用目录路径",       "D"),
                new ActionItem("cmd",            "在 CMD 打开目录",        "T"),
                new ActionItem("powershell",     "在 PowerShell 打开目录", "S")
            };
            // 第 8 项仅登记条目显示（v1 无手动移除出口，本项补体验缺口）
            if (target != null && target.IsCustom) items.Add(new ActionItem("remove-custom", "移除登记", "R"));
            return items;
        }

        /// <summary>鼠标点底部「操作 Ctrl+Enter」键帽 = 键盘 Ctrl+Enter，呼出动作面板。
        /// 统一在根级隧道事件里做 hit test：直连子元素事件在焦点/捕获场景可能收不到 MouseUp。</summary>
        private void OnViewPreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!ActionsHint.IsVisible) return;
            var src = e.OriginalSource as DependencyObject;
            while (src != null && !ReferenceEquals(src, ActionsHint)) src = VisualTreeHelper.GetParent(src);
            if (ReferenceEquals(src, ActionsHint)) OnActionsHintClick(sender, e);
        }

        private void OnActionsHintClick(object sender, MouseButtonEventArgs e)
        {
            if (_actionMode || _items.Count == 0) return;
            EnterActionMode(_items[SelectedIndex()].Entry);
        }

        // ---- 动作模式（Ctrl+Enter 动作面板：独立浮层组件 Views/ActionPanel）----

        private void EnterActionMode(AppEntry target)
        {
            var items = BuildAppActions(target);
            if (items.Count == 0) return; // 无可用动作（如内置命令条目）不弹面板
            _actionMode = true;
            _actionTarget = target;
            UpdateHintBar(); // 面板浮于提示条上方，打开期间整条隐藏
            Actions.PageStep = PageStep;
            Actions.Show(RootBorder, items); // 定位/过滤/键位由组件内部负责
        }

        /// <summary>动作面板选中执行：交给 App 层（执行、toast、收起统一在那里处理）。</summary>
        private void OnActionInvoked(string actionId)
        {
            if (!_actionMode) return;
            var handler = ActionRequested;
            if (handler != null) handler(_actionTarget, actionId);
        }

        /// <summary>回车/数字/点击的统一执行入口：列表态一律启动条目（动作面板有独立入口）。</summary>
        private void ActivateAt(int index)
        {
            LaunchAt(index);
        }

        // ---- 选择与键位（L6 全量）----

        private void OnViewPreviewKeyDown(object sender, KeyEventArgs e)
        {
            // 动作面板态：键位策略集中在面板组件（聚焦在 Popup 内时面板自己也会收到同一份 HandleKey）
            if (_actionMode)
            {
                if (Actions.HandleKey(e)) e.Handled = true;
                return;
            }

            var count = _items.Count;
            var listVisible = ListArea.Visibility == Visibility.Visible;
            var isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

            if (e.Key == Key.Down || (isCtrl && e.Key == Key.J))
            {
                if (listVisible && count > 0) { SetSelected(Math.Min(SelectedIndex() + 1, count - 1)); e.Handled = true; }
                return;
            }
            if (e.Key == Key.Up || (isCtrl && e.Key == Key.K))
            {
                if (listVisible && count > 0) { SetSelected(Math.Max(SelectedIndex() - 1, 0)); e.Handled = true; }
                return;
            }
            if (isCtrl && e.Key == Key.N)
            {
                if (listVisible && count > 0) { SetSelected(Math.Min(SelectedIndex() + Math.Max(1, PageStep), count - 1)); e.Handled = true; }
                return;
            }
            if (isCtrl && e.Key == Key.P)
            {
                if (listVisible && count > 0) { SetSelected(Math.Max(SelectedIndex() - Math.Max(1, PageStep), 0)); e.Handled = true; }
                return;
            }
            if (isCtrl && e.Key == Key.Enter)
            {
                // Ctrl+Enter：动作面板开关（此处只负责「开」，关闭由面板的 HandleKey 处理）
                if (listVisible && count > 0) EnterActionMode(_items[SelectedIndex()].Entry);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Enter)
            {
                if (listVisible && SelectedIndex() >= 0) { ActivateAt(SelectedIndex()); e.Handled = true; }
                return;
            }
            if (!isCtrl && (e.Key >= Key.D0 && e.Key <= Key.D9 || e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9))
            {
                // 数字键 1-0 映射当前可视区第 1-10 条（0 = 第 10 个），滚到哪都对可见行生效；无结果时放行给搜索框
                if (listVisible && count > 0)
                {
                    var digit = (int)((e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9)
                        ? e.Key - Key.NumPad0
                        : e.Key - Key.D0);
                    var position = digit == 0 ? 9 : digit - 1;
                    var index = FirstVisibleIndex() + position;
                    if (index < count) { ActivateAt(index); }
                    e.Handled = true; // 有结果时数字键不进搜索框（v1 语义）
                }
            }
        }

        private int SelectedIndex()
        {
            var index = PART_List.SelectedIndex;
            return index < 0 ? 0 : index;
        }

        private void SetSelected(int index, bool scrollIntoView = true)
        {
            PART_List.SelectedIndex = index;
            UpdateHintBar(); // 选中条目变了，可用动作可能不同 → 「操作」提示段联动
            if (scrollIntoView && index >= 0 && index < _items.Count)
            {
                PART_List.ScrollIntoView(_items[index]);
                // 兜底：ScrollIntoView 引发的滚动若错过 ScrollChanged 时机，
                // 排一次布局后优先级重算角标（键盘步进路径的正确性保证）
                Dispatcher.BeginInvoke(new Action(delegate { UpdateBadges(FirstVisibleIndex()); }),
                    DispatcherPriority.Loaded);
            }
        }

        private void LaunchAt(int index)
        {
            if (index < 0 || index >= _items.Count) return;
            var handler = LaunchRequested;
            if (handler != null) handler(_items[index].Entry);
        }

        private void OnListClick(object sender, MouseButtonEventArgs e)
        {
            var item = e.OriginalSource as DependencyObject;
            while (item != null && !(item is ListBoxItem)) item = VisualTreeHelper.GetParent(item);
            var container = item as ListBoxItem;
            if (container == null) return;
            var index = PART_List.ItemContainerGenerator.IndexFromContainer(container);
            if (index >= 0) { ActivateAt(index); e.Handled = true; }
        }

        // ---- 可视区角标（规则见 ListBadgeHelper，与剪贴板列表共用同一份实现）----

        /// <summary>第一个可见条目的索引（「露出 ≥60%」判定，不按行高估算）。</summary>
        private int FirstVisibleIndex()
        {
            return ListBadgeHelper.FirstVisibleIndex(PART_List, _scroll, _items.Count);
        }

        private void OnListScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_scroll == null) _scroll = ListBadgeHelper.FindScroll(PART_List);
            UpdateBadges(FirstVisibleIndex());
        }

        private void UpdateBadges(int firstVisible)
        {
            ListBadgeHelper.UpdateBadges(_items, firstVisible);
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            // Border.CornerRadius 只圆自身背景，不裁切子内容——搜索栏白底方角会伸出
            // 圆角之外并在窗外投出暗色伪影（实测踩过）。对内容网格施加同半径圆角裁切。
            var radius = 8.0;
            if (TryFindResource("Radius.Window") is CornerRadius corner)
                radius = corner.TopLeft;
            if (radius > 0 && ContentGrid.ActualWidth > 0)
            {
                ContentGrid.Clip = new RectangleGeometry(
                    new Rect(0, 0, ContentGrid.ActualWidth, ContentGrid.ActualHeight), radius, radius);
            }
        }
    }
}
