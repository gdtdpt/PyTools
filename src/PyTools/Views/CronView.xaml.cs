using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PyTools.Core;

namespace PyTools.Views
{
    /// <summary>
    /// Cron 表达式视图（M4）：表达式⇄构造器双向实时联动（v1 语义整体移植）。
    /// 双栏构造器（左秒分时/右日月周）；正在输入的控件不回写；参数没填完不算错只提示补齐。
    /// 动作面板复用 Views/ActionPanel；键位照全局约定（Ctrl+Enter / Esc 由窗口级路由）。
    /// </summary>
    public partial class CronView : UserControl, ISearchFocusable
    {
        private bool _actionMode;
        private bool _applyingFields;   // 解析结果回写构造器期间抑制联动（SelectionChanged/TextChanged 会被逐项触发）
        private bool _suppressExpr;     // 构造器生成表达式写回 ExprBox 期间，抑制 TextChanged 的重复渲染

        /// <summary>构造器行：控件引用集合（代码后置构建，XAML 写不下 6 行 × 4 模式参数面板）。</summary>
        private sealed class FieldRow
        {
            public CronFieldDef Def;
            public string[] Modes;      // 本行下拉可用的模式（秒/分无点选网格，不提供「指定」）
            public Border RowBorder;
            public ComboBox Mode;
            public Panel EveryPanel, ListPanel, RangePanel, CustomPanel;
            public TextBox StepBox, ListText, RangeFrom, RangeTo, RangeStep, CustomText;
            public WrapPanel GridButtons; // null = 秒/分（无点选网格）
        }

        private readonly Dictionary<string, FieldRow> _rows = new Dictionary<string, FieldRow>();

        public CronView()
        {
            InitializeComponent();
            BuildRows();
            Actions.Invoked += OnActionInvoked;
            Actions.Dismissed += ExitActionModeInternal;
            PreviewKeyDown += OnViewPreviewKeyDown;
            PreviewMouseLeftButtonUp += OnViewPreviewMouseUp;
        }

        public TextBox SearchBox { get { return ExprBox; } } // ISearchFocusable

        public void FocusSearch()
        {
            ExprBox.Focus();
            ExprBox.CaretIndex = ExprBox.Text.Length;
        }

        /// <summary>进入视图：干净状态（cron 使用多为临时推算，不记忆上次表达式——2026-11-03 拍板）。</summary>
        public void ResetForCron()
        {
            ExitActionModeInternal();
            ExprBox.Text = "";
            RenderExpression("");
        }

        /// <summary>Esc 分层：动作面板先关，返回是否已消费。</summary>
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

        // ---- 构造器构建 ----

        private void BuildRows()
        {
            var leftKeys = new[] { "second", "minute", "hour" };
            var rightKeys = new[] { "day", "month", "week" };
            foreach (var def in CronExpr.Fields)
            {
                var row = CreateRow(def);
                _rows[def.Key] = row;
                var host = Array.IndexOf(leftKeys, def.Key) >= 0 ? LeftColumn : RightColumn;
                host.Children.Add(row.RowBorder);
            }
        }

