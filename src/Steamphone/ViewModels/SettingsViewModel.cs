using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Steamphone.Core;
using Steamphone.Services;

namespace Steamphone.ViewModels;

public partial class SettingsViewModel(AppDatabase database, IDeviceServices device,
    ICredentialStore credentials, IMonitoringController monitoring, SteamConnection steam, AppNavigator navigator,
    IBackupDocuments documents, MonitorState monitor, PlaytimeTracker tracker) : BaseViewModel
{
    [ObservableProperty] private string usageLabel = "";
    [ObservableProperty] private string batteryLabel = "";
    [ObservableProperty] private string notificationLabel = "";
    [ObservableProperty] private string theme = "System";
    [ObservableProperty] private int pollSeconds = 2;
    [ObservableProperty] private string savedMessage = "";
    [ObservableProperty] private string backupMessage = "";
    [ObservableProperty] private bool keepConnectionWhenLocked;
    [ObservableProperty] private bool announceGameWhileInvisible;
    public string[] Themes { get; } = ["System", "Dark", "Light"];
    public int[] Intervals { get; } = [1, 2, 3, 5, 10];

    public override Task RefreshAsync() => PerformAsync(async () =>
    {
        var settings = await database.GetSettingsAsync();
        Theme = settings.Theme; PollSeconds = settings.PollSeconds; KeepConnectionWhenLocked = settings.KeepConnectionWhenLocked;
        AnnounceGameWhileInvisible = settings.AnnounceGameWhileInvisible;
        UsageLabel = device.HasUsageAccess ? "Acces acordat" : "Acces necesar pentru detectarea jocurilor";
        BatteryLabel = device.BatteryOptimized ? "Optimizare activă · poate întrerupe conexiunea" : "Fără restricții de optimizare";
        NotificationLabel = device.NotificationsAllowed ? "Notificări permise" : "Activează notificarea serviciului";
    });
    [RelayCommand] private Task UsageAsync() => PerformAsync(() => { device.OpenUsageSettings(); return Task.CompletedTask; });
    [RelayCommand] private Task BatteryAsync() => PerformAsync(() => { device.OpenBatterySettings(); return Task.CompletedTask; });
    [RelayCommand] private Task NotificationsAsync() => PerformAsync(() => { device.OpenNotificationSettings(); return Task.CompletedTask; });
    [RelayCommand] private Task SaveAsync() => PerformAsync(async () =>
    {
        await database.UpdateSettingsAsync(settings =>
        {
            settings.Theme = Theme; settings.PollSeconds = PollSeconds;
            settings.KeepConnectionWhenLocked = KeepConnectionWhenLocked;
            settings.AnnounceGameWhileInvisible = AnnounceGameWhileInvisible;
        });
        Application.Current!.UserAppTheme = Theme switch { "Dark" => AppTheme.Dark, "Light" => AppTheme.Light, _ => AppTheme.Unspecified };
        SavedMessage = "Setările au fost salvate.";
    });
    [RelayCommand] private Task ExportAsync() => PerformAsync(async () =>
    {
        BackupMessage = "";
        await tracker.FlushAsync();
        var backup = await database.ExportAsync();
        var name = await documents.SaveAsync($"steamphone-backup-{DateTime.Now:yyyyMMdd-HHmmss}.json", BackupCodec.Encode(backup));
        BackupMessage = name is null ? "Export anulat." : $"Fișier salvat: {name}";
    });
    [RelayCommand] private Task ImportAsync() => PerformAsync(async () =>
    {
        BackupMessage = "";
        using var document = await documents.OpenAsync();
        if (document is null) { BackupMessage = "Import anulat."; return; }
        var backup = await BackupCodec.DecodeAsync(document.Content);
        var confirmed = await Shell.Current.DisplayAlertAsync("Importă backup-ul?",
            $"{backup.Mappings.Count} mapări și {backup.Sessions.Count} sesiuni. Preferințele și mapările din fișier se aplică; istoricul existent se păstrează, fără dublarea sesiunilor.",
            "Importă", "Renunță");
        if (!confirmed) { BackupMessage = "Import anulat."; return; }
        var restart = monitor.Snapshot.Running;
        await monitoring.StopAsync();
        ImportSummary summary;
        try { summary = await database.ImportAsync(backup); }
        finally { if (restart) await monitoring.StartAsync(); }
        var settings = await database.GetSettingsAsync();
        Theme = settings.Theme; PollSeconds = settings.PollSeconds; KeepConnectionWhenLocked = settings.KeepConnectionWhenLocked;
        AnnounceGameWhileInvisible = settings.AnnounceGameWhileInvisible;
        Application.Current!.UserAppTheme = Theme switch { "Dark" => AppTheme.Dark, "Light" => AppTheme.Light, _ => AppTheme.Unspecified };
        BackupMessage = $"Import finalizat: {summary.Mappings} mapări și {summary.Sessions} sesiuni. Contul Steam a rămas pe acest dispozitiv.";
    });
    [RelayCommand] private Task LogoutAsync() => PerformAsync(async () =>
    {
        await monitoring.StopAsync();
        await steam.DisconnectAsync();
        await credentials.ClearAsync();
        await navigator.ShowLoginAsync();
    });
}
