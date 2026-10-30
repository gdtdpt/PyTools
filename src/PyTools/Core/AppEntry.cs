using System.Collections.Generic;

namespace PyTools.Core
{
    /// <summary>
    /// 启动器条目（扫描 / 登记 同构，v1 app-loader.js 的内存模型等价）。
    /// Path 是启动依据（.lnk 原路径或 exe），TargetPath 是解析后的真实目标。
    /// </summary>
    public sealed class AppEntry
    {
        public string Name = "";
        public string Path = "";        // .lnk 或 exe 原路径
        public string TargetPath = "";  // 解析后的目标（lnk 展开）
        public string TargetDir = "";
        public string Args = "";        // 仅登记条目（同 exe 不同参数视为不同条目）
        public string IconPath = "";
        public bool IsCustom;           // 用户显式登记（同一 target 覆盖扫描条目）

        /// <summary>搜索词：小写关键词（name/targetName/targetFolder）。</summary>
        public List<string> Keywords = new List<string>();

        /// <summary>拼音首字母串变体（多音字展开，见 Pinyin.Expand）。</summary>
        public List<string> Initials = new List<string>();
    }
}
