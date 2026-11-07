using Steamphone.ViewModels;
namespace Steamphone.Views;
public partial class DashboardPage : ContentPage
{
    private readonly DashboardViewModel viewModel;
    public DashboardPage(DashboardViewModel viewModel) { InitializeComponent(); BindingContext = this.viewModel = viewModel; }
    protected override async void OnAppearing() { base.OnAppearing(); await viewModel.RefreshAsync(); }
}
