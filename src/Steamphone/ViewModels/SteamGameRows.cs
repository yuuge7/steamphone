using CommunityToolkit.Mvvm.ComponentModel;
using Steamphone.Core;

namespace Steamphone.ViewModels;

public abstract partial class SteamGameRowViewModel : ObservableObject
{
    public abstract uint SteamAppId { get; }
    [ObservableProperty] private string steamGameName = "Se încarcă numele jocului…";

    public static async Task LoadNamesAsync(IEnumerable<SteamGameRowViewModel> rows, SteamGameCatalog catalog, CancellationToken token)
    {
        await Task.WhenAll(rows.GroupBy(x => x.SteamAppId).Select(async group =>
        {
            try
            {
                var name = await catalog.GetNameAsync(group.Key, token);
                if (!token.IsCancellationRequested)
                    foreach (var row in group) row.SteamGameName = name ?? "Numele jocului nu este disponibil";
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                if (!token.IsCancellationRequested)
                    foreach (var row in group) row.SteamGameName = "Numele jocului nu este disponibil";
            }
        }));
    }
}

public sealed class MappingRowViewModel(AppMapping mapping) : SteamGameRowViewModel
{
    public AppMapping Mapping { get; } = mapping;
    public override uint SteamAppId => (uint)Mapping.SteamAppId;
    public string DisplayName => Mapping.DisplayName;
    public bool Enabled => Mapping.Enabled;
}

public sealed class GameTotalRowViewModel(GameTotal total) : SteamGameRowViewModel
{
    public override uint SteamAppId => (uint)total.SteamAppId;
    public string DisplayName => total.DisplayName;
    public string DurationLabel => total.DurationLabel;
}
