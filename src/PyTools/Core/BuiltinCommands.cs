using System;
using System.Collections.Generic;
using System.Linq;

namespace PyTools.Core
{
    /// <summary>内置工具入口（v2 内置条目退化形态：只有 id/行为/标题/副标题，无子命令与参数——v2 无命令直达）。</summary>
    public sealed class BuiltinCommand
    {
        public string Id = "";
        /// <summary>行为类型："view" = 回车切工具视图；"action" = 回车直接执行（静默 + 气泡反馈），不弹窗；
        /// "tool-list" = 回车进启动器的工具列表模式（2026-11-06，v1 tools 命令同款）。</summary>
        public string Kind = "view";
        public string Title = "";
        public string Subtitle = "";
        public string[] Aliases = new string[0];
        /// <summary>矢量图标资源键（Themes/Icons.xaml，Icon.<Id>）；空 = 列表行走首字头像回退。</summary>
        public string IconKey = "";
    }

    /// <summary>
    /// 内置工具入口注册表（M4 逐工具扩充）。启动器输入唤醒词命中后回车，按 Kind 切视图或执行动作。
    /// 唤醒词口径（M4 定稿）：每工具 = 英文 id 主词 + 至多一个中文别名；工具标题的拼音
    /// 首字母自动参与命中（复用应用搜索的 Pinyin.Expand），不再多挂同义词。
    /// 同词可命中多条（如 format → 「格式化」面板 + 「格式化剪贴板」动作），按 rank 与声明顺序排列。
    /// </summary>
    public static class BuiltinCommands
    {
        private static readonly BuiltinCommand[] Items =
        {
            // 工具列表入口（2026-11-06，v1 tools 命令同款）：回车进启动器的工具列表模式；
            // 无矢量图标，列表行走首字头像回退（「工」）。置于首位与 v1 的结果排序一致。
            new BuiltinCommand
            {
                Id = "tools",
                Kind = "tool-list",
                Title = "工具列表",
                Subtitle = "浏览全部工具，按名称或唤醒词查找",
                Aliases = new[] { "tools", "工具" }
            },
            new BuiltinCommand
            {
                Id = "vsc",
                Kind = "view",
                Title = "VSCode 项目",
                Subtitle = "打开最近项目",
                Aliases = new[] { "vsc" },
                IconKey = "Icon.Vsc"
            },
            new BuiltinCommand
            {
                Id = "clipboard",
                Kind = "view",
                Title = "剪贴板记录",
                Subtitle = "查看剪贴板历史",
                Aliases = new[] { "clipboard", "剪贴板" },
                IconKey = "Icon.Clipboard"
            },
            new BuiltinCommand
            {
                Id = "format",
                Kind = "view",
                Title = "格式化",
                Subtitle = "结构化数据格式化（JSON/XML）",
                Aliases = new[] { "format", "格式化" },
                IconKey = "Icon.Format"
            },
            new BuiltinCommand
            {
                Id = "encode",
                Kind = "view",
                Title = "常用编码转换",
                Subtitle = "URL/Base64/Unicode/HTML/JSON/Hex 互转",
                Aliases = new[] { "encode", "编码" },
                IconKey = "Icon.Encode"
            },
            new BuiltinCommand
            {
                Id = "cron",
                Kind = "view",
                Title = "Cron 表达式",
                Subtitle = "构造解析与最近触发时间推算",
                Aliases = new[] { "cron" }, // 无中文别名：「定时」「计划」太泛，留给以后真正的定时任务工具
                IconKey = "Icon.Cron"
            },
            new BuiltinCommand
            {
                Id = "regex",
                Kind = "view",
                Title = "正则测试",
                Subtitle = "正则匹配高亮与替换预览",
                Aliases = new[] { "regex", "正则" },
                IconKey = "Icon.Regex"
            },
            new BuiltinCommand
            {
                Id = "diff",
                Kind = "view", // 打开独立常驻窗口（不寄居呼出壳），仍属「工具入口」语义
                Title = "文件差异对比",
                Subtitle = "并排比较两个文本文件",
                Aliases = new[] { "diff", "对比" },
                IconKey = "Icon.Diff"
            },
            new BuiltinCommand
            {
                Id = "ocr",
                Kind = "view", // 打开独立常驻窗口（与 diff 同族），仍属「工具入口」语义
                Title = "图片文字识别",
                Subtitle = "截图或图片 → AI 提取文字",
                Aliases = new[] { "ocr", "识别" },
                IconKey = "Icon.Ocr"
            },
            new BuiltinCommand
            {
                Id = "translate",
                Kind = "view",
                Title = "AI 翻译",
                Subtitle = "中英日互译，支持自动识别语种",
                Aliases = new[] { "fanyi", "翻译" },
                IconKey = "Icon.Translate"
            },
            new BuiltinCommand
            {
                Id = "naming",
                Kind = "view",
                Title = "代码命名",
                Subtitle = "由名词或业务描述生成变量/类/表名等命名",
                Aliases = new[] { "naming", "var", "命名" }, // var = v1 最高频呼出词（keywords 首位）
                IconKey = "Icon.Naming"
            },
            new BuiltinCommand
            {
                Id = "format-clipboard",
                Kind = "action",
                Title = "格式化剪贴板",
                Subtitle = "自动识别 JSON/XML，格式化并写回",
                Aliases = new[] { "format", "格式化" },
                IconKey = "Icon.FormatClipboard"
            }
        };

