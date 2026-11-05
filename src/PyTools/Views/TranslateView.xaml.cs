using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PyTools.Core;

namespace PyTools.Views
{
    /// <summary>
    /// AI 翻译视图（M4 AI 批第三个消费者，v1 tools/ai-translate 移植）：
    /// 语言条（源/目标/互换）+ 左右双栏（原文/译文，均可编辑）+ 状态栏。
    /// 与 v1 的差异：① 源语言「自动」不再预发 detect 请求，由模型自判并回 detectedLang；
    /// ② 译文可编辑（译文常需小改）；③ 打开时剪贴板有文本自动填入（复制外文即呼出的主路径）；
    /// ④ AI 生命周期只走状态栏（模型/用时/字数），不做常驻可用性徽标（v1 假绿教训）。
    /// </summary>
    public partial class TranslateView : UserControl, ISearchFocusable
    {
        private bool _actionMode;
        private bool _translating;      // 翻译请求进行中（不可重入）
        private string _lastDetected = ""; // 上次自动识别的源语言（互换时作新源）

        public TranslateView()
        {
            InitializeComponent();
            Actions.Invoked += OnActionInvoked;
            Actions.Dismissed += ExitActionModeInternal;
            PreviewKeyDown += OnViewPreviewKeyDown;
            PreviewMouseLeftButtonUp += OnViewPreviewMouseUp;
            UpdatePlaceholders();
            UpdateButtons();
        }

        public TextBox SearchBox { get { return SourceBox; } } // ISearchFocusable

        public void FocusSearch()
        {
            SourceBox.Focus();
            SourceBox.CaretIndex = SourceBox.Text.Length;
        }

