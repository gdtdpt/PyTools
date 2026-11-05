using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace PyTools.Core
{
    /// <summary>
    /// OCR 图片来源装载（v1 ai-ocr 语义移植）：剪贴板优先取 PNG 原始流，本地文件保留原格式字节。
    /// 保留原格式的理由（拍板 #2）：4000×3000 照片转 PNG 可能 20MB+，而 OpenAI 兼容接口接受 jpeg。
    /// 上限 10MB 直接拒绝（拍板 #4）：隐式降质会让识别结果悄悄变差，不如让用户知情。
    /// </summary>
    public sealed class OcrImageRecord
    {
        public bool Ok;
        public byte[] Bytes;
        public string Mime = "";
        public int Width;
        public int Height;
        public string Error = "";

        public int ByteCount { get { return Bytes == null ? 0 : Bytes.Length; } }

        /// <summary>接口用的 dataUrl（保留原格式 MIME）。</summary>
        public string DataUrl
        {
            get
            {
                if (Bytes == null || Bytes.Length == 0) return "";
                return "data:" + Mime + ";base64," + Convert.ToBase64String(Bytes);
            }
        }

        /// <summary>预览用位图（OnLoad + Freeze，避免文件/流关闭后延迟加载失败）。</summary>
        public BitmapImage ToBitmap()
        {
            if (Bytes == null || Bytes.Length == 0) return null;
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new MemoryStream(Bytes);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch { return null; }
        }
    }

    public static class OcrImage
    {
        public const int MaxBytes = 10 * 1024 * 1024;

        private static readonly string[] AllowedExtensions = { ".png", ".jpg", ".jpeg", ".bmp" };

        /// <summary>从剪贴板读图。优先 "PNG" 格式流（原始压缩字节），退化才走 GetImage() 再编码。</summary>
        public static OcrImageRecord FromClipboard()
        {
            try
            {
                var data = Clipboard.GetDataObject();
                if (data == null) return Fail("剪贴板没有图片");

                if (data.GetDataPresent("PNG"))
                {
                    var raw = ReadPngStream(data.GetData("PNG"));
                    if (raw != null)
                    {
                        var fromPng = FromBytes(raw, "image/png");
                        if (fromPng.Ok) return fromPng;
                    }
                }

                if (!Clipboard.ContainsImage()) return Fail("剪贴板没有图片");
                var source = Clipboard.GetImage();
                if (source == null) return Fail("剪贴板没有图片");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));
                using (var stream = new MemoryStream())
                {
                    encoder.Save(stream);
                    return FromBytes(stream.ToArray(), "image/png");
                }
            }
            catch (Exception ex)
            {
                return Fail("读取剪贴板失败：" + ex.Message);
            }
        }

        /// <summary>"PNG" 格式在不同来源下可能是 MemoryStream 或 byte[]，两种都接。</summary>
        private static byte[] ReadPngStream(object value)
        {
            var stream = value as MemoryStream;
            if (stream != null) return stream.ToArray();
            var bytes = value as byte[];
            if (bytes != null && bytes.Length > 0) return bytes;
            var anyStream = value as Stream;
            if (anyStream != null)
            {
                using (var buffer = new MemoryStream())
                {
                    anyStream.Position = 0;
                    anyStream.CopyTo(buffer);
                    return buffer.ToArray();
                }
            }
            return null;
        }

        /// <summary>从本地图片文件装载（扩展名白名单 + 大小上限 + 可解码性校验）。</summary>
        public static OcrImageRecord FromFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return Fail("没有选择图片");
            if (Directory.Exists(path)) return Fail("仅支持图片文件，不支持文件夹");
            if (!File.Exists(path)) return Fail("文件不存在：" + path);
            var extension = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            if (Array.IndexOf(AllowedExtensions, extension) < 0) return Fail("仅支持 png / jpg / jpeg / bmp 图片");
            try
            {
                var info = new FileInfo(path);
                if (info.Length > MaxBytes) return Fail("图片超过 10MB，请先压缩或裁剪后再识别");
                var bytes = File.ReadAllBytes(path);
                return FromBytes(bytes, MimeOf(extension));
            }
            catch (Exception ex)
            {
                return Fail("读取图片失败：" + ex.Message);
            }
        }

        /// <summary>字节 → 记录：解码校验（不让接口收到坏图）+ 取原始像素尺寸 + 全透明 alpha 修复。</summary>
        public static OcrImageRecord FromBytes(byte[] bytes, string mime)
        {
            if (bytes == null || bytes.Length == 0) return Fail("图片内容为空");
            if (bytes.Length > MaxBytes) return Fail("图片超过 10MB，请先压缩或裁剪后再识别");
            try
            {
                using (var stream = new MemoryStream(bytes))
                {
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    if (decoder.Frames.Count == 0) return Fail("图片无法解码，可能已损坏");
                    var frame = decoder.Frames[0];
                    if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0) return Fail("图片无法解码，可能已损坏");

                    // 剪贴板 DIB 坑（2026-11-05 实测）：GDI 系截图工具放的 32bpp DIB 不写 alpha 字节（全 0），
                    // GetImage() 兜底重编码会把 alpha=0 原样带进 PNG——解码一切正常（尺寸/大小都对，
                    // 状态栏报「载入成功」）但渲染全透明，表现为「载入成功但卡片空白」。
                    // 检测到非预乘格式全零 alpha 时展平为 Bgr32（丢 alpha 保 RGB）。真实透明图不受影响。
                    var fixedBytes = bytes;
                    var fixedMime = mime;
                    if (HasDeadAlpha(frame))
                    {
                        var opaque = EncodeOpaque(frame);
                        if (opaque != null)
                        {
                            fixedBytes = opaque;
                            fixedMime = "image/png"; // 展平重编码一律 PNG
                        }
                    }
                    return new OcrImageRecord { Ok = true, Bytes = fixedBytes, Mime = fixedMime, Width = frame.PixelWidth, Height = frame.PixelHeight };
                }
            }
            catch { return Fail("图片无法解码，可能已损坏"); }
        }

        /// <summary>非预乘 alpha 格式（Bgra32；Rgba32 非法输入时 WIC 解码会归一为 Bgra32）且采样行 alpha 全 0 → 判定死 alpha。</summary>
        private static bool HasDeadAlpha(BitmapSource frame)
        {
            var format = frame.Format;
            if (format != System.Windows.Media.PixelFormats.Bgra32)
            {
                return false; // Pbgra32 预乘后 a=0 则 RGB 必为 0，无从恢复，不动
            }
            int width = frame.PixelWidth, height = frame.PixelHeight;
            int stride = width * 4;
            var row = new byte[stride];
            int samples = Math.Min(height, 256); // 采样至多 256 行，全 0 才判死
            for (int i = 0; i < samples; i++)
            {
                int y = i * height / samples;
                frame.CopyPixels(new System.Windows.Int32Rect(0, y, width, 1), row, stride, 0);
                for (int x = 3; x < stride; x += 4)
                {
                    if (row[x] != 0) return false;
                }
            }
            return true;
        }

        /// <summary>丢 alpha 保 RGB 重编码 PNG（Bgra32 → Bgr32，非预乘直转不损 RGB）。</summary>
        private static byte[] EncodeOpaque(BitmapSource frame)
        {
            try
            {
                var converted = new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgr32, null, 0);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(converted));
                using (var stream = new MemoryStream())
                {
                    encoder.Save(stream);
                    return stream.ToArray();
                }
            }
            catch { return null; }
        }

        private static string MimeOf(string extension)
        {
            switch (extension)
            {
                case ".png": return "image/png";
                case ".bmp": return "image/bmp";
                default: return "image/jpeg"; // .jpg / .jpeg
            }
        }

        private static OcrImageRecord Fail(string error)
        {
            return new OcrImageRecord { Ok = false, Error = error };
        }
    }
}
