using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Steamphone.Core;
using Steamphone.Services;

namespace Steamphone.ViewModels;

public partial class FriendsViewModel(SteamConnection steam, AppDatabase database, IMonitoringController monitoring,
    MonitorState monitor, AppNavigator navigator, ICredentialStore credentials) : BaseViewModel
{
    private int dirty;
    private bool active;
    public event Action? UpdatesPending;
    [ObservableProperty] private IReadOnlyList<SteamFriend> friends = Array.Empty<SteamFriend>();
    [ObservableProperty] private string search = "";
    [ObservableProperty] private string filter = "Toți";
    [ObservableProperty] private string summary = "Prietenii tăi, în același loc.";
    [ObservableProperty] private string connectionMessage = "Conectează Steam pentru lista live.";
    [ObservableProperty] private bool isConnected;
    [ObservableProperty] private bool isLocalOnly;
    [ObservableProperty] private string emptyTitle = "Hai să aducem prietenii aici";
    [ObservableProperty] private string emptyMessage = "Pornește conexiunea Steam. Lista se actualizează automat.";
    public string[] Filters { get; } = ["Toți", "În joc", "Online"];
    public bool ShowConnect => !IsConnected && !IsLocalOnly;
    public bool HasFriends => Friends.Count > 0;
    public bool IsWaiting => IsConnected && !steam.FriendsReady;

    public void Activate()
    {
        if (active) return;
        active = true;
        steam.FriendsChanged += MarkDirty;
        steam.Changed += OnSteamChanged;
        monitor.Changed += OnMonitorChanged;
        Interlocked.Exchange(ref dirty, 0);
        MarkDirty();
    }
    public void Deactivate()
    {
        if (!active) return;
        active = false;
        steam.FriendsChanged -= MarkDirty;
        steam.Changed -= OnSteamChanged;
        monitor.Changed -= OnMonitorChanged;
        Interlocked.Exchange(ref dirty, 0);
    }
    private void MarkDirty()
    {
        if (Interlocked.Exchange(ref dirty, 1) == 0) UpdatesPending?.Invoke();
    }
    private void OnSteamChanged(SteamState _) => MarkDirty();
    private void OnMonitorChanged(MonitorSnapshot _) => MarkDirty();
    public void FlushUpdates()
    {
        if (Interlocked.Exchange(ref dirty, 0) != 0) RenderRoster();
    }
    public override Task RefreshAsync() => PerformAsync(async () =>
    {
        IsLocalOnly = (await database.GetSettingsAsync()).Visibility == VisibilityMode.LocalOnly;
        RenderRoster();
    });
    partial void OnSearchChanged(string value) => RenderRoster();
    partial void OnFilterChanged(string value) => RenderRoster();
    partial void OnFriendsChanged(IReadOnlyList<SteamFriend> value) => OnPropertyChanged(nameof(HasFriends));
    private void RenderRoster()
    {
        IsConnected = steam.State.Phase == ConnectionPhase.Connected;
        if (monitor.Snapshot.Running) IsLocalOnly = monitor.Snapshot.Visibility == VisibilityMode.LocalOnly;
        ConnectionMessage = IsLocalOnly ? "Doar local · Steam deconectat" : steam.State.Message;
        var roster = IsConnected ? steam.GetFriendsSnapshot() : Array.Empty<SteamFriend>();
        Summary = IsConnected ? $"{roster.Count(x => x.IsPlaying)} în joc · {roster.Count(x => x.IsOnline)} online · {roster.Count} prieteni"
            : "Prietenii tăi, în același loc.";
        var query = Search.Trim();
        var visible = roster.Where(x => (Filter != "În joc" || x.IsPlaying) && (Filter != "Online" || x.IsOnline) &&
            (query.Length == 0 || x.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
             x.Activity.Contains(query, StringComparison.CurrentCultureIgnoreCase))).ToArray();
        if (!Friends.SequenceEqual(visible)) Friends = visible;
        EmptyTitle = IsLocalOnly ? "Ești în modul Doar local" : !IsConnected ? "Conectează-te cu prietenii"
            : !steam.FriendsReady ? "Se încarcă prietenii…" : roster.Count == 0 ? "Lista ta de prieteni este goală" : "Niciun prieten în această selecție";
        EmptyMessage = IsLocalOnly ? "Alege Invizibil, Doar online sau Joc vizibil din Acasă pentru a folosi hub-ul."
            : !IsConnected ? "Pornește conexiunea de mai sus. Dacă sesiunea a expirat, reconectează contul din Acasă."
            : !steam.FriendsReady ? "Steam trimite lista și statusurile prin conexiunea existentă."
            : roster.Count == 0 ? "Prietenii adăugați pe Steam vor apărea aici." : "Încearcă alt filtru sau caută un alt nume ori joc.";
        OnPropertyChanged(nameof(ShowConnect)); OnPropertyChanged(nameof(IsWaiting));
    }
    [RelayCommand] private void SelectFilter(string value) => Filter = value;
    [RelayCommand] private Task ConnectAsync() => PerformAsync(async () =>
    {
        if (steam.State.Phase == ConnectionPhase.NeedsLogin || await credentials.ReadAsync() is null)
        {
            await navigator.ShowLoginAsync(); return;
        }
        steam.RetryConnection(); await monitoring.StartAsync();
        if (steam.State.Phase == ConnectionPhase.NeedsLogin) await navigator.ShowLoginAsync();
        RenderRoster();
    });
    [RelayCommand] private Task GoHomeAsync() => Shell.Current.GoToAsync("//home");
    [RelayCommand] private Task OpenStoreAsync() => PerformAsync(() => Browser.Default.OpenAsync("https://store.steampowered.com/", BrowserLaunchMode.External));
    [RelayCommand] private Task OpenLibraryAsync() => PerformAsync(() => Browser.Default.OpenAsync("https://steamcommunity.com/my/games/", BrowserLaunchMode.External));
    [RelayCommand] private Task OpenProfileAsync() => PerformAsync(() => Browser.Default.OpenAsync("https://steamcommunity.com/my/", BrowserLaunchMode.External));
    [RelayCommand] private Task OpenFriendAsync(SteamFriend friend) => PerformAsync(async () =>
    {
        var choice = await Shell.Current.DisplayActionSheetAsync(friend.Name, "Închide", null,
            friend.IsPlaying && friend.AppId > 0 ? ["Vezi profilul", "Vezi jocul în magazin"] : ["Vezi profilul"]);
        if (choice == "Vezi profilul") await Browser.Default.OpenAsync(friend.ProfileUrl, BrowserLaunchMode.External);
        else if (choice == "Vezi jocul în magazin") await Browser.Default.OpenAsync($"https://store.steampowered.com/app/{friend.AppId}/", BrowserLaunchMode.External);
    });
}
