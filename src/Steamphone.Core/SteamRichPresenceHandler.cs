using SteamKit2;
using SteamKit2.Internal;

namespace Steamphone.Core;

// Forward through the existing callback pump so roster changes remain serialized.
internal sealed class SteamRichPresenceHandler : ClientMsgHandler
{
    public override void HandleMsg(IPacketMsg packetMsg)
    {
        if (packetMsg.MsgType == EMsg.ClientRichPresenceInfo)
            Client.PostCallback(new RichPresenceCallback(new ClientMsgProtobuf<CMsgClientRichPresenceInfo>(packetMsg).Body));
    }
}

internal sealed class RichPresenceCallback(CMsgClientRichPresenceInfo body) : CallbackMsg
{
    public CMsgClientRichPresenceInfo Body { get; } = body;
}
