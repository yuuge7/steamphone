using Android.App;
using Android.App.Usage;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Steamphone.Core;
using Steamphone.Services;
using Application = Android.App.Application;

namespace Steamphone.Platforms.Android;

public sealed class AndroidDeviceServices : IDeviceServices
{
    private readonly Context context = Application.Context;
    private readonly ForegroundState foreground = new();
    private long cursor;

    public bool HasUsageAccess
    {
        get
        {
            var manager = (AppOpsManager)context.GetSystemService(Context.AppOpsService)!;
            return manager.CheckOpNoThrow(AppOpsManager.OpstrGetUsageStats!, global::Android.OS.Process.MyUid(), context.PackageName!) == AppOpsManagerMode.Allowed;
        }
    }
    public bool BatteryOptimized => !((PowerManager)context.GetSystemService(Context.PowerService)!)
        .IsIgnoringBatteryOptimizations(context.PackageName!);
    public bool NotificationsAllowed => ((NotificationManager)context.GetSystemService(Context.NotificationService)!).AreNotificationsEnabled();

    public Task<IReadOnlyList<InstalledApp>> GetInstalledAppsAsync(CancellationToken cancellation) => Task.Run<IReadOnlyList<InstalledApp>>(() =>
    {
        var manager = context.PackageManager!;
        var result = new List<InstalledApp>();
        // QUERY_ALL_PACKAGES is intentional: show installed system apps as well as launcher apps.
        foreach (var application in manager.GetInstalledApplications(PackageInfoFlags.MetaData))
        {
            cancellation.ThrowIfCancellationRequested();
            if (application.PackageName == context.PackageName) continue;
            try
            {
                var label = manager.GetApplicationLabel(application)?.ToString();
                var packageName = application.PackageName!;
                using var drawable = manager.GetApplicationIcon(application);
                using var bitmap = Bitmap.CreateBitmap(72, 72, Bitmap.Config.Argb8888!)!;
                using (var canvas = new Canvas(bitmap))
                {
                    drawable?.SetBounds(0, 0, 72, 72);
                    drawable?.Draw(canvas);
                }
                using var stream = new MemoryStream();
                bitmap.Compress(Bitmap.CompressFormat.Png!, 100, stream);
                result.Add(new InstalledApp(packageName, string.IsNullOrWhiteSpace(label) ? packageName : label,
                    stream.ToArray(), (application.Flags & ApplicationInfoFlags.System) != 0));
            }
            catch (PackageManager.NameNotFoundException) { } // The package may be uninstalled while scanning.
        }
        return result.OrderBy(x => x.IsSystem).ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }, cancellation);

    public string? GetForegroundPackage()
    {
        if (!HasUsageAccess) { foreground.Clear(); cursor = 0; return null; }
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // Incremental ordered events; a last-used UsageStats aggregate cannot reliably detect pause.
        var start = cursor == 0 || now < cursor ? now - (long)TimeSpan.FromHours(24).TotalMilliseconds : cursor;
        var usage = (UsageStatsManager)context.GetSystemService(Context.UsageStatsService)!;
        using var events = usage.QueryEvents(start, now);
        using var item = new UsageEvents.Event();
        while (events?.HasNextEvent == true)
        {
            events.GetNextEvent(item);
            var type = (int)item.EventType;
            if (type == 1 && item.PackageName is { } resumed) foreground.Resume(resumed, item.ClassName);
            else if (type == 2 && item.PackageName is { } paused) foreground.Pause(paused, item.ClassName);
            else if (type is 16 or 17 or 26) foreground.Clear(); // Screen off, keyguard shown, device shutdown.
        }
        cursor = now;
        var power = (PowerManager)context.GetSystemService(Context.PowerService)!;
        var keyguard = (KeyguardManager)context.GetSystemService(Context.KeyguardService)!;
        return power.IsInteractive && !keyguard.IsKeyguardLocked ? foreground.PackageName : null;
    }

    private void Open(string action, global::Android.Net.Uri? data = null)
    {
        var intent = new Intent(action);
        if (data is not null) intent.SetData(data);
        intent.AddFlags(ActivityFlags.NewTask);
        context.StartActivity(intent);
    }
    public void OpenUsageSettings()
    {
        try { Open(Settings.ActionUsageAccessSettings, global::Android.Net.Uri.Parse($"package:{context.PackageName}")); }
        catch (ActivityNotFoundException) { Open(Settings.ActionUsageAccessSettings); }
    }
    public void OpenBatterySettings() => Open(Settings.ActionIgnoreBatteryOptimizationSettings);
    public void OpenNotificationSettings()
    {
        var intent = new Intent(Settings.ActionAppNotificationSettings);
        intent.PutExtra(Settings.ExtraAppPackage, context.PackageName);
        intent.AddFlags(ActivityFlags.NewTask);
        context.StartActivity(intent);
    }
}
