using Steamphone.Core;

namespace Steamphone.Services;

public sealed class MonitoringWorker(AppDatabase database, PlaytimeTracker tracker, SteamConnection steam,
    IDeviceServices device, MonitorState state)
{
    public async Task RunAsync(Action<string> notify, CancellationToken cancellation)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        state.Update(x => x with { Running = true, Message = "Monitorizarea pornește…" });
        try
        {
            await database.RecoverSessionsAsync();
            var tracking = TrackAsync(notify, run.Token);
            var reconnecting = ReconnectAsync(run.Token);
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
            try { await tracker.StopAsync(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
            try { await steam.DisconnectAsync(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
            finally { state.Update(x => x with { Running = false, CurrentApp = null }); }
        }
    }

    private async Task TrackAsync(Action<string> notify, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var settings = await database.GetSettingsAsync();
            var hasAccess = device.HasUsageAccess;
            var package = hasAccess ? device.GetForegroundPackage() : null;
            var mappings = await database.GetMappingsAsync();
            var mapping = mappings.FirstOrDefault(x => x.Enabled && x.PackageName == package);
            await tracker.ObserveAsync(mapping);
            steam.SetPresence(mapping is null ? null : (uint)mapping.SteamAppId, settings.DefaultStatus);
            var message = !hasAccess ? "Acordă accesul la utilizarea aplicațiilor din Setări."
                : mapping is null ? "În așteptarea unei aplicații mapate" : $"Se urmărește {mapping.DisplayName}";
            state.Update(x => x with { CurrentApp = mapping?.DisplayName, Message = message, UsageAccess = hasAccess });
            var notification = $"{steam.State.Message} · {message}";
            notify(notification);
            await Task.Delay(TimeSpan.FromSeconds(settings.PollSeconds), token);
        }
    }

    private async Task ReconnectAsync(CancellationToken token)
    {
        var retrySeconds = 2;
        while (!token.IsCancellationRequested)
        {
            if (steam.State.Phase == ConnectionPhase.Disconnected && steam.CanReconnect)
            {
                try
                {
                    if (await steam.ReconnectAsync(token)) retrySeconds = 2;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    state.Update(x => x with { Message = $"Reconectare în {retrySeconds}s: {ex.GetType().Name}" });
                    await Task.Delay(TimeSpan.FromSeconds(retrySeconds + Random.Shared.NextDouble()), token);
                    retrySeconds = Math.Min(60, retrySeconds * 2);
                }
            }
            await Task.Delay(1000, token);
        }
    }
}
