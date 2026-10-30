using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PyTools.Views
{
    /// <summary>带角标的列表条目（应用列表 / 剪贴板列表共用同一套「锚定可视区」角标规则）。</summary>
    public interface IBadgedItem
    {
        string Badge { get; set; }
    }

    /// <summary>
    /// 列表角标与可视区求值（M2a 三次迭代定案的规则，M3 抽出供两个列表共用）：
    /// 数字 1-0 锚定**当前可视区**，滚动后对可见行重编号；
    /// 首可见行不能按「VerticalOffset / 行高」估算（行高含边距 + DPI 缩放，估算必偏），
    /// 必须遍历虚拟化面板已实现的容器求真实位置，取「露出 ≥60%」的第一行。
    /// </summary>
    internal static class ListBadgeHelper
    {
        private const double MinVisibleRatio = 0.4; // 允许上方遮住 40% 以内仍算"第一可见行"

        public static ScrollViewer FindScroll(DependencyObject root)
        {
            if (root == null) return null;
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var scroll = child as ScrollViewer;
                if (scroll != null) return scroll;
                scroll = FindScroll(child);
                if (scroll != null) return scroll;
            }
            return null;
        }

        public static int FirstVisibleIndex(ListBox list, ScrollViewer scroll, int count)
        {
            if (list == null || count == 0) return 0;
            if (scroll == null) scroll = FindScroll(list);
            if (scroll == null) return 0;

            var generator = list.ItemContainerGenerator;
            for (var i = 0; i < count; i++)
            {
                var container = generator.ContainerFromIndex(i) as FrameworkElement;
                if (container == null) continue; // 虚拟化未实现（在视口之外）
                double top;
                try { top = container.TransformToVisual(scroll).Transform(new Point(0, 0)).Y; }
                catch (System.InvalidOperationException) { continue; }
                if (top > -container.ActualHeight * MinVisibleRatio) return i;
            }
            return 0;
        }

        public static void UpdateBadges(IList items, int firstVisible)
        {
            if (items == null) return;
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i] as IBadgedItem;
                if (item == null) continue;
                var offset = i - firstVisible;
                item.Badge = (offset >= 0 && offset <= 9) ? (offset == 9 ? "0" : (offset + 1).ToString()) : "";
            }
        }
    }
}
