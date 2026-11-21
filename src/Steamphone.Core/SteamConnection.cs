using System.Security.Cryptography;
using System.Text;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;

namespace Steamphone.Core;

public enum ConnectionPhase { Disconnected, Connecting, Authenticating, Connected, NeedsLogin }
public sealed record SteamState(ConnectionPhase Phase, string Message, string? Username = null, bool PlayingBlocked = false);

public sealed class SteamConnection : IAsyncDisposable
{
    private readonly SteamClient client;
    private readonly SteamUser user;
    private readonly SteamFriends friends;
    private readonly SteamPresenceWriter presence;
    private readonly CallbackManager callbacks;
    private readonly ICredentialStore credentials;
    private readonly SteamGuardBroker guard;
    private readonly Func<Task<AppSettings>>? loadSettings;
    private readonly object friendSync = new();
    private readonly Dictionary<ulong, SteamFriend> roster = [];
    private readonly FriendLoadProgress friendProgress = new();
    private readonly SemaphoreSlim operation = new(1, 1);
    private readonly object sync = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task callbackTask;
    private readonly List<IDisposable> subscriptions = [];
    private TaskCompletionSource<bool>? connected;
    private TaskCompletionSource<bool>? disconnected;
    private TaskCompletionSource<EResult>? loggedOn;
    private CancellationTokenSource? activeLogin;
    private SteamState state = new(ConnectionPhase.Disconnected, "Deconectat");
    private bool playingBlocked = true;
    private volatile bool transportStarted;
    private uint? desiredAppId;
    private BaseStatus desiredStatus = BaseStatus.Online;
    private VisibilityMode desiredVisibility;
    private bool desiredInvisibleAnnouncement;
    private bool friendsViewActive;
    private DateTime detailsRequestedUtc;

