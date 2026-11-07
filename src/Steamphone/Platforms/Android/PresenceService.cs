using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using Microsoft.Extensions.DependencyInjection;
using Steamphone.Services;

namespace Steamphone.Platforms.Android;

[Service(Name = "com.steamphone.app.PresenceService", Exported = false,
    ForegroundServiceType = ForegroundService.TypeSpecialUse)]
public sealed class PresenceService : Service
{
    public const string StopAction = "com.steamphone.app.STOP";
    private const string ChannelId = "steam_presence";
    private const int NotificationId = 401;
    private CancellationTokenSource? cancellation;
    private Task? running;
    private PowerManager.WakeLock? wakeLock;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == StopAction)
        {
            cancellation?.Cancel();
            if (running is null) StopSelf();
            return StartCommandResult.NotSticky;
        }
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(ChannelId, "Prezență Steam", NotificationImportance.Low)
        { Description = "Conexiune Steam și monitorizare aplicații", LockscreenVisibility = NotificationVisibility.Private });
        var notification = BuildNotification("Conectare la Steam…");
        if (OperatingSystem.IsAndroidVersionAtLeast(34)) StartForeground(NotificationId, notification, ForegroundService.TypeSpecialUse);
        else StartForeground(NotificationId, notification);
        if (running is { IsCompleted: false }) return StartCommandResult.Sticky;
        cancellation = new CancellationTokenSource();
        var power = (PowerManager)GetSystemService(PowerService)!;
        wakeLock = power.NewWakeLock(WakeLockFlags.Partial, "Steamphone:Presence");
        wakeLock?.SetReferenceCounted(false);
        wakeLock?.Acquire(10 * 60 * 1000L);
        var worker = IPlatformApplication.Current!.Services.GetRequiredService<MonitoringWorker>();
        running = Task.Run(async () =>
        {
            var renewAt = SystemClock.ElapsedRealtime() + 5 * 60 * 1000L;
            string? lastText = null;
            try
            {
                await worker.RunAsync(message =>
                {
                    // Renew a bounded lease while the user-started foreground service remains alive.
                    if (SystemClock.ElapsedRealtime() >= renewAt)
                    {
                        wakeLock?.Acquire(10 * 60 * 1000L);
                        renewAt = SystemClock.ElapsedRealtime() + 5 * 60 * 1000L;
                    }
                    if (lastText != message) manager.Notify(NotificationId, BuildNotification(message));
                    lastText = message;
                }, cancellation.Token);
            }
            finally
            {
                if (wakeLock?.IsHeld == true) wakeLock.Release();
                MainThread.BeginInvokeOnMainThread(() => { StopForeground(StopForegroundFlags.Remove); StopSelf(); });
            }
        });
        return StartCommandResult.Sticky;
    }

    private Notification BuildNotification(string text)
    {
        var open = new Intent(this, typeof(MainActivity)).AddFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        var pendingOpen = PendingIntent.GetActivity(this, 0, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var stop = new Intent(this, typeof(PresenceService)).SetAction(StopAction);
        var pendingStop = PendingIntent.GetService(this, 1, stop, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        using var builder = new NotificationCompat.Builder(this, ChannelId);
        builder.SetSmallIcon(Resource.Drawable.presence_notification);
        builder.SetContentTitle("Steamphone");
        builder.SetContentText(text);
        builder.SetStyle(new NotificationCompat.BigTextStyle().BigText(text));
        builder.SetContentIntent(pendingOpen);
        builder.SetOngoing(true);
        builder.SetOnlyAlertOnce(true);
        builder.SetVisibility(NotificationCompat.VisibilityPrivate);
        builder.AddAction(0, "Oprește", pendingStop);
        return builder.Build() ?? throw new InvalidOperationException("Notificarea nu a putut fi creată.");
    }

    public override void OnDestroy()
    {
        cancellation?.Cancel();
        if (wakeLock?.IsHeld == true) wakeLock.Release();
        base.OnDestroy();
    }
}