        /// <summary>查询命中的全部内置条目，按（rank 升序、声明顺序）排列；无命中返回空表。</summary>
        public static List<AppEntry> MatchAll(string query)
        {
            var result = new List<AppEntry>();
            var q = (query ?? "").Trim().ToLowerInvariant();
            if (q.Length == 0) return result;

            var hits = new List<Tuple<int, int, BuiltinCommand>>(); // rank, 声明序, 条目
            for (var i = 0; i < Items.Length; i++)
            {
                var rank = Rank(Items[i], q);
                if (rank < int.MaxValue) hits.Add(Tuple.Create(rank, i, Items[i]));
            }
            foreach (var hit in hits.OrderBy(h => h.Item1).ThenBy(h => h.Item2))
                result.Add(ToEntry(hit.Item3));
            return result;
        }

        /// <summary>查询命中的第一条内置条目（兼容旧调用；null = 无命中）。</summary>
        public static AppEntry Match(string query)
        {
            var all = MatchAll(query);
            return all.Count > 0 ? all[0] : null;
        }

        public static BuiltinCommand Find(string id)
        {
            foreach (var command in Items)
                if (string.Equals(command.Id, id, StringComparison.OrdinalIgnoreCase)) return command;
            return null;
        }

        private static AppEntry ToEntry(BuiltinCommand command)
        {
            return new AppEntry
            {
                Name = command.Title,
                Path = "",
                TargetPath = command.Subtitle, // 行副标题直接复用 TargetPath 的展示位
                IsCommand = true,
                CommandId = command.Id,
                IconKey = command.IconKey
            };
        }

        /// <summary>全部工具条目（声明序，不含「工具列表」入口自身）——工具列表模式的空查询视图。</summary>
        public static List<AppEntry> All()
        {
            var result = new List<AppEntry>();
            foreach (var command in Items)
                if (command.Kind != "tool-list") result.Add(ToEntry(command));
            return result;
        }

        /// <summary>
        /// 工具列表模式过滤（v1 filterToolList 口径）：空查询列全部；
        /// 有查询按别名/标题/标题拼音首字母（Rank）或副标题子串命中。
        /// </summary>
        public static List<AppEntry> FilterToolList(string query)
        {
            var q = (query ?? "").Trim().ToLowerInvariant();
            if (q.Length == 0) return All();
            var result = new List<AppEntry>();
            foreach (var command in Items)
            {
                if (command.Kind == "tool-list") continue;
                if (Rank(command, q) < int.MaxValue) { result.Add(ToEntry(command)); continue; }
                var subtitle = (command.Subtitle ?? "").ToLowerInvariant();
                if (subtitle.Length > 0 && subtitle.IndexOf(q, StringComparison.Ordinal) >= 0)
                    result.Add(ToEntry(command));
            }
            return result;
        }

        private static int Rank(BuiltinCommand command, string q)
        {
            var best = int.MaxValue;
            foreach (var alias in command.Aliases)
            {
                var term = (alias ?? "").ToLowerInvariant();
                if (term.Length == 0) continue;
                if (term == q) return 0;
                if (term.StartsWith(q, StringComparison.Ordinal)) { if (best > 1) best = 1; continue; }
                if (term.IndexOf(q, StringComparison.Ordinal) >= 0 && best > 2) best = 2;
            }
            var title = command.Title ?? "";
            if (title.ToLowerInvariant().IndexOf(q, StringComparison.Ordinal) >= 0 && best > 3) best = 3;
            // 标题拼音首字母（如「剪贴板记录」→ jtbjl）：与应用搜索同权语义，视同标题包含档
            if (best > 3 && Pinyin.Ready)
            {
                foreach (var initials in Pinyin.Expand(title))
                {
                    if (initials.StartsWith(q, StringComparison.Ordinal)) { best = 3; break; }
                    if (initials.IndexOf(q, StringComparison.Ordinal) >= 0 && best > 4) best = 4;
                }
            }
            return best;
        }
    }
}
