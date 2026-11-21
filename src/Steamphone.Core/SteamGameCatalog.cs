using System.Collections.Concurrent;
using System.Text.Json;
using SQLite;

namespace Steamphone.Core;

[Table("SteamGameTitles")]
public sealed class SteamGameTitle
{
    [PrimaryKey] public long AppId { get; set; }
    public string Name { get; set; } = "";
    public long UpdatedUtcTicks { get; set; }
}

/// <summary>Public Steam store titles, cached independently of mappings and history. No API key or login.</summary>
public sealed class SteamGameCatalog(HttpClient http, AppDatabase database)
{
    private readonly SemaphoreSlim downloads = new(4, 4);
    private readonly ConcurrentDictionary<uint, SemaphoreSlim> requests = new();
    private readonly ConcurrentDictionary<uint, DateTime> retryAfter = new();

    public async Task<IReadOnlyDictionary<uint, string>> GetCachedNamesAsync(IEnumerable<uint> appIds)
    {
        var ids = appIds.ToHashSet();
        return (await database.GetSteamGameTitlesAsync()).Where(x => ids.Contains((uint)x.AppId))
            .ToDictionary(x => (uint)x.AppId, x => x.Name);
    }

    public async Task<string?> GetNameAsync(uint appId, CancellationToken cancellation = default)
    {
        if (appId == 0) return null;
        cancellation.ThrowIfCancellationRequested();
        var cached = await database.GetSteamGameTitleAsync(appId);
        if ((await database.GetSettingsAsync()).Visibility == VisibilityMode.LocalOnly) return cached?.Name;
        if (IsFresh(cached) || retryAfter.GetValueOrDefault(appId) > DateTime.UtcNow) return cached?.Name;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var gate = requests.GetOrAdd(appId, _ => new(1, 1));
        var entered = false;
        var downloading = false;
        var attempted = false;
        try
        {
            await gate.WaitAsync(timeout.Token);
            entered = true;
            cached = await database.GetSteamGameTitleAsync(appId);
            if (IsFresh(cached) || retryAfter.GetValueOrDefault(appId) > DateTime.UtcNow) return cached?.Name;
            await downloads.WaitAsync(timeout.Token);
            downloading = true;
            attempted = true;
            using var response = await http.GetAsync(
                $"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic&l=english", timeout.Token);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (body.Length > 256 * 1024) return Unavailable(appId, cached);
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(appId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var app) ||
                app.ValueKind != JsonValueKind.Object || !app.TryGetProperty("success", out var success) ||
                success.ValueKind != JsonValueKind.True || !app.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("name", out var title) ||
                title.ValueKind != JsonValueKind.String || !data.TryGetProperty("steam_appid", out var returnedId) ||
                returnedId.ValueKind != JsonValueKind.Number || !returnedId.TryGetUInt32(out var id) || id != appId)
                return Unavailable(appId, cached);
            var name = title.GetString()?.Trim();
            if (name is not { Length: > 0 and <= 240 }) return Unavailable(appId, cached);
            await database.SaveSteamGameTitleAsync(new() { AppId = appId, Name = name, UpdatedUtcTicks = DateTime.UtcNow.Ticks });
            retryAfter.TryRemove(appId, out _);
            return name;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { return attempted ? Unavailable(appId, cached) : cached?.Name; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or SQLiteException)
        { return attempted ? Unavailable(appId, cached) : cached?.Name; }
        finally
        {
            if (downloading) downloads.Release();
            if (entered) gate.Release();
        }
    }

    private string? Unavailable(uint appId, SteamGameTitle? cached)
    {
        retryAfter[appId] = DateTime.UtcNow.AddMinutes(10);
        return cached?.Name;
    }
    private static bool IsFresh(SteamGameTitle? cached) => cached is { Name.Length: > 0 } &&
        DateTime.UtcNow.Ticks - cached.UpdatedUtcTicks < TimeSpan.FromDays(7).Ticks;
}
