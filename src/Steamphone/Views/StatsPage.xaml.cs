using Steamphone.ViewModels;
namespace Steamphone.Views;
public partial class StatsPage : ContentPage
{
    private readonly StatsViewModel viewModel;
    public StatsPage(StatsViewModel viewModel) { InitializeComponent(); BindingContext = this.viewModel = viewModel; }
    protected override async void OnAppearing() { base.OnAppearing(); await viewModel.RefreshAsync(); }
    protected override void OnDisappearing() { viewModel.Deactivate(); base.OnDisappearing(); }
}
