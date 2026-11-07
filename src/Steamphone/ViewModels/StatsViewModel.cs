using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Steamphone.Core;

namespace Steamphone.ViewModels;

public partial class StatsViewModel(AppDatabase database) : BaseViewModel
{
    public ObservableCollection<GameTotal> Games { get; } = [];
    [ObservableProperty] private string totalTime = "0 ore și 0 minute";
    public override Task RefreshAsync() => PerformAsync(async () =>
    {
        var totals = await database.GetTotalsAsync();
        Games.Clear();
        foreach (var total in totals) Games.Add(total);
        TotalTime = GameTotal.FormatDuration(TimeSpan.FromTicks(totals.Sum(x => x.DurationTicks)));
    });
    [RelayCommand] private Task ReloadAsync() => RefreshAsync();
}
