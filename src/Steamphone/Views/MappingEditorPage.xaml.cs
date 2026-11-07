using Steamphone.ViewModels;
namespace Steamphone.Views;
public partial class MappingEditorPage : ContentPage
{
    public MappingEditorPage(MappingEditorViewModel viewModel) { InitializeComponent(); BindingContext = viewModel; }
}
