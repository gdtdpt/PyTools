using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PyTools.Core;

namespace PyTools.Views
{
    /// <summary>
    /// 文件差异对比（M4，2026-11-04 设计拍板）：v2 首个独立常驻窗口。
    /// 形态：常驻桌面（独立 HWND 失焦不隐藏、任务栏有图标）、可拖拽可 resize、每次打开回默认居中
    /// （关闭 = 一次工作结束，不记忆位置）、Esc 关闭。
    /// 装载三入口（无按钮）：拖入文件 / 单击文件槽弹选择框 / 鼠标悬停该侧按 Ctrl+V（粘贴跟随悬停侧，
    /// 不抢键盘焦点——点击已被定义为「弹选择框」，两者靠悬停解耦）。
    /// 比较触发：首次双侧就绪自动比较；之后切换文件只清结果并提示手动（Ctrl+Enter → 开始比较）。
    /// 结果区每侧 = 46px 行号冻结列 + 内容列（2026-11-04 拍板）：横滚只滚内容，行号钉在左缘；
    /// 垂直滚动四个列表互相同步，水平只在两个内容列之间互写。
    /// </summary>
    public partial class DiffWindow : Window
    {
        private const string FileFilter =
            "常见文本文件|*.txt;*.json;*.xml;*.js;*.ts;*.css;*.html;*.htm;*.md;*.log;*.ini;*.yaml;*.yml;*.cmd;*.bat;*.ps1;*.conf;*.config"
            + "|所有文件|*.*";

        private readonly Brush _slotNormalBorder;
        private readonly Brush _slotHotBorder;
        private readonly Brush _slotInvalidBorder;

        private string _leftPath, _rightPath;
        private string _leftText, _rightText;
        private string _hoverSide;                 // "left" / "right" / null
        private bool _autoComparePending = true;   // 首次双侧就绪自动比较；比较过一次后改手动
        private bool _scrollToFirstDiffOnCompare;  // 调试预载时滚动到首个差异行（截图自验）
        private int _taskSeq;                      // 过期结果丢弃（换文件时旧任务可能后返回）
        private ScrollViewer _leftGutterScroll, _leftScroll;    // 左侧：行号冻结列 + 内容列
        private ScrollViewer _rightScroll, _rightGutterScroll;  // 右侧：内容列 + 行号冻结列
        private bool _syncingScroll;

        // ---- 差异跳转（F3/Shift+F3）----
        private DiffResult _result;                // 当前比较结果（跳转按 Rows 定位）
        private List<int> _diffIndexes;            // 差异行索引表（比较完成时构建）
        private DiffRow _focusedRow;               // 当前聚焦行（IsFocused 驱动行号/片段聚焦样式）
        private int _focusedSeq = -1;              // 聚焦行在 _diffIndexes 中的序号（连按 F3 的前进基准）
        private double _charWidth = -1;            // 等宽字符宽（超长行水平定位用，懒测一次）

        private const double RowHeight = 22;       // 与 DiffItemStyle 的行高一致

        public DiffWindow()
        {
            InitializeComponent();
            ApplyDefaultSize();

            _slotNormalBorder = (Brush)FindResource("Border.Input");
            _slotHotBorder = (Brush)FindResource("Accent");
            _slotInvalidBorder = (Brush)FindResource("Text.Error");

            Actions.Invoked += OnActionInvoked;
            Actions.Dismissed += delegate { Root.Focus(); };
            Loaded += OnWindowLoaded;
        }

