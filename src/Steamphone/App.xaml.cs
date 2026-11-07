using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Steamphone.Core;
using Steamphone.Views;
using Steamphone.ViewModels;

namespace Steamphone;

public partial class App : Application
{
    private readonly IServiceProvider services;
    public App(IServiceProvider services) { InitializeComponent(); this.services = services; }
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var login = services.GetRequiredService<LoginPage>();
        login.AutoResume = true;
        var window = new Window(new NavigationPage(login));
        window.Resumed += async (_, _) =>
        {
            var page = window.Page is Shell shell ? shell.CurrentPage : (window.Page as NavigationPage)?.CurrentPage;
            if (page?.BindingContext is BaseViewModel viewModel)
            {
                try { await viewModel.RefreshAsync(); }
                catch (Exception ex) { viewModel.Error = ex.Message; }
            }
        };
        return window;
    }
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}
