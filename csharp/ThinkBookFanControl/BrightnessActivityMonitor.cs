using System;
using System.Management;

namespace ThinkBookFanControl;

public sealed class BrightnessActivityMonitor : IDisposable
{
    private ManagementEventWatcher? _watcher;

    public DateTimeOffset? LastActivity { get; private set; }

    public BrightnessActivityMonitor()
    {
        try
        {
            _watcher = new ManagementEventWatcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessEvent");
            _watcher.EventArrived += (_, _) => LastActivity = DateTimeOffset.Now;
            _watcher.Start();
        }
        catch
        {
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    public bool IsActive(TimeSpan quietPeriod)
    {
        return LastActivity is DateTimeOffset lastActivity && DateTimeOffset.Now - lastActivity < quietPeriod;
    }

    public TimeSpan RemainingQuietDelay(TimeSpan quietPeriod)
    {
        if (LastActivity is not DateTimeOffset lastActivity)
            return TimeSpan.Zero;

        var remaining = quietPeriod - (DateTimeOffset.Now - lastActivity);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    public void Dispose()
    {
        if (_watcher is null)
            return;

        try { _watcher.Stop(); } catch { }
        _watcher.Dispose();
    }
}
