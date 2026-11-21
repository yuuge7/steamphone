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
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ConnectionLabel))] private bool isConnected;
    [ObservableProperty] private string totalTime = "0 ore și 0 minute";
    [ObservableProperty] private int mappingCount;
    [ObservableProperty] private BaseStatus selectedStatus;
    [ObservableProperty] private VisibilityMode selectedVisibility;
    [ObservableProperty] private bool privacySaving;
    [ObservableProperty] private bool usageMissing;
    [ObservableProperty] private bool playingBlocked;
    [ObservableProperty] private bool announceGameWhileInvisible;
    public bool IsPublic => SelectedVisibility == VisibilityMode.Public;
    public bool IsPrivate => !IsPublic;
    public bool IsLocalOnly => SelectedVisibility == VisibilityMode.LocalOnly;
    public bool ShowSignIn => !IsConnected && !IsLocalOnly;
    public string ConnectionLabel => IsConnected ? "CONECTAT" : "DECONECTAT";
    public string ModeLabel => SelectedVisibility switch
    {
        VisibilityMode.Invisible => "INVIZIBIL", VisibilityMode.OnlineOnly => "ONLINE · JOC ASCUNS",
        VisibilityMode.LocalOnly => "DOAR PE TELEFON", _ => "JOC VIZIBIL"
    };
    public string PresencePreview => PrivacySaving || (IsRunning && monitor.Snapshot.Visibility != SelectedVisibility)
        ? "Se aplică modul…" : !IsRunning ? "Monitorizare oprită"
        : IsLocalOnly ? IsConnected ? "Se închide conexiunea Steam…" : "Offline · conexiune Steam oprită"
        : !IsConnected ? "În așteptarea conexiunii Steam"
        : SelectedVisibility switch
        {
            VisibilityMode.Invisible => !AnnounceGameWhileInvisible ? "Offline pentru prieteni · joc neanunțat"
                : monitor.Snapshot.CurrentApp is null ? "Invizibil · fără joc activ"
                : PlayingBlocked ? "Invizibil · joc în așteptare" : "Invizibil · joc trimis către Steam",
            VisibilityMode.OnlineOnly => "Online · fără joc afișat de Steamphone",
            _ when PlayingBlocked => "Joc în așteptare · alt dispozitiv activ",
            _ => monitor.Snapshot.CurrentApp is { } game ? $"În joc · {game}" : $"Fără joc · {SelectedStatus}"
        };
    public string PrivacyDetail => SelectedVisibility switch
    {
        VisibilityMode.Invisible => AnnounceGameWhileInvisible
            ? "Status Invizibil, cu anunțarea jocului activată din Setări. Jocul ajunge la Steam; Valve decide dacă înregistrează ore. Confidențialitatea profilului se configurează separat în Steam."
            : "Rămâi conectat și vezi prietenii. Jocul nu este trimis; timpul se salvează local. Poți activa separat anunțarea jocului în mod invizibil din Setări.",
        VisibilityMode.OnlineOnly => "Steamphone afișează Online fără să trimită jocul. Timpul se salvează doar local.",
        VisibilityMode.LocalOnly => "Monitorizare și istoric fără conexiune la Steam. Prietenii live nu sunt disponibili în acest mod.",
        _ => "Jocul mapat este anunțat pe Steam. Când îl închizi, revii la statusul de mai jos."
    };
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
        PlayingBlocked = value.PlayingBlocked;
        OnPropertyChanged(nameof(PresencePreview));
        OnPropertyChanged(nameof(ShowSignIn));
    }
    private void UpdateMonitor(MonitorSnapshot value)
    {
        IsRunning = value.Running; CurrentGame = value.CurrentApp ?? "Niciun joc în prim-plan";
        MonitorMessage = value.Running ? value.Message : "Monitorizarea este oprită";
        UsageMissing = value.Running && !value.UsageAccess;
        OnPropertyChanged(nameof(PresencePreview));
    }

    public override Task RefreshAsync() => PerformAsync(async () =>
    {
        loading = true;
        try
        {
            var settings = await database.GetSettingsAsync();
            SelectedStatus = settings.DefaultStatus; SelectedVisibility = settings.Visibility;
            AnnounceGameWhileInvisible = settings.AnnounceGameWhileInvisible;
        }
        finally { loading = false; }
        MappingCount = (await database.GetMappingsAsync()).Count;
        TotalTime = GameTotal.FormatDuration(TimeSpan.FromTicks((await database.GetTotalsAsync()).Sum(x => x.DurationTicks)));
        UpdateSteam(steam.State); UpdateMonitor(monitor.Snapshot);
        if (AccountName == "Cont Steam") AccountName = (await credentials.ReadAsync())?.Username ?? "Cont Steam";
    });

    partial void OnSelectedStatusChanged(BaseStatus value)
    {
        OnPropertyChanged(nameof(PresencePreview));
        if (!loading) _ = SaveStatusAsync(value);
    }
    private async Task SaveStatusAsync(BaseStatus value)
    {
        await settingsGate.WaitAsync();
        try
        {
            await database.UpdateSettingsAsync(settings => settings.DefaultStatus = value);
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { settingsGate.Release(); }
    }
    partial void OnSelectedVisibilityChanged(VisibilityMode value)
    {
        foreach (var name in new[] { nameof(IsPublic), nameof(IsPrivate), nameof(IsLocalOnly), nameof(ShowSignIn), nameof(ModeLabel), nameof(PresencePreview), nameof(PrivacyDetail) })
            OnPropertyChanged(name);
        if (!loading) _ = SaveVisibilityAsync(value);
    }
    partial void OnPrivacySavingChanged(bool value) => OnPropertyChanged(nameof(PresencePreview));
    partial void OnAnnounceGameWhileInvisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(PresencePreview)); OnPropertyChanged(nameof(PrivacyDetail));
    }
    private async Task SaveVisibilityAsync(VisibilityMode value)
    {
        await settingsGate.WaitAsync();
        PrivacySaving = true;
        Error = "";
        try { await database.UpdateSettingsAsync(settings => settings.Visibility = value); }
        catch (Exception ex)
        {
            Error = $"Modul nu a fost salvat: {ex.Message}";
            loading = true;
            try { SelectedVisibility = (await database.GetSettingsAsync()).Visibility; }
            finally { loading = false; }
        }
        finally { PrivacySaving = false; settingsGate.Release(); }
    }
    [RelayCommand] private Task OpenSettingsAsync() => Shell.Current.GoToAsync("//settings");
    [RelayCommand] private Task OpenFriendsAsync() => Shell.Current.GoToAsync("//friends");
    [RelayCommand] private Task StartAsync() => PerformAsync(async () => { steam.RetryConnection(); await monitoring.StartAsync(); });
    [RelayCommand] private Task StopAsync() => PerformAsync(monitoring.StopAsync);
    [RelayCommand] private Task SignInAsync() => PerformAsync(navigator.ShowLoginAsync);
    [RelayCommand] private Task AddMappingAsync() => PerformAsync(() => navigator.OpenMappingAsync());
}
