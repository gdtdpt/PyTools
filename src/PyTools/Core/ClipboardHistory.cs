using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media.Imaging;

namespace PyTools.Core
{
    public enum ClipKind { Text, Files, Image }

    public sealed class ClipFile
    {
        public string Name;
        public string Path;
    }

    /// <summary>一条剪贴板记录。字段与 v1 clipboard-history.json 一一对应（v2 首启可直接读 v1 数据）。</summary>
    public sealed class ClipRecord
    {
        public string Id;
        public ClipKind Kind;
        public long CreatedAt;
        public long UpdatedAt;
        public string Text;
        public List<ClipFile> Files;
        /// <summary>去重签名：文本=原文，文件=小写路径串，图片=PNG 的 SHA1。图片记录加载时为 null，首次去重比对时惰性计算。</summary>
        public string Signature;
        /// <summary>PNG 字节的 SHA1（仅图片记录，落盘到 json 的 sha1 键，避免每次启动重读全部图片）。</summary>
        public string ImageSha1;

        public bool IsImage { get { return Kind == ClipKind.Image; } }

        public string KindLabel
        {
            get { return Kind == ClipKind.Text ? "文本" : (Kind == ClipKind.Image ? "图片" : "文件"); }
        }

        /// <summary>左侧类型字符（图片走缩略图，不显示字符）。</summary>
        public string Glyph
        {
            get { return Kind == ClipKind.Text ? "文" : (Kind == ClipKind.Files ? "件" : ""); }
        }

        /// <summary>标题：文本取首行预览，文件取首个文件名，图片固定「图片」。</summary>
        public string Preview
        {
            get
            {
                if (Kind == ClipKind.Text)
                {
                    var line = FirstLine(Text);
                    if (line.Length == 0) return "文本";
                    return line.Length > ClipboardHistory.MaxPreviewChars
                        ? line.Substring(0, ClipboardHistory.MaxPreviewChars) + "…"
                        : line;
                }
                if (Kind == ClipKind.Files)
                {
                    if (Files == null || Files.Count == 0) return "文件";
                    return Files[0].Name;
                }
                return "图片";
            }
        }

        /// <summary>副标题：文本字数 / 文件个数 / 图片尺寸（尺寸在缩略图解码后回填）。</summary>
        public string Subtitle
        {
            get
            {
                if (Kind == ClipKind.Text) return "共 " + (Text == null ? 0 : Text.Length) + " 字";
                if (Kind == ClipKind.Files) return "共 " + (Files == null ? 0 : Files.Count) + " 个文件";
                return _imageSize ?? "图片";
            }
        }

        private string _imageSize;

        public void SetImageSize(int width, int height)
        {
            _imageSize = width + " × " + height;
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var index = text.IndexOfAny(new[] { '\r', '\n' });
            var line = index < 0 ? text : text.Substring(0, index);
            return line.Trim();
        }
    }

    /// <summary>
    /// 剪贴板历史（M3，矩阵 C1-C4）：捕获、去重、容量淘汰、落盘、写回、缩略图缓存。
    /// 数据格式与 v1 一致；图片只存原图 PNG，缩略图按记录 id 在内存里懒解码
    /// （v1 的性能根因是每次呼出把全部图片重新编码成 data URL）。
    /// </summary>
    public sealed class ClipboardHistory
    {
        public const int MaxRecords = 100;
        public const int MaxPreviewChars = 200;
        public const long MaxTextChars = 1024 * 1024;        // 单条文本上限（字符）
        public const long MaxImageBytes = 20L * 1024 * 1024; // PNG 编码后上限
        public const long MaxImagePixels = 25000000;         // 25MP：挡住高压缩率大图解码爆内存
        public const int ThumbDecodeEdge = 64;               // 缩略图解码边长（约 2x DPI 余量）

        // 隐私格式（2026-10-30 确认）：前者强语义（存在即拦），后者弱语义（值为 0 才拦）
        public const string ExcludeFormat = "ExcludeClipboardContentFromMonitorProcessing";
        public const string HistoryFormat = "CanIncludeInClipboardHistory";

        private const int SelfWriteSuppressMs = 700;

        private readonly string _jsonPath;
        private readonly string _imageDir;
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();
        private readonly List<ClipRecord> _records = new List<ClipRecord>();
        private readonly Dictionary<string, BitmapSource> _thumbs = new Dictionary<string, BitmapSource>();
        private long _suppressUntilTicks;

        public ClipboardHistory(string configDir)
        {
            _jsonPath = Path.Combine(configDir, "clipboard-history.json");
            _imageDir = Path.Combine(configDir, "clipboard-history", "images");
            Load();
        }

