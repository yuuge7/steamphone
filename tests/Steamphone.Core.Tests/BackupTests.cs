using System.Text;
using System.Text.Json;
using Xunit;

namespace Steamphone.Core.Tests;

public sealed class BackupTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "steamphone-backup-tests", Guid.NewGuid().ToString("N"));
    private AppDatabase database = null!;
    private static BackupFile Sample() => new()
    {
        Settings = new(BaseStatus.Away, "Dark", 3),
        Mappings = [new("com.example.game", "My game", 570, true)],
        Sessions = [new(Guid.NewGuid().ToString("N"), "com.example.game", "My game", 570,
            DateTime.UtcNow.AddMinutes(-10).Ticks, DateTime.UtcNow.Ticks, TimeSpan.FromMinutes(10).Ticks)]
    };
    public Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        database = new AppDatabase(Path.Combine(directory, "data.db3"));
        return Task.CompletedTask;
    }
    public async Task DisposeAsync() { await database.CloseAsync(); Directory.Delete(directory, true); }

    [Fact]
    public async Task RoundTripRestoresAllDataWithoutCredentialsOrRuntimeState()
    {
        var sample = Sample();
        await database.ImportAsync(sample);
        var bytes = BackupCodec.Encode(await database.ExportAsync());
        var json = Encoding.UTF8.GetString(bytes);
        foreach (var sensitive in new[] { "password", "refreshToken", "guardData", "installationId", "isOpen" })
            Assert.DoesNotContain(sensitive, json, StringComparison.OrdinalIgnoreCase);
        using var stream = new MemoryStream(bytes);
        var decoded = await BackupCodec.DecodeAsync(stream);
        Assert.Equal(sample.Mappings, decoded.Mappings);
        Assert.Equal(sample.Sessions, decoded.Sessions);
        Assert.Equal(sample.Settings, decoded.Settings);
    }

    [Fact]
    public async Task RepeatedAndOlderImportsNeverDoubleCountOrReduceTime()
    {
        var sample = Sample();
        await database.ImportAsync(sample);
        await database.ImportAsync(sample);
        sample.Sessions[0] = sample.Sessions[0] with { DurationTicks = TimeSpan.FromMinutes(4).Ticks };
        await database.ImportAsync(sample);
        Assert.Equal(TimeSpan.FromMinutes(10).Ticks, Assert.Single(await database.GetTotalsAsync()).DurationTicks);
        Assert.Single((await database.ExportAsync()).Sessions);
    }

    [Fact]
    public async Task MergePreservesUnrelatedMappingsAndHistory()
    {
        await database.SaveMappingAsync(new AppMapping { PackageName = "org.other.app", DisplayName = "Other", SteamAppId = 730 });
        await database.ImportAsync(Sample());
        Assert.Equal(2, (await database.GetMappingsAsync()).Count);
        Assert.Equal(BaseStatus.Away, (await database.GetSettingsAsync()).DefaultStatus);
    }

    [Fact]
    public async Task SessionIdentityConflictRollsBackMappingsAndSettings()
    {
        var sample = Sample();
        await database.ImportAsync(sample);
        sample.Mappings[0] = sample.Mappings[0] with { SteamAppId = 730 };
        sample.Settings = sample.Settings with { Theme = "Light" };
        sample.Sessions[0] = sample.Sessions[0] with { SteamAppId = 730 };
        await Assert.ThrowsAsync<InvalidDataException>(() => database.ImportAsync(sample));
        Assert.Equal(570, Assert.Single(await database.GetMappingsAsync()).SteamAppId);
        Assert.Equal("Dark", (await database.GetSettingsAsync()).Theme);
        Assert.Equal(570, Assert.Single(await database.GetTotalsAsync()).SteamAppId);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("not json")]
    [InlineData("{\"format\":\"steamphone-backup\",\"version\":999,\"settings\":{},\"mappings\":[],\"sessions\":[]}")]
    public async Task MalformedOrUnsupportedFilesDoNotChangeTheDatabase(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await Assert.ThrowsAsync<InvalidDataException>(() => BackupCodec.DecodeAsync(stream));
        Assert.Empty(await database.GetMappingsAsync());
    }

    [Fact]
    public void DuplicateMappingsAndOverflowingTotalsAreRejected()
    {
        var sample = Sample();
        sample.Mappings.Add(sample.Mappings[0]);
        Assert.Throws<InvalidDataException>(() => BackupCodec.Validate(sample));
        sample = Sample();
        sample.Sessions[0] = sample.Sessions[0] with { DurationTicks = long.MaxValue };
        sample.Sessions.Add(sample.Sessions[0] with { SessionId = Guid.NewGuid().ToString("N"), DurationTicks = 1 });
        Assert.Throws<InvalidDataException>(() => BackupCodec.Validate(sample));
    }

    [Fact]
    public async Task StreamSizeLimitWorksWithoutTrustingFileMetadata()
    {
        using var stream = new MemoryStream(new byte[BackupCodec.MaximumBytes + 1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => BackupCodec.DecodeAsync(stream));
    }

    [Fact]
    public async Task JsonSavedWithWindowsUtf8BomCanBeImported()
    {
        var encoded = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(BackupCodec.Encode(Sample())).ToArray();
        using var stream = new MemoryStream(encoded);
        Assert.Single((await BackupCodec.DecodeAsync(stream)).Mappings);
    }

    [Fact]
    public async Task UnknownSecretFieldsAndInvalidPreferencesAreRejected()
    {
        var sample = Sample();
        sample.Settings = sample.Settings with { PollSeconds = -1 };
        Assert.Throws<InvalidDataException>(() => BackupCodec.Validate(sample));
        var json = Encoding.UTF8.GetString(BackupCodec.Encode(Sample())).Replace("\"format\":", "\"refreshToken\": \"must-not-import\", \"format\":");
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await Assert.ThrowsAsync<InvalidDataException>(() => BackupCodec.DecodeAsync(stream));
    }

    [Fact]
    public async Task ActiveSessionIsExportedAsAClosedPortableCheckpoint()
    {
        var sample = Sample().Sessions[0];
        await database.SaveSessionAsync(new PlaytimeStats { SessionId = sample.SessionId, PackageName = sample.PackageName,
            DisplayName = sample.DisplayName, SteamAppId = sample.SteamAppId, StartedUtcTicks = sample.StartedUtcTicks,
            LastObservedUtcTicks = sample.LastObservedUtcTicks, DurationTicks = sample.DurationTicks, IsOpen = true });
        var backup = await database.ExportAsync();
        await database.DeleteMappingAsync(sample.PackageName);
        Assert.Equal(sample, Assert.Single(backup.Sessions));
        Assert.DoesNotContain("isOpen", Encoding.UTF8.GetString(BackupCodec.Encode(backup)));
    }
}
