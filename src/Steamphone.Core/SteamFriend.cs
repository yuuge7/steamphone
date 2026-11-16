using SteamKit2;

namespace Steamphone.Core;

public sealed record SteamFriend(ulong SteamId, string Name, EPersonaState State, uint AppId, string? GameName)
{
    public string? AvatarHash { get; init; }
    public DateTime? LastLogOffUtc { get; init; }
    public EPersonaStateFlag StateFlags { get; init; }
    public string RichActivity { get; init; } = "";
    public bool IsOnline => State is not (EPersonaState.Offline or EPersonaState.Invisible);
    public bool IsPlaying => IsOnline && (AppId > 0 || !string.IsNullOrWhiteSpace(GameName));
    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : System.Globalization.StringInfo.GetNextTextElement(Name).ToUpperInvariant();
    public string Activity => IsPlaying ? string.IsNullOrWhiteSpace(GameName) ? $"În joc · AppID {AppId}" : GameName : State switch
    {
        EPersonaState.Away => "Plecat", EPersonaState.Busy => "Ocupat", EPersonaState.Snooze => "Inactiv",
        EPersonaState.Offline or EPersonaState.Invisible => "Offline", _ => "Online"
    };
    public string ProfileUrl => $"https://steamcommunity.com/profiles/{SteamId}";
    public string Device => !IsOnline ? "" : StateFlags.HasFlag(EPersonaStateFlag.ClientTypeMobile) ? "Pe mobil"
        : StateFlags.HasFlag(EPersonaStateFlag.ClientTypeWeb) ? "În browser"
        : StateFlags.HasFlag(EPersonaStateFlag.ClientTypeVR) ? "În VR"
        : StateFlags.HasFlag(EPersonaStateFlag.ClientTypeTenfoot) ? "Big Picture" : "";
    public bool IsJoinable => IsPlaying && StateFlags.HasFlag(EPersonaStateFlag.InJoinableGame);
    public string LastSeen => FormatLastSeen(LastLogOffUtc, DateTime.UtcNow);
    public string SecondaryActivity => !IsOnline ? LastSeen : IsPlaying && RichActivity.Length > 0 ? RichActivity
        : string.Join(" · ", new[] { Device, IsJoinable ? "Joc accesibil prietenilor" : "" }.Where(x => x.Length > 0));

    // Persona callbacks contain partial updates. A name-only packet must not erase last-seen/device data.
    public SteamFriend MergeDetails(EClientPersonaStateFlag fields, EPersonaStateFlag flags, DateTime lastLogOffUtc) => this with
    {
        StateFlags = fields.HasFlag(EClientPersonaStateFlag.Presence) ? flags : StateFlags,
        LastLogOffUtc = fields.HasFlag(EClientPersonaStateFlag.LastSeen)
            ? lastLogOffUtc > DateTime.UnixEpoch ? DateTime.SpecifyKind(lastLogOffUtc, DateTimeKind.Utc) : null
            : LastLogOffUtc
    };

    public static string? NormalizeAvatarHash(byte[]? hash) => hash is { Length: 20 } && hash.Any(x => x != 0)
        ? Convert.ToHexStringLower(hash) : null;

    public SteamFriend MergeGameDetails(EClientPersonaStateFlag fields, uint appId, string? gameName) =>
        (fields & (EClientPersonaStateFlag.GameExtraInfo | EClientPersonaStateFlag.GameDataBlob)) == 0 ? this
            : this with { AppId = appId, GameName = gameName, RichActivity = AppId == appId ? RichActivity : "" };

    public static string FormatLastSeen(DateTime? lastSeen, DateTime nowUtc)
    {
        if (lastSeen is null || lastSeen <= DateTime.UnixEpoch || lastSeen > nowUtc) return "";
        var elapsed = nowUtc - lastSeen.Value;
        return elapsed.TotalMinutes < 1 ? "Ultima conectare: acum câteva clipe"
            : elapsed.TotalHours < 1 ? $"Ultima conectare: acum {(int)elapsed.TotalMinutes} min"
            : elapsed.TotalDays < 1 ? $"Ultima conectare: acum {(int)elapsed.TotalHours} h"
            : $"Ultima conectare: acum {(int)elapsed.TotalDays} zile";
    }
}

public sealed record SteamFriendProfile(string RealName, string Location, DateTime? CreatedUtc, string Headline)
{
    public string MemberSince => CreatedUtc is { } created ? $"Pe Steam din {created:yyyy}" : "";
}
