using Microsoft.Extensions.DependencyInjection;
using Steamphone.ViewModels;
using Steamphone.Views;

namespace Steamphone.Services;

public sealed class AppNavigator(IServiceProvider services)
{
    private static Window Window => Application.Current!.Windows[0];
    public Task ShowMainAsync() => MainThread.InvokeOnMainThreadAsync(() =>
        Window.Page = services.GetRequiredService<AppShell>());
    public Task ShowLoginAsync() => MainThread.InvokeOnMainThreadAsync(() =>
        Window.Page = new NavigationPage(services.GetRequiredService<LoginPage>()));

    public async Task OpenMappingAsync(Core.AppMapping? mapping = null)
    {
        var page = services.GetRequiredService<MappingEditorPage>();
        await Shell.Current.Navigation.PushAsync(page);
        await ((MappingEditorViewModel)page.BindingContext).InitializeAsync(mapping);
    }

    public Task CloseEditorAsync() => Shell.Current.Navigation.PopAsync();
}
