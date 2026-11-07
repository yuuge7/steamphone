using SQLite;

namespace Steamphone.Core;

public sealed class AppDatabase
{
    static AppDatabase() => SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
    private readonly SQLiteAsyncConnection connection;
    private readonly Lazy<Task> initialize;

    public AppDatabase(string path)
    {
        connection = new SQLiteAsyncConnection(path, SQLiteOpenFlags.ReadWrite |
            SQLiteOpenFlags.Create | SQLiteOpenFlags.FullMutex);
        initialize = new Lazy<Task>(InitializeAsync);
    }

    private async Task InitializeAsync()
    {
        await connection.EnableWriteAheadLoggingAsync();
        await connection.CreateTableAsync<AppMapping>();
        await connection.CreateTableAsync<PlaytimeStats>();
        await connection.CreateTableAsync<AppSettings>();
        await connection.ExecuteAsync("PRAGMA user_version = 1");
    }

    public async Task<List<AppMapping>> GetMappingsAsync()
    {
        await initialize.Value;
        return await connection.Table<AppMapping>().OrderBy(x => x.DisplayName).ToListAsync();
    }

    public async Task SaveMappingAsync(AppMapping mapping)
    {
        if (string.IsNullOrWhiteSpace(mapping.PackageName) || string.IsNullOrWhiteSpace(mapping.DisplayName))
            throw new ArgumentException("Selectează o aplicație instalată.");
        if (mapping.SteamAppId is <= 0 or > uint.MaxValue)
            throw new ArgumentException("Steam AppID trebuie să fie între 1 și 4294967295.");
        await initialize.Value;
        await connection.InsertOrReplaceAsync(mapping);
    }

    public async Task DeleteMappingAsync(string packageName)
    {
        await initialize.Value;
        await connection.DeleteAsync<AppMapping>(packageName);
    }

    public async Task<AppSettings> GetSettingsAsync()
    {
        await initialize.Value;
        return await connection.FindAsync<AppSettings>(1) ?? new AppSettings();
    }

    public async Task SaveSettingsAsync(AppSettings settings)
    {
        await initialize.Value;
        settings.Id = 1;
        settings.PollSeconds = Math.Clamp(settings.PollSeconds, 1, 10);
        await connection.InsertOrReplaceAsync(settings);
    }

    public async Task SaveSessionAsync(PlaytimeStats session)
    {
        await initialize.Value;
        await connection.InsertOrReplaceAsync(session);
    }

    // On process death, keep only time already checkpointed. Never count the downtime.
    public async Task RecoverSessionsAsync()
    {
        await initialize.Value;
        await connection.ExecuteAsync("UPDATE PlaytimeStats SET IsOpen = 0 WHERE IsOpen = 1");
    }

    public async Task<List<GameTotal>> GetTotalsAsync()
    {
        await initialize.Value;
        var sums = await connection.QueryAsync<TotalRow>(
            "SELECT PackageName, SteamAppId, MAX(DisplayName) AS DisplayName, " +
            "SUM(DurationTicks) AS DurationTicks FROM PlaytimeStats GROUP BY PackageName, SteamAppId");
        var totals = sums.Select(x => new GameTotal(x.PackageName, x.DisplayName, x.SteamAppId, x.DurationTicks))
            .ToDictionary(x => (x.PackageName, x.SteamAppId));
        foreach (var mapping in await GetMappingsAsync())
        {
            var key = (mapping.PackageName, mapping.SteamAppId);
            totals[key] = totals.TryGetValue(key, out var existing)
                ? existing with { DisplayName = mapping.DisplayName }
                : new GameTotal(mapping.PackageName, mapping.DisplayName, mapping.SteamAppId, 0);
        }
        return totals.Values.OrderByDescending(x => x.DurationTicks).ThenBy(x => x.DisplayName).ToList();
    }

    public async Task CloseAsync() { await initialize.Value; await connection.CloseAsync(); }

    public async Task<BackupFile> ExportAsync()
    {
        await initialize.Value;
        var backup = new BackupFile();
        await connection.RunInTransactionAsync(db =>
        {
            var settings = db.Find<AppSettings>(1) ?? new AppSettings();
            backup.Settings = new(settings.DefaultStatus, settings.Theme, settings.PollSeconds);
            backup.Mappings = db.Table<AppMapping>().ToList()
                .Select(x => new BackupMapping(x.PackageName, x.DisplayName, x.SteamAppId, x.Enabled)).ToList();
            backup.Sessions = db.Table<PlaytimeStats>().ToList().Select(x => new BackupSession(x.SessionId,
                x.PackageName, x.DisplayName, x.SteamAppId, x.StartedUtcTicks, x.LastObservedUtcTicks, x.DurationTicks)).ToList();
        });
        return backup;
    }

    /// <summary>All-or-nothing merge. Session IDs make repeat imports idempotent.</summary>
    public async Task<ImportSummary> ImportAsync(BackupFile backup)
    {
        BackupCodec.Validate(backup);
        await initialize.Value;
        await connection.RunInTransactionAsync(db =>
        {
            foreach (var mapping in backup.Mappings)
                db.InsertOrReplace(new AppMapping { PackageName = mapping.PackageName, DisplayName = mapping.DisplayName,
                    SteamAppId = mapping.SteamAppId, Enabled = mapping.Enabled });
            foreach (var session in backup.Sessions)
            {
                var existing = db.Find<PlaytimeStats>(session.SessionId);
                if (existing is not null && (existing.PackageName != session.PackageName || existing.SteamAppId != session.SteamAppId ||
                    existing.StartedUtcTicks != session.StartedUtcTicks))
                    throw new InvalidDataException("Un ID de sesiune din fișier aparține altui joc. Importul a fost anulat integral.");
                if (existing is not null && existing.DurationTicks >= session.DurationTicks) continue;
                db.InsertOrReplace(new PlaytimeStats { SessionId = session.SessionId, PackageName = session.PackageName,
                    DisplayName = session.DisplayName, SteamAppId = session.SteamAppId, StartedUtcTicks = session.StartedUtcTicks,
                    LastObservedUtcTicks = session.LastObservedUtcTicks, DurationTicks = session.DurationTicks, IsOpen = false });
            }
            // Check merged totals as well, before committing the transaction.
            try { _ = db.Table<PlaytimeStats>().ToList().Aggregate(0L, (sum, x) => checked(sum + x.DurationTicks)); }
            catch (OverflowException ex) { throw new InvalidDataException("Durata cumulată este prea mare. Import anulat.", ex); }
            db.InsertOrReplace(new AppSettings { DefaultStatus = backup.Settings.DefaultStatus,
                Theme = backup.Settings.Theme, PollSeconds = backup.Settings.PollSeconds });
        });
        return new ImportSummary(backup.Mappings.Count, backup.Sessions.Count);
    }

    public sealed class TotalRow
    {
        public string PackageName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public long SteamAppId { get; set; }
        public long DurationTicks { get; set; }
    }
}
