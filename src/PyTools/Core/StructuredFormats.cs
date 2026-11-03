using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PyTools.Core
{
    /// <summary>格式化结果：识别出的格式与输出文本。</summary>
    public sealed class FormatResult
    {
        public string Format = "";
        public string Output = "";
    }

    /// <summary>
    /// JSON/XML 结构化数据格式化引擎（自 v1 tools/json-format/formats.js 逐行为移植）。
    /// XML 用手写 tokenizer 保真格式化：声明/注释/CDATA/DOCTYPE/混合内容逐字符保留，不经过 XDocument。
    /// JSON 用 JavaScriptSerializer 反序列化（字典保插入序），再自行带缩进序列化。
    /// </summary>
    public static class StructuredFormats
    {
        /// <summary>自动识别：以 &lt; 开头视为 XML，否则 JSON（与 v1 detect 一致）。</summary>
        public static string Detect(string text)
        {
            var value = NormalizeText(text);
            if (value.Length == 0) return null;
            return value.StartsWith("<") ? "xml" : "json";
        }

        /// <summary>处理入口：mode = "format" | "minify"；format = "auto" | "json" | "xml"；indent = "0"/"2"/"4"/"tab"。</summary>
        public static FormatResult Process(string text, string mode, string format, string indent)
        {
            var source = NormalizeText(text);
            if (source.Length == 0) throw new FormatException("没有内容可处理");

            var selected = format == "auto" ? Detect(source) : format;
            if (selected != "json" && selected != "xml") throw new FormatException("不支持的格式: " + format);

            if (mode == "minify")
                return new FormatResult { Format = selected, Output = selected == "json" ? MinifyJson(source) : MinifyXml(source) };
            if (mode == "format")
                return new FormatResult { Format = selected, Output = selected == "json" ? FormatJson(source, indent) : FormatXml(source, indent) };
            throw new FormatException("不支持的处理模式: " + mode);
        }

        // ---- 公共 ----

        private static string NormalizeText(string text)
        {
            if (text == null) return "";
            // 去前导 BOM（可能有多个）+ 首尾空白；TrimStart 不会越界
            return text.TrimStart('\uFEFF').Trim();
        }

        private static string ResolveIndent(string indent)
        {
            if (indent == "tab") return "\t";
            int value;
            if (int.TryParse(indent, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                && (value == 0 || value == 2 || value == 4)) return new string(' ', value);
            throw new FormatException("无效的缩进配置: " + indent);
        }

        // ---- JSON ----
        // JavaScriptSerializer 反序列化 → object tree（List<object> / 保序 KV 列表 / 原始值），再自行缩进输出。

        private static readonly System.Web.Script.Serialization.JavaScriptSerializer Parser =
            new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        private static string FormatJson(string text, string indent)
        {
            var unit = ResolveIndent(indent);
            var value = ParseJson(NormalizeText(text));
            var sb = new StringBuilder();
            WriteJsonValue(sb, value, unit, 0, pretty: true);
            return sb.ToString();
        }

        private static string MinifyJson(string text)
        {
            var value = ParseJson(NormalizeText(text));
            var sb = new StringBuilder();
            WriteJsonValue(sb, value, "", 0, pretty: false);
            return sb.ToString();
        }

        private static object ParseJson(string source)
        {
            try
            {
                return NormalizeJson(Parser.DeserializeObject(source));
            }
            catch (FormatException)
            {
                throw;
            }
            catch (Exception err)
            {
                throw new FormatException("JSON 解析失败: " + CompactMessage(err.Message));
            }
        }

        /// <summary>把 JavaScriptSerializer 的 Dictionary 转成保序键值列表（JSON 对象键序与输入一致）。</summary>
        private static object NormalizeJson(object value)
        {
            var dict = value as IDictionary<string, object>;
            if (dict != null)
            {
                var pairs = new List<KeyValuePair<string, object>>(dict.Count);
                foreach (var entry in dict) pairs.Add(new KeyValuePair<string, object>(entry.Key, NormalizeJson(entry.Value)));
                return new JsonObject(pairs);
            }
            var list = value as System.Collections.IEnumerable;
            if (list != null && !(value is string))
            {
                var items = new List<object>();
                foreach (var item in list) items.Add(NormalizeJson(item));
                return items;
            }
            return value;
        }

        /// <summary>保序 JSON 对象（替代 Dictionary，键序与输入一致）。</summary>
        private sealed class JsonObject
        {
            public readonly List<KeyValuePair<string, object>> Pairs;
            public JsonObject(List<KeyValuePair<string, object>> pairs) { Pairs = pairs; }
        }

        private static void WriteJsonValue(StringBuilder sb, object value, string unit, int level, bool pretty)
        {
            if (value == null) { sb.Append("null"); return; }
            if (value is bool) { sb.Append((bool)value ? "true" : "false"); return; }
            if (value is string) { WriteJsonString(sb, (string)value); return; }
            if (value is JsonObject)
            {
                var pairs = ((JsonObject)value).Pairs;
                if (pairs.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{');
                for (var i = 0; i < pairs.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    if (pretty) { sb.Append('\n').Append(Repeat(unit, level + 1)); }
                    WriteJsonString(sb, pairs[i].Key);
                    sb.Append(':');
                    if (pretty) sb.Append(' ');
                    WriteJsonValue(sb, pairs[i].Value, unit, level + 1, pretty);
                }
                if (pretty) { sb.Append('\n').Append(Repeat(unit, level)); }
                sb.Append('}');
                return;
            }
            var items = value as List<object>;
            if (items != null)
            {
                if (items.Count == 0) { sb.Append("[]"); return; }
                sb.Append('[');
                for (var i = 0; i < items.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    if (pretty) { sb.Append('\n').Append(Repeat(unit, level + 1)); }
                    WriteJsonValue(sb, items[i], unit, level + 1, pretty);
                }
                if (pretty) { sb.Append('\n').Append(Repeat(unit, level)); }
                sb.Append(']');
                return;
            }
            // 数字：原样输出（JavaScriptSerializer 已还原为 int/long/decimal/double）
            sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        private static void WriteJsonString(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (var ch in value)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < ' ') sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---- 供编码转换（EncodeView）使用的 JSON 字面量方向（语义对照 v1 encoding-converter）----

        /// <summary>标准 JSON → JSON 字符串字面量（v1: JSON.stringify(JSON.stringify(p))，仅接受对象/数组）。</summary>
        public static string JsonEncodeLiteral(string text)
        {
            var value = ParseJson(NormalizeText(text));
            if (!(value is JsonObject) && !(value is List<object>)) throw new FormatException("仅支持 JSON 对象或数组");
            var compact = new StringBuilder();
            WriteJsonValue(compact, value, "", 0, pretty: false);
            var literal = new StringBuilder();
            WriteJsonString(literal, compact.ToString());
            return literal.ToString();
        }

        /// <summary>JSON 字符串字面量 → 标准 JSON（字符串：解开内容再 2 空格美化；对象/数组：直接美化；其余类型报错）。</summary>
        public static string JsonDecodeLiteral(string text)
        {
            var value = ParseJson(NormalizeText(text));
            var inner = value as string;
            if (inner != null) value = ParseJson(inner);
            else if (!(value is JsonObject) && !(value is List<object>)) throw new FormatException("仅支持字符串、对象或数组");
            var sb = new StringBuilder();
            WriteJsonValue(sb, value, "  ", 0, pretty: true);
            return sb.ToString();
        }

        private static string CompactMessage(string message)
        {
            return string.Join(" ", (message ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        }

        // ---- XML（手写保真 tokenizer，自 v1 逐行移植）----

        private static void ParseXmlGuard(string text)
        {
            var source = NormalizeText(text);
            if (source.Length == 0) throw new FormatException("XML 解析失败: 没有内容");
            var tokens = TokenizeXml(source);
            if (!tokens.Any(t => t.Kind == "open")) throw new FormatException("XML 解析失败: 没有根元素");
        }

        private static string ReadQuotedMarkup(string source, int start, string closing)
        {
            var end = source.IndexOf(closing, start + closing.Length, StringComparison.Ordinal);
            if (end == -1) throw new FormatException("XML 标记没有闭合");
            return source.Substring(start, end + closing.Length - start);
        }

        private static string ReadTagRaw(string source, int start)
        {
            var quote = '\0';
            for (var i = start + 1; i < source.Length; i++)
            {
                var ch = source[i];
                if (quote != '\0')
                {
                    if (ch == quote) quote = '\0';
                }
                else if (ch == '"' || ch == '\'') quote = ch;
                else if (ch == '>') return source.Substring(start, i + 1 - start);
            }
            throw new FormatException("XML 标记没有闭合");
        }

        private static bool IsSelfClosing(string raw)
        {
            // v1 正则 ^<[^>]*\/\s*>$ 的等价判断：去除尾部空白后以 "/>" 结尾
            return raw.TrimEnd().EndsWith("/>", StringComparison.Ordinal);
        }

        private static string TagName(string raw, int skipHead, int skipTail)
        {
            var inner = raw.Substring(skipHead, raw.Length - skipHead - skipTail).Trim();
            var parts = inner.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[0] : "";
        }

        private static string ReadDoctypeRaw(string source, int start)
        {
            var quote = '\0';
            var internalSubset = false;
            for (var i = start + 2; i < source.Length; i++)
            {
                var ch = source[i];
                if (quote != '\0')
                {
                    if (ch == quote) quote = '\0';
                }
                else if (ch == '"' || ch == '\'') quote = ch;
                else if (ch == '[') internalSubset = true;
                else if (ch == ']') internalSubset = false;
                else if (ch == '>' && !internalSubset) return source.Substring(start, i + 1 - start);
            }
            throw new FormatException("DOCTYPE 没有闭合");
        }

        private sealed class XmlToken
        {
            public string Kind = "";
            public string Raw = "";
            public string Name = "";
            public bool SelfClosing;
        }

        private static List<XmlToken> TokenizeXml(string source)
        {
            var tokens = new List<XmlToken>();
            var i = 0;
            while (i < source.Length)
            {
                if (StartsWithAt(source, "<!--", i))
                {
                    var raw = ReadQuotedMarkup(source, i, "-->");
                    tokens.Add(new XmlToken { Kind = "comment", Raw = raw });
                    i += raw.Length;
                }
                else if (StartsWithAt(source, "<![CDATA[", i))
                {
                    var raw = ReadQuotedMarkup(source, i, "]]>");
                    tokens.Add(new XmlToken { Kind = "cdata", Raw = raw });
                    i += raw.Length;
                }
                else if (i == 0 && StartsWithAt(source, "<?xml", i))
                {
                    var raw = ReadQuotedMarkup(source, i, "?>");
                    tokens.Add(new XmlToken { Kind = "declaration", Raw = raw });
                    i += raw.Length;
                }
                else if (StartsWithAt(source, "<?", i))
                {
                    var raw = ReadQuotedMarkup(source, i, "?>");
                    tokens.Add(new XmlToken { Kind = "processing-instruction", Raw = raw });
                    i += raw.Length;
                }
                else if (StartsWithAt(source, "<!DOCTYPE", i) || StartsWithAt(source, "<!doctype", i))
                {
                    var raw = ReadDoctypeRaw(source, i);
                    tokens.Add(new XmlToken { Kind = "doctype", Raw = raw });
                    i += raw.Length;
                }
                else if (StartsWithAt(source, "</", i))
                {
                    var raw = ReadTagRaw(source, i);
                    tokens.Add(new XmlToken { Kind = "close", Raw = raw, Name = TagName(raw, 2, 1) });
                    i += raw.Length;
                }
                else if (StartsWithAt(source, "<", i))
                {
                    var raw = ReadTagRaw(source, i);
                    var selfClosing = IsSelfClosing(raw);
                    var name = selfClosing ? TagName(raw, 1, 2) : TagName(raw, 1, 1);
                    tokens.Add(new XmlToken { Kind = "open", Raw = raw, Name = name, SelfClosing = selfClosing });
                    i += raw.Length;
                }
                else
                {
                    var next = source.IndexOf('<', i + 1);
                    var end = next == -1 ? source.Length : next;
                    tokens.Add(new XmlToken { Kind = "text", Raw = source.Substring(i, end - i) });
                    i = end;
                }
            }
            return tokens;
        }

        private static bool StartsWithAt(string source, string prefix, int index)
        {
            if (index + prefix.Length > source.Length) return false;
            return string.CompareOrdinal(source, index, prefix, 0, prefix.Length) == 0;
        }

        private sealed class XmlNodeT
        {
            public string Kind = "";
            public string Raw = "";
            public string Name = "";
            public string OpenRaw = "";
            public string CloseRaw = "";
            public bool SelfClosing;
            public List<XmlNodeT> Children = new List<XmlNodeT>();
        }

        private static XmlNodeT BuildXmlTree(string source)
        {
            var root = new XmlNodeT { Kind = "root" };
            var ancestors = new List<XmlNodeT>();
            var parent = root;

            foreach (var token in TokenizeXml(source))
            {
                if (token.Kind == "open")
                {
                    var node = new XmlNodeT
                    {
                        Kind = "element",
                        Name = token.Name,
                        OpenRaw = token.Raw,
                        SelfClosing = token.SelfClosing
                    };
                    parent.Children.Add(node);
                    if (!node.SelfClosing)
                    {
                        ancestors.Add(parent);
                        parent = node;
                    }
                }
                else if (token.Kind == "close")
                {
                    if (parent.Kind != "element" || parent.Name != token.Name)
                        throw new FormatException("XML 标签层级不匹配: " + token.Name);
                    parent.CloseRaw = token.Raw;
                    parent = ancestors[ancestors.Count - 1];
                    ancestors.RemoveAt(ancestors.Count - 1);
                }
                else
                {
                    parent.Children.Add(new XmlNodeT { Kind = token.Kind, Raw = token.Raw });
                }
            }

            if (!ReferenceEquals(parent, root)) throw new FormatException("XML 根元素没有闭合");
            return root;
        }

        private static string CollapseTagWhitespace(string raw)
        {
            var output = new StringBuilder();
            var quote = '\0';
            for (var index = 0; index < raw.Length; index++)
            {
                var ch = raw[index];
                if (quote != '\0')
                {
                    output.Append(ch);
                    if (ch == quote) quote = '\0';
                }
                else if (ch == '"' || ch == '\'')
                {
                    quote = ch;
                    output.Append(ch);
                }
                else if (char.IsWhiteSpace(ch))
                {
                    if (output.Length == 0) continue;
                    var outStr = output.ToString();
                    if (outStr.EndsWith("<") || outStr.EndsWith("</") || outStr.EndsWith("<!")
                        || outStr.EndsWith("<?") || outStr.EndsWith("/") || outStr.EndsWith("=")) continue;
                    var next = index + 1 < raw.Length ? raw[index + 1] : '\0';
                    if (next == '>' || next == '/' || next == '=') continue;
                    output.Append(' ');
                }
                else
                {
                    output.Append(ch);
                }
            }
            return output.ToString();
        }

        private static bool IsWhitespaceText(XmlNodeT node)
        {
            return node.Kind == "text" && node.Raw.Trim().Length == 0;
        }

        private static List<XmlNodeT> ChildNodes(XmlNodeT node)
        {
            return node.Children.Where(child => !IsWhitespaceText(child)).ToList();
        }

        private static bool HasSignificantText(XmlNodeT node)
        {
            return node.Children.Any(child => child.Kind == "text" && child.Raw.Trim().Length > 0);
        }

        private static string RenderOriginalNode(XmlNodeT node)
        {
            if (node.Kind != "element") return node.Raw;
            if (node.SelfClosing) return node.OpenRaw;
            var sb = new StringBuilder(node.OpenRaw);
            foreach (var child in node.Children) sb.Append(RenderOriginalNode(child));
            sb.Append(node.CloseRaw);
            return sb.ToString();
        }

        private static string RenderPrettyNode(XmlNodeT node, int level, string unit)
        {
            var padding = Repeat(unit, level);

            if (node.Kind == "element")
            {
                var open = CollapseTagWhitespace(node.OpenRaw);
                if (node.SelfClosing) return padding + open;

                var close = CollapseTagWhitespace(node.CloseRaw);
                var content = ChildNodes(node);
                if (content.Count == 0) return padding + open + close;

                var text = HasSignificantText(node);
                var markup = content.Any(child => child.Kind != "text");
                if (text && markup)
                    return padding + open + string.Join("", content.Select(RenderOriginalNode)) + close;
                if (!markup)
                    return padding + open + string.Join("", content.Select(child => child.Raw)) + close;
                return padding + open + "\n" +
                    string.Join("\n", content.Select(child => RenderPrettyNode(child, level + 1, unit))) +
                    "\n" + padding + close;
            }

            if (node.Kind == "declaration" || node.Kind == "processing-instruction")
                return padding + CollapseTagWhitespace(node.Raw);
            if (node.Kind == "doctype") return padding + node.Raw.Trim();
            return padding + node.Raw.Trim();
        }

        private static string RenderMinifiedNode(XmlNodeT node)
        {
            if (node.Kind == "element")
            {
                var open = CollapseTagWhitespace(node.OpenRaw);
                if (node.SelfClosing) return open;

                var close = CollapseTagWhitespace(node.CloseRaw);
                var content = ChildNodes(node);
                if (content.Count == 0) return open + close;

                var text = HasSignificantText(node);
                var markup = content.Any(child => child.Kind != "text");
                if (text && markup)
                    return open + string.Join("", content.Select(RenderOriginalNode)) + close;
                if (!markup)
                    return open + string.Join("", content.Select(child => child.Raw)) + close;
                return open + string.Join("", content.Select(RenderMinifiedNode)) + close;
            }

            if (node.Kind == "declaration" || node.Kind == "processing-instruction")
                return CollapseTagWhitespace(node.Raw);
            return node.Raw.Trim();
        }

        private static string FormatXml(string text, string indent)
        {
            var source = NormalizeText(text);
            ParseXmlGuard(source);
            var tree = BuildXmlTree(source);
            var unit = ResolveIndent(indent); // "tab" 或 N 个空格
            var nodes = ChildNodes(tree);
            if (nodes.Count == 0) throw new FormatException("XML 解析失败: 没有根元素");
            return string.Join("\n", nodes.Select(node => RenderPrettyNode(node, 0, unit)));
        }

        private static string MinifyXml(string text)
        {
            var source = NormalizeText(text);
            ParseXmlGuard(source);
            var tree = BuildXmlTree(source);
            var nodes = ChildNodes(tree);
            if (nodes.Count == 0) throw new FormatException("XML 解析失败: 没有根元素");
            return string.Join("", nodes.Select(RenderMinifiedNode));
        }

        private static string Repeat(string unit, int count)
        {
            if (count <= 0) return "";
            var sb = new StringBuilder(unit.Length * count);
            for (var i = 0; i < count; i++) sb.Append(unit);
            return sb.ToString();
        }
    }
}
