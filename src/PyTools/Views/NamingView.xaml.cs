using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PyTools.Core;

namespace PyTools.Views
{
    /// <summary>
    /// 命名列表行：IsHeader = 批次小标题（不可选中、不响应鼠标），否则是一条候选。
    /// 注意必须用属性而非公开字段——WPF 绑定不认字段，绑定失败静默空白（首版实测踩到）。
    /// </summary>
    public sealed class NamingRow
    {
        public bool IsHeader { get; set; }
        public string HeaderText { get; set; }
        public string Group { get; set; }
        public string Value { get; set; }
        public string Style { get; set; }
        public string Note { get; set; }
    }

    /// <summary>
    /// 代码命名视图（M4 AI 批第四个消费者，v1 tools/code-naming 移植）：
    /// 描述输入 + 八组候选列表 + 状态栏。与 v1 的差异：
    /// ① 换一批**追加为新批次**（最多 3 批，批次小标题分节，可上下对比），不再整批替换；
    /// ② exclude 跨批次累积，解析层再与已有值去重（同值不跨批重复）；
    /// ③ 打开不读剪贴板、不预填（用户拍板：描述得自己想）；
    /// ④ 列表可按全局键位步进，Enter 复制选中行（v1 只能鼠标点）。
    /// </summary>
    public partial class NamingView : UserControl, ISearchFocusable
    {
        private const int MaxBatches = 3;

        private readonly ObservableCollection<NamingRow> _rows = new ObservableCollection<NamingRow>();
        private readonly List<List<NamingRow>> _batches = new List<List<NamingRow>>();
        private readonly List<string> _values = new List<string>(); // 跨批次累积（exclude 来源）
        private bool _actionMode;
        private bool _busy;
        private string _generatedInput = ""; // 当前结果对应的描述（改了描述就不是「换一批」）

        public NamingView()
        {
            InitializeComponent();
            PageStep = 5;
            PART_List.ItemsSource = _rows;
            PART_List.MouseLeftButtonUp += OnListItemClick;
            InputBox.KeyDown += OnInputKeyDown;
            Actions.Invoked += OnActionInvoked;
            Actions.Dismissed += ExitActionModeInternal;
            PreviewKeyDown += OnViewPreviewKeyDown;
            PreviewMouseLeftButtonUp += OnViewPreviewMouseUp;
            UpdatePlaceholders();
            UpdateButtons();
            RenderRows();
        }

        /// <summary>Ctrl+N/P 步进条数（与启动器列表一致，取 v2.pageStep）。</summary>
        public int PageStep { get; set; }

        public TextBox SearchBox { get { return InputBox; } } // ISearchFocusable

        public void FocusSearch()
        {
            InputBox.Focus();
            InputBox.SelectAll();
        }

        /// <summary>进入视图：保留上次结果（同一描述常要反复取用），焦点回输入框并全选。</summary>
        public void ResetForNaming()
        {
            ExitActionModeInternal();
            UpdatePlaceholders();
            UpdateButtons();
            InputBox.Focus();
            InputBox.SelectAll();
        }

        /// <summary>
        /// 调试 / 截图自验：预填描述（--naming-text=），demo=true 时直接铺两批示例结果（--naming-demo，不请求 AI）。
        /// </summary>
        public void DebugPreload(string text, bool demo)
        {
            if (!string.IsNullOrEmpty(text)) InputBox.Text = text;
            if (!demo) return;

            _generatedInput = InputBox.Text.Trim();
            _batches.Clear();
            _values.Clear();
            _batches.Add(MakeDemoBatch(
                new[] { "变量:camelCase:orderTime", "类:PascalCase:OrderRecord", "表名:snake_case:order_records", "字段:snake_case:placed_at" }));
            _batches.Add(MakeDemoBatch(
                new[] { "变量:camelCase:placedAt", "类:PascalCase:OrderEvent", "表名:snake_case:order_events", "字段:snake_case:created_at" }));
            UpdateSummary("下单时间", "取「下单」+ 时间语义；表名用复数、字段用过去式动词");
            RenderRows();
            SelectFirstOfBatch(_batches[_batches.Count - 1]);
            SetStatus("ok", "已换一批，共 4 条 · demo · 0.0s");
            UpdatePlaceholders();
            UpdateButtons();
        }

