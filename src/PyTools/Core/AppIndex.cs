using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PyTools.Core
{
    /// <summary>
    /// 启动器索引：扫描编排（后台线程）+ 过滤打分（v1 scoreEntry 语义简化版，仅应用类条目）。
    /// AppsUpdated 在构造时捕获的 SynchronizationContext（UI 线程）上触发。
    /// </summary>
    public sealed class AppIndex
    {
        private readonly string _baseDir;
        private readonly AppConfig _config;
        private readonly SynchronizationContext _ui;
        private List<AppEntry> _apps = new List<AppEntry>();
        private List<string> _roots = new List<string>();

        public CustomApps Custom { get; private set; }
        public IconCache Icons { get; private set; }
        public bool Scanning { get; private set; }

        public event Action AppsUpdated;      // 列表已替换（UI 线程）
        public event Action ScanFinished;     // 含首扫，UI 可用来气泡提示

        public AppIndex(string baseDir, AppConfig config)
        {
            _baseDir = baseDir;
            _config = config;
            _ui = SynchronizationContext.Current ?? new SynchronizationContext();
            Custom = new CustomApps(CustomApps.DefaultPath(baseDir));
            Icons = new IconCache(baseDir);
        }

        public List<AppEntry> Apps { get { return _apps; } }
        public List<string> Roots { get { return _roots; } }
        public bool PinyinEnabled { get { return _config.PinyinSearch && Pinyin.Ready; } }

        public void RescanAsync()
        {
            if (Scanning) return;
            Scanning = true;
            var baseDir = _baseDir;
            var appDirs = _config.AppDirs;
            var pinyinEnabled = PinyinEnabled;
            Task.Run(delegate
            {
                List<AppEntry> scanned;
                List<string> roots;
                try
                {
                    roots = AppScanner.Roots(appDirs, baseDir);
                    scanned = AppScanner.Scan(roots, Custom.Load());
                }
                catch (Exception ex)
                {
                    Logger.Error("scan failed: " + ex.Message);
                    scanned = null;
                    roots = null;
                }
                _ui.Post(delegate
                {
                    Scanning = false;
                    if (scanned != null)
                    {
                        _apps = scanned;
                        _roots = roots;
                    }
                    if (AppsUpdated != null) AppsUpdated();
                    if (ScanFinished != null) ScanFinished();
                }, null);
            });
        }

        /// <summary>
        /// 过滤打分：精确词 rank*10，前缀 rank*10+1，包含 rank*10+2（关键词与首字母串同权）。
        /// 与 v1 一致：空查询返回空列表（呼出时只有搜索框）。
        /// </summary>
        public List<AppEntry> Filter(string query)
        {
            var q = (query ?? "").Trim().ToLowerInvariant();
            var result = new List<AppEntry>();
            if (q.Length == 0) return result;
            var useInitials = PinyinEnabled;
            foreach (var entry in _apps)
            {
                int best = int.MaxValue;
                for (var rank = 0; rank < entry.Keywords.Count; rank++)
                {
                    var term = entry.Keywords[rank];
                    var score = ScoreTerm(term, q, rank);
                    if (score < best) best = score;
                }
                if (useInitials)
                {
                    foreach (var term in entry.Initials)
                    {
                        var score = ScoreTerm(term, q, 1); // 首字母串视同 targetName 档
                        if (score < best) best = score;
                    }
                }
                if (best < int.MaxValue) result.Add(entry);
            }
            result.Sort(delegate(AppEntry a, AppEntry b) { return string.Compare(a.Name, b.Name, StringComparison.CurrentCulture); });
            return result;
        }

        private static int ScoreTerm(string term, string query, int rank)
        {
            if (term == query) return rank * 10;
            if (term.StartsWith(query, StringComparison.Ordinal)) return rank * 10 + 1;
            if (term.Contains(query)) return rank * 10 + 2;
            return int.MaxValue;
        }
    }
}