        /// <summary>v2.clipboardPrivacyFilter，每次捕获时读取（开关即时生效）。</summary>
        public bool PrivacyFilter { get; set; }

        /// <summary>记录列表（已按 updatedAt 倒序）。</summary>
        public IList<ClipRecord> Records { get { return _records; } }

        public string JsonPath { get { return _jsonPath; } }
        public string ImageDirectory { get { return _imageDir; } }

        // ---- 捕获 ----

        /// <summary>
        /// 从剪贴板数据对象生成快照（不落盘、不改状态）。返回 null = 忽略
        /// （隐私格式 / 空内容 / 超上限 / 异常）。imagePng 仅在图片记录时非空。
        /// </summary>
        public ClipRecord Snapshot(IDataObject data, out byte[] imagePng, out string skipReason)
        {
            imagePng = null;
            skipReason = null;
            if (data == null) { skipReason = "empty"; return null; }

            if (PrivacyFilter)
            {
                var privacy = PrivacySkipReason(data);
                if (privacy != null) { skipReason = privacy; return null; }
            }

            try
            {
                // 优先级对齐 v1：files > image > text。
                // 已知取舍：Office 系同时提供 Bitmap 与 Text，会记为图片（与 v1 一致）；要反转只需调整本段顺序。
                var files = ReadFiles(data);
                if (files != null && files.Count > 0)
                {
                    return new ClipRecord
                    {
                        Id = Guid.NewGuid().ToString("D"),
                        Kind = ClipKind.Files,
                        Files = files,
                        CreatedAt = Now,
                        UpdatedAt = Now,
                        Signature = FileSignature(files)
                    };
                }

                int width, height;
                byte[] png;
                if (TryReadPng(data, out png, out width, out height))
                {
                    if (png.LongLength > MaxImageBytes) { skipReason = "image-too-large"; return null; }
                    imagePng = png;
                    var sha1 = Sha1Hex(png);
                    var record = new ClipRecord
                    {
                        Id = Guid.NewGuid().ToString("D"),
                        Kind = ClipKind.Image,
                        CreatedAt = Now,
                        UpdatedAt = Now,
                        ImageSha1 = sha1,
                        Signature = "image:" + sha1
                    };
                    record.SetImageSize(width, height);
                    return record;
                }

                var text = ReadText(data);
                if (string.IsNullOrEmpty(text)) { skipReason = "empty"; return null; }
                if (text.Length > MaxTextChars) { skipReason = "text-too-large"; return null; }
                return new ClipRecord
                {
                    Id = Guid.NewGuid().ToString("D"),
                    Kind = ClipKind.Text,
                    Text = text,
                    CreatedAt = Now,
                    UpdatedAt = Now,
                    Signature = "text:" + text
                };
            }
            catch (Exception ex)
            {
                Logger.Error("clipboard snapshot failed: " + ex.Message);
                skipReason = "error";
                return null;
            }
        }

        /// <summary>
        /// 捕获并入库：同签名视为重复（只更新时间并置顶，不新增）、超 100 条淘汰最旧、落盘。
        /// 返回 null 表示未记录（重复时 skipReason="duplicate"）。
        /// </summary>
        public ClipRecord Capture(IDataObject data, out string skipReason)
        {
            byte[] png;
            var snapshot = Snapshot(data, out png, out skipReason);
            if (snapshot == null) return null;

            var existing = FindDuplicate(snapshot);
            if (existing != null)
            {
                existing.UpdatedAt = Now;
                _records.Remove(existing);
                _records.Insert(0, existing);
                Save();
                skipReason = "duplicate";
                return null;
            }

            _records.Insert(0, snapshot);
            if (snapshot.Kind == ClipKind.Image && png != null)
            {
                try
                {
                    Directory.CreateDirectory(_imageDir);
                    File.WriteAllBytes(ImageFile(snapshot.Id), png);
                }
                catch (Exception ex) { Logger.Error("clipboard image save failed: " + ex.Message); }
            }

            Trim();
            Save();
            return snapshot;
        }

        /// <summary>
        /// 自身写回剪贴板会触发一次 WM_CLIPBOARDUPDATE；按时间窗屏蔽，避免"写回即新增一条"。
        /// 图片重新编码后字节可能与原 PNG 不同，因此不能只靠签名比对，时间窗才是可靠判据。
        /// </summary>
        public bool InSelfWriteWindow
        {
            get { return DateTime.UtcNow.Ticks < _suppressUntilTicks; }
        }

