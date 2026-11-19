using System;
using System.Collections.Generic;

namespace PyTools.Core
{
    /// <summary>
    /// 网页搜索引擎配置（v2 新增，2026-11-19）：名称 + 触发别名 + URL 模板。
    /// URL 模板里 %s 会被替换为编码后的查询词；**不含 %s 的模板视为「直接打开该页面」**
    /// （DeepSeek 等不支持 URL 带词的站点、以及「打开 ChatGPT 开聊」这类用法）。
    /// </summary>
    public sealed class WebSearchEngine
    {
        public string Name = "";
        /// <summary>触发别名（主搜索框输入「别名 + 空格」进入该引擎的搜索态）；空串 = 只能从左侧列表末尾条目使用。</summary>
        public string Alias = "";
        public string Url = "";
        public bool Enabled = true;
    }

    /// <summary>
    /// 网页搜索：别名前缀解析、URL 拼接、URL 直开判定、内置条目生成。纯逻辑，无 WPF 依赖（可自测）。
    /// </summary>
    public static class WebSearch
    {
        /// <summary>查询词占位符（模板中出现即替换为编码后的查询词）。</summary>
        public const string Placeholder = "%s";

        public const string DefaultEngineName = "Google";

        /// <summary>首次运行（config 里没有 v2.webSearchEngines）时的预置引擎（2026-11-19 用户指定别名）。</summary>
        public static List<WebSearchEngine> Defaults()
        {
            return new List<WebSearchEngine>
            {
                new WebSearchEngine { Name = "Google", Alias = "g",  Url = "https://www.google.com/search?q=" + Placeholder, Enabled = true },
                new WebSearchEngine { Name = "百度",   Alias = "bd", Url = "https://www.baidu.com/s?wd=" + Placeholder,      Enabled = true },
                new WebSearchEngine { Name = "Bing",   Alias = "bi", Url = "https://www.bing.com/search?q=" + Placeholder,   Enabled = false }
            };
        }

        // ---- 配置读写（JavaScriptSerializer 把 JSON 数组反序列化成 ArrayList，必须兜底）----

        /// <summary>从 config 的原始节点解析引擎表；节点缺失/非法一律得到空表（调用方决定是否回落预置）。</summary>
        public static List<WebSearchEngine> ParseList(object node)
        {
            var result = new List<WebSearchEngine>();
            var array = node as System.Collections.ArrayList;
            if (array == null) return result;
            foreach (var item in array)
            {
                var map = item as Dictionary<string, object>;
                if (map == null) continue;
                var engine = new WebSearchEngine
                {
                    Name = MapString(map, "name"),
                    Alias = MapString(map, "alias"),
                    Url = MapString(map, "url"),
                    Enabled = MapBool(map, "enabled", true)
                };
                if (engine.Name.Length == 0) engine.Name = engine.Url; // 宽容：名称缺失用 URL 兜底
                if (engine.Name.Length == 0) continue;
                result.Add(engine);
            }
            return result;
        }

        /// <summary>引擎表 → 可被 JavaScriptSerializer 序列化的原始结构。</summary>
        public static List<Dictionary<string, object>> ToRaw(IEnumerable<WebSearchEngine> engines)
        {
            var result = new List<Dictionary<string, object>>();
            if (engines == null) return result;
            foreach (var engine in engines)
            {
                if (engine == null) continue;
                result.Add(new Dictionary<string, object>
                {
                    { "name", engine.Name ?? "" },
                    { "alias", engine.Alias ?? "" },
                    { "url", engine.Url ?? "" },
                    { "enabled", engine.Enabled }
                });
            }
            return result;
        }

        // ---- 别名前缀 ----

        /// <summary>
        /// 别名前缀命中：输入以「别名 + 空格」开头（别名不区分大小写、必须在开头，别名最长者优先）。
        /// 命中返回 true，query = 空格之后的剩余文本（可能为空串）。
        /// </summary>
        public static bool TryMatchAlias(string text, IEnumerable<WebSearchEngine> engines, out WebSearchEngine engine, out string query)
        {
            engine = null;
            query = "";
            if (string.IsNullOrEmpty(text) || engines == null) return false;

            var bestLength = -1;
            foreach (var candidate in engines)
            {
                if (candidate == null || !candidate.Enabled) continue;
                if ((candidate.Name ?? "").Trim().Length == 0) continue; // 未完成的引擎行（如设置里新增的空行）不参与
                if ((candidate.Url ?? "").Trim().Length == 0) continue;
                var alias = candidate.Alias ?? "";
                if (alias.Length == 0) continue;
                if (text.Length <= alias.Length) continue;          // 必须还有空格（或被更长别名覆盖）
                if (text[alias.Length] != ' ') continue;
                if (string.Compare(text, 0, alias, 0, alias.Length, StringComparison.OrdinalIgnoreCase) != 0) continue;
                if (alias.Length > bestLength)
                {
                    bestLength = alias.Length;
                    engine = candidate;
                    query = text.Substring(alias.Length + 1);
                }
            }
            return engine != null;
        }

        /// <summary>别名合法性：不含空白字符（空格是触发分隔符）。空别名合法（= 不使用前缀）。</summary>
        public static bool IsAliasValid(string alias)
        {
            if (string.IsNullOrEmpty(alias)) return true;
            foreach (var c in alias)
                if (char.IsWhiteSpace(c)) return false;
            return true;
        }

