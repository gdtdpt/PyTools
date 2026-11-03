using System;
using System.Collections.Generic;
using System.Linq;

namespace PyTools.Core
{
    /// <summary>字段定义（六段：秒 分 时 日 月 周）。</summary>
    public sealed class CronFieldDef
    {
        public string Key;
        public string Label;
        public int Min;
        public int Max;
        public CronFieldDef(string key, string label, int min, int max)
        {
            Key = key; Label = label; Min = min; Max = max;
        }
    }

    /// <summary>单个取值项：* / a / a-b / 任意前三者加 /step。</summary>
    public sealed class CronSegment
    {
        public int From;
        public int To;
        public int Step;
        public bool IsStar;
        public bool StartOnly; // 「5/10」= 从 5 起每 10 直到上限
    }

    /// <summary>解析后的单字段。</summary>
    public sealed class CronField
    {
        public string Key;
        public string Label;
        public string Raw;
        public bool IsWild;
        public List<CronSegment> Segments;
        public List<int> Values;
        public bool[] Mask;
        public string Description;
    }

    public sealed class CronFieldError
    {
        public string FieldKey; // null = 与具体字段无关（段数/宏）
        public string Label;
        public string Raw;
        public string Message;
    }

    public sealed class CronParseResult
    {
        public bool Ok;
        public CronFieldError Error;
        public List<CronFieldError> Errors;
        public List<CronField> Fields; // @reboot 时为 null
        public string Summary;
        public bool OrRule;
        public string Note;
        public string Source;
        public bool IsMacro;
    }

    /// <summary>构造器单行状态（模式 + 参数）。</summary>
    public sealed class CronBuilderState
    {
        public string Mode = "every";
        public string Step = "";
        public List<string> Values;
        public string From;
        public string To;
        public string RangeStep;
        public string Text;
    }

    /// <summary>构造器/解析抛出的可读错误（消息直接给用户看）。</summary>
    public sealed class CronException : Exception
    {
        public CronException(string message) : base(message) { }
    }

    public sealed class CronRunResult
    {
        public bool Ok;
        public CronFieldError Error;
        public List<DateTime> Runs = new List<DateTime>();
        public bool Truncated;
        public string Note;
    }

    /// <summary>
    /// Cron 表达式解析与触发时间推算（M4，自 v1 tools/cron/cron-core.js 移植）。
    /// 纯逻辑不碰 UI；六段式 + 5 段 crontab 兼容 + 宏；日/周同限「或」规则；
    /// 推算上限 4 年（v1 同参数），防死循环步数上限与 v1 一致。
    /// </summary>
    public static class CronExpr
    {
        public static readonly CronFieldDef[] Fields =
        {
            new CronFieldDef("second", "秒", 0, 59),
            new CronFieldDef("minute", "分", 0, 59),
            new CronFieldDef("hour", "时", 0, 23),
            new CronFieldDef("day", "日", 1, 31),
            new CronFieldDef("month", "月", 1, 12),
            new CronFieldDef("week", "周", 0, 7),
        };

        // 段描述用的单位词：week 的「天」指星期内的天
        private static readonly Dictionary<string, string> Unit = new Dictionary<string, string>
        {
            { "second", "秒" }, { "minute", "分钟" }, { "hour", "小时" },
            { "day", "天" }, { "month", "个月" }, { "week", "天" },
        };

        private static readonly string[] WeekLabels = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };

        private static readonly Dictionary<string, string> Macros = new Dictionary<string, string>
        {
            { "@yearly", "0 0 0 1 1 *" },
            { "@annually", "0 0 0 1 1 *" },
            { "@monthly", "0 0 0 1 * *" },
            { "@weekly", "0 0 0 * * 0" },
            { "@daily", "0 0 0 * * *" },
            { "@midnight", "0 0 0 * * *" },
            { "@hourly", "0 0 * * * *" },
        };

        private static readonly TimeSpan SearchLimit = TimeSpan.FromDays(4 * 366);
        private const int MaxSteps = 2000000;

