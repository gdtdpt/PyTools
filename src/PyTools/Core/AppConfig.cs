using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Web.Script.Serialization;

namespace PyTools.Core
{
    /// <summary>
    /// 配置：exe 旁 config.json，键名与 v1 完全兼容（同一个文件两版都能读写）。
    /// v2 新增键使用 "v2." 前缀；写回时保留全部未知键，避免破坏 v1 的数据。
    /// </summary>
    public sealed class AppConfig
    {
        // ---- v1 沿用键 ----
        public string Hotkey = "Ctrl+Shift+Space";
        public bool AutoStart = true;
        public bool ClipboardHotkeyEnabled = false;
        // M3：默认留空（不预设键位）。config.json 里已有的值原样读取，不迁移不改判——键位是用户数据，
        // 哪怕来自旧版本的默认值也让用户自己判断（2026-10-30 用户拍板）。空串 = 未设置、不注册。
        public string ClipboardHotkey = "";
        public List<string> AppDirs = new List<string>();
        public bool AddAppContextMenu = false;
        public double BoundsX = double.NaN, BoundsY = double.NaN, BoundsWidth = 640, BoundsHeight = 420;
        // 呼出位置记忆（2026-11-13 改为**每屏独立**）：键 = ScreenInfo.Key（设备名@整屏物理尺寸），
        // 值 = 相对**该屏**工作区原点的偏移（DIP）。全局单一偏移在两块屏工作区宽度不同时必然错位：
        // 小屏会把大屏的左边距原样搬过来，右侧留白被挤掉（rc8 双屏真机反馈）。
        private readonly Dictionary<string, Point> _summonOffsets = new Dictionary<string, Point>();

        /// <summary>偏移字段的坐标语义版本（3 = 每屏独立；2 及更早 = 全局单一，跨屏错位，一律作废）。</summary>
        public const int SummonOffsetVersion = 3;

        // ---- v2 新增键（v2. 前缀）----
        public bool LiteMode = false;
        public bool PinyinSearch = true;   // 拼音首字母搜索（M2a，L7）
        public int PageStep = 5;           // Ctrl+N/P 步进条数（M2a，L6）
        public bool ClipboardPrivacyFilter = true; // 剪贴板隐私格式过滤（M3）
        public string EncodeFormat = "url";        // 编码转换记住上次格式（M4，2026-11-02 用户拍板）
        public string EncodeDirection = "encode";  // 编码转换记住上次方向（M4）
        public string Theme = "light";             // 主题：light / dark / system（启动读一次注册表，不实时监听，2026-11-05 拍板）

        private readonly string _path;
        private Dictionary<string, object> _raw = new Dictionary<string, object>();
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();

        private AppConfig(string path) { _path = path; }

        public string FilePath { get { return _path; } }

        public static AppConfig Load(string path)
        {
            var cfg = new AppConfig(path);
            try
            {
                var json = File.ReadAllText(path);
                cfg._raw = cfg._serializer.Deserialize<Dictionary<string, object>>(json) ?? new Dictionary<string, object>();
            }
            catch
            {
                cfg._raw = new Dictionary<string, object>(); // 首次运行 / 文件损坏 → 默认值
            }
            cfg.MapFromRaw();
            return cfg;
        }

