using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Steamphone.Core;
using Steamphone.Services;

namespace Steamphone.ViewModels;

public partial class FriendsViewModel(SteamConnection steam, AppDatabase database, IMonitoringController monitoring,
    MonitorState monitor, AppNavigator navigator, ICredentialStore credentials, SteamImageCache images) : BaseViewModel
{
    private int dirty;
    private bool active;
    private CancellationTokenSource? viewLifetime;
    private CancellationTokenSource? detailLifetime;
    private CancellationTokenSource? imageWindow;
    private CancellationTokenSource? loadWindow;
    private bool warmingAvatars;
    private CancellationToken warmingToken;
    private readonly Dictionary<ulong, FriendRowViewModel> rows = [];
    private int firstVisible;
    private int lastVisible = 9;
    public event Action? UpdatesPending;
    public ObservableCollection<FriendRowViewModel> Friends { get; } = [];
    private readonly Dictionary<ulong, SteamFriendProfile?> profiles = [];
    [ObservableProperty] private string search = "";
    [ObservableProperty] private string filter = "Toți";
    [ObservableProperty] private string connectionMessage = "Conectează Steam pentru lista live.";
    [ObservableProperty] private bool isConnected;
    [ObservableProperty] private bool isLocalOnly;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CountsSummary))] private int playingCount;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CountsSummary))] private int onlineCount;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CountsSummary))] private int friendCount;
    [ObservableProperty] private string emptyTitle = "Prietenii tăi sunt pe Steam";
    [ObservableProperty] private string emptyMessage = "Conectează contul și vezi cine este online.";
    [ObservableProperty] private FriendRowViewModel? selectedFriend;
    [ObservableProperty] private bool profileLoading;
    [ObservableProperty] private string profileName = "";
    [ObservableProperty] private string profileLocation = "";
    [ObservableProperty] private string profileMemberSince = "";
    [ObservableProperty] private string profileMessage = "";
    [ObservableProperty] private string? gameArtwork;
    [ObservableProperty] private bool loadExpired;
    [ObservableProperty] private string loadMessage = "";
    [ObservableProperty] private double loadFraction;
    public bool ShowConnect => !IsConnected && !IsLocalOnly;
    public bool HasFriends => Friends.Count > 0;
    public bool IsWaiting => IsConnected && !LoadExpired && (!steam.FriendsReady || steam.FriendsLoadedCount < FriendCount);
    public bool CanRetryLoad => IsConnected && !IsWaiting;
    public bool HasSelectedFriend => SelectedFriend is not null;
    public bool HasProfileName => ProfileName.Length > 0;
    public bool HasProfileLocation => ProfileLocation.Length > 0;
    public bool HasProfileMemberSince => ProfileMemberSince.Length > 0;
    public bool HasGameArtwork => GameArtwork is not null;
    public string ConnectionBadge => IsConnected ? "LIVE" : "OFFLINE";
    public string CountsSummary => $"{PlayingCount} în joc · {OnlineCount} online · {FriendCount} prieteni";

    public void Activate()
    {
        if (active) return;
        active = true;
        viewLifetime = new();
        steam.FriendsChanged += MarkDirty;
        steam.Changed += OnSteamChanged;
        monitor.Changed += OnMonitorChanged;
        steam.SetFriendsViewActive(true);
        MarkDirty();
    }
    public void Deactivate()
    {
        if (!active) return;
        active = false;
        steam.FriendsChanged -= MarkDirty;
        steam.Changed -= OnSteamChanged;
        monitor.Changed -= OnMonitorChanged;
        steam.SetFriendsViewActive(false);
        CloseFriend();
        imageWindow?.Cancel(); imageWindow?.Dispose(); imageWindow = null;
        loadWindow?.Cancel(); loadWindow?.Dispose(); loadWindow = null;
        viewLifetime?.Cancel();
        viewLifetime?.Dispose();
        viewLifetime = null;
        foreach (var row in rows.Values) row.Avatar = null;
        rows.Clear();
        profiles.Clear();
        Friends.Clear();
        IsConnected = false;
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
        if (active && Interlocked.Exchange(ref dirty, 0) != 0) RenderRoster();
    }
    public override Task RefreshAsync() => PerformAsync(async () =>
    {
        IsLocalOnly = (await database.GetSettingsAsync()).Visibility == VisibilityMode.LocalOnly;
        if (active) RenderRoster();
    });
    partial void OnSearchChanged(string value) { firstVisible = 0; lastVisible = 9; RenderRoster(); }
    partial void OnFilterChanged(string value) { firstVisible = 0; lastVisible = 9; RenderRoster(); }
    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ConnectionBadge));
        if (value) StartLoadWindow();
        else
        {
            imageWindow?.Cancel();
            loadWindow?.Cancel();
        }
    }
    partial void OnSelectedFriendChanged(FriendRowViewModel? value) => OnPropertyChanged(nameof(HasSelectedFriend));
    partial void OnProfileNameChanged(string value) => OnPropertyChanged(nameof(HasProfileName));
    partial void OnProfileLocationChanged(string value) => OnPropertyChanged(nameof(HasProfileLocation));
    partial void OnProfileMemberSinceChanged(string value) => OnPropertyChanged(nameof(HasProfileMemberSince));
    partial void OnGameArtworkChanged(string? value) => OnPropertyChanged(nameof(HasGameArtwork));
    private void RenderRoster()
    {
        if (!active) return;
        IsConnected = steam.State.Phase == ConnectionPhase.Connected;
        if (monitor.Snapshot.Running) IsLocalOnly = monitor.Snapshot.Visibility == VisibilityMode.LocalOnly;
        ConnectionMessage = IsLocalOnly ? "Doar local · Steam deconectat" : IsConnected ? "Conectat la Steam · actualizări live" : steam.State.Message;
        var roster = IsConnected ? steam.GetFriendsSnapshot() : Array.Empty<SteamFriend>();
        PlayingCount = roster.Count(x => x.IsPlaying);
        OnlineCount = roster.Count(x => x.IsOnline);
        FriendCount = roster.Count;
        var loaded = steam.FriendsLoadedCount;
        LoadFraction = FriendCount == 0 ? 0 : Math.Clamp((double)loaded / FriendCount, 0, 1);
        LoadMessage = IsWaiting ? FriendCount == 0 ? "Se încarcă lista Steam…" : $"Se încarcă activitatea · {loaded}/{FriendCount}"
            : LoadExpired && (!steam.FriendsReady || loaded < FriendCount) ? "Steam răspunde lent. Reîncearcă pentru detaliile lipsă."
            : IsConnected ? $"{FriendCount} prieteni · activitate actualizată" : ConnectionMessage;
        var ids = roster.Select(x => x.SteamId).ToHashSet();
        foreach (var id in rows.Keys.Where(x => !ids.Contains(x)).ToArray()) rows.Remove(id);
        if (SelectedFriend is { } selected && !ids.Contains(selected.Friend.SteamId)) CloseFriend();
        foreach (var friend in roster)
        {
            if (rows.TryGetValue(friend.SteamId, out var row))
            {
                if (SelectedFriend == row && (row.Friend.AppId != friend.AppId || row.Friend.IsPlaying != friend.IsPlaying))
                    GameArtwork = null;
                row.Update(friend);
            }
            else rows[friend.SteamId] = new(friend);
        }
        var query = Search.Trim();
        var visible = roster.Where(x => (Filter != "În joc" || x.IsPlaying) && (Filter != "Online" || x.IsOnline) &&
            (Filter != "Offline" || !x.IsOnline) && (query.Length == 0 ||
             x.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
             x.Activity.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
             x.RichActivity.Contains(query, StringComparison.CurrentCultureIgnoreCase))).Select(x => rows[x.SteamId]).ToArray();
        string? previousSection = null;
        foreach (var row in visible)
        {
            var section = row.Friend.IsPlaying ? "ÎN JOC" : row.Friend.IsOnline ? "ONLINE" : "OFFLINE";
            row.Section = section != previousSection ? section : "";
            previousSection = section;
        }
        Friends.Synchronize(visible);
        EmptyTitle = IsLocalOnly ? "Ești în modul Doar local" : !IsConnected ? "Aproape de prietenii tăi"
            : !steam.FriendsReady ? LoadExpired ? "Steam răspunde mai greu" : "Se încarcă prietenii…" : roster.Count == 0 ? "Lista ta de prieteni este goală" : "Niciun prieten găsit";
        EmptyMessage = IsLocalOnly ? "Alege alt mod de vizibilitate din Acasă pentru a vedea prietenii."
            : !IsConnected ? "Conectează Steam pentru statusuri, jocuri și profiluri live."
            : !steam.FriendsReady ? LoadExpired ? "Reîncearcă încărcarea sau verifică conexiunea la internet." : "Lista apare imediat ce Steam trimite primele date."
            : roster.Count == 0 ? "Prietenii adăugați pe Steam vor apărea aici." : "Încearcă alt nume, joc sau filtru.";
        OnPropertyChanged(nameof(ShowConnect));
        OnPropertyChanged(nameof(IsWaiting));
        OnPropertyChanged(nameof(CanRetryLoad));
        OnPropertyChanged(nameof(HasFriends));
        _ = LoadVisibleAvatarsAsync(firstVisible, lastVisible);
        // Fill the rest of the roster during the same bounded visit, after visible rows are queued.
        if (imageWindow is { IsCancellationRequested: false })
            _ = WarmAvatarsAsync(imageWindow.Token);
    }

    private async Task WarmAvatarsAsync(CancellationToken token)
    {
        if (warmingAvatars && warmingToken == token) return;
        warmingAvatars = true;
        warmingToken = token;
        try
        {
            while (active && !token.IsCancellationRequested)
            {
                // Small waves leave room for newly visible rows and an opened friend's artwork.
                var next = rows.Values.Where(x => x.CanLoadAvatar).Take(SteamImageCache.MaximumConcurrentDownloads).ToArray();
                if (next.Length == 0) break;
                await Task.WhenAll(next.Select(x => x.LoadAvatarAsync(images, token)));
            }
        }
        finally { if (warmingToken == token) warmingAvatars = false; }
    }

    private void StartLoadWindow()
    {
        if (!active || viewLifetime is null) return;
        loadWindow?.Cancel(); loadWindow?.Dispose();
        loadWindow = CancellationTokenSource.CreateLinkedTokenSource(viewLifetime.Token);
        imageWindow?.Cancel(); imageWindow?.Dispose();
        imageWindow = CancellationTokenSource.CreateLinkedTokenSource(viewLifetime.Token);
        imageWindow.CancelAfter(FriendLoadProgress.LoadBudget);
        LoadExpired = false;
        _ = FinishLoadWindowAsync(loadWindow.Token);
    }

    private async Task FinishLoadWindowAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8), token);
            if (active && !token.IsCancellationRequested && IsWaiting) steam.RefreshFriendDetails();
            await Task.Delay(FriendLoadProgress.LoadBudget - TimeSpan.FromSeconds(8), token);
            if (!active || token.IsCancellationRequested) return;
            LoadExpired = true;
            RenderRoster();
        }
        catch (OperationCanceledException) { }
    }

    [RelayCommand] private void RetryLoad()
    {
        if (!active || !IsConnected) return;
        foreach (var row in rows.Values) row.AllowAvatarRetry();
        StartLoadWindow();
        steam.RefreshFriendDetails();
        RenderRoster();
    }

    public async Task LoadVisibleAvatarsAsync(int first, int last)
    {
        var moved = firstVisible != Math.Max(0, first) || lastVisible != Math.Max(Math.Max(0, first), last);
        firstVisible = Math.Max(0, first);
        lastVisible = Math.Max(firstVisible, last);
        if (!active || viewLifetime is null) return;
        if (moved && imageWindow is { IsCancellationRequested: true })
        {
            imageWindow.Dispose();
            imageWindow = CancellationTokenSource.CreateLinkedTokenSource(viewLifetime.Token);
            imageWindow.CancelAfter(FriendLoadProgress.LoadBudget);
        }
        var token = imageWindow?.Token ?? viewLifetime.Token;
        var visible = Friends.Skip(firstVisible).Take(Math.Min(30, lastVisible - firstVisible + 1)).ToArray();
        await Task.WhenAll(visible.Select(x => x.LoadAvatarAsync(images, token)));
    }

    [RelayCommand] private void SelectFilter(string value) => Filter = value;
    [RelayCommand] private Task ConnectAsync() => PerformAsync(async () =>
    {
        if (steam.State.Phase == ConnectionPhase.NeedsLogin || await credentials.ReadAsync() is null)
        { await navigator.ShowLoginAsync(); return; }
        steam.RetryConnection(); await monitoring.StartAsync();
        if (steam.State.Phase == ConnectionPhase.NeedsLogin) await navigator.ShowLoginAsync();
        RenderRoster();
    });
    [RelayCommand] private Task GoHomeAsync() => Shell.Current.GoToAsync("//home");
    [RelayCommand] private Task OpenStoreAsync() => PerformAsync(() => Browser.Default.OpenAsync("https://store.steampowered.com/", BrowserLaunchMode.External));
    [RelayCommand] private Task OpenLibraryAsync() => PerformAsync(() => Browser.Default.OpenAsync("https://steamcommunity.com/my/games/", BrowserLaunchMode.External));
    [RelayCommand] private Task OpenProfileAsync() => PerformAsync(() => Browser.Default.OpenAsync("https://steamcommunity.com/my/", BrowserLaunchMode.External));
    [RelayCommand] private async Task OpenFriendAsync(FriendRowViewModel row)
    {
        if (!active || viewLifetime is null) return;
        CloseFriend();
        SelectedFriend = row;
        ProfileName = ProfileLocation = ProfileMemberSince = ProfileMessage = "";
        ProfileLoading = true;
        detailLifetime = CancellationTokenSource.CreateLinkedTokenSource(viewLifetime.Token);
        var token = detailLifetime.Token;
        await Task.WhenAll(LoadProfileAsync(row.Friend.SteamId, token), LoadArtworkAsync(row.Friend, token),
            row.LoadAvatarAsync(images, token));
    }
    private async Task LoadProfileAsync(ulong id, CancellationToken token)
    {
        try
        {
            if (!profiles.TryGetValue(id, out var profile))
            {
                profile = await steam.GetFriendProfileAsync(id, token);
                if (!token.IsCancellationRequested) profiles[id] = profile;
            }
            if (token.IsCancellationRequested) return;
            ProfileName = profile?.RealName ?? "";
            ProfileLocation = profile?.Location ?? "";
            ProfileMemberSince = profile?.MemberSince ?? "";
            ProfileMessage = profile is null ? "Detaliile profilului nu sunt disponibile. Poți deschide profilul în Steam."
                : ProfileName.Length + ProfileLocation.Length + ProfileMemberSince.Length == 0
                    ? "Acest prieten nu a publicat alte detalii de profil." : "";
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!token.IsCancellationRequested) ProfileMessage = "Profilul nu a putut fi încărcat. Poți încerca din nou."; }
        finally { if (!token.IsCancellationRequested) ProfileLoading = false; }
    }
    private async Task LoadArtworkAsync(SteamFriend friend, CancellationToken token)
    {
        if (!friend.IsPlaying) return;
        try
        {
            var path = await images.GetGameHeaderAsync(friend.AppId, token);
            if (!token.IsCancellationRequested) GameArtwork = path;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException) { }
    }
    [RelayCommand] private void CloseFriend()
    {
        detailLifetime?.Cancel();
        detailLifetime?.Dispose();
        detailLifetime = null;
        SelectedFriend = null;
        GameArtwork = null;
        ProfileLoading = false;
    }
    [RelayCommand] private Task OpenSelectedProfileAsync()
    {
        var url = SelectedFriend?.Friend.ProfileUrl;
        return url is null ? Task.CompletedTask : PerformAsync(() => Browser.Default.OpenAsync(url, BrowserLaunchMode.External));
    }
    [RelayCommand] private Task OpenSelectedGameAsync()
    {
        var friend = SelectedFriend?.Friend;
        return friend is not { IsPlaying: true, AppId: > 0 } ? Task.CompletedTask
            : PerformAsync(() => Browser.Default.OpenAsync($"https://store.steampowered.com/app/{friend.AppId}/", BrowserLaunchMode.External));
    }
    [RelayCommand] private Task RetryProfileAsync()
    {
        if (SelectedFriend is not { } row) return Task.CompletedTask;
        profiles.Remove(row.Friend.SteamId);
        return OpenFriendAsync(row);
    }
}

