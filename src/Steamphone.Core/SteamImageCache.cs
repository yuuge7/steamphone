namespace Steamphone.Core;

/// <summary>Small disk cache. Callers own visibility-scoped cancellation; no background work or timers.</summary>
public sealed class SteamImageCache(HttpClient http, string directory)
{
    public const long MaximumCacheBytes = 16 * 1024 * 1024;
    public const int MaximumImageBytes = 512 * 1024;
    public const int MaximumConcurrentDownloads = 4;
    public static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim slots = new(MaximumConcurrentDownloads, MaximumConcurrentDownloads);

    public Task<string?> GetAvatarAsync(string? hash, CancellationToken cancellation)
    {
        if (hash is not { Length: 40 } || !hash.All(Uri.IsHexDigit)) return Task.FromResult<string?>(null);
        hash = hash.ToLowerInvariant();
        return GetAsync($"avatar-medium-{hash}", $"https://avatars.steamstatic.com/{hash}_medium.jpg", cancellation);
    }

    public Task<string?> GetGameHeaderAsync(uint appId, CancellationToken cancellation) => appId == 0
        ? Task.FromResult<string?>(null)
        : GetAsync($"game-{appId}", $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg", cancellation);

    private async Task<string?> GetAsync(string key, string url, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var path = Path.Combine(directory, key + ".jpg");
        // Disk hits must never queue behind a slow network request.
        if (IsFresh(path)) return path;
        await slots.WaitAsync(cancellation);
        string? temporary = null;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            if (IsFresh(path)) return path;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(DownloadTimeout);
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            if (response.Content.Headers.ContentLength > MaximumImageBytes) return null;
            if (response.Content.Headers.ContentType?.MediaType is not ("image/jpeg" or "image/png")) return null;
            temporary = Path.Combine(directory, key + "." + Guid.NewGuid().ToString("N") + ".tmp");
            await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, true))
            {
                var buffer = new byte[8192];
                var total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    total += read;
                    if (total > MaximumImageBytes) return null;
                    await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                }
                if (total == 0) return null;
            }
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
            Trim(path);
            return path;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return File.Exists(path) ? path : null;
        }
        catch (HttpRequestException) when (File.Exists(path)) { return path; }
        finally
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
            slots.Release();
        }
    }

    private static bool IsFresh(string path) => File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromDays(7);

    private void Trim(string current)
    {
        var images = new DirectoryInfo(directory).GetFiles("*.jpg").OrderBy(x => x.LastWriteTimeUtc).ToArray();
        var size = images.Sum(x => x.Length);
        foreach (var image in images)
        {
            if (size <= MaximumCacheBytes) break;
            if (image.FullName == current) continue;
            try { var length = image.Length; image.Delete(); size -= length; }
            catch (IOException) { /* A displayed image can be temporarily locked by Android. */ }
        }
    }
}
