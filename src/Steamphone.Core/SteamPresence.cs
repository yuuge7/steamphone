using SteamKit2;
using SteamKit2.Internal;

namespace Steamphone.Core;

public static class SteamPresence
{
    // Private modes suppress game IDs by default, including during login or reconnect.
    // Invisible can explicitly opt into game announcements; Steam-side hours are never promised.
    public static PresencePlan Resolve(uint? appId, BaseStatus idleStatus, VisibilityMode visibility,
        bool announceGameWhileInvisible = false) => visibility switch
    {
        VisibilityMode.Public => new(appId is > 0 ? appId : null, Persona(appId, idleStatus), true),
        VisibilityMode.Invisible => new(announceGameWhileInvisible && appId is > 0 ? appId : null, EPersonaState.Invisible, true),
        VisibilityMode.OnlineOnly => new(null, EPersonaState.Online, true),
        _ => new(null, EPersonaState.Offline, false)
    };

    public static EPersonaState Persona(uint? appId, BaseStatus idleStatus) => appId is > 0
        ? EPersonaState.Online
        : idleStatus switch
        {
            BaseStatus.Away => EPersonaState.Away, BaseStatus.Busy => EPersonaState.Busy,
            BaseStatus.Snooze => EPersonaState.Snooze, BaseStatus.Offline => EPersonaState.Offline,
            _ => EPersonaState.Online
        };

    public static ClientMsgProtobuf<CMsgClientGamesPlayed> GamesPlayed(uint? appId)
    {
        var message = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);
        if (appId is > 0)
            message.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed { game_id = appId.Value });
        return message;
    }
}

public sealed record PresencePlan(uint? AppId, EPersonaState Persona, bool Connect);

/// <summary>Serialized by SteamConnection. Caches only packets actually sent on this transport.</summary>
public sealed class SteamPresenceWriter(Action<EPersonaState> setPersona, Action<uint?> setGame)
{
    private bool personaSent, gameSent;
    private EPersonaState persona;
    private uint? appId;

    public void Reset() { personaSent = gameSent = false; appId = null; }
    public void ResetPlayingSession() => gameSent = false;

    public void Apply(PresencePlan plan, bool playingBlocked)
    {
        if (!playingBlocked && appId.HasValue && plan.AppId is null)
        {
            setGame(null);
            appId = null;
            gameSent = true;
        }
        if (!personaSent || persona != plan.Persona)
        {
            setPersona(plan.Persona);
            persona = plan.Persona;
            personaSent = true;
        }
        // Even clearing GamesPlayed can disconnect a client while another device owns the session.
        if (playingBlocked) return;
        if (!gameSent || appId != plan.AppId)
        {
            setGame(plan.AppId);
            appId = plan.AppId;
            gameSent = true;
        }
    }
}
