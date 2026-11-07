using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Content;

namespace Steamphone;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, Exported = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation |
    ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    private const int BackupRequest = 17041;
    private TaskCompletionSource<global::Android.Net.Uri?>? pendingDocument;

    public Task<global::Android.Net.Uri?> ChooseBackupDocumentAsync(Intent intent)
    {
        if (pendingDocument is not null) throw new InvalidOperationException("Există deja un selector de fișiere deschis.");
        var completion = new TaskCompletionSource<global::Android.Net.Uri?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingDocument = completion;
        try
        {
#pragma warning disable CS0618 // MAUI's MainActivity owns this request code and forwards all other results to base.
            StartActivityForResult(intent, BackupRequest);
#pragma warning restore CS0618
        }
        catch { pendingDocument = null; throw; }
        return completion.Task;
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != BackupRequest) return;
        var completion = pendingDocument;
        pendingDocument = null;
        completion?.TrySetResult(resultCode == Result.Ok ? data?.Data : null);
    }
}