        /// <summary>按签名查重。图片记录若加载时未带 sha1，则此处惰性读文件计算一次（仅针对老数据，之后内存有值）。</summary>
        private ClipRecord FindDuplicate(ClipRecord snapshot)
        {
            foreach (var record in _records)
            {
                if (record.Kind == ClipKind.Image && record.Signature == null)
                    record.Signature = ComputeImageSignature(record);
                if (record.Signature != null && record.Signature == snapshot.Signature) return record;
            }
            return null;
        }

        private string ComputeImageSignature(ClipRecord record)
        {
            try
            {
                var bytes = File.ReadAllBytes(ImageFile(record.Id));
                record.ImageSha1 = Sha1Hex(bytes);
                return "image:" + record.ImageSha1;
            }
            catch
            {
                return "image-unavailable:" + record.Id; // 文件缺失：永不会与任何快照相等
            }
        }

        private void Trim()
        {
            while (_records.Count > MaxRecords)
            {
                var removed = _records[_records.Count - 1];
                _records.RemoveAt(_records.Count - 1);
                RemoveImageOf(removed);
            }
        }

        private void RemoveImageOf(ClipRecord record)
        {
            if (record == null || record.Kind != ClipKind.Image) return;
            _thumbs.Remove(record.Id);
            try { File.Delete(ImageFile(record.Id)); }
            catch (Exception ex) { Logger.Error("clipboard image delete failed: " + ex.Message); }
        }

        // ---- 写回 ----

        /// <summary>
        /// 把记录写回剪贴板（调用方须先收起窗口并恢复焦点，否则 Ctrl+V 会粘回自己）。
        /// 返回 null = 成功，否则错误文案。
        /// </summary>
        public string WriteBack(ClipRecord record)
        {
            if (record == null) return "记录不存在";
            _suppressUntilTicks = DateTime.UtcNow.AddMilliseconds(SelfWriteSuppressMs).Ticks;
            try
            {
                var data = new DataObject();
                switch (record.Kind)
                {
                    case ClipKind.Text:
                        data.SetData(DataFormats.UnicodeText, record.Text);
                        data.SetData(DataFormats.Text, record.Text);
                        break;
                    case ClipKind.Files:
                        var paths = new System.Collections.Specialized.StringCollection();
                        foreach (var file in record.Files) paths.Add(file.Path);
                        data.SetFileDropList(paths);
                        break;
                    case ClipKind.Image:
                        var image = DecodeFull(record);
                        if (image == null) return "图片文件缺失，无法写回";
                        data.SetImage(image);
                        break;
                }
                Clipboard.SetDataObject(data, true);

                // 置顶 + 刷新时间（v1 select 语义）
                _records.Remove(record);
                record.UpdatedAt = Now;
                _records.Insert(0, record);
                Save();
                return null;
            }
            catch (Exception ex)
            {
                return "写回剪贴板失败：" + ex.Message;
            }
        }

        // ---- 缩略图（C3）----

        /// <summary>
        /// 按记录 id 缓存缩略图。只解 ThumbDecodeEdge 宽（WPF 解码期缩放，不产生全尺寸位图）。
        /// 列表虚拟化下仅对已实现的容器调用，100 条历史不会全量解码。
        /// </summary>
        public BitmapSource GetThumbnail(ClipRecord record)
        {
            if (record == null || record.Kind != ClipKind.Image) return null;
            BitmapSource cached;
            if (_thumbs.TryGetValue(record.Id, out cached)) return cached;
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(ImageFile(record.Id), UriKind.Absolute);
                image.DecodePixelWidth = ThumbDecodeEdge;
                image.CacheOption = BitmapCacheOption.OnLoad; // 读完即释放文件句柄
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                image.EndInit();
                image.Freeze();
                record.SetImageSize(image.PixelWidth, image.PixelHeight);
                _thumbs[record.Id] = image;
                return image;
            }
            catch (Exception ex)
            {
                Logger.Error("clipboard thumbnail failed: " + ex.Message);
                return null;
            }
        }

