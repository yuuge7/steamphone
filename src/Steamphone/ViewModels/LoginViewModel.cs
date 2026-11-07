using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Steamphone.Core;
using Steamphone.Services;

namespace Steamphone.ViewModels;

public partial class LoginViewModel : BaseViewModel
{
    private readonly SteamConnection steam;
    private readonly SteamGuardBroker guard;
    private readonly IMonitoringController monitoring;
    private readonly ICredentialStore credentials;
    private readonly AppNavigator navigator;
    private CancellationTokenSource? cancellation;
    [ObservableProperty] private string username = "";
    [ObservableProperty] private string password = "";
    [ObservableProperty] private string guardCode = "";
    [ObservableProperty] private string guardMessage = "";
    [ObservableProperty] private bool needsCode;
    [ObservableProperty] private bool hasGuardChallenge;
    [ObservableProperty] private bool hasSavedAccount;
    [ObservableProperty] private bool preferGuardCode;
    [ObservableProperty] private string connectionMessage = "Conectare directă. Contul rămâne pe telefon.";

    public LoginViewModel(SteamConnection steam, SteamGuardBroker guard, IMonitoringController monitoring,
        ICredentialStore credentials, AppNavigator navigator)
    {
        this.steam = steam; this.guard = guard; this.monitoring = monitoring;
        this.credentials = credentials; this.navigator = navigator;
        guard.Changed += challenge => MainThread.BeginInvokeOnMainThread(() =>
        {
            HasGuardChallenge = challenge is not null;
            NeedsCode = challenge?.RequiresCode == true;
            GuardMessage = challenge?.Message ?? "";
            GuardCode = "";
        });
        steam.Changed += value => MainThread.BeginInvokeOnMainThread(() => ConnectionMessage = value.Message);
    }

    public override async Task RefreshAsync()
    {
        var saved = await credentials.ReadAsync();
        HasSavedAccount = saved is not null;
        if (saved is not null) Username = saved.Username;
    }

    [RelayCommand]
    private Task LoginAsync() => PerformAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrEmpty(Password))
            throw new ArgumentException("Completează contul și parola Steam.");
        cancellation = new CancellationTokenSource();
        guard.PreferCode = PreferGuardCode;
        try
        {
            await monitoring.StartAsync();
            await steam.LoginAsync(Username, Password, cancellation.Token);
            await navigator.ShowMainAsync();
        }
        finally
        {
            Password = "";
            cancellation.Dispose();
            cancellation = null;
        }
    });

    [RelayCommand]
    private Task ResumeAsync() => PerformAsync(async () =>
    {
        if (await credentials.ReadAsync() is null) throw new InvalidOperationException("Sesiunea nu mai este disponibilă. Conectează-te din nou.");
        steam.RetryConnection();
        await monitoring.StartAsync();
        await navigator.ShowMainAsync();
    });

    [RelayCommand] private void SubmitGuard() { if (!string.IsNullOrWhiteSpace(GuardCode)) guard.Submit(GuardCode); }
    [RelayCommand] private void Cancel() { cancellation?.Cancel(); guard.Cancel(); }
    [RelayCommand] private Task ConfigureAsync() => PerformAsync(navigator.ShowMainAsync);
}
