using ProtoBuf;
using SteamKit2;
using SteamKit2.Internal;
using Xunit;

namespace Steamphone.Core.Tests;

public sealed class PresenceProtocolTests
{
    [Theory]
    [InlineData(570u)]
    [InlineData(uint.MaxValue)]
    public void GamesPlayedPreservesAppIdInActualProtobuf(uint appId)
    {
        using var stream = new MemoryStream();
        Serializer.Serialize(stream, SteamPresence.GamesPlayed(appId).Body);
        stream.Position = 0;
        var decoded = Serializer.Deserialize<CMsgClientGamesPlayed>(stream);
        Assert.Equal((ulong)appId, Assert.Single(decoded.games_played).game_id);
    }

    [Fact]
    public void EndingGameSendsEmptyPlayedSet()
    {
        Assert.Single(SteamPresence.GamesPlayed(570).Body.games_played);
        using var stream = new MemoryStream();
        Serializer.Serialize(stream, SteamPresence.GamesPlayed(null).Body);
        stream.Position = 0;
        Assert.Empty(Serializer.Deserialize<CMsgClientGamesPlayed>(stream).games_played);
    }

    [Theory]
    [InlineData(BaseStatus.Online, EPersonaState.Online)]
    [InlineData(BaseStatus.Away, EPersonaState.Away)]
    [InlineData(BaseStatus.Busy, EPersonaState.Busy)]
    [InlineData(BaseStatus.Snooze, EPersonaState.Snooze)]
    [InlineData(BaseStatus.Offline, EPersonaState.Offline)]
    public void IdleStatusRestoresWhileMappedGameUsesOnline(BaseStatus status, EPersonaState expected)
    {
        Assert.Equal(expected, SteamPresence.Persona(null, status));
        Assert.Equal(EPersonaState.Online, SteamPresence.Persona(570, status));
    }
}