        private void MapFromRaw()
        {
            Hotkey = GetString("hotkey", Hotkey);
            AutoStart = GetBool("autoStart", AutoStart);
            ClipboardHotkeyEnabled = GetBool("clipboardHotkeyEnabled", ClipboardHotkeyEnabled);
            ClipboardHotkey = GetString("clipboardHotkey", ClipboardHotkey);
            AddAppContextMenu = GetBool("addAppContextMenu", AddAppContextMenu);
            LiteMode = GetBool("v2.liteMode", LiteMode);
            PinyinSearch = GetBool("v2.pinyinSearch", PinyinSearch);
            PageStep = (int)GetNumber(_raw, "v2.pageStep", PageStep);
            if (PageStep < 1) PageStep = 1;
            ClipboardPrivacyFilter = GetBool("v2.clipboardPrivacyFilter", ClipboardPrivacyFilter);
            EncodeFormat = GetString("v2.encodeFormat", EncodeFormat);
            EncodeDirection = GetString("v2.encodeDirection", EncodeDirection);
            Theme = GetString("v2.theme", Theme);
            if (Theme != "dark" && Theme != "system") Theme = "light"; // 宽容：未知值回浅色
            // 偏移只在语义版本匹配时信任（2026-11-12/13）：rc5 时代以错误坐标语义落盘的旧偏移、
            // 以及版本 2 的「全局单一偏移」都必须作废——无版本标记/版本不符一律按无记忆处理，
            // 下次呼出走默认位后重新落盘。
            if (GetNumber(_raw, "v2.summonOffsetVer", 0) == SummonOffsetVersion) LoadSummonOffsets();
            else _summonOffsets.Clear();

            var bounds = _raw.ContainsKey("windowBounds") ? _raw["windowBounds"] as Dictionary<string, object> : null;
            if (bounds != null)
            {
                BoundsX = GetNumber(bounds, "x", BoundsX);
                BoundsY = GetNumber(bounds, "y", BoundsY);
                BoundsWidth = GetNumber(bounds, "width", BoundsWidth);
                BoundsHeight = GetNumber(bounds, "height", BoundsHeight);
            }
        }

        /// <summary>保存位置（每次收起时调用）。未知键原样保留。</summary>
        public void SaveBounds(double x, double y, double width, double height)
        {
            BoundsX = x; BoundsY = y; BoundsWidth = width; BoundsHeight = height;
            try
            {
                _raw["windowBounds"] = new Dictionary<string, object>
                {
                    { "x", x }, { "y", y }, { "width", width }, { "height", height }
                };
                WriteBack();
            }
            catch (Exception ex) { Logger.Error("save bounds failed: " + ex.Message); }
        }

        /// <summary>清除全部位置记忆（下次呼出走默认位：目标屏内水平居中、垂直上 1/3）。设置窗口「恢复默认位置」调用。</summary>
        public void ResetBounds()
        {
            BoundsX = double.NaN; BoundsY = double.NaN;
            _summonOffsets.Clear();
            try
            {
                _raw.Remove("windowBounds");
                _raw.Remove("v2.summonOffsets");
                _raw.Remove("v2.summonOffsetX");
                _raw.Remove("v2.summonOffsetY");
                _raw.Remove("v2.summonOffsetVer");
                WriteBack();
            }
            catch (Exception ex) { Logger.Error("reset bounds failed: " + ex.Message); }
        }

        /// <summary>取某块屏的记忆偏移（DIP，相对该屏工作区原点）。无记忆 = false → 调用方走默认位。</summary>
        public bool TryGetSummonOffset(string screenKey, out double offsetX, out double offsetY)
        {
            offsetX = double.NaN; offsetY = double.NaN;
            if (string.IsNullOrEmpty(screenKey)) return false;
            Point p;
            if (!_summonOffsets.TryGetValue(screenKey, out p)) return false;
            offsetX = p.X; offsetY = p.Y;
            return true;
        }

        /// <summary>已记忆的屏数（自测/诊断用）。</summary>
        public int SummonOffsetCount { get { return _summonOffsets.Count; } }

        /// <summary>写下某块屏的记忆偏移（DIP）。只覆盖这一块屏的条目，其它屏互不影响。</summary>
        public void SaveSummonOffset(string screenKey, double offsetX, double offsetY)
        {
            if (string.IsNullOrEmpty(screenKey) || double.IsNaN(offsetX) || double.IsNaN(offsetY)) return;
            _summonOffsets[screenKey] = new Point(offsetX, offsetY);
            try { WriteBack(); }
            catch (Exception ex) { Logger.Error("save summon offset failed: " + ex.Message); }
        }

        private void LoadSummonOffsets()
        {
            _summonOffsets.Clear();
            object node;
            if (!_raw.TryGetValue("v2.summonOffsets", out node)) return;
            // JavaScriptSerializer 把 JSON 对象反序列化成 Dictionary<string,object>（数组才是 ArrayList，
            // 所以下面逐键存 {x,y} 对象而不是 [x,y]，避免踩 ArrayList 的坑）
            var map = node as Dictionary<string, object>;
            if (map == null) return;
            foreach (var kv in map)
            {
                var one = kv.Value as Dictionary<string, object>;
                if (one == null) continue;
                var x = GetNumber(one, "x", double.NaN);
                var y = GetNumber(one, "y", double.NaN);
                if (double.IsNaN(x) || double.IsNaN(y)) continue;
                _summonOffsets[kv.Key] = new Point(x, y);
            }
        }