        private FieldRow CreateRow(CronFieldDef def)
        {
            var row = new FieldRow { Def = def };
            var hasGrid = def.Key == "hour" || def.Key == "day" || def.Key == "month" || def.Key == "week";
            // 秒/分 60 个值做不了点选网格，没有网格的「指定」与「自定义」完全同质——不提供（2026-11-03 反馈）
            row.Modes = hasGrid ? new[] { "every", "list", "range", "custom" } : new[] { "every", "range", "custom" };

            var border = new Border
            {
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Transparent,
                Background = (Brush)FindResource("Bg.Input"),
                Padding = new Thickness(5, 3, 5, 3),
                Margin = new Thickness(0, 0, 0, 6),
            };
            row.RowBorder = border;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) }); // 「自定义」四字 + 新内边距不截断
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var name = new TextBlock
            {
                Text = def.Label,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 13,
                Foreground = (Brush)FindResource("Text.Primary"),
            };
            Grid.SetColumn(name, 0);

            var mode = new ComboBox
            {
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, // 行内按钮网格撑高行时，下拉框保持居中不被拉伸
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };
            var modeLabels = new Dictionary<string, string> { { "every", "每…" }, { "list", "指定" }, { "range", "区间" }, { "custom", "自定义" } };
            foreach (var modeKey in row.Modes)
                mode.Items.Add(new ComboBoxItem { Content = modeLabels[modeKey] });
            mode.SelectedIndex = 0;
            mode.SelectionChanged += delegate { ShowParam(row); OnBuilderChanged(); };
            row.Mode = mode;
            Grid.SetColumn(mode, 1);

            // 参数区：四个面板叠放，按模式显隐
            var paramsHost = new Grid();
            Grid.SetColumn(paramsHost, 2);

            // 每…
            var every = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var stepBox = MonoBox(46);
            stepBox.TextChanged += delegate { OnBuilderChanged(); };
            every.Children.Add(Hint("每"));
            every.Children.Add(stepBox);
            every.Children.Add(Hint(EveryUnit(def.Key)));
            row.EveryPanel = every;
            row.StepBox = stepBox;

            // 指定：点选网格（可选）+ 文本框（文本框是最终数据源，网格只做可视化与点选）
            var list = new StackPanel();
            row.ListPanel = list;
            if (def.Key == "hour" || def.Key == "day" || def.Key == "month" || def.Key == "week")
            {
                var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
                row.GridButtons = buttons;
                var from = def.Key == "hour" ? 0 : 1;
                var gridMax = def.Key == "week" ? 6 : def.Max; // 周 7 也是周日，点选网格与展示一致只出 0-6
                for (var value = from; value <= gridMax; value++)
                {
                    // 切换块走 ToggleButton 隐式样式（选中 = 淡蓝底 + 蓝字，与胶囊单选同语义）
                    var button = new ToggleButton
                    {
                        Content = def.Key == "week" ? WeekLabel(value) : value.ToString(),
                        Tag = value.ToString(CultureInfo.InvariantCulture),
                        MinWidth = 26,
                        Height = 22,
                        Margin = new Thickness(0, 0, 3, 3),
                        Padding = new Thickness(3, 0, 3, 0),
                        FontSize = 11,
                    };
                    var captured = row;
                    button.Click += delegate
                    {
                        SyncTextFromGrid(captured);
                        OnBuilderChanged();
                    };
                    buttons.Children.Add(button);
                }
                list.Children.Add(buttons);
            }
            var listText = MonoBox(double.NaN);
            listText.TextChanged += delegate { SyncGridFromText(row); OnBuilderChanged(); };
            row.ListText = listText;
            list.Children.Add(listText);

            // 区间
            var range = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var rangeFrom = MonoBox(44); var rangeTo = MonoBox(44); var rangeStep = MonoBox(44);
            rangeFrom.TextChanged += delegate { OnBuilderChanged(); };
            rangeTo.TextChanged += delegate { OnBuilderChanged(); };
            rangeStep.TextChanged += delegate { OnBuilderChanged(); };
            range.Children.Add(rangeFrom);
            range.Children.Add(Hint("至"));
            range.Children.Add(rangeTo);
            range.Children.Add(Hint("每"));
            range.Children.Add(rangeStep);
            row.RangePanel = range;
            row.RangeFrom = rangeFrom;
            row.RangeTo = rangeTo;
            row.RangeStep = rangeStep;

            // 自定义
            var custom = new Grid();
            var customText = MonoBox(double.NaN);
            customText.TextChanged += delegate { OnBuilderChanged(); };
            custom.Children.Add(customText);
            row.CustomPanel = custom;
            row.CustomText = customText;

            Panel[] allParams = { every, list, range, custom };
            foreach (var panel in allParams)
                paramsHost.Children.Add(panel);

            grid.Children.Add(name);
            grid.Children.Add(mode);
            grid.Children.Add(paramsHost);
            border.Child = grid;

            ShowParam(row); // 默认「每…」
            return row;
        }

        private static string EveryUnit(string key)
        {
            switch (key)
            {
                case "second": return "秒";
                case "minute": return "分钟";
                case "hour": return "小时";
                case "day": return "天";
                case "month": return "个月";
                default: return "天";
            }
        }

        private static string WeekLabel(int value)
        {
            return new[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" }[value];
        }

        private TextBox MonoBox(double width)
        {
            var box = new TextBox
            {
                Height = 24,
                FontSize = 12,
                FontFamily = new FontFamily("Consolas"),
                Foreground = (Brush)FindResource("Text.Primary"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4, 2, 4, 2),
                VerticalContentAlignment = VerticalAlignment.Center,
                SpellCheck = { IsEnabled = false },
                CaretBrush = (Brush)FindResource("Text.Primary"),
            };
            if (!double.IsNaN(width))
            {
                box.Width = width;
                box.HorizontalAlignment = HorizontalAlignment.Left;
                box.TextAlignment = TextAlignment.Center;
            }
            return box;
        }

        private TextBlock Hint(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = (Brush)FindResource("Text.Placeholder"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0),
            };
        }

        private void ShowParam(FieldRow row)
        {
            // 按模式名（而非下拉下标）映射面板：秒/分的下拉少一项「指定」，下标语义与有网格的行不同
            var mode = ModeOf(row);
            row.EveryPanel.Visibility = mode == "every" ? Visibility.Visible : Visibility.Collapsed;
            row.ListPanel.Visibility = mode == "list" ? Visibility.Visible : Visibility.Collapsed;
            row.RangePanel.Visibility = mode == "range" ? Visibility.Visible : Visibility.Collapsed;
            row.CustomPanel.Visibility = mode == "custom" ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---- 构造器状态 ⇄ 表达式 ----

        private static string ModeOf(FieldRow row)
        {
            var index = Math.Max(0, Math.Min(row.Mode.SelectedIndex, row.Modes.Length - 1));
            return row.Modes[index];
        }

        private static CronBuilderState ReadState(FieldRow row)
        {
            var mode = ModeOf(row);
            var state = new CronBuilderState { Mode = mode };
            if (mode == "every") state.Step = row.StepBox.Text;
            else if (mode == "list")
                state.Values = new List<string>(row.ListText.Text.Split(','));
            else if (mode == "range")
            {
                state.From = row.RangeFrom.Text;
                state.To = row.RangeTo.Text;
                state.RangeStep = row.RangeStep.Text;
            }
            else state.Text = row.CustomText.Text;
            return state;
        }

        private void ApplyState(FieldRow row, CronBuilderState state)
        {
            var mode = state.Mode;
            if (mode == "list" && Array.IndexOf(row.Modes, "list") < 0)
            {
                // 秒/分无点选网格：表达式侧的「指定」降级为自定义直写，值保持可见可编辑
                mode = "custom";
                state = new CronBuilderState
                {
                    Mode = "custom",
                    Text = state.Values != null ? string.Join(",", state.Values) : "",
                };
            }
            var modeIndex = Array.IndexOf(row.Modes, mode);
            if (modeIndex < 0) modeIndex = 0;
            row.Mode.SelectedIndex = modeIndex;
            ShowParam(row);
            if (state.Mode == "every") SetTextIfNotFocused(row.StepBox, state.Step ?? "");
            else if (state.Mode == "list")
            {
                SetTextIfNotFocused(row.ListText, state.Values != null ? string.Join(",", state.Values) : "");
                SyncGridFromText(row);
            }
            else if (state.Mode == "range")
            {
                SetTextIfNotFocused(row.RangeFrom, state.From ?? "");
                SetTextIfNotFocused(row.RangeTo, state.To ?? "");
                SetTextIfNotFocused(row.RangeStep, state.RangeStep ?? "");
            }
            else SetTextIfNotFocused(row.CustomText, state.Text ?? "");
        }

        // 正在输入的控件不回写：否则刚敲的「,」「1」这类中间状态会被解析结果当场抹掉（v1 同语义）
        private static void SetTextIfNotFocused(TextBox box, string text)
        {
            if (ReferenceEquals(Keyboard.FocusedElement, box)) return;
            box.Text = text;
        }

        private void SyncGridFromText(FieldRow row)
        {
            if (row.GridButtons == null) return;
            var values = new HashSet<string>();
            foreach (var item in row.ListText.Text.Split(',')) values.Add(item.Trim());
            foreach (ToggleButton button in row.GridButtons.Children)
            {
                var on = values.Contains((string)button.Tag);
                if (on != (button.IsChecked == true)) button.IsChecked = on;
            }
        }

        private void SyncTextFromGrid(FieldRow row)
        {
            if (row.GridButtons == null) return;
            var values = new List<string>();
            foreach (ToggleButton button in row.GridButtons.Children)
                if (button.IsChecked == true)
                    values.Add((string)button.Tag);
            SetTextIfNotFocused(row.ListText, string.Join(",", values));
        }

        private static bool IsIncomplete(FieldRow row)
        {
            var mode = ModeOf(row);
            if (mode == "list") return row.ListText.Text.Trim().Length == 0;
            if (mode == "custom") return row.CustomText.Text.Trim().Length == 0;
            if (mode == "range") return row.RangeFrom.Text.Trim().Length == 0 || row.RangeTo.Text.Trim().Length == 0;
            return false;
        }

        private void MarkErrorField(string fieldKey)
        {
            foreach (var pair in _rows)
            {
                var isErr = fieldKey != null && pair.Key == fieldKey;
                pair.Value.RowBorder.BorderBrush = isErr ? (Brush)FindResource("Text.Error") : Brushes.Transparent;
            }
        }

        /// <summary>构造器任一控件改动：逐段生成表达式 → 整体渲染（不回写构造器）。</summary>
        private void OnBuilderChanged()
        {
            if (_applyingFields) return; // 回写期间状态本来就一致，不联动
            var texts = new string[CronExpr.Fields.Length];
            string failureKey = null, failureLabel = null, failureMessage = null;
            FieldRow failureRow = null;
            foreach (var def in CronExpr.Fields)
            {
                var row = _rows[def.Key];
                try
                {
                    var text = CronExpr.BuildField(def.Key, ReadState(row));
                    row.RowBorder.BorderBrush = Brushes.Transparent;
                    texts[Array.IndexOf(CronExpr.Fields, def)] = text;
                }
                catch (CronException ex)
                {
                    row.RowBorder.BorderBrush = (Brush)FindResource("Text.Error");
                    if (failureKey == null)
                    {
                        failureKey = def.Key;
                        failureLabel = def.Label;
                        failureMessage = ex.Message;
                        failureRow = row;
                    }
                }
            }
            if (failureKey != null)
            {
                if (IsIncomplete(failureRow))
                {
                    // 参数还没填完不算错误：不标红，只提示补齐（v1 同语义）
                    failureRow.RowBorder.BorderBrush = Brushes.Transparent;
                    SetStatus(null, failureLabel + "段还没填完，补齐参数后生效");
                    return;
                }
                SetStatus("err", failureLabel + "段：" + failureMessage);
                return;
            }
            _suppressExpr = true;
            try { ExprBox.Text = string.Join(" ", texts); }
            finally { _suppressExpr = false; }
            RenderExpression(ExprBox.Text, applyBuilder: false);
        }

        // ---- 表达式渲染 ----

        private void OnExprChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressExpr) return; // 构造器生成的表达式，由 OnBuilderChanged 自己渲染
            RenderExpression(ExprBox.Text);
        }

        private void RenderExpression(string text)
        {
            RenderExpression(text, applyBuilder: true);
        }

        private void RenderExpression(string text, bool applyBuilder)
        {
            var source = (text ?? "").Trim();
            if (source.Length == 0)
            {
                SetStatus(null, "输入表达式，或在构造器里点选");
                SummaryText.Text = "等待输入";
                MarkErrorField(null);
                OrRuleNote.Visibility = Visibility.Collapsed;
                RunsList.Children.Clear();
                return;
            }

            var parsed = CronExpr.Parse(source);
            if (!parsed.Ok)
            {
                SetStatus("err", parsed.Error.Message);
                MarkErrorField(parsed.Error.FieldKey);
                SummaryText.Text = "表达式无效";
                OrRuleNote.Visibility = Visibility.Collapsed;
                RenderRuns(null);
                return;
            }

            SetStatus("ok", parsed.Note ?? "表达式合法");
            if (parsed.Fields != null && applyBuilder)
            {
                // 回写构造器（仅表达式侧改动时；构造器侧改动状态已一致，v1 applyBuilder:false 同语义）
                _applyingFields = true;
                try
                {
                    foreach (var field in parsed.Fields)
                        if (_rows.ContainsKey(field.Key))
                            ApplyState(_rows[field.Key], CronExpr.FieldToBuilder(field));
                }
                finally { _applyingFields = false; }
            }
            MarkErrorField(null);
            SummaryText.Text = parsed.Summary;
            OrRuleNote.Visibility = parsed.OrRule ? Visibility.Visible : Visibility.Collapsed;
            RenderRuns(parsed);
        }

        private void OnCountChanged(object sender, SelectionChangedEventArgs e)
        {
            // XAML 加载期 IsSelected="True" 就会触发本回调，此时后排行（OrRuleNote 等）尚未构造
            if (OrRuleNote == null) return;
            RenderExpression(ExprBox.Text);
        }

        // ---- 最近触发 ----

        private void RenderRuns(CronParseResult parsed)
        {
            RunsList.Children.Clear();
            if (parsed == null)
            {
                AddRunEmpty("表达式无效，无法推算触发时间");
                return;
            }
            var count = new[] { 5, 10, 20 }[Math.Max(0, Math.Min(CountBox.SelectedIndex, 2))];
            var result = CronExpr.NextRuns(parsed.Source, count, DateTime.Now);
            if (!result.Ok)
            {
                AddRunEmpty(result.Error.Message);
                return;
            }
            if (result.Runs.Count == 0)
            {
                AddRunEmpty(result.Note ?? "在可预见范围内（4 年）不会触发");
                return;
            }
            for (var i = 0; i < result.Runs.Count; i++)
            {
                var date = result.Runs[i];
                var line = new Grid { Margin = new Thickness(0, 0, 0, 2) };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var indexText = i == 0 ? "下一次" : (i + 1).ToString();
                AddRunCell(line, 0, indexText, i == 0 ? "Text.Primary" : "Text.Secondary");
                AddRunCell(line, 1, date.ToString("yyyy-MM-dd HH:mm:ss"), i == 0 ? "Text.Primary" : "Text.Secondary", mono: true);
                AddRunCell(line, 2, WeekLabel((int)date.DayOfWeek), "Text.Secondary");
                AddRunCell(line, 3, RelativeText(date), "Text.Secondary", right: true);

                var wrap = new Border
                {
                    Background = (Brush)FindResource(i == 0 ? "Bg.ItemSelected" : "Bg.Window"),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 3, 6, 3),
                    Child = line,
                };
                RunsList.Children.Add(wrap);
            }
            if (result.Truncated) AddRunEmpty("4 年内只有这些触发时间");
        }

        private void AddRunCell(Grid line, int column, string text, string brushKey, bool mono = false, bool right = false)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = (Brush)FindResource(brushKey),
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (mono) block.FontFamily = new FontFamily("Consolas");
            if (right) block.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(block, column);
            line.Children.Add(block);
        }

        private void AddRunEmpty(string text)
        {
            RunsList.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = (Brush)FindResource("Text.Placeholder"),
                Margin = new Thickness(2, 2, 0, 0),
            });
        }

        private static string RelativeText(DateTime date)
        {
            var diff = date - DateTime.Now;
            if (diff.TotalSeconds <= 1) return "即将触发";
            if (diff.TotalDays >= 1) return string.Format("{0} 天后", (int)diff.TotalDays);
            if (diff.TotalHours >= 1) return string.Format("{0} 小时后", (int)diff.TotalHours);
            if (diff.TotalMinutes >= 1) return string.Format("{0} 分钟后", (int)diff.TotalMinutes);
            return "即将触发";
        }

        // ---- 复制 / 状态栏 ----

        private void OnCopyClick(object sender, RoutedEventArgs e)
        {
            CopyExpression();
        }

        private void CopyExpression()
        {
            var text = ExprBox.Text.Trim();
            if (text.Length == 0)
            {
                SetStatus("err", "没有可复制的表达式");
                return;
            }
            try
            {
                Clipboard.SetText(text);
                SetStatus("ok", "已复制表达式");
            }
            catch (Exception ex)
            {
                SetStatus("err", "复制失败：" + ex.Message);
            }
        }

        private void SetStatus(string kind, string message)
        {
            StatusText.Text = message;
            StatusText.Foreground = kind == "ok" ? (Brush)FindResource("Text.Success")
                : kind == "err" ? (Brush)FindResource("Text.Error")
                : (Brush)FindResource("Text.Placeholder");
        }

        // ---- 动作面板（Ctrl+Enter，与启动器同一套约定）----

        private List<ActionItem> BuildCronActions()
        {
            return new List<ActionItem>
            {
                new ActionItem("copy", "复制表达式", "C", ExprBox.Text.Trim().Length > 0)
            };
        }

        private void EnterActionMode()
        {
            _actionMode = true;
            Actions.Show(RootBorder, BuildCronActions());
        }

        private void ExitActionModeInternal()
        {
            if (!_actionMode) return;
            _actionMode = false;
            Actions.Hide();
            ExprBox.Focus();
        }

        private void OnActionInvoked(string actionId)
        {
            if (actionId == "copy") CopyExpression();
            // 执行后即退出动作态（与其他视图语义一致）
            ExitActionModeInternal();
        }

        // ---- 键位 ----

        private void OnViewPreviewKeyDown(object sender, KeyEventArgs e)
        {
            var isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (!isCtrl || e.Key != Key.Enter) return;

            // Ctrl+Enter：动作面板开关（输入区有焦点也在此拦截，隧道事件先于 TextBox）
            if (_actionMode)
            {
                if (Actions.HandleKey(e)) e.Handled = true;
            }
            else
            {
                EnterActionMode();
                e.Handled = true;
            }
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
