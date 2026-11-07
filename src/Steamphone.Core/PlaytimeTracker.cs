namespace Steamphone.Core;

/// <summary>One serialized writer; elapsed time is monotonic, never wall-clock arithmetic.</summary>
public sealed class PlaytimeTracker(AppDatabase database, TimeProvider? clock = null, TimeSpan? maximumObservationGap = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly TimeSpan maximumGap = maximumObservationGap ?? TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim gate = new(1, 1);
    private PlaytimeStats? session;
    private long observedAt;
    public string? CurrentName => session?.DisplayName;

    public async Task ObserveAsync(AppMapping? mapping)
    {
        await gate.WaitAsync();
        try
        {
            var now = time.GetTimestamp();
            if (session is not null)
            {
                var elapsed = time.GetElapsedTime(observedAt, now);
                var checkpoint = new PlaytimeStats
                {
                    SessionId = session.SessionId, PackageName = session.PackageName,
                    DisplayName = session.DisplayName, SteamAppId = session.SteamAppId,
                    StartedUtcTicks = session.StartedUtcTicks, LastObservedUtcTicks = time.GetUtcNow().UtcTicks,
                    DurationTicks = session.DurationTicks + (elapsed <= maximumGap ? Math.Max(0, elapsed.Ticks) : 0),
                    IsOpen = mapping is { Enabled: true } && session.PackageName == mapping.PackageName && session.SteamAppId == mapping.SteamAppId
                };
                await database.SaveSessionAsync(checkpoint);
                session = checkpoint.IsOpen ? checkpoint : null;
            }
            if (session is null && mapping is { Enabled: true })
            {
                session = new PlaytimeStats
                {
                    PackageName = mapping.PackageName, DisplayName = mapping.DisplayName,
                    SteamAppId = mapping.SteamAppId, StartedUtcTicks = time.GetUtcNow().UtcTicks,
                    LastObservedUtcTicks = time.GetUtcNow().UtcTicks, IsOpen = true
                };
                await database.SaveSessionAsync(session);
            }
            observedAt = now;
        }
        finally { gate.Release(); }
    }

    public Task StopAsync() => ObserveAsync(null);
}
