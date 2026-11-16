using Microsoft.Extensions.DependencyInjection;
using Steamphone.Core;
using Steamphone.Platforms.Android;
using Steamphone.Services;
using Steamphone.ViewModels;
using Steamphone.Views;

namespace Steamphone;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder().UseMauiApp<App>().ConfigureFonts(fonts =>
        {
            fonts.AddFont("Barlow-Regular.ttf", "Barlow");
            fonts.AddFont("Barlow-Medium.ttf", "BarlowMedium");
            fonts.AddFont("Barlow-SemiBold.ttf", "BarlowSemiBold");
            fonts.AddFont("BarlowSemiCondensed-SemiBold.ttf", "Display");
            fonts.AddFont("JetBrainsMono-Regular.ttf", "Mono");
        });
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("CleanInput", (handler, _) =>
            handler.PlatformView.BackgroundTintList = global::Android.Content.Res.ColorStateList.ValueOf(global::Android.Graphics.Color.Transparent));
        Microsoft.Maui.Handlers.PickerHandler.Mapper.AppendToMapping("CleanInput", (handler, _) =>
            handler.PlatformView.BackgroundTintList = global::Android.Content.Res.ColorStateList.ValueOf(global::Android.Graphics.Color.Transparent));
        Microsoft.Maui.Handlers.SearchBarHandler.Mapper.AppendToMapping("CleanInput", (handler, _) =>
        {
            handler.PlatformView.FindViewById(Resource.Id.search_plate)?.SetBackgroundColor(global::Android.Graphics.Color.Transparent);
            handler.PlatformView.SetBackgroundColor(global::Android.Graphics.Color.Transparent);
        });
        builder.Services.AddSingleton(_ => new SteamImageCache(new HttpClient(), Path.Combine(FileSystem.CacheDirectory, "steam-social")));
        builder.Services.AddSingleton(_ => new AppDatabase(Path.Combine(FileSystem.AppDataDirectory, "steamphone.db3")));
        builder.Services.AddSingleton<ICredentialStore, SecureCredentialStore>();
        builder.Services.AddSingleton<SteamGuardBroker>();
        builder.Services.AddSingleton(sp =>
        {
            var identity = Preferences.Default.Get("steam.installation.id", "");
            if (string.IsNullOrEmpty(identity))
            {
                identity = Guid.NewGuid().ToString("N");
                Preferences.Default.Set("steam.installation.id", identity);
            }
            return new SteamConnection(sp.GetRequiredService<ICredentialStore>(), sp.GetRequiredService<SteamGuardBroker>(), identity,
                sp.GetRequiredService<AppDatabase>().GetSettingsAsync);
        });
        builder.Services.AddSingleton<PlaytimeTracker>();
        builder.Services.AddSingleton<MonitorState>();
        builder.Services.AddSingleton<IDeviceServices, AndroidDeviceServices>();
        builder.Services.AddSingleton<IMonitoringController, AndroidMonitoringController>();
        builder.Services.AddSingleton<IBackupDocuments, AndroidBackupDocuments>();
        builder.Services.AddSingleton<MonitoringWorker>();
        builder.Services.AddSingleton<AppNavigator>();
        builder.Services.AddSingleton<LoginViewModel>();
        builder.Services.AddSingleton<DashboardViewModel>();
        builder.Services.AddTransient<FriendsViewModel>();
        builder.Services.AddSingleton<MappingsViewModel>();
        builder.Services.AddSingleton<StatsViewModel>();
        builder.Services.AddSingleton<SettingsViewModel>();
        builder.Services.AddTransient<MappingEditorViewModel>();
        builder.Services.AddTransient<LoginPage>();
        builder.Services.AddTransient<DashboardPage>();
        builder.Services.AddTransient<FriendsPage>();
        builder.Services.AddTransient<MappingsPage>();
        builder.Services.AddTransient<StatsPage>();
        builder.Services.AddTransient<SettingsPage>();
        builder.Services.AddTransient<MappingEditorPage>();
        builder.Services.AddTransient<AppShell>();
        return builder.Build();
    }
}
