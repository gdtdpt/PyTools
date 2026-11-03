using System;
using System.Globalization;
using System.Text;

namespace PyTools.Core
{
    /// <summary>
    /// 常用编码转换引擎（自 v1 tools/encoding-converter/view.js 逐行为移植）。
    /// format：url | base64 | unicode | html | json | hex；direction：encode | decode。
    /// 任何失败抛 FormatException，由视图转警告状态（v1：结果原样保留输入）。
    /// </summary>
    public static class TextEncoders
    {
        public static string Convert(string text, string format, string direction)
        {
            var encode = direction == "encode";
            switch (format)
            {
                case "url": return encode ? UrlEncode(text) : UrlDecode(text);
                case "base64": return encode ? Base64Encode(text) : Base64Decode(text);
                case "unicode": return encode ? UnicodeEscape(text) : UnicodeUnescape(text);
                case "html": return encode ? HtmlEncode(text) : HtmlDecodeText(text);
                case "json": return encode ? StructuredFormats.JsonEncodeLiteral(text) : StructuredFormats.JsonDecodeLiteral(text);
                case "hex": return encode ? HexEncode(text) : HexDecode(text);
                default: throw new FormatException("不支持的格式: " + format);
            }
        }

        // ---- URL（JS encodeURIComponent / decodeURIComponent 语义）----

        // encodeURIComponent 的不转义集：A-Za-z0-9-_.!~*'()。Uri.EscapeDataString 会转义 !'()*，不能用。
        private static bool IsUrlUnreserved(char c)
        {
            return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                || c == '-' || c == '_' || c == '.' || c == '!' || c == '~'
                || c == '*' || c == '\'' || c == '(' || c == ')';
        }

        private static string UrlEncode(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text ?? "");
            var sb = new StringBuilder(bytes.Length);
            foreach (var b in bytes)
            {
                if (IsUrlUnreserved((char)b)) sb.Append((char)b);
                else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static string UrlDecode(string text)
        {
            // 百分号序列按字节收集后严格 UTF-8 解码（非法序列报错，同 JS malformed URI）；
            // 其余字符（含 + 与原生非 ASCII）原样保留——decodeURIComponent 不把 + 当空格。
            if (text == null) return "";
            var sb = new StringBuilder(text.Length);
            var bytes = new byte[text.Length];
            var n = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '%')
                {
                    if (i + 2 >= text.Length) throw new FormatException("不完整的 % 转义序列");
                    bytes[n++] = (byte)(HexVal(text[i + 1]) * 16 + HexVal(text[i + 2]));
                    i += 2;
                    continue;
                }
                if (n > 0)
                {
                    sb.Append(StrictUtf8(bytes, n));
                    n = 0;
                }
                sb.Append(c);
            }
            if (n > 0) sb.Append(StrictUtf8(bytes, n));
            return sb.ToString();
        }

        private static int HexVal(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            throw new FormatException("非法的十六进制字符: " + c);
        }

        // ---- Base64（TextEncoder + btoa / atob + TextDecoder fatal 语义）----

        private static string Base64Encode(string text)
        {
            return System.Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? ""));
        }

        private static string Base64Decode(string text)
        {
            // JS atob 前先去全部空白；atob 容忍缺省 padding，Convert.FromBase64String 不容忍 → 补齐
            var compact = StripWhitespace(text ?? "");
            if (compact.Length == 0) return "";
            var pad = compact.Length % 4;
            if (pad == 1) throw new FormatException("Base64 长度不合法");
            if (pad != 0) compact += new string('=', 4 - pad);
            try
            {
                var bytes = System.Convert.FromBase64String(compact);
                return StrictUtf8(bytes, bytes.Length);
            }
            catch (FormatException) { throw; }
            catch (Exception err) { throw new FormatException("Base64 解码失败: " + err.Message); }
        }

        private static string StripWhitespace(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
                if (!char.IsWhiteSpace(c)) sb.Append(c);
            return sb.ToString();
        }

        // ---- Unicode（按 UTF-16 码元逐字符，与 JS charCodeAt / fromCharCode 一致）----

        private static string UnicodeEscape(string text)
        {
            if (text == null) return "";
            var sb = new StringBuilder(text.Length * 6);
            foreach (var c in text)
                sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static string UnicodeUnescape(string text)
        {
            // JS: /\\u([\da-f]{4})|\\x([\da-f]{2})/gi → String.fromCharCode(parseInt(...))
            if (text == null) return "";
            var sb = new StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '\\' && i + 1 < text.Length && (text[i + 1] == 'u' || text[i + 1] == 'x'))
                {
                    var wide = text[i + 1] == 'u';
                    var digits = wide ? 4 : 2;
                    if (i + 2 + digits <= text.Length)
                    {
                        var value = 0;
                        var ok = true;
                        for (var k = 0; k < digits; k++)
                        {
                            var c = text[i + 2 + k];
                            int v;
                            if (c >= '0' && c <= '9') v = c - '0';
                            else if (c >= 'a' && c <= 'f') v = c - 'a' + 10;
                            else if (c >= 'A' && c <= 'F') v = c - 'A' + 10;
                            else { ok = false; break; }
                            value = value * 16 + v;
                        }
                        if (ok)
                        {
                            sb.Append((char)value);
                            // 已消费 i 与 i+1 及 digits 位数字；continue 会让 for 再 i++，故只补 1+digits
                            i += 1 + digits;
                            continue;
                        }
                    }
                }
                sb.Append(text[i]);
            }
            return sb.ToString();
        }

        // ---- HTML 实体（编码 5 字符同 v1；解码用 WebUtility，实体表与浏览器接近）----

        private static string HtmlEncode(string text)
        {
            if (text == null) return "";
            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&#39;"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        private static string HtmlDecodeText(string text)
        {
            // v1 用 textarea.innerHTML 技巧：只解实体、标签原样保留（textarea 是 RCDATA）。WebUtility.HtmlDecode 行为等价。
            return System.Net.WebUtility.HtmlDecode(text ?? "");
        }

        // ---- Hex（UTF-8 字节 ↔ 小写十六进制，空格分隔）----

        private static string HexEncode(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text ?? "");
            var sb = new StringBuilder(bytes.Length * 3);
            for (var i = 0; i < bytes.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static string HexDecode(string text)
        {
            // v1: 先剥 0x（忽略大小写）再剥空白与逗号；空串/奇数长度/非十六进制字符都报错
            var src = text ?? "";
            var compact = new StringBuilder(src.Length);
            for (var i = 0; i < src.Length; i++)
            {
                var c = src[i];
                if (c == '0' && i + 1 < src.Length && (src[i + 1] == 'x' || src[i + 1] == 'X')) { i++; continue; }
                if (char.IsWhiteSpace(c) || c == ',') continue;
                compact.Append(c);
            }
            var hex = compact.ToString();
            if (hex.Length == 0 || hex.Length % 2 != 0) throw new FormatException("Hex 输入为空或长度不是偶数");
            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = (byte)(HexVal(hex[i * 2]) * 16 + HexVal(hex[i * 2 + 1]));
            return StrictUtf8(bytes, bytes.Length);
        }

        // ---- 公共 ----

        /// <summary>严格 UTF-8 解码：非法字节序列抛异常（对应 JS TextDecoder fatal:true / malformed URI）。</summary>
        private static string StrictUtf8(byte[] bytes, int count)
        {
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes, 0, count);
            }
            catch (Exception err)
            {
                throw new FormatException("不是合法的 UTF-8 字节序列: " + err.Message);
            }
        }
    }
}
