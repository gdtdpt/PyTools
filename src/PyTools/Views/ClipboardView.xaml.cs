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
    /// <summary>搜索框焦点接口：呼出壳不关心内容视图的具体类型。</summary>
    public interface ISearchFocusable
    {
        void FocusSearch();
    }

    /// <summary>
    /// 剪贴板条目视图模型。缩略图走 getter 懒加载——ListBox 虚拟化下只有已实现的容器会读取绑定，
    /// 100 条历史不会全量解码（配合 ClipboardHistory 内的按 id 缓存，重复读取零成本）。
    /// </summary>
    public sealed class ClipItemVM : INotifyPropertyChanged, IBadgedItem
    {
        private readonly ClipboardHistory _history;
        private string _badge = "";
        private BitmapSource _thumb;
        private bool _thumbTried;

        public ClipItemVM(ClipRecord record, ClipboardHistory history)
        {
            Record = record;
            _history = history;
        }

        public ClipRecord Record { get; private set; }

        public bool IsImage { get { return Record.IsImage; } }
        public string Title { get { return Record.Preview; } }
        public string Glyph { get { return Record.Glyph; } }

        public string Subtitle
        {
            get
            {
                if (Record.IsImage) EnsureThumb(); // 图片尺寸要等缩略图解码后才知道
                return Record.Subtitle;
            }
        }

        public BitmapSource Thumb
        {
            get { EnsureThumb(); return _thumb; }
        }

        private void EnsureThumb()
        {
            if (_thumbTried) return;
            _thumbTried = true;
            _thumb = _history != null ? _history.GetThumbnail(Record) : null;
            if (_thumb != null) Notify("Subtitle"); // 尺寸回填后刷新副标题
        }

        public string Badge
        {
            get { return _badge; }
            set { if (_badge != value) { _badge = value; Notify("Badge"); } }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Notify(string name)
        {
            if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// 剪贴板视图（M3，C2/S9）：过滤、锚定可视区角标、选择回填。
    /// 与应用列表共用呼出壳与令牌，但语义不同：空查询显示全部记录。
    /// </summary>
    public partial class ClipboardView : UserControl, ISearchFocusable
    {
        private readonly List<ClipItemVM> _items = new List<ClipItemVM>();
        private ClipboardHistory _history;
        private ScrollViewer _scroll;

        public ClipboardView()
        {
            InitializeComponent();
            PART_List.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnListScrollChanged));
            PART_List.PreviewMouseLeftButtonUp += OnListClick;
            PART_SearchBox.TextChanged += delegate { RefreshList(); };
            PreviewKeyDown += OnViewPreviewKeyDown;
            Loaded += delegate
            {
                _scroll = ListBadgeHelper.FindScroll(PART_List);
                // 列表首次布局完成后重算角标（虚拟化容器的位置只有排过版才准确）
                Dispatcher.BeginInvoke(new Action(delegate { RecalcBadges(); }), DispatcherPriority.Loaded);
            };
        }

        /// <summary>剪辑板历史（App 注入）。</summary>
        public ClipboardHistory History
        {
            get { return _history; }
            set { _history = value; }
        }

        /// <summary>Ctrl+N/P 步进条数（v2.pageStep）。</summary>
        public int PageStep { get; set; }

        /// <summary>选中某条记录（App 负责收起窗口、恢复焦点、写回剪贴板）。</summary>
        public event Action<ClipRecord> SelectRequested;

        public TextBox SearchBox { get { return PART_SearchBox; } }

        public void FocusSearch()
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }

        /// <summary>呼出时重置：清空查询并重建列表（空查询 = 全部记录）。</summary>
        public void ResetForClipboard()
        {
            PART_SearchBox.Text = "";
            RefreshList();
            Dispatcher.BeginInvoke(new Action(delegate { RecalcBadges(); }), DispatcherPriority.Loaded);
        }

        /// <summary>历史有新记录时刷新（窗口可见时才刷）。</summary>
        public void OnHistoryChanged()
        {
            if (!IsVisible) return;
            RefreshList();
        }

        // ---- 渲染 ----

        private void RefreshList()
        {
            if (_history == null) return;
            var query = (PART_SearchBox.Text ?? "").Trim();
            _items.Clear();
            foreach (var record in _history.Records)
            {
                if (query.Length > 0 && !Matches(record, query)) continue;
                _items.Add(new ClipItemVM(record, _history));
            }

            PART_List.ItemsSource = null;
            PART_List.ItemsSource = _items;
            SetSelected(_items.Count > 0 ? 0 : -1, scrollIntoView: false);
            RecalcBadges();

            EmptyHint.Text = _history.Records.Count == 0 ? "暂无剪贴板记录" : "没有匹配的记录";
            EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>过滤：文本匹配全文，文件匹配文件名与路径；图片没有可搜文本，不参与匹配（v1 语义）。</summary>
        private static bool Matches(ClipRecord record, string query)
        {
            if (record.Kind == ClipKind.Text)
                return record.Text != null && record.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
            if (record.Kind == ClipKind.Files)
            {
                foreach (var file in record.Files)
                {
                    if (file.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                    if (file.Path.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
            }
            return false;
        }

        // ---- 选择与键位 ----

        private void OnViewPreviewKeyDown(object sender, KeyEventArgs e)
        {
            var count = _items.Count;
            var isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

            if (e.Key == Key.Down || (isCtrl && e.Key == Key.J))
            {
                if (count > 0) { SetSelected(Math.Min(SelectedIndex() + 1, count - 1)); e.Handled = true; }
                return;
            }
            if (e.Key == Key.Up || (isCtrl && e.Key == Key.K))
            {
                if (count > 0) { SetSelected(Math.Max(SelectedIndex() - 1, 0)); e.Handled = true; }
                return;
            }
            if (isCtrl && e.Key == Key.N)
            {
                if (count > 0) { SetSelected(Math.Min(SelectedIndex() + Math.Max(1, PageStep), count - 1)); e.Handled = true; }
                return;
            }
            if (isCtrl && e.Key == Key.P)
            {
                if (count > 0) { SetSelected(Math.Max(SelectedIndex() - Math.Max(1, PageStep), 0)); e.Handled = true; }
                return;
            }
            if (e.Key == Key.Enter)
            {
                if (SelectedIndex() >= 0 && count > 0) { ActivateAt(SelectedIndex()); e.Handled = true; }
                return;
            }
            if (!isCtrl && (e.Key >= Key.D0 && e.Key <= Key.D9 || e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9))
            {
                // 数字键 1-0 映射当前可视区第 1-10 条（0 = 第 10 个），与应用列表同一条规则
                if (count > 0)
                {
                    var digit = (int)((e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9)
                        ? e.Key - Key.NumPad0
                        : e.Key - Key.D0);
                    var position = digit == 0 ? 9 : digit - 1;
                    var index = ListBadgeHelper.FirstVisibleIndex(PART_List, _scroll, count) + position;
                    if (index < count) ActivateAt(index);
                    e.Handled = true;
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
                Dispatcher.BeginInvoke(new Action(delegate { RecalcBadges(); }), DispatcherPriority.Loaded);
            }
        }

        private void ActivateAt(int index)
        {
            if (index < 0 || index >= _items.Count) return;
            var handler = SelectRequested;
            if (handler != null) handler(_items[index].Record);
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

        // ---- 角标 ----

        private void OnListScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_scroll == null) _scroll = ListBadgeHelper.FindScroll(PART_List);
            RecalcBadges();
        }

        private void RecalcBadges()
        {
            ListBadgeHelper.UpdateBadges(_items, ListBadgeHelper.FirstVisibleIndex(PART_List, _scroll, _items.Count));
        }

        // ---- 圆角裁切（与 SummonView 同因：Border.CornerRadius 不裁子内容，搜索栏白底会伸出圆角）----

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            var radius = 8.0;
            if (TryFindResource("Radius.Window") is CornerRadius corner) radius = corner.TopLeft;
            if (radius > 0 && ContentGrid.ActualWidth > 0)
            {
                ContentGrid.Clip = new RectangleGeometry(
                    new Rect(0, 0, ContentGrid.ActualWidth, ContentGrid.ActualHeight), radius, radius);
            }
        }
    }
}
