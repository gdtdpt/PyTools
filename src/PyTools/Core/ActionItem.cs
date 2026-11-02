using System;

namespace PyTools.Core
{
    /// <summary>
    /// 动作面板的一行（声明式动作表，M4 各工具共用）：
    /// 视图只负责产出条目，面板负责过滤、导航、单键提示与呼出定位。
    /// 注意：Title/Hint 会被 XAML 绑定，必须是属性（字段绑定不上，实测踩过）。
    /// </summary>
    public sealed class ActionItem
    {
        /// <summary>动作 id：过滤时与标题一起参与匹配（标题是中文、id 是英文，输 copy 也能命中 copy-name）。</summary>
        public string Id { get; set; }

        /// <summary>行标题（中文）。</summary>
        public string Title { get; set; }

        /// <summary>行尾单键提示（大写字母）；空串 = 无提示键。同一张表内不得重复。</summary>
        public string Hint { get; set; }

        /// <summary>false = 当前上下文不提供该动作，面板直接不列出（如未安装 Git Bash 时的「在 Git Bash 打开」）。</summary>
        public bool Available { get; set; }

        public ActionItem()
        {
            Id = "";
            Title = "";
            Hint = "";
            Available = true;
        }

        public ActionItem(string id, string title, string hint, bool available = true)
        {
            Id = id;
            Title = title;
            Hint = hint;
            Available = available;
        }
    }
}
