using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Steamphone.Core;
using Steamphone.Services;

namespace Steamphone.ViewModels;

public partial class MappingEditorViewModel(AppDatabase database, IDeviceServices device, AppNavigator navigator, SteamGameCatalog catalog) : BaseViewModel
{
    private CancellationTokenSource? nameLookup;
    private IReadOnlyList<InstalledApp> allApps = [];
    private AppMapping? original;
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private InstalledApp? selectedApp;
    [ObservableProperty] private string steamAppId = "";
    [ObservableProperty] private string steamGameName = "Introdu ID-ul pentru a vedea jocul Steam.";
    [ObservableProperty] private bool enabled = true;
    [ObservableProperty] private bool showSystemApps;
    [ObservableProperty] private string selectedLabel = "Selectează o aplicație din listă";
    public ObservableCollection<InstalledApp> Apps { get; } = [];
    public void Deactivate() { nameLookup?.Cancel(); nameLookup?.Dispose(); nameLookup = null; }

    partial void OnSteamAppIdChanged(string value)
    {
        Deactivate();
        if (!uint.TryParse(value.Trim(), out var id) || id == 0)
        {
            SteamGameName = "Introdu ID-ul pentru a vedea jocul Steam.";
            return;
        }
        nameLookup = new();
        SteamGameName = "Se încarcă numele jocului…";
        _ = ResolveNameAsync(id, nameLookup.Token);
    }
    private async Task ResolveNameAsync(uint id, CancellationToken token)
    {
        try
        {
            await Task.Delay(350, token);
            var name = await catalog.GetNameAsync(id, token);
            if (!token.IsCancellationRequested) SteamGameName = name ?? "Joc negăsit. Verifică ID-ul sau conexiunea.";
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!token.IsCancellationRequested) SteamGameName = "Numele jocului nu este disponibil."; }
    }

    public Task InitializeAsync(AppMapping? mapping) => PerformAsync(async () =>
    {
        original = mapping;
        SteamAppId = mapping?.SteamAppId.ToString() ?? "";
        Enabled = mapping?.Enabled ?? true;
        allApps = await device.GetInstalledAppsAsync(CancellationToken.None);
        var selected = allApps.FirstOrDefault(x => x.PackageName == mapping?.PackageName);
        if (mapping is not null && selected is null)
            throw new InvalidOperationException("Aplicația a fost dezinstalată. Reinstaleaz-o pentru a edita această mapare.");
        ShowSystemApps = selected?.IsSystem == true;
        Filter();
        SelectedApp = selected;
    });
    partial void OnSearchTextChanged(string value) => Filter();
    partial void OnShowSystemAppsChanged(bool value) => Filter();
    partial void OnSelectedAppChanged(InstalledApp? value) => SelectedLabel = value?.DisplayName ?? "Selectează o aplicație din listă";
    private void Filter()
    {
        var query = SearchText.Trim();
        Apps.Clear();
        foreach (var app in allApps.Where(x => (ShowSystemApps || !x.IsSystem) &&
                     (x.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) || x.PackageName.Contains(query, StringComparison.OrdinalIgnoreCase))))
            Apps.Add(app);
    }
    [RelayCommand] private Task SaveAsync() => PerformAsync(async () =>
    {
        if (SelectedApp is null) throw new ArgumentException("Selectează aplicația din listă.");
        if (!uint.TryParse(SteamAppId.Trim(), out var appId) || appId == 0)
            throw new ArgumentException("Introdu un Steam AppID valid, mai mare decât zero.");
        var mappings = await database.GetMappingsAsync();
        if (SelectedApp.PackageName != original?.PackageName && mappings.Any(x => x.PackageName == SelectedApp.PackageName))
            throw new ArgumentException("Aplicația are deja o mapare. Editeaz-o din lista Mapări.");
        // Preserve the package identity when editing; changing only the AppID starts a new statistics group.
        if (original is not null && SelectedApp.PackageName != original.PackageName)
            throw new ArgumentException("La editare păstrează aplicația selectată. Adaugă o mapare nouă pentru altă aplicație.");
        await database.SaveMappingAsync(new AppMapping
        {
            PackageName = SelectedApp.PackageName, DisplayName = SelectedApp.DisplayName,
            SteamAppId = appId, Enabled = Enabled
        });
        await navigator.CloseEditorAsync();
    });
}
