using System.Text;
using SteamKit2;
using Xunit;

namespace Steamphone.Core.Tests;

public sealed class PrivacyTests
{
    [Theory]
    [InlineData(VisibilityMode.Invisible, EPersonaState.Invisible, true)]
    [InlineData(VisibilityMode.OnlineOnly, EPersonaState.Online, true)]
    [InlineData(VisibilityMode.LocalOnly, EPersonaState.Offline, false)]
    public void PrivateModesNeverAnnounceGamesRegardlessOfIdleStatus(VisibilityMode mode, EPersonaState expected, bool connect)
    {
        foreach (var status in Enum.GetValues<BaseStatus>())
        foreach (uint? appId in new uint?[] { null, 570, uint.MaxValue })
        {
            var plan = SteamPresence.Resolve(appId, status, mode);
            Assert.Null(plan.AppId);
            Assert.Empty(SteamPresence.GamesPlayed(plan.AppId).Body.games_played);
            Assert.Equal(expected, plan.Persona);
            Assert.Equal(connect, plan.Connect);
        }
    }

    [Theory]
    [InlineData(VisibilityMode.Invisible)]
    [InlineData(VisibilityMode.OnlineOnly)]
    [InlineData(VisibilityMode.LocalOnly)]
    public void HidingAnActiveGameClearsItBeforeChangingPersonaAndNeverReannouncesAfterReconnect(VisibilityMode mode)
    {
        var packets = new List<string>();
        var writer = new SteamPresenceWriter(state => packets.Add($"persona:{state}"), id => packets.Add($"game:{id}"));
        writer.Apply(SteamPresence.Resolve(570, BaseStatus.Online, VisibilityMode.Public), false);
        packets.Clear();
        var hidden = SteamPresence.Resolve(570, BaseStatus.Away, mode);
        writer.Apply(hidden, false);
        Assert.Equal("game:", packets[0]);
        Assert.DoesNotContain("game:570", packets);
        packets.Clear();
        writer.Reset();
        writer.Apply(hidden, true);
        writer.Apply(hidden, false);
        Assert.DoesNotContain("game:570", packets);
        Assert.Single(packets, x => x.StartsWith("game:"));
    }

    [Fact]
    public void RepeatedObservationsSendNoPacketsAndBlockedSessionNeverSendsGamesPlayed()
    {
        var personas = new List<EPersonaState>();
        var games = new List<uint?>();
        var writer = new SteamPresenceWriter(personas.Add, games.Add);
        var plan = SteamPresence.Resolve(570, BaseStatus.Offline, VisibilityMode.Public);
        for (var i = 0; i < 100; i++) writer.Apply(plan, true);
        Assert.Single(personas);
        Assert.Empty(games);
        writer.Apply(plan, false);
        for (var i = 0; i < 100; i++) writer.Apply(plan, false);
        Assert.Equal(570u, Assert.Single(games));
        Assert.Single(personas);
        writer.ResetPlayingSession();
        writer.Apply(SteamPresence.Resolve(730, BaseStatus.Online, VisibilityMode.Invisible), true);
        Assert.Single(games);
        writer.Apply(SteamPresence.Resolve(730, BaseStatus.Online, VisibilityMode.Invisible), false);
        Assert.Null(games.Last());
    }

    [Fact]
    public void InvisibleGameAnnouncementRequiresExplicitOptInAndNeverSwitchesPersonaToOnline()
    {
        var packets = new List<string>();
        var writer = new SteamPresenceWriter(state => packets.Add($"persona:{state}"), id => packets.Add($"game:{id}"));
        var plan = SteamPresence.Resolve(570, BaseStatus.Online, VisibilityMode.Invisible, true);
        writer.Apply(plan, false);
        Assert.Equal(new[] { "persona:Invisible", "game:570" }, packets);
        packets.Clear();
        writer.Reset();
        writer.Apply(plan, true);
        writer.Apply(plan, false);
        Assert.Equal(new[] { "persona:Invisible", "game:570" }, packets);
        Assert.Null(SteamPresence.Resolve(570, BaseStatus.Online, VisibilityMode.OnlineOnly, true).AppId);
        Assert.Null(SteamPresence.Resolve(570, BaseStatus.Online, VisibilityMode.LocalOnly, true).AppId);
    }

    [Fact]
    public async Task OldBackupsDefaultToExistingBehaviorAndNewPrivacyPreferencesRoundTrip()
    {
        const string oldJson = """{"format":"steamphone-backup","version":1,"settings":{"defaultStatus":"Away","theme":"Dark","pollSeconds":3},"mappings":[],"sessions":[]}""";
        using var oldStream = new MemoryStream(Encoding.UTF8.GetBytes(oldJson));
        var backup = await BackupCodec.DecodeAsync(oldStream);
        Assert.Equal(VisibilityMode.Public, backup.Settings.Visibility);
        Assert.False(backup.Settings.AnnounceGameWhileInvisible);
        backup.Settings = backup.Settings with { Visibility = VisibilityMode.Invisible, KeepConnectionWhenLocked = true,
            AnnounceGameWhileInvisible = true };
        using var newStream = new MemoryStream(BackupCodec.Encode(backup));
        Assert.Equal(backup.Settings, (await BackupCodec.DecodeAsync(newStream)).Settings);
        backup.Settings = backup.Settings with { Visibility = (VisibilityMode)999 };
        Assert.Throws<InvalidDataException>(() => BackupCodec.Validate(backup));
    }

    [Fact]
    public void InvisibleAndOfflineFriendsNeverDisplayStaleGameActivity()
    {
        foreach (var state in new[] { EPersonaState.Invisible, EPersonaState.Offline })
        {
            var friend = new SteamFriend(1, "Someone", state, 570, "Dota 2");
            Assert.False(friend.IsPlaying);
            Assert.False(friend.IsOnline);
            Assert.Equal("Offline", friend.Activity);
        }
        Assert.Equal("În joc · AppID 570", new SteamFriend(1, "Someone", EPersonaState.Online, 570, null).Activity);
    }
}
