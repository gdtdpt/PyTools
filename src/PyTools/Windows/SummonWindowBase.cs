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

        public void FocusSearch()
        {
            var view = Content as Views.SummonView;
            if (view != null) view.FocusSearch();
        }
    }
}
