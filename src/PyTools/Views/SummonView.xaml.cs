using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    public sealed class ItemVM : INotifyPropertyChanged
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

    public partial class SummonView : UserControl
    {
        private readonly List<ItemVM> _items = new List<ItemVM>();
        private readonly DispatcherTimer _toastTimer;
        private int _generation; // 结果集换代号：图标异步回调过期即弃
        private ScrollViewer _scroll;

        public SummonView()
        {
            InitializeComponent();
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
            _toastTimer.Tick += delegate { Toast.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };
            PART_List.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnListScrollChanged));
            PART_List.PreviewMouseLeftButtonUp += OnListClick;
            SearchBox.TextChanged += delegate { RefreshResults(); };
            PreviewKeyDown += OnViewPreviewKeyDown;
            Loaded += delegate { _scroll = FindScroll(PART_List); };
        }

        /// <summary>索引（App 注入）。null 时搜索无结果。</summary>
        public AppIndex Index { get; set; }

        /// <summary>Ctrl+N/P 步进条数（v2.pageStep）。</summary>
        public int PageStep { get; set; }

        public event Action<AppEntry> LaunchRequested;

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
            SearchBox.Text = "";
            SetListVisible(false);
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
            if (IsVisible) RefreshResults();
        }

        // ---- 结果渲染 ----

        private void RefreshResults()
        {
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
            if (e.Key == Key.Enter)
            {
                if (listVisible && SelectedIndex() >= 0) { LaunchAt(SelectedIndex()); e.Handled = true; }
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
                    if (index < count) { LaunchAt(index); }
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
            if (index >= 0) { LaunchAt(index); e.Handled = true; }
        }

        // ---- 可视区角标（数字锚定可视区：滚动后对可见行重编号，任意位置每行都有号可按）----

        /// <summary>第一个可见条目的索引。不按行高估算（行高含边距/DPI 缩放，估算必偏），
        /// 而是从虚拟化面板已实现的容器里找「露出 ≥60%」的最小索引——只露一条缝的行不算，
        /// 保证 1 号永远落在用户眼中完整的第一行。</summary>
        private int FirstVisibleIndex()
        {
            if (_scroll == null || _items.Count == 0) return 0;
            var generator = PART_List.ItemContainerGenerator;
            for (var i = 0; i < _items.Count; i++)
            {
                var container = generator.ContainerFromIndex(i) as FrameworkElement;
                if (container == null) continue; // 虚拟化未实现（视口之外）
                double top;
                try { top = container.TransformToVisual(_scroll).Transform(new Point(0, 0)).Y; }
                catch (InvalidOperationException) { continue; }
                if (top > -container.ActualHeight * 0.4) return i;
            }
            return 0;
        }

        private void OnListScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_scroll == null) _scroll = FindScroll(PART_List);
            UpdateBadges(FirstVisibleIndex());
        }

        private void UpdateBadges(int firstVisible)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                var offset = i - firstVisible;
                if (offset >= 0 && offset <= 9)
                {
                    _items[i].Badge = offset == 9 ? "0" : (offset + 1).ToString();
                }
                else
                {
                    _items[i].Badge = "";
                }
            }
        }

        private static ScrollViewer FindScroll(DependencyObject root)
        {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var scroll = child as ScrollViewer;
                if (scroll != null) return scroll;
                scroll = FindScroll(child);
                if (scroll != null) return scroll;
            }
            return null;
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
