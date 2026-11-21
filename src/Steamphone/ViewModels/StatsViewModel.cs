using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Steamphone.Core;

namespace Steamphone.ViewModels;

public partial class StatsViewModel(AppDatabase database, PlaytimeTracker tracker, SteamGameCatalog catalog) : BaseViewModel
{
    private CancellationTokenSource? namesLifetime;
    public ObservableCollection<GameTotalRowViewModel> Games { get; } = [];
    public void Deactivate() { namesLifetime?.Cancel(); namesLifetime?.Dispose(); namesLifetime = null; }
    [ObservableProperty] private string totalTime = "0 ore și 0 minute";
    public override Task RefreshAsync() => PerformAsync(async () =>
    {
        Deactivate();
        namesLifetime = new();
        var token = namesLifetime.Token;
        await tracker.FlushAsync();
        var totals = await database.GetTotalsAsync();
        var cached = await catalog.GetCachedNamesAsync(totals.Select(x => (uint)x.SteamAppId));
        if (token.IsCancellationRequested) return;
        Games.Clear();
        foreach (var total in totals)
        {
            var row = new GameTotalRowViewModel(total);
            if (cached.TryGetValue(row.SteamAppId, out var name)) row.SteamGameName = name;
            Games.Add(row);
        }
        TotalTime = GameTotal.FormatDuration(TimeSpan.FromTicks(totals.Sum(x => x.DurationTicks)));
        _ = SteamGameRowViewModel.LoadNamesAsync(Games.ToArray(), catalog, token);
    });
    [RelayCommand] private Task ReloadAsync() => RefreshAsync();
}
