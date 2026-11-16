using Steamphone.ViewModels;

namespace Steamphone.Views;

public partial class FriendsPage : ContentPage
{
    private readonly FriendsViewModel viewModel;
    private readonly IDispatcherTimer updateTimer;
    private bool visible;
    private Window? observedWindow;
    public FriendsPage(FriendsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = this.viewModel = viewModel;
        updateTimer = Dispatcher.CreateTimer();
        updateTimer.Interval = TimeSpan.FromMilliseconds(250);
        updateTimer.IsRepeating = false;
        updateTimer.Tick += (_, _) => viewModel.FlushUpdates();
        viewModel.UpdatesPending += () => Dispatcher.Dispatch(() => { if (visible && !updateTimer.IsRunning) updateTimer.Start(); });
    }
    protected override async void OnAppearing()
    {
        observedWindow = Window;
        if (observedWindow is not null)
        {
            observedWindow.Stopped += OnStopped;
            observedWindow.Resumed += OnResumed;
        }
        base.OnAppearing(); visible = true; viewModel.Activate(); await viewModel.RefreshAsync();
    }
    protected override void OnDisappearing()
    {
        if (observedWindow is not null)
        {
            observedWindow.Stopped -= OnStopped;
            observedWindow.Resumed -= OnResumed;
            observedWindow = null;
        }
        visible = false; updateTimer.Stop(); viewModel.Deactivate(); base.OnDisappearing();
    }
    private void OnStopped(object? sender, EventArgs args)
    {
        visible = false; updateTimer.Stop(); viewModel.Deactivate();
    }
    private void OnResumed(object? sender, EventArgs args)
    {
        visible = true; viewModel.Activate();
    }
    private async void OnRosterScrolled(object? sender, ItemsViewScrolledEventArgs args) =>
        await viewModel.LoadVisibleAvatarsAsync(args.FirstVisibleItemIndex, args.LastVisibleItemIndex);
    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (StatsStrip is null) return;
        var compact = height < 650;
        StatsStrip.IsVisible = !compact;
        CommunityEyebrow.IsVisible = !compact;
        CompactSummary.IsVisible = compact;
        RosterHeading.Spacing = compact ? 8 : 16;
        RosterLayout.RowSpacing = compact ? 10 : 14;
    }
    protected override bool OnBackButtonPressed()
    {
        if (!viewModel.HasSelectedFriend) return base.OnBackButtonPressed();
        viewModel.CloseFriendCommand.Execute(null);
        return true;
    }
}
