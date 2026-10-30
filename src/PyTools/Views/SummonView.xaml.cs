using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PyTools.Views
{
    public partial class SummonView : UserControl
    {
        public SummonView()
        {
            InitializeComponent();
        }

        public TextBox SearchBox { get { return PART_SearchBox; } }

        public void FocusSearch()
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            // Border.CornerRadius 只圆自身背景，不裁切子内容——搜索栏白底方角会伸出
            // 圆角之外并在窗外投出暗色伪影（实测踩过）。对内容网格施加同半径圆角裁切。
            var radius = 8.0;
            if (TryFindResource("Radius.Window") is CornerRadius corner)
                radius = corner.TopLeft;
            if (radius > 0 && ContentGrid.ActualWidth > 0)
            {
                ContentGrid.Clip = new RectangleGeometry(
                    new Rect(0, 0, ContentGrid.ActualWidth, ContentGrid.ActualHeight), radius, radius);
            }
        }
    }
}
