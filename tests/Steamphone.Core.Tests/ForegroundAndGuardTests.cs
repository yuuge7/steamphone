using Steamphone.Core;
using Xunit;

namespace Steamphone.Core.Tests;

public sealed class ForegroundAndGuardTests
{
    [Fact]
    public void LatePauseOfOldAppDoesNotClearNewForegroundApp()
    {
        var state = new ForegroundState();
        state.Resume("first", "Main");
        state.Resume("second", "Main");
        state.Pause("first", "Main");
        Assert.Equal("second", state.PackageName);
        state.Pause("second", "Main");
        Assert.Null(state.PackageName);
    }

    [Fact]
    public void SamePackageActivityTransitionAndScreenLockClearCorrectly()
    {
        var state = new ForegroundState();
        state.Resume("game", "FirstActivity");
        state.Resume("game", "SecondActivity");
        state.Pause("game", "FirstActivity");
        Assert.Equal("game", state.PackageName);
        state.Clear();
        Assert.Null(state.PackageName);
    }

    [Fact]
    public async Task GuardSupportsFreshCodeAfterRejection()
    {
        var guard = new SteamGuardBroker();
        guard.Begin(CancellationToken.None);
        GuardChallenge? shown = null;
        guard.Changed += challenge => shown = challenge;
        var first = guard.GetDeviceCodeAsync(false);
        Assert.True(shown?.RequiresCode);
        guard.Submit(" 12345 ");
        Assert.Equal("12345", await first);
        var retry = guard.GetDeviceCodeAsync(true);
        Assert.Contains("incorect", shown!.Message);
        guard.Submit("67890");
        Assert.Equal("67890", await retry);
    }

    [Fact]
    public async Task GuardCancellationDoesNotLeaveAuthenticationWaiting()
    {
        using var cancellation = new CancellationTokenSource();
        var guard = new SteamGuardBroker();
        guard.Begin(cancellation.Token);
        var code = guard.GetEmailCodeAsync("m***@mail.test", false);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => code);
        Assert.True(await guard.AcceptDeviceConfirmationAsync());
    }

    [Fact]
    public async Task GuardCanSelectCodeInsteadOfMobileApproval()
    {
        var guard = new SteamGuardBroker { PreferCode = true };
        Assert.False(await guard.AcceptDeviceConfirmationAsync());
        var code = guard.GetDeviceCodeAsync(false);
        guard.Submit("ABCDE");
        Assert.Equal("ABCDE", await code);
    }
}
