using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace PyTools.Core
{
    /// <summary>
    /// 拼音首字母（L7，纯本地）：内嵌「汉字→读音首字母集合」映射表（EmbeddedResource，
    /// 源数据 mozillazg/pinyin-data，每字保留全部读音——多音字在展开时枚举变体，不赌常用音）。
    /// 非汉字字符按原样小写保留；表中没有的汉字不产生首字母（精确关键词仍可命中）。
    /// </summary>
    public static class Pinyin
    {
        private const int MaxVariants = 8;

        private static readonly Dictionary<char, string> Map = new Dictionary<char, string>();
        private static bool _loaded;

        public static bool Ready { get { EnsureLoaded(); return _loaded && Map.Count > 0; } }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                using (var stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("PyTools.Assets.pinyin-initials.txt"))
                {
                    if (stream == null) return;
                    using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            var sep = line.IndexOf(':');
                            if (sep != 1) continue;
                            var letter = line[0];
                            if (letter < 'a' || letter > 'z') continue;
                            for (var i = sep + 1; i < line.Length; i++)
                            {
                                var ch = line[i];
                                string letters;
                                if (!Map.TryGetValue(ch, out letters))
                                {
                                    Map[ch] = letter.ToString();
                                }
                                else if (!letters.Contains(letter.ToString()))
                                {
                                    Map[ch] = letters + letter;
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                Map.Clear(); // 表损坏时静默降级：仅精确关键词可搜，不影响其它功能
            }
        }

        /// <summary>单字的首字母集合；非 a-z 汉字返回 null。ASCII 字母数字返回小写自身。</summary>
        public static string LettersOf(char ch)
        {
            EnsureLoaded();
            if (ch >= 'A' && ch <= 'Z') return ((char)(ch + 32)).ToString();
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')) return ch.ToString();
            string letters;
            return Map.TryGetValue(ch, out letters) ? letters : null;
        }

        /// <summary>
        /// 文本的拼音首字母串变体（多音字展开；上限 MaxVariants，超出取字典序靠前的组合）。
        /// 空格等分隔符跳过；含无法映射的汉字时该字所在分支终止。
        /// </summary>
        public static List<string> Expand(string text)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text)) return result;
            EnsureLoaded();
            var current = new List<string> { "" };
            foreach (var ch in text)
            {
                if (char.IsWhiteSpace(ch) || ch == '-' || ch == '_' || ch == '.' || ch == '(' || ch == ')')
                {
                    continue; // 分隔符不进入首字母串
                }
                var letters = LettersOf(ch);
                if (letters == null) return result; // 存在无读音汉字 → 整串放弃

                var next = new List<string>();
                foreach (var prefix in current)
                {
                    foreach (var letter in letters)
                    {
                        if (next.Count >= MaxVariants) break;
                        next.Add(prefix + letter);
                    }
                    if (next.Count >= MaxVariants) break;
                }
                if (next.Count == 0) return result;
                current = next;
            }
            result.AddRange(current);
            return result;
        }
    }
}