        public void SaveLiteMode(bool lite)
        {
            LiteMode = lite;
            try { _raw["v2.liteMode"] = lite; WriteBack(); }
            catch (Exception ex) { Logger.Error("save liteMode failed: " + ex.Message); }
        }

        /// <summary>主题三态（设置窗口点击即热切换，App.ApplyTheme 负责重挂字典）。</summary>
        public void SaveTheme(string theme)
        {
            if (theme != "dark" && theme != "system") theme = "light";
            Theme = theme;
            try { _raw["v2.theme"] = theme; WriteBack(); }
            catch (Exception ex) { Logger.Error("save theme failed: " + ex.Message); }
        }

        /// <summary>编码转换记住上次格式/方向（EncodeView 选项变更时调用，M4）。</summary>
        public void SaveEncodeChoice(string format, string direction)
        {
            if (format != EncodeFormat || direction != EncodeDirection)
            {
                EncodeFormat = format;
                EncodeDirection = direction;
            }
            try { _raw["v2.encodeFormat"] = EncodeFormat; _raw["v2.encodeDirection"] = EncodeDirection; WriteBack(); }
            catch (Exception ex) { Logger.Error("save encode choice failed: " + ex.Message); }
        }

        /// <summary>把当前全部字段写回（设置窗口各项统一走这里，M2b）。</summary>
        public void Save()
        {
            try { WriteBack(); }
            catch (Exception ex) { Logger.Error("save config failed: " + ex.Message); }
        }

        private void WriteBack()
        {
            // 已知键同步回 raw（v1 键名），未知键原样保留
            _raw["hotkey"] = Hotkey;
            _raw["autoStart"] = AutoStart;
            _raw["clipboardHotkeyEnabled"] = ClipboardHotkeyEnabled;
            _raw["clipboardHotkey"] = ClipboardHotkey;
            _raw["appDirs"] = AppDirs;
            _raw["addAppContextMenu"] = AddAppContextMenu;
            _raw["v2.liteMode"] = LiteMode;
            _raw["v2.pinyinSearch"] = PinyinSearch;
            _raw["v2.pageStep"] = PageStep;
            _raw["v2.clipboardPrivacyFilter"] = ClipboardPrivacyFilter;
            _raw["v2.encodeFormat"] = EncodeFormat;
            _raw["v2.encodeDirection"] = EncodeDirection;
            _raw["v2.theme"] = Theme;
            // 每屏位置记忆：无记忆时不写键（键缺失 = 读回空字典）。旧版「全局单一偏移」的扁平键
            // 一律清除——留着只会让后续版本或 v1 读到一个语义已废的坐标。
            _raw.Remove("v2.summonOffsetX");
            _raw.Remove("v2.summonOffsetY");
            if (_summonOffsets.Count == 0)
            {
                _raw.Remove("v2.summonOffsets");
                _raw.Remove("v2.summonOffsetVer");
            }
            else
            {
                var offsets = new Dictionary<string, object>();
                foreach (var kv in _summonOffsets)
                    offsets[kv.Key] = new Dictionary<string, object> { { "x", kv.Value.X }, { "y", kv.Value.Y } };
                _raw["v2.summonOffsets"] = offsets;
                _raw["v2.summonOffsetVer"] = SummonOffsetVersion;
            }
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            File.WriteAllText(_path, _serializer.Serialize(_raw));
        }

        // ---- 取值辅助（宽容处理类型）----
        private string GetString(string key, string fallback)
        {
            object v; if (!_raw.TryGetValue(key, out v) || v == null) return fallback;
            return v.ToString();
        }

        private bool GetBool(string key, bool fallback)
        {
            object v; if (!_raw.TryGetValue(key, out v) || v == null) return fallback;
            if (v is bool) return (bool)v;
            bool parsed; if (bool.TryParse(v.ToString(), out parsed)) return parsed;
            return fallback;
        }

        private static double GetNumber(Dictionary<string, object> source, string key, double fallback)
        {
            object v; if (!source.TryGetValue(key, out v) || v == null) return fallback;
            try { return Convert.ToDouble(v); }
            catch { return fallback; }
        }
    }
}