        private static CronFieldDef DefOf(string key)
        {
            foreach (var def in Fields)
                if (def.Key == key) return def;
            throw new CronException("未知字段: " + key);
        }

        private static string Pad(int value)
        {
            return value.ToString("00");
        }

        // ---- 单项解析 ----

        private static CronSegment ParseItem(CronFieldDef def, string item)
        {
            var parts = item.Split('/');
            if (parts.Length > 2) throw new CronException("无法识别的写法「" + item + "」");
            var body = parts[0];
            var stepText = parts.Length > 1 ? parts[1] : null;
            var step = 1;
            if (stepText != null)
            {
                if (!IsDigits(stepText)) throw new CronException("步进「" + stepText + "」应为正整数");
                step = int.Parse(stepText);
                if (step < 1 || step > def.Max) throw new CronException("步进「" + stepText + "」超出范围 1-" + def.Max);
            }

            int from, to;
            var isStar = false;
            if (body == "*")
            {
                isStar = true;
                from = def.Min;
                to = def.Max;
            }
            else if (IsRange(body))
            {
                var bounds = body.Split('-');
                from = int.Parse(bounds[0]);
                to = int.Parse(bounds[1]);
                if (from > to) throw new CronException("区间「" + body + "」起点大于终点");
            }
            else if (IsDigits(body))
            {
                from = int.Parse(body);
                to = from;
            }
            else
            {
                throw new CronException("无法识别的写法「" + item + "」");
            }
            if (from < def.Min || to > def.Max)
                throw new CronException("取值 " + item + " 超出范围 " + def.Min + "-" + def.Max);

            var startOnly = !isStar && IsDigits(body) && step > 1; // 「5/10」= 从 5 起每 10 直到上限
            if (startOnly) to = def.Max;
            return new CronSegment { From = from, To = to, Step = step, IsStar = isStar, StartOnly = startOnly };
        }

        private static bool IsDigits(string text)
        {
            if (text.Length == 0) return false;
            foreach (var ch in text)
                if (ch < '0' || ch > '9') return false;
            return true;
        }

        private static bool IsRange(string text)
        {
            var parts = text.Split('-');
            return parts.Length == 2 && IsDigits(parts[0]) && IsDigits(parts[1]);
        }

        private static CronField ParseField(CronFieldDef def, string rawText)
        {
            var raw = (rawText ?? "").Trim();
            if (raw.Length == 0) throw new CronException("该段为空");
            var text = raw == "?" ? "*" : raw; // 「?」按不限处理，兼容 Quartz 写法
            var mask = new bool[def.Max + 1];
            var segments = new List<CronSegment>();

            foreach (var item in text.Split(','))
            {
                if (item.Length == 0) throw new CronException("存在空的取值项");
                var segment = ParseItem(def, item);
                for (var value = segment.From; value <= segment.To; value += segment.Step) mask[value] = true;
                segments.Add(segment);
            }

            var values = new List<int>();
            for (var value = def.Min; value <= def.Max; value++)
                if (mask[value]) values.Add(value);
            if (values.Count == 0) throw new CronException("该段没有任何有效取值");

            if (def.Key == "week")
            {
                // 周字段 0 与 7 都是周日：匹配时都要命中，展示与构造器统一归到 0
                values = values.Select(v => v == 7 ? 0 : v).Distinct().OrderBy(v => v).ToList();
            }

            return new CronField
            {
                Key = def.Key,
                Label = def.Label,
                Raw = raw,
                IsWild = text == "*",
                Segments = segments,
                Values = values,
                Mask = mask,
            };
        }

        // ---- 中文描述 ----

        private static string CoreValue(string key, int value)
        {
            return key == "week" ? WeekLabels[value == 7 ? 0 : value] : value.ToString();
        }

        private static string UnitSuffix(string key)
        {
            switch (key)
            {
                case "second": return " 秒";
                case "minute": return " 分";
                case "hour": return " 点";
                case "day": return " 号";
                case "month": return " 月";
                default: return "";
            }
        }

