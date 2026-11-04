using System;
using Microsoft.Win32;

namespace PyTools.Core
{
    /// <summary>
    /// 开机自启（M2b，S10）：HKCU\Software\Microsoft\Windows\CurrentVersion\Run 写 "PyTools" 项。
    /// 免管理员；value = 引号包裹的 exe 绝对路径。实况以注册表回读为准（不信任内存意图）。
    /// </summary>
    public static class Startup
    {
        private const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const string ValueName = "PyTools";

        public static string CommandValue()
        {
            return "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\"";
        }

        /// <summary>系统实况：Run 项存在且指向当前 exe。</summary>
        public static bool IsApplied()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    if (key == null) return false;
                    var value = (key.GetValue(ValueName) as string) ?? "";
                    return string.Equals(value, CommandValue(), StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        /// <summary>应用自启意愿。返回 null = 成功（含实况回读确认），否则为失败原因。</summary>
        public static string Apply(bool enable)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null) return "无法打开注册表 Run 项";
                    if (enable) key.SetValue(ValueName, CommandValue());
                    else key.DeleteValue(ValueName, false);
                }
                return IsApplied() == enable ? null : "开机自启状态应用失败";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>
        /// 判定 Run 项值名是否为历史 PyTools 残留。v1 Electron setLoginItemSettings 的键名
        /// 由 app.getName() 派生且随版本漂移（实测残留如 "electron.app.pytools-1.6.0"），
        /// 取消自启只删当前版本键、旧键遗留导致每天自启旧包。判定口径：值名含 "pytools"
        /// 且不是本应用固定键名 "PyTools"。
        /// </summary>
        public static bool IsLegacyName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.IndexOf("pytools", StringComparison.OrdinalIgnoreCase) >= 0
                && !string.Equals(name, ValueName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 启动清理（S10 补充）：删除 Run 项里非本键名的历史 PyTools 残留值。
        /// 只删 HKCU（免管理员、只影响当前用户），单条失败不中断。返回删除的值名。
        /// </summary>
        public static System.Collections.Generic.List<string> CleanupLegacy()
        {
            var removed = new System.Collections.Generic.List<string>();
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return removed;
                    foreach (var name in key.GetValueNames())
                    {
                        if (!IsLegacyName(name)) continue;
                        try { key.DeleteValue(name, false); removed.Add(name); }
                        catch { /* 单条失败保留，下次启动重试 */ }
                    }
                }
            }
            catch { /* 注册表不可用时静默，不影响启动 */ }
            return removed;
        }
    }
}