        private List<NamingRow> MakeDemoBatch(string[] specs)
        {
            var batch = new List<NamingRow>();
            foreach (var spec in specs)
            {
                var parts = spec.Split(':');
                batch.Add(new NamingRow { Group = parts[0], Style = parts[1], Value = parts[2] });
                _values.Add(parts[2]);
            }
            return batch;
        }

        public bool TryExitActionMode()
        {
            if (!_actionMode) return false;
            ExitActionModeInternal();
            return true;
        }

        public void ExitActionMode()
        {
            ExitActionModeInternal();
        }

        // ---- 输入 ----

        private void OnInputChanged(object sender, TextChangedEventArgs e)
        {
            if (StatusText == null) return; // XAML 加载期
            UpdatePlaceholders();
            UpdateButtons();
            if (_busy) return;
            var text = InputBox.Text.Trim();
            if (text.Length == 0) SetStatus(null, "等待输入描述");
            else if (_batches.Count > 0 && !string.Equals(text, _generatedInput, StringComparison.Ordinal))
                SetStatus(null, "描述已修改，回车重新生成");
            else SetStatus(null, "回车生成命名");
        }

        private void OnInputFocusChanged(object sender, KeyboardFocusChangedEventArgs e)
        {
            UpdatePlaceholders();
        }

        /// <summary>watermark 惯例：无内容且未聚焦才显示（聚焦时光标会压着提示首字）。</summary>
        private void UpdatePlaceholders()
        {
            if (InputPlaceholder == null) return;
            InputPlaceholder.Visibility = InputBox.Text.Length == 0 && !InputBox.IsKeyboardFocusWithin
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateButtons()
        {
            if (GenerateButton == null) return;
            var text = InputBox.Text.Trim();
            GenerateButton.IsEnabled = !_busy && text.Length > 0;
            RerollButton.IsEnabled = !_busy && _batches.Count > 0
                && string.Equals(text, _generatedInput, StringComparison.Ordinal);
        }

        /// <summary>输入框内 Enter = 生成（单行框；视图级 PreviewKeyDown 对输入框焦点下的 Enter 让位）。</summary>
        private void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            StartGenerate(false);
        }

        // ---- 生成 / 换一批 ----

        private async void StartGenerate(bool reroll)
        {
            if (_busy) return;
            var text = InputBox.Text.Trim();
            if (text.Length == 0)
            {
                SetStatus("err", "请输入名词或业务描述");
                InputBox.Focus();
                return;
            }
            if (text.Length > AiClient.NamingMaxInput)
            {
                SetStatus("err", "输入过长，请控制在 " + AiClient.NamingMaxInput + " 字以内");
                return;
            }
            if (reroll && (_batches.Count == 0 || !string.Equals(text, _generatedInput, StringComparison.Ordinal)))
                reroll = false; // 描述变过 → 视为重新生成（不拿旧批次的 exclude 做「换一批」）

            var status = AiClient.Status();
            if (!status.Available)
            {
                SetStatus("err", "AI 不可用: " + status.Reason);
                return;
            }

            if (!reroll)
            {
                _batches.Clear();
                _values.Clear();
                _generatedInput = text;
            }

            _busy = true;
            UpdateButtons();
            SetStatus(null, reroll ? "正在换一批…" : "正在生成命名…");
            var watch = Stopwatch.StartNew();
            try
            {
                var result = await AiClient.NamingAsync(text, new List<string>(_values));
                watch.Stop();
                if (!result.Ok)
                {
                    var reason = result.Error;
                    if (result.RequestFailed) reason += "（可在设置中更换模型）";
                    SetStatus("err", "生成失败: " + reason);
                    return;
                }

                var batch = new List<NamingRow>();
                foreach (var item in result.Items)
                {
                    batch.Add(new NamingRow { Group = item.Group, Value = item.Value, Style = item.Style, Note = item.Note });
                    if (!_values.Contains(item.Value)) _values.Add(item.Value);
                }
                _batches.Add(batch);
                while (_batches.Count > MaxBatches) _batches.RemoveAt(0); // 只保留最近 3 批

                UpdateSummary(result.Core, result.Note);
                RenderRows();
                SelectFirstOfBatch(batch);
                SetStatus("ok", (reroll ? "已换一批，" : "已生成，") + "共 " + batch.Count + " 条 · "
                    + result.Model + " · " + (watch.ElapsedMilliseconds / 1000.0).ToString("0.0") + "s");
            }
            catch (Exception ex)
            {
                SetStatus("err", "生成失败: " + ex.Message);
            }
            finally
            {
                _busy = false;
                UpdateButtons();
            }
        }