        private static string ListPhrase(string key, List<int> values)
        {
            if (key == "week") return string.Join("、", values.Select(v => WeekLabels[v == 7 ? 0 : v]));
            var joined = string.Join("、", values.Select(v => v.ToString()));
            var prefix = key == "second" || key == "minute" ? "第 " : "";
            return prefix + joined + UnitSuffix(key);
        }

        private static string RangePhrase(string key, CronSegment segment)
        {
            var step = segment.Step > 1 ? "每 " + segment.Step + " " + Unit[key] : "";
            if (key == "week")
            {
                var from = WeekLabels[segment.From == 7 ? 0 : segment.From];
                var to = WeekLabels[segment.To == 7 ? 0 : segment.To];
                return from + "至" + to + step;
            }
            return CoreValue(key, segment.From) + " 至 " + CoreValue(key, segment.To) + UnitSuffix(key) + step;
        }

        private static string DescribeSegment(string key, CronSegment segment)
        {
            if (segment.IsStar) return "每 " + segment.Step + " " + Unit[key];
            if (segment.StartOnly) return "从 " + CoreValue(key, segment.From) + " 起每 " + segment.Step + " " + Unit[key];
            if (segment.From != segment.To) return RangePhrase(key, segment);
            return ListPhrase(key, new List<int> { segment.From });
        }

        public static string DescribeField(CronField field)
        {
            if (field.IsWild)
            {
                switch (field.Key)
                {
                    case "second": return "每秒";
                    case "minute": return "每分钟";
                    case "hour": return "每小时";
                    case "day": return "每天";
                    case "month": return "每月";
                    default: return "每天";
                }
            }
            var segments = field.Segments;
            if (segments.Count > 1 && segments.All(s => s.From == s.To))
                return ListPhrase(field.Key, field.Values);
            if (segments.Count == 1) return DescribeSegment(field.Key, segments[0]);
            return string.Join("、", segments.Select(s => DescribeSegment(field.Key, s)));
        }

        private static string DayPhrase(CronField day, CronField month)
        {
            var yearly = month.IsWild ? "" : "每年 " + DescribeField(month);
            var prefix = yearly.Length > 0 ? yearly : "每月";
            var segment = day.Segments.Count == 1 ? day.Segments[0] : null;

            if (segment != null && segment.IsStar)
                return yearly.Length > 0 ? yearly + "的每 " + segment.Step + " 天" : "每 " + segment.Step + " 天";
            if (segment != null && segment.StartOnly) return prefix + " " + segment.From + " 号起每 " + segment.Step + " 天";
            if (segment != null && segment.From != segment.To)
            {
                var step = segment.Step > 1 ? "每 " + segment.Step + " 天" : "";
                return prefix + " " + segment.From + " 至 " + segment.To + " 号" + step;
            }
            if (segment != null) return prefix + " " + segment.From + " 号";
            return prefix + " " + ListPhrase("day", day.Values);
        }

        private static string WeekPhrase(CronField week)
        {
            if (week.IsWild || week.Values.Count == 7) return "每天";
            var segments = week.Segments;
            if (segments.Count == 1)
            {
                var segment = segments[0];
                if (!segment.IsStar && segment.From != segment.To && segment.Step == 1)
                    return "每" + WeekLabels[segment.From] + "至" + WeekLabels[segment.To == 7 ? 0 : segment.To];
            }
            if (segments.All(s => s.From == s.To)) return "每" + ListPhrase("week", week.Values);
            return "每" + DescribeField(week);
        }

        private static List<int[]> Combinations(CronField second, CronField minute, CronField hour)
        {
            var result = new List<int[]>();
            foreach (var h in hour.Values)
                foreach (var m in minute.Values)
                    foreach (var s in second.Values)
                        result.Add(new[] { h, m, s });
            return result;
        }

