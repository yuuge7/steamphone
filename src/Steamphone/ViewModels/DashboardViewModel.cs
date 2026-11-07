using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Steamphone.Core;
using Steamphone.Services;

namespace Steamphone.ViewModels;

public partial class DashboardViewModel : BaseViewModel
{
    private readonly AppDatabase database;
    private readonly SteamConnection steam;
    private readonly IMonitoringController monitoring;
    private readonly MonitorState monitor;
    private readonly AppNavigator navigator;
    private readonly ICredentialStore credentials;
    private readonly SemaphoreSlim settingsGate = new(1, 1);
    private bool loading;
    [ObservableProperty] private string connectionMessage = "Deconectat";
    [ObservableProperty] private string accountName = "Cont Steam";
    [ObservableProperty] private string currentGame = "Niciun joc în prim-plan";
    [ObservableProperty] private string monitorMessage = "Monitorizarea este oprită";
    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private bool needsLogin;
    [ObservableProperty] private bool isConnected;
    [ObservableProperty] private string totalTime = "0 ore și 0 minute";
    [ObservableProperty] private int mappingCount;
    [ObservableProperty] private BaseStatus selectedStatus;
    public BaseStatus[] Statuses { get; } = Enum.GetValues<BaseStatus>();

    public DashboardViewModel(AppDatabase database, SteamConnection steam, IMonitoringController monitoring,
        MonitorState monitor, AppNavigator navigator, ICredentialStore credentials)
    {
        this.database = database; this.steam = steam; this.monitoring = monitoring;
        this.monitor = monitor; this.navigator = navigator; this.credentials = credentials;
        steam.Changed += value => MainThread.BeginInvokeOnMainThread(() => UpdateSteam(value));
        monitor.Changed += value => MainThread.BeginInvokeOnMainThread(() => UpdateMonitor(value));
        UpdateSteam(steam.State); UpdateMonitor(monitor.Snapshot);
    }
    private void UpdateSteam(SteamState value)
    {
        ConnectionMessage = value.Message; AccountName = value.Username ?? "Cont Steam";
        NeedsLogin = value.Phase == ConnectionPhase.NeedsLogin;
        IsConnected = value.Phase == ConnectionPhase.Connected;
    }
    private void UpdateMonitor(MonitorSnapshot value)
    {
        IsRunning = value.Running; CurrentGame = value.CurrentApp ?? "Niciun joc în prim-plan";
        MonitorMessage = value.Running ? value.Message : "Monitorizarea este oprită";
    }

    public override Task RefreshAsync() => PerformAsync(async () =>
    {
        loading = true;
        try { SelectedStatus = (await database.GetSettingsAsync()).DefaultStatus; }
        finally { loading = false; }
        MappingCount = (await database.GetMappingsAsync()).Count;
        TotalTime = GameTotal.FormatDuration(TimeSpan.FromTicks((await database.GetTotalsAsync()).Sum(x => x.DurationTicks)));
        UpdateSteam(steam.State); UpdateMonitor(monitor.Snapshot);
        if (AccountName == "Cont Steam") AccountName = (await credentials.ReadAsync())?.Username ?? "Cont Steam";
    });

    partial void OnSelectedStatusChanged(BaseStatus value)
    {
        if (!loading) _ = SaveStatusAsync(value);
    }
    private async Task SaveStatusAsync(BaseStatus value)
    {
        await settingsGate.WaitAsync();
        try
        {
            var settings = await database.GetSettingsAsync();
            settings.DefaultStatus = value;
            await database.SaveSettingsAsync(settings);
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { settingsGate.Release(); }
    }
    [RelayCommand] private Task StartAsync() => PerformAsync(async () => { steam.RetryConnection(); await monitoring.StartAsync(); });
    [RelayCommand] private Task StopAsync() => PerformAsync(monitoring.StopAsync);
    [RelayCommand] private Task SignInAsync() => PerformAsync(navigator.ShowLoginAsync);
    [RelayCommand] private Task AddMappingAsync() => PerformAsync(() => navigator.OpenMappingAsync());
}
