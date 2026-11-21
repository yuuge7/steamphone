using SQLite;

namespace Steamphone.Core;

public enum BaseStatus { Online, Away, Busy, Snooze, Offline }
public enum VisibilityMode { Public, Invisible, OnlineOnly, LocalOnly }

[Table("Mappings")]
public sealed class AppMapping
{
    [PrimaryKey] public string PackageName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public long SteamAppId { get; set; }
    public bool Enabled { get; set; } = true;
}

[Table("PlaytimeStats")]
public sealed class PlaytimeStats
{
    [PrimaryKey] public string SessionId { get; set; } = Guid.NewGuid().ToString("N");
    [Indexed] public string PackageName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public long SteamAppId { get; set; }
    public long StartedUtcTicks { get; set; }
    public long LastObservedUtcTicks { get; set; }
    public long DurationTicks { get; set; }
    public bool IsOpen { get; set; }
}

[Table("Settings")]
public sealed class AppSettings
{
    [PrimaryKey] public int Id { get; set; } = 1;
    public BaseStatus DefaultStatus { get; set; } = BaseStatus.Online;
    public string Theme { get; set; } = "System";
    public int PollSeconds { get; set; } = 2;
    public VisibilityMode Visibility { get; set; } = VisibilityMode.Public;
    public bool KeepConnectionWhenLocked { get; set; }
    public bool AnnounceGameWhileInvisible { get; set; }
}

public sealed record GameTotal(string PackageName, string DisplayName, long SteamAppId, long DurationTicks)
{
    public string DurationLabel => FormatDuration(TimeSpan.FromTicks(DurationTicks));
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMinutes < 1)
        {
            var seconds = Math.Max(0, (int)duration.TotalSeconds);
            return $"{seconds} {(seconds == 1 ? "secundă" : "secunde")}";
        }
        var hours = (long)duration.TotalHours;
        var minutes = duration.Minutes;
        var hourLabel = $"{hours} {(hours == 1 ? "oră" : "ore")}";
        var minuteLabel = $"{minutes} {(minutes == 1 ? "minut" : "minute")}";
        return hours == 0 ? minuteLabel : minutes == 0 ? hourLabel : $"{hourLabel} și {minuteLabel}";
    }
}

public sealed record SavedAccount(string Username, string RefreshToken, string? GuardData);

public interface ICredentialStore
{
    Task<SavedAccount?> ReadAsync();
    Task WriteAsync(SavedAccount account);
    Task ClearAsync();
}
