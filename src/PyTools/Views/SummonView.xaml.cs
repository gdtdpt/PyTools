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
        /// <summary>操作菜单动作定义（v1 createAppActions 七项 + 移除登记，2026-10-30 用户确认）。</summary>
        private static readonly string[][] ActionDefs = new[]
        {
            new[] { "open",         "打开应用",             "" },
            new[] { "show-folder",  "打开所在目录",         "" },
            new[] { "copy-name",    "复制应用名",           "" },
            new[] { "copy-path",    "复制应用路径",         "" },
            new[] { "copy-dir",     "复制应用目录路径",     "" },
            new[] { "cmd",          "在 CMD 打开目录",      "" },
            new[] { "powershell",   "在 PowerShell 打开目录", "" },
            new[] { "remove-custom","移除登记",             "custom" } // 仅登记条目显示
        };
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
            PART_ActionFilter.TextChanged += delegate { if (_actionMode) RefreshActions(); };
            PreviewKeyDown += OnViewPreviewKeyDown;
            PreviewMouseLeftButtonUp += OnViewPreviewMouseUp; // 底部「操作」键帽的鼠标呼出（根级 hit test）
            ActionsCard.SizeChanged += delegate { UpdatePopupOffsets(); }; // 过滤增减行时保持底边锚定
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
            ActionsPopup.IsOpen = false;
            PART_ActionFilter.Text = "";
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
            if (_actionMode) RefreshActions();
            else RefreshResults();
        }

        // ---- 结果渲染 ----

        private void RefreshResults()
        {
            if (_actionMode) { RefreshActions(); return; } // 保险：操作模式下不重刷结果列表
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
            PART_List.ItemsSource = null;
            PART_List.ItemsSource = _items;
            SetSelected(results.Count > 0 ? 0 : -1, scrollIntoView: false);
            UpdateBadges(FirstVisibleIndex());

            EmptyHint.Text = Index != null && Index.Scanning && Index.Apps.Count == 0
                ? "正在扫描应用…"
                : "无匹配应用";
            EmptyHint.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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

        /// <summary>目标条目可用的动作数（无过滤查询）。动作面板与底部提示条共用同一判定，
        /// M4 工具接入时在此处按条目类型裁剪即可，两处自动一致。</summary>
        private static int AvailableActionCount(AppEntry target)
        {
            var n = 0;
            foreach (var def in ActionDefs)
                if (def[2] != "custom" || (target != null && target.IsCustom)) n++;
            return n;
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

        // ---- 操作模式（Ctrl+Enter 动作面板，Raycast 式 POC）----

        private sealed class ActionVM
        {
            public string Title { get; set; }
            public string Hint { get; set; } // 行尾单键提示：空查询时按下即执行
        }

        // 与 ActionDefs 顺序一一对应，各字母互不重复
        private static readonly string[] ActionHints = { "O", "F", "N", "P", "D", "T", "S", "R" };

        private readonly List<ActionVM> _actionRows = new List<ActionVM>();
        private readonly List<string> _actionIds = new List<string>(); // 与 _actionRows 平行：过滤后的动作 id
        private int _actionSel;

        private void EnterActionMode(AppEntry target)
        {
            _actionMode = true;
            _actionTarget = target;
            UpdateHintBar(); // 面板浮于提示条上方，打开期间整条隐藏
            PART_ActionFilter.Text = "";
            RefreshActions();
            ActionsPopup.IsOpen = true;
            UpdatePopupOffsets();
            PART_ActionFilter.Focus(); // 焦点移入面板过滤框（Raycast 式），主搜索框原样保留
        }

        /// <summary>动作面板右下角贴窗（留 12px 边距）。面板是独立 Popup、可伸出窗外，
        /// 高度与主列表无关；卡片尺寸变化（过滤增减行）时重算，保持底边锚定。</summary>
        private void UpdatePopupOffsets()
        {
            if (!_actionMode) return;
            ActionsPopup.HorizontalOffset = Math.Max(0, RootBorder.ActualWidth - 12 - ActionsCard.ActualWidth);
            ActionsPopup.VerticalOffset = Math.Max(0, RootBorder.ActualHeight - 12 - ActionsCard.ActualHeight);
        }

        private void RefreshActions()
        {
            var query = (PART_ActionFilter.Text ?? "").Trim();
            _actionRows.Clear();
            _actionIds.Clear();
            for (var i = 0; i < ActionDefs.Length; i++)
            {
                var def = ActionDefs[i];
                var id = def[0];
                var title = def[1];
                if (def[2] == "custom" && (_actionTarget == null || !_actionTarget.IsCustom)) continue;
                // 标题与 id 双匹配：标题是中文（复制应用名），输英文 "copy" 也能靠 id（copy-name）命中
                if (query.Length > 0
                    && title.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0
                    && id.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;

                _actionRows.Add(new ActionVM { Title = title, Hint = ActionHints[i] });
                _actionIds.Add(id);
            }
            PART_Actions.ItemsSource = null;
            PART_Actions.ItemsSource = _actionRows;
            SetActionSelected(0);

            NoActionHint.Visibility = _actionRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetActionSelected(int index)
        {
            if (_actionRows.Count == 0) { _actionSel = -1; return; }
            _actionSel = Math.Max(0, Math.Min(index, _actionRows.Count - 1));
            PART_Actions.SelectedIndex = _actionSel;
            PART_Actions.ScrollIntoView(_actionRows[_actionSel]);
        }

        private void ExecuteActionAt(int index)
        {
            if (!_actionMode || index < 0 || index >= _actionIds.Count) return;
            var handler = ActionRequested;
            if (handler != null) handler(_actionTarget, _actionIds[index]);
        }

        private void OnActionClick(object sender, MouseButtonEventArgs e)
        {
            var item = e.OriginalSource as DependencyObject;
            while (item != null && !(item is ListBoxItem)) item = VisualTreeHelper.GetParent(item);
            var container = item as ListBoxItem;
            if (container == null) return;
            var index = PART_Actions.ItemContainerGenerator.IndexFromContainer(container);
            if (index >= 0) { ExecuteActionAt(index); e.Handled = true; }
        }

        /// <summary>回车/数字/点击的统一执行入口：按模式分流。</summary>
        private void ActivateAt(int index)
        {
            if (_actionMode) ExecuteActionAt(index);
            else LaunchAt(index);
        }

        // ---- 选择与键位（L6 全量）----

        private void OnViewPreviewKeyDown(object sender, KeyEventArgs e)
        {
            var count = _items.Count;
            var listVisible = ListArea.Visibility == Visibility.Visible;
            var isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

            if (e.Key == Key.Down || (isCtrl && e.Key == Key.J))
            {
                // 动作面板打开期间，所有移动键（含习惯键 Ctrl+J/K、步进键 Ctrl+N/P）都驱动面板
                if (_actionMode) { SetActionSelected(_actionSel + 1); e.Handled = true; }
                else if (listVisible && count > 0) { SetSelected(Math.Min(SelectedIndex() + 1, count - 1)); e.Handled = true; }
                return;
            }
            if (e.Key == Key.Up || (isCtrl && e.Key == Key.K))
            {
                if (_actionMode) { SetActionSelected(_actionSel - 1); e.Handled = true; }
                else if (listVisible && count > 0) { SetSelected(Math.Max(SelectedIndex() - 1, 0)); e.Handled = true; }
                return;
            }
            if (isCtrl && e.Key == Key.N)
            {
                if (_actionMode) { SetActionSelected(_actionSel + Math.Max(1, PageStep)); e.Handled = true; }
                else if (listVisible && count > 0) { SetSelected(Math.Min(SelectedIndex() + Math.Max(1, PageStep), count - 1)); e.Handled = true; }
                return;
            }
            if (isCtrl && e.Key == Key.P)
            {
                if (_actionMode) { SetActionSelected(_actionSel - Math.Max(1, PageStep)); e.Handled = true; }
                else if (listVisible && count > 0) { SetSelected(Math.Max(SelectedIndex() - Math.Max(1, PageStep), 0)); e.Handled = true; }
                return;
            }
            if (isCtrl && e.Key == Key.Enter)
            {
                // Ctrl+Enter：动作面板全局开关（开 → 关；关 → 开，需有选中条目）
                if (_actionMode) ExitActionMode();
                else if (listVisible && count > 0) EnterActionMode(_items[SelectedIndex()].Entry);
                e.Handled = true;
                return;
            }
            if (_actionMode)
            {
                // 动作面板态：移动键已在上方统一接管；这里只管回车执行、Tab 退出、单键直执行
                if (e.Key == Key.Enter) { ExecuteActionAt(_actionSel); e.Handled = true; return; }
                if (e.Key == Key.Tab) { ExitActionMode(); e.Handled = true; return; }
                if (!isCtrl && e.Key >= Key.A && e.Key <= Key.Z)
                {
                    // 过滤为空且字母命中行尾提示键 → 直执行（Raycast quick key）；
                    // 未命中的字母放行给面板过滤框（否则想输 "copy" 的 c 会被吞掉）
                    var letter = ((char)('A' + (e.Key - Key.A))).ToString();
                    var idx = _actionRows.FindIndex(r => r.Hint == letter);
                    if ((PART_ActionFilter.Text ?? "").Trim().Length == 0 && idx >= 0)
                    {
                        ExecuteActionAt(idx);
                        e.Handled = true;
                    }
                    return;
                }
                return; // 其余按键（数字/字母/标点）交面板过滤框正常输入
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
