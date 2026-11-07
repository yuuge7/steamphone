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
    private readonly CallbackManager callbacks;
    private readonly ICredentialStore credentials;
    private readonly SteamGuardBroker guard;
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
    private bool presenceSent;
    private uint? desiredAppId, sentAppId;
    private BaseStatus desiredStatus = BaseStatus.Online, sentStatus;

    public event Action<SteamState>? Changed;
    public SteamState State { get { lock (sync) return state; } }
    public bool CanReconnect => State.Phase != ConnectionPhase.NeedsLogin;

    public SteamConnection(ICredentialStore credentials, SteamGuardBroker guard, string installationId)
    {
        this.credentials = credentials;
        this.guard = guard;
        var configuration = SteamConfiguration.Create(builder => builder
            .WithProtocolTypes(ProtocolTypes.Tcp)
            .WithConnectionTimeout(TimeSpan.FromSeconds(20))
            .WithMachineInfoProvider(new InstallationMachineInfo(installationId)));
        client = new SteamClient(configuration);
        user = client.GetHandler<SteamUser>()!;
        friends = client.GetHandler<SteamFriends>()!;
        callbacks = new CallbackManager(client);
        subscriptions.Add(callbacks.Subscribe<SteamClient.ConnectedCallback>(_ => connected?.TrySetResult(true)));
        subscriptions.Add(callbacks.Subscribe<SteamClient.DisconnectedCallback>(_ =>
        {
            lock (sync) { presenceSent = false; playingBlocked = true; transportStarted = false; }
            connected?.TrySetException(new IOException("Conexiunea Steam s-a închis."));
            loggedOn?.TrySetException(new IOException("Conexiunea Steam s-a închis."));
            disconnected?.TrySetResult(true);
            if (State.Phase != ConnectionPhase.NeedsLogin)
                Publish(new(ConnectionPhase.Disconnected, "Conexiune pierdută. Se va reîncerca.", State.Username));
        }));
        subscriptions.Add(callbacks.Subscribe<SteamUser.LoggedOnCallback>(result => loggedOn?.TrySetResult(result.Result)));
        subscriptions.Add(callbacks.Subscribe<SteamUser.LoggedOffCallback>(result =>
        {
            var needsLogin = IsPermanentFailure(result.Result) || result.Result is EResult.LoggedInElsewhere or EResult.LogonSessionReplaced;
            Publish(new(needsLogin ? ConnectionPhase.NeedsLogin : ConnectionPhase.Disconnected,
                $"Steam a închis sesiunea: {result.Result}.", State.Username));
            client.Disconnect();
        }));
        subscriptions.Add(callbacks.Subscribe<SteamUser.PlayingSessionStateCallback>(result =>
        {
            lock (sync) { playingBlocked = result.PlayingBlocked; presenceSent = false; }
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
        lock (sync) presenceSent = false;
        Publish(new(ConnectionPhase.Connected, playingBlocked ? "Conectat. Se verifică sesiunea de joc…" : "Conectat la Steam", account.Username, playingBlocked));
        ApplyPresence();
    }

    public void SetPresence(uint? appId, BaseStatus status)
    {
        lock (sync) { desiredAppId = appId; desiredStatus = status; }
        ApplyPresence();
    }

    private void ApplyPresence()
    {
        lock (sync)
        {
            if (state.Phase != ConnectionPhase.Connected) return;
            var visibleAppId = desiredAppId;
            var visibleStatus = visibleAppId.HasValue ? BaseStatus.Online : desiredStatus;
            if (!presenceSent || sentStatus != visibleStatus)
            {
                friends.SetPersonaState(SteamPresence.Persona(visibleAppId, desiredStatus));
            }
            // Steam disconnects clients that send GamesPlayed while another client owns the playing session.
            if (playingBlocked) return;
            if (!presenceSent || sentAppId != visibleAppId)
            {
                client.Send(SteamPresence.GamesPlayed(visibleAppId));
            }
            sentAppId = visibleAppId;
            sentStatus = visibleStatus;
            presenceSent = true;
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
            SetPresence(null, BaseStatus.Offline);
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