        /// <summary>默认尺寸（v1 口径）：工作区 65%×75%，钳制在 900×560 ~ 1400×900；位置由 CenterScreen 居中。</summary>
        private void ApplyDefaultSize()
        {
            var workArea = SystemParameters.WorkArea;
            var width = Math.Min(Math.Max(workArea.Width * 0.65, 900), 1400);
            var height = Math.Min(Math.Max(workArea.Height * 0.75, 560), 900);
            if (width > workArea.Width - 16) width = Math.Max(600, workArea.Width - 16);
            if (height > workArea.Height - 16) height = Math.Max(400, workArea.Height - 16);
            Width = width;
            Height = height;
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            // ListBox 模板展开后才能取到内部 ScrollViewer（滚动同步的挂点）；
            // 每侧两个（行号冻结列 + 内容列），共四个
            _leftGutterScroll = FindScrollViewer(LeftGutterList);
            _leftScroll = FindScrollViewer(LeftList);
            _rightScroll = FindScrollViewer(RightList);
            _rightGutterScroll = FindScrollViewer(RightGutterList);
            foreach (var viewer in AllScrolls())
                if (viewer != null) viewer.ScrollChanged += OnAnyScrollChanged;
        }

        // ---- 文件装载（三入口共用链路）----

        private void OnSlotLeftClick(object sender, MouseButtonEventArgs e)
        {
            ChooseFile(true);
        }

        private void OnSlotRightClick(object sender, MouseButtonEventArgs e)
        {
            ChooseFile(false);
        }

