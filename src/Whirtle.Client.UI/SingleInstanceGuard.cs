// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

namespace Whirtle.Client.UI;

/// <summary>
/// Ensures only one Whirtle process runs at a time. The second instance signals
/// the first via a named event, then exits.
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = "Whirtle_SingleInstance";
    private const string EventName = "Whirtle_Activate";

    private readonly Mutex           _mutex;
    private readonly EventWaitHandle _event;
    private readonly Thread          _listenerThread;
    private volatile bool            _disposed;

    public event Action? ActivationRequested;

    private SingleInstanceGuard(Mutex mutex, EventWaitHandle evt)
    {
        _mutex = mutex;
        _event = evt;
        _listenerThread = new Thread(ListenerLoop) { IsBackground = true };
        _listenerThread.Start();
    }

    /// <summary>
    /// Returns true and sets <paramref name="guard"/> if this is the first instance.
    /// Returns false (and a null guard) if another instance is already running;
    /// the caller should exit immediately.
    /// </summary>
    public static bool TryBecomePrimary(out SingleInstanceGuard? guard)
    {
        var mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            try
            {
                using var activateEvent = EventWaitHandle.OpenExisting(EventName);
                activateEvent.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
            mutex.Dispose();
            guard = null;
            return false;
        }

        var evt = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        guard = new SingleInstanceGuard(mutex, evt);
        return true;
    }

    private void ListenerLoop()
    {
        while (!_disposed)
        {
            try
            {
                if (_event.WaitOne(500))
                    ActivationRequested?.Invoke();
            }
            catch (ObjectDisposedException) { break; }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _event.Set();   // wake listener thread so it exits promptly
        _event.Dispose();
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
    }
}
