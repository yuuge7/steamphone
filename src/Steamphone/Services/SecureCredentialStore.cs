using System.Text.Json;
using Steamphone.Core;

namespace Steamphone.Services;

public sealed class SecureCredentialStore : ICredentialStore
{
    private const string Key = "steam.account.v1";
    public async Task<SavedAccount?> ReadAsync()
    {
        try
        {
            var json = await SecureStorage.Default.GetAsync(Key);
            return json is null ? null : JsonSerializer.Deserialize<SavedAccount>(json);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Android Keystore entries cannot be restored onto a different installation.
            SecureStorage.Default.Remove(Key);
            return null;
        }
    }
    public Task WriteAsync(SavedAccount account) => SecureStorage.Default.SetAsync(Key, JsonSerializer.Serialize(account));
    public Task ClearAsync() { SecureStorage.Default.Remove(Key); return Task.CompletedTask; }
}
