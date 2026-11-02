using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PyTools.Core;

namespace PyTools.Views
{
    /// <summary>
    /// 格式化视图（M4）：JSON/XML 结构化数据格式化。工具栏 + 等宽编辑区 + 状态栏。
    /// 面板内「格式化剪贴板」= 编辑区 + 剪贴板双写（v1 语义）；动作面板复用 Views/ActionPanel。
    /// </summary>
    public partial class FormatView : UserControl, ISearchFocusable
    {
        private bool _actionMode;
        private string _lastContent;   // 撤销：上次处理前的内容（与 v1 lastContent 一致）

        public FormatView()
        {
            InitializeComponent();
            Actions.Invoked += OnActionInvoked;
            Actions.Dismissed += ExitActionModeInternal;
            PreviewKeyDown += OnViewPreviewKeyDown;
            PreviewMouseLeftButtonUp += OnViewPreviewMouseUp; // 状态栏「操作」键帽的鼠标呼出
        }

        public TextBox SearchBox { get { return Editor; } } // ISearchFocusable：焦点交给编辑区

        public void FocusSearch()
        {
            Editor.Focus();
            Editor.CaretIndex = Editor.Text.Length;
        }

        /// <summary>进入视图：退出动作态、状态栏回默认提示。编辑区内容保留（工具语义，与 v1 面板一致）。</summary>
        public void ResetForFormat()
        {
            ExitActionModeInternal();
            SetStatus(null, "Ctrl+Enter 操作 · Esc 返回启动器");
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

        // ---- 状态栏 ----

        private void SetStatus(string kind, string message)
        {
            StatusText.Text = message;
            StatusText.Foreground = kind == "ok" ? (Brush)FindResource("Text.Success")
                : kind == "err" ? (Brush)FindResource("Text.Error")
                : (Brush)FindResource("Text.Placeholder");
        }

        // ---- 处理动作 ----

        private void OnFormatClipboard(object sender, RoutedEventArgs e) { RunFromClipboard(); }
        private void OnFormatInput(object sender, RoutedEventArgs e) { RunFromEditor("format"); }
        private void OnMinify(object sender, RoutedEventArgs e) { RunFromEditor("minify"); }
        private void OnCopy(object sender, RoutedEventArgs e) { CopyResult(); }
        private void OnUndo(object sender, RoutedEventArgs e) { Undo(); }
        private void OnOptionChanged(object sender, RoutedEventArgs e) { /* 选择即时生效，下次处理时读取 */ }

        private void OnEditorChanged(object sender, TextChangedEventArgs e)
        {
            UndoButton.IsEnabled = _lastContent != null;
        }

        private string SelectedFormat()
        {
            if (FormatJsonRadio.IsChecked == true) return "json";
            if (FormatXmlRadio.IsChecked == true) return "xml";
            return "auto";
        }

        private string SelectedIndent()
        {
            if (Indent4Radio.IsChecked == true) return "4";
            if (IndentTabRadio.IsChecked == true) return "tab";
            return "2";
        }

        /// <summary>读取剪贴板 → 格式化 → 编辑区 + 剪贴板双写（v1 语义）。返回错误文案或 null。</summary>
        private string RunFromClipboard()
        {
            string input;
            try { input = Clipboard.GetText(); }
            catch (Exception ex) { SetStatus("err", "读取剪贴板失败: " + ex.Message); return "读取剪贴板失败: " + ex.Message; }
            return ProcessAndApply(input, "format", writeBack: true);
        }

        private string RunFromEditor(string mode)
        {
            return ProcessAndApply(Editor.Text, mode, writeBack: false);
        }

        private string ProcessAndApply(string input, string mode, bool writeBack)
        {
            if (string.IsNullOrWhiteSpace(input)) { SetStatus("err", "没有内容可处理"); return "没有内容可处理"; }
            try
            {
                _lastContent = input;
                var result = StructuredFormats.Process(input, mode, SelectedFormat(), SelectedIndent());
                Editor.Text = result.Output;
                var label = mode == "minify" ? "压缩" : "格式化";
                var name = result.Format == "json" ? "JSON" : "XML";
                if (writeBack)
                {
                    try { Clipboard.SetText(result.Output); }
                    catch (Exception ex) { SetStatus("err", "写回剪贴板失败: " + ex.Message); return "写回剪贴板失败: " + ex.Message; }
                    SetStatus("ok", "剪贴板已" + label + "并写回（" + name + "，" + input.Length + " → " + result.Output.Length + " 字符）");
                }
                else
                {
                    SetStatus("ok", name + " " + label + "完成（" + input.Length + " → " + result.Output.Length + " 字符）");
                }
                return null;
            }
            catch (FormatException err)
            {
                Editor.Text = input; // 与 v1 一致：失败时回填原文
                SetStatus("err", err.Message);
                return err.Message;
            }
        }

        private void CopyResult()
        {
            try
            {
                Clipboard.SetText(Editor.Text ?? "");
                SetStatus("ok", "已复制到剪贴板");
            }
            catch (Exception ex) { SetStatus("err", "复制到剪贴板失败: " + ex.Message); }
        }

        private void Undo()
        {
            if (_lastContent == null) return;
            var restored = _lastContent;
            _lastContent = null; // 与 v1 一致：撤销回到处理前，Undo 按钮随后禁用
            Editor.Text = restored;
            SetStatus("ok", "已恢复上次内容");
        }

        // ---- 动作面板（Ctrl+Enter，与启动器同一套约定）----

        private List<ActionItem> BuildFormatActions()
        {
            return new List<ActionItem>
            {
                new ActionItem("fmt-clipboard", "格式化剪贴板并写回", "F"),
                new ActionItem("fmt-input",     "格式化输入",         "I"),
                new ActionItem("minify",        "压缩",               "M"),
                new ActionItem("copy",          "复制结果",           "C"),
                new ActionItem("undo",          "撤销",               "U", _lastContent != null)
            };
        }

        private void EnterActionMode()
        {
            _actionMode = true;
            Actions.Show(RootBorder, BuildFormatActions());
        }

        private void ExitActionModeInternal()
        {
            if (!_actionMode) return;
            _actionMode = false;
            Actions.Hide();
            Editor.Focus();
        }

        private void OnActionInvoked(string actionId)
        {
            string error;
            switch (actionId)
            {
                case "fmt-clipboard": error = RunFromClipboard(); break;
                case "fmt-input": error = RunFromEditor("format"); break;
                case "minify": error = RunFromEditor("minify"); break;
                case "copy": CopyResult(); error = null; break;
                case "undo": Undo(); error = null; break;
                default: error = "未知操作：" + actionId; break;
            }
            // 执行后即退出动作态（面板组件触发时已自行收起）：结果进状态栏，焦点回编辑区，与其他视图语义一致
            ExitActionModeInternal();
        }

        // ---- 键位 ----

        private void OnViewPreviewKeyDown(object sender, KeyEventArgs e)
        {
            var isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (!isCtrl || e.Key != Key.Enter) return;

            // Ctrl+Enter：动作面板开关（编辑区有焦点也在此拦截，隧道事件先于 TextBox）
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

        /// <summary>鼠标点状态栏「操作 Ctrl+Enter」键帽呼出动作面板（根级隧道事件 + hit test，与其他视图同一实现）。</summary>
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
