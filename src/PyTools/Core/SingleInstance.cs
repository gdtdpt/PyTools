using System;
using System.Threading;

namespace PyTools.Core
{
    /// <summary>
    /// 单实例：命名 Mutex + EventWaitHandle 唤起信号。
    /// 第二实例：Set 信号后退出；首实例收到信号呼出窗口（与 v1 二次启动唤起语义一致）。
    /// </summary>
    public sealed class SingleInstance : IDisposable
    {
        public const string MutexName = @"Local\PyTools.V2.SingleInstance";
        public const string ShowSignalName = @"Local\PyTools.V2.ShowSignal";

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