        private static void Summarize(List<CronField> fields, out string summary, out bool orRule)
        {
            var byKey = fields.ToDictionary(f => f.Key);
            var day = byKey["day"];
            var week = byKey["week"];
            var month = byKey["month"];
            var dayActive = !day.IsWild;
            var weekActive = !week.IsWild;

            string datePhrase;
            if (dayActive && weekActive) datePhrase = DayPhrase(day, month) + "或" + WeekPhrase(week);
            else if (weekActive) datePhrase = WeekPhrase(week);
            else if (dayActive) datePhrase = DayPhrase(day, month);
            else if (!month.IsWild) datePhrase = "每年 " + DescribeField(month);
            else datePhrase = "每天";

            var second = byKey["second"];
            var minute = byKey["minute"];
            var hour = byKey["hour"];
            var product = second.Values.Count * minute.Values.Count * hour.Values.Count;
            string timePhrase;
            var enumerated = false;
            if (product <= 12)
            {
                enumerated = true;
                timePhrase = string.Join("、", Combinations(second, minute, hour)
                    .Select(c => Pad(c[0]) + ":" + Pad(c[1]) + ":" + Pad(c[2])));
            }
            else
            {
                var parts = new List<string>();
                if (!hour.IsWild) parts.Add(DescribeField(hour));
                if (!minute.IsWild) parts.Add(DescribeField(minute));
                // 秒固定为 0 时是「每分钟/每小时」这类写法的隐含前提，不再单独念出来
                var secondFixedZero = second.Values.Count == 1 && second.Values[0] == 0;
                if (!second.IsWild && !secondFixedZero) parts.Add(DescribeField(second));
                if (parts.Count > 0) timePhrase = string.Join("，", parts);
                else if (secondFixedZero) timePhrase = "每分钟";
                else timePhrase = "每秒";
            }
            summary = datePhrase + (enumerated ? "的 " : "，") + timePhrase;
            orRule = dayActive && weekActive;
        }

        // ---- 整体解析 ----

        private static CronParseResult ParseFieldsParts(string[] parts)
        {
            var fields = new List<CronField>();
            var errors = new List<CronFieldError>();
            for (var index = 0; index < Fields.Length; index++)
            {
                var def = Fields[index];
                try
                {
                    fields.Add(ParseField(def, parts[index]));
                }
                catch (CronException ex)
                {
                    errors.Add(new CronFieldError
                    {
                        FieldKey = def.Key,
                        Label = def.Label,
                        Raw = parts[index] ?? "",
                        Message = ex.Message,
                    });
                    fields.Add(null);
                }
            }
            if (errors.Count > 0)
                return new CronParseResult { Ok = false, Error = errors[0], Errors = errors };

            foreach (var field in fields) field.Description = DescribeField(field);
            string summary;
            bool orRule;
            Summarize(fields, out summary, out orRule);
            return new CronParseResult { Ok = true, Fields = fields, Summary = summary, OrRule = orRule };
        }

        public static CronParseResult Parse(string rawText)
        {
            var text = (rawText ?? "").Trim();
            if (text.Length == 0)
                return new CronParseResult { Ok = false, Error = new CronFieldError { FieldKey = null, Message = "请输入 Cron 表达式" } };

            if (text.StartsWith("@", StringComparison.Ordinal))
            {
                var macro = text.ToLowerInvariant();
                if (macro == "@reboot")
                {
                    return new CronParseResult
                    {
                        Ok = true,
                        IsMacro = true,
                        Source = text,
                        Fields = null,
                        Summary = "@reboot：开机时触发，没有固定时间",
                        Note = "@reboot 由 cron 服务启动时执行，不是时间表达式，无法推算触发时刻",
                    };
                }
                if (!Macros.ContainsKey(macro))
                {
                    return new CronParseResult
                    {
                        Ok = false,
                        Error = new CronFieldError
                        {
                            FieldKey = null,
                            Message = "不支持的宏「" + text + "」，可用：" + string.Join("、", Macros.Keys) + "、@reboot",
                        }
                    };
                }
                var parsed = ParseFieldsParts(SplitFields(Macros[macro]));
                if (!parsed.Ok) return parsed;
                parsed.Source = text;
                parsed.Note = text + " 等价于 " + Macros[macro];
                return parsed;
            }

            var parts = SplitFields(text);
            if (parts.Length == 5)
            {
                var expanded = new[] { "0" }.Concat(parts).ToArray();
                var parsed = ParseFieldsParts(expanded);
                if (!parsed.Ok) return parsed;
                parsed.Source = text;
                parsed.Note = "按 crontab 的 5 段解释（分 时 日 月 周），秒固定为 0";
                return parsed;
            }
            if (parts.Length == 6)
            {
                var parsed = ParseFieldsParts(parts);
                if (!parsed.Ok) return parsed;
                parsed.Source = text;
                return parsed;
            }
            return new CronParseResult
            {
                Ok = false,
                Error = new CronFieldError
                {
                    FieldKey = null,
                    Message = "段数不对：当前 " + parts.Length + " 段，应为 6 段（秒 分 时 日 月 周）或 5 段（分 时 日 月 周）",
                }
            };
        }

