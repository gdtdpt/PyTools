using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace PyTools.Core
{
    /// <summary>单条捕获组（列表展示用）。</summary>
    public sealed class RegexGroupInfo
    {
        /// <summary>展示标签：编号组 "$1"、命名组 "name (命名)"。</summary>
        public string Label;
        /// <summary>组值；组未参与匹配为 null（视图层显示 "(空)"，与 v1 一致）。</summary>
        public string Value;
    }

    /// <summary>单条匹配（起始位置 + 匹配值 + 捕获组展开）。</summary>
    public sealed class RegexMatchInfo
    {
        /// <summary>起始位置（0 基，视图展示时 +1 对齐 v1 的「起始位置 N」）。</summary>
        public int Index;
        public string Value;
        public readonly List<RegexGroupInfo> Groups = new List<RegexGroupInfo>();
    }

    /// <summary>一次求值的完整结果。</summary>
    public sealed class RegexResult
    {
        public bool Ok;
        /// <summary>编译失败 / 匹配超时的原因文案（Ok=false 时非空）。</summary>
        public string Error;
        public readonly List<RegexMatchInfo> Matches = new List<RegexMatchInfo>();
        /// <summary>达到 MatchLimit 截断（只影响列表，替换/高亮语义不受影响）。</summary>
        public bool Truncated;
        /// <summary>匹配上限（v1 同值 200，防大文本炸界面）。</summary>
        public const int MatchLimit = 200;
    }

    /// <summary>
    /// 正则测试引擎（T5 纯本地，.NET Regex 原生，零第三方依赖）。
    /// 与 v1（tools/regex-tool，JS）语义对齐的四个点：
    /// ① 无 g 时列表只收第一条、替换只替换第一处；
    /// ② 组展开顺序 = 命名组在前（label「name (命名)」）、编号组在后（$1 起）；
    /// ③ 匹配上限 200 条；④ 替换串 $1/${name} 语义与 JS 等价。
    /// v2 增补：表达式编译与求值带 250ms 超时（灾难性回溯不冻结 UI，v1 无此防线）。
    /// </summary>
    public static class RegexTester
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);

        /// <summary>i/m/s → RegexOptions（g 不进 options，在求值/替换层控制条数）。</summary>
        public static RegexOptions BuildOptions(bool ignoreCase, bool multiline, bool dotAll)
        {
            var options = RegexOptions.None;
            if (ignoreCase) options |= RegexOptions.IgnoreCase;
            if (multiline) options |= RegexOptions.Multiline;
            if (dotAll) options |= RegexOptions.Singleline; // .NET 的 s = Singleline（. 匹配换行）
            return options;
        }

        /// <summary>
        /// 求值：返回匹配列表（含组展开）。空表达式返回 Ok 且 0 匹配（视图层显示「等待输入正则」）。
        /// </summary>
        public static RegexResult Evaluate(string pattern, string text, bool global, bool ignoreCase, bool multiline, bool dotAll)
        {
            var result = new RegexResult();
            if (string.IsNullOrEmpty(pattern))
            {
                result.Ok = true;
                return result;
            }
            Regex regex;
            try
            {
                regex = new Regex(pattern, BuildOptions(ignoreCase, multiline, dotAll), Timeout);
            }
            catch (ArgumentException ex)
            {
                result.Error = ex.Message;
                return result;
            }

            result.Ok = true;
            try
            {
                var current = regex.Match(text ?? "");
                while (current.Success)
                {
                    var info = new RegexMatchInfo { Index = current.Index, Value = current.Value };
                    // .NET 组号实测（2026-11-04 selftest + 探针确认）：未命名组先编号 1..k，命名组编号排后
                    // （如 (?<year>…)-(\d{2}) → year=2、未命名组=1）。GetGroupNames 里纯数字名即未命名组，
                    // 只展开它们即与 JS 的 $n 语义一致（JS 命名组不占号）；命名组按定义顺序展开在前。
                    var numbered = new List<int>();
                    foreach (var name in regex.GetGroupNames())
                    {
                        int num;
                        if (int.TryParse(name, out num))
                        {
                            if (num > 0) numbered.Add(num);
                            continue;
                        }
                        var namedGroup = current.Groups[name];
                        info.Groups.Add(new RegexGroupInfo
                        {
                            Label = name + " (命名)",
                            Value = namedGroup.Success ? namedGroup.Value : null,
                        });
                    }
                    numbered.Sort();
                    foreach (var num in numbered)
                    {
                        var group = current.Groups[num];
                        info.Groups.Add(new RegexGroupInfo
                        {
                            Label = "$" + num,
                            Value = group.Success ? group.Value : null,
                        });
                    }
                    result.Matches.Add(info);
                    if (result.Matches.Count >= RegexResult.MatchLimit)
                    {
                        result.Truncated = true;
                        break;
                    }
                    if (!global) break;
                    current = current.NextMatch(); // .NET 对零长匹配自动推进一位，无 v1 手动 lastIndex 逻辑
                }
            }
            catch (RegexMatchTimeoutException)
            {
                result.Matches.Clear();
                result.Error = "匹配超时（250ms），正则可能引发灾难性回溯";
                result.Ok = false;
            }
            return result;
        }

        /// <summary>替换（v1 语义：无 g 只替换第一处；$1/${name} 与 JS 等价）。空表达式原样返回。</summary>
        public static string Replace(string pattern, string text, string replacement, bool global, bool ignoreCase, bool multiline, bool dotAll)
        {
            if (string.IsNullOrEmpty(pattern)) return text;
            Regex regex;
            try
            {
                regex = new Regex(pattern, BuildOptions(ignoreCase, multiline, dotAll), Timeout);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException(ex.Message, ex);
            }
            try
            {
                return regex.Replace(text ?? "", replacement ?? "", global ? -1 : 1);
            }
            catch (RegexMatchTimeoutException)
            {
                throw new ArgumentException("匹配超时（250ms），正则可能引发灾难性回溯");
            }
        }
    }
}