        private BitmapSource DecodeFull(ClipRecord record)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(ImageFile(record.Id), UriKind.Absolute);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex)
            {
                Logger.Error("clipboard image decode failed: " + ex.Message);
                return null;
            }
        }

        public string ImageFile(string id)
        {
            return Path.Combine(_imageDir, id + ".png");
        }

        // ---- 读剪贴板内容 ----

        private static List<ClipFile> ReadFiles(IDataObject data)
        {
            if (!data.GetDataPresent(DataFormats.FileDrop, false)) return null;
            var paths = data.GetData(DataFormats.FileDrop, false) as string[];
            if (paths == null || paths.Length == 0) return null;
            var list = new List<ClipFile>();
            foreach (var path in paths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                string name;
                try { name = Path.GetFileName(path); }
                catch { name = path; }
                if (string.IsNullOrEmpty(name)) continue;
                list.Add(new ClipFile { Name = name, Path = path });
            }
            return list;
        }

        private static string ReadText(IDataObject data)
        {
            if (data.GetDataPresent(DataFormats.UnicodeText, false))
            {
                var text = data.GetData(DataFormats.UnicodeText, false) as string;
                if (!string.IsNullOrEmpty(text)) return text;
            }
            if (data.GetDataPresent(DataFormats.Text, false))
            {
                return data.GetData(DataFormats.Text, false) as string;
            }
            return null;
        }

        private static bool TryReadPng(IDataObject data, out byte[] png, out int width, out int height)
        {
            png = null; width = 0; height = 0;
            if (!data.GetDataPresent(DataFormats.Bitmap, false)) return false;
            var source = data.GetData(DataFormats.Bitmap, false) as BitmapSource;
            if (source == null) return false;
            width = source.PixelWidth;
            height = source.PixelHeight;
            if (width <= 0 || height <= 0) return false;
            if ((long)width * height > MaxImagePixels) return false;

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using (var stream = new MemoryStream())
            {
                encoder.Save(stream);
                png = stream.ToArray();
            }
            return png.Length > 0;
        }

        /// <summary>隐私格式判定：返回跳过原因（null = 不跳过）。</summary>
        public static string PrivacySkipReason(IDataObject data)
        {
            try
            {
                // 强语义：程序明确要求"别监控我"（浏览器隐私模式、密码管理器）
                if (data.GetDataPresent(ExcludeFormat, false)) return "privacy-exclude";
                // 弱语义：明确声明"不进系统历史"（值为 0 才算；值解析不出按允许处理）
                if (data.GetDataPresent(HistoryFormat, false))
                {
                    int value;
                    if (TryReadDword(data.GetData(HistoryFormat, false), out value) && value == 0)
                        return "privacy-history";
                }
            }
            catch { /* 格式探测失败按不跳过处理 */ }
            return null;
        }

        private static bool TryReadDword(object raw, out int value)
        {
            value = 0;
            if (raw == null) return false;
            if (raw is int) { value = (int)raw; return true; }
            if (raw is uint) { value = unchecked((int)(uint)raw); return true; }
            var stream = raw as MemoryStream;
            if (stream != null && stream.Length >= 4)
            {
                value = BitConverter.ToInt32(stream.ToArray(), 0);
                return true;
            }
            var bytes = raw as byte[];
            if (bytes != null && bytes.Length >= 4)
            {
                value = BitConverter.ToInt32(bytes, 0);
                return true;
            }
            return false;
        }

        // ---- 签名 ----

        private static string FileSignature(List<ClipFile> files)
        {
            var builder = new StringBuilder("files:");
            for (var i = 0; i < files.Count; i++)
            {
                if (i > 0) builder.Append('\0');
                builder.Append(files[i].Path.ToLowerInvariant());
            }
            return builder.ToString();
        }

        public static string Sha1Hex(byte[] bytes)
        {
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(bytes);
                var builder = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return builder.ToString();
            }
        }

        private static long Now
        {
            get { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }
        }

        // ---- 持久化 ----

        public void Save()
        {
            try
            {
                var list = new List<Dictionary<string, object>>();
                foreach (var record in _records)
                {
                    var item = new Dictionary<string, object>
                    {
                        { "id", record.Id },
                        { "type", TypeName(record.Kind) },
                        { "createdAt", record.CreatedAt },
                        { "updatedAt", record.UpdatedAt }
                    };
                    if (record.Kind == ClipKind.Text) item["text"] = record.Text;
                    if (record.Kind == ClipKind.Files)
                    {
                        var files = new List<Dictionary<string, object>>();
                        foreach (var file in record.Files)
                            files.Add(new Dictionary<string, object> { { "name", file.Name }, { "path", file.Path } });
                        item["files"] = files;
                    }
                    if (record.Kind == ClipKind.Image)
                    {
                        item["imageFile"] = record.Id + ".png";
                        if (!string.IsNullOrEmpty(record.ImageSha1)) item["sha1"] = record.ImageSha1;
                    }
                    list.Add(item);
                }
                var root = new Dictionary<string, object> { { "version", 1 }, { "records", list } };
                var dir = Path.GetDirectoryName(_jsonPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_jsonPath, _serializer.Serialize(root), Encoding.UTF8);
            }
            catch (Exception ex) { Logger.Error("clipboard history save failed: " + ex.Message); }
        }

        private static string TypeName(ClipKind kind)
        {
            return kind == ClipKind.Text ? "text" : (kind == ClipKind.Image ? "image" : "files");
        }

        private void Load()
        {
            _records.Clear();
            if (!File.Exists(_jsonPath)) return;

            try
            {
                var root = _serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(_jsonPath));
                object rawList;
                if (root == null || !root.TryGetValue("records", out rawList)) return;
                var items = rawList as System.Collections.IEnumerable;
                if (items == null) return;

                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in items)
                {
                    var map = item as Dictionary<string, object>;
                    if (map == null) continue;
                    try
                    {
                        var record = ParseRecord(map);
                        if (record == null || seen.Contains(record.Id)) continue;
                        seen.Add(record.Id);
                        _records.Add(record);
                    }
                    catch (Exception ex) { Logger.Error("clipboard record skipped: " + ex.Message); }
                }
            }
            catch (Exception ex) { Logger.Error("clipboard history load failed: " + ex.Message); }

            _records.Sort(delegate(ClipRecord a, ClipRecord b) { return b.UpdatedAt.CompareTo(a.UpdatedAt); });
            CleanupOrphanImages();
        }

        private ClipRecord ParseRecord(Dictionary<string, object> map)
        {
            var id = GetString(map, "id");
            if (string.IsNullOrEmpty(id)) return null;
            var type = GetString(map, "type");
            var record = new ClipRecord
            {
                Id = id,
                CreatedAt = GetLong(map, "createdAt"),
                UpdatedAt = GetLong(map, "updatedAt")
            };

            if (type == "text")
            {
                var text = GetString(map, "text");
                if (string.IsNullOrEmpty(text)) return null;
                record.Kind = ClipKind.Text;
                record.Text = text;
                record.Signature = "text:" + text;
                return record;
            }

            if (type == "files")
            {
                var files = new List<ClipFile>();
                var rawFiles = map.ContainsKey("files") ? map["files"] as System.Collections.IEnumerable : null;
                if (rawFiles != null)
                {
                    foreach (var raw in rawFiles)
                    {
                        var fileMap = raw as Dictionary<string, object>;
                        if (fileMap == null) continue;
                        var path = GetString(fileMap, "path");
                        if (string.IsNullOrEmpty(path)) continue;
                        var name = GetString(fileMap, "name");
                        if (string.IsNullOrEmpty(name)) { try { name = Path.GetFileName(path); } catch { name = path; } }
                        files.Add(new ClipFile { Name = name, Path = path });
                    }
                }
                if (files.Count == 0) return null;
                record.Kind = ClipKind.Files;
                record.Files = files;
                record.Signature = FileSignature(files);
                return record;
            }

            if (type == "image")
            {
                // 文件名必须与 id 严格匹配（v1 有同样校验），且文件必须存在
                var fileName = GetString(map, "imageFile");
                var expected = id + ".png";
                if (!string.IsNullOrEmpty(fileName) && fileName != expected) return null;
                if (!File.Exists(ImageFile(id))) return null;
                record.Kind = ClipKind.Image;
                record.ImageSha1 = GetString(map, "sha1"); // v1 老数据没有此键 → 留空，首次去重比对时惰性计算
                record.Signature = string.IsNullOrEmpty(record.ImageSha1) ? null : "image:" + record.ImageSha1;
                return record;
            }

            return null;
        }

        private void CleanupOrphanImages()
        {
            try
            {
                if (!Directory.Exists(_imageDir)) return;
                var alive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var record in _records)
                    if (record.Kind == ClipKind.Image) alive.Add(record.Id + ".png");
                foreach (var file in Directory.GetFiles(_imageDir, "*.png"))
                {
                    if (!alive.Contains(Path.GetFileName(file))) File.Delete(file);
                }
            }
            catch (Exception ex) { Logger.Error("clipboard orphan cleanup failed: " + ex.Message); }
        }

        private static string GetString(Dictionary<string, object> map, string key)
        {
            object value;
            if (!map.TryGetValue(key, out value) || value == null) return null;
            return value.ToString();
        }

        private static long GetLong(Dictionary<string, object> map, string key)
        {
            object value;
            if (!map.TryGetValue(key, out value) || value == null) return Now;
            try { return Convert.ToInt64(value, CultureInfo.InvariantCulture); }
            catch { return Now; }
        }
    }
}
