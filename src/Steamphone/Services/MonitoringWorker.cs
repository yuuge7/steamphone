using Steamphone.Core;

namespace Steamphone.Services;

public sealed class MonitoringWorker(AppDatabase database, PlaytimeTracker tracker, SteamConnection steam,
    IDeviceServices device, MonitorState state)
{
    private static void Signal(SemaphoreSlim pulse)
    {
        try { pulse.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { } // An already-dispatched device event may outlive unsubscription.
    }

    public async Task RunAsync(Action<string> notify, CancellationToken cancellation)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        using var trackingPulse = new SemaphoreSlim(0, 1);
        using var connectionPulse = new SemaphoreSlim(0, 1);
        var attemptSync = new object();
        CancellationTokenSource? attempt = null;
        void ConfigurationChanged()
        {
            Signal(trackingPulse); Signal(connectionPulse);
            lock (attemptSync) attempt?.Cancel();
        }
        void SteamChanged(SteamState _) => Signal(connectionPulse);
        database.ConfigurationChanged += ConfigurationChanged;
        device.EnvironmentChanged += ConfigurationChanged;
        steam.Changed += SteamChanged;
        state.Update(x => x with { Running = true, Message = "Monitorizarea pornește…" });
        try
        {
            var settings = await database.GetSettingsAsync();
            steam.SetPresence(null, settings.DefaultStatus, settings.Visibility, settings.AnnounceGameWhileInvisible);
            await database.RecoverSessionsAsync();
            var tracking = TrackAsync(notify, trackingPulse, run.Token);
            var reconnecting = ReconnectAsync(connectionPulse, run.Token);
            var finished = await Task.WhenAny(tracking, reconnecting);
            await run.CancelAsync();
            try { await Task.WhenAll(tracking, reconnecting); }
            catch (OperationCanceledException) when (!finished.IsFaulted) { }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            state.Update(x => x with { Message = $"Monitorizare întreruptă: {ex.Message}" });
        }
        finally
        {
            database.ConfigurationChanged -= ConfigurationChanged;
            device.EnvironmentChanged -= ConfigurationChanged;
            steam.Changed -= SteamChanged;
            try { await tracker.StopAsync(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
            try { await steam.DisconnectAsync(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
            finally { state.Update(x => x with { Running = false, CurrentApp = null }); }
        }

        async Task ReconnectAsync(SemaphoreSlim pulse, CancellationToken token)
        {
            var retrySeconds = 2;
            while (!token.IsCancellationRequested)
            {
                var settings = await database.GetSettingsAsync();
                if (settings.Visibility == VisibilityMode.LocalOnly || !device.HasInternet)
                {
                    if (steam.State.Phase is ConnectionPhase.Connected or ConnectionPhase.Connecting or ConnectionPhase.Authenticating)
                        await steam.DisconnectAsync();
                    await pulse.WaitAsync(token);
                    continue;
                }
                if (steam.State.Phase == ConnectionPhase.Disconnected && steam.CanReconnect)
                {
                    using var connecting = CancellationTokenSource.CreateLinkedTokenSource(token);
                    lock (attemptSync) attempt = connecting;
                    try
                    {
                        if (await steam.ReconnectAsync(connecting.Token)) retrySeconds = 2;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (OperationCanceledException) { continue; }
                    catch (Exception ex)
                    {
                        state.Update(x => x with { Message = $"Reconectare în {retrySeconds}s: {ex.GetType().Name}" });
                        while (pulse.Wait(0)) { }
                        await pulse.WaitAsync(TimeSpan.FromSeconds(retrySeconds + Random.Shared.NextDouble()), token);
                        retrySeconds = Math.Min(120, retrySeconds * 2);
                        continue;
                    }
                    finally { lock (attemptSync) attempt = null; }
                }
                await pulse.WaitAsync(token);
            }
        }
    }

    private async Task TrackAsync(Action<string> notify, SemaphoreSlim pulse, CancellationToken token)
    {
        long revision = -1;
        var settings = new AppSettings();
        var mappings = new Dictionary<string, AppMapping>(StringComparer.Ordinal);
        while (!token.IsCancellationRequested)
        {
            var currentRevision = database.ConfigurationRevision;
            if (currentRevision != revision)
            {
                settings = await database.GetSettingsAsync();
                mappings = (await database.GetMappingsAsync()).Where(x => x.Enabled).ToDictionary(x => x.PackageName);
                revision = currentRevision;
            }
            var hasAccess = device.HasUsageAccess;
            var interactive = device.IsInteractive;
            var package = hasAccess && interactive ? device.GetForegroundPackage() : null;
            var mapping = package is not null ? mappings.GetValueOrDefault(package) : null;
            await tracker.ObserveAsync(mapping);
            steam.SetPresence(mapping is null ? null : (uint)mapping.SteamAppId, settings.DefaultStatus, settings.Visibility,
                settings.AnnounceGameWhileInvisible);
            var message = !hasAccess ? "Acordă accesul la utilizare din Setări."
                : !interactive ? "Ecran blocat · cronometru în pauză"
                : mapping is null ? "În așteptarea unui joc mapat" : $"Se urmărește {mapping.DisplayName}";
            state.Update(x => x with { CurrentApp = mapping?.DisplayName, Message = message, UsageAccess = hasAccess,
                Visibility = settings.Visibility, KeepConnectionWhenLocked = settings.KeepConnectionWhenLocked });
            var visibility = settings.Visibility switch
            {
                VisibilityMode.Invisible => settings.AnnounceGameWhileInvisible ? "Invizibil · joc trimis către Steam" : "Invizibil · joc neanunțat",
                VisibilityMode.OnlineOnly => "Online · joc ascuns",
                VisibilityMode.LocalOnly => "Doar local · fără Steam", _ => "Joc vizibil pe Steam"
            };
            var notification = $"{visibility} · {message}";
            // The service deduplicates notifications and uses this heartbeat to renew an optional wake lease.
            notify(notification);
            await pulse.WaitAsync(TimeSpan.FromSeconds(interactive ? settings.PollSeconds : 30), token);
        }
    }
}
