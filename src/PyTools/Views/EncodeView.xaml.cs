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
    /// 编码转换视图（M4）：URL/Base64/Unicode/HTML 实体/JSON/Hex 六格式双向实时转换。
    /// 进入时预填剪贴板（只读不写回，结果进剪贴板走「复制结果」动作）；格式/方向记住上次选择。
    /// 动作面板复用 Views/ActionPanel；键位照全局约定（Ctrl+Enter / Esc 由窗口级路由）。
    /// </summary>
    public partial class EncodeView : UserControl, ISearchFocusable
    {
        private bool _actionMode;
        private bool _loadingOptions;   // 恢复上次选择期间不触发转换/保存

        /// <summary>配置（App 注入）：格式/方向选择持久化到 v2.encodeFormat / v2.encodeDirection。</summary>
        public AppConfig Config { get; set; }

        public EncodeView()
        {
            InitializeComponent();
            Actions.Invoked += OnActionInvoked;
            Actions.Dismissed += ExitActionModeInternal;
            PreviewKeyDown += OnViewPreviewKeyDown;
            PreviewMouseLeftButtonUp += OnViewPreviewMouseUp; // 状态栏「操作」键帽的鼠标呼出
        }

        public TextBox SearchBox { get { return InputBox; } } // ISearchFocusable：焦点交给输入区

        public void FocusSearch()
        {
            InputBox.Focus();
            InputBox.CaretIndex = InputBox.Text.Length;
        }

        /// <summary>
        /// 进入视图：预填剪贴板文本 → 恢复上次格式/方向 → 立即转换显示结果。
        /// 空剪贴板/图片/文件时输入区留空（正常手动输入）；只读不写回。
        /// </summary>
        public void ResetForEncode()
        {
            ExitActionModeInternal();

            _loadingOptions = true;
            SelectFormat(Config == null ? "url" : Config.EncodeFormat);
            SelectDirection(Config == null ? "encode" : Config.EncodeDirection);
            _loadingOptions = false;
            UpdateDirectionHint();

            string clip = null;
            try { if (Clipboard.ContainsText()) clip = Clipboard.GetText(); }
            catch { /* 剪贴板读取失败按空处理 */ }
            InputBox.Text = clip ?? "";
            InputBox.CaretIndex = InputBox.Text.Length;
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

        // ---- 选项（格式 / 方向）----

        private static readonly string[] FormatIds = { "url", "base64", "unicode", "html", "json", "hex" };

        private string SelectedFormat()
        {
            return SelectedRadioTag(FormatUrlRadio, FormatBase64Radio, FormatUnicodeRadio, FormatHtmlRadio, FormatJsonRadio, FormatHexRadio);
        }

        private string SelectedDirection()
        {
            return DirectionDecodeRadio.IsChecked == true ? "decode" : "encode";
        }

        private static string SelectedRadioTag(params RadioButton[] radios)
        {
            foreach (var radio in radios)
                if (radio.IsChecked == true) return (string)radio.Tag;
            return (string)radios[0].Tag;
        }

        private void SelectFormat(string format)
        {
            foreach (var id in FormatIds)
                if (id == format) { FindFormatRadio(id).IsChecked = true; return; }
            FormatUrlRadio.IsChecked = true;
        }

        private RadioButton FindFormatRadio(string id)
        {
            if (id == "base64") return FormatBase64Radio;
            if (id == "unicode") return FormatUnicodeRadio;
            if (id == "html") return FormatHtmlRadio;
            if (id == "json") return FormatJsonRadio;
            if (id == "hex") return FormatHexRadio;
            return FormatUrlRadio;
        }

        private void SelectDirection(string direction)
        {
            if (direction == "decode") DirectionDecodeRadio.IsChecked = true;
            else DirectionEncodeRadio.IsChecked = true;
        }

        private void OnOptionChanged(object sender, RoutedEventArgs e)
        {
            // XAML 加载期间 IsChecked="True" 就会触发 Checked，此时兄弟控件尚未构造，跳过
            if (_loadingOptions || DirectionDecodeRadio == null) return;
            UpdateDirectionHint();
            if (Config != null) Config.SaveEncodeChoice(SelectedFormat(), SelectedDirection());
            Convert();
        }

        /// <summary>方向示例提示（v1 方向下拉的选项文案）。</summary>
        private void UpdateDirectionHint()
        {
            string encode, decode;
            switch (SelectedFormat())
            {
                case "base64": encode = "编码 → Base64"; decode = "解码 → UTF-8"; break;
                case "unicode": encode = "转义 → \\uXXXX"; decode = "反转义 → 文本"; break;
                case "html": encode = "编码 → HTML 实体"; decode = "解码 → 文本"; break;
                case "json": encode = "标准 JSON → JSON 字符串"; decode = "JSON 字符串 → 标准 JSON"; break;
                case "hex": encode = "文本 → Hex"; decode = "Hex → UTF-8"; break;
                default: encode = "编码 → %E4%B8%AD"; decode = "解码 → 文本"; break;
            }
            DirectionHint.Text = SelectedDirection() == "encode" ? encode : decode;
        }

        // ---- 实时转换 ----

        private void OnInputChanged(object sender, TextChangedEventArgs e)
        {
            if (_loadingOptions) return;
            Convert();
        }

        private void Convert()
        {
            var input = InputBox.Text;
            if (input.Length == 0)
            {
                ResultBox.Clear();
                SetStatus(null, "Ctrl+Enter 操作 · Esc 返回启动器");
                return;
            }
            try
            {
                ResultBox.Text = TextEncoders.Convert(input, SelectedFormat(), SelectedDirection());
                SetStatus("ok", "转换完成");
            }
            catch (FormatException err)
            {
                ResultBox.Text = input; // 与 v1 一致：失败时结果区原样保留输入
                SetStatus("err", "无法按当前方向转换，已原样保留输入（" + err.Message + "）");
            }
        }

        // ---- 状态栏 ----

        private void SetStatus(string kind, string message)
        {
            StatusText.Text = message;
            StatusText.Foreground = kind == "ok" ? (Brush)FindResource("Text.Success")
                : kind == "err" ? (Brush)FindResource("Text.Error")
                : (Brush)FindResource("Text.Placeholder");
        }

        // ---- 动作面板（Ctrl+Enter，与启动器同一套约定）----

        private List<ActionItem> BuildEncodeActions()
        {
            return new List<ActionItem>
            {
                new ActionItem("copy", "复制结果", "C", ResultBox.Text.Length > 0)
            };
        }

        private void EnterActionMode()
        {
            _actionMode = true;
            Actions.Show(RootBorder, BuildEncodeActions());
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
            if (actionId == "copy")
            {
                try
                {
                    Clipboard.SetText(ResultBox.Text ?? "");
                    SetStatus("ok", "已复制结果（" + (ResultBox.Text ?? "").Length + " 字符）");
                }
                catch (Exception ex) { SetStatus("err", "复制到剪贴板失败: " + ex.Message); }
            }
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
