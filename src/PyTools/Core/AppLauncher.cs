using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace PyTools.Core
{
    /// <summary>
    /// 应用启动（L3/L4，v1 app-loader.openApp 语义等价）：
    /// isKnownApp 授权 → 目标存在性（死条目：登记条目移除 + 重扫，其余保留）→
    /// 无参 ShellExecute 原路径 / 带参 spawn（quote-aware 分词，WorkingDirectory=目标目录）。
    /// 回车与 Tab 菜单两条路径必须都走 Open（v1 教训：两条路径行为要统一）。
    /// </summary>
    public static class AppLauncher
    {
        public sealed class OpenResult
        {
            public bool Ok;
            public string Message = "";      // Ok=true 且非空 → 成功但带提示；Ok=false → 失败提示
            public bool RemovedDeadEntry;    // 已移除登记条目，调用方应重扫
        }

        /// <summary>授权：在自定义清单中，或位于任一扫描根之下（路径前缀，v1 语义）。</summary>
        public static bool IsKnownApp(string appPath, List<string> roots, CustomApps custom)
        {
            string target;
            try { target = Path.GetFullPath((appPath ?? "").Trim()); }
            catch { return false; }
            if (custom != null && custom.Has(target)) return true;

            var extension = Path.GetExtension(target).ToLowerInvariant();
            if (extension != ".lnk" && extension != ".exe") return false;
            foreach (var root in roots ?? new List<string>())
            {
                var normalized = root.EndsWith("\\", StringComparison.Ordinal) ? root : root + "\\";
                if (target.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>启动条目。返回 null = 成功；否则 OpenResult（Ok/Message/RemovedDeadEntry）。</summary>
        public static OpenResult Open(AppEntry entry, List<string> roots, CustomApps custom)
        {
            if (entry == null) return new OpenResult { Ok = false, Message = "应用不在允许的应用目录内" };
            string target;
            try { target = Path.GetFullPath((entry.Path ?? "").Trim()); }
            catch { return new OpenResult { Ok = false, Message = "应用不在允许的应用目录内" }; }
            if (!IsKnownApp(target, roots, custom))
                return new OpenResult { Ok = false, Message = "应用不在允许的应用目录内" };

            // 启动前确认目标存在：登记条目的目标被删除时移除记录，其余启动失败保留记录（v1 1.8.1）。
            // lnk 本体存在不代表目标存在，有展开目标时按展开目标判定。
            var realTarget = RealTarget(entry);
            var existsPath = realTarget ?? target;
            if (!File.Exists(existsPath))
            {
                var deadName = Path.GetFileName(existsPath);
                if (custom != null && custom.Remove(entry.TargetPath))
                {
                    return new OpenResult
                    {
                        Ok = true,
                        Message = "目标不存在，已从列表移除：" + deadName,
                        RemovedDeadEntry = true
                    };
                }
                return new OpenResult { Ok = false, Message = "目标不存在：" + deadName };
            }

            var rawArgs = (entry.Args ?? "").Trim();
            if (rawArgs.Length == 0)
            {
                // 无参：ShellExecute 原路径（lnk 自带参数由 shell 处理，exe 直启）
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = target,
                        UseShellExecute = true
                    });
                    return null;
                }
                catch (Exception ex)
                {
                    return new OpenResult { Ok = false, Message = SpawnErrorText(ex) };
                }
            }

            // 带参：必须对解析后的真实目标 spawn。扫描条目的 Path 是 .lnk 本身
            // （参数在 lnk 内），CreateProcess 直接对 .lnk 会报 193（ERROR_BAD_EXE_FORMAT）；
            // Chrome 的开始菜单快捷方式即带 --source-shortcut-location=start-menu。
            var spawnFile = realTarget ?? target;
            var shellFallback = realTarget == null &&
                string.Equals(Path.GetExtension(spawnFile), ".lnk", StringComparison.OrdinalIgnoreCase);
            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = spawnFile,
                    Arguments = JoinArgs(SplitArgs(rawArgs)),
                    UseShellExecute = shellFallback,
                    WorkingDirectory = string.IsNullOrEmpty(entry.TargetDir) ? Path.GetDirectoryName(spawnFile) : entry.TargetDir,
                    CreateNoWindow = true
                };
                Process.Start(start);
                return null;
            }
            catch (Exception ex)
            {
                return new OpenResult { Ok = false, Message = SpawnErrorText(ex) };
            }
        }

        /// <summary>
        /// 解析后的真实启动目标（.lnk 已展开）；不可用时返回 null。
        /// 扫描条目 Path=lnk、TargetPath=真实 exe；登记条目两者相同。
        /// </summary>
        public static string RealTarget(AppEntry entry)
        {
            var targetPath = (entry.TargetPath ?? "").Trim();
            if (targetPath.Length == 0) return null;
            try { targetPath = Path.GetFullPath(targetPath); }
            catch { return null; }
            // 未展开的环境变量路径（%ProgramFiles%\…）不是有效目标
            if (targetPath.IndexOf('%') >= 0) return null;
            if (CustomApps.SamePath(targetPath, entry.Path)) return null;
            return targetPath;
        }

        /// <summary>Windows 命令行简单分词：空白分隔，双引号内保留空白（v1 splitArgs 逐字符移植）。</summary>
        public static List<string> SplitArgs(string raw)
        {
            var outArgs = new List<string>();
            var current = new StringBuilder();
            var inQuote = false;
            foreach (var ch in raw ?? "")
            {
                if (ch == '"')
                {
                    inQuote = !inQuote;
                    continue;
                }
                if (!inQuote && char.IsWhiteSpace(ch))
                {
                    if (current.Length > 0)
                    {
                        outArgs.Add(current.ToString());
                        current.Length = 0;
                    }
                    continue;
                }
                current.Append(ch);
            }
            if (current.Length > 0) outArgs.Add(current.ToString());
            return outArgs;
        }

        private static string JoinArgs(List<string> tokens)
        {
            var parts = new string[tokens.Count];
            for (var i = 0; i < tokens.Count; i++)
            {
                // 逐 token 重加引号：原串可能因分词丢失引号结构（含空格的参数必须包回去）
                parts[i] = tokens[i].IndexOf(' ') >= 0 ? "\"" + tokens[i] + "\"" : tokens[i];
            }
            return string.Join(" ", parts);
        }

        /// <summary>spawn 失败文案：无效 PE 等系统码对用户无意义，补一句可读提示（v1 spawnErrorText 语义）。</summary>
        public static string SpawnErrorText(Exception error)
        {
            var win32 = error as Win32Exception;
            if (win32 != null && (win32.NativeErrorCode == 193 || win32.NativeErrorCode == 216))
            {
                return "启动失败：" + win32.Message + "（文件可能不是有效的可执行程序）";
            }
            return "启动失败：" + error.Message;
        }
    }
}
