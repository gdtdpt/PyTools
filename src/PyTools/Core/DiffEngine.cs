using System;
using System.Collections.Generic;

namespace PyTools.Core
{
    /// <summary>差异行类型（v1 diff-row 同语义）。</summary>
    public enum DiffKind
    {
        Equal,
        Change,
        Delete,
        Insert
    }

    /// <summary>行内片段（仅 Change 行有）：Changed = 该段在行内 diff 中被判定为增/删。</summary>
    public sealed class DiffPart
    {
        public string Text { get; set; }
        public bool Changed { get; set; }

        public DiffPart()
        {
            Text = "";
        }

        public DiffPart(string text, bool changed)
        {
            Text = text ?? "";
            Changed = changed;
        }
    }

    /// <summary>
    /// 双栏对齐后的一行。Left/Right 为 null 表示该侧是无内容空档（对面有增/删行）。
    /// 行号 0 = 空档（模板显示空串）。
    /// </summary>
    public sealed class DiffRow : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isFocused;

        public DiffKind Kind { get; set; }
        public string Left { get; set; }
        public string Right { get; set; }
        public List<DiffPart> LeftParts { get; set; }
        public List<DiffPart> RightParts { get; set; }
        public int LeftNumber { get; set; }
        public int RightNumber { get; set; }

        /// <summary>跳转聚焦态（F3 命中行）：行模板与 DiffTextBlock 按此切换聚焦样式。</summary>
        public bool IsFocused
        {
            get { return _isFocused; }
            set
            {
                if (_isFocused == value) return;
                _isFocused = value;
                var handler = PropertyChanged;
                if (handler != null) handler(this, new System.ComponentModel.PropertyChangedEventArgs("IsFocused"));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

        public string LeftNumberText { get { return LeftNumber > 0 ? LeftNumber.ToString() : ""; } }
        public string RightNumberText { get { return RightNumber > 0 ? RightNumber.ToString() : ""; } }
    }

    /// <summary>比较结果：行表 + 差异行数（Kind != Equal，含 Change/Delete/Insert）。</summary>
    public sealed class DiffResult
    {
        public List<DiffRow> Rows { get; set; }
        public int DifferenceCount { get; set; }
    }

    /// <summary>
    /// 文本差异引擎（v1 tools/text-diff/diff-worker.js 纯移植，2026-11-04）：
    /// patience diff（唯一行锚点 + LIS）→ 行数超限或无锚点退 Myers → 再退 anchorFallback（lookahead 80 就近配对）。
    /// **比较键 = 原行原文，零归一化**（v1 的 compareKey 会剥 &lt;b&gt;/&lt;strong&gt; 并折叠空白，那是 HTML 面板
    /// 误渲染场景下打的补丁，v2 拍板废除——内容原样显示、原样比较，行尾空格与全半角差异都算差异）。
    /// 仅读取层把换行统一为 \n（换行协议不算内容）。
    /// </summary>
    public static class DiffEngine
    {
        /// <summary>Myers 的 a+b 行数上限（与 v1 一致；超出退 anchorFallback，防 O(ND) 内存爆炸）。</summary>
        private const int MyersLineLimit = 30000;

        /// <summary>anchorFallback 的就近配对窗口（与 v1 一致）。</summary>
        private const int Lookahead = 80;

        /// <summary>按行拆分：CRLF/CR 先归一到 LF，再按 LF 拆（尾部空行保留，与 v1 split('\n') 一致）。</summary>
        public static string[] SplitLines(string text)
        {
            var normalized = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            return normalized.Split('\n');
        }

        public static DiffResult Compare(string leftText, string rightText)
        {
            var a = SplitLines(leftText);
            var b = SplitLines(rightText);
            var rows = RowsFromOps(PatienceOps(a, b));

            var leftNumber = 0;
            var rightNumber = 0;
            var differences = 0;
            foreach (var row in rows)
            {
                if (row.Left != null) row.LeftNumber = ++leftNumber;
                if (row.Right != null) row.RightNumber = ++rightNumber;
                if (row.Kind != DiffKind.Equal) differences++;
                if (row.Kind == DiffKind.Change) BuildInlineParts(row);
            }
            return new DiffResult { Rows = rows, DifferenceCount = differences };
        }

        // ---- 行内（字符级）差异 ----

        private static void BuildInlineParts(DiffRow row)
        {
            var left = Chars(row.Left ?? "");
            var right = Chars(row.Right ?? "");
            row.LeftParts = new List<DiffPart>();
            row.RightParts = new List<DiffPart>();
            foreach (var op in RowsFromOps(PatienceOps(left, right)))
            {
                switch (op.Kind)
                {
                    case DiffKind.Equal:
                        AppendPart(row.LeftParts, op.Left, false);
                        AppendPart(row.RightParts, op.Right, false);
                        break;
                    case DiffKind.Change:
                        AppendPart(row.LeftParts, op.Left, true);
                        AppendPart(row.RightParts, op.Right, true);
                        break;
                    case DiffKind.Delete:
                        AppendPart(row.LeftParts, op.Left, true);
                        break;
                    case DiffKind.Insert:
                        AppendPart(row.RightParts, op.Right, true);
                        break;
                }
            }
        }

        /// <summary>同标记态相邻片段合并，减少 Run 数量（长行字符级 diff 会产生大量碎段）。</summary>
        private static void AppendPart(List<DiffPart> parts, string text, bool changed)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (parts.Count > 0)
            {
                var last = parts[parts.Count - 1];
                if (last.Changed == changed) { last.Text += text; return; }
            }
            parts.Add(new DiffPart(text, changed));
        }

        private static string[] Chars(string text)
        {
            var result = new string[text.Length];
            for (var i = 0; i < text.Length; i++) result[i] = text[i].ToString();
            return result;
        }

        // ---- 一维 diff 操作序列 ----

        private struct Op
        {
            public DiffKind Kind;
            public string Left;
            public string Right;

            public static Op Equal(string left, string right)
            {
                return new Op { Kind = DiffKind.Equal, Left = left, Right = right };
            }

            public static Op Delete(string left)
            {
                return new Op { Kind = DiffKind.Delete, Left = left };
            }

            public static Op Insert(string right)
            {
                return new Op { Kind = DiffKind.Insert, Right = right };
            }
        }

        /// <summary>patience：唯一行做锚点，按 LIS 求最长公共子序列后再逐段 Myers；无锚点直接 Myers/兜底。</summary>
        private static List<Op> PatienceOps(string[] a, string[] b)
        {
            var leftCount = new Dictionary<string, int>(StringComparer.Ordinal);
            var rightCount = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var line in a) leftCount[line] = Get(leftCount, line) + 1;
            foreach (var line in b) rightCount[line] = Get(rightCount, line) + 1;

            var positions = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < b.Length; i++)
                if (Get(rightCount, b[i]) == 1) positions[b[i]] = i;

