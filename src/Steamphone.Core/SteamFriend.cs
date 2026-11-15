using SteamKit2;

namespace Steamphone.Core;

public sealed record SteamFriend(ulong SteamId, string Name, EPersonaState State, uint AppId, string? GameName)
{
    public bool IsOnline => State is not (EPersonaState.Offline or EPersonaState.Invisible);
    public bool IsPlaying => IsOnline && (AppId > 0 || !string.IsNullOrWhiteSpace(GameName));
    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : System.Globalization.StringInfo.GetNextTextElement(Name).ToUpperInvariant();
    public string Activity => IsPlaying ? string.IsNullOrWhiteSpace(GameName) ? $"În joc · AppID {AppId}" : GameName : State switch
    {
        EPersonaState.Away => "Plecat", EPersonaState.Busy => "Ocupat", EPersonaState.Snooze => "Inactiv",
        EPersonaState.Offline or EPersonaState.Invisible => "Offline", _ => "Online"
    };
    public string ProfileUrl => $"https://steamcommunity.com/profiles/{SteamId}";
}
