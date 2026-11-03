using System;
using System.Collections.Generic;
using System.IO;
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

        // ---- v2 新增键（v2. 前缀）----
        public bool LiteMode = false;
        public bool PinyinSearch = true;   // 拼音首字母搜索（M2a，L7）
        public int PageStep = 5;           // Ctrl+N/P 步进条数（M2a，L6）
        public bool ClipboardPrivacyFilter = true; // 剪贴板隐私格式过滤（M3）
        public string EncodeFormat = "url";        // 编码转换记住上次格式（M4，2026-11-02 用户拍板）
        public string EncodeDirection = "encode";  // 编码转换记住上次方向（M4）

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

        /// <summary>清除位置记忆（下次呼出走默认位：水平居中、垂直上 1/3）。设置窗口「恢复默认位置」调用。</summary>
        public void ResetBounds()
        {
            BoundsX = double.NaN; BoundsY = double.NaN;
            try { _raw.Remove("windowBounds"); WriteBack(); }
            catch (Exception ex) { Logger.Error("reset bounds failed: " + ex.Message); }
        }

        public void SaveLiteMode(bool lite)
        {
            LiteMode = lite;
            try { _raw["v2.liteMode"] = lite; WriteBack(); }
            catch (Exception ex) { Logger.Error("save liteMode failed: " + ex.Message); }
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
