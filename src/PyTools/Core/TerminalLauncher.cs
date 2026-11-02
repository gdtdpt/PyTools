using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace PyTools.Core
{
    /// <summary>
    /// 终端启动（CMD / PowerShell / Git Bash）。返回值 null = 成功，否则为可读失败原因。
    /// Git Bash 只认真正的 Git for Windows 安装（同根下 bin\bash.exe + usr\bin\mintty.exe），
    /// 因此 C:\Windows\System32\bash.exe（WSL）不会被误判。
    /// </summary>
    public static class TerminalLauncher
    {
        public static string OpenCmd(string dir)
        {
            if (!IsDirectory(dir)) return "目录不存在：" + dir;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/d /k",
                    WorkingDirectory = dir,
                    UseShellExecute = true
                });
                return null;
            }
            catch (Exception ex) { return "打开 CMD 失败：" + ex.Message; }
        }

        public static string OpenPowerShell(string dir)
        {
            if (!IsDirectory(dir)) return "目录不存在：" + dir;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoExit -NoLogo",
                    WorkingDirectory = dir,
                    UseShellExecute = true
                });
                return null;
            }
            catch (Exception ex) { return "打开 PowerShell 失败：" + ex.Message; }
        }

        /// <summary>
        /// Git Bash：mintty 独立窗口（Git Bash 同款终端）。mintty 没有 --dir 参数，
        /// 靠继承父进程工作目录；Git for Windows 的 /etc/profile 见 CHERE_INVOKING 才不 cd 回 home。
        /// </summary>
        public static string OpenGitBash(string dir)
        {
            if (!IsDirectory(dir)) return "目录不存在：" + dir;
            var root = DetectGitBash();
            if (root == null) return "未检测到 Git Bash（需安装 Git for Windows）";

            var mintty = Path.Combine(root, "usr", "bin", "mintty.exe");
            var bash = Path.Combine(root, "bin", "bash.exe");
            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = mintty,
                    Arguments = "-t \"Git Bash\" \"" + bash + "\" -l -i",
                    WorkingDirectory = dir,
                    UseShellExecute = false
                };
                // Git for Windows 的 /etc/profile 见 CHERE_INVOKING 才保持当前目录，否则 cd 回 home。
                // 不用 psi.EnvironmentVariables：它合并父环境时大小写不敏感，机器上若同时存在
                // HTTPS_PROXY/https_proxy（实测本机如此）会直接抛「已添加项」。
                // 改为本进程临时注入，子进程按继承拿到；窗口进程由 mintty 自行分叉。
                Environment.SetEnvironmentVariable("CHERE_INVOKING", "1");
                try { Process.Start(start); }
                finally { Environment.SetEnvironmentVariable("CHERE_INVOKING", null); }
                return null;
            }
            catch (Exception ex) { return "打开 Git Bash 失败：" + ex.Message; }
        }

        /// <summary>Git Bash 安装根目录；未安装返回 null。检测顺序见类注释。</summary>
        public static string DetectGitBash()
        {
            // 1) 注册表 InstallPath（64/32 位视图 × 机器/用户）
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                {
                    try
                    {
                        using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
                        using (var key = baseKey.OpenSubKey(@"SOFTWARE\GitForWindows"))
                        {
                            var value = key == null ? null : key.GetValue("InstallPath") as string;
                            if (LooksLikeGitRoot(value)) return value.TrimEnd('\\');
                        }
                    }
                    catch { } // 无权限/键不存在：继续下一种来源
                }
            }

            // 2) 常见安装位置（含 scoop 布局：本机即 scoop 安装，没有 Program Files\Git）。
            //    放在 PATH 查找之前：PATH 上可能先命中别的程序自带的 PortableGit（实测本机
            //    where git.exe 首条就是第三方 vendor 目录），那不是用户日常用的 Git。
            var candidates = new List<string>();
            var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
            var programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(programFiles)) candidates.Add(Path.Combine(programFiles, "Git"));
            if (!string.IsNullOrEmpty(programFilesX86)) candidates.Add(Path.Combine(programFilesX86, "Git"));
            if (!string.IsNullOrEmpty(localAppData)) candidates.Add(Path.Combine(localAppData, "Programs", "Git"));
            if (!string.IsNullOrEmpty(userProfile)) candidates.Add(Path.Combine(userProfile, "scoop", "apps", "git", "current"));
            foreach (var candidate in candidates)
                if (LooksLikeGitRoot(candidate)) return candidate;

            // 3) 兜底：PATH 上的 git.exe 反推安装根（<root>\cmd\git.exe → <root>）
            foreach (var exe in WhereAll("git.exe"))
            {
                var dir = Path.GetDirectoryName(exe);
                if (string.IsNullOrEmpty(dir)) continue;
                var parent = Path.GetDirectoryName(dir);
                if (LooksLikeGitRoot(parent)) return parent;
                var grand = string.IsNullOrEmpty(parent) ? null : Path.GetDirectoryName(parent);
                if (LooksLikeGitRoot(grand)) return grand;
            }

            return null;
        }

        /// <summary>判定是不是 Git for Windows 安装根：bin\bash.exe + usr\bin\mintty.exe 同时存在。</summary>
        public static bool LooksLikeGitRoot(string root)
        {
            if (string.IsNullOrEmpty(root)) return false;
            try
            {
                return File.Exists(Path.Combine(root, "bin", "bash.exe"))
                    && File.Exists(Path.Combine(root, "usr", "bin", "mintty.exe"));
            }
            catch { return false; }
        }

        private static bool IsDirectory(string dir)
        {
            return !string.IsNullOrEmpty(dir) && Directory.Exists(dir);
        }

        /// <summary>PATH 上第一个命中（where.exe）；未找到返回 null。</summary>
        public static string Which(string fileName)
        {
            var all = WhereAll(fileName);
            return all.Count > 0 ? all[0] : null;
        }

        /// <summary>where.exe 查找（PATH 上的全部命中；失败返回空表）。</summary>
        private static List<string> WhereAll(string fileName)
        {
            var result = new List<string>();
            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = "where.exe",
                    Arguments = fileName,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var process = Process.Start(start))
                {
                    if (process == null) return result;
                    var output = process.StandardOutput.ReadToEnd();
                    if (!process.WaitForExit(1500)) { try { process.Kill(); } catch { } return result; }
                    foreach (var line in output.Split('\n'))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.Length > 0) result.Add(trimmed);
                    }
                }
            }
            catch { }
            return result;
        }
    }
}
