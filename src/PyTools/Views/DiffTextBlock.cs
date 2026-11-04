using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using PyTools.Core;

namespace PyTools.Views
{
    /// <summary>
    /// 差异行文本控件：把 DiffRow 的左右片段渲染成 Run 集合，行内变更段加底色。
    /// 继承 TextBlock 是为了在 DataTemplate 里直接用（TextBlock 的 Inlines 无法绑定，
    /// 只能靠依赖属性变更时在代码里重建——虚拟化回收时同一实例换 Row，重建路径必须幂等）。
    /// 聚焦态（F3 命中行）：Row.IsFocused 变化时重建，变更段底色换聚焦加深色。
    /// 不可见字符：变更片段里的零宽字符（0 宽渲染完全不可见）原位替换为光标大小的竖色条。
    /// </summary>
    public sealed class DiffTextBlock : TextBlock
    {
        public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
            "Row", typeof(DiffRow), typeof(DiffTextBlock),
            new PropertyMetadata(null, OnContentChanged));

        public static readonly DependencyProperty IsLeftProperty = DependencyProperty.Register(
            "IsLeft", typeof(bool), typeof(DiffTextBlock),
            new PropertyMetadata(true, OnContentChanged));

        private DiffRow _observedRow; // 已订阅 IsFocused 的行（换 Row 时退订）

        public DiffRow Row
        {
            get { return (DiffRow)GetValue(RowProperty); }
            set { SetValue(RowProperty, value); }
        }

        public bool IsLeft
        {
            get { return (bool)GetValue(IsLeftProperty); }
            set { SetValue(IsLeftProperty, value); }
        }

        private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((DiffTextBlock)d).Rebuild();
        }

        private void Rebuild()
        {
            var row = Row;
            WatchFocus(row);

            Inlines.Clear();
            if (row == null) return;
            var text = IsLeft ? row.Left : row.Right;
            if (text == null) return; // 空档：不显示内容，底色由行模板负责

            var focused = row.IsFocused;
            var changedBackground = FindBrush(focused
                ? (IsLeft ? "Diff.Inline.Del.Focus" : "Diff.Inline.Add.Focus")
                : (IsLeft ? "Diff.Inline.Del" : "Diff.Inline.Add"));

            var parts = IsLeft ? row.LeftParts : row.RightParts;
            if (parts == null || parts.Count == 0)
            {
                // 无行内片段：Delete/Insert 行整行都是差异（Change 行必有片段，Equal 行不标）
                var wholeChanged = (row.Kind == DiffKind.Delete && IsLeft)
                                || (row.Kind == DiffKind.Insert && !IsLeft);
                AppendText(text, wholeChanged, changedBackground, focused);
                return;
            }

            foreach (var part in parts)
                AppendText(part.Text, part.Changed, changedBackground, focused);
        }

        /// <summary>渲染一段文本；差异段中的不可见字符拆出来用竖条标识替代（0 宽字符背景色没有可见宽度）。</summary>
        private void AppendText(string text, bool changed, Brush changedBackground, bool focused)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            if (!changed || !DiffEngine.ContainsInvisible(text))
            {
                var run = new Run(text);
                if (changed && changedBackground != null) run.Background = changedBackground;
                Inlines.Add(run);
                return;
            }

            // 逐字符扫描：不可见字符 → 竖条标识；其余归并成普通 Run（保持差异底色）
            var buffer = new StringBuilder();
            foreach (var c in text)
            {
                if (DiffEngine.IsInvisibleChar(c))
                {
                    if (buffer.Length > 0)
                    {
                        AddChangedRun(buffer.ToString(), changedBackground);
                        buffer.Length = 0;
                    }
                    AddInvisibleMarker(focused);
                }
                else
                {
                    buffer.Append(c);
                }
            }
            if (buffer.Length > 0) AddChangedRun(buffer.ToString(), changedBackground);
        }

        private void AddChangedRun(string text, Brush changedBackground)
        {
            var run = new Run(text);
            if (changedBackground != null) run.Background = changedBackground;
            Inlines.Add(run);
        }

        /// <summary>不可见字符占位：约 4px 宽的圆角竖条（普通态灰、聚焦态 Accent 蓝），基线对齐到行底。</summary>
        private void AddInvisibleMarker(bool focused)
        {
            var bar = new Border
            {
                Width = 4,
                Height = 13,
                CornerRadius = new CornerRadius(1),
                Margin = new Thickness(0, 0, 1, 0),
                Background = FindBrush(focused ? "Accent" : "Diff.Invisible")
            };
            Inlines.Add(new InlineUIContainer(bar) { BaselineAlignment = BaselineAlignment.Bottom });
        }

        /// <summary>订阅/换绑 Row 的 IsFocused 通知（虚拟化回收换 Row 时必须先退订旧实例）。</summary>
        private void WatchFocus(DiffRow row)
        {
            if (ReferenceEquals(_observedRow, row)) return;
            if (_observedRow != null) _observedRow.PropertyChanged -= OnRowPropertyChanged;
            _observedRow = row;
            if (_observedRow != null) _observedRow.PropertyChanged += OnRowPropertyChanged;
        }

        private void OnRowPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "IsFocused") Rebuild();
        }

        private static Brush FindBrush(string key)
        {
            if (Application.Current == null) return null;
            return Application.Current.TryFindResource(key) as Brush;
        }
    }
}
