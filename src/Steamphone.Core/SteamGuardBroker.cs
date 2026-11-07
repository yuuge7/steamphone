using SteamKit2.Authentication;

namespace Steamphone.Core;

public sealed record GuardChallenge(string Message, bool RequiresCode);

public sealed class SteamGuardBroker : IAuthenticator
{
    private readonly object sync = new();
    private TaskCompletionSource<string>? pending;
    private CancellationToken cancellation;
    public bool PreferCode { get; set; }
    public event Action<GuardChallenge?>? Changed;

    public void Begin(CancellationToken token) { cancellation = token; Cancel(); }
    public void Submit(string code)
    {
        lock (sync) pending?.TrySetResult(code.Trim());
    }
    public void Cancel()
    {
        lock (sync) { pending?.TrySetCanceled(); pending = null; }
        Changed?.Invoke(null);
    }
    private async Task<string> RequestAsync(string message)
    {
        TaskCompletionSource<string> completion;
        lock (sync) pending = completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Changed?.Invoke(new GuardChallenge(message, true));
        try { return await completion.Task.WaitAsync(cancellation); }
        finally
        {
            lock (sync) if (pending == completion) pending = null;
            Changed?.Invoke(null);
        }
    }
    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect) => RequestAsync(
        previousCodeWasIncorrect ? "Cod incorect. Introdu noul cod Steam Guard." : "Introdu codul din aplicația Steam Guard.");
    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) => RequestAsync(
        previousCodeWasIncorrect ? $"Cod incorect. Verifică mesajul trimis la {email}." : $"Introdu codul trimis la {email}.");
    public Task<bool> AcceptDeviceConfirmationAsync()
    {
        if (PreferCode) return Task.FromResult(false);
        Changed?.Invoke(new GuardChallenge("Aprobă conectarea în aplicația Steam de pe telefon.", false));
        return Task.FromResult(true);
    }
}
