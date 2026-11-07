using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Steamphone.Core;
using Steamphone.Services;

namespace Steamphone.ViewModels;

public partial class MappingsViewModel(AppDatabase database, AppNavigator navigator) : BaseViewModel
{
    public ObservableCollection<AppMapping> Mappings { get; } = [];
    public override Task RefreshAsync() => PerformAsync(async () =>
    {
        var mappings = await database.GetMappingsAsync();
        Mappings.Clear();
        foreach (var mapping in mappings) Mappings.Add(mapping);
    });
    [RelayCommand] private Task AddAsync() => PerformAsync(() => navigator.OpenMappingAsync());
    [RelayCommand] private Task EditAsync(AppMapping mapping) => PerformAsync(() => navigator.OpenMappingAsync(mapping));
    [RelayCommand] private Task DeleteAsync(AppMapping mapping) => PerformAsync(async () =>
    {
        var confirm = await Shell.Current.DisplayAlertAsync("Ștergi maparea?", $"{mapping.DisplayName}: istoricul local se păstrează.", "Șterge", "Renunță");
        if (!confirm) return;
        await database.DeleteMappingAsync(mapping.PackageName);
        Mappings.Remove(mapping);
    });
}
