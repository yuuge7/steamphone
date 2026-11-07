using Steamphone.Core;
using Steamphone.ViewModels;

namespace Steamphone.Views;

public partial class LoginPage : ContentPage
{
    private readonly LoginViewModel viewModel;
    private readonly AppDatabase database;
    public bool AutoResume { get; set; }
    public LoginPage(LoginViewModel viewModel, AppDatabase database)
    {
        InitializeComponent(); BindingContext = this.viewModel = viewModel; this.database = database;
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            var settings = await database.GetSettingsAsync();
            Application.Current!.UserAppTheme = settings.Theme switch { "Dark" => AppTheme.Dark, "Light" => AppTheme.Light, _ => AppTheme.Unspecified };
            await viewModel.RefreshAsync();
            if (AutoResume && viewModel.HasSavedAccount)
            {
                AutoResume = false;
                await viewModel.ResumeCommand.ExecuteAsync(null);
            }
        }
        catch (Exception ex) { viewModel.Error = ex.Message; }
        finally { AutoResume = false; }
    }
}
