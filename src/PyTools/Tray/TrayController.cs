using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace PyTools.Tray
{
    /// <summary>
    /// 托盘（WinForms NotifyIcon，BCL）：打开 / 重新扫描（M2 占位）/ 退出。
    /// 气泡能力（BalloonTip）本期就位，M2 静默添加反馈直接接入。
    /// </summary>
    public sealed class TrayController : IDisposable
    {
        private NotifyIcon _icon;

        public event Action OpenRequested;
        public event Action ExitRequested;
        public event Action RescanRequested;
        public event Action SettingsRequested;

        public void Init()
        {
            _icon = new NotifyIcon
            {
                Icon = LoadIcon(),
                Text = "PyTools v2",
                Visible = true
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add("打开", null, (s, e) => Raise(OpenRequested));
            menu.Items.Add("设置", null, (s, e) => Raise(SettingsRequested));
            menu.Items.Add("重新扫描", null, (s, e) => Raise(RescanRequested));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (s, e) => Raise(ExitRequested));

            _icon.ContextMenuStrip = menu;
            _icon.DoubleClick += (s, e) => Raise(OpenRequested);
        }

        public void ShowBubble(string title, string text)
        {
            if (_icon == null) return;
            try
            {
                _icon.BalloonTipTitle = title;
                _icon.BalloonTipText = text;
                _icon.ShowBalloonTip(3000);
            }
            catch { /* 气泡失败不影响运行 */ }
        }

        private static Icon LoadIcon()
        {
            // 从程序集嵌入资源读原始 ICO 字节（WPF pack:// 资源会被转码，不能用）
            try
            {
                using (var stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("PyTools.Assets.icon.ico"))
                {
                    if (stream != null) return new Icon(stream);
                }
            }
            catch { /* 落到系统默认图标 */ }
            try
            {
                var exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(exe))
                {
                    var extracted = Icon.ExtractAssociatedIcon(exe);
                    if (extracted != null) return extracted;
                }
            }
            catch { /* 落到系统默认图标 */ }
            return SystemIcons.Application;
        }

        private void Raise(Action handler)
        {
            if (handler != null) handler();
        }

        public void Dispose()
        {
            if (_icon == null) return;
            _icon.Visible = false;
            _icon.Dispose();
            _icon = null;
        }
    }
}
