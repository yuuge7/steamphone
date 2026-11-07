using Microsoft.Extensions.DependencyInjection;
using Steamphone.Views;

namespace Steamphone;

public partial class AppShell : Shell
{
    public AppShell(IServiceProvider services)
    {
        InitializeComponent();
        HomeTab.ContentTemplate = new DataTemplate(() => services.GetRequiredService<DashboardPage>());
        MappingsTab.ContentTemplate = new DataTemplate(() => services.GetRequiredService<MappingsPage>());
        StatsTab.ContentTemplate = new DataTemplate(() => services.GetRequiredService<StatsPage>());
        SettingsTab.ContentTemplate = new DataTemplate(() => services.GetRequiredService<SettingsPage>());
    }
}
