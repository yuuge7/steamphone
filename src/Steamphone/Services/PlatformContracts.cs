using Steamphone.Core;

namespace Steamphone.Services;

public sealed record InstalledApp(string PackageName, string DisplayName, byte[] IconBytes, bool IsSystem)
{
    public ImageSource Icon => ImageSource.FromStream(() => new MemoryStream(IconBytes, false));
}

public interface IDeviceServices
{
    bool HasUsageAccess { get; }
    bool BatteryOptimized { get; }
    bool NotificationsAllowed { get; }
    Task<IReadOnlyList<InstalledApp>> GetInstalledAppsAsync(CancellationToken cancellation);
    string? GetForegroundPackage();
    void OpenUsageSettings();
    void OpenBatterySettings();
    void OpenNotificationSettings();
}

public interface IMonitoringController
{
    Task StartAsync();
    Task StopAsync();
}

public sealed record MonitorSnapshot(bool Running = false, string? CurrentApp = null,
    string Message = "Monitorizarea este oprită", bool UsageAccess = false);

public sealed class MonitorState
{
    private readonly object sync = new();
    private MonitorSnapshot snapshot = new();
    public MonitorSnapshot Snapshot { get { lock (sync) return snapshot; } }
    public event Action<MonitorSnapshot>? Changed;
    public void Update(Func<MonitorSnapshot, MonitorSnapshot> update)
    {
        MonitorSnapshot value;
        lock (sync) snapshot = value = update(snapshot);
        Changed?.Invoke(value);
    }
}
