using CommunityToolkit.Mvvm.ComponentModel;

namespace Steamphone.ViewModels;

public abstract partial class BaseViewModel : ObservableObject
{
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string error = "";
    public bool HasError => !string.IsNullOrEmpty(Error);
    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    protected async Task PerformAsync(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        Error = "";
        try { await action(); }
        catch (OperationCanceledException) { Error = "Operația a fost anulată."; }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsBusy = false; }
    }
    public virtual Task RefreshAsync() => Task.CompletedTask;
}