    public event Action<SteamState>? Changed;
    public event Action? FriendsChanged;
    public bool FriendsReady { get; private set; }
    public int FriendsLoadedCount { get { lock (friendSync) return roster.Keys.Count(friendProgress.IsLoaded); } }
    public IReadOnlyList<SteamFriend> GetFriendsSnapshot()
    {
        lock (friendSync) return roster.Values.OrderByDescending(x => x.IsPlaying)
            .ThenByDescending(x => x.IsOnline).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    public SteamState State { get { lock (sync) return state; } }
    public bool CanReconnect => State.Phase != ConnectionPhase.NeedsLogin;

    public SteamConnection(ICredentialStore credentials, SteamGuardBroker guard, string installationId,
        Func<Task<AppSettings>>? loadSettings = null)
    {
        this.credentials = credentials;
        this.guard = guard;
        this.loadSettings = loadSettings;
        var configuration = SteamConfiguration.Create(builder => builder
            .WithProtocolTypes(ProtocolTypes.Tcp)
            .WithConnectionTimeout(TimeSpan.FromSeconds(20))
            .WithDefaultPersonaStateFlags(FriendLoadProgress.RequiredFields | EClientPersonaStateFlag.GameDataBlob)
            .WithMachineInfoProvider(new InstallationMachineInfo(installationId)));
        client = new SteamClient(configuration);
        client.AddHandler(new SteamRichPresenceHandler());
        user = client.GetHandler<SteamUser>()!;
        friends = client.GetHandler<SteamFriends>()!;
        presence = new SteamPresenceWriter(friends.SetPersonaState, appId => client.Send(SteamPresence.GamesPlayed(appId)));
        callbacks = new CallbackManager(client);
        subscriptions.Add(callbacks.Subscribe<SteamClient.ConnectedCallback>(_ => connected?.TrySetResult(true)));
        subscriptions.Add(callbacks.Subscribe<SteamClient.DisconnectedCallback>(_ =>
        {
            lock (sync) { presence.Reset(); playingBlocked = true; transportStarted = false; }
            lock (friendSync) { roster.Clear(); friendProgress.Clear(); FriendsReady = false; detailsRequestedUtc = default; }
            FriendsChanged?.Invoke();
            connected?.TrySetException(new IOException("Conexiunea Steam s-a închis."));
            loggedOn?.TrySetException(new IOException("Conexiunea Steam s-a închis."));
            disconnected?.TrySetResult(true);
            if (State.Phase != ConnectionPhase.NeedsLogin)
                Publish(new(ConnectionPhase.Disconnected, "Conexiune pierdută. Se va reîncerca.", State.Username));
        }));
        subscriptions.Add(callbacks.Subscribe<SteamUser.LoggedOnCallback>(result => loggedOn?.TrySetResult(result.Result)));
        subscriptions.Add(callbacks.Subscribe<SteamFriends.FriendsListCallback>(value =>
        {
            lock (friendSync)
            {
                var removed = roster.Keys.ToHashSet();
                for (var i = 0; i < friends.GetFriendCount(); i++) UpdateFriend(friends.GetFriendByIndex(i));
                for (var i = 0; i < friends.GetFriendCount(); i++) removed.Remove(friends.GetFriendByIndex(i).ConvertToUInt64());
                foreach (var id in removed) roster.Remove(id);
                friendProgress.Retain(roster.Keys.ToHashSet());
                FriendsReady = true;
                // SteamKit sends the initial persona request before this callback. Do not duplicate it.
                if (!value.Incremental) detailsRequestedUtc = DateTime.UtcNow;
            }
            RequestVisibleFriendDetails();
            FriendsChanged?.Invoke();
        }));
        subscriptions.Add(callbacks.Subscribe<SteamFriends.PersonaStateCallback>(value =>
        {
            lock (friendSync)
            {
                UpdateFriend(value.FriendID);
                var id = value.FriendID.ConvertToUInt64();
                friendProgress.Receive(id, value.StatusFlags);
                if (roster.TryGetValue(id, out var friend))
                {
                    friend = friend.MergeDetails(value.StatusFlags, value.StateFlags, value.LastLogOff);
                    // SteamKit 3.5 caches game fields only for GameDataBlob. Extra-info packets also carry them.
                    roster[id] = friend.MergeGameDetails(value.StatusFlags, value.GameID.AppID, value.GameName);
                }
            }
            FriendsChanged?.Invoke();
        }));
        subscriptions.Add(callbacks.Subscribe<RichPresenceCallback>(value =>
        {
            lock (friendSync)
            {
                if (!friendsViewActive) return;
                foreach (var detail in value.Body.rich_presence)
                {
                    if (!roster.TryGetValue(detail.steamid_user, out var friend)) continue;
                    // Display human-readable game status; localization tokens are not user-facing text.
                    var status = detail.rich_presense.FirstOrDefault(x => x.key == "status")?.value ?? "";
                    roster[detail.steamid_user] = friend with
                    { RichActivity = status.StartsWith('#') ? "" : status[..Math.Min(status.Length, 240)] };
                }
            }
            FriendsChanged?.Invoke();
        }));
        subscriptions.Add(callbacks.Subscribe<SteamUser.LoggedOffCallback>(result =>
        {
            var needsLogin = IsPermanentFailure(result.Result) || result.Result is EResult.LoggedInElsewhere or EResult.LogonSessionReplaced;
            Publish(new(needsLogin ? ConnectionPhase.NeedsLogin : ConnectionPhase.Disconnected,
                $"Steam a închis sesiunea: {result.Result}.", State.Username));
            client.Disconnect();
        }));
        subscriptions.Add(callbacks.Subscribe<SteamUser.PlayingSessionStateCallback>(result =>
        {
            lock (sync) { playingBlocked = result.PlayingBlocked; presence.ResetPlayingSession(); }
            if (State.Phase == ConnectionPhase.Connected)
            {
                Publish(new(ConnectionPhase.Connected, result.PlayingBlocked
                    ? "Un alt dispozitiv rulează un joc. Prezența este în așteptare."
                    : "Conectat la Steam", State.Username, result.PlayingBlocked));
                ApplyPresence();
            }
        }));
        callbackTask = Task.Run(async () =>
        {
            while (!lifetime.IsCancellationRequested)
            {
                try { await callbacks.RunWaitCallbackAsync(lifetime.Token); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Publish(new(ConnectionPhase.Disconnected, $"Eroare Steam: {ex.GetType().Name}", State.Username));
                    client.Disconnect();
                }
            }
        });
    }

    private void Publish(SteamState value)
    {
        lock (sync) state = value;
        Changed?.Invoke(value);
    }

    // Called only by the serialized callback pump. SteamKit has already merged partial persona updates.
    private void UpdateFriend(SteamID id)
    {
        if (friends.GetFriendRelationship(id) != EFriendRelationship.Friend) { roster.Remove(id.ConvertToUInt64()); return; }
        var key = id.ConvertToUInt64();
        roster.TryGetValue(key, out var previous);
        var appId = previous?.AppId ?? friends.GetFriendGamePlayed(id).AppID;
        var name = friends.GetFriendPersonaName(id);
        roster[key] = new(key, string.IsNullOrWhiteSpace(name) ? "Prieten Steam" : name,
            friends.GetFriendPersonaState(id), appId, previous?.GameName ?? friends.GetFriendGamePlayedName(id))
        {
            AvatarHash = SteamFriend.NormalizeAvatarHash(friends.GetFriendAvatar(id)),
            LastLogOffUtc = previous?.LastLogOffUtc,
            StateFlags = previous?.StateFlags ?? 0,
            RichActivity = previous?.AppId == appId ? previous.RichActivity : ""
        };
    }

    public void SetFriendsViewActive(bool active)
    {
        lock (friendSync) friendsViewActive = active;
        if (active) RequestVisibleFriendDetails();
    }

    public void RefreshFriendDetails() => RequestVisibleFriendDetails(retryMissing: true);

    private void RequestVisibleFriendDetails(bool retryMissing = false)
    {
        lock (friendSync)
        {
            if (!friendsViewActive || !client.IsConnected || !FriendsReady ||
                DateTime.UtcNow - detailsRequestedUtc < (retryMissing ? TimeSpan.FromSeconds(5) : TimeSpan.FromMinutes(2))) return;
            detailsRequestedUtc = DateTime.UtcNow;
            var missing = roster.Keys.Where(x => !friendProgress.IsLoaded(x)).ToArray();
            var ids = retryMissing && missing.Length > 0 ? missing : roster.Keys.ToArray();
            foreach (var batch in ids.Chunk(100))
                friends.RequestFriendInfo(batch.Select(x => new SteamID(x)),
                    FriendLoadProgress.RequiredFields | EClientPersonaStateFlag.GameDataBlob);
        }
    }

    public async Task<SteamFriendProfile?> GetFriendProfileAsync(ulong id, CancellationToken cancellation)
    {
        Task<SteamFriends.ProfileInfoCallback> response;
        lock (friendSync)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!friendsViewActive || !client.IsConnected || !roster.ContainsKey(id)) return null;
            var richRequest = new ClientMsgProtobuf<CMsgClientRichPresenceRequest>(EMsg.ClientRichPresenceRequest);
            richRequest.Body.steamid_request.Add(id);
            client.Send(richRequest);
            response = friends.RequestProfileInfo(new SteamID(id)).ToTask();
        }
        var profile = await response.WaitAsync(TimeSpan.FromSeconds(12), cancellation);
        if (profile.Result != EResult.OK) return null;
        return new(profile.RealName ?? "", string.Join(", ", new[] { profile.CityName, profile.StateName, profile.CountryName }
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct()),
            profile.TimeCreated > DateTime.UnixEpoch ? profile.TimeCreated : null, profile.Headline ?? "");
    }

    private async Task LoadPresenceSettingsAsync()
    {
        if (loadSettings is null) return;
        var settings = await loadSettings();
        lock (sync)
        {
            desiredStatus = settings.DefaultStatus; desiredVisibility = settings.Visibility;
            desiredInvisibleAnnouncement = settings.AnnounceGameWhileInvisible;
        }
    }

    public async Task LoginAsync(string username, string password, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            throw new ArgumentException("Completează contul și parola Steam.");
        await operation.WaitAsync(cancellation);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(4));
        lock (sync) activeLogin = timeout;
        guard.Begin(timeout.Token);
        try
        {
            await DisconnectCoreAsync();
            await ConnectCoreAsync(timeout.Token);
            Publish(new(ConnectionPhase.Authenticating, "Autentificare Steam…", username.Trim()));
            var saved = await credentials.ReadAsync();
            var guardData = saved?.Username.Equals(username.Trim(), StringComparison.OrdinalIgnoreCase) == true ? saved.GuardData : null;
            var session = await client.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
            {
                Username = username.Trim(), Password = password,
                IsPersistentSession = true, Authenticator = guard,
                GuardData = guardData,
                DeviceFriendlyName = "Steamphone · Android",
                ClientOSType = EOSType.AndroidUnknown,
                PlatformType = EAuthTokenPlatformType.k_EAuthTokenPlatformType_SteamClient
            }).WaitAsync(timeout.Token);
            var result = await session.PollingWaitForResultAsync(timeout.Token);
            var account = new SavedAccount(result.AccountName, result.RefreshToken, result.NewGuardData ?? guardData);
            await LogOnCoreAsync(account, timeout.Token);
            await credentials.WriteAsync(account);
        }
        catch
        {
            await DisconnectCoreAsync();
            Publish(new(ConnectionPhase.NeedsLogin, "Conectarea nu a reușit. Verifică datele și încearcă din nou."));
            throw;
        }
        finally { lock (sync) activeLogin = null; guard.Cancel(); operation.Release(); }
    }

    public async Task<bool> ReconnectAsync(CancellationToken cancellation)
    {
        await operation.WaitAsync(cancellation);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            if (State.Phase == ConnectionPhase.Connected) return true;
            var account = await credentials.ReadAsync();
            if (account is null)
            {
                Publish(new(ConnectionPhase.NeedsLogin, "Conectează-te la contul Steam."));
                return false;
            }
            await DisconnectCoreAsync();
            await ConnectCoreAsync(timeout.Token);
            await LogOnCoreAsync(account, timeout.Token);
            return true;
        }
        catch (SteamLogonException ex) when (IsPermanentFailure(ex.Result))
        {
            await credentials.ClearAsync();
            await DisconnectCoreAsync();
            Publish(new(ConnectionPhase.NeedsLogin, "Sesiunea a expirat. Autentifică-te din nou."));
            return false;
        }
        catch (SteamLogonException ex) when (ex.Result is EResult.LoggedInElsewhere or EResult.LogonSessionReplaced)
        {
            await DisconnectCoreAsync();
            Publish(new(ConnectionPhase.NeedsLogin, "Steam folosește deja o altă sesiune. Repornește conectarea când este disponibilă."));
            return false;
        }
        catch
        {
            await DisconnectCoreAsync();
            throw;
        }
        finally { operation.Release(); }
    }

    private async Task ConnectCoreAsync(CancellationToken token)
    {
        await LoadPresenceSettingsAsync();
        lock (sync)
            if (desiredVisibility == VisibilityMode.LocalOnly)
                throw new InvalidOperationException("Modul Doar local nu se conectează la Steam. Alege alt mod din Acasă.");
        Publish(new(ConnectionPhase.Connecting, "Conectare la rețeaua Steam…", State.Username));
        connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync) { transportStarted = true; playingBlocked = true; }
        client.Connect();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
    }

    private async Task LogOnCoreAsync(SavedAccount account, CancellationToken token)
    {
        loggedOn = new(TaskCreationOptions.RunContinuationsAsynchronously);
        user.LogOn(new SteamUser.LogOnDetails
        {
            Username = account.Username, AccessToken = account.RefreshToken,
            ShouldRememberPassword = true, LoginID = 0x5350484E,
            ClientOSType = EOSType.AndroidUnknown
        });
        var result = await loggedOn.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
        if (result != EResult.OK) throw new SteamLogonException(result);
        await LoadPresenceSettingsAsync();
        lock (sync) presence.Reset();
        Publish(new(ConnectionPhase.Connected, playingBlocked ? "Conectat. Se verifică sesiunea de joc…" : "Conectat la Steam", account.Username, playingBlocked));
        ApplyPresence();
    }

    public void SetPresence(uint? appId, BaseStatus status, VisibilityMode visibility = VisibilityMode.Public,
        bool announceGameWhileInvisible = false)
    {
        lock (sync)
        {
            desiredAppId = appId; desiredStatus = status; desiredVisibility = visibility;
            desiredInvisibleAnnouncement = announceGameWhileInvisible;
        }
        ApplyPresence();
    }

    private void ApplyPresence()
    {
        lock (sync)
        {
            if (state.Phase != ConnectionPhase.Connected) return;
            presence.Apply(SteamPresence.Resolve(desiredAppId, desiredStatus, desiredVisibility, desiredInvisibleAnnouncement), playingBlocked);
        }
    }

    private async Task DisconnectCoreAsync()
    {
        if (!transportStarted && !client.IsConnected) return;
        disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await Task.Run(client.Disconnect);
        try { await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException) { }
    }

    public async Task DisconnectAsync()
    {
        lock (sync) activeLogin?.Cancel();
        guard.Cancel();
        await operation.WaitAsync();
        try
        {
            lock (sync)
            {
                if (state.Phase == ConnectionPhase.Connected)
                {
                    if (!playingBlocked) client.Send(SteamPresence.GamesPlayed(null));
                    friends.SetPersonaState(EPersonaState.Offline);
                }
                presence.Reset();
            }
            await DisconnectCoreAsync();
            Publish(new(ConnectionPhase.Disconnected, "Deconectat", State.Username));
        }
        finally { operation.Release(); }
    }

    public void RetryConnection()
    {
        if (State.Phase == ConnectionPhase.NeedsLogin)
            Publish(new(ConnectionPhase.Disconnected, "Reconectare solicitată", State.Username));
    }

    private static bool IsPermanentFailure(EResult result) => result is EResult.InvalidPassword or EResult.AccessDenied
        or EResult.Expired or EResult.Revoked or EResult.AccountDisabled;

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        lifetime.Cancel();
        await callbackTask;
        foreach (var subscription in subscriptions) subscription.Dispose();
        lifetime.Dispose();
        operation.Dispose();
    }

    private sealed class InstallationMachineInfo(string identity) : IMachineInfoProvider
    {
        private byte[] Hash(string suffix) => SHA256.HashData(Encoding.UTF8.GetBytes($"Steamphone:{identity}:{suffix}"));
        public byte[] GetMachineGuid() => Hash("guid");
        public byte[] GetMacAddress() => Hash("mac");
        public byte[] GetDiskId() => Hash("disk");
    }
}

public sealed class SteamLogonException(EResult result) : Exception($"Steam a refuzat logarea: {result}.")
{
    public EResult Result { get; } = result;
}
