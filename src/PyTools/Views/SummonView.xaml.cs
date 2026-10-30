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
        private bool _actionMode;      // Tab 操作模式：列表内容 = 当前条目的动作集
        private AppEntry _actionTarget; // 操作模式的目标条目

        public SummonView()
        {
            InitializeComponent();
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
            _toastTimer.Tick += delegate { Toast.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };
            PART_List.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnListScrollChanged));
            PART_List.PreviewMouseLeftButtonUp += OnListClick;
            SearchBox.TextChanged += delegate { if (_actionMode) RefreshActions(); else RefreshResults(); };
            PreviewKeyDown += OnViewPreviewKeyDown;
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

        /// <summary>Esc / Tab 由 App 转问：操作模式先退回结果列表而非藏窗。返回是否已消费。</summary>
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
            ModeBadge.Visibility = Visibility.Collapsed;
            PART_Placeholder.Text = "搜索应用…";
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
            if (!visible)
            {
                PART_List.ItemsSource = null;
                _items.Clear();
                _generation++;
                EmptyHint.Visibility = Visibility.Collapsed;
            }
        }

        // ---- 操作模式（Tab，L5）----

        private readonly List<string> _actionIds = new List<string>();

        private void EnterActionMode(AppEntry target)
        {
            _actionMode = true;
            _actionTarget = target;
            ModeBadge.Visibility = Visibility.Visible;
            PART_Placeholder.Text = "过滤操作…（Tab/Esc 返回）";
            SearchBox.Text = "";
            RefreshActions();
        }

        private void RefreshActions()
        {
            var query = (SearchBox.Text ?? "").Trim();
            _generation++;
            _items.Clear();
            _actionIds.Clear();
            foreach (var def in ActionDefs)
            {
                var id = def[0];
                var title = def[1];
                if (def[2] == "custom" && (_actionTarget == null || !_actionTarget.IsCustom)) continue;
                if (query.Length > 0 && title.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;

                // 动作条目复用 ItemVM 渲染管线（角标/键位/滚动全复用）：Name=标题、PathText=描述（v1 对齐）
                var targetFile = !string.IsNullOrEmpty(_actionTarget.TargetPath) ? _actionTarget.TargetPath : _actionTarget.Path;
                string description;
                switch (id)
                {
                    case "open":
                    case "copy-name":
                        description = _actionTarget.Name;
                        break;
                    case "copy-dir":
                    case "cmd":
                    case "powershell":
                        description = !string.IsNullOrEmpty(_actionTarget.TargetDir)
                            ? _actionTarget.TargetDir
                            : Path.GetDirectoryName(targetFile);
                        break;
                    case "remove-custom":
                        description = "从应用列表删除此登记";
                        break;
                    default: // show-folder / copy-path
                        description = targetFile;
                        break;
                }
                _items.Add(new ItemVM { Entry = new AppEntry { Name = title, TargetPath = description, Path = description } });
                _actionIds.Add(id);
            }
            PART_List.ItemsSource = null;
            PART_List.ItemsSource = _items;
            SetSelected(_items.Count > 0 ? 0 : -1, scrollIntoView: false);
            UpdateBadges(0);

            EmptyHint.Text = _actionTarget == null ? "没有可用操作" : "没有匹配的操作";
            EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SetListVisible(true);
        }

        private void ExecuteActionAt(int index)
        {
            if (!_actionMode || index < 0 || index >= _actionIds.Count) return;
            var handler = ActionRequested;
            if (handler != null) handler(_actionTarget, _actionIds[index]);
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
            if (e.Key == Key.Tab)
            {
                if (_actionMode)
                {
                    ExitActionModeInternal();
                    RefreshResults();
                    e.Handled = true;
                }
                else if (listVisible && count > 0)
                {
                    EnterActionMode(_items[SelectedIndex()].Entry);
                    e.Handled = true;
                }
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
