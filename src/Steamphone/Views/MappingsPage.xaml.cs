using Steamphone.ViewModels;
namespace Steamphone.Views;
public partial class MappingsPage : ContentPage
{
    private readonly MappingsViewModel viewModel;
    public MappingsPage(MappingsViewModel viewModel) { InitializeComponent(); BindingContext = this.viewModel = viewModel; }
    protected override async void OnAppearing() { base.OnAppearing(); await viewModel.RefreshAsync(); }
    protected override void OnDisappearing() { viewModel.Deactivate(); base.OnDisappearing(); }
}