        private static string[] SplitFields(string text)
        {
            return text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        }

        // ---- 表达式段 ⇄ 构造器状态 ----

        public static CronBuilderState FieldToBuilder(CronField field)
        {
            if (field.IsWild) return new CronBuilderState { Mode = "every", Step = "" };
            var segments = field.Segments;
            if (segments.Count == 1)
            {
                var segment = segments[0];
                if (segment.IsStar) return new CronBuilderState { Mode = "every", Step = segment.Step.ToString() };
                if (segment.StartOnly) return new CronBuilderState { Mode = "custom", Text = field.Raw };
                if (segment.From != segment.To)
                {
                    return new CronBuilderState
                    {
                        Mode = "range",
                        From = segment.From.ToString(),
                        To = segment.To.ToString(),
                        RangeStep = segment.Step > 1 ? segment.Step.ToString() : "",
                    };
                }
            }
            if (segments.All(s => s.From == s.To))
                return new CronBuilderState { Mode = "list", Values = field.Values.Select(v => v.ToString()).ToList() };
            return new CronBuilderState { Mode = "custom", Text = field.Raw };
        }

        private static int RequireInt(string value, CronFieldDef def, string label)
        {
            var text = (value ?? "").Trim();
            if (!IsDigits(text)) throw new CronException(label + "应是整数");
            var number = int.Parse(text);
            if (number < def.Min || number > def.Max)
                throw new CronException(label + " " + number + " 超出范围 " + def.Min + "-" + def.Max);
            return number;
        }

        public static string BuildField(string key, CronBuilderState state)
        {
            var def = DefOf(key);
            var mode = state != null && !string.IsNullOrEmpty(state.Mode) ? state.Mode : "every";

            if (mode == "custom")
            {
                var text = (state != null ? state.Text : "") ?? "";
                text = text.Trim();
                if (text.Length == 0) throw new CronException("自定义内容为空");
                ParseField(def, text); // 复用解析校验
                return text;
            }

            if (mode == "every")
            {
                var stepText = ((state != null ? state.Step : "") ?? "").Trim();
                if (stepText.Length == 0 || stepText == "1") return "*";
                if (!IsDigits(stepText)) throw new CronException("步进应是整数");
                var step = int.Parse(stepText);
                if (step < 2 || step > def.Max) throw new CronException("步进 " + step + " 超出范围 2-" + def.Max);
                return "*/" + step;
            }

            if (mode == "list")
            {
                var rawValues = state != null && state.Values != null ? state.Values : new List<string>();
                if (rawValues.Count == 0) throw new CronException("至少填写或点选一个值");
                var values = rawValues
                    .Select(value =>
                    {
                        var text = (value ?? "").Trim();
                        if (!IsDigits(text))
                            throw new CronException("「" + text + "」不是整数；区间或步进请改用「区间」「自定义」模式");
                        return RequireInt(text, def, "取值");
                    })
                    .Distinct()
                    .OrderBy(v => v)
                    .ToList();
                return string.Join(",", values);
            }

            if (mode == "range")
            {
                var from = RequireInt(state != null ? state.From : null, def, "起点");
                var to = RequireInt(state != null ? state.To : null, def, "终点");
                if (from > to) throw new CronException("起点大于终点");
                var stepText = ((state != null ? state.RangeStep : "") ?? "").Trim();
                if (stepText.Length == 0) return from + "-" + to;
                if (!IsDigits(stepText)) throw new CronException("步进应是整数");
                var step = int.Parse(stepText);
                if (step < 2) return from + "-" + to;
                if (step > def.Max) throw new CronException("步进 " + step + " 超出范围");
                return from + "-" + to + "/" + step;
            }

            throw new CronException("未知的字段模式: " + mode);
        }

