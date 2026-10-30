using System;
using System.IO;
using System.Threading;

namespace PyTools.Core
{
    /// <summary>
    /// 单实例：命名 Mutex + EventWaitHandle 唤起信号。
    /// 第二实例：Set 信号后退出；首实例收到信号呼出窗口（与 v1 二次启动唤起语义一致）。
    /// --add-app 转发（M2b）：payload 走 exe 旁 add-app.msg 文件 + AddAppSignal 信号；
    /// 不用命令行转发（v1 教训：进程间 argv 会被重排，自包含 token 也不如文件稳）。
    /// </summary>
    public sealed class SingleInstance : IDisposable
    {
        public const string MutexName = @"Local\PyTools.V2.SingleInstance";
        public const string ShowSignalName = @"Local\PyTools.V2.ShowSignal";
        public const string AddAppSignalName = @"Local\PyTools.V2.AddAppSignal";
        public const string AddAppFileName = "add-app.msg";

        private Mutex _mutex;
        private EventWaitHandle _signal;

        /// <summary>尝试成为唯一实例。false 表示已有实例在运行。</summary>
        public bool TryAcquire()
        {
            _mutex = new Mutex(true, MutexName, out var createdNew);
            if (!createdNew)
            {
                _mutex.Dispose(); _mutex = null;
                return false;
            }
            _signal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
            return true;
        }

        /// <summary>在 UI 线程上监听唤起信号。</summary>
        public void ListenShow(SynchronizationContext ui, Action onShow)
        {
            if (_signal == null) throw new InvalidOperationException("TryAcquire first");
            ThreadPool.RegisterWaitForSingleObject(
                _signal,
                (state, timedOut) => ui.Post(_ => onShow(), null),
                null,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }

        /// <summary>在 UI 线程上监听静默添加信号（M2b）。</summary>
        public void ListenAddApp(SynchronizationContext ui, Action<string> onAddApp)
        {
            EventWaitHandle signal;
            if (!EventWaitHandle.TryOpenExisting(AddAppSignalName, out signal))
            {
                signal = new EventWaitHandle(false, EventResetMode.AutoReset, AddAppSignalName);
            }
            ThreadPool.RegisterWaitForSingleObject(
                signal,
                (state, timedOut) =>
                {
                    var path = ReadAddAppMessage();
                    if (path != null) ui.Post(_ => onAddApp(path), null);
                },
                null,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }

        /// <summary>第二实例调用：写入 payload 并通知首实例登记，然后自行退出。</summary>
        public static bool SignalAddApp(string target)
        {
            try
            {
                var file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, AddAppFileName);
                File.WriteAllText(file, target ?? "");
            }
            catch { return false; }
            EventWaitHandle signal;
            if (EventWaitHandle.TryOpenExisting(AddAppSignalName, out signal))
            {
                try { signal.Set(); return true; }
                finally { signal.Dispose(); }
            }
            return false;
        }

        /// <summary>首实例读取并删除 payload；无消息返回 null。</summary>
        private static string ReadAddAppMessage()
        {
            try
            {
                var file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, AddAppFileName);
                if (!File.Exists(file)) return null;
                var text = File.ReadAllText(file);
                File.Delete(file);
                return text;
            }
            catch { return null; }
        }

        /// <summary>第二实例调用：通知已有实例呼出窗口。</summary>
        public static bool SignalExisting()
        {
            EventWaitHandle existing;
            if (EventWaitHandle.TryOpenExisting(ShowSignalName, out existing))
            {
                try { existing.Set(); return true; }
                finally { existing.Dispose(); }
            }
            return false;
        }

        public void Dispose()
        {
            try
            {
                if (_mutex != null) { _mutex.ReleaseMutex(); _mutex.Dispose(); _mutex = null; }
                if (_signal != null) { _signal.Dispose(); _signal = null; }
            }
            catch { /* 退出路径尽力而为 */ }
        }
    }
}