        private void ChooseFile(bool left)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = left ? "选择左侧比较文件" : "选择右侧比较文件",
                Filter = FileFilter,
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) == true) LoadFile(left, dialog.FileName);
        }

        private void LoadFile(bool left, string path)
        {
            string text;
            string encoding;
            try
            {
                text = TextFileLoader.Read(path, out encoding);
            }
            catch (Exception ex)
            {
                Logger.Error("diff read failed: " + path + " -> " + ex.Message);
                SetStatus("读取文件失败：" + ex.Message, "error");
                return;
            }

            if (left) { _leftPath = path; _leftText = text; }
            else { _rightPath = path; _rightText = text; }
            SetSlot(left, path, encoding);
            ClearResult();
            Logger.Info("diff load " + (left ? "left" : "right") + ": " + path
                        + " (" + encoding + ", " + text.Length + " chars)");

            if (_leftText != null && _rightText != null)
            {
                if (_autoComparePending) Compare();
                else SetStatus("文件已更新，按 Ctrl+Enter 选择开始比较", "warn");
            }
            else
            {
                SetStatus(left ? "已载入左侧文件，请选择右侧文件" : "已载入右侧文件，请选择左侧文件", "info");
            }
        }

        private void SetSlot(bool left, string path, string encoding)
        {
            var nameBlock = left ? LeftName : RightName;
            nameBlock.Text = Path.GetFileName(path);
            nameBlock.Foreground = (Brush)FindResource("Text.Primary");

            var pathBlock = left ? LeftPath : RightPath;
            pathBlock.Text = path;
            pathBlock.Foreground = (Brush)FindResource("Text.Secondary");

            (left ? LeftEnc : RightEnc).Text = encoding;
        }

        private void ClearResult()
        {
            _taskSeq++; // 作废进行中的比较
            LeftList.ItemsSource = null;
            RightList.ItemsSource = null;
            LeftGutterList.ItemsSource = null;
            RightGutterList.ItemsSource = null;
            EmptyHint.Visibility = Visibility.Visible;
            _result = null;
            _diffIndexes = null;
            if (_focusedRow != null) { _focusedRow.IsFocused = false; _focusedRow = null; }
            _focusedSeq = -1;
        }

        // ---- 拖拽 ----

        private void OnSlotDragEnter(object sender, DragEventArgs e)
        {
            OnSlotDragOver(sender, e);
        }

        private void OnSlotDragOver(object sender, DragEventArgs e)
        {
            string path, error;
            var valid = TryGetSingleFile(e.Data, out path, out error);
            e.Effects = valid ? DragDropEffects.Copy : DragDropEffects.None;
            SetSlotBorder(sender as Border, valid ? _slotHotBorder : _slotInvalidBorder);
            e.Handled = true;
        }

        private void OnSlotDragLeave(object sender, DragEventArgs e)
        {
            SetSlotBorder(sender as Border, _slotNormalBorder);
            e.Handled = true;
        }

        private void OnSlotDrop(object sender, DragEventArgs e)
        {
            var slot = sender as Border;
            SetSlotBorder(slot, _slotNormalBorder);
            string path, error;
            if (!TryGetSingleFile(e.Data, out path, out error))
            {
                SetStatus(error, "error");
                e.Handled = true;
                return;
            }
            LoadFile(ReferenceEquals(slot, SlotLeft), path);
            e.Handled = true;
        }

        private static bool TryGetSingleFile(IDataObject data, out string path, out string error)
        {
            path = null;
            error = null;
            if (data == null || !data.GetDataPresent(DataFormats.FileDrop))
            {
                error = "拖入的内容不是文件";
                return false;
            }
            var items = data.GetData(DataFormats.FileDrop) as string[];
            if (items == null || items.Length == 0)
            {
                error = "拖入的内容不是文件";
                return false;
            }
            if (items.Length > 1)
            {
                error = "一次只能拖入一个文件";
                return false;
            }
            if (Directory.Exists(items[0]))
            {
                error = "仅支持文件，不支持文件夹";
                return false;
            }
            if (!File.Exists(items[0]))
            {
                error = "文件不存在：" + items[0];
                return false;
            }
            path = items[0];
            return true;
        }

        private static void SetSlotBorder(Border slot, Brush brush)
        {
            if (slot != null && brush != null) slot.BorderBrush = brush;
        }

        // ---- 粘贴（Ctrl+V 跟随鼠标悬停侧）----

        private void OnSlotMouseEnter(object sender, MouseEventArgs e)
        {
            _hoverSide = ReferenceEquals(sender, SlotLeft) ? "left" : "right";
        }

        private void OnSlotMouseLeave(object sender, MouseEventArgs e)
        {
            var side = ReferenceEquals(sender, SlotLeft) ? "left" : "right";
            if (_hoverSide == side) _hoverSide = null;
        }

        private void PasteToHoverSide()
        {
            if (_hoverSide == null)
            {
                SetStatus("将鼠标移到左侧或右侧文件框上再按 Ctrl+V", "warn");
                return;
            }
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    var files = Clipboard.GetFileDropList();
                    if (files != null && files.Count > 0)
                    {
                        LoadFile(_hoverSide == "left", files[0]);
                        if (files.Count > 1) SetStatus("剪贴板有多个文件，已取第一个", "warn");
                        return;
                    }
                }
                if (Clipboard.ContainsText())
                {
                    SetStatus("剪贴板为文本内容，仅支持文件（在资源管理器中复制文件后再粘贴）", "warn");
                    return;
                }
                SetStatus("剪贴板没有可用的文件", "warn");
            }
            catch (Exception ex)
            {
                SetStatus("读取剪贴板失败：" + ex.Message, "error");
            }
        }

        // ---- 比较 ----

        private async void Compare()
        {
            if (_leftText == null || _rightText == null)
            {
                SetStatus("请先选择左右两个文件", "warn");
                return;
            }
            var seq = ++_taskSeq;
            SetStatus("正在计算差异…", "info");

            var leftText = _leftText;
            var rightText = _rightText;
            DiffResult result;
            try
            {
                // 后台线程：大文件 Myers 可能耗时数百毫秒，不能卡 UI（v1 用 Web Worker，WPF 无 worker）
                result = await Task.Run(() => DiffEngine.Compare(leftText, rightText));
            }
            catch (Exception ex)
            {
                if (seq == _taskSeq) SetStatus("比较失败：" + ex.Message, "error");
                return;
            }
            if (seq != _taskSeq) return; // 期间换过文件，丢弃过期结果

            _autoComparePending = false;
            _result = result;
            _diffIndexes = DiffEngine.DiffLineIndexes(result.Rows);
            if (_focusedRow != null) { _focusedRow.IsFocused = false; _focusedRow = null; }
            _focusedSeq = -1;
            LeftList.ItemsSource = result.Rows;
            RightList.ItemsSource = result.Rows;
            LeftGutterList.ItemsSource = result.Rows;
            RightGutterList.ItemsSource = result.Rows;
            EmptyHint.Visibility = result.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SetVerticalAll(0);

            SetStatus(result.DifferenceCount == 0
                ? "两个文件内容相同"
                : "比较完成，共 " + _diffIndexes.Count + " 处差异（F3/Shift+F3 或 N/P 跳转）", "success");

            if (_scrollToFirstDiffOnCompare)
            {
                // 直接按行高算像素偏移：虚拟化下 ScrollIntoView 在布局完成前调用不可靠（实测滚不动），
                // 像素滚动只依赖固定行高，确定性生效
                var rowIndex = result.Rows.FindIndex(delegate(DiffRow row) { return row.Kind != DiffKind.Equal; });
                if (rowIndex > 0) SetVerticalAll(rowIndex * RowHeight);
            }
        }

        // ---- 滚动同步（垂直四个列表全同步；水平只在两个内容列之间互写，防回环）----
        // 行号冻结列 Horizontal=Disabled 永不横滚，水平同步不经过它。

        private ScrollViewer[] AllScrolls()
        {
            return new[] { _leftGutterScroll, _leftScroll, _rightScroll, _rightGutterScroll };
        }

        private void OnAnyScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_syncingScroll) return;
            var source = sender as ScrollViewer;
            if (source == null) return;
            _syncingScroll = true;
            try
            {
                if (e.VerticalChange != 0)
                {
                    foreach (var viewer in AllScrolls())
                        if (viewer != null && !ReferenceEquals(viewer, source))
                            viewer.ScrollToVerticalOffset(source.VerticalOffset);
                }
                if (e.HorizontalChange != 0)
                {
                    if (ReferenceEquals(source, _leftScroll) && _rightScroll != null)
                        _rightScroll.ScrollToHorizontalOffset(source.HorizontalOffset);
                    else if (ReferenceEquals(source, _rightScroll) && _leftScroll != null)
                        _leftScroll.ScrollToHorizontalOffset(source.HorizontalOffset);
                }
            }
            finally
            {
                _syncingScroll = false;
            }
        }

        /// <summary>四个列表统一写垂直 offset（各自按自身 ScrollableHeight 钳制；行号列因无横滚条
        /// 视口略高、可滚范围略大，写同一值即可，多出的部分被钳制掉，不会漂）。</summary>
        private void SetVerticalAll(double offset)
        {
            _syncingScroll = true;
            try
            {
                foreach (var viewer in AllScrolls())
                    if (viewer != null)
                        viewer.ScrollToVerticalOffset(Math.Max(0, Math.Min(offset, Math.Max(0, viewer.ScrollableHeight))));
            }
            finally
            {
                _syncingScroll = false;
            }
        }

        private static ScrollViewer FindScrollViewer(DependencyObject root)
        {
            if (root == null) return null;
            var viewer = root as ScrollViewer;
            if (viewer != null) return viewer;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
                if (found != null) return found;
            }
            return null;
        }

        // ---- 差异跳转（F3 下一处 / Shift+F3 上一处，2026-11-04 增补拍板；
        //      备用键 N/P——F3 易被第三方全局热键占用，2026-11-05 拍板）----

        private void JumpToDiff(bool forward)
        {
            Logger.Info("diff jump dir=" + (forward ? "next" : "prev")
                        + " indexes=" + (_diffIndexes == null ? -1 : _diffIndexes.Count)
                        + " scroll=" + (_leftScroll != null));
            if (_diffIndexes == null || _diffIndexes.Count == 0 || _leftScroll == null)
            {
                SetStatus("两个文件内容相同，无差异可跳转", "info");
                return;
            }
            // 定位基准：聚焦行可见时从聚焦行继续（连按 F3 逐处前进，与视口无关——
            // 目标行已在视口内时 VerticalOffset 不变，纯视口基准会反复命中同一处，实测踩过）；
            // 聚焦行不存在/已被滚出视口（用户手动浏览）→ 以视口第一可见行为基准。
            var firstVisible = (int)(_leftScroll.VerticalOffset / RowHeight);
            var fromIndex = firstVisible;
            if (_focusedSeq >= 0 && _focusedSeq < _diffIndexes.Count)
            {
                var focusRow = _diffIndexes[_focusedSeq];
                var lastVisible = (int)((_leftScroll.VerticalOffset + _leftScroll.ViewportHeight) / RowHeight);
                if (focusRow >= firstVisible && focusRow < lastVisible) fromIndex = focusRow;
            }
            bool wrapped;
            var target = DiffEngine.FindJumpTarget(_diffIndexes, fromIndex, forward, out wrapped);
            if (target < 0) return;
            FocusDiffAt(target, wrapped);
        }

        private void FocusDiffAt(int target, bool wrapped)
        {
            var rowIndex = _diffIndexes[target];
            var row = _result.Rows[rowIndex];

            if (_focusedRow != null) _focusedRow.IsFocused = false;
            row.IsFocused = true;
            _focusedRow = row;
            _focusedSeq = target;

            LeftList.ScrollIntoView(row);
            RightList.ScrollIntoView(row);
            UpdateLayout(); // 虚拟化生成行容器后才能做水平定位
            CenterRow(rowIndex);
            HorizontalReveal(row);

            var position = "差异 " + (target + 1) + " / " + _diffIndexes.Count;
            var prefix = wrapped ? (target == 0 ? "已到最后一个差异，回绕到开头 · " : "已到最前的差异，回绕到末尾 · ") : "";
            if (RowHasInvisibleDiff(row))
                SetStatus(prefix + position + " · 此处差异为不可见字符（如零宽空格），以竖条标识显示", wrapped ? "warn" : "info");
            else
                SetStatus(prefix + position, wrapped ? "warn" : "info");
        }

        /// <summary>把命中行滚到视口垂直居中。行已在视口内时 ScrollIntoView 几乎不动，用户会以为没跳；
        /// 居中让每次跳转都有明确的画面位移。四个列表直接写同一像素值（行高恒定，不会漂；
        /// 行号列若只靠 ScrollIntoView 的同步跟随，会在 ScrollIntoView 与 CenterRow 之间闪一下旧位置）。</summary>
        private void CenterRow(int rowIndex)
        {
            if (_leftScroll == null) return;
            var target = rowIndex * RowHeight - (_leftScroll.ViewportHeight - RowHeight) / 2;
            target = Math.Max(0, Math.Min(target, Math.Max(0, _leftScroll.ScrollableHeight)));
            SetVerticalAll(target);
        }

        /// <summary>跳转后水平定位：change 行把第一个行内差异片段滚到视口 40% 处（等宽字体按字符偏移估算 x）；
        /// 其余情况（delete/insert 整行差异、无片段）回到行首——上一处长行留下的横滚 offset 会让短行整行落在视口外（实测）。</summary>
        private void HorizontalReveal(DiffRow row)
        {
            if (_leftScroll == null || _rightScroll == null) return;
            var leftTarget = 0.0;
            var rightTarget = 0.0;
            if (row.Kind == DiffKind.Change)
            {
                var left = OffsetForParts(row.LeftParts);
                if (left >= 0) leftTarget = left;
                var right = OffsetForParts(row.RightParts);
                if (right >= 0) rightTarget = right;
            }

            _syncingScroll = true; // 直接分别设置两栏，绕开互写
            try
            {
                _leftScroll.ScrollToHorizontalOffset(Math.Min(leftTarget, _leftScroll.ScrollableWidth));
                _rightScroll.ScrollToHorizontalOffset(Math.Min(rightTarget, _rightScroll.ScrollableWidth));
            }
            finally
            {
                _syncingScroll = false;
            }
        }

        /// <summary>该侧第一个差异片段的 x 偏移（滚动目标 = 片段出现在视口 40% 处）；无片段返回 -1。</summary>
        private double OffsetForParts(List<DiffPart> parts)
        {
            if (parts == null) return -1;
            var chars = 0;
            foreach (var part in parts)
            {
                if (part.Changed)
                    return Math.Max(0, 8 + MonoCharWidth() * chars - _leftScroll.ViewportWidth * 0.4);
                chars += part.Text.Length;
            }
            return -1;
        }

        /// <summary>Consolas 等宽字符宽（DIP，懒测一次；TextBlock 与 ScrollViewer offset 同为 DIP）。</summary>
        private double MonoCharWidth()
        {
            if (_charWidth < 0)
            {
                var typeface = new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                var formatted = new FormattedText("M", System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, 12.5, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                _charWidth = formatted.Width;
            }
            return _charWidth;
        }

        private static bool RowHasInvisibleDiff(DiffRow row)
        {
            return HasInvisibleParts(row.LeftParts) || HasInvisibleParts(row.RightParts)
                || (row.Kind == DiffKind.Delete && DiffEngine.ContainsInvisible(row.Left))
                || (row.Kind == DiffKind.Insert && DiffEngine.ContainsInvisible(row.Right));
        }

        private static bool HasInvisibleParts(List<DiffPart> parts)
        {
            if (parts == null) return false;
            foreach (var part in parts)
                if (part.Changed && DiffEngine.ContainsInvisible(part.Text)) return true;
            return false;
        }

        // ---- 键位与动作面板 ----

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

            if (Actions.IsOpen)
            {
                if (Actions.HandleKey(e)) e.Handled = true;
                return;
            }
            if (e.Key == Key.F3) { JumpToDiff((Keyboard.Modifiers & ModifierKeys.Shift) == 0); e.Handled = true; return; }
            // F3 备用键 N/P（2026-11-05 拍板）：F3 是全局热键重灾区（被第三方 RegisterHotkey
            // 截走后焦点应用永远收不到），裸字母键几乎不会被全局注册；仅排除 Ctrl/Alt，
            // Shift 忽略（N/P 方向与大小写无关）
            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
            {
                if (e.Key == Key.N) { JumpToDiff(true); e.Handled = true; return; }
                if (e.Key == Key.P) { JumpToDiff(false); e.Handled = true; return; }
            }
            if (ctrl && e.Key == Key.Enter) { ShowActions(); e.Handled = true; return; }
            if (ctrl && e.Key == Key.V) { PasteToHoverSide(); e.Handled = true; return; }
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        }

        /// <summary>动作面板（三项文件操作 + 两项差异跳转，2026-11-04 拍板）。</summary>
        private void ShowActions()
        {
            var bothLoaded = _leftText != null && _rightText != null;
            var hasDiffs = _diffIndexes != null && _diffIndexes.Count > 0;
            Actions.Show(Root, new List<ActionItem>
            {
                new ActionItem("select-left", "选择左侧文件", "L"),
                new ActionItem("select-right", "选择右侧文件", "R"),
                new ActionItem("compare", "开始比较", "C", bothLoaded),
                new ActionItem("diff-next", "跳到下一个差异", "F3/N", hasDiffs),
                new ActionItem("diff-prev", "跳到上一个差异", "Shift+F3/P", hasDiffs)
            });
        }

        private void OnActionInvoked(string id)
        {
            if (id == "select-left") ChooseFile(true);
            else if (id == "select-right") ChooseFile(false);
            else if (id == "compare") Compare();
            else if (id == "diff-next") JumpToDiff(true);
            else if (id == "diff-prev") JumpToDiff(false);
        }

        // ---- 状态栏 ----

        /// <summary>调试入口（--diff-left/--diff-right）：预载文件并触发比较，UI 截图自验用。</summary>
        public void DebugPreload(string left, string right)
        {
            _scrollToFirstDiffOnCompare = true;
            if (!string.IsNullOrEmpty(left)) LoadFile(true, left);
            if (!string.IsNullOrEmpty(right)) LoadFile(false, right);
        }

        private void SetStatus(string text, string kind)
        {
            StatusText.Text = text;
            string key;
            switch (kind)
            {
                case "success": key = "Text.Success"; break;
                case "error": key = "Text.Error"; break;
                case "warn": key = "Text.Primary"; break;
                default: key = "Text.Secondary"; break;
            }
            StatusText.Foreground = (Brush)FindResource(key);
        }
    }
}