        // ---- 触发时间推算 ----

        private static bool DayMatches(Dictionary<string, CronField> fields, DateTime date)
        {
            var day = fields["day"];
            var week = fields["week"];
            var weekday = (int)date.DayOfWeek; // 周日 = 0，与 cron 周字段编号一致
            var dayOk = date.Day <= 31 && day.Mask[date.Day];
            var weekOk = week.Mask[weekday] || (weekday == 0 && week.Mask[7]);
            if (!day.IsWild && !week.IsWild) return dayOk || weekOk; // 日/周同时限定按「或」
            return dayOk && weekOk;
        }

        private static int NextInMask(bool[] mask, int after, int max)
        {
            for (var value = after + 1; value <= max; value++)
                if (mask[value]) return value;
            return -1;
        }

        private static DateTime AdvanceDay(DateTime date)
        {
            return date.Date.AddDays(1);
        }

        private static DateTime AdvanceMonth(DateTime date)
        {
            return new DateTime(date.Year, date.Month, 1).AddMonths(1);
        }

        public static CronRunResult NextRuns(string source, int count, DateTime from)
        {
            var parsed = Parse(source);
            if (!parsed.Ok) return new CronRunResult { Ok = false, Error = parsed.Error };
            if (parsed.Fields == null) return new CronRunResult { Ok = true, Note = parsed.Note };

            var want = Math.Max(1, Math.Min(count <= 0 ? 5 : count, 50));
            var fields = parsed.Fields.ToDictionary(f => f.Key);

            var runs = new List<DateTime>();
            var limit = from + SearchLimit;
            var cursor = new DateTime(from.Year, from.Month, from.Day, from.Hour, from.Minute, from.Second).AddSeconds(1);

            var steps = 0;
            while (runs.Count < want && cursor <= limit && steps < MaxSteps)
            {
                steps++;
                if (!fields["month"].Mask[cursor.Month])
                {
                    cursor = AdvanceMonth(cursor);
                    continue;
                }
                if (!DayMatches(fields, cursor))
                {
                    cursor = AdvanceDay(cursor);
                    continue;
                }
                var hour = cursor.Hour;
                if (!fields["hour"].Mask[hour])
                {
                    var next = NextInMask(fields["hour"].Mask, hour, 23);
                    cursor = next < 0 ? AdvanceDay(cursor) : new DateTime(cursor.Year, cursor.Month, cursor.Day, next, 0, 0);
                    continue;
                }
                var minute = cursor.Minute;
                if (!fields["minute"].Mask[minute])
                {
                    var next = NextInMask(fields["minute"].Mask, minute, 59);
                    if (next < 0)
                        cursor = new DateTime(cursor.Year, cursor.Month, cursor.Day, hour, 0, 0).AddHours(1);
                    else
                        cursor = new DateTime(cursor.Year, cursor.Month, cursor.Day, hour, next, 0);
                    continue;
                }
                var second = cursor.Second;
                if (!fields["second"].Mask[second])
                {
                    var next = NextInMask(fields["second"].Mask, second, 59);
                    if (next < 0)
                        cursor = new DateTime(cursor.Year, cursor.Month, cursor.Day, hour, minute, 0).AddMinutes(1);
                    else
                        cursor = new DateTime(cursor.Year, cursor.Month, cursor.Day, hour, minute, next);
                    continue;
                }
                runs.Add(cursor);
                cursor = cursor.AddSeconds(1);
            }

            return new CronRunResult
            {
                Ok = true,
                Runs = runs,
                Truncated = runs.Count < want,
                Note = runs.Count > 0 ? "" : "在可预见范围内（4 年）不会触发",
            };
        }
    }
}