        /// <summary>进入视图：剪贴板有文本且与当前原文不同 → 填入（复制新内容即呼出翻译的主路径）。</summary>
        public void ResetForTranslate()
        {
            ExitActionModeInternal();
            AutoFillFromClipboard();
            UpdatePlaceholders();
            UpdateButtons();
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

        // ---- 语言 ----

        private static readonly Dictionary<string, string> LangNames = new Dictionary<string, string>
        {
            { "zh-CN", "中文" },
            { "en", "英文" },
            { "ja", "日文" },
        };

        private static string LangTag(ComboBox box)
        {
            var item = box.SelectedItem as ComboBoxItem;
            return item == null ? "" : (string)(item.Tag ?? "");
        }

        private static string LangLabel(string tag)
        {
            string name;
            return LangNames.TryGetValue(tag ?? "", out name) ? name : "原文";
        }

        private static void SelectLang(ComboBox box, string tag)
        {
            foreach (var entry in box.Items)
            {
                var item = entry as ComboBoxItem;
                if (item == null) continue;
                if (string.Equals((string)(item.Tag ?? ""), tag ?? "", StringComparison.Ordinal))
                {
                    box.SelectedItem = item;
                    return;
                }
            }
        }

        private void OnLangChanged(object sender, SelectionChangedEventArgs e)
        {
            if (StatusText == null) return; // XAML 加载期初始选中触发
            var source = LangTag(SourceLangBox);
            var target = LangTag(TargetLangBox);
            if (source.Length > 0 && source == target)
                SetStatus("err", "源语言与目标语言相同，请调整其中一个");
            else if (!_translating)
                SetStatus(null, SourceBox.Text.Trim().Length == 0 ? "等待输入文本" : "待翻译");
        }

        // ---- 输入 / 占位提示 ----

        private void OnSourceTextChanged(object sender, TextChangedEventArgs e)
        {
            UpdatePlaceholders();
            UpdateButtons();
            if (StatusText == null || _translating) return;
            SetStatus(null, SourceBox.Text.Trim().Length == 0 ? "等待输入文本" : "待翻译");
        }

        private void OnResultTextChanged(object sender, TextChangedEventArgs e)
        {
            UpdatePlaceholders();
        }

        private void OnSourceFocusChanged(object sender, KeyboardFocusChangedEventArgs e)
        {
            UpdatePlaceholders();
        }

        private void OnResultFocusChanged(object sender, KeyboardFocusChangedEventArgs e)
        {
            UpdatePlaceholders();
        }

        /// <summary>watermark 惯例：无内容且未聚焦才显示（聚焦时光标会压着提示首字）。</summary>
        private void UpdatePlaceholders()
        {
            if (SourcePlaceholder == null) return;
            SourcePlaceholder.Visibility = SourceBox.Text.Length == 0 && !SourceBox.IsKeyboardFocusWithin
                ? Visibility.Visible : Visibility.Collapsed;
            ResultPlaceholder.Visibility = ResultBox.Text.Length == 0 && !ResultBox.IsKeyboardFocusWithin
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateButtons()
        {
            if (TranslateButton == null) return;
            var hasText = SourceBox.Text.Trim().Length > 0;
            TranslateButton.IsEnabled = hasText && !_translating;
            SwapButton.IsEnabled = hasText && !_translating;
        }

        private void AutoFillFromClipboard()
        {
            string text;
            try
            {
                if (!Clipboard.ContainsText()) return;
                text = Clipboard.GetText() ?? "";
            }
            catch { return; } // 剪贴板被其他程序占用：静默跳过，用户可手动 Ctrl+V

            if (text.Trim().Length == 0) return;
            if (string.Equals(text, SourceBox.Text, StringComparison.Ordinal)) return; // 同一份内容不重复覆盖
            SourceBox.Text = text;
            SetStatus(null, "已从剪贴板填入 " + text.Length + " 字");
        }

        // ---- 翻译 ----

        private async void StartTranslate()
        {
            if (_translating) return;
            var text = SourceBox.Text;
            if (text.Trim().Length == 0)
            {
                SetStatus("err", "请输入要翻译的文本");
                SourceBox.Focus();
                return;
            }
            var source = LangTag(SourceLangBox);
            var target = LangTag(TargetLangBox);
            if (source.Length > 0 && source == target)
            {
                SetStatus("err", "源语言与目标语言相同，请调整其中一个");
                return;
            }
            var status = AiClient.Status();
            if (!status.Available)
            {
                SetStatus("err", "AI 不可用: " + status.Reason);
                return;
            }

            _translating = true;
            UpdateButtons();
            SetStatus(null, "翻译中…");
            var watch = Stopwatch.StartNew();
            try
            {
                var result = await AiClient.TranslateAsync(source, target, text);
                watch.Stop();
                if (result.Ok)
                {
                    var detected = (result.DetectedLang ?? "").Trim().ToLowerInvariant();
                    if (source.Length == 0 && detected.Length > 0) _lastDetected = detected;
                    var fromTag = source.Length > 0 ? source : (detected.Length > 0 ? detected : "");
                    ResultBox.Text = result.Translation;
                    SetStatus("ok", LangLabel(fromTag) + " → " + LangLabel(target)
                        + " · " + result.Model
                        + " · " + (watch.ElapsedMilliseconds / 1000.0).ToString("0.0") + "s"
                        + " · " + result.Translation.Length + " 字");
                }
                else
                {
                    var reason = result.Error;
                    if (result.RequestFailed) reason += "（可在设置中更换模型）";
                    SetStatus("err", "翻译失败: " + reason);
                }
            }
            catch (Exception ex)
            {
                SetStatus("err", "翻译失败: " + ex.Message);
            }
            finally
            {
                _translating = false;
                UpdateButtons();
            }
        }

        // ---- 互换 / 复制 / 清空 ----

        private void OnSwapClick(object sender, RoutedEventArgs e)
        {
            SwapLanguages();
        }

        private void SwapLanguages()
        {
            if (_translating) return;
            var source = LangTag(SourceLangBox);
            var target = LangTag(TargetLangBox);
            if (source.Length == 0)
            {
                if (_lastDetected.Length == 0)
                {
                    SetStatus("err", "源语言是「自动检测」，请先翻译一次或手动选择源语言再互换");
                    return;
                }
                source = _lastDetected;
            }

            SelectLang(TargetLangBox, source);
            SelectLang(SourceLangBox, target);

            var translation = ResultBox.Text;
            if (translation.Trim().Length > 0)
            {
                // 回译场景：译文搬到原文栏，直接再翻一次
                SourceBox.Text = translation;
                ResultBox.Text = "";
                SetStatus(null, "已互换语言，译文已移到原文栏");
            }
            else
            {
                SetStatus(null, "已互换语言");
            }
            _lastDetected = target; // 新原文语种 = 原目标语言
            UpdatePlaceholders();
            UpdateButtons();
            SourceBox.Focus();
        }

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

        private void ClearAll()
        {
            SourceBox.Text = "";
            ResultBox.Text = "";
            _lastDetected = "";
            SetStatus(null, "已清空");
            UpdatePlaceholders();
            UpdateButtons();
            SourceBox.Focus();
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
            var hasText = SourceBox.Text.Trim().Length > 0;
            return new List<ActionItem>
            {
                new ActionItem("translate", "翻译", "T", hasText && !_translating),
                new ActionItem("copy-result", "复制译文", "C", ResultBox.Text.Trim().Length > 0),
                new ActionItem("swap", "互换语言", "S", hasText && !_translating),
                new ActionItem("paste", "从剪贴板填入", "V", !_translating),
                new ActionItem("clear", "清空", "X", hasText || ResultBox.Text.Length > 0),
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
            SourceBox.Focus();
        }

        private void OnActionInvoked(string actionId)
        {
            ExitActionModeInternal();
            if (actionId == "translate") { StartTranslate(); return; }
            if (actionId == "copy-result") { CopyWithStatus(ResultBox.Text, "没有可复制的译文", "译文已复制"); return; }
            if (actionId == "swap") { SwapLanguages(); return; }
            if (actionId == "paste") { AutoFillFromClipboard(); return; }
            if (actionId == "clear") { ClearAll(); return; }
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

        private void OnTranslateClick(object sender, RoutedEventArgs e)
        {
            StartTranslate();
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