        private void UpdateSummary(string core, string note)
        {
            var coreText = (core ?? "").Trim();
            var noteText = (note ?? "").Trim();
            CoreText.Text = coreText.Length > 0 ? "核心概念 " + coreText : "";
            NoteText.Text = noteText;
            SummaryRow.Visibility = CoreText.Text.Length > 0 || noteText.Length > 0
                ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---- 列表渲染 ----

        private void RenderRows()
        {
            _rows.Clear();
            for (var i = 0; i < _batches.Count; i++)
            {
                var batch = _batches[i];
                _rows.Add(new NamingRow { IsHeader = true, HeaderText = "第 " + (i + 1) + " 批 · " + batch.Count + " 条" });
                foreach (var row in batch) _rows.Add(row);
            }
            EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SelectFirstOfBatch(List<NamingRow> batch)
        {
            if (batch.Count == 0) return;
            var index = _rows.IndexOf(batch[0]);
            if (index >= 0) SetSelectedIndex(index);
        }

        private void SetSelectedIndex(int index)
        {
            if (index < 0 || index >= _rows.Count) return;
            PART_List.SelectedIndex = index;
            PART_List.ScrollIntoView(_rows[index]);
        }

        /// <summary>可选中行的索引（批次小标题不算）。</summary>
        private List<int> ItemIndexes()
        {
            var list = new List<int>();
            for (var i = 0; i < _rows.Count; i++)
                if (!_rows[i].IsHeader) list.Add(i);
            return list;
        }

        private void MoveSelection(int steps)
        {
            var items = ItemIndexes();
            if (items.Count == 0) return;
            var pos = items.IndexOf(PART_List.SelectedIndex);
            if (pos < 0) pos = steps > 0 ? 0 : items.Count - 1;
            else pos = Math.Max(0, Math.Min(items.Count - 1, pos + steps));
            SetSelectedIndex(items[pos]);
        }

        private NamingRow SelectedRow()
        {
            var row = PART_List.SelectedItem as NamingRow;
            return row != null && !row.IsHeader ? row : null;
        }

        private void OnListItemClick(object sender, MouseButtonEventArgs e)
        {
            var src = e.OriginalSource as DependencyObject;
            while (src != null && !(src is ListBoxItem)) src = VisualTreeHelper.GetParent(src);
            var container = src as ListBoxItem;
            if (container == null) return;
            var row = container.DataContext as NamingRow;
            if (row == null || row.IsHeader) return;
            CopyRow(row);
        }

        private void CopyRow(NamingRow row)
        {
            if (row == null || row.IsHeader || row.Value.Length == 0) return;
            try
            {
                Clipboard.SetText(row.Value);
                SetStatus(null, "已复制 " + row.Value);
            }
            catch (Exception ex)
            {
                SetStatus("err", "复制失败：" + ex.Message);
            }
        }

        private void ClearAll()
        {
            InputBox.Text = "";
            _batches.Clear();
            _values.Clear();
            _generatedInput = "";
            CoreText.Text = "";
            NoteText.Text = "";
            SummaryRow.Visibility = Visibility.Collapsed;
            RenderRows();
            SetStatus(null, "已清空");
            UpdatePlaceholders();
            UpdateButtons();
            InputBox.Focus();
        }

        private void SetStatus(string kind, string message)
        {
            StatusText.Text = message;
            StatusText.ToolTip = string.IsNullOrEmpty(message) ? null : message;
            StatusText.Foreground = kind == "err" ? (Brush)FindResource("Text.Error")
                : kind == "ok" ? (Brush)FindResource("Accent")
                : (Brush)FindResource("Text.Placeholder");
        }

        // ---- 动作面板（Ctrl+Enter，与启动器同一套约定）----

        private List<ActionItem> BuildActions()
        {
            var text = InputBox.Text.Trim();
            var canGenerate = text.Length > 0 && !_busy;
            var canReroll = _batches.Count > 0 && !_busy
                && string.Equals(text, _generatedInput, StringComparison.Ordinal);
            return new List<ActionItem>
            {
                new ActionItem("generate", "生成命名", "G", canGenerate),
                new ActionItem("reroll", "换一批", "R", canReroll),
                new ActionItem("copy", "复制选中项", "C", SelectedRow() != null),
                new ActionItem("clear", "清空", "X", text.Length > 0 || _rows.Count > 0),
            };
        }

        private void EnterActionMode()
        {
            _actionMode = true;
            Actions.Show(RootBorder, BuildActions());
        }

        private void ExitActionModeInternal()
        {
            if (!_actionMode) return;
            _actionMode = false;
            Actions.Hide();
            InputBox.Focus();
        }

        private void OnActionInvoked(string actionId)
        {
            ExitActionModeInternal();
            if (actionId == "generate") { StartGenerate(false); return; }
            if (actionId == "reroll") { StartGenerate(true); return; }
            if (actionId == "copy") { CopyRow(SelectedRow()); return; }
            if (actionId == "clear") { ClearAll(); return; }
        }

        // ---- 键位（全局约定：↓/↑ 一条、Ctrl+J/K 加速、Ctrl+N/P 翻页、Enter 执行、Ctrl+Enter 面板）----

        private void OnViewPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_actionMode)
            {
                if (Actions.HandleKey(e)) e.Handled = true;
                return;
            }

            var isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

            if (isCtrl && e.Key == Key.Enter)
            {
                EnterActionMode();
                e.Handled = true;
                return;
            }
            if (isCtrl && (e.Key == Key.J || e.Key == Key.K))
            {
                MoveSelection(e.Key == Key.J ? 1 : -1);
                e.Handled = true;
                return;
            }
            if (isCtrl && (e.Key == Key.N || e.Key == Key.P))
            {
                MoveSelection(e.Key == Key.N ? Math.Max(1, PageStep) : -Math.Max(1, PageStep));
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Down || e.Key == Key.Up)
            {
                MoveSelection(e.Key == Key.Down ? 1 : -1);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Enter)
            {
                // 输入框内 Enter 归输入框（生成）；列表态 Enter 复制选中项
                if (InputBox.IsKeyboardFocusWithin) return;
                var row = SelectedRow();
                if (row != null)
                {
                    CopyRow(row);
                    e.Handled = true;
                }
            }
        }

        private void OnGenerateClick(object sender, RoutedEventArgs e)
        {
            StartGenerate(false);
        }

        private void OnRerollClick(object sender, RoutedEventArgs e)
        {
            StartGenerate(true);
        }

        /// <summary>鼠标点状态栏「操作 Ctrl+Enter」键帽呼出动作面板（与其他视图同一实现）。</summary>
        private void OnViewPreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!ActionsHint.IsVisible) return;
            var src = e.OriginalSource as DependencyObject;
            while (src != null && !ReferenceEquals(src, ActionsHint)) src = VisualTreeHelper.GetParent(src);
            if (!ReferenceEquals(src, ActionsHint)) return;
            if (_actionMode) return;
            EnterActionMode();
            e.Handled = true;
        }

        // ---- 圆角裁切（与其他视图同因：Border.CornerRadius 不裁子内容）----

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