        /// <summary>别名唯一性校验：忽略自身与空别名，比较不区分大小写。被占用返回占用者名称，否则 null。</summary>
        public static string AliasConflict(string alias, WebSearchEngine self, IEnumerable<WebSearchEngine> engines)
        {
            if (string.IsNullOrEmpty(alias) || engines == null) return null;
            foreach (var candidate in engines)
            {
                if (candidate == null || ReferenceEquals(candidate, self)) continue;
                if (string.Equals(candidate.Alias, alias, StringComparison.OrdinalIgnoreCase)) return candidate.Name;
            }
            return null;
        }

        // ---- URL ----

        /// <summary>拼接最终 URL：模板含 %s 则替换为编码后的查询词，不含则原样返回（直接打开页面）。</summary>
        public static bool TryBuildUrl(WebSearchEngine engine, string query, out string url, out string error)
        {
            url = null;
            error = null;
            if (engine == null) { error = "搜索引擎不存在"; return false; }
            var template = (engine.Url ?? "").Trim();
            if (template.Length == 0) { error = "搜索地址为空"; return false; }
            url = template.IndexOf(Placeholder, StringComparison.Ordinal) >= 0
                ? template.Replace(Placeholder, Uri.EscapeDataString(query ?? ""))
                : template;
            if (!IsUrl(url)) { error = "URL 模板无效"; url = null; return false; }
            return true;
        }

        /// <summary>URL 直开判定（2026-11-19 定稿）：仅 http/https 前缀且 Uri 可解析；不做「像域名」模糊判定。</summary>
        public static bool IsUrl(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            var value = text.Trim();
            if (value.IndexOf(' ') >= 0) return false;
            if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri)) return false;
            return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
        }

        /// <summary>用系统默认浏览器打开（已打开的浏览器进程会复用新标签页）。成功 null，失败返回错误文案。</summary>
        public static string Open(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(url);
                return null;
            }
            catch (Exception ex) { return "打开浏览器失败：" + ex.Message; }
        }

        // ---- 列表条目（内置命令条目，CommandId = web-open，参数在 AppEntry.CommandArg）----

        public static AppEntry BuildUrlEntry(string url)
        {
            var value = (url ?? "").Trim();
            return new AppEntry
            {
                Name = "用浏览器打开",
                Path = "",
                TargetPath = value,   // 副标题展示 URL
                IsCommand = true,
                CommandId = "web-open",
                CommandArg = value,
                IconKey = "Icon.OpenUrl"
            };
        }

        /// <summary>列表末尾的引擎兜底条目：每个启用且 URL 可用的引擎一条，默认引擎排最前。</summary>
        public static List<AppEntry> BuildFallbackEntries(IEnumerable<WebSearchEngine> engines, string defaultName, string query)
        {
            var result = new List<AppEntry>();
            var q = (query ?? "").Trim();
            if (engines == null || q.Length == 0) return result;

            var ordered = new List<WebSearchEngine>();
            foreach (var engine in engines)
            {
                if (engine == null || !engine.Enabled) continue;
                if ((engine.Name ?? "").Trim().Length == 0) continue; // 未完成的引擎行（名称未填）不参与
                string probe, error;
                if (!TryBuildUrl(engine, q, out probe, out error)) continue;
                if (!string.Equals(engine.Name, defaultName, StringComparison.OrdinalIgnoreCase))
                    ordered.Add(engine);
            }
            // 默认引擎排最前（其余保持配置顺序）
            foreach (var engine in engines)
            {
                if (engine == null || !engine.Enabled) continue;
                if ((engine.Name ?? "").Trim().Length == 0) continue;
                if (!string.Equals(engine.Name, defaultName, StringComparison.OrdinalIgnoreCase)) continue;
                string probe, error;
                if (!TryBuildUrl(engine, q, out probe, out error)) continue;
                ordered.Insert(0, engine);
            }

            foreach (var engine in ordered)
            {
                string url, error;
                if (!TryBuildUrl(engine, q, out url, out error)) continue;
                // 副标题提示别名（␣ 字符在中文 UI 字体里会渲染成下划线，一律用文字表述）
                var subtitle = engine.Alias.Length > 0 ? "别名 " + engine.Alias : "";
                if (string.Equals(engine.Name, defaultName, StringComparison.OrdinalIgnoreCase))
                    subtitle = subtitle.Length > 0 ? "默认引擎 · " + subtitle : "默认引擎";
                result.Add(new AppEntry
                {
                    Name = "用 " + engine.Name + " 搜索「" + q + "」",
                    Path = "",
                    TargetPath = subtitle,
                    IsCommand = true,
                    CommandId = "web-open",
                    CommandArg = url,
                    IconKey = "Icon.WebSearch"
                });
            }
            return result;
        }

        // ---- 原始节点取值（宽容）----

        private static string MapString(Dictionary<string, object> map, string key)
        {
            object value;
            if (!map.TryGetValue(key, out value) || value == null) return "";
            return value.ToString().Trim();
        }

        private static bool MapBool(Dictionary<string, object> map, string key, bool fallback)
        {
            object value;
            if (!map.TryGetValue(key, out value) || value == null) return fallback;
            if (value is bool) return (bool)value;
            bool parsed;
            if (bool.TryParse(value.ToString(), out parsed)) return parsed;
            return fallback;
        }
    }
}
