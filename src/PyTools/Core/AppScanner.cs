using System;
using System.Collections.Generic;
using System.IO;
using PyTools.Native;

namespace PyTools.Core
{
    /// <summary>
    /// 应用扫描（L1，v1 app-loader.js 语义等价）：
    /// 开始菜单两根目录 + appDirs 白名单（相对 baseDir 解析），深度 8，收录 .lnk/.exe，
    /// 身份键 target|args（小写），自定义清单同键覆盖扫描条目（显式登记优先）。
    /// </summary>
    public static class AppScanner
    {
        public static readonly HashSet<string> ApplicationExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".com", ".scr", ".msc", ".cpl"
        };

        private const int MaxDepth = 8;

        /// <summary>扫描根：开始菜单（用户 + 公共）+ appDirs（存在才返回），去重。</summary>
        public static List<string> Roots(List<string> appDirs, string baseDir)
        {
            var roots = new List<string>();
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            if (!string.IsNullOrEmpty(appData)) roots.Add(appData);
            var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
            if (!string.IsNullOrEmpty(common)) roots.Add(common);

            foreach (var dir in appDirs ?? new List<string>())
            {
                var value = (dir ?? "").Trim();
                if (value.Length == 0) continue;
                string resolved;
                try
                {
                    resolved = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(baseDir, value));
                }
                catch { continue; }
                roots.Add(resolved);
            }

            var unique = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots)
            {
                if (!seen.Add(root.ToLowerInvariant())) continue;
                if (!Directory.Exists(root)) continue;
                unique.Add(root);
            }
            return unique;
        }

        /// <summary>纯扫描（无 IO 副作用以外的操作），roots × custom 合并排序，供直接调用与自测。</summary>
        public static List<AppEntry> Scan(List<string> roots, List<CustomApp> custom)
        {
            var seen = new Dictionary<string, AppEntry>(StringComparer.Ordinal);
            foreach (var root in roots)
            {
                ScanDir(root, 0, seen);
            }
            // 自定义条目优先：同一 target 已被扫描收录时，以显式登记为准
            foreach (var entry in CustomEntries(custom))
            {
                seen[entry.TargetPath.ToLowerInvariant() + "|" + entry.Args] = entry;
            }

            var list = new List<AppEntry>(seen.Values);
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));
            foreach (var entry in list) BuildSearchTerms(entry);
            return list;
        }

        private static void ScanDir(string dir, int depth, Dictionary<string, AppEntry> seen)
        {
            IEnumerable<FileSystemInfo> entries;
            try
            {
                var info = new DirectoryInfo(dir);
                if (!info.Exists) return;
                entries = info.EnumerateFileSystemInfos();
            }
            catch { return; }

            foreach (var entry in entries)
            {
                try
                {
                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                    {
                        if (depth < MaxDepth) ScanDir(entry.FullName, depth + 1, seen);
                        continue;
                    }
                    var extension = (entry.Extension ?? "").ToLowerInvariant();
                    if (extension != ".lnk" && extension != ".exe") continue;

                    var name = Path.GetFileNameWithoutExtension(entry.Name).Trim();
                    if (name.Length == 0) continue;

                    var target = entry.FullName;
                    var arguments = "";
                    var iconPath = entry.FullName;
                    if (extension == ".lnk")
                    {
                        ShellLink.LinkInfo link;
                        if (!ShellLink.TryResolve(entry.FullName, out link)) continue;
                        target = string.IsNullOrEmpty(link.Target) ? entry.FullName : Path.GetFullPath(link.Target);
                        if (!IsApplicationTarget(extension, target)) continue;
                        arguments = link.Arguments ?? "";
                        iconPath = IconPathFromShortcut(link, target, entry.FullName);
                    }
                    else if (!IsApplicationTarget(extension, target))
                    {
                        continue;
                    }

                    var key = target.ToLowerInvariant() + "|" + arguments;
                    if (seen.ContainsKey(key)) continue;

                    var targetDir = Path.GetDirectoryName(target) ?? "";
                    var item = new AppEntry
                    {
                        Name = name,
                        Path = entry.FullName,
                        TargetPath = target,
                        TargetDir = targetDir,
                        Args = arguments,
                        IconPath = iconPath,
                        IsCustom = false
                    };
                    seen[key] = item;
                }
                catch
                {
                    // 单个条目失败不影响整体扫描
                }
            }
        }

        private static bool IsApplicationTarget(string sourceExtension, string target)
        {
            if (sourceExtension == ".exe") return true;
            return ApplicationExtensions.Contains(Path.GetExtension(target ?? ""));
        }

        /// <summary>v1 iconPathFromShortcut 语义：target → lnk 图标位置 → lnk 原路径，取第一个存在的文件。</summary>
        private static string IconPathFromShortcut(ShellLink.LinkInfo link, string target, string fallback)
        {
            var candidates = new[] { target, link.IconPath, fallback };
            foreach (var candidate in candidates)
            {
                var value = (candidate ?? "").Trim();
                if (value.Length == 0) continue;
                try
                {
                    if (File.Exists(value)) return value;
                }
                catch { }
            }
            return fallback;
        }

        /// <summary>登记条目转同构（v1 customEntries）：只收应用扩展名、不含 lnk（lnk 在登记时已解析）。</summary>
        private static IEnumerable<AppEntry> CustomEntries(List<CustomApp> custom)
        {
            foreach (var item in custom ?? new List<CustomApp>())
            {
                var targetPath = "";
                try { targetPath = Path.GetFullPath((item.Path ?? "").Trim()); }
                catch { continue; }
                if (targetPath.Length == 0) continue;
                var extension = Path.GetExtension(targetPath).ToLowerInvariant();
                if (extension == ".lnk" || !ApplicationExtensions.Contains(extension)) continue;

                var exeName = Path.GetFileNameWithoutExtension(targetPath);
                var targetFolder = Path.GetFileName(Path.GetDirectoryName(targetPath)) ?? "";
                yield return new AppEntry
                {
                    Name = string.IsNullOrEmpty(item.Name) ? (string.IsNullOrEmpty(exeName) ? "未命名应用" : exeName) : item.Name.Trim(),
                    Path = targetPath,
                    TargetPath = targetPath,
                    TargetDir = Path.GetDirectoryName(targetPath) ?? "",
                    Args = item.Args ?? "",
                    IconPath = targetPath,
                    IsCustom = true
                };
            }
        }

        /// <summary>搜索词与首字母（v1 keywords = [name, targetName, targetFolder]，rank 递增）。</summary>
        private static void BuildSearchTerms(AppEntry entry)
        {
            var keywords = new[]
            {
                entry.Name,
                Path.GetFileNameWithoutExtension(entry.TargetPath ?? entry.Path) ?? "",
                Path.GetFileName((entry.TargetDir ?? "")) ?? ""
            };
            var rank = 0;
            foreach (var keyword in keywords)
            {
                var value = (keyword ?? "").Trim().ToLowerInvariant();
                if (value.Length > 0) entry.Keywords.Add(value);
                if (Pinyin.Ready)
                {
                    foreach (var variant in Pinyin.Expand(value))
                    {
                        if (!entry.Initials.Contains(variant)) entry.Initials.Add(variant);
                    }
                }
                rank++;
            }
        }
    }
}
