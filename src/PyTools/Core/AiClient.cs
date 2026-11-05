using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace PyTools.Core
{
    /// <summary>
    /// AI 能力共享客户端（M4 AI 批，v1 src/main/ai-adapter.js 语义移植）。
    /// 配置读 exe 旁 ai-local.json（与 v1 同名同字段，部署手工放置、不进打包清单）；
    /// enabled 兼容 v1 的 config.json ai.enabled || ai-local.enabled 合并语义。
    /// 状态原则（2026-11-05 拍板）：配置有效性与服务可达性绝不合并展示——本类不提供
    /// 「服务可用」的预判，可达性只认真实请求的结果；UI 不做常驻徽标（v1 假绿教训）。
    /// 模型列表来自服务端 GET /models（OpenAI 兼容），配置只存当前选中项（model 字段）。
    /// </summary>
    public static class AiClient
    {
        private const string ProviderAllow = "internal-openai-compatible";
        private static readonly Regex ToolChoiceRetryPattern = new Regex("tool_choice|thinking mode", RegexOptions.IgnoreCase);

        /// <summary>供应商不支持 tool_choice 的报错判断（HTTP 错误体与响应体两条路径共用；离线可测）。</summary>
        internal static bool IsToolChoiceError(string errorText)
        {
            return errorText != null && ToolChoiceRetryPattern.IsMatch(errorText);
        }
        private static readonly Regex FencePattern = new Regex("```(?:regex)?\\s*([\\s\\S]*?)```", RegexOptions.IgnoreCase);
        private static readonly Regex JsonFencePattern = new Regex("```(?:json)?\\s*([\\s\\S]*?)```", RegexOptions.IgnoreCase);
        private static readonly Regex AnyFencePattern = new Regex("```[a-zA-Z]*\\s*([\\s\\S]*?)```", RegexOptions.IgnoreCase);
        private static readonly Regex InlinePattern = new Regex("^\\s*(?:regex\\s*)?((?:\\^|\\$|\\[|\\\\|\\())[\\s\\S]+$", RegexOptions.IgnoreCase);
        private static readonly string[] FlagNames = { "g", "i", "m", "s" };

        private const int RequestTimeoutMs = 30000;
        private const int ModelsTimeoutMs = 15000;
        private const int OcrTimeoutMs = 60000; // OCR 图大、推理慢，v1 同口径 60s
        private const int TranslateTimeoutMs = 60000; // 整篇文档翻译可能很长，同 OCR 口径 60s

        private const string SystemPrompt =
            "你是内网里的正则表达式助手。根据用户给出的自然语言目标或样例文本，生成最合适的正则表达式。\n" +
            "当服务支持函数调用时，请调用 regex_generate；否则只输出一个 JSON 对象，不要输出 Markdown、代码块或解释性文字。\n" +
            "JSON 字段:\n" +
            "- pattern: 正则表达式字符串\n" +
            "- flags: 字符串数组，只允许 g、i、m、s\n" +
            "- confidence: 0-100 的整数\n" +
            "- reason: 简短中文说明";

        private static string _dir;

        /// <summary>App 启动时调用一次；selftest 可显式传目录，不依赖本初始化。</summary>
        public static void Initialize(string appDirectory) { _dir = appDirectory; }

        private static string Dir(string explicitDir)
        {
            if (explicitDir != null) return explicitDir;
            if (_dir != null) return _dir;
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        // ---- 配置（v1 aiConfig() 语义）----

        public sealed class AiLocalConfig
        {
            public bool Enabled;          // config.json ai.enabled || ai-local.enabled
            public string Provider = "";
            public string BaseUrl = "";
            public string Model = "";
            public string ApiKey = "";
            public double Temperature = 0.2;
            public double? MaxTokens;
        }

        public sealed class AiStatusInfo
        {
            public bool Available;
            public string Reason = "";    // 不可用原因：未启用 / provider 不支持 / 服务地址缺失 / 模型缺失 / 密钥缺失
        }

        private static Dictionary<string, object> ReadJsonDict(string path)
        {
            try
            {
                var json = File.ReadAllText(path);
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                return serializer.Deserialize<Dictionary<string, object>>(json)
                    ?? new Dictionary<string, object>();
            }
            catch { return new Dictionary<string, object>(); } // 缺文件 / 损坏 → 空（与 v1 一致）
        }

        internal static AiLocalConfig LoadConfig(string dir)
        {
            var basePath = Path.Combine(dir, "config.json");
            var localPath = Path.Combine(dir, "ai-local.json");
            var baseRaw = ReadJsonDict(basePath);
            var aiObj = AsDict(Get(baseRaw, "ai"));
            var local = ReadJsonDict(localPath);

            var cfg = new AiLocalConfig();
            var baseEnabled = aiObj != null && AsBool(Get(aiObj, "enabled")) == true;
            cfg.Enabled = baseEnabled || AsBool(Get(local, "enabled")) == true;

            var provider = Str(Get(aiObj, "provider"));
            if (provider.Length == 0) provider = Str(Get(local, "provider"));
            cfg.Provider = provider;
            cfg.BaseUrl = Str(Get(local, "baseUrl"));
            cfg.Model = Str(Get(local, "model"));
            cfg.ApiKey = Str(Get(local, "apiKey"));
            cfg.Temperature = Num(local, "temperature", 0.2);
            var maxTokens = Get(local, "maxTokens");
            if (maxTokens != null)
            {
                double value;
                if (TryDouble(maxTokens, out value)) cfg.MaxTokens = value;
            }
            return cfg;
        }

        /// <summary>逐项给出不可用原因（v1 status() 语义，全部离线可判）。</summary>
        public static AiStatusInfo Status(string dir = null)
        {
            var cfg = LoadConfig(Dir(dir));
            if (!cfg.Enabled) return new AiStatusInfo { Available = false, Reason = "未启用" };
            if (cfg.Provider != ProviderAllow) return new AiStatusInfo { Available = false, Reason = "provider 不支持" };
            if (cfg.BaseUrl.Length == 0) return new AiStatusInfo { Available = false, Reason = "服务地址缺失" };
            if (cfg.Model.Length == 0) return new AiStatusInfo { Available = false, Reason = "模型缺失" };
            if (cfg.ApiKey.Length == 0) return new AiStatusInfo { Available = false, Reason = "密钥缺失" };
            return new AiStatusInfo { Available = true };
        }

        /// <summary>当前选中模型（ai-local.json 的 model 字段；未配置返回空串）。</summary>
        public static string CurrentModel(string dir = null)
        {
            return LoadConfig(Dir(dir)).Model;
        }

        /// <summary>切换模型：写回 ai-local.json 的 model 字段（未知键保留）。null = 成功，否则错误文案。</summary>
        public static string SelectModel(string modelId, string dir = null)
        {
            var path = Path.Combine(Dir(dir), "ai-local.json");
            try
            {
                var raw = ReadJsonDict(path);
                if (raw.Count == 0 && !File.Exists(path)) return "ai-local.json 不存在";
                raw["model"] = modelId ?? "";
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                File.WriteAllText(path, serializer.Serialize(raw));
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error("select model failed: " + ex.Message);
                return ex.Message;
            }
        }

        // ---- 模型清单（服务端拉取，2026-11-05 拍板）----

        public sealed class ModelListResult
        {
            public List<string> Models = new List<string>();
            public string Error = "";     // 拉取失败原因；空 = 成功（清单可能为空）
        }

        /// <summary>GET {baseUrl}/models（OpenAI 兼容）。失败不抛异常，错误进 Error；不影响生成功能。</summary>
        public static Task<ModelListResult> ListModelsAsync(string dir = null)
        {
            return Task.Run(delegate { return ListModels(dir); });
        }

        public static ModelListResult ListModels(string dir = null)
        {
            var d = Dir(dir);
            var status = Status(d);
            if (!status.Available) return new ModelListResult { Error = status.Reason };
            var cfg = LoadConfig(d);
            try
            {
                var raw = Request(cfg, cfg.BaseUrl, "GET", null, ModelsTimeoutMs);
                return ParseModelList(raw);
            }
            catch (WebException wex) { return new ModelListResult { Error = RequestErrorText(wex, ModelsTimeoutMs) }; }
            catch (Exception ex) { return new ModelListResult { Error = ex.Message }; }
        }

        /// <summary>/models 原始返回 → 清单（离线可测；畸形响应不抛异常）。</summary>
        internal static ModelListResult ParseModelList(string raw)
        {
            var body = ParseJsonDict(raw);
            if (body == null) return new ModelListResult { Error = "AI 返回不是 JSON" };
            var detail = ErrorText(body);
            if (detail.Length > 0) return new ModelListResult { Error = detail };
            var result = new ModelListResult();
            var data = AsArray(Get(body, "data"));
            if (data != null)
            {
                foreach (var item in data)
                {
                    var dict = AsDict(item);
                    var id = dict != null ? Str(Get(dict, "id")) : "";
                    if (id.Length > 0 && !result.Models.Contains(id)) result.Models.Add(id);
                }
            }
            result.Models.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        // ---- 正则生成（v1 generate() 语义）----

        public sealed class AiGenerateResult
        {
            public bool Ok;
            public string Pattern = "";
            public List<string> Flags = new List<string>();
            public int Confidence;
            public string Reason = "";
            public string Model = "";
            public string Error = "";
            public bool RequestFailed;    // 请求类失败（超时/网络/HTTP）→ UI 提示可在设置中更换模型
        }

        public static Task<AiGenerateResult> GenerateRegexAsync(string prompt)
        {
            return Task.Run(delegate { return GenerateRegex(prompt); });
        }

        public static AiGenerateResult GenerateRegex(string prompt)
        {
            return GenerateRegex(prompt, null);
        }

        public static AiGenerateResult GenerateRegex(string prompt, string dir)
        {
            var d = Dir(dir);
            var status = Status(d);
            if (!status.Available) return new AiGenerateResult { Error = status.Reason };
            var text = (prompt ?? "").Trim();
            if (text.Length == 0) return new AiGenerateResult { Error = "请输入自然语言目标或样例文本" };

            var cfg = LoadConfig(d);
            AiGenerateResult parsed;
            try
            {
                string raw;
                try
                {
                    raw = Request(cfg, cfg.BaseUrl, "POST", Serialize(BuildChatPayload(cfg, text, true)), RequestTimeoutMs);
                }
                catch (WebException wex)
                {
                    // HTTP 层拒绝（400 等）同样触发降级：DeepSeek 思考模型不支持 tool_choice 时
                    // 错误体在 Request 里被包装进 WebException.Message，匹配关键词则重试纯 JSON 输出
                    if (!IsToolChoiceError(wex.Message)) throw;
                    raw = Request(cfg, cfg.BaseUrl, "POST", Serialize(BuildChatPayload(cfg, text, false)), RequestTimeoutMs);
                }
                parsed = ParseResponseBody(raw);
                // DeepSeek 思考模型暂不支持强制 tool_choice：报错含关键词时自动降级为纯 JSON 输出重试一次
                //（部分网关把 400 包装成 200 返回，错误落在响应体里）
                if (parsed.Error.Length > 0 && IsToolChoiceError(parsed.Error))
                {
                    var retryRaw = Request(cfg, cfg.BaseUrl, "POST", Serialize(BuildChatPayload(cfg, text, false)), RequestTimeoutMs);
                    parsed = ParseResponseBody(retryRaw);
                }
            }
            catch (WebException wex)
            {
                return new AiGenerateResult { Error = RequestErrorText(wex, RequestTimeoutMs), RequestFailed = true };
            }
            catch (Exception ex)
            {
                return new AiGenerateResult { Error = ex.Message, RequestFailed = true };
            }

            if (parsed.Error.Length > 0 || parsed.Pattern.Length == 0)
            {
                parsed.Error = parsed.Error.Length > 0 ? parsed.Error : "AI 返回格式无效";
                return parsed;
            }
            parsed.Ok = true;
            parsed.Model = cfg.Model;
            return parsed;
        }

        /// <summary>chat/completions 请求体（v1 buildPayload 语义；tools/tool_choice 可关）。</summary>
        internal static Dictionary<string, object> BuildChatPayload(AiLocalConfig cfg, string prompt, bool useTools)
        {
            var payload = new Dictionary<string, object>
            {
                {
                    "messages", new object[]
                    {
                        new Dictionary<string, object> { { "role", "system" }, { "content", SystemPrompt } },
                        new Dictionary<string, object> { { "role", "user" }, { "content", prompt } },
                    }
                },
                { "temperature", cfg.Temperature },
            };
            if (cfg.Model.Length > 0) payload["model"] = cfg.Model;
            if (cfg.MaxTokens.HasValue) payload["max_tokens"] = cfg.MaxTokens.Value;
            if (useTools)
            {
                payload["tools"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        { "type", "function" },
                        {
                            "function", new Dictionary<string, object>
                            {
                                { "name", "regex_generate" },
                                { "description", "返回可直接编译的正则表达式及其简要说明" },
                                {
                                    "parameters", new Dictionary<string, object>
                                    {
                                        { "type", "object" },
                                        {
                                            "properties", new Dictionary<string, object>
                                            {
                                                { "pattern", new Dictionary<string, object> { { "type", "string" }, { "description", "正则表达式字符串" } } },
                                                {
                                                    "flags", new Dictionary<string, object>
                                                    {
                                                        { "type", "array" },
                                                        { "items", new Dictionary<string, object> { { "type", "string" }, { "enum", new[] { "g", "i", "m", "s" } } } },
                                                    }
                                                },
                                                { "confidence", new Dictionary<string, object> { { "type", "integer" }, { "minimum", 0 }, { "maximum", 100 } } },
                                                { "reason", new Dictionary<string, object> { { "type", "string" }, { "description", "简短中文说明" } } },
                                            }
                                        },
                                        { "required", new[] { "pattern" } },
                                    }
                                },
                            }
                        },
                    }
                };
                payload["tool_choice"] = new Dictionary<string, object>
                {
                    { "type", "function" },
                    { "function", new Dictionary<string, object> { { "name", "regex_generate" } } },
                };
            }
            return payload;
        }

        // ---- 图片文字识别（v1 ai-ocr 语义，M4 AI 批第二个消费者）----

        /// <summary>
        /// OCR 专用提示词（v1 OCR_PROMPT 语义 + v2 纯文本兜底口径）：
        /// 要求只回 JSON {text,error}，但模型直接吐纯文本时调用侧也接受（拍板 #1）。
        /// </summary>
        private const string OcrPrompt =
            "你是内网里的图片文字识别助手。请逐字识别用户图片中的全部文字，保持原有换行与表格对齐（用空格对齐列）。\n" +
            "只输出一个 JSON 对象，不要输出 Markdown、代码块或解释性文字。JSON 字段:\n" +
            "- text: 识别到的全部文字（原样保留换行）\n" +
            "- error: 识别失败时的简短中文原因；成功留空";

        public sealed class AiOcrResult
        {
            public bool Ok;
            public string Text = "";
            public string Model = "";
            public string Error = "";
            public bool RequestFailed;    // 请求类失败（超时/网络/HTTP）→ UI 提示可在设置中更换模型
        }

        public static Task<AiOcrResult> OcrAsync(string dataUrl)
        {
            return Task.Run(delegate { return Ocr(dataUrl); });
        }

        public static AiOcrResult Ocr(string dataUrl, string dir = null)
        {
            var d = Dir(dir);
            var status = Status(d);
            if (!status.Available) return new AiOcrResult { Error = status.Reason };
            if (string.IsNullOrEmpty(dataUrl)) return new AiOcrResult { Error = "没有可识别的图片" };

            var cfg = LoadConfig(d);
            AiOcrResult parsed;
            try
            {
                var raw = Request(cfg, cfg.BaseUrl, "POST", Serialize(BuildOcrPayload(cfg, dataUrl)), OcrTimeoutMs);
                parsed = ParseOcrResponse(raw);
            }
            catch (WebException wex)
            {
                return new AiOcrResult { Error = RequestErrorText(wex, OcrTimeoutMs), RequestFailed = true };
            }
            catch (Exception ex)
            {
                return new AiOcrResult { Error = ex.Message, RequestFailed = true };
            }

            if (parsed.Error.Length > 0) return parsed;
            parsed.Ok = true;
            parsed.Model = cfg.Model;
            return parsed;
        }

        /// <summary>OCR 请求体：图文混合 content 数组（离线可测；不含 tools）。</summary>
        internal static Dictionary<string, object> BuildOcrPayload(AiLocalConfig cfg, string dataUrl)
        {
            var userContent = new object[]
            {
                new Dictionary<string, object> { { "type", "text" }, { "text", "请识别这张图片里的全部文字。" } },
                new Dictionary<string, object>
                {
                    { "type", "image_url" },
                    { "image_url", new Dictionary<string, object> { { "url", dataUrl } } },
                },
            };
            var payload = new Dictionary<string, object>
            {
                {
                    "messages", new object[]
                    {
                        new Dictionary<string, object> { { "role", "system" }, { "content", OcrPrompt } },
                        new Dictionary<string, object> { { "role", "user" }, { "content", userContent } },
                    }
                },
                { "temperature", 0 }, // 识别要稳定复现，不引入采样随机性
            };
            if (cfg.Model.Length > 0) payload["model"] = cfg.Model;
            if (cfg.MaxTokens.HasValue) payload["max_tokens"] = cfg.MaxTokens.Value;
            return payload;
        }

        /// <summary>OCR 响应解析（离线可测）：直返 text → 服务端 error → content JSON → 纯文本兜底。</summary>
        public static AiOcrResult ParseOcrResponse(string raw)
        {
            var body = ParseJsonDict(raw);
            if (body == null)
            {
                var plainRaw = StripCodeFence((raw ?? "").Trim(), AnyFencePattern).Trim();
                return plainRaw.Length > 0 ? new AiOcrResult { Text = plainRaw } : new AiOcrResult { Error = "AI 返回为空" };
            }

            var detail = ErrorText(body);
            if (detail.Length > 0) return new AiOcrResult { Error = detail };

            var direct = Str(Get(body, "text"));
            if (direct.Trim().Length > 0) return new AiOcrResult { Text = direct.Trim() };

            var choices = AsArray(Get(body, "choices"));
            var choice = choices != null && choices.Length > 0 ? AsDict(choices[0]) : null;
            var message = choice != null ? AsDict(Get(choice, "message")) : null;
            var content = message != null ? Str(Get(message, "content")) : "";
            if (content.Trim().Length == 0) return new AiOcrResult { Error = "AI 返回内容为空" };

            var parsed = ParseJsonDict(StripCodeFence(content, JsonFencePattern)) ?? ParseJsonDict(ExtractJsonObject(content));
            if (parsed != null)
            {
                var text = Str(Get(parsed, "text")).Trim();
                if (text.Length > 0) return new AiOcrResult { Text = text };
                var reason = Str(Get(parsed, "error")).Trim();
                return new AiOcrResult { Error = reason.Length > 0 ? reason : "AI 未识别出文字" };
            }

            // 纯文本兜底：模型忽略了 JSON 要求但正文有效，白跑一次请求太亏（拍板 #1）
            var plain = StripCodeFence(content, AnyFencePattern).Trim();
            return plain.Length > 0 ? new AiOcrResult { Text = plain } : new AiOcrResult { Error = "AI 未识别出文字" };
        }

        // ---- 翻译（v1 ai-translate 语义 + v2 单次请求口径，M4 AI 批第三个消费者）----

        /// <summary>
        /// 翻译提示词：v1 口径 + 三条 v2 增强（保留换行段落、代码/专有名词原样、
        /// 源语言未知时自判并回 detectedLang，省掉 v1 的独立 detect 请求）。
        /// </summary>
        private const string TranslatePrompt =
            "你是内网里的翻译助手。用户会给出 sourceLang、targetLang 和 text，请把 text 翻译成 targetLang。\n" +
            "- sourceLang 为空表示原文语种未知，请自行判断\n" +
            "- 保留原文的换行与段落结构\n" +
            "- 代码片段、变量名、命令、URL、专有名词原样保留，不翻译\n" +
            "只输出一个 JSON 对象，不要输出 Markdown、代码块或解释性文字。JSON 字段:\n" +
            "- translation: 译文文本（保留换行）\n" +
            "- detectedLang: 原文语种，取 zh-CN、en、ja 之一；无法判断时留空\n" +
            "- error: 翻译失败时的简短中文原因；成功留空";

        public sealed class AiTranslateResult
        {
            public bool Ok;
            public string Translation = "";
            public string DetectedLang = "";   // 源语言自动时模型自判结果（zh-CN / en / ja，可能为空）
            public string Model = "";
            public string Error = "";
            public bool RequestFailed;         // 请求类失败（超时/网络/HTTP）→ UI 提示可在设置中更换模型
        }

        public static Task<AiTranslateResult> TranslateAsync(string sourceLang, string targetLang, string text)
        {
            return Task.Run(delegate { return Translate(sourceLang, targetLang, text); });
        }

        public static AiTranslateResult Translate(string sourceLang, string targetLang, string text, string dir = null)
        {
            var d = Dir(dir);
            var status = Status(d);
            if (!status.Available) return new AiTranslateResult { Error = status.Reason };
            if (string.IsNullOrWhiteSpace(text)) return new AiTranslateResult { Error = "请输入要翻译的文本" };
            if (string.IsNullOrWhiteSpace(targetLang)) return new AiTranslateResult { Error = "请选择目标语言" };

            var cfg = LoadConfig(d);
            AiTranslateResult parsed;
            try
            {
                var raw = Request(cfg, cfg.BaseUrl, "POST",
                    Serialize(BuildTranslatePayload(cfg, sourceLang, targetLang, text)), TranslateTimeoutMs);
                parsed = ParseTranslateResponse(raw);
            }
            catch (WebException wex)
            {
                return new AiTranslateResult { Error = RequestErrorText(wex, TranslateTimeoutMs), RequestFailed = true };
            }
            catch (Exception ex)
            {
                return new AiTranslateResult { Error = ex.Message, RequestFailed = true };
            }

            if (parsed.Error.Length > 0) return parsed;
            parsed.Ok = true;
            parsed.Model = cfg.Model;
            return parsed;
        }

        /// <summary>翻译请求体：纯文本 content（离线可测；不含 tools）。</summary>
        internal static Dictionary<string, object> BuildTranslatePayload(AiLocalConfig cfg, string sourceLang,
            string targetLang, string text)
        {
            var user = new Dictionary<string, object>
            {
                { "sourceLang", sourceLang ?? "" },
                { "targetLang", targetLang ?? "" },
                { "text", text ?? "" },
            };
            var payload = new Dictionary<string, object>
            {
                {
                    "messages", new object[]
                    {
                        new Dictionary<string, object> { { "role", "system" }, { "content", TranslatePrompt } },
                        new Dictionary<string, object> { { "role", "user" }, { "content", Serialize(user) } },
                    }
                },
                { "temperature", 0 }, // 翻译要稳定复现，同一句不应每次不同
            };
            if (cfg.Model.Length > 0) payload["model"] = cfg.Model;
            if (cfg.MaxTokens.HasValue) payload["max_tokens"] = cfg.MaxTokens.Value;
            return payload;
        }

        /// <summary>翻译响应解析（离线可测）：直返 translation → 服务端 error → content JSON → 纯文本兜底。</summary>
        public static AiTranslateResult ParseTranslateResponse(string raw)
        {
            var body = ParseJsonDict(raw);
            if (body == null)
            {
                var plainRaw = StripCodeFence((raw ?? "").Trim(), AnyFencePattern).Trim();
                return plainRaw.Length > 0
                    ? new AiTranslateResult { Translation = plainRaw }
                    : new AiTranslateResult { Error = "AI 返回为空" };
            }

            var detail = ErrorText(body);
            if (detail.Length > 0) return new AiTranslateResult { Error = detail };

            var direct = Str(Get(body, "translation"));
            if (direct.Trim().Length > 0)
                return new AiTranslateResult { Translation = direct.Trim(), DetectedLang = Str(Get(body, "detectedLang")).Trim() };

            var choices = AsArray(Get(body, "choices"));
            var choice = choices != null && choices.Length > 0 ? AsDict(choices[0]) : null;
            var message = choice != null ? AsDict(Get(choice, "message")) : null;
            var content = message != null ? Str(Get(message, "content")) : "";
            if (content.Trim().Length == 0) return new AiTranslateResult { Error = "AI 返回内容为空" };

            var parsed = ParseJsonDict(StripCodeFence(content, JsonFencePattern)) ?? ParseJsonDict(ExtractJsonObject(content));
            if (parsed != null)
            {
                var translation = Str(Get(parsed, "translation")).Trim();
                if (translation.Length > 0)
                    return new AiTranslateResult { Translation = translation, DetectedLang = Str(Get(parsed, "detectedLang")).Trim() };
                var reason = Str(Get(parsed, "error")).Trim();
                return new AiTranslateResult { Error = reason.Length > 0 ? reason : "AI 未返回译文" };
            }

            // 纯文本兜底：模型忽略 JSON 要求但正文是有效译文，白跑一次请求太亏（与 OCR 同口径）
            var plain = StripCodeFence(content, AnyFencePattern).Trim();
            return plain.Length > 0
                ? new AiTranslateResult { Translation = plain }
                : new AiTranslateResult { Error = "AI 未返回译文" };
        }

        // ---- 响应解析（v1 parseResponseBody 语义，全部离线）----

        /// <summary>解析 chat/completions 原始返回。失败时 Error 非空、Pattern 空。</summary>
        public static AiGenerateResult ParseResponseBody(string raw)
        {
            var body = ParseJsonDict(raw);
            if (body == null) return new AiGenerateResult { Error = "AI 返回不是 JSON" };

            var directPattern = Str(Get(body, "pattern"));
            if (directPattern.Length > 0) return FromParsed(directPattern, Get(body, "flags"), Get(body, "confidence"), Get(body, "reason"));

            var detail = ErrorText(body);
            if (detail.Length > 0) return new AiGenerateResult { Error = detail };

            var choices = AsArray(Get(body, "choices"));
            var choice = choices != null && choices.Length > 0 ? AsDict(choices[0]) : null;
            var message = choice != null ? AsDict(Get(choice, "message")) : null;
            if (message != null)
            {
                var calls = AsArray(Get(message, "tool_calls"));
                if (calls != null)
                {
                    foreach (var call in calls)
                    {
                        var function = AsDict(Get(AsDict(call), "function"));
                        if (function == null) continue;
                        var args = UnwrapArguments(Str(Get(function, "arguments")));
                        var pattern = args != null ? Str(Get(args, "pattern")).Trim() : "";
                        if (pattern.Length > 0)
                            return FromParsed(pattern, Get(args, "flags"), Get(args, "confidence"), Get(args, "reason"));
                    }
                }
                var content = Str(Get(message, "content"));
                if (content.Length > 0)
                {
                    var parsed = ParseContent(content);
                    if (parsed != null && parsed.Pattern.Length > 0) return parsed;
                }
            }
            return new AiGenerateResult { Error = "AI 返回内容缺少 pattern" };
        }

        private static AiGenerateResult FromParsed(object pattern, object flags, object confidence, object reason)
        {
            var text = Str(pattern).Trim();
            return new AiGenerateResult
            {
                Pattern = text,
                Flags = FilterFlags(flags),
                Confidence = ToInt(confidence),
                Reason = Str(reason).Trim(),
                Error = text.Length > 0 ? "" : "AI 返回内容缺少 pattern",
            };
        }

        /// <summary>content 三层兜底（v1 parseContent）：JSON → ```regex 围栏 → 行内正则。</summary>
        public static AiGenerateResult ParseContent(string content)
        {
            var text = content ?? "";
            var parsed = ParseJsonDict(StripCodeFence(text, JsonFencePattern)) ?? ParseJsonDict(ExtractJsonObject(text));
            if (parsed != null)
            {
                var pattern = Str(Get(parsed, "pattern"));
                if (pattern.Length > 0)
                    return FromParsed(pattern.Trim(), Get(parsed, "flags"), Get(parsed, "confidence"), Get(parsed, "reason"));
            }

            var fence = FencePattern.Match(text);
            if (fence.Success && fence.Groups[1].Value.Trim().Length > 0)
            {
                var lines = fence.Groups[1].Value.Trim().Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                var start = lines[0].Trim().ToLowerInvariant() == "regex" ? 1 : 0;
                return new AiGenerateResult { Pattern = start < lines.Length ? lines[start] : "" };
            }

            var inline = InlinePattern.Match(text);
            if (inline.Success)
                return new AiGenerateResult { Pattern = text.Substring(inline.Groups[1].Index).Trim() };
            return null;
        }

        /// <summary>剥 ```json 围栏后整体解析；失败再取首尾大括号之间的片段。</summary>
        public static string ExtractJsonObject(string text)
        {
            var value = text ?? "";
            var start = value.IndexOf('{');
            if (start < 0) return null;
            var end = value.LastIndexOf('}');
            if (end <= start) return null;
            return value.Substring(start, end - start + 1);
        }

        public static string StripCodeFence(string text, Regex pattern)
        {
            var value = text ?? "";
            var match = pattern.Match(value);
            return match.Success ? match.Groups[1].Value : value;
        }

        private static Dictionary<string, object> UnwrapArguments(string text)
        {
            var value = text ?? "";
            return ParseJsonDict(StripCodeFence(value, JsonFencePattern)) ?? ParseJsonDict(ExtractJsonObject(value));
        }

        private static List<string> FilterFlags(object flags)
        {
            var result = new List<string>();
            var array = AsArray(flags);
            if (array == null) return result;
            foreach (var item in array)
            {
                var flag = Str(item);
                if (FlagNames.Contains(flag) && !result.Contains(flag)) result.Add(flag);
            }
            return result;
        }

        // ---- HTTP（HttpWebRequest，零第三方；TLS 1.2 显式开启）----

        private static string Request(AiLocalConfig cfg, string baseUrl, string method, string jsonBody, int timeoutMs)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; // .NET 4.6.2 默认链路可能不含
            var url = TrimSlash(baseUrl) + (method == "GET" ? "/models" : "/chat/completions");
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = method;
            request.ContentType = "application/json";
            request.Headers.Add("Authorization", "Bearer " + cfg.ApiKey);
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;

            if (jsonBody != null)
            {
                var bytes = Encoding.UTF8.GetBytes(jsonBody);
                using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
            }
            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    return reader.ReadToEnd();
            }
            catch (WebException wex)
            {
                var response = wex.Response as HttpWebResponse;
                if (response != null)
                {
                    string body;
                    using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) body = reader.ReadToEnd();
                    var detail = ErrorText(ParseJsonDict(body));
                    throw new WebException(detail.Length > 0 ? detail : "HTTP " + (int)response.StatusCode, wex.Status);
                }
                throw;
            }
        }

        private static string RequestErrorText(WebException wex, int timeoutMs)
        {
            if (wex.Status == WebExceptionStatus.Timeout) return "连接超时（" + (timeoutMs / 1000) + "s）";
            if (wex.Status == WebExceptionStatus.NameResolutionFailure) return "无法解析服务地址";
            if (wex.Status == WebExceptionStatus.ConnectFailure) return "无法连接服务";
            return wex.Message.Length > 0 ? wex.Message : wex.Status.ToString();
        }

        private static string TrimSlash(string url)
        {
            return (url ?? "").TrimEnd('/');
        }

        private static string Serialize(Dictionary<string, object> payload)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            return serializer.Serialize(payload);
        }

        // ---- JSON 取值辅助（JavaScriptSerializer 反序列化的宽容读取）----

        private static object Get(Dictionary<string, object> dict, string key)
        {
            object value;
            return dict != null && dict.TryGetValue(key, out value) ? value : null;
        }

        private static Dictionary<string, object> AsDict(object value)
        {
            return value as Dictionary<string, object>;
        }

        /// <summary>JavaScriptSerializer 反序列化 JSON 数组：.NET 4.x 实测给 ArrayList（非 object[]/List），宽容取。</summary>
        private static object[] AsArray(object value)
        {
            if (value is object[]) return (object[])value;
            var list = value as System.Collections.ArrayList;
            return list != null ? list.ToArray() : null;
        }

        private static string Str(object value)
        {
            return value == null ? "" : value.ToString();
        }

        private static bool? AsBool(object value)
        {
            if (value is bool) return (bool)value;
            bool parsed;
            return bool.TryParse(Str(value), out parsed) ? parsed : (bool?)null;
        }

        private static double Num(Dictionary<string, object> dict, string key, double fallback)
        {
            double value;
            return TryDouble(Get(dict, key), out value) ? value : fallback;
        }

        private static bool TryDouble(object value, out double result)
        {
            try { result = Convert.ToDouble(value); return true; }
            catch { result = 0; return false; }
        }

        private static int ToInt(object value)
        {
            double number;
            if (!TryDouble(value, out number) || number < 0) return 0;
            return (int)Math.Min(number, 100);
        }

        private static Dictionary<string, object> ParseJsonDict(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                return serializer.Deserialize<Dictionary<string, object>>(text);
            }
            catch { return null; }
        }

        /// <summary>服务端错误信息提取（v1 serverError / body.error 语义）。</summary>
        private static string ErrorText(Dictionary<string, object> body)
        {
            var error = AsDict(Get(body, "error"));
            if (error == null) return "";
            var detail = Str(Get(error, "message"));
            if (detail.Length == 0) detail = Str(Get(error, "code"));
            return detail.Length > 0 ? detail.Trim() : "AI 返回错误";
        }
    }
}
