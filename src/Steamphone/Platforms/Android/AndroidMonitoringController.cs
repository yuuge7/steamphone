using Android.Content;
using AndroidX.Core.Content;
using Steamphone.Services;
using Application = Android.App.Application;

namespace Steamphone.Platforms.Android;

public sealed class AndroidMonitoringController(MonitorState state) : IMonitoringController
{
    public async Task StartAsync()
    {
        if (state.Snapshot.Running) return;
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            await Permissions.RequestAsync<Permissions.PostNotifications>();
        var intent = new Intent(Application.Context, typeof(PresenceService));
        ContextCompat.StartForegroundService(Application.Context, intent);
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!state.Snapshot.Running && DateTime.UtcNow < deadline) await Task.Delay(100);
        if (!state.Snapshot.Running) throw new InvalidOperationException("Android nu a pornit serviciul. Redeschide aplicația și încearcă din nou.");
    }

    public async Task StopAsync()
    {
        if (!state.Snapshot.Running) return;
        var intent = new Intent(Application.Context, typeof(PresenceService)).SetAction(PresenceService.StopAction);
        Application.Context.StartService(intent);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (state.Snapshot.Running && DateTime.UtcNow < deadline) await Task.Delay(100);
        if (state.Snapshot.Running) throw new InvalidOperationException("Serviciul încă se oprește. Reîncearcă în câteva secunde.");
    }
}
