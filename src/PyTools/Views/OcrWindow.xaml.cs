using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PyTools.Core;

namespace PyTools.Views
{
    /// <summary>
    /// 图片文字识别（M4 AI 批第二个消费者）：独立常驻窗口（2026-11-05 拍板，与 diff 同族）。
    /// 状态机：展开双栏（初始/换图）→ 识别中（图片卡缩成 120×90、信息卡报进度）→ 已识别（结果可编辑，
    /// 单击缩略卡回双栏）。AI 生命周期只走左侧信息卡，状态栏只报通用操作反馈（拍板 #10）。
    /// </summary>
    public partial class OcrWindow : Window
    {
        private const double ThumbWidth = 120;
        private const double ThumbHeight = 90;
        private const double Gap = 10;
        private const int CollapseMs = 220;

        private OcrImageRecord _image;
        private bool _collapsed;        // 图片卡是否处于缩略态
        private bool _recognizing;
        private bool _animating;
        private bool _actionMode;
        private bool _closed;

        // 信息卡状态（识别成功/失败/未识别三态互斥）
        private bool _hasResult;
        private string _lastError;
        private string _lastModel = "";
        private long _lastElapsedMs;

        private DispatcherTimer _dotsTimer;   // 「识别中…」的动态省略号（主题无进度条样式，纯文本更稳）
        private int _dotsStep;
        private Brush _checker;               // 图片底衬棋盘格：白色截图贴浅色卡片才看得见边界

        public OcrWindow()
        {
            InitializeComponent();
            Actions.Invoked += OnActionInvoked;
            Actions.Dismissed += delegate { ExitActionModeInternal(); Root.Focus(); };
            _checker = BuildCheckerBrush();
            Root.Focusable = true; // 初始焦点落窗口内容而非结果框：光标不压占位提示（2026-11-05 验收反馈）
            ApplyDefaultSize();
            ToolTipService.SetInitialShowDelay(ImageCard, 150);
            Loaded += OnWindowLoaded;
            Body.SizeChanged += delegate
            {
                if (!_collapsed && !_animating) ApplyExpandedLayout();
            };
            Closed += delegate { _closed = true; StopDots(); };
        }

        /// <summary>16px 棋盘格（白 + 浅灰）：截图多为白底，直接贴卡片底色会「看似没载入」。</summary>
        private static Brush BuildCheckerBrush()
        {
            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing
            {
                Brush = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
                Geometry = new RectangleGeometry(new Rect(0, 0, 16, 16)),
            });
            group.Children.Add(new GeometryDrawing
            {
                Brush = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
                Geometry = new RectangleGeometry(new Rect(0, 0, 8, 8)),
            });
            group.Children.Add(new GeometryDrawing
            {
                Brush = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
                Geometry = new RectangleGeometry(new Rect(8, 8, 8, 8)),
            });
            var brush = new DrawingBrush
            {
                Drawing = group,
                Viewport = new Rect(0, 0, 16, 16),
                ViewportUnits = BrushMappingMode.Absolute,
                TileMode = TileMode.Tile,
            };
            brush.Freeze();
            return brush;
        }

        /// <summary>默认尺寸（v1 口径）：工作区 70%×78%，钳制在 720×460 ~ 1200×900。</summary>
        private void ApplyDefaultSize()
        {
            var workArea = SystemParameters.WorkArea;
            var width = Math.Min(Math.Max(workArea.Width * 0.7, 900), 1200);
            var height = Math.Min(Math.Max(workArea.Height * 0.78, 560), 900);
            if (width > workArea.Width - 16) width = Math.Max(600, workArea.Width - 16);
            if (height > workArea.Height - 16) height = Math.Max(420, workArea.Height - 16);
            Width = width;
            Height = height;
            Left = workArea.Left + (workArea.Width - width) / 2;
            Top = workArea.Top + (workArea.Height - height) / 2;
        }

