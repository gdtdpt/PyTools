using System;
using System.IO;

namespace PyTools.Core
{
    /// <summary>
    /// 落盘日志：exe 旁 logs\pytools-yyyyMMdd.log，按天滚动，保留 7 天。
    /// M1 强制打点：热键注册成败、呼出/收起耗时、前台恢复每级尝试与结果。
    /// </summary>
    public static class Logger
    {
        private static readonly object Gate = new object();
        private static bool _debug;
        private static bool _broken; // 日志初始化失败后静默降级，绝不因日志异常崩溃

        public static string Directory { get; private set; }

        public static void Init(string baseDirectory, bool debug)
        {
            _debug = debug;
            try
            {
                Directory = Path.Combine(baseDirectory, "logs");
                System.IO.Directory.CreateDirectory(Directory);
                CleanupOld();
                Info("logger initialized, debug=" + debug);
            }
            catch
            {
                _broken = true;
            }
        }

        public static void Info(string message) { Write("INFO", message); }

        public static void Debug(string message)
        {
            if (_debug) Write("DEBUG", message);
        }

        public static void Warn(string message) { Write("WARN", message); }

        public static void Error(string message) { Write("ERROR", message); }

        private static void Write(string level, string message)
        {
            if (_broken || string.IsNullOrEmpty(Directory)) return;
            try
            {
                lock (Gate)
                {
                    var path = Path.Combine(Directory, "pytools-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                    var line = DateTime.Now.ToString("HH:mm:ss.fff") + " [" + level + "] " + message;
                    File.AppendAllText(path, line + Environment.NewLine);
                }
            }
            catch
            {
                // 写日志失败不抛出
            }
        }

        private static void CleanupOld()
        {
            try
            {
                var cutoff = DateTime.Now.AddDays(-7);
                foreach (var file in System.IO.Directory.GetFiles(Directory, "pytools-*.log"))
                {
                    if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
                }
            }
            catch
            {
                // 清理失败不影响运行
            }
        }
    }
}
