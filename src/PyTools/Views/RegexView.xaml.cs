using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PyTools.Core;

namespace PyTools.Views
{
    /// <summary>
    /// 正则测试视图（M4，T5）：表达式 + 替换串 + 4 标志位 → 左测试文本、右三块结果同屏实时刷新（方案 A）。
    /// 渲染逻辑对齐 v1 tools/regex-tool（等待/无效/无匹配三态文案照搬）；增强 = 匹配列表点选联动
    /// （点某条 → 高亮区滚动定位 + 闪烁标注）。AI 生成走动作面板（M4 AI 批第一个消费者，共用 AiClient）；
    /// 服务可达性只认真实请求结果，状态栏按请求生命周期呈现（无常驻徽标，2026-11-05 拍板）。
    /// </summary>
    public partial class RegexView : UserControl, ISearchFocusable
    {
        private bool _actionMode;
        private bool _generating;          // AI 请求进行中（不可重入）
        private DispatcherTimer _flashTimer; // 点选联动的闪烁还原（单发）
        private Run _flashingRun;

        /// <summary>高亮区 Run 与匹配序号的对应表（点选联动定位用）。</summary>
        private readonly List<KeyValuePair<Run, int>> _highlightRuns = new List<KeyValuePair<Run, int>>();

        public RegexView()
        {
            InitializeComponent();
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

        /// <summary>进入视图：保留上次输入（v1 面板语义：DOM 常驻不重置；正则调试常来回切）。</summary>
        public void ResetForRegex()
        {
            ExitActionModeInternal();
            Update();
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

        // ---- 实时渲染（v1 update() 对齐）----

        private void OnInputChanged(object sender, RoutedEventArgs e)
        {
            // XAML 加载期 IsChecked="True" 触发 Checked 时兄弟控件未构造（encode/cron 同款坑）
            if (StatusText == null || HighlightText == null) return;
            Update();
        }

        private void Update()
        {
            var pattern = ExprBox.Text;
            if (string.IsNullOrWhiteSpace(pattern))
            {
                SetStatus(null, "等待输入正则");
                ClearMatchList();
                SetPlaceholder(HighlightText, "等待输入正则");
                SetPlaceholder(ReplaceResult, "等待输入正则");
                return;
            }

            var result = RegexTester.Evaluate(pattern, TestBox.Text, FlagGlobal.IsChecked == true,
                FlagIgnoreCase.IsChecked == true, FlagMultiline.IsChecked == true, FlagDotAll.IsChecked == true);
            if (!result.Ok)
            {
                SetStatus("err", "正则无效: " + result.Error);
                ClearMatchList();
                SetPlaceholder(HighlightText, "正则无效");
                ReplaceResult.Inlines.Clear();
                return;
            }

            var matches = result.Matches;
            if (result.Truncated) SetStatus(null, "已显示前 " + RegexResult.MatchLimit + " 条匹配");
            else if (matches.Count > 0) SetStatus(null, "匹配 " + matches.Count + " 条");
            else SetStatus(null, "没有匹配");

            RenderMatchList(matches);
            RenderHighlight(TestBox.Text, matches);
            RenderReplacement(matches);
        }

        private void RenderMatchList(List<RegexMatchInfo> matches)
        {
            ClearMatchList();
            if (matches.Count == 0)
            {
                MatchList.Children.Add(ListLine("没找到匹配", null));
                return;
            }
            for (var i = 0; i < matches.Count; i++)
            {
                var match = matches[i];
                var border = new Border
                {
                    Background = (Brush)FindResource("Bg.Input"),
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(7, 5, 7, 5),
                    Margin = new Thickness(0, 0, 0, 5),
                    Cursor = Cursors.Hand,
                    Tag = i, // 点选联动：定位高亮区第 i 个 Run
                };
                var stack = new StackPanel();
                var head = new StackPanel { Orientation = Orientation.Horizontal };
                head.Children.Add(new TextBlock
                {
                    Text = "起始位置 " + (match.Index + 1),
                    FontSize = 11,
                    Foreground = (Brush)FindResource("Text.Placeholder"),
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                head.Children.Add(new TextBlock
                {
                    Text = match.Value.Length == 0 ? "(空匹配)" : match.Value,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    Foreground = (Brush)FindResource("Text.Primary"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                stack.Children.Add(head);
                foreach (var group in match.Groups)
                {
                    var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 1, 0, 0) };
                    line.Children.Add(new TextBlock
                    {
                        Text = group.Label,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 11,
                        Foreground = (Brush)FindResource("Accent"),
                        MinWidth = 60,
                    });
                    line.Children.Add(new TextBlock
                    {
                        Text = group.Value ?? "(空)",
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 11,
                        Foreground = (Brush)FindResource("Text.Secondary"),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    });
                    stack.Children.Add(line);
                }
                border.Child = stack;
                var capturedIndex = i;
                border.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e)
                {
                    LocateInHighlight(capturedIndex);
                    e.Handled = true;
                };
                MatchList.Children.Add(border);
            }
        }

        private static FrameworkElement ListLine(string text, string detail)
        {
            return new TextBlock
            {
                Text = detail == null ? text : text + " " + detail,
                FontSize = 12,
                Foreground = (Brush)Application.Current.TryFindResource("Text.Placeholder"),
                Margin = new Thickness(2, 2, 0, 0),
            };
        }

        private void ClearMatchList()
        {
            MatchList.Children.Clear();
        }

        private void RenderHighlight(string text, List<RegexMatchInfo> matches)
        {
            _highlightRuns.Clear();
            HighlightText.Inlines.Clear();
            if (matches.Count == 0)
            {
                HighlightText.Inlines.Add(new Run("没有可高亮的匹配")
                {
                    Foreground = (Brush)FindResource("Text.Placeholder"),
                });
                return;
            }
            var cursor = 0;
            for (var i = 0; i < matches.Count; i++)
            {
                var match = matches[i];
                if (match.Index > cursor)
                    HighlightText.Inlines.Add(new Run(text.Substring(cursor, match.Index - cursor)));
                var value = match.Value.Length == 0 ? "(空匹配)" : match.Value; // 零长匹配给可见占位，否则高亮看不见
                var run = new Run(value)
                {
                    Background = (Brush)FindResource("Accent.Soft"),
                    Foreground = (Brush)FindResource("Accent"),
                };
                HighlightText.Inlines.Add(run);
                _highlightRuns.Add(new KeyValuePair<Run, int>(run, i));
                cursor = match.Index + match.Value.Length;
            }
            if (cursor < text.Length)
                HighlightText.Inlines.Add(new Run(text.Substring(cursor)));
        }

        private void RenderReplacement(List<RegexMatchInfo> matches)
        {
            ReplaceResult.Inlines.Clear();
            if (matches.Count == 0)
            {
                SetPlaceholder(ReplaceResult, "没有可替换的匹配");
                return;
            }
            string output;
            try
            {
                output = RegexTester.Replace(ExprBox.Text, TestBox.Text, ReplaceBox.Text, FlagGlobal.IsChecked == true,
                    FlagIgnoreCase.IsChecked == true, FlagMultiline.IsChecked == true, FlagDotAll.IsChecked == true);
            }
            catch (ArgumentException ex)
            {
                SetStatus("err", "替换失败: " + ex.Message);
                return;
            }
            if (output.Length == 0)
            {
                SetPlaceholder(ReplaceResult, "替换后为空");
                return;
            }
            ReplaceResult.Inlines.Add(new Run(output));
        }

        private static void SetPlaceholder(TextBlock block, string text)
        {
            block.Inlines.Clear();
            block.Inlines.Add(new Run(text)
            {
                Foreground = (Brush)Application.Current.TryFindResource("Text.Placeholder"),
            });
        }

        private void SetStatus(string kind, string message)
        {
            StatusText.Text = message;
            // 状态栏单行显示，长文本（如 AI 的 reason）会被截断出省略号：悬停气泡给全文
            StatusText.ToolTip = string.IsNullOrEmpty(message) ? null : message;
            StatusText.Foreground = kind == "err" ? (Brush)FindResource("Text.Error")
                : kind == "ok" ? (Brush)FindResource("Accent")
                : (Brush)FindResource("Text.Placeholder");
        }

        // ---- 点选联动：滚动定位 + 闪烁标注 ----

        private void LocateInHighlight(int matchIndex)
        {
            foreach (var pair in _highlightRuns)
            {
                if (pair.Value != matchIndex) continue;
                pair.Key.BringIntoView(); // FrameworkContentElement：让 ScrollViewer 滚到该 Run
                StartFlash(pair.Key);
                return;
            }
        }

        private void StartFlash(Run run)
        {
            if (_flashTimer == null)
            {
                _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
                _flashTimer.Tick += delegate
                {
                    _flashTimer.Stop();
                    if (_flashingRun != null)
                        _flashingRun.Background = (Brush)FindResource("Accent.Soft"); // 还原为常态高亮底
                    _flashingRun = null;
                };
            }
            if (_flashingRun != null)
                _flashingRun.Background = (Brush)FindResource("Accent.Soft");
            _flashingRun = run;
            run.Background = (Brush)FindResource("Accent"); // 短暂加深 → 650ms 后还原
            _flashTimer.Stop();
            _flashTimer.Start();
        }

        // ---- 复制 / 状态栏 ----

        private void CopyWithStatus(string text, string emptyHint, string doneLabel)
        {
            if (string.IsNullOrEmpty(text))
            {
                SetStatus("err", emptyHint);
                return;
            }
            try
            {
                Clipboard.SetText(text);
                SetStatus(null, doneLabel);
            }
            catch (Exception ex)
            {
                SetStatus("err", "复制失败：" + ex.Message);
            }
        }

        // ---- 动作面板（Ctrl+Enter，与启动器同一套约定）----

        private List<ActionItem> BuildActions()
        {
            return new List<ActionItem>
            {
                new ActionItem("copy-pattern", "复制表达式", "P", ExprBox.Text.Trim().Length > 0),
                new ActionItem("copy-replaced", "复制替换结果", "R", ReplaceResult.Text.Length > 0
                    && ReplaceResult.Text != "没有可替换的匹配" && ReplaceResult.Text != "替换后为空"),
                new ActionItem("copy-text", "复制测试文本", "T", TestBox.Text.Length > 0),
                // AI 未配置时不列出（配置级状态用确定性载体表达）；生成中不可重入
                new ActionItem("ai-generate", "AI 生成正则", "A", !_generating && AiClient.Status().Available),
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
            ExprBox.Focus();
        }

        private void OnActionInvoked(string actionId)
        {
            if (actionId == "copy-pattern") CopyWithStatus(ExprBox.Text.Trim(), "没有可复制的表达式", "已复制表达式");
            else if (actionId == "copy-replaced") CopyWithStatus(ReplaceResult.Text, "没有可复制的替换结果", "已复制替换结果");
            else if (actionId == "copy-text") CopyWithStatus(TestBox.Text, "测试文本为空", "已复制测试文本");
            else if (actionId == "ai-generate") { ExitActionModeInternal(); StartAiGenerate(); return; }
            // 执行后即退出动作态（与其他视图语义一致）
            ExitActionModeInternal();
        }

        // ---- AI 生成（v1 regex-tool generateAi() 语义）----

        private async void StartAiGenerate()
        {
            if (_generating) return;
            var prompt = TestBox.Text.Trim();
            if (prompt.Length == 0)
            {
                SetStatus("err", "请先把目标或样例写到测试文本里，再让 AI 生成");
                TestBox.Focus();
                return;
            }
            var status = AiClient.Status();
            if (!status.Available)
            {
                SetStatus("err", "AI 不可用: " + status.Reason);
                return;
            }
            _generating = true;
            SetStatus(null, "AI 生成中…");
            try
            {
                var result = await AiClient.GenerateRegexAsync(prompt);
                if (result.Ok)
                {
                    // 先设标志位再填表达式（最后一次 TextChanged 用完整状态刷新三块结果）
                    FlagGlobal.IsChecked = result.Flags.Contains("g");
                    FlagIgnoreCase.IsChecked = result.Flags.Contains("i");
                    FlagMultiline.IsChecked = result.Flags.Contains("m");
                    FlagDotAll.IsChecked = result.Flags.Contains("s");
                    ExprBox.Text = result.Pattern;
                    Update(); // 表达式与原值相同时 TextChanged 不触发，手动兜底
                    SetStatus("ok", "AI 已生成 · " + result.Model + " · 置信度 " + result.Confidence
                        + (result.Reason.Length > 0 ? " · " + result.Reason : ""));
                }
                else
                {
                    // 请求类失败（超时/网络/HTTP）→ 指路设置窗口换模型；格式类失败不指（换模型无用）
                    var text = result.Error;
                    if (result.RequestFailed) text += "（可在设置中更换模型）";
                    SetStatus("err", "AI 生成失败: " + text);
                }
            }
            catch (Exception ex)
            {
                SetStatus("err", "AI 生成失败: " + ex.Message);
            }
            finally
            {
                _generating = false;
            }
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
