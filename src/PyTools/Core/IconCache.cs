using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;

namespace PyTools.Core
{
    /// <summary>
    /// 图标提取与落盘缓存（v1 没有的改进）：进程内 ExtractAssociatedIcon（v1 是 spawn PowerShell 子进程），
    /// PNG 32×32 落盘 exe 旁 icons\，文件名 = SHA1(iconPath小写)+mtime 刻度（源文件变更自动失效重提取）。
    /// 提取在后台线程执行，结果经回调（调用方负责回到 UI 线程）。
    /// </summary>
    public sealed class IconCache
    {
        private readonly string _cacheDir;
        private readonly ConcurrentDictionary<string, BitmapImage> _memory =
            new ConcurrentDictionary<string, BitmapImage>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, object> _inflight =
            new ConcurrentDictionary<string, object>(StringComparer.Ordinal);

        public IconCache(string baseDir)
        {
            _cacheDir = Path.Combine(baseDir, "icons");
        }

        /// <summary>仅内存命中；未命中返回 null（调用方可先给占位再 LoadAsync）。</summary>
        public BitmapImage GetCached(string iconPath)
        {
            var key = (iconPath ?? "").ToLowerInvariant();
            if (key.Length == 0) return null;
            BitmapImage image;
            return _memory.TryGetValue(key, out image) ? image : null;
        }

        /// <summary>
        /// 异步取图标：内存 → 落盘缓存 → 现场提取。onLoaded 可能在后台线程触发，
        /// 拿到的 BitmapImage 已 Freeze，可跨线程使用；失败回调 image=null。
        /// </summary>
        public void LoadAsync(string iconPath, Action<string, BitmapImage> onLoaded)
        {
            var key = (iconPath ?? "").ToLowerInvariant();
            if (key.Length == 0) { FireNull(onLoaded, iconPath); return; }

            BitmapImage cached;
            if (_memory.TryGetValue(key, out cached)) { Deliver(onLoaded, iconPath, cached); return; }
            if (!_inflight.TryAdd(key, null)) return; // 同路径已在加载，等待首次回调即可

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                BitmapImage image = null;
                try
                {
                    var file = CacheFileFor(iconPath);
                    if (file == null)
                    {
                        // 提取失败：记一次负缓存，避免每帧重试
                        _memory[key] = null;
                    }
                    else
                    {
                        if (!File.Exists(file)) ExtractTo(iconPath, file);
                        image = LoadBitmap(file);
                        _memory[key] = image;
                    }
                }
                catch
                {
                    _memory[key] = null;
                }
                finally
                {
                    object _;
                    _inflight.TryRemove(key, out _);
                }
                onLoaded(iconPath, image);
            });
        }

        /// <summary>落盘缓存文件名（供自测）：SHA1 前 16 位 + mtime 刻度；源文件不存在返回 null。</summary>
        public string CacheFileFor(string iconPath)
        {
            try
            {
                var info = new FileInfo(iconPath);
                if (!info.Exists) return null;
                var stamp = info.LastWriteTimeUtc.Ticks;
                string hash;
                using (var sha1 = SHA1.Create())
                {
                    var bytes = sha1.ComputeHash(System.Text.Encoding.UTF8.GetBytes(iconPath.ToLowerInvariant()));
                    var sb = new System.Text.StringBuilder(16);
                    for (var i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
                    hash = sb.ToString();
                }
                return Path.Combine(_cacheDir, hash + "-" + stamp + ".png");
            }
            catch
            {
                return null;
            }
        }

        private static void ExtractTo(string iconPath, string pngFile)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pngFile));
            using (var icon = ExtractIcon(iconPath))
            {
                if (icon == null) throw new InvalidOperationException("icon extract failed: " + iconPath);
                using (var bitmap = icon.ToBitmap())
                {
                    bitmap.Save(pngFile, ImageFormat.Png);
                }
            }
        }

        private static Icon ExtractIcon(string path)
        {
            try
            {
                return Icon.ExtractAssociatedIcon(path);
            }
            catch
            {
                try { return new Icon(path, 32, 32); }
                catch { return null; }
            }
        }

        private static BitmapImage LoadBitmap(string file)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(file, UriKind.Absolute);
            image.DecodePixelWidth = 32;
            image.EndInit();
            image.Freeze();
            return image;
        }

        private static void Deliver(Action<string, BitmapImage> callback, string path, BitmapImage image)
        {
            try { if (callback != null) callback(path, image); }
            catch { /* 回调异常不影响缓存线程 */ }
        }

        private static void FireNull(Action<string, BitmapImage> callback, string path)
        {
            Deliver(callback, path, null);
        }
    }
}
