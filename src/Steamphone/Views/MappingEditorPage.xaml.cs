using Steamphone.ViewModels;
namespace Steamphone.Views;
public partial class MappingEditorPage : ContentPage
{
    private readonly MappingEditorViewModel viewModel;
    public MappingEditorPage(MappingEditorViewModel viewModel) { InitializeComponent(); BindingContext = this.viewModel = viewModel; }
    protected override void OnDisappearing() { viewModel.Deactivate(); base.OnDisappearing(); }
}
