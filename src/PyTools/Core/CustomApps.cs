using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace PyTools.Core
{
    /// <summary>登记条目（custom-apps.json，v1 格式逐字段沿用）。</summary>
    public sealed class CustomApp
    {
        public string Name = "";
        public string Path = "";
        public string Args = "";
        public string AddedAt = "";
    }

    /// <summary>
    /// 自定义应用清单：exe 旁 custom-apps.json，独立于 config.json。
    /// 条目为用户显式登记的绿色应用；目标不存在时按真实路径移除（v1 1.8.1 语义）。
    /// </summary>
    public sealed class CustomApps
    {
        private const string FailInvalid = "invalid";
        private const string FailNotFound = "not-found";
        private const string FailNotFile = "not-file";
        private const string FailBrokenShortcut = "shortcut-broken";
        private const string FailNotApp = "not-app";
        private const string FailDuplicate = "duplicate";

        private static readonly HashSet<string> ApplicationExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".com", ".scr", ".msc", ".cpl"
        };

        private readonly string _path;

        public CustomApps(string path) { _path = path; }

        public static string DefaultPath(string baseDir)
        {
            return Path.Combine(baseDir, "custom-apps.json");
        }

        public string FilePath { get { return _path; } }

        public List<CustomApp> Load()
        {
            try
            {
                var data = ReadData();
                return data.Apps;
            }
            catch
            {
                return new List<CustomApp>();
            }
        }

        private Data ReadData()
        {
            // 文件不存在 / JSON 损坏 → 空清单（v1 load 的语义：首次运行与损坏同构）
            try
            {
                var json = File.ReadAllText(_path);
                var data = new JavaScriptSerializer().Deserialize<Data>(json);
                if (data == null || data.Apps == null) data = new Data();
                var kept = new List<CustomApp>();
                foreach (var item in data.Apps)
                {
                    if (item != null && !string.IsNullOrEmpty(item.Path)) kept.Add(item);
                }
                data.Apps = kept;
                return data;
            }
            catch
            {
                return new Data();
            }
        }

        private void Save(Data data)
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_path, new JavaScriptSerializer().Serialize(data));
        }

        public static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath((a ?? "").Trim()),
                    Path.GetFullPath((b ?? "").Trim()),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public bool Has(string realPath)
        {
            foreach (var item in Load())
            {
                if (SamePath(item.Path, realPath)) return true;
            }
            return false;
        }

        public sealed class AddResult
        {
            public bool Ok;
            public string Reason = "";  // 失败原因（v1 resolveTarget 的 reason 语义）
            public string Name = "";
            public string Args = "";
            public string Path = "";
        }

        /// <summary>
        /// 登记一个应用：lnk 展开为真实应用路径（最多两层），其余要求是应用扩展名。
        /// </summary>
        public AddResult Add(string rawPath)
        {
            var result = new AddResult();
            string current = Normalize(rawPath);
            if (current == null) { result.Reason = FailInvalid; return result; }

            var name = "";
            var args = "";
            var hops = 0;
            while (true)
            {
                FileInfo stat;
                try { stat = new FileInfo(current); }
                catch { result.Reason = FailNotFound; return result; }
                if (!stat.Exists) { result.Reason = FailNotFound; return result; }

                var extension = (stat.Extension ?? "").ToLowerInvariant();
                if (extension == ".lnk")
                {
                    if (hops >= 2) { result.Reason = FailBrokenShortcut; return result; }
                    Native.ShellLink.LinkInfo link;
                    if (!Native.ShellLink.TryResolve(current, out link) || string.IsNullOrEmpty(link.Target))
                    {
                        result.Reason = FailBrokenShortcut; return result;
                    }
                    if (name.Length == 0) name = Path.GetFileNameWithoutExtension(current).Trim();
                    args = (link.Arguments ?? "").Trim();
                    current = Path.GetFullPath(link.Target);
                    hops++;
                    continue;
                }

                if (!ApplicationExtensions.Contains(extension)) { result.Reason = FailNotApp; return result; }
                if (name.Length == 0) name = Path.GetFileNameWithoutExtension(current).Trim();
                break;
            }

            if (Has(current)) { result.Reason = FailDuplicate; result.Name = name; return result; }

            var data = ReadData();
            data.Apps.Add(new CustomApp
            {
                Name = name,
                Path = current,
                Args = args,
                AddedAt = DateTime.UtcNow.ToString("o")
            });
            Save(data);
            result.Ok = true;
            result.Name = string.IsNullOrEmpty(name) ? "未命名应用" : name;
            result.Args = args;
            result.Path = current;
            return result;
        }

        /// <summary>目标已被删除时移除登记（按真实路径匹配，与 Has/Add 同一判定口径）。返回是否确有删除。</summary>
        public bool Remove(string realPath)
        {
            var data = ReadData();
            var kept = new List<CustomApp>();
            var changed = false;
            foreach (var item in data.Apps)
            {
                if (SamePath(item.Path, realPath)) { changed = true; continue; }
                kept.Add(item);
            }
            if (!changed) return false;
            data.Apps = kept;
            Save(data);
            return true;
        }

        public void Clear()
        {
            Save(new Data());
        }

        private static string Normalize(string rawPath)
        {
            var source = (rawPath ?? "").Trim();
            if (source.Length == 0) return null;
            try { return Path.GetFullPath(source); }
            catch { return null; }
        }

        private sealed class Data
        {
            public int Version = 1;
            public List<CustomApp> Apps = new List<CustomApp>();
        }
    }
}
