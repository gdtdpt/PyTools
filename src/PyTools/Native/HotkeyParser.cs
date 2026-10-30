using System;
using System.Collections.Generic;

namespace PyTools.Native
{
    /// <summary>
    /// 热键配置串解析："Ctrl+Shift+Space" → 修饰符 + 虚拟键。
    /// 修饰符：Ctrl/Control、Alt、Shift、Win/Windows（大小写不敏感，可任意组合）。
    /// 主键：字母 A-Z、数字 0-9、F1-F24、Space、Tab、Enter、Backspace、
    ///       Insert、Delete、Home、End、PgUp、PgDn、方向键、标点（;=,-./`[ ]\ '）。
    /// </summary>
    public static class HotkeyParser
    {
        private static readonly Dictionary<string, uint> Modifiers = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            { "Ctrl", NativeMethods.ModControl }, { "Control", NativeMethods.ModControl },
            { "Alt", NativeMethods.ModAlt },
            { "Shift", NativeMethods.ModShift },
            { "Win", NativeMethods.ModWin }, { "Windows", NativeMethods.ModWin }
        };

        private static readonly Dictionary<string, uint> NamedKeys = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            { "Space", 0x20 }, { "Tab", 0x09 }, { "Enter", 0x0D }, { "Return", 0x0D },
            { "Backspace", 0x08 }, { "Back", 0x08 },
            { "Insert", 0x2D }, { "Delete", 0x2E }, { "Del", 0x2E },
            { "Home", 0x24 }, { "End", 0x23 },
            { "PgUp", 0x21 }, { "PageUp", 0x21 },
            { "PgDn", 0x22 }, { "PageDown", 0x22 },
            { "Left", 0x25 }, { "Up", 0x26 }, { "Right", 0x27 }, { "Down", 0x28 },
            { "OemSemicolon", 0xBA }, { "OemPlus", 0xBB }, { "OemComma", 0xBC }, { "OemMinus", 0xBD },
            { "OemPeriod", 0xBE }, { "OemQuestion", 0xBF }, { "OemTilde", 0xC0 },
            { "OemOpenBrackets", 0xDB }, { "OemPipe", 0xDC }, { "OemCloseBrackets", 0xDD }, { "OemQuotes", 0xDE }
        };

        public static bool TryParse(string text, out uint modifiers, out uint virtualKey, out string error)
        {
            modifiers = 0; virtualKey = 0; error = null;
            if (string.IsNullOrWhiteSpace(text)) { error = "热键串为空"; return false; }

            var parts = text.Trim().Split('+');
            if (parts.Length < 2) { error = "热键至少需要修饰符 + 主键：" + text; return false; }

            for (var i = 0; i < parts.Length - 1; i++)
            {
                uint mod;
                if (!Modifiers.TryGetValue(parts[i].Trim(), out mod)) { error = "未知修饰符：" + parts[i]; return false; }
                modifiers |= mod;
            }

            var key = parts[parts.Length - 1].Trim();
            if (key.Length == 1)
            {
                var c = char.ToUpperInvariant(key[0]);
                if (c >= 'A' && c <= 'Z') { virtualKey = (uint)c; return true; }
                if (c >= '0' && c <= '9') { virtualKey = (uint)c; return true; }
                if (key == ";" || key == "=" || key == "," || key == "-" || key == "." || key == "/" ||
                    key == "`" || key == "[" || key == "]" || key == "\\" || key == "'")
                {
                    // 标点走 NamedKeys 的 Oem 名，裸符号不支持（避免歧义）
                    error = "标点主键请用名称（如 OemSemicolon）：" + key;
                    return false;
                }
                error = "无法识别的主键：" + key;
                return false;
            }

            if (key.Length == 2 && (key[0] == 'F' || key[0] == 'f'))
            {
                int fn;
                if (int.TryParse(key.Substring(1), out fn) && fn >= 1 && fn <= 24)
                {
                    virtualKey = (uint)(0x70 + fn - 1);
                    return true;
                }
            }

            uint named;
            if (NamedKeys.TryGetValue(key, out named)) { virtualKey = named; return true; }

            error = "无法识别的主键：" + key;
            return false;
        }
    }
}