        /// <summary>层叠偏移（2026-11-05 拍板）：已有独立窗口打开时右移下移各 32px，避免完全盖住。</summary>
        public void ApplyCascade(int steps)
        {
            if (steps <= 0) return;
            var workArea = SystemParameters.WorkArea;
            var left = Left + steps * 32;
            var top = Top + steps * 32;
            Left = Math.Max(workArea.Left, Math.Min(left, workArea.Right - Width));
            Top = Math.Max(workArea.Top, Math.Min(top, workArea.Bottom - Height));
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            ApplyExpandedLayout();
            UpdateInfoCard();
            UpdateButtons();
            LoadClipboardImage(true); // 打开即尝试填入剪贴板图片（截图后直接呼出的典型链路）
            Root.Focus(); // 焦点不进结果框：光标不压占位提示，Ctrl+V 由窗口级统一响应
        }

        // ---- 图片来源（剪贴板 / 文件 / 拖拽 / Ctrl+V，四入口共用 ApplyImage）----

        private void OnReadClipboardClick(object sender, RoutedEventArgs e)
        {
            LoadClipboardImage(false);
        }

        private void OnChooseFileClick(object sender, RoutedEventArgs e)
        {
            ChooseFile();
        }

        private void ChooseFile()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择要识别的图片",
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp",
                CheckFileExists = true,
            };
            if (dialog.ShowDialog(this) != true) return;
            LoadFile(dialog.FileName);
        }

        private void LoadClipboardImage(bool auto)
        {
            if (_recognizing) return;
            var record = OcrImage.FromClipboard();
            if (!record.Ok)
            {
                // 打开时的自动探测失败是常态（剪贴板没图），静默保持等待态；手动点的才报原因
                if (auto) SetStatus(null, "等待图片");
                else SetStatus("warn", record.Error);
                ToolbarHint.Text = "";
                return;
            }
            ApplyImage(record, "已从剪贴板载入图片");
            ToolbarHint.Text = auto ? "已从剪贴板自动载入" : "";
        }

        private void LoadFile(string path)
        {
            if (_recognizing) return;
            var record = OcrImage.FromFile(path);
            if (!record.Ok)
            {
                SetStatus("error", record.Error);
                return;
            }
            ApplyImage(record, "已载入 " + System.IO.Path.GetFileName(path));
            ToolbarHint.Text = "";
        }

        /// <summary>装载图片：换图一律清空旧结果并恢复双栏（避免结果与新图不对应，2026-11-05 拍板）。</summary>
        private void ApplyImage(OcrImageRecord record, string statusText)
        {
            _image = record;
            var bitmap = record.ToBitmap();
            if (bitmap == null)
            {
                _image = null;
                SetStatus("error", "图片无法显示");
                UpdateInfoCard();
                UpdateButtons();
                return;
            }
            PreviewImage.Source = bitmap;
            CardHost.Background = _checker; // 棋盘格底衬：白色图片也有可见边界（Image 无 Background 属性，放宿主 Grid）
            PreviewImage.ToolTip = null;
            EmptyHint.Visibility = Visibility.Collapsed;

            _hasResult = false;
            _lastError = null;
            _lastModel = "";
            _lastElapsedMs = 0;
            ResultBox.Text = "";       // 触发 OnResultTextChanged：占位提示与字符数归零
            ResultMeta.Text = "";

            if (_collapsed) Expand(true);
            UpdateInfoCard();
            UpdateButtons();
            SetStatus(null, statusText + "（" + record.Width + "×" + record.Height + "，" + FormatSize(record.ByteCount) + "）");
        }

        private void OnClearClick(object sender, RoutedEventArgs e)
        {
            ClearAll();
        }

        /// <summary>清空 = 图 + 结果 + 信息卡一起复位回初始态（拍板 5）。</summary>
        private void ClearAll()
        {
            if (_recognizing) return;
            _image = null;
            _hasResult = false;
            _lastError = null;
            _lastModel = "";
            _lastElapsedMs = 0;
            PreviewImage.Source = null;
            CardHost.Background = null;
            PreviewImage.ToolTip = null;
            EmptyHint.Visibility = Visibility.Visible;
            ResultBox.Text = "";
            ResultMeta.Text = "";
            ToolbarHint.Text = "";
            if (_collapsed) Expand(true);
            UpdateInfoCard();
            UpdateButtons();
            SetStatus(null, "等待图片");
        }

        // ---- 拖拽 ----

        private void OnWindowDragOver(object sender, DragEventArgs e)
        {
            e.Effects = HasSingleDropImage(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnWindowDrop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                SetStatus("warn", "拖入的内容不是图片文件");
                return;
            }
            var items = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (items == null || items.Length == 0)
            {
                SetStatus("warn", "拖入的内容不是图片文件");
                return;
            }
            if (items.Length > 1)
            {
                SetStatus("warn", "一次只能拖入一张图片");
                return;
            }
            LoadFile(items[0]);
        }

        private static bool HasSingleDropImage(IDataObject data)
        {
            if (data == null || !data.GetDataPresent(DataFormats.FileDrop)) return false;
            var items = data.GetData(DataFormats.FileDrop) as string[];
            if (items == null || items.Length != 1 || System.IO.Directory.Exists(items[0])) return false;
            var extension = (System.IO.Path.GetExtension(items[0]) ?? "").ToLowerInvariant();
            return extension == ".png" || extension == ".jpg" || extension == ".jpeg" || extension == ".bmp";
        }

        // ---- 布局：展开双栏 ↔ 缩略让位 ----

        private void ApplyExpandedLayout()
        {
            var bodyWidth = Body.ActualWidth;
            var bodyHeight = Body.ActualHeight;
            if (bodyWidth <= 0 || bodyHeight <= 0) return;
            var infoHeight = InfoCard.ActualHeight;
            if (infoHeight <= 0) infoHeight = 72;
            ImageCard.Width = Math.Max(260, Math.Round(bodyWidth * 0.42));
            ImageCard.Height = Math.Max(200, bodyHeight - infoHeight - Gap);
        }

        /// <summary>缩略态：图片卡动画到 120×90，结果区自然占满剩余宽度（Auto 列跟子元素宽度走）。</summary>
        private void Collapse()
        {
            if (_collapsed) return;
            _collapsed = true;
            ThumbHint.Visibility = Visibility.Visible;
            SetPreviewToolTip();
            AnimateCard(ThumbWidth, ThumbHeight);
        }

        private void Expand(bool immediate = false)
        {
            if (!_collapsed) return;
            var bodyWidth = Body.ActualWidth;
            var bodyHeight = Body.ActualHeight;
            var infoHeight = InfoCard.ActualHeight;
            if (infoHeight <= 0) infoHeight = 72;
            var toWidth = Math.Max(260, Math.Round((bodyWidth > 0 ? bodyWidth : 900) * 0.42));
            var toHeight = Math.Max(200, (bodyHeight > 0 ? bodyHeight : 500) - infoHeight - Gap);
            _collapsed = false;
            ThumbHint.Visibility = Visibility.Collapsed;
            ImageCard.ToolTip = null; // 展开后看的就是原图，气泡无意义
            if (immediate)
            {
                ImageCard.BeginAnimation(WidthProperty, null);
                ImageCard.BeginAnimation(HeightProperty, null);
                ImageCard.Width = toWidth;
                ImageCard.Height = toHeight;
                return;
            }
            AnimateCard(toWidth, toHeight);
        }

        /// <summary>宽度/高度同时动画（Auto 列随子元素测量 → 改卡片宽度即布局动画，无需自定义 GridLengthAnimation）。</summary>
        private void AnimateCard(double toWidth, double toHeight)
        {
            _animating = true;
            var duration = TimeSpan.FromMilliseconds(CollapseMs);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var fromWidth = ImageCard.ActualWidth > 0 ? ImageCard.ActualWidth : ImageCard.Width;
            var fromHeight = ImageCard.ActualHeight > 0 ? ImageCard.ActualHeight : ImageCard.Height;
            var remaining = 2;
            Action settle = delegate
            {
                if (--remaining > 0) return;
                _animating = false;
                ImageCard.BeginAnimation(WidthProperty, null);
                ImageCard.BeginAnimation(HeightProperty, null);
                ImageCard.Width = toWidth;
                ImageCard.Height = toHeight;
            };
            var widthAnim = new DoubleAnimation(fromWidth, toWidth, duration) { EasingFunction = ease };
            widthAnim.Completed += delegate { settle(); };
            var heightAnim = new DoubleAnimation(fromHeight, toHeight, duration) { EasingFunction = ease };
            heightAnim.Completed += delegate { settle(); };
            ImageCard.BeginAnimation(WidthProperty, widthAnim);
            ImageCard.BeginAnimation(HeightProperty, heightAnim);
        }

        /// <summary>缩略态 hover 看原图：ToolTip 装大图（系统管延迟/失焦，比自绘 Popup 稳）。</summary>
        private void SetPreviewToolTip()
        {
            if (PreviewImage.Source == null) return;
            var preview = new Image
            {
                Source = PreviewImage.Source,
                Stretch = Stretch.Uniform,
                MaxWidth = 860,
                MaxHeight = 680,
            };
            var panel = new StackPanel();
            panel.Children.Add(preview);
            panel.Children.Add(new TextBlock
            {
                Text = _image != null ? _image.Width + "×" + _image.Height + " · 单击展开" : "",
                Margin = new Thickness(0, 6, 0, 0),
                FontSize = 11,
                Foreground = (Brush)FindResource("Text.Secondary"),
            });
            ImageCard.ToolTip = panel;
        }

        private void OnImageCardClick(object sender, MouseButtonEventArgs e)
        {
            if (_recognizing) return; // 识别中不打断（拍板 3）
            if (_collapsed) { Expand(); e.Handled = true; return; }
            if (_image == null) { ChooseFile(); e.Handled = true; }
        }

        // ---- 识别 ----

        private void OnRecognizeClick(object sender, RoutedEventArgs e)
        {
            Recognize();
        }

        private async void Recognize()
        {
            if (_recognizing) return;
            if (_image == null)
            {
                SetStatus("warn", "请先载入图片：拖入、Ctrl+V 粘贴，或点上方按钮选择");
                UpdateInfoCard();
                return;
            }
            var status = AiClient.Status();
            if (!status.Available)
            {
                _lastError = "AI 不可用：" + status.Reason;
                UpdateInfoCard();
                SetStatus("error", "AI 不可用：" + status.Reason + "（在设置窗口的 AI 分组里配置）");
                return;
            }

            _recognizing = true;
            _lastError = null;
            _hasResult = false;
            ToolbarHint.Text = "";
            UpdateInfoCard();
            UpdateButtons();
            StartDots();
            SetStatus(null, "识别中…");
            Collapse();                       // 请求发出即让位（用户能感知图片让位）
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var result = await AiClient.OcrAsync(_image.DataUrl);
                if (_closed) return;
                stopwatch.Stop();
                if (result.Ok)
                {
                    _hasResult = true;
                    _lastModel = result.Model;
                    _lastElapsedMs = stopwatch.ElapsedMilliseconds;
                    ResultBox.Text = result.Text;
                    ResultBox.Focus();
                    ResultBox.CaretIndex = 0;
                    SetStatus("ok", "识别完成");
                }
                else
                {
                    _hasResult = false;
                    _lastError = result.Error + (result.RequestFailed ? "（可在设置中更换模型）" : "");
                    SetStatus("error", "识别失败：" + result.Error);
                }
            }
            catch (Exception ex)
            {
                if (_closed) return;
                _hasResult = false;
                _lastError = ex.Message;
                SetStatus("error", "识别失败：" + ex.Message);
            }
            finally
            {
                if (!_closed)
                {
                    _recognizing = false;
                    StopDots();
                    UpdateInfoCard();
                    UpdateButtons();
                }
            }
        }

        // ---- AI 信息卡（识别生命周期唯一呈现处）----

        private void UpdateInfoCard()
        {
            if (InfoTitle == null) return;
            if (_recognizing)
            {
                InfoTitle.Text = "识别中" + new string('.', _dotsStep);
                InfoTitle.Foreground = (Brush)FindResource("Accent");
                InfoDetail.Text = _image != null ? _image.Width + "×" + _image.Height + " · 已发出请求" : "";
            }
            else if (_lastError != null)
            {
                InfoTitle.Text = "识别失败";
                InfoTitle.Foreground = (Brush)FindResource("Text.Error");
                InfoDetail.Text = _lastError;
            }
            else if (_hasResult)
            {
                InfoTitle.Text = "识别完成";
                InfoTitle.Foreground = (Brush)FindResource("Accent");
                InfoDetail.Text = (_lastModel.Length > 0 ? _lastModel : "AI")
                    + " · " + (_lastElapsedMs / 1000.0).ToString("0.0") + "s"
                    + Environment.NewLine + WordCount() + " 字 · 可直接编辑";
            }
            else if (_image != null)
            {
                InfoTitle.Text = "按 Ctrl+R 或点「识别」开始";
                InfoTitle.Foreground = (Brush)FindResource("Text.Primary");
                InfoDetail.Text = _image.Width + "×" + _image.Height + " · " + FormatSize(_image.ByteCount);
            }
            else
            {
                InfoTitle.Text = "等待图片";
                InfoTitle.Foreground = (Brush)FindResource("Text.Primary");
                InfoDetail.Text = "拖入、Ctrl+V 粘贴，或点上方按钮载入";
            }
        }

        private int WordCount()
        {
            var text = ResultBox.Text ?? "";
            return text.Replace("\r", "").Length;
        }

        private void StartDots()
        {
            if (_dotsTimer == null)
            {
                _dotsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
                _dotsTimer.Tick += delegate
                {
                    _dotsStep = (_dotsStep + 1) % 4;
                    UpdateInfoCard();
                };
            }
            _dotsStep = 0;
            _dotsTimer.Start();
        }

        private void StopDots()
        {
            if (_dotsTimer != null) _dotsTimer.Stop();
        }

        // ---- 结果区 ----

        private void OnResultTextChanged(object sender, TextChangedEventArgs e)
        {
            if (ResultPlaceholder == null) return;
            var hasText = (ResultBox.Text ?? "").Length > 0;
            UpdateResultPlaceholder();
            ResultMeta.Text = hasText ? "可编辑" : "";
            if (_hasResult) UpdateInfoCard(); // 字数实时跟随（拍板 4）
            UpdateButtons();
        }

        /// <summary>
        /// 占位提示：无文字 **且** 未聚焦时才显示（2026-11-05 验收反馈）。
        /// 原来只判有无文字，聚焦后光标停在提示首字上形成重叠；watermark 惯例是聚焦即让位。
        /// </summary>
        private void UpdateResultPlaceholder()
        {
            if (ResultPlaceholder == null) return;
            var hasText = (ResultBox.Text ?? "").Length > 0;
            ResultPlaceholder.Visibility = hasText || ResultBox.IsKeyboardFocusWithin
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void OnResultBoxFocusChanged(object sender, KeyboardFocusChangedEventArgs e)
        {
            UpdateResultPlaceholder();
        }

        private void UpdateButtons()
        {
            if (RecognizeButton == null) return;
            RecognizeButton.IsEnabled = _image != null && !_recognizing;
            ClearButton.IsEnabled = !_recognizing && (_image != null || (ResultBox.Text ?? "").Length > 0);
            PasteButton.IsEnabled = !_recognizing;
            FileButton.IsEnabled = !_recognizing;
            ImageCard.Cursor = _recognizing ? Cursors.Arrow : Cursors.Hand;
        }

        private void SetStatus(string kind, string message)
        {
            StatusText.Text = message;
            StatusText.ToolTip = string.IsNullOrEmpty(message) ? null : message;
            StatusText.Foreground = kind == "error" ? (Brush)FindResource("Text.Error")
                : kind == "ok" ? (Brush)FindResource("Accent")
                : kind == "warn" ? (Brush)FindResource("Text.Secondary")
                : (Brush)FindResource("Text.Placeholder");
        }

        private static string FormatSize(int bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0") + " KB";
            return (bytes / (1024.0 * 1024)).ToString("0.0") + " MB";
        }

        // ---- 动作面板（Ctrl+Enter，与其他视图同一套约定）----

        private List<ActionItem> BuildActions()
        {
            var hasResult = (ResultBox.Text ?? "").Length > 0;
            return new List<ActionItem>
            {
                new ActionItem("read-clipboard", "读取剪贴板图片", "C", !_recognizing),
                new ActionItem("choose-file", "选择本地图片", "F", !_recognizing),
                // AI 未配置时不列出识别项（配置级状态用确定性载体表达）
                new ActionItem("recognize", "开始识别", "R", !_recognizing && _image != null && AiClient.Status().Available),
                new ActionItem("copy-result", "复制识别结果", "Y", hasResult),
                new ActionItem("expand-image", "展开图片", "E", _collapsed && !_recognizing),
                new ActionItem("clear", "清空", "X", !_recognizing && (_image != null || hasResult)),
            };
        }

        private void EnterActionMode()
        {
            _actionMode = true;
            Actions.Show(Root, BuildActions());
        }

        private void ExitActionModeInternal()
        {
            if (!_actionMode) return;
            _actionMode = false;
            Actions.Hide();
        }

        public bool TryExitActionMode()
        {
            if (!_actionMode) return false;
            ExitActionModeInternal();
            Root.Focus();
            return true;
        }

        private void OnActionInvoked(string actionId)
        {
            ExitActionModeInternal();
            switch (actionId)
            {
                case "read-clipboard": LoadClipboardImage(false); return;
                case "choose-file": ChooseFile(); return;
                case "recognize": Recognize(); return;
                case "copy-result": CopyResult(); return;
                case "expand-image": Expand(); return;
                case "clear": ClearAll(); return;
            }
        }

        private void CopyResult()
        {
            var text = ResultBox.Text ?? "";
            if (text.Length == 0)
            {
                SetStatus("warn", "没有可复制的识别结果");
                return;
            }
            try
            {
                Clipboard.SetText(text);
                SetStatus("ok", "已复制识别结果（" + WordCount() + " 字）");
            }
            catch (Exception ex)
            {
                SetStatus("error", "复制失败：" + ex.Message);
            }
        }

        // ---- 键位 ----

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Actions.IsOpen)
            {
                if (Actions.HandleKey(e)) e.Handled = true;
                return;
            }
            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (ctrl && e.Key == Key.Enter) { EnterActionMode(); e.Handled = true; return; }
            if (ctrl && e.Key == Key.R) { Recognize(); e.Handled = true; return; }
            if (ctrl && e.Key == Key.V)
            {
                // 剪贴板有图 → 拦截为「填入左栏」；是文本则放行给结果框原生粘贴
                bool hasImage;
                try { hasImage = Clipboard.ContainsImage(); }
                catch { hasImage = false; }
                if (hasImage) { LoadClipboardImage(false); e.Handled = true; }
                return;
            }
            if (e.Key == Key.Escape)
            {
                if (_actionMode) { ExitActionModeInternal(); Root.Focus(); e.Handled = true; return; }
                Close();
                e.Handled = true;
            }
        }

        /// <summary>鼠标点状态栏「操作 Ctrl+Enter」键帽呼出动作面板（与其他视图同一实现）。</summary>
        private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!ActionsHint.IsVisible || _actionMode) return;
            var source = e.OriginalSource as DependencyObject;
            while (source != null && !ReferenceEquals(source, ActionsHint)) source = VisualTreeHelper.GetParent(source);
            if (!ReferenceEquals(source, ActionsHint)) return;
            EnterActionMode();
            e.Handled = true;
        }

        // ---- 截图自验钩子（--ocr 调试入口）----

        /// <summary>调试预载：载入本地图片；simulateResult 非空时填充结果并进入缩略态（免真实请求）。</summary>
        public void DebugPreload(string imagePath, string simulateResult)
        {
            if (!string.IsNullOrEmpty(imagePath))
            {
                var record = OcrImage.FromFile(imagePath);
                if (record.Ok) ApplyImage(record, "调试预载图片");
                else SetStatus("error", record.Error);
            }
            if (simulateResult == null) return;
            ResultBox.Text = simulateResult;
            _hasResult = true;
            _lastModel = "debug-model";
            _lastElapsedMs = 3200;
            Collapse();
            UpdateInfoCard();
        }
    }
}