            var candidates = new List<int[]>(); // [aIndex, bIndex]
            for (var i = 0; i < a.Length; i++)
            {
                int position;
                if (Get(leftCount, a[i]) == 1 && positions.TryGetValue(a[i], out position))
                    candidates.Add(new[] { i, position });
            }

            var tails = new List<int>();
            var previous = new int[candidates.Count];
            for (var i = 0; i < previous.Length; i++) previous[i] = -1;
            for (var i = 0; i < candidates.Count; i++)
            {
                var lo = 0;
                var hi = tails.Count;
                while (lo < hi)
                {
                    var mid = (lo + hi) >> 1;
                    if (candidates[tails[mid]][1] < candidates[i][1]) lo = mid + 1;
                    else hi = mid;
                }
                if (lo > 0) previous[i] = tails[lo - 1];
                if (lo == tails.Count) tails.Add(i);
                else tails[lo] = i;
            }

            var anchors = new List<int[]>();
            var cursor = tails.Count > 0 ? tails[tails.Count - 1] : -1;
            while (cursor >= 0)
            {
                anchors.Insert(0, candidates[cursor]);
                cursor = previous[cursor];
            }

            if (anchors.Count == 0) return MyersOps(a, b) ?? AnchorFallback(a, b);

            var ops = new List<Op>();
            var ai = 0;
            var bi = 0;
            foreach (var anchor in anchors)
            {
                var segmentA = Slice(a, ai, anchor[0]);
                var segmentB = Slice(b, bi, anchor[1]);
                ops.AddRange(MyersOps(segmentA, segmentB) ?? AnchorFallback(segmentA, segmentB));
                ops.Add(Op.Equal(a[anchor[0]], b[anchor[1]]));
                ai = anchor[0] + 1;
                bi = anchor[1] + 1;
            }
            var tailA = Slice(a, ai, a.Length);
            var tailB = Slice(b, bi, b.Length);
            ops.AddRange(MyersOps(tailA, tailB) ?? AnchorFallback(tailA, tailB));
            return ops;
        }

        private static string[] Slice(string[] source, int start, int end)
        {
            var length = Math.Max(0, end - start);
            var result = new string[length];
            Array.Copy(source, start, result, 0, length);
            return result;
        }

        /// <summary>Myers O(ND)：返回 null 表示 a+b 超限（交由 anchorFallback）。</summary>
        private static List<Op> MyersOps(string[] a, string[] b)
        {
            var max = a.Length + b.Length;
            if (max > MyersLineLimit) return null;

            var v = new Dictionary<int, int> { { 1, 0 } };
            var trace = new List<Dictionary<int, int>>();
            for (var d = 0; d <= max; d++)
            {
                trace.Add(new Dictionary<int, int>(v));
                for (var k = -d; k <= d; k += 2)
                {
                    int x;
                    if (k == -d || (k != d && GetV(v, k - 1) < GetV(v, k + 1))) x = GetVOrZero(v, k + 1);
                    else x = GetVOrZero(v, k - 1) + 1;
                    var y = x - k;
                    while (x < a.Length && y < b.Length && string.Equals(a[x], b[y], StringComparison.Ordinal)) { x++; y++; }
                    v[k] = x;
                    if (x >= a.Length && y >= b.Length) return Backtrack(trace, a, b, d);
                }
            }
            return null;
        }

        private static List<Op> Backtrack(List<Dictionary<int, int>> trace, string[] a, string[] b, int d)
        {
            var ops = new List<Op>();
            var cx = a.Length;
            var cy = b.Length;
            for (var depth = d; depth > 0; depth--)
            {
                var previous = trace[depth];
                var ck = cx - cy;
                var previousK = ck == -depth || (ck != depth && GetV(previous, ck - 1) < GetV(previous, ck + 1))
                    ? ck + 1
                    : ck - 1;
                var previousX = GetVOrZero(previous, previousK);
                var previousY = previousX - previousK;
                while (cx > previousX && cy > previousY)
                {
                    ops.Add(Op.Equal(a[cx - 1], b[cy - 1]));
                    cx--;
                    cy--;
                }
                if (cx == previousX) { ops.Add(Op.Insert(b[cy - 1])); cy--; }
                else { ops.Add(Op.Delete(a[cx - 1])); cx--; }
            }
            while (cx > 0 && cy > 0)
            {
                ops.Add(Op.Equal(a[cx - 1], b[cy - 1]));
                cx--;
                cy--;
            }
            while (cx > 0) ops.Add(Op.Delete(a[--cx]));
            while (cy > 0) ops.Add(Op.Insert(b[--cy]));
            ops.Reverse();
            return ops;
        }

        /// <summary>兜底：无可用 Myers 时按 lookahead 窗口找最近相同行对齐，找不到就按「改/删/增」顺序产行。</summary>
        private static List<Op> AnchorFallback(string[] a, string[] b)
        {
            var ops = new List<Op>();
            var i = 0;
            var j = 0;
            while (i < a.Length || j < b.Length)
            {
                if (i < a.Length && j < b.Length && string.Equals(a[i], b[j], StringComparison.Ordinal))
                {
                    ops.Add(Op.Equal(a[i], b[j]));
                    i++;
                    j++;
                    continue;
                }
                var bestX = -1;
                var bestY = -1;
                var best = int.MaxValue;
                var aEnd = Math.Min(a.Length, i + Lookahead);
                var bEnd = Math.Min(b.Length, j + Lookahead);
                for (var x = i; x < aEnd; x++)
                {
                    for (var y = j; y < bEnd; y++)
                    {
                        if (!string.Equals(a[x], b[y], StringComparison.Ordinal)) continue;
                        var cost = x - i + y - j;
                        if (cost >= best) continue;
                        best = cost;
                        bestX = x;
                        bestY = y;
                    }
                }
                if (bestX < 0)
                {
                    if (i < a.Length && j < b.Length) ops.Add(new Op { Kind = DiffKind.Change, Left = a[i++], Right = b[j++] });
                    else if (i < a.Length) ops.Add(Op.Delete(a[i++]));
                    else ops.Add(Op.Insert(b[j++]));
                    continue;
                }
                while (i < bestX) ops.Add(Op.Delete(a[i++]));
                while (j < bestY) ops.Add(Op.Insert(b[j++]));
            }
            return ops;
        }

        /// <summary>连续 delete/insert 段按最小值逐行配对成 change（与 v1 rowsFromOps 一致）。</summary>
        private static List<DiffRow> RowsFromOps(List<Op> ops)
        {
            var rows = new List<DiffRow>();
            var i = 0;
            while (i < ops.Count)
            {
                var op = ops[i];
                if (op.Kind == DiffKind.Equal)
                {
                    rows.Add(new DiffRow { Kind = DiffKind.Equal, Left = op.Left, Right = op.Right });
                    i++;
                    continue;
                }
                if (op.Kind == DiffKind.Change)
                {
                    rows.Add(new DiffRow { Kind = DiffKind.Change, Left = op.Left, Right = op.Right });
                    i++;
                    continue;
                }
                var deletes = new List<string>();
                var inserts = new List<string>();
                while (i < ops.Count && ops[i].Kind == DiffKind.Delete) deletes.Add(ops[i++].Left);
                while (i < ops.Count && ops[i].Kind == DiffKind.Insert) inserts.Add(ops[i++].Right);
                var paired = Math.Min(deletes.Count, inserts.Count);
                for (var k = 0; k < paired; k++)
                    rows.Add(new DiffRow { Kind = DiffKind.Change, Left = deletes[k], Right = inserts[k] });
                for (var k = paired; k < deletes.Count; k++)
                    rows.Add(new DiffRow { Kind = DiffKind.Delete, Left = deletes[k] });
                for (var k = paired; k < inserts.Count; k++)
                    rows.Add(new DiffRow { Kind = DiffKind.Insert, Right = inserts[k] });
            }
            return rows;
        }

        private static int Get(Dictionary<string, int> map, string key)
        {
            int value;
            return map.TryGetValue(key, out value) ? value : 0;
        }

        // ---- 差异跳转（F3/Shift+F3，2026-11-04 增补拍板）与不可见字符 ----

        /// <summary>差异行索引表（Kind != Equal，按出现顺序）。跳转定位的基础，比较完成时构建一次。</summary>
        public static List<int> DiffLineIndexes(List<DiffRow> rows)
        {
            var indexes = new List<int>();
            for (var i = 0; i < rows.Count; i++)
                if (rows[i].Kind != DiffKind.Equal) indexes.Add(i);
            return indexes;
        }

        /// <summary>
        /// 找跳转目标在差异行索引表中的序号。fromIndex = 当前视口第一可见行（无状态定位，
        /// 来回切文件/重算后天然正确）。forward：第一个起始行 &gt; fromIndex 的差异行；没有则回绕到第一个。
        /// backward：最后一个起始行 &lt; fromIndex 的；没有则回绕到最后一个。无差异返回 -1。
        /// </summary>
        public static int FindJumpTarget(List<int> diffIndexes, int fromIndex, bool forward, out bool wrapped)
        {
            wrapped = false;
            if (diffIndexes == null || diffIndexes.Count == 0) return -1;
            if (forward)
            {
                foreach (var index in diffIndexes)
                    if (index > fromIndex) return diffIndexes.IndexOf(index);
                wrapped = true;
                return 0; // 已到最后一个：回绕到开头
            }
            for (var i = diffIndexes.Count - 1; i >= 0; i--)
                if (diffIndexes[i] < fromIndex) return i;
            wrapped = true;
            return diffIndexes.Count - 1; // 已到最前：回绕到末尾
        }

        /// <summary>
        /// 不可见字符白名单（零归一化拍板下这些字符如实判差异，但渲染宽 0 看不见，需竖条标识）：
        /// 软连字符、阿拉伯数字符标记、蒙古元音分隔符、零宽空格/连接符族（200B-200F）、
        /// 词连接符族（2060-2064）、BOM/零宽不换行空格、行间注记符。普通空格/Tab 不在内（不做全量可视化）。
        /// </summary>
        public static bool IsInvisibleChar(char c)
        {
            return c == '\u00AD' || c == '\u061C' || c == '\u180E'
                || (c >= '\u200B' && c <= '\u200F')
                || (c >= '\u2060' && c <= '\u2064')
                || c == '\uFEFF'
                || (c >= '\uFFF9' && c <= '\uFFFB');
        }

        /// <summary>文本中是否含不可见字符。</summary>
        public static bool ContainsInvisible(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (var c in text)
                if (IsInvisibleChar(c)) return true;
            return false;
        }

        private static int GetV(Dictionary<int, int> map, int key)
        {
            int value;
            return map.TryGetValue(key, out value) ? value : -1;
        }

        private static int GetVOrZero(Dictionary<int, int> map, int key)
        {
            int value;
            return map.TryGetValue(key, out value) ? value : 0;
        }
    }
}
