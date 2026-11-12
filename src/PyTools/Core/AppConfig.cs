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
        // 呼出位置记忆 v2（2026-11-09 跟随鼠标所在屏）：相对「其所在屏工作区原点」的偏移（DIP）。
        // NaN = 旧配置尚未迁移，呼出时按旧绝对坐标口径定位一次，首次 SavePosition 后即写入新字段。
        public double SummonOffsetX = double.NaN, SummonOffsetY = double.NaN;

        /// <summary>偏移字段的坐标语义版本（2 = 相对所在屏工作区原点的 DIP）。版本不符的旧值一律作废。</summary>
        public const int SummonOffsetVersion = 2;

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
            // 偏移只在语义版本匹配时信任（2026-11-12）：rc5 时代以错误坐标语义落盘的旧偏移
            // 必须作废——无版本标记/版本不符一律按未迁移处理，下次呼出走默认位后重新落盘
            if (GetNumber(_raw, "v2.summonOffsetVer", 0) == SummonOffsetVersion)
            {
                SummonOffsetX = GetNumber(_raw, "v2.summonOffsetX", SummonOffsetX);
                SummonOffsetY = GetNumber(_raw, "v2.summonOffsetY", SummonOffsetY);
            }
            else
            {
                SummonOffsetX = double.NaN;
                SummonOffsetY = double.NaN;
            }

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

        /// <summary>清除位置记忆（下次呼出走默认位：目标屏内水平居中、垂直上 1/3）。设置窗口「恢复默认位置」调用。</summary>
        public void ResetBounds()
        {
            BoundsX = double.NaN; BoundsY = double.NaN;
            SummonOffsetX = double.NaN; SummonOffsetY = double.NaN;
            try
            {
                _raw.Remove("windowBounds");
                _raw.Remove("v2.summonOffsetX");
                _raw.Remove("v2.summonOffsetY");
                _raw.Remove("v2.summonOffsetVer");
                WriteBack();
            }
            catch (Exception ex) { Logger.Error("reset bounds failed: " + ex.Message); }
        }

        /// <summary>呼出位置偏移写入（相对所在屏工作区原点，DIP）。NaN = 清除记忆。</summary>
        public void SaveSummonOffset(double offsetX, double offsetY)
        {
            SummonOffsetX = offsetX; SummonOffsetY = offsetY;
            try
            {
                // JavaScriptSerializer 不支持 NaN，无值时必须删键而不是写 NaN
                if (double.IsNaN(offsetX) || double.IsNaN(offsetY))
                {
                    _raw.Remove("v2.summonOffsetX");
                    _raw.Remove("v2.summonOffsetY");
                    _raw.Remove("v2.summonOffsetVer");
                }
                else
                {
                    _raw["v2.summonOffsetX"] = offsetX;
                    _raw["v2.summonOffsetY"] = offsetY;
                    _raw["v2.summonOffsetVer"] = SummonOffsetVersion;
                }
                WriteBack();
            }
            catch (Exception ex) { Logger.Error("save summon offset failed: " + ex.Message); }
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
            // 偏移可能仍是 NaN（未迁移/被版本检查作废）：JavaScriptSerializer 不支持 NaN，有值才写键（键缺失 = MapFromRaw 读回 NaN）
            if (!double.IsNaN(SummonOffsetX) && !double.IsNaN(SummonOffsetY))
            {
                _raw["v2.summonOffsetX"] = SummonOffsetX;
                _raw["v2.summonOffsetY"] = SummonOffsetY;
                _raw["v2.summonOffsetVer"] = SummonOffsetVersion;
            }
            else
            {
                _raw.Remove("v2.summonOffsetX");
                _raw.Remove("v2.summonOffsetY");
                _raw.Remove("v2.summonOffsetVer");
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
