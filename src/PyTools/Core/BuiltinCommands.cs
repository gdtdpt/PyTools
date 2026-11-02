using System;

namespace PyTools.Core
{
    /// <summary>内置工具入口（v2 内置命令退化形态：只有 id/别名/标题/副标题，无子命令与参数）。</summary>
    public sealed class BuiltinCommand
    {
        public string Id = "";
        public string Title = "";
        public string Subtitle = "";
        public string[] Aliases = new string[0];
    }

    /// <summary>
    /// 内置命令注册表（M4 逐工具扩充）。当前只有 vsc：
    /// 启动器输入别名命中后回车，切到对应工具视图。
    /// </summary>
    public static class BuiltinCommands
    {
        private static readonly BuiltinCommand[] Items =
        {
            new BuiltinCommand
            {
                Id = "vsc",
                Title = "VSCode 项目",
                Subtitle = "打开最近项目",
                Aliases = new[] { "vsc" }
            }
        };

        /// <summary>查询命中的内置命令条目（exact &gt; 前缀 &gt; 包含；标题也参与匹配）。</summary>
        public static AppEntry Match(string query)
        {
            var q = (query ?? "").Trim().ToLowerInvariant();
            if (q.Length == 0) return null;

            BuiltinCommand best = null;
            var bestRank = int.MaxValue;
            foreach (var command in Items)
            {
                var rank = Rank(command, q);
                if (rank < bestRank) { bestRank = rank; best = command; }
            }
            if (best == null) return null;

            return new AppEntry
            {
                Name = best.Title,
                Path = "",
                TargetPath = best.Subtitle, // 行副标题直接复用 TargetPath 的展示位
                IsCommand = true,
                CommandId = best.Id
            };
        }

        public static BuiltinCommand Find(string id)
        {
            foreach (var command in Items)
                if (string.Equals(command.Id, id, StringComparison.OrdinalIgnoreCase)) return command;
            return null;
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
            if (command.Title.ToLowerInvariant().IndexOf(q, StringComparison.Ordinal) >= 0 && best > 3) best = 3;
            return best;
        }
    }
}
