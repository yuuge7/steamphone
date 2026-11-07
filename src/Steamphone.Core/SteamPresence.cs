using SteamKit2;
using SteamKit2.Internal;

namespace Steamphone.Core;

public static class SteamPresence
{
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
