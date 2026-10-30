using System;
using Microsoft.Win32;

namespace PyTools.Core
{
    /// <summary>
    /// 「添加到 PyTools」资源管理器右键菜单（M2b，L8）。
    /// HKCU\Software\Classes 下 exefile + lnkfile 的 shell\AddToPyTools 静态动词，免管理员；
    /// command = "&lt;exe路径&gt;" "--add-app=%1"（自包含 token）。用 Registry API 直接写（零子进程）。
    /// </summary>
    public static class ContextMenuRegistry
    {
        public const string MenuText = "添加到 PyTools";
        public static readonly string[] ProgIds = { "exefile", "lnkfile" };

        private const string ClassesRoot = "Software\\Classes";

        public static string KeyPath(string progId)
        {
            return ClassesRoot + "\\" + progId + "\\shell\\AddToPyTools";
        }

        public static string LauncherPath
        {
            get { return System.Reflection.Assembly.GetExecutingAssembly().Location; }
        }

        public static string ExpectedCommand()
        {
            return "\"" + LauncherPath + "\" \"--add-app=%1\"";
        }

        /// <summary>开启右键菜单（幂等重写，兼任挪目录后的自愈）。</summary>
        public static string Enable()
        {
            try
            {
                var exe = LauncherPath;
                foreach (var progId in ProgIds)
                {
                    using (var key = Registry.CurrentUser.CreateSubKey(KeyPath(progId)))
                    {
                        if (key == null) return "无法创建注册表键";
                        key.SetValue(null, MenuText);
                        key.SetValue("Icon", "\"" + exe + "\",0");
                    }
                    using (var command = Registry.CurrentUser.CreateSubKey(KeyPath(progId) + "\\command"))
                    {
                        if (command == null) return "无法创建注册表键";
                        command.SetValue(null, ExpectedCommand());
                    }
                }
                return null; // null = 成功
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>关闭右键菜单。键不存在视为已达成；删除后仍查询得到才报错。</summary>
        public static string Disable()
        {
            try
            {
                foreach (var progId in ProgIds)
                {
                    try { Registry.CurrentUser.DeleteSubKeyTree(KeyPath(progId), false); }
                    catch { /* 键不存在 = 目标语义已达成 */ }
                }
                return Configured() ? "右键菜单删除失败，请检查权限" : null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>实际注册状态：两个 ProgID 的 command 都与期望值一致才算已开启（旧格式判为未配置）。</summary>
        public static bool Configured()
        {
            foreach (var progId in ProgIds)
            {
                try
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(KeyPath(progId) + "\\command"))
                    {
                        if (key == null) return false;
                        var value = (key.GetValue(null) as string) ?? "";
                        if (!string.Equals(value, ExpectedCommand(), StringComparison.OrdinalIgnoreCase)) return false;
                    }
                }
                catch { return false; }
            }
            return true;
        }

        /// <summary>启动自愈：期望命令不一致时幂等重写（挪目录 / 旧格式）。</summary>
        public static string Repair()
        {
            return Configured() ? null : Enable();
        }
    }
}
