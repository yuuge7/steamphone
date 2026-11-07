using Android.App;
using Android.Content;
using Android.Provider;
using Steamphone.Services;
using Uri = Android.Net.Uri;

namespace Steamphone.Platforms.Android;

public sealed class AndroidBackupDocuments : IBackupDocuments
{
    private static MainActivity Activity => Platform.CurrentActivity as MainActivity
        ?? throw new InvalidOperationException("Deschide aplicația pentru a selecta un fișier.");

    public async Task<string?> SaveAsync(string suggestedName, byte[] contents)
    {
        using var intent = new Intent(Intent.ActionCreateDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("application/json");
        intent.PutExtra(Intent.ExtraTitle, suggestedName);
        intent.PutExtra(DocumentsContract.ExtraInitialUri,
            DocumentsContract.BuildDocumentUri("com.android.externalstorage.documents", "primary:Download"));
        var uri = await Activity.ChooseBackupDocumentAsync(intent);
        if (uri is null) return null;
        var resolver = Activity.ContentResolver!;
        try
        {
            await using var stream = resolver.OpenOutputStream(uri, "w")
                ?? throw new IOException("Locația aleasă nu permite salvarea fișierului.");
            await stream.WriteAsync(contents);
            await stream.FlushAsync();
        }
        catch
        {
            try { DocumentsContract.DeleteDocument(resolver, uri); }
            catch (Exception) { /* A document provider may not support deletion after a failed write. */ }
            throw;
        }
        return GetName(uri) ?? suggestedName;
    }

    public async Task<BackupDocument?> OpenAsync()
    {
        using var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        // Some document providers incorrectly classify JSON as text/plain or octet-stream.
        // The codec validates the content rather than trusting its MIME type or extension.
        intent.SetType("*/*");
        var uri = await Activity.ChooseBackupDocumentAsync(intent);
        if (uri is null) return null;
        var stream = Activity.ContentResolver!.OpenInputStream(uri)
            ?? throw new IOException("Fișierul selectat nu poate fi citit.");
        return new BackupDocument(GetName(uri) ?? "backup.json", stream);
    }

    private static string? GetName(Uri uri)
    {
        using var cursor = Activity.ContentResolver!.Query(uri, [IOpenableColumns.DisplayName!], null, null, null);
        return cursor?.MoveToFirst() == true ? cursor.GetString(0) : null;
    }
}
