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
        public string ClipboardHotkey = "Alt+B";
        public List<string> AppDirs = new List<string>();
        public bool AddAppContextMenu = false;
        public double BoundsX = double.NaN, BoundsY = double.NaN, BoundsWidth = 640, BoundsHeight = 420;

        // ---- v2 新增键（v2. 前缀）----
        public bool LiteMode = false;

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

        public void SaveLiteMode(bool lite)
        {
            LiteMode = lite;
            try { _raw["v2.liteMode"] = lite; WriteBack(); }
            catch (Exception ex) { Logger.Error("save liteMode failed: " + ex.Message); }
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
