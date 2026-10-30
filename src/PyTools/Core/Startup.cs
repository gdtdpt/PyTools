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
    }
}
