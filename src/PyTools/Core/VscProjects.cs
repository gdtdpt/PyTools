using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace PyTools.Core
{
    /// <summary>VSCode 最近项目（v1 vscode-recent.js 语义：读 workspaceStorage 下的 workspace.json）。</summary>
    public sealed class VscProject
    {
        /// <summary>项目目录；多根工作区时为 .code-workspace 文件所在目录（目录/CMD/GitBash 的落点）。</summary>
        public string Path = "";

        /// <summary>非空 = 多根工作区：用 VSCode 打开时传这个文件。</summary>
        public string WorkspaceFile = "";

        public string Title = "";
        public DateTime OpenedAt;

        /// <summary>副标题：多根显示工作区文件路径，单目录显示项目路径。</summary>
        public string Subtitle
        {
            get { return WorkspaceFile.Length > 0 ? WorkspaceFile : Path; }
        }

        /// <summary>过滤用文本（标题 + 路径 + 工作区文件路径）。</summary>
        public string SearchText
        {
            get { return (Title + "\n" + Path + "\n" + WorkspaceFile).ToLowerInvariant(); }
        }
    }

    public static class VscProjects
    {
        /// <summary>%APPDATA%\Code\User\workspaceStorage</summary>
        public static string StorageRoot()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return System.IO.Path.Combine(appData, "Code", "User", "workspaceStorage");
        }

        /// <summary>读取最近项目：mtime 降序（v1 语义）、按目录去重、坏条目跳过。</summary>
        public static List<VscProject> Load()
        {
            return LoadFrom(StorageRoot());
        }

        /// <summary>指定根目录读取（自测注入夹具用；生产走 Load）。</summary>
        public static List<VscProject> LoadFrom(string root)
        {
            var list = new List<VscProject>();
            string[] dirs;
            try { dirs = Directory.GetDirectories(root); }
            catch { return list; } // 没装 VSCode / 从未打开过项目

            foreach (var dir in dirs)
            {
                var project = ReadEntry(dir);
                if (project != null) list.Add(project);
            }
            list.Sort(delegate(VscProject a, VscProject b) { return b.OpenedAt.CompareTo(a.OpenedAt); });

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unique = new List<VscProject>();
            foreach (var project in list)
            {
                if (!seen.Add(project.Path)) continue; // 同一目录多个 storage（不同窗口/工作区）只留最近的
                unique.Add(project);
            }
            return unique;
        }

        private static VscProject ReadEntry(string storageDir)
        {
            var jsonPath = System.IO.Path.Combine(storageDir, "workspace.json");
            if (!File.Exists(jsonPath)) return null;

            Dictionary<string, object> data;
            try
            {
                data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(jsonPath));
            }
            catch { return null; }
            if (data == null) return null;

            var folder = data.ContainsKey("folder") ? data["folder"] as string : null;
            var workspace = data.ContainsKey("workspace") ? data["workspace"] as string : null;

            DateTime openedAt;
            try { openedAt = Directory.GetLastWriteTimeUtc(storageDir); }
            catch { openedAt = DateTime.MinValue; }

            // 多根工作区优先：workspace 指向 .code-workspace 文件
            if (!string.IsNullOrEmpty(workspace))
            {
                var file = UriToPath(workspace);
                var parent = string.IsNullOrEmpty(file) ? null : System.IO.Path.GetDirectoryName(file);
                if (string.IsNullOrEmpty(parent)) return null;
                return new VscProject
                {
                    Path = parent,
                    WorkspaceFile = file,
                    Title = System.IO.Path.GetFileNameWithoutExtension(file),
                    OpenedAt = openedAt
                };
            }

            if (!string.IsNullOrEmpty(folder))
            {
                var path = UriToPath(folder);
                if (string.IsNullOrEmpty(path)) return null;
                var trimmed = path.TrimEnd('\\');
                var title = System.IO.Path.GetFileName(trimmed);
                return new VscProject
                {
                    Path = trimmed,
                    Title = string.IsNullOrEmpty(title) ? trimmed : title,
                    OpenedAt = openedAt
                };
            }
            return null;
        }

        /// <summary>file:///c%3A/Users/... → C:\Users\...（盘符统一大写，v1 normalizeWindowsPath 语义）。
        /// 注意：盘符被百分号编码（%3A）时 .NET 会给出 "/c:/Users/..."，必须自己剥掉前导斜杠。</summary>
        public static string UriToPath(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            try
            {
                var uri = new Uri(raw);
                if (!uri.IsFile) return null;

                var path = uri.LocalPath.Replace('/', '\\');
                if (path.StartsWith("\\\\", StringComparison.Ordinal)) return path; // UNC 路径原样返回
                if (path.Length >= 3 && path[0] == '\\' && path[2] == ':') path = path.Substring(1); // \c:\... → c:\...
                if (path.Length >= 2 && path[1] == ':' && char.IsLower(path[0]))
                    path = char.ToUpperInvariant(path[0]) + path.Substring(1);
                return path;
            }
            catch { return null; }
        }
    }
}
