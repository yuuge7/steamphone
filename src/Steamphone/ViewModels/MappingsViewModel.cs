using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Steamphone.Core;
using Steamphone.Services;

namespace Steamphone.ViewModels;

public partial class MappingsViewModel(AppDatabase database, AppNavigator navigator, SteamGameCatalog catalog) : BaseViewModel
{
    private CancellationTokenSource? namesLifetime;
    public ObservableCollection<MappingRowViewModel> Mappings { get; } = [];
    public void Deactivate() { namesLifetime?.Cancel(); namesLifetime?.Dispose(); namesLifetime = null; }
    public override Task RefreshAsync() => PerformAsync(async () =>
    {
        Deactivate();
        namesLifetime = new();
        var token = namesLifetime.Token;
        var mappings = await database.GetMappingsAsync();
        var cached = await catalog.GetCachedNamesAsync(mappings.Select(x => (uint)x.SteamAppId));
        if (token.IsCancellationRequested) return;
        Mappings.Clear();
        foreach (var mapping in mappings)
        {
            var row = new MappingRowViewModel(mapping);
            if (cached.TryGetValue(row.SteamAppId, out var name)) row.SteamGameName = name;
            Mappings.Add(row);
        }
        _ = SteamGameRowViewModel.LoadNamesAsync(Mappings.ToArray(), catalog, token);
    });
    [RelayCommand] private Task AddAsync() => PerformAsync(() => navigator.OpenMappingAsync());
    [RelayCommand] private Task EditAsync(MappingRowViewModel row) => PerformAsync(() => navigator.OpenMappingAsync(row.Mapping));
    [RelayCommand] private Task DeleteAsync(MappingRowViewModel row) => PerformAsync(async () =>
    {
        var mapping = row.Mapping;
        var confirm = await Shell.Current.DisplayAlertAsync("Ștergi maparea?", $"{mapping.DisplayName}: istoricul local se păstrează.", "Șterge", "Renunță");
        if (!confirm) return;
        await database.DeleteMappingAsync(mapping.PackageName);
        Mappings.Remove(row);
    });
}
