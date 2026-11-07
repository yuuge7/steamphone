namespace Steamphone.Services;

public sealed record BackupDocument(string Name, Stream Content) : IDisposable
{
    public void Dispose() => Content.Dispose();
}

public interface IBackupDocuments
{
    Task<string?> SaveAsync(string suggestedName, byte[] contents);
    Task<BackupDocument?> OpenAsync();
}
