using System;
using System.IO;
using System.Text;

namespace PyTools.Core
{
    /// <summary>
    /// 文本文件读取与编码检测（text-diff 用，2026-11-04 用户确认需要 GBK）：
    /// BOM（UTF-8 / UTF-16 LE / BE）优先 → 无 BOM 先按严格 UTF-8 解码 → 失败按 GBK(936)。
    /// 不做编码选择器（v1 也没有）；GBK 解码器由 BCL 内置（Windows 代码页 936），零第三方依赖。
    /// </summary>
    public static class TextFileLoader
    {
        private const int GbkCodePage = 936;

        /// <summary>严格模式：遇到非法 UTF-8 字节序列抛 DecoderFallbackException（用于编码探测）。</summary>
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static string Read(string path, out string encodingName)
        {
            return Decode(File.ReadAllBytes(path), out encodingName);
        }

        public static string Decode(byte[] bytes, out string encodingName)
        {
            if (bytes == null) bytes = new byte[0];

            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                encodingName = "UTF-8 BOM";
                return StrictUtf8.GetString(bytes, 3, bytes.Length - 3);
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                encodingName = "UTF-16 LE";
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                encodingName = "UTF-16 BE";
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            }

            try
            {
                encodingName = "UTF-8";
                return StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                // 无 BOM 且不是合法 UTF-8：中文文本的本机现实是 GBK/GB18030 系
                encodingName = "GBK";
                return Encoding.GetEncoding(GbkCodePage).GetString(bytes);
            }
        }
    }
}
