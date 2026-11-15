using Steamphone.Core;
using Xunit;

namespace Steamphone.Core.Tests;

public sealed class TrackingTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "steamphone-tests", Guid.NewGuid().ToString("N"));
    private AppDatabase database = null!;
    private readonly ManualClock clock = new();
    private static AppMapping Mapping(string package, uint id = 570) => new()
    { PackageName = package, DisplayName = package + " game", SteamAppId = id };

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        database = new AppDatabase(Path.Combine(directory, "test.db3"));
        return Task.CompletedTask;
    }
    public async Task DisposeAsync()
    {
        await database.CloseAsync();
        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task SwitchingAppsAndStoppingCountsEachIntervalExactlyOnce()
    {
        var tracker = new PlaytimeTracker(database, clock, TimeSpan.MaxValue);
        await tracker.ObserveAsync(Mapping("first"));
        clock.Advance(TimeSpan.FromMinutes(3));
        await tracker.ObserveAsync(Mapping("first"));
        clock.Advance(TimeSpan.FromMinutes(2));
        await tracker.ObserveAsync(Mapping("second", 730));
        clock.Advance(TimeSpan.FromMinutes(7));
        await tracker.StopAsync();
        clock.Advance(TimeSpan.FromHours(4));
        await tracker.StopAsync();
        var totals = await database.GetTotalsAsync();
        Assert.Equal(TimeSpan.FromMinutes(5).Ticks, totals.Single(x => x.PackageName == "first").DurationTicks);
        Assert.Equal(TimeSpan.FromMinutes(7).Ticks, totals.Single(x => x.PackageName == "second").DurationTicks);
    }

    [Fact]
    public async Task ProcessRecoveryKeepsCheckpointWithoutCountingDowntime()
    {
        var tracker = new PlaytimeTracker(database, clock, TimeSpan.MaxValue);
        await tracker.ObserveAsync(Mapping("game"));
        clock.Advance(TimeSpan.FromSeconds(20));
        await tracker.ObserveAsync(Mapping("game"));
        await database.CloseAsync();
        database = new AppDatabase(Path.Combine(directory, "test.db3"));
        clock.Advance(TimeSpan.FromDays(2));
        await database.RecoverSessionsAsync();
        var recovered = new PlaytimeTracker(database, clock);
        await recovered.ObserveAsync(Mapping("game"));
        clock.Advance(TimeSpan.FromSeconds(10));
        await recovered.StopAsync();
        Assert.Equal(TimeSpan.FromSeconds(30).Ticks, Assert.Single(await database.GetTotalsAsync()).DurationTicks);
    }

    [Fact]
    public async Task ChangingAppIdSplitsHistoryAndDeletionPreservesIt()
    {
        var mapping = Mapping("game");
        await database.SaveMappingAsync(mapping);
        var tracker = new PlaytimeTracker(database, clock);
        await tracker.ObserveAsync(mapping);
        clock.Advance(TimeSpan.FromSeconds(5));
        await tracker.ObserveAsync(Mapping("game", 730));
        clock.Advance(TimeSpan.FromSeconds(8));
        await tracker.StopAsync();
        await database.DeleteMappingAsync("game");
        var totals = await database.GetTotalsAsync();
        Assert.Equal(2, totals.Count);
        Assert.Equal(TimeSpan.FromSeconds(5).Ticks, totals.Single(x => x.SteamAppId == 570).DurationTicks);
        Assert.Equal(TimeSpan.FromSeconds(8).Ticks, totals.Single(x => x.SteamAppId == 730).DurationTicks);
    }

    [Fact]
    public async Task LongObservationGapFromSuspensionDoesNotBecomePlaytime()
    {
        var tracker = new PlaytimeTracker(database, clock);
        await tracker.ObserveAsync(Mapping("game"));
        clock.Advance(TimeSpan.FromSeconds(2));
        await tracker.ObserveAsync(Mapping("game"));
        clock.Advance(TimeSpan.FromHours(3));
        await tracker.StopAsync();
        Assert.Equal(TimeSpan.FromSeconds(2).Ticks, Assert.Single(await database.GetTotalsAsync()).DurationTicks);
    }

    [Fact]
    public async Task DisabledMappingAndUnmappedAppsStopTheTimer()
    {
        var mapping = Mapping("game");
        var tracker = new PlaytimeTracker(database, clock);
        await tracker.ObserveAsync(mapping);
        clock.Advance(TimeSpan.FromSeconds(10));
        mapping.Enabled = false;
        await tracker.ObserveAsync(mapping);
        clock.Advance(TimeSpan.FromMinutes(10));
        await tracker.ObserveAsync(null);
        Assert.Equal(TimeSpan.FromSeconds(10).Ticks, Assert.Single(await database.GetTotalsAsync()).DurationTicks);
    }

    [Fact]
    public async Task ClockCorrectionDoesNotCreateNegativeOrExtraTime()
    {
        var tracker = new PlaytimeTracker(database, clock);
        await tracker.ObserveAsync(Mapping("game"));
        clock.AdjustWallClock(TimeSpan.FromHours(-3));
        clock.Advance(TimeSpan.FromSeconds(9));
        await tracker.StopAsync();
        Assert.Equal(TimeSpan.FromSeconds(9).Ticks, Assert.Single(await database.GetTotalsAsync()).DurationTicks);
    }

    [Fact]
    public async Task UnplayedMappingsAppearAndInvalidIdsAreRejected()
    {
        await database.SaveMappingAsync(Mapping("unplayed"));
        Assert.Equal(0, Assert.Single(await database.GetTotalsAsync()).DurationTicks);
        var invalid = Mapping("invalid");
        invalid.SteamAppId = (long)uint.MaxValue + 1;
        await Assert.ThrowsAsync<ArgumentException>(() => database.SaveMappingAsync(invalid));
    }

    [Fact]
    public async Task CheckpointsAreBatchedButStoppingPreservesAllObservedTime()
    {
        var tracker = new PlaytimeTracker(database, clock);
        await tracker.ObserveAsync(Mapping("game"));
        for (var i = 0; i < 4; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            await tracker.ObserveAsync(Mapping("game"));
        }
        Assert.Equal(0, Assert.Single((await database.ExportAsync()).Sessions).DurationTicks);
        await tracker.FlushAsync();
        Assert.Equal(TimeSpan.FromSeconds(8).Ticks, Assert.Single((await database.ExportAsync()).Sessions).DurationTicks);
        clock.Advance(TimeSpan.FromSeconds(2));
        await tracker.ObserveAsync(Mapping("game"));
        Assert.Equal(TimeSpan.FromSeconds(10).Ticks, Assert.Single((await database.ExportAsync()).Sessions).DurationTicks);
        clock.Advance(TimeSpan.FromSeconds(2));
        await tracker.StopAsync();
        Assert.Equal(TimeSpan.FromSeconds(12).Ticks, Assert.Single(await database.GetTotalsAsync()).DurationTicks);
    }

    [Fact]
    public async Task ConcurrentPreferenceUpdatesPreservePrivacyAndPrivacySurvivesReopenAndBackup()
    {
        await Task.WhenAll(database.UpdateSettingsAsync(x => x.Visibility = VisibilityMode.Invisible),
            database.UpdateSettingsAsync(x => { x.Theme = "Dark"; x.KeepConnectionWhenLocked = true; }));
        Assert.Equal(VisibilityMode.Invisible, (await database.GetSettingsAsync()).Visibility);
        Assert.Equal("Dark", (await database.GetSettingsAsync()).Theme);
        var backup = await database.ExportAsync();
        await database.CloseAsync();
        database = new AppDatabase(Path.Combine(directory, "test.db3"));
        Assert.Equal(VisibilityMode.Invisible, (await database.GetSettingsAsync()).Visibility);
        await database.UpdateSettingsAsync(x => x.Visibility = VisibilityMode.Public);
        await database.ImportAsync(backup);
        Assert.Equal(VisibilityMode.Invisible, (await database.GetSettingsAsync()).Visibility);
        Assert.True((await database.GetSettingsAsync()).KeepConnectionWhenLocked);
    }

    [Fact]
    public async Task ExistingDatabaseGainsPrivacyColumnsWithoutLosingSettingsOrMappings()
    {
        await database.CloseAsync();
        var path = Path.Combine(directory, "legacy.db3");
        using (var legacy = new SQLite.SQLiteConnection(path))
        {
            legacy.Execute("CREATE TABLE Settings (Id INTEGER PRIMARY KEY, DefaultStatus INTEGER, Theme TEXT, PollSeconds INTEGER)");
            legacy.Execute("INSERT INTO Settings VALUES (1, 2, 'Dark', 5)");
        }
        database = new AppDatabase(path);
        var settings = await database.GetSettingsAsync();
        Assert.Equal(BaseStatus.Busy, settings.DefaultStatus);
        Assert.Equal("Dark", settings.Theme);
        Assert.Equal(5, settings.PollSeconds);
        Assert.Equal(VisibilityMode.Public, settings.Visibility);
        Assert.False(settings.KeepConnectionWhenLocked);
    }

    [Fact]
    public async Task SettingsSurviveReopenAndPollingIsBounded()
    {
        await database.SaveSettingsAsync(new AppSettings { DefaultStatus = BaseStatus.Busy, PollSeconds = 500, Theme = "Dark" });
        await database.CloseAsync();
        database = new AppDatabase(Path.Combine(directory, "test.db3"));
        var settings = await database.GetSettingsAsync();
        Assert.Equal(BaseStatus.Busy, settings.DefaultStatus);
        Assert.Equal(10, settings.PollSeconds);
        Assert.Equal("Dark", settings.Theme);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset utc = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
        private long elapsed;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => elapsed;
        public override DateTimeOffset GetUtcNow() => utc;
        public void Advance(TimeSpan by) { elapsed += by.Ticks; utc += by; }
        public void AdjustWallClock(TimeSpan by) => utc += by;
    }
}