public partial class FriendRowViewModel(SteamFriend friend) : ObservableObject
{
    [ObservableProperty] private SteamFriend friend = friend;
    [ObservableProperty] private string? avatar;
    [ObservableProperty] private string section = "";
    private bool loading;
    private Task? avatarLoad;
    private string? attemptedHash;
    private DateTime retryAfter;
    public bool HasAvatar => Avatar is not null;
    public bool HasSection => Section.Length > 0;
    public bool HasSecondaryActivity => Friend.SecondaryActivity.Length > 0;
    public bool HasStoreGame => Friend.IsPlaying && Friend.AppId > 0;
    public bool CanLoadAvatar => !loading && !HasAvatar && Friend.AvatarHash is not null &&
        (attemptedHash != Friend.AvatarHash || DateTime.UtcNow >= retryAfter);
    public void Update(SteamFriend value)
    {
        if (Friend == value) return;
        if (Friend.AvatarHash != value.AvatarHash) Avatar = null;
        Friend = value;
        OnPropertyChanged(nameof(HasSecondaryActivity));
        OnPropertyChanged(nameof(HasStoreGame));
    }
    partial void OnAvatarChanged(string? value) => OnPropertyChanged(nameof(HasAvatar));
    partial void OnSectionChanged(string value) => OnPropertyChanged(nameof(HasSection));
    public void AllowAvatarRetry() { attemptedHash = null; retryAfter = default; }
    public async Task LoadAvatarAsync(SteamImageCache images, CancellationToken token)
    {
        if (token.IsCancellationRequested) return;
        if (avatarLoad is { IsCompleted: false } pending) await pending;
        if (token.IsCancellationRequested || !CanLoadAvatar) return;
        avatarLoad = DownloadAvatarAsync(images, token);
        await avatarLoad;
    }

    private async Task DownloadAvatarAsync(SteamImageCache images, CancellationToken token)
    {
        loading = true;
        var hash = Friend.AvatarHash;
        attemptedHash = hash;
        retryAfter = DateTime.UtcNow.AddSeconds(30);
        try
        {
            var path = await images.GetAvatarAsync(hash, token);
            if (!token.IsCancellationRequested && Friend.AvatarHash == hash) Avatar = path;
        }
        catch (OperationCanceledException) { attemptedHash = null; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException) { }
        finally { loading = false; }
    }
}
