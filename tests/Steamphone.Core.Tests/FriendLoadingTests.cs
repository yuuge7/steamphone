using System.Collections.ObjectModel;
using System.Collections.Specialized;
using SteamKit2;
using Steamphone.Core;
using Xunit;

namespace Steamphone.Core.Tests;

public sealed class FriendLoadingTests
{
    [Fact]
    public void PersonasAreReadyOnlyAfterAllRequestedFieldsArrive()
    {
        var progress = new FriendLoadProgress();
        progress.Receive(1, EClientPersonaStateFlag.PlayerName | EClientPersonaStateFlag.Presence);
        Assert.False(progress.IsLoaded(1));
        progress.Receive(1, EClientPersonaStateFlag.LastSeen);
        Assert.False(progress.IsLoaded(1));
        progress.Receive(1, EClientPersonaStateFlag.GameExtraInfo);
        Assert.True(progress.IsLoaded(1));
        progress.Receive(1, EClientPersonaStateFlag.PlayerName);
        Assert.True(progress.IsLoaded(1));
        Assert.False(progress.IsLoaded(2));
    }

    [Fact]
    public void RemovedFriendsAndDisconnectedSessionsDoNotKeepStaleReadiness()
    {
        var progress = new FriendLoadProgress();
        progress.Receive(1, FriendLoadProgress.RequiredFields);
        progress.Receive(2, FriendLoadProgress.RequiredFields);
        progress.Retain(new HashSet<ulong> { 2 });
        Assert.False(progress.IsLoaded(1));
        Assert.True(progress.IsLoaded(2));
        progress.Clear();
        Assert.False(progress.IsLoaded(2));
    }

    [Fact]
    public void LiveRosterChangesUseMovesAndPreserveExistingRowsWithoutResettingTheList()
    {
        var original = Enumerable.Range(0, 500).Select(_ => new object()).ToArray();
        var rows = new ObservableCollection<object>(original);
        var notifications = new List<NotifyCollectionChangedAction>();
        rows.CollectionChanged += (_, args) => notifications.Add(args.Action);
        var desired = new[] { original[499], new object() }.Concat(original.Take(498)).ToArray();
        rows.Synchronize(desired);
        Assert.Equal(desired, rows);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, notifications);
        Assert.Equal(3, notifications.Count); // One removed, one moved, one inserted.
        notifications.Clear();
        rows.Synchronize(desired);
        Assert.Empty(notifications);
    }
}
