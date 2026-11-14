using System;
using System.Threading;

namespace Gallery.Services;

/// <summary>
/// 单实例：命名 Mutex 保证同一份数据只有一个进程；再启动时通知已运行的实例把窗口拉到前面。
/// 名字里带 scope（默认数据根 or 沙箱路径的散列），所以多个沙箱可以同时跑互不干扰。
/// </summary>
public static class SingleInstance
{
    private static Mutex? _mutex;
    private static EventWaitHandle? _signal;
    private static RegisteredWaitHandle? _wait;
    private static string _eventName = "";

    private static string ScopeOf(string scope)
    {
        // 不能用 string.GetHashCode：.NET 每个进程的随机种子不同，跨进程对不上
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(scope));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }

    /// <summary>true = 我是第一个实例；false = 已有实例在跑（调用方发信号后退出）。</summary>
    public static bool TryAcquire(string scope)
    {
        var id = ScopeOf(scope);
        _eventName = $@"Local\Gallery.SingleInstance.Activate.{id}";
        _mutex = new Mutex(initiallyOwned: true, $@"Local\Gallery.SingleInstance.{id}", out var createdNew);
        if (!createdNew) return false;

        _signal = new EventWaitHandle(false, EventResetMode.AutoReset, _eventName);
        return true;
    }

    public static bool IsRunning(string scope)
    {
        if (!Mutex.TryOpenExisting($@"Local\Gallery.SingleInstance.{ScopeOf(scope)}", out var mutex)) return false;
        mutex.Dispose();
        return true;
    }

    /// <summary>已有实例在跑时调用：把它唤到前台。</summary>
    public static void SignalExisting(string scope)
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting($@"Local\Gallery.SingleInstance.Activate.{ScopeOf(scope)}", out var handle))
            {
                using (handle) handle.Set();
            }
        }
        catch
        {
            // 唤醒失败就当普通退出
        }
    }

    /// <summary>第一个实例在 UI 就绪后调用；每次收到信号就在 UI 线程执行 <paramref name="activate"/>。</summary>
    public static void ListenForActivation(Action activate)
    {
        if (_signal is null) return;
        _wait = ThreadPool.RegisterWaitForSingleObject(
            _signal,
            (_, _) => activate(),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }
}
