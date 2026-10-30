using System;
using System.Windows;
using System.Windows.Input;

namespace PyTools.Windows
{
    /// <summary>
    /// 呼出窗基类：无边框、不占任务栏、置顶、Esc 收起。
    /// 透明壳 / 不透明壳两个子类共用；呼出/收起主流程由 App 编排。
    /// </summary>
    public abstract class SummonWindowBase : Window
    {
        public event Action EscapePressed;

        protected SummonWindowBase()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            // 动态高度（M2a）：空查询只有搜索框，有结果向下展开（顶边锚定），宽度仍由 App 按记忆设定
            SizeToContent = SizeToContent.Height;
            PreviewKeyDown += OnPreviewKeyDown;
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                EscapePressed?.Invoke();
                e.Handled = true;
            }
        }

        /// <summary>把焦点交给内容视图的搜索框（启动器视图 / 剪贴板视图各自实现）。</summary>
        public void FocusSearch()
        {
            var focusable = Content as Views.ISearchFocusable;
            if (focusable != null) focusable.FocusSearch();
        }
    }
}
