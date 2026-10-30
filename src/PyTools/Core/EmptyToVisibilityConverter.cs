using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PyTools.Core
{
    /// <summary>
    /// 空串/ null → Visible，非空 → Collapsed（搜索框占位提示用）。
    /// </summary>
    public sealed class EmptyToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var text = value as string;
            return string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}
